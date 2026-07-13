using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Xunit;
using Xunit.Abstractions;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Reader-specific behavior: column projection reads only the requested columns, row-group pruning
/// skips groups whose statistics prove no match, and malformed/unsupported inputs surface a
/// deterministic <see cref="DeltaStorageException"/> (never partial rows).
/// </summary>
public sealed class ParquetReaderTests
{
    private readonly SeededRandom _random;

    public ParquetReaderTests(ITestOutputHelper output)
    {
        _random = SeededRandom.Create(output);
    }

    private static readonly StructType FullSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
        new StructField("score", DataTypes.DoubleType, nullable: true),
    });

    [Fact]
    public async Task Projection_ReadsOnlyRequestedColumns()
    {
        ColumnBatch source = TestData.RandomBatch(FullSchema, rowCount: 64, _random);
        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(stream, FullSchema, new[] { source }, CancellationToken.None);
        stream.Position = 0;

        var projection = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(stream, projection, keepRowGroup: null, nullFillMissingColumns: false, CancellationToken.None))
        {
            batches.Add(batch);
        }

        ColumnBatch result = Assert.Single(batches);
        Assert.Equal(projection, result.Schema);
        Assert.Equal(1, result.ColumnCount);
        Assert.Equal(source.LogicalRowCount, result.LogicalRowCount);

        // The projected column must still carry the original values in order.
        ColumnVector expectedId = source.SelectedColumn(0);
        ColumnVector actualId = result.SelectedColumn(0);
        for (int r = 0; r < result.LogicalRowCount; r++)
        {
            Assert.Equal(expectedId.GetValue<long>(r), actualId.GetValue<long>(r));
        }
    }

    [Fact]
    public async Task Projection_ResolvesRequestedColumnsByName_NotByFilePosition()
    {
        // A file whose three long columns carry DISTINCT per-column values so any positional misread is
        // loud: a=10s, b=20s, c=30s. The by-name contract in ParquetFileReader.ResolveFileFields must map
        // each REQUESTED column to the FILE column of the SAME NAME, regardless of the requested order.
        var fileSchema = new StructType(new[]
        {
            new StructField("a", DataTypes.LongType, nullable: false),
            new StructField("b", DataTypes.LongType, nullable: false),
            new StructField("c", DataTypes.LongType, nullable: false),
        });
        ColumnBatch source = BuildLongColumns(
            fileSchema,
            ("a", new long[] { 10, 11, 12 }),
            ("b", new long[] { 20, 21, 22 }),
            ("c", new long[] { 30, 31, 32 }));

        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(stream, fileSchema, new[] { source }, CancellationToken.None);
        byte[] fileBytes = stream.ToArray();

        // Request a DIFFERENT order than the file: [c, a]. A positional regression (assigning file column 0
        // to requested position 0) would return a's values (10s) under 'c' and b's values (20s) under 'a'.
        var projection = new StructType(new[]
        {
            new StructField("c", DataTypes.LongType, nullable: false),
            new StructField("a", DataTypes.LongType, nullable: false),
        });
        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(new MemoryStream(fileBytes), projection, keepRowGroup: null, nullFillMissingColumns: false, CancellationToken.None))
        {
            batches.Add(batch);
        }

        ColumnBatch result = Assert.Single(batches);

        // result.Schema mirrors the REQUESTED order, not the file order.
        Assert.Equal(projection, result.Schema);
        Assert.Equal(2, result.ColumnCount);

        // Each requested column carries ITS OWN values: c -> 30s at position 0, a -> 10s at position 1.
        ColumnVector cCol = result.SelectedColumn(0);
        ColumnVector aCol = result.SelectedColumn(1);
        Assert.Equal(new long[] { 30, 31, 32 }, ReadLongs(cCol, result.LogicalRowCount));
        Assert.Equal(new long[] { 10, 11, 12 }, ReadLongs(aCol, result.LogicalRowCount));

        // A lone middle-column projection must also resolve by name (not fall back to file position 0).
        var middle = new StructType(new[] { new StructField("b", DataTypes.LongType, nullable: false) });
        var middleBatches = new List<ColumnBatch>();
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(new MemoryStream(fileBytes), middle, keepRowGroup: null, nullFillMissingColumns: false, CancellationToken.None))
        {
            middleBatches.Add(batch);
        }

        ColumnBatch middleResult = Assert.Single(middleBatches);
        Assert.Equal(middle, middleResult.Schema);
        Assert.Equal(new long[] { 20, 21, 22 }, ReadLongs(middleResult.SelectedColumn(0), middleResult.LogicalRowCount));
    }

    [Fact]
    public async Task RowGroupPruning_SkipsNonMatchingGroups()
    {
        // Two row groups: ids [1..3] then [100..102]; a predicate that keeps only groups whose max id
        // is >= 100 must drop the first group and return exactly the second group's rows.
        var schema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 100, 101, 102 });

        using var stream = new MemoryStream();
        await new ParquetFileWriter(rowGroupRowLimit: 3).WriteAsync(stream, schema, new[] { batch }, CancellationToken.None);
        stream.Position = 0;

        var kept = new List<long>();
        await foreach (ColumnBatch result in new ParquetFileReader().ReadAsync(
            stream,
            schema,
            keepRowGroup: stats => stats.Max("id") is long max && max >= 100,
            nullFillMissingColumns: false,
            CancellationToken.None))
        {
            ColumnVector column = result.SelectedColumn(0);
            for (int r = 0; r < result.LogicalRowCount; r++)
            {
                kept.Add(column.GetValue<long>(r));
            }
        }

        Assert.Equal(new long[] { 100, 101, 102 }, kept);
    }

    [Fact]
    public async Task TruncatedStream_ThrowsDeterministicCorruptData()
    {
        var schema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 4, 5 });
        using var full = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(full, schema, new[] { batch }, CancellationToken.None);

        // Keep only the first half of the file so the footer/magic is gone.
        byte[] truncated = full.ToArray().AsSpan(0, (int)(full.Length / 2)).ToArray();
        using var stream = new MemoryStream(truncated);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            await foreach (ColumnBatch _ in new ParquetFileReader().ReadAsync(stream, schema, keepRowGroup: null, nullFillMissingColumns: false, CancellationToken.None))
            {
            }
        });
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public async Task GarbageStream_ThrowsDeterministicCorruptData()
    {
        var schema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        var garbage = new byte[256];
        _random.NextBytes(garbage);
        using var stream = new MemoryStream(garbage);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            await foreach (ColumnBatch _ in new ParquetFileReader().ReadAsync(stream, schema, keepRowGroup: null, nullFillMissingColumns: false, CancellationToken.None))
            {
            }
        });
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public async Task NestedRequestedType_ThrowsUnsupportedFeature()
    {
        // Write a simple valid file, then request a nested (array) column, which the layer defers.
        var writeSchema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ColumnBatch batch = BuildLongBatch(writeSchema, new long[] { 1, 2, 3 });
        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(stream, writeSchema, new[] { batch }, CancellationToken.None);
        stream.Position = 0;

        var nested = new StructType(new[]
        {
            new StructField("id", DataTypes.CreateArrayType(DataTypes.LongType), nullable: true),
        });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            await foreach (ColumnBatch _ in new ParquetFileReader().ReadAsync(stream, nested, keepRowGroup: null, nullFillMissingColumns: false, CancellationToken.None))
            {
            }
        });
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
    }

    // ---------------------------------------------------------------- #497 read-side null-fill

    [Fact]
    public async Task NullFill_AbsentNullableColumn_MaterializedAsNull()
    {
        // A file physically written under a NARROW schema {id} read back through a WIDER projection
        // {id, name?, score?}: with nullFillMissingColumns enabled, the absent nullable columns come back
        // as all-null rather than throwing (#497 evolved-column read null-fill).
        var narrow = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ColumnBatch batch = BuildLongBatch(narrow, new long[] { 7, 8, 9 });
        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(stream, narrow, new[] { batch }, CancellationToken.None);
        stream.Position = 0;

        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch b in new ParquetFileReader().ReadAsync(
            stream, FullSchema, keepRowGroup: null, nullFillMissingColumns: true, CancellationToken.None))
        {
            batches.Add(b);
        }

        ColumnBatch result = Assert.Single(batches);
        Assert.Equal(FullSchema, result.Schema);
        Assert.Equal(3, result.LogicalRowCount);

        ColumnVector id = result.SelectedColumn(0);
        ColumnVector name = result.SelectedColumn(1);
        ColumnVector score = result.SelectedColumn(2);
        for (int r = 0; r < result.LogicalRowCount; r++)
        {
            Assert.False(id.IsNull(r)); // present column keeps its real value
            Assert.True(name.IsNull(r)); // absent nullable column is null-filled
            Assert.True(score.IsNull(r));
        }

        Assert.Equal(new long[] { 7, 8, 9 }, ReadLongs(id, result.LogicalRowCount));
    }

    [Fact]
    public async Task NullFill_AbsentNonNullableColumn_StillFailsClosed()
    {
        // Null-fill only applies to NULLABLE absent columns. An absent REQUIRED (non-nullable) column cannot
        // carry null, so it still fails closed even with null-fill enabled (a required lane is never
        // fabricated).
        var narrow = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ColumnBatch batch = BuildLongBatch(narrow, new long[] { 1, 2 });
        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(stream, narrow, new[] { batch }, CancellationToken.None);
        stream.Position = 0;

        var wider = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("required", DataTypes.LongType, nullable: false),
        });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            await foreach (ColumnBatch _ in new ParquetFileReader().ReadAsync(
                stream, wider, keepRowGroup: null, nullFillMissingColumns: true, CancellationToken.None))
            {
            }
        });
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("is not present", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullFillDisabled_AbsentNullableColumn_FailsClosed()
    {
        // With null-fill DISABLED (the general reader default), an absent column of ANY nullability fails
        // closed — the strict projection contract other callers (OPTIMIZE/DELETE) rely on is preserved.
        var narrow = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ColumnBatch batch = BuildLongBatch(narrow, new long[] { 1, 2 });
        using var stream = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(stream, narrow, new[] { batch }, CancellationToken.None);
        stream.Position = 0;

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            await foreach (ColumnBatch _ in new ParquetFileReader().ReadAsync(
                stream, FullSchema, keepRowGroup: null, nullFillMissingColumns: false, CancellationToken.None))
            {
            }
        });
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("is not present", error.Message, StringComparison.Ordinal);
    }

    private static ColumnBatch BuildLongBatch(StructType schema, long[] values)
    {
        MutableColumnVector vector = ColumnVectors.Create(DataTypes.LongType, values.Length);
        foreach (long value in values)
        {
            vector.AppendValue(value);
        }

        return new ManagedColumnBatch(schema, new ColumnVector[] { vector }, values.Length);
    }

    // Builds a multi-column long batch from (name -> values) pairs, in the given schema's column order. All
    // columns must share the same row count.
    private static ColumnBatch BuildLongColumns(StructType schema, params (string Name, long[] Values)[] columns)
    {
        int rowCount = columns[0].Values.Length;
        var vectors = new ColumnVector[columns.Length];
        for (int c = 0; c < columns.Length; c++)
        {
            MutableColumnVector vector = ColumnVectors.Create(DataTypes.LongType, rowCount);
            foreach (long value in columns[c].Values)
            {
                vector.AppendValue(value);
            }

            vectors[c] = vector;
        }

        return new ManagedColumnBatch(schema, vectors, rowCount);
    }

    private static long[] ReadLongs(ColumnVector column, int rowCount)
    {
        var values = new long[rowCount];
        for (int r = 0; r < rowCount; r++)
        {
            values[r] = column.GetValue<long>(r);
        }

        return values;
    }
}
