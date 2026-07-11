using System.Collections.Immutable;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// End-to-end round-trip tests for the PUBLIC Delta read facade (<see cref="DeltaReadSource"/>, #499) — the
/// symmetric counterpart of <see cref="DeltaWriteTargetTests"/>. Each happy-path test writes a real Delta
/// table through the PUBLIC write door (<see cref="DeltaWriteTarget"/>), then reads it back through the read
/// facade and asserts the resolved version, schema, row multiset, and partition-filled columns for a base
/// (latest) read, a <c>versionAsOf</c> read, and a <c>timestampAsOf</c> read (partitioned + unpartitioned).
/// It also covers the fail-closed diagnostics: both-dimensions, out-of-range version, not-a-Delta-table,
/// timestamp out of range, and the #497 schema-evolution guard (an active file physically narrower than the
/// snapshot schema fails closed with <see cref="DeltaReadSchemaEvolutionException"/> — read-side null-fill is
/// #497, not implemented here).
/// </summary>
public sealed class DeltaReadSourceTests : IDisposable
{
    private readonly string _root;

    private static readonly StructType PartitionedSchema = new(new[]
    {
        new StructField("region", DataTypes.StringType, nullable: true),
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    private static readonly StructType FlatSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    public DeltaReadSourceTests() =>
        _root = Path.Combine(Path.GetTempPath(), "deltaread-facade-" + Guid.NewGuid().ToString("N"));

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

    private DeltaWriteTarget WriteTarget() => DeltaWriteTarget.ForLocalPath(_root);

    private DeltaReadSource ReadSource() => DeltaReadSource.ForLocalPath(_root);

    // ---------------------------------------------------------------- base (latest) read

    [Fact]
    public async Task ReadLatest_Unpartitioned_ReturnsAllRows()
    {
        using (DeltaWriteTarget target = WriteTarget())
        {
            await target.AppendAsync(
                FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a"), (2, "b"), (3, null)) });
        }

        using DeltaReadSource source = ReadSource();
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(0L, info.Version);
        Assert.Equal(FlatSchema.SimpleString, info.Schema.SimpleString);

        List<(long, string?)> rows = await ReadFlatAsync(source, info.Version);
        Assert.Equal(
            new (long, string?)[] { (1L, "a"), (2L, "b"), (3L, (string?)null) },
            rows.OrderBy(r => r.Item1).ToList());
    }

    [Fact]
    public async Task ReadLatest_Partitioned_ReDerivesPartitionColumnFromAddAction()
    {
        using (DeltaWriteTarget target = WriteTarget())
        {
            await target.AppendAsync(
                PartitionedSchema, new[] { "region" },
                new[] { PartitionedBatch(("US", 1, "alice"), ("EU", 2, "bob")) });
        }

        using DeltaReadSource source = ReadSource();
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);

        // The output batches carry the FULL table schema (region first), even though the physical Parquet
        // files store only (id, name); region is const-filled from add.partitionValues.
        List<(string?, long, string?)> rows = await ReadPartitionedAsync(source, info.Version);
        Assert.Equal(
            new (string?, long, string?)[] { ("EU", 2L, "bob"), ("US", 1L, "alice") },
            Sorted(rows));
    }

    [Fact]
    public async Task ReadLatest_PartitionedWithNullPartition_ReDerivesNull()
    {
        using (DeltaWriteTarget target = WriteTarget())
        {
            await target.AppendAsync(
                PartitionedSchema, new[] { "region" }, new[] { PartitionedBatch((null, 7, "z")) });
        }

        using DeltaReadSource source = ReadSource();
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);

        List<(string?, long, string?)> rows = await ReadPartitionedAsync(source, info.Version);
        Assert.Equal(new (string?, long, string?)[] { (null, 7L, "z") }, rows);
    }

    // ---------------------------------------------------------------- versionAsOf

    [Fact]
    public async Task VersionAsOf_ReadsExactHistoricalSnapshot()
    {
        using (DeltaWriteTarget target = WriteTarget())
        {
            await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a")) }); // v0
            await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((2, "b")) }); // v1
        }

        using DeltaReadSource source = ReadSource();

