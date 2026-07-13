using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Writing;

/// <summary>
/// End-to-end tests for <c>overwriteSchema</c> destructive schema replacement (#496): a full (Static)
/// overwrite with <c>overwriteSchema: true</c> replaces the table schema wholesale — drop, narrow, reorder,
/// add, or change a column's type, and change partition columns — because every prior file is rewritten in
/// the same version. Reads go through the PUBLIC <see cref="DeltaReadSource"/> so the read adapts to the new
/// snapshot schema. A dynamic partition overwrite must fail closed (untouched-partition files still carry the
/// old schema), and <c>overwriteSchema: false</c> must preserve the strict/additive enforcement.
/// </summary>
public sealed class DeltaOverwriteSchemaTests : IDisposable
{
    private readonly string _root;

    public DeltaOverwriteSchemaTests() =>
        _root = Path.Combine(Path.GetTempPath(), "deltaoverwriteschema-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private DeltaWriteTarget Target() => DeltaWriteTarget.ForLocalPath(_root);

    private static readonly StructType WideSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
        new StructField("extra", DataTypes.StringType, nullable: true),
    });

    [Fact]
    public async Task StaticOverwriteSchema_DropAndReorderColumns_ReplacesSchema()
    {
        // Seed a {id, name, extra} table, then overwriteSchema to {name, id} — DROP `extra` and REORDER.
        // This is illegal under additive evolution but legal here because every prior file is rewritten.
        using (DeltaWriteTarget target = Target())
        {
            await target.AppendAsync(
                WideSchema, Array.Empty<string>(),
                new[] { Batch(WideSchema, ("id", new long[] { 1, 2 }), ("name", new[] { "a", "b" }), ("extra", new[] { "x", "y" })) });

            var reordered = new StructType(new[]
            {
                new StructField("name", DataTypes.StringType, nullable: true),
                new StructField("id", DataTypes.LongType, nullable: false),
            });
            DeltaWriteResult result = await target.OverwriteAsync(
                reordered, Array.Empty<string>(),
                new[] { Batch(reordered, ("name", new[] { "z" }), ("id", new long[] { 9 })) },
                DeltaPartitionOverwriteMode.Static,
                overwriteSchema: true);
            Assert.Equal(1L, result.Version);
        }

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        // Schema replaced wholesale: {name, id} in the new order, `extra` dropped.
        Assert.Equal(new[] { "name", "id" }, info.Schema.Fields.Select(f => f.Name).ToArray());
        List<IReadOnlyList<object?>> rows = await ReadRowsAsync(source, info);
        var row = Assert.Single(rows);
        Assert.Equal("z", row[0]);
        Assert.Equal(9L, row[1]);
    }

    [Fact]
    public async Task StaticOverwriteSchema_NarrowType_ReplacesSchema()
    {
        // Narrow the type of a column (long -> int) — forbidden additively (type widening/narrowing is
        // fail-closed, #495), but legal under overwriteSchema because all prior data is rewritten.
        var longSchema = new StructType(new[] { new StructField("v", DataTypes.LongType, nullable: false) });
        var intSchema = new StructType(new[] { new StructField("v", DataTypes.IntegerType, nullable: false) });

        using (DeltaWriteTarget target = Target())
        {
            await target.AppendAsync(longSchema, Array.Empty<string>(), new[] { Batch(longSchema, ("v", new long[] { 100 })) });

            DeltaWriteResult result = await target.OverwriteAsync(
                intSchema, Array.Empty<string>(),
                new[] { IntBatch(intSchema, "v", new[] { 7 }) },
                DeltaPartitionOverwriteMode.Static,
                overwriteSchema: true);
            Assert.Equal(1L, result.Version);
        }

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.IsType<IntegerType>(info.Schema["v"].DataType);
        List<IReadOnlyList<object?>> rows = await ReadRowsAsync(source, info);
        Assert.Equal(7, Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task StaticOverwriteSchema_ChangePartitionColumns_Repartitions()
    {
        // overwriteSchema can also change partition columns on a full overwrite (all files rewritten per the
        // new partitioning). Seed a table partitioned by `region`, then repartition by `bucket`.
        var byRegion = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        });
        var byBucket = new StructType(new[]
        {
            new StructField("bucket", DataTypes.StringType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        });

        using (DeltaWriteTarget target = Target())
        {
            await target.AppendAsync(
                byRegion, new[] { "region" },
                new[] { Batch(byRegion, ("region", new[] { "US" }), ("id", new long[] { 1 })) });

            DeltaWriteResult result = await target.OverwriteAsync(
                byBucket, new[] { "bucket" },
                new[] { Batch(byBucket, ("bucket", new[] { "b0" }), ("id", new long[] { 42 })) },
                DeltaPartitionOverwriteMode.Static,
                overwriteSchema: true);
            Assert.Equal(1L, result.Version);
        }

        // The committed metaData.partitionColumns are the NEW partition columns.
        var backend = new LocalFileSystemBackend(_root);
        try
        {
            Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync();
            Assert.Equal(new[] { "bucket" }, snapshot.Metadata.PartitionColumns.ToArray());
        }
        finally
        {
            backend.Dispose();
        }

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(new[] { "bucket", "id" }, info.Schema.Fields.Select(f => f.Name).ToArray());
        List<IReadOnlyList<object?>> rows = await ReadRowsAsync(source, info);
        var row = Assert.Single(rows);
        Assert.Equal("b0", row[0]);
        Assert.Equal(42L, row[1]);
    }

    [Fact]
    public async Task DynamicOverwriteSchema_IsRejectedFailClosed()
    {
        // overwriteSchema with a DYNAMIC partition overwrite is illegal: untouched-partition files survive
        // and still carry the old schema, so a wholesale replacement would leave them unreadable.
        var schema = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        });
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(
            schema, new[] { "region" },
            new[] { Batch(schema, ("region", new[] { "US" }), ("id", new long[] { 1 })) });

        var narrowed = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: true),
            new StructField("id", DataTypes.IntegerType, nullable: false),
        });
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            target.OverwriteAsync(
                narrowed, new[] { "region" },
                new[] { IntPartitionedBatch(narrowed, "US", new[] { 2 }) },
                DeltaPartitionOverwriteMode.Dynamic,
                overwriteSchema: true));
        Assert.Contains("overwriteSchema", ex.Message, StringComparison.Ordinal);

        // The table is unchanged (still the original long schema, original row).
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(0L, info.Version);
        Assert.IsType<LongType>(info.Schema["id"].DataType);
    }

    [Fact]
    public async Task StaticOverwrite_WithoutOverwriteSchema_DropColumn_StillRejected()
    {
        // overwriteSchema: false preserves the strict/additive enforcement — dropping a required column via a
        // plain overwrite is still rejected (the destructive path is opt-in only).
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("keep", DataTypes.LongType, nullable: false),
        });
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(
            schema, Array.Empty<string>(),
            new[] { Batch(schema, ("id", new long[] { 1 }), ("keep", new long[] { 2 })) });

        var dropped = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() =>
            target.OverwriteAsync(
                dropped, Array.Empty<string>(),
                new[] { Batch(dropped, ("id", new long[] { 9 })) },
                DeltaPartitionOverwriteMode.Static,
                overwriteSchema: false));
    }

    [Fact]
    public async Task OverwriteSchema_FreshPath_CreatesTableWithSchema()
    {
        // overwriteSchema on a fresh path is moot (the create sets the schema) but must succeed — the write's
        // schema becomes version 0.
        var schema = new StructType(new[] { new StructField("v", DataTypes.LongType, nullable: false) });
        using (DeltaWriteTarget target = Target())
        {
            DeltaWriteResult result = await target.OverwriteAsync(
                schema, Array.Empty<string>(),
                new[] { Batch(schema, ("v", new long[] { 5 })) },
                DeltaPartitionOverwriteMode.Static,
                overwriteSchema: true);
            Assert.Equal(0L, result.Version);
        }

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(new[] { "v" }, info.Schema.Fields.Select(f => f.Name).ToArray());
        Assert.Equal(5L, Assert.Single(await ReadRowsAsync(source, info))[0]);
    }

    // ---- helpers ----

    // Reads all rows as boxed object?[] in schema order (long -> long, int -> int, string -> string/null).
    private static async Task<List<IReadOnlyList<object?>>> ReadRowsAsync(DeltaReadSource source, DeltaSnapshotInfo info)
    {
        var rows = new List<IReadOnlyList<object?>>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                var row = new object?[info.Schema.Count];
                for (int c = 0; c < info.Schema.Count; c++)
                {
                    ColumnVector col = batch.SelectedColumn(c);
                    row[c] = col.IsNull(r)
                        ? null
                        : info.Schema[c].DataType switch
                        {
                            LongType => col.GetValue<long>(r),
                            IntegerType => col.GetValue<int>(r),
                            StringType => Encoding.UTF8.GetString(col.GetBytes(r)),
                            _ => throw new NotSupportedException(info.Schema[c].DataType.SimpleString),
                        };
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    // Builds a batch for a schema whose columns are long (long[]) or string (string?[]), keyed by name.
    private static ColumnBatch Batch(StructType schema, params (string Name, Array Values)[] columns)
    {
        int rowCount = columns[0].Values.Length;
        var vectors = new ColumnVector[schema.Count];
        for (int c = 0; c < schema.Count; c++)
        {
            (string _, Array values) = Array.Find(columns, t => t.Name == schema[c].Name);
            MutableColumnVector v = ColumnVectors.Create(schema[c].DataType, rowCount);
            for (int r = 0; r < rowCount; r++)
            {
                switch (schema[c].DataType)
                {
                    case LongType:
                        v.AppendValue((long)values.GetValue(r)!);
                        break;
                    case StringType:
                        if (values.GetValue(r) is string s)
                        {
                            v.AppendBytes(Encoding.UTF8.GetBytes(s));
                        }
                        else
                        {
                            v.AppendNull();
                        }

                        break;
                    default:
                        throw new NotSupportedException(schema[c].DataType.SimpleString);
                }
            }

            vectors[c] = v;
        }

        return new ManagedColumnBatch(schema, vectors, rowCount);
    }

    private static ColumnBatch IntBatch(StructType schema, string column, int[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.IntegerType, values.Length);
        foreach (int value in values)
        {
            v.AppendValue(value);
        }

        return new ManagedColumnBatch(schema, new ColumnVector[] { v }, values.Length);
    }

    private static ColumnBatch IntPartitionedBatch(StructType schema, string region, int[] ids)
    {
        MutableColumnVector regionVec = ColumnVectors.Create(DataTypes.StringType, ids.Length);
        MutableColumnVector idVec = ColumnVectors.Create(DataTypes.IntegerType, ids.Length);
        foreach (int id in ids)
        {
            regionVec.AppendBytes(Encoding.UTF8.GetBytes(region));
            idVec.AppendValue(id);
        }

        return new ManagedColumnBatch(schema, new ColumnVector[] { regionVec, idVec }, ids.Length);
    }
}
