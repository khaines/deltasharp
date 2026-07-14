using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Parquet;
using Xunit;

namespace DeltaSharp.Storage.Tests.Writing;

/// <summary>
/// End-to-end round-trip tests for the PUBLIC Delta write facade (<see cref="DeltaWriteTarget"/>, #487).
/// Each test stages a <see cref="ColumnBatch"/> through the facade (Parquet data files + a real
/// <c>_delta_log</c> commit), then reads the table back through the internal <see cref="DeltaLog"/> /
/// <see cref="ParquetFileReader"/> and asserts the row multiset, schema, partition layout, and the commit
/// actions (version, <c>add</c> actions, <c>dataChange</c>). Covers create+append (v0→v1), static
/// overwrite, dynamic partition overwrite (only touched partitions replaced), and unpartitioned tables.
/// </summary>
public sealed class DeltaWriteTargetTests : IDisposable
{
    private readonly string _root;

    // A partitioned table: region is the partition column; (id, name) are the data columns.
    private static readonly StructType PartitionedSchema = new(new[]
    {
        new StructField("region", DataTypes.StringType, nullable: true),
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    private static readonly StructType DataSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    // An unpartitioned table: (id, name).
    private static readonly StructType FlatSchema = DataSchema;

    public DeltaWriteTargetTests() =>
        _root = Path.Combine(Path.GetTempPath(), "deltawrite-facade-" + Guid.NewGuid().ToString("N"));

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

    private static ColumnBatch PartitionedBatch(params (string? Region, long Id, string? Name)[] rows)
    {
        MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((string? r, long i, string? n) in rows)
        {
            AppendString(region, r);
            id.AppendValue(i);
            AppendString(name, n);
        }

        return new ManagedColumnBatch(
            PartitionedSchema, new ColumnVector[] { region, id, name }, rows.Length);
    }

    private static ColumnBatch FlatBatch(params (long Id, string? Name)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((long i, string? n) in rows)
        {
            id.AppendValue(i);
            AppendString(name, n);
        }

        return new ManagedColumnBatch(FlatSchema, new ColumnVector[] { id, name }, rows.Length);
    }

    private static void AppendString(MutableColumnVector vector, string? value)
    {
        if (value is null)
        {
            vector.AppendNull();
        }
        else
        {
            vector.AppendBytes(Encoding.UTF8.GetBytes(value));
        }
    }

    // ---------------------------------------------------------------- create + append

    [Fact]
    public async Task Append_ToFreshPath_CreatesTableAtVersion0()
    {
        using DeltaWriteTarget target = Target();
        DeltaWriteResult result = await target.AppendAsync(
            PartitionedSchema, new[] { "region" },
            new[] { PartitionedBatch(("US", 1, "alice"), ("EU", 2, "bob")) });

        Assert.Equal(0L, result.Version);
        Assert.Equal(2L, result.RowsWritten);

        List<(string?, long, string?)> rows = await ReadPartitionedAsync();
        Assert.Equal(
            new (string?, long, string?)[] { ("EU", 2L, "bob"), ("US", 1L, "alice") },
            Sorted(rows));
    }

    [Fact]
    public async Task Append_Twice_CreatesV0ThenV1_AndUnionsRows()
    {
        using DeltaWriteTarget target = Target();
        DeltaWriteResult first = await target.AppendAsync(
            PartitionedSchema, new[] { "region" }, new[] { PartitionedBatch(("US", 1, "alice")) });
        DeltaWriteResult second = await target.AppendAsync(
            PartitionedSchema, new[] { "region" }, new[] { PartitionedBatch(("US", 2, "bob")) });

        Assert.Equal(0L, first.Version);
        Assert.Equal(1L, second.Version);

        List<(string?, long, string?)> rows = await ReadPartitionedAsync();
        Assert.Equal(
            new (string?, long, string?)[] { ("US", 1L, "alice"), ("US", 2L, "bob") },
            Sorted(rows));
    }

    [Fact]
    public async Task Append_CommitsAddActions_WithDataChangeTrue()
    {
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(
            PartitionedSchema, new[] { "region" },
            new[] { PartitionedBatch(("US", 1, "alice"), ("EU", 2, "bob")) });

        Snapshot snapshot = await LoadSnapshotAsync();
        Assert.Equal(0L, snapshot.Version);
        Assert.Equal(2, snapshot.ActiveFiles.Length); // one file per touched partition
        Assert.All(snapshot.ActiveFiles, add => Assert.True(add.DataChange));
        Assert.All(snapshot.ActiveFiles, add => Assert.True(add.PartitionValues.ContainsKey("region")));
        Assert.Equal(
            new[] { "EU", "US" },
            snapshot.ActiveFiles.Select(a => a.PartitionValues["region"]).OrderBy(v => v, StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- static overwrite

    [Fact]
    public async Task StaticOverwrite_ReplacesAllPriorFiles()
    {
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(
            PartitionedSchema, new[] { "region" },
            new[] { PartitionedBatch(("US", 1, "alice"), ("EU", 2, "bob")) });

        DeltaWriteResult result = await target.OverwriteAsync(
            PartitionedSchema, new[] { "region" },
            new[] { PartitionedBatch(("US", 9, "zoe")) }, DeltaPartitionOverwriteMode.Static);

        Assert.Equal(1L, result.Version);
        List<(string?, long, string?)> rows = await ReadPartitionedAsync();
        Assert.Equal(new (string?, long, string?)[] { ("US", 9L, "zoe") }, Sorted(rows)); // EU partition also removed (full overwrite)
    }

    // ---------------------------------------------------------------- dynamic partition overwrite

    [Fact]
    public async Task DynamicOverwrite_ReplacesOnlyTouchedPartitions()
    {
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(
            PartitionedSchema, new[] { "region" },
            new[] { PartitionedBatch(("US", 1, "alice"), ("EU", 2, "bob")) });

        // Overwrite only the US partition dynamically; EU must survive untouched.
        DeltaWriteResult result = await target.OverwriteAsync(
            PartitionedSchema, new[] { "region" },
            new[] { PartitionedBatch(("US", 9, "zoe")) }, DeltaPartitionOverwriteMode.Dynamic);

        Assert.Equal(1L, result.Version);
        List<(string?, long, string?)> rows = await ReadPartitionedAsync();
        Assert.Equal(
            new (string?, long, string?)[] { ("EU", 2L, "bob"), ("US", 9L, "zoe") },
            Sorted(rows));
    }

    // ---------------------------------------------------------------- unpartitioned

    [Fact]
    public async Task Append_Unpartitioned_WritesSingleFileAtRoot()
    {
        using DeltaWriteTarget target = Target();
        DeltaWriteResult result = await target.AppendAsync(
            FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a"), (2, "b"), (3, null)) });

        Assert.Equal(0L, result.Version);
        Snapshot snapshot = await LoadSnapshotAsync();
        AddFileAction add = Assert.Single(snapshot.ActiveFiles);
        Assert.Empty(add.PartitionValues);
        Assert.DoesNotContain('/', add.Path); // a file at the table root (no partition directory)

        List<(long, string?)> rows = await ReadFlatAsync();
        Assert.Equal(new[] { (1L, "a"), (2L, "b"), (3L, (string?)null) }, rows.OrderBy(r => r.Item1).ToList());
    }

    [Fact]
    public async Task Overwrite_Unpartitioned_ReplacesData()
    {
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a")) });
        await target.OverwriteAsync(
            FlatSchema, Array.Empty<string>(), new[] { FlatBatch((5, "e")) }, DeltaPartitionOverwriteMode.Static);

        List<(long, string?)> rows = await ReadFlatAsync();
        Assert.Equal(new (long, string?)[] { (5L, "e") }, rows);
    }

    // ---------------------------------------------------------------- multi-batch (Quality M2)

    [Fact]
    public async Task Append_MultipleBatchesInOneAppend_RoundTripsAllRows()
    {
        // Quality M2: one Append with MULTIPLE ColumnBatches must stage them all — every row from every
        // batch must round-trip (a facade that only wrote the first batch would silently lose data).
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(
            FlatSchema, Array.Empty<string>(),
            new[] { FlatBatch((1, "a"), (2, "b")), FlatBatch((3, "c"), (4, null)) });

        List<(long, string?)> rows = await ReadFlatAsync();
        Assert.Equal(
            new (long, string?)[] { (1L, "a"), (2L, "b"), (3L, "c"), (4L, (string?)null) }, rows);
    }

    // ---------------------------------------------------------------- empty overwrite (truncate) — Spark parity

    [Fact]
    public async Task StaticOverwrite_Empty_OnExistingTable_TruncatesToEmpty()
    {
        // CRITICAL (red-team, #487 round-2): a STATIC overwrite of an EMPTY DataFrame TRUNCATES an existing
        // table (Spark parity — overwrite replaces prior data; replacing with nothing leaves nothing). It is
        // NOT the silent no-op the pre-fix code implemented (which preserved stale data).
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a"), (2, "b")) });

        DeltaWriteResult result = await target.OverwriteAsync(
            FlatSchema, Array.Empty<string>(), Array.Empty<ColumnBatch>(), DeltaPartitionOverwriteMode.Static);

        Assert.Equal(1L, result.Version);      // a new version was committed (truncate is a real commit)
        Assert.Equal(0, result.FilesWritten);  // 0 adds
        Snapshot snapshot = await LoadSnapshotAsync();
        Assert.Empty(snapshot.ActiveFiles);    // every prior active file was removed
        Assert.Empty(await ReadFlatAsync());   // reads back empty
    }

    [Fact]
    public async Task StaticOverwrite_Empty_OnAlreadyEmptyTable_IsNoOp_VersionUnchanged()
    {
        // MEDIUM (Architect, #487 round-3): a STATIC empty overwrite of an ALREADY-empty table (same schema)
        // is an idempotent no-op — Spark treats it as benign. It must NOT build a 0-remove/0-add/0-metadata
        // action list (which DeltaCommitter rejects as an empty commit with an internal ArgumentException).
        // Repro: create the empty schema'd table at v0, then overwrite-empty again over that empty table.
        using DeltaWriteTarget target = Target();
        DeltaWriteResult created = await target.OverwriteAsync(
            FlatSchema, Array.Empty<string>(), Array.Empty<ColumnBatch>(), DeltaPartitionOverwriteMode.Static);
        Assert.Equal(0L, created.Version); // fresh path ⇒ empty table at v0

        // Second empty static overwrite over the now-empty table: idempotent no-op, no throw.
        DeltaWriteResult result = await target.OverwriteAsync(
            FlatSchema, Array.Empty<string>(), Array.Empty<ColumnBatch>(), DeltaPartitionOverwriteMode.Static);

        Assert.Equal(0L, result.Version);      // version UNCHANGED (no new commit)
        Assert.Equal(0, result.FilesWritten);  // 0 adds
        Snapshot snapshot = await LoadSnapshotAsync();
        Assert.Equal(0L, snapshot.Version);    // still v0 — nothing was committed
        Assert.Empty(snapshot.ActiveFiles);    // still empty
        Assert.Empty(await ReadFlatAsync());
    }

    [Fact]
    public async Task StaticOverwrite_Empty_TwiceOnTruncatedTable_IsNoOp_VersionUnchanged()
    {
        // MEDIUM (Architect, #487 round-3): re-running an empty overwrite must be idempotent. First empty
        // overwrite over a NON-empty table truncates (a real commit); a SECOND empty overwrite over the now-
        // empty table is a no-op (version unchanged, no 0-action ArgumentException).
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a"), (2, "b")) });

        DeltaWriteResult truncate = await target.OverwriteAsync(
            FlatSchema, Array.Empty<string>(), Array.Empty<ColumnBatch>(), DeltaPartitionOverwriteMode.Static);
        Assert.Equal(1L, truncate.Version);    // real truncate committed at v1

        DeltaWriteResult again = await target.OverwriteAsync(
            FlatSchema, Array.Empty<string>(), Array.Empty<ColumnBatch>(), DeltaPartitionOverwriteMode.Static);

        Assert.Equal(1L, again.Version);       // version UNCHANGED — idempotent no-op
        Snapshot snapshot = await LoadSnapshotAsync();
        Assert.Equal(1L, snapshot.Version);
        Assert.Empty(snapshot.ActiveFiles);
        Assert.Empty(await ReadFlatAsync());
    }

    [Fact]
    public async Task DynamicOverwrite_Empty_IsNoOp_LeavesTableUnchanged()
    {
        // CRITICAL (red-team, #487 round-2): a DYNAMIC overwrite of an EMPTY DataFrame is a genuine no-op —
        // empty data touches no partitions, so nothing is removed and the version is unchanged.
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(
            PartitionedSchema, new[] { "region" },
            new[] { PartitionedBatch(("US", 1, "alice"), ("EU", 2, "bob")) });

        DeltaWriteResult result = await target.OverwriteAsync(
            PartitionedSchema, new[] { "region" }, Array.Empty<ColumnBatch>(), DeltaPartitionOverwriteMode.Dynamic);

        Assert.Equal(0L, result.Version);      // version UNCHANGED (still v0)
        List<(string?, long, string?)> rows = await ReadPartitionedAsync();
        Assert.Equal(
            new (string?, long, string?)[] { ("EU", 2L, "bob"), ("US", 1L, "alice") },
            Sorted(rows));                      // both partitions preserved
    }

    [Fact]
    public async Task Overwrite_Empty_OnFreshPath_CreatesEmptySchemadTableAtV0()
    {
        // CRITICAL (red-team, #487 round-2): an empty overwrite against a FRESH path creates the schema'd
        // empty table at v0 (protocol + metaData, 0 adds) — Spark creates the empty table.
        using DeltaWriteTarget target = Target();
        DeltaWriteResult result = await target.OverwriteAsync(
            FlatSchema, Array.Empty<string>(), Array.Empty<ColumnBatch>(), DeltaPartitionOverwriteMode.Static);

        Assert.Equal(0L, result.Version);
        Assert.True(await target.TableExistsAsync());
        Snapshot snapshot = await LoadSnapshotAsync();
        Assert.Empty(snapshot.ActiveFiles);                                   // an empty table
        Assert.Equal(FlatSchema.SimpleString, snapshot.Schema.SimpleString);  // carrying the declared schema
        Assert.Empty(await ReadFlatAsync());
    }

    // ---------------------------------------------------------------- null partition + schema split (Quality Low)

    [Fact]
    public async Task Append_NullPartitionValue_HiveDefaultDir_NullInPartitionValues_AndSchemaSplit()
    {
        // Quality Low: a NULL partition value uses the __HIVE_DEFAULT_PARTITION__ directory in the physical
        // path but records a REAL null in add.partitionValues. The metadata (full) schema keeps the partition
        // column; the on-disk data-file schema excludes it (partition columns live only in partitionValues +
        // the directory path, never in the Parquet data file).
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(
            PartitionedSchema, new[] { "region" }, new[] { PartitionedBatch((null, 1, "alice")) });

        Snapshot snapshot = await LoadSnapshotAsync();
        AddFileAction add = Assert.Single(snapshot.ActiveFiles);
        Assert.True(add.PartitionValues.ContainsKey("region"));
        Assert.Null(add.PartitionValues["region"]);                            // a real null in partitionValues
        Assert.Contains(DeltaWriteEncoding.HiveDefaultPartition, add.Path);    // sentinel dir in the path

        // Metadata (full) schema INCLUDES the partition column...
        Assert.Contains(snapshot.Schema.Fields, f => f.Name == "region");
        // ...while the on-disk DATA-file schema EXCLUDES it.
        IReadOnlyList<string> dataFileColumns = DataFileColumnNames(add.Path);
        Assert.DoesNotContain("region", dataFileColumns);
        Assert.Contains("id", dataFileColumns);
        Assert.Contains("name", dataFileColumns);

        List<(string?, long, string?)> rows = await ReadPartitionedAsync();
        Assert.Equal(new (string?, long, string?)[] { (null, 1L, "alice") }, rows);
    }

    // The physical Parquet data file's column names, read via Parquet.Net's schema (proves the partition
    // column is NOT stored in the data file).
    private IReadOnlyList<string> DataFileColumnNames(string relativePath)
    {
        using FileStream fs = File.OpenRead(Path.Combine(_root, relativePath));
        ParquetReader reader = ParquetReader.CreateAsync(fs).GetAwaiter().GetResult();
        try
        {
            return reader.Schema.GetDataFields().Select(f => f.Name).ToList();
        }
        finally
        {
            reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // ---------------------------------------------------------------- existence check

    [Fact]
    public async Task TableExists_IsFalseBeforeWrite_TrueAfter()
    {
        using DeltaWriteTarget target = Target();
        Assert.False(await target.TableExistsAsync());
        await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a")) });
        Assert.True(await target.TableExistsAsync());
    }

    // ---------------------------------------------------------------- read-back helpers

    private async Task<Snapshot> LoadSnapshotAsync()
    {
        var backend = new LocalFileSystemBackend(_root);
        try
        {
            var log = new DeltaLog(backend);
            return await log.LoadSnapshotAsync();
        }
        finally
        {
            backend.Dispose();
        }
    }

    private async Task<List<(string? Region, long Id, string? Name)>> ReadPartitionedAsync()
    {
        var backend = new LocalFileSystemBackend(_root);
        try
        {
            var log = new DeltaLog(backend);
            Snapshot snapshot = await log.LoadSnapshotAsync();
            var reader = new ParquetFileReader();
            var rows = new List<(string?, long, string?)>();
            foreach (AddFileAction add in snapshot.ActiveFiles)
            {
                add.PartitionValues.TryGetValue("region", out string? region);
                Stream stream = await backend.OpenReadAsync(add.Path, CancellationToken.None);
                await using (stream)
                {
                    await foreach (ColumnBatch batch in reader.ReadAsync(stream, DataSchema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None))
                    {
                        ColumnVector id = batch.SelectedColumn(0);
                        ColumnVector name = batch.SelectedColumn(1);
                        for (int r = 0; r < batch.LogicalRowCount; r++)
                        {
                            rows.Add((region, id.GetValue<long>(r), name.IsNull(r) ? null : Encoding.UTF8.GetString(name.GetBytes(r))));
                        }
                    }
                }
            }

            return rows;
        }
        finally
        {
            backend.Dispose();
        }
    }

    private async Task<List<(long Id, string? Name)>> ReadFlatAsync()
    {
        var backend = new LocalFileSystemBackend(_root);
        try
        {
            var log = new DeltaLog(backend);
            Snapshot snapshot = await log.LoadSnapshotAsync();
            var reader = new ParquetFileReader();
            var rows = new List<(long, string?)>();
            foreach (AddFileAction add in snapshot.ActiveFiles)
            {
                Stream stream = await backend.OpenReadAsync(add.Path, CancellationToken.None);
                await using (stream)
                {
                    await foreach (ColumnBatch batch in reader.ReadAsync(stream, FlatSchema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None))
                    {
                        ColumnVector id = batch.SelectedColumn(0);
                        ColumnVector name = batch.SelectedColumn(1);
                        for (int r = 0; r < batch.LogicalRowCount; r++)
                        {
                            rows.Add((id.GetValue<long>(r), name.IsNull(r) ? null : Encoding.UTF8.GetString(name.GetBytes(r))));
                        }
                    }
                }
            }

            return rows.OrderBy(r => r.Item1).ToList();
        }
        finally
        {
            backend.Dispose();
        }
    }

    private static List<(string?, long, string?)> Sorted(IEnumerable<(string? Region, long Id, string? Name)> rows) =>
        rows.OrderBy(r => r.Region, StringComparer.Ordinal).ThenBy(r => r.Id).ToList();
}