        DeltaSnapshotInfo v0 = await source.LoadSnapshotAsync(versionAsOf: 0, timestampAsOf: null);
        Assert.Equal(0L, v0.Version);
        Assert.Equal(new (long, string?)[] { (1L, "a") }, await ReadFlatAsync(source, v0.Version));

        DeltaSnapshotInfo latest = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(1L, latest.Version);
        Assert.Equal(
            new (long, string?)[] { (1L, "a"), (2L, "b") },
            (await ReadFlatAsync(source, latest.Version)).OrderBy(r => r.Item1).ToList());
    }

    [Fact]
    public async Task VersionAsOf_OutOfRange_FailsClosed()
    {
        using (DeltaWriteTarget target = WriteTarget())
        {
            await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a")) });
        }

        using DeltaReadSource source = ReadSource();
        await Assert.ThrowsAsync<DeltaReadException>(
            () => source.LoadSnapshotAsync(versionAsOf: 99, timestampAsOf: null));
    }

    // ---------------------------------------------------------------- timestampAsOf (deterministic mtimes)

    [Fact]
    public async Task TimestampAsOf_ResolvesTheLastCommitAtOrBefore()
    {
        using (DeltaWriteTarget target = WriteTarget())
        {
            await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a")) }); // v0
            await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((2, "b")) }); // v1
        }

        // Pin deterministic commit timestamps so resolution is not at the mercy of filesystem mtime
        // granularity: v0 @ 2020-01-01, v1 @ 2021-01-01 (both UTC).
        var t0 = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SetCommitTimestamp(0, t0);
        SetCommitTimestamp(1, t1);

        using DeltaReadSource source = ReadSource();

        // Exactly the first commit → v0; between the two → v0; exactly the last → v1.
        Assert.Equal(0L, (await source.LoadSnapshotAsync(null, t0)).Version);
        Assert.Equal(0L, (await source.LoadSnapshotAsync(null, t0.AddMonths(6))).Version);
        Assert.Equal(1L, (await source.LoadSnapshotAsync(null, t1)).Version);
    }

    [Fact]
    public async Task TimestampAsOf_AfterLatestCommit_FailsClosed()
    {
        using (DeltaWriteTarget target = WriteTarget())
        {
            await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a")) });
        }

        SetCommitTimestamp(0, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        using DeltaReadSource source = ReadSource();
        await Assert.ThrowsAsync<DeltaReadException>(
            () => source.LoadSnapshotAsync(null, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task TimestampAsOf_BeforeFirstCommit_FailsClosed()
    {
        using (DeltaWriteTarget target = WriteTarget())
        {
            await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a")) });
        }

        SetCommitTimestamp(0, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        using DeltaReadSource source = ReadSource();
        await Assert.ThrowsAsync<DeltaReadException>(
            () => source.LoadSnapshotAsync(null, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    // ---------------------------------------------------------------- fail-closed diagnostics

    [Fact]
    public async Task BothVersionAndTimestamp_Throws()
    {
        using DeltaReadSource source = ReadSource();
        await Assert.ThrowsAsync<ArgumentException>(
            () => source.LoadSnapshotAsync(
                versionAsOf: 0,
                timestampAsOf: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task NotADeltaTable_FailsClosed()
    {
        Directory.CreateDirectory(_root); // an empty directory, no _delta_log
        using DeltaReadSource source = ReadSource();
        await Assert.ThrowsAsync<DeltaReadException>(() => source.LoadSnapshotAsync(null, null));
    }

    // ---------------------------------------------------------------- #497 schema-evolution guard (fail closed)

    [Fact]
    public async Task EvolvedNarrowFile_FailsClosed_NamingIssue497()
    {
        // Manufacture an ADDITIVELY-EVOLVED table the PUBLIC write door cannot create (it writes with
        // SchemaEvolutionMode.None): commit a NARROW file {id} at v0, then evolve the metadata to {id, name}
        // at v1 with a WIDE file — leaving the v0 file physically narrower than the snapshot schema. Reading
        // the latest snapshot then requests `name` from the narrow v0 file, which ParquetFileReader cannot
        // satisfy (read-side null-fill is #497, not implemented). The facade must fail CLOSED with a clear
        // schema-evolution error rather than a misleading "column not present" corruption error.
        var narrow = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        StructType wide = FlatSchema;

        var backend = new LocalFileSystemBackend(_root);
        try
        {
            var writer = new DeltaTableWriter(backend);
            var parquet = new ParquetFileWriter();
            var log = new DeltaLog(backend);

            StagedDataFile narrowFile = await StageAsync(
                backend, parquet, "part-narrow.parquet", narrow, NarrowBatch(10, 11));
            await writer.CreateOrAppendAsync(narrow, Array.Empty<string>(), new[] { narrowFile });

            Snapshot v0 = await log.LoadSnapshotAsync();
            StagedDataFile wideFile = await StageAsync(
                backend, parquet, "part-wide.parquet", wide, FlatBatch((20, "twenty")));
            await writer.AppendAsync(v0, wide, new[] { wideFile }, SchemaEvolutionMode.AddNewColumns);
        }
        finally
        {
            backend.Dispose();
        }

        using DeltaReadSource source = ReadSource();
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(1L, info.Version);
        Assert.Contains("name", info.Schema.Fields.Select(f => f.Name)); // schema evolved to wide

        DeltaReadSchemaEvolutionException ex = await Assert.ThrowsAsync<DeltaReadSchemaEvolutionException>(
            () => source.ReadBatchesAsync(info.Version));
        Assert.Contains("#497", ex.Message);
    }

    // ---------------------------------------------------------------- helpers

    private void SetCommitTimestamp(long version, DateTimeOffset timestamp)
    {
        string commit = Path.Combine(
            _root,
            "_delta_log",
            version.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".json");
        File.SetLastWriteTimeUtc(commit, timestamp.UtcDateTime);
    }

    private static async Task<StagedDataFile> StageAsync(
        LocalFileSystemBackend backend,
        ParquetFileWriter parquet,
        string relativePath,
        StructType schema,
        ColumnBatch batch)
    {
        using var buffer = new MemoryStream();
        ParquetFileWriter.WriteResult result = await parquet.WriteWithStatisticsAsync(
            buffer, schema, new[] { batch }, StatisticsPolicy.Default, CancellationToken.None);
        byte[] bytes = buffer.ToArray();
        await backend.PutIfAbsentAsync(relativePath, bytes, CancellationToken.None);
        return new StagedDataFile(
            relativePath,
            ImmutableSortedDictionary<string, string?>.Empty,
            Size: bytes.LongLength,
            ModificationTime: 0,
            Stats: result.Statistics);
    }

    private static async Task<List<(long Id, string? Name)>> ReadFlatAsync(DeltaReadSource source, long version)
    {
        var rows = new List<(long, string?)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(version))
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector name = batch.SelectedColumn(1);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((id.GetValue<long>(r), name.IsNull(r) ? null : Encoding.UTF8.GetString(name.GetBytes(r))));
            }
        }

        return rows;
    }

    private static async Task<List<(string? Region, long Id, string? Name)>> ReadPartitionedAsync(
        DeltaReadSource source, long version)
    {
        var rows = new List<(string?, long, string?)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(version))
        {
            ColumnVector region = batch.SelectedColumn(0);
            ColumnVector id = batch.SelectedColumn(1);
            ColumnVector name = batch.SelectedColumn(2);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((
                    region.IsNull(r) ? null : Encoding.UTF8.GetString(region.GetBytes(r)),
                    id.GetValue<long>(r),
                    name.IsNull(r) ? null : Encoding.UTF8.GetString(name.GetBytes(r))));
            }
        }

        return rows;
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

    private static ColumnBatch NarrowBatch(params long[] ids)
    {
        var narrow = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, ids.Length);
        foreach (long i in ids)
        {
            id.AppendValue(i);
        }

        return new ManagedColumnBatch(narrow, new ColumnVector[] { id }, ids.Length);
    }

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

    private static List<(string?, long, string?)> Sorted(IEnumerable<(string? Region, long Id, string? Name)> rows) =>
        rows.OrderBy(r => r.Region, StringComparer.Ordinal).ThenBy(r => r.Id).ToList();
}
