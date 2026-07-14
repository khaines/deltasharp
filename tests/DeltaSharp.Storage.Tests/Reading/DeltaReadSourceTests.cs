using System.Collections.Immutable;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Tests.Delta;
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
/// timestamp out of range, and the #497 read-side null-fill (an active file physically narrower than the
/// snapshot schema reads back with its absent, later-added <b>nullable</b> columns null-filled; an absent
/// <b>required</b> column still fails closed with <see cref="DeltaReadSchemaEvolutionException"/>).
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

    // ---------------------------------------------------------------- #497 read-side null-fill

    [Fact]
    public async Task EvolvedNarrowFile_ReadsBack_WithNullFilledColumns_Issue497()
    {
        // Manufacture an ADDITIVELY-EVOLVED table the PUBLIC write door cannot create in one step (it writes
        // with SchemaEvolutionMode.None): commit a NARROW file {id} at v0, then evolve the metadata to
        // {id, name} at v1 with a WIDE file — leaving the v0 file physically narrower than the snapshot
        // schema. Reading the latest snapshot requests `name` from the narrow v0 file; read-side null-fill
        // (#497) materializes the absent, later-added NULLABLE column as null for those older rows rather
        // than failing closed with a "column not present" corruption error.
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

        List<(long Id, string? Name)> rows = await ReadFlatAsync(source, info.Version);
        Assert.Equal(3, rows.Count);
        // v0 narrow-file rows read back with the later-added `name` column NULL-FILLED (#497).
        Assert.Contains((10L, (string?)null), rows);
        Assert.Contains((11L, (string?)null), rows);
        // v1 wide-file row carries its real value — the null-fill is per-file, not table-wide.
        Assert.Contains((20L, "twenty"), rows);
    }

    [Fact]
    public async Task MissingRequiredColumn_FailsClosed_Issue497()
    {
        // The reverse of the null-fill case: a file physically {id} under a table whose CURRENT schema
        // requires a non-nullable `code` column absent from that file. Read-side null-fill only fills
        // NULLABLE columns (#497), so a missing REQUIRED column cannot be fabricated — the read fails closed
        // with a clear DeltaReadSchemaEvolutionException rather than a misleading corruption error.
        var narrow = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        var requiredWide = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("code", DataTypes.LongType, nullable: false),
        });

        var backend = new LocalFileSystemBackend(_root);
        try
        {
            var writer = new DeltaTableWriter(backend);
            var parquet = new ParquetFileWriter();
            // The staged file physically has only {id}; the test StageAsync helper does not supply a
            // DataSchema, so the write-door's physical cross-check is skipped — this manufactures the
            // required-column-absent state the public write door cannot legally create.
            StagedDataFile narrowFile = await StageAsync(
                backend, parquet, "part-narrow.parquet", narrow, NarrowBatch(1, 2));
            await writer.CreateOrAppendAsync(requiredWide, Array.Empty<string>(), new[] { narrowFile });
        }
        finally
        {
            backend.Dispose();
        }

        using DeltaReadSource source = ReadSource();
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        DeltaReadSchemaEvolutionException ex = await Assert.ThrowsAsync<DeltaReadSchemaEvolutionException>(
            () => source.ReadBatchesAsync(info.Version));
        Assert.Equal("part-narrow.parquet", ex.FilePath);
        Assert.Contains("required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvolvedNarrowFile_WithDeletionVector_NullFillsAndAppliesDv_Issue497()
    {
        // The DV × null-fill composition (QueryExec/Delta council ask): a narrow {id} DV-enabled file gets a
        // row deleted (DV), THEN the table is additively evolved to {id, name}. Reading the latest snapshot
        // must both NULL-FILL the later-added `name` on the narrow file AND apply its DV (exclude the deleted
        // physical row) — the null-fill materializes exactly rowCount rows so the DV's physical-ordinal
        // alignment is preserved.
        var narrow = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        StructType wide = FlatSchema;

        var backend = new LocalFileSystemBackend(_root);
        try
        {
            // v0: DV-enabled narrow table with rows 10, 11, 12.
            using (DeltaWriteTarget target = WriteTarget())
            {
                await target.CreateDeletionVectorTableAsync(narrow, Array.Empty<string>(), new[] { NarrowBatch(10, 11, 12) });
            }

            // v1: DELETE id==11 → a deletion vector over the narrow file (DELETE reads {id} strictly).
            var delete = new DeltaDelete(
                backend, new DeltaLog(backend), new DeltaCommitter(backend),
                idSource: new SeededDeletionVectorIdSource("dv-nullfill"));
            DeleteResult deleted = await delete.DeleteAsync(
                DeltaDeletePredicate.FromRowPredicate((b, r) => b.SelectedColumn(0).GetValue<long>(r) == 11));
            Assert.Equal(1, deleted.RowsDeleted);

            // v2: additively evolve to {id, name} with a wide file (AddNewColumns).
            Snapshot afterDelete = await new DeltaLog(backend).LoadSnapshotAsync();
            StagedDataFile wideFile = await StageAsync(
                backend, new ParquetFileWriter(), "part-wide.parquet", wide, FlatBatch((20, "twenty")));
            await new DeltaTableWriter(backend).AppendAsync(
                afterDelete, wide, new[] { wideFile }, SchemaEvolutionMode.AddNewColumns);
        }
        finally
        {
            backend.Dispose();
        }

        using DeltaReadSource source = ReadSource();
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        List<(long Id, string? Name)> rows = await ReadFlatAsync(source, info.Version);

        // Row 11 excluded by the DV; 10 and 12 survive with `name` null-filled; the wide row carries its value.
        Assert.Equal(3, rows.Count);
        Assert.Contains((10L, (string?)null), rows);
        Assert.Contains((12L, (string?)null), rows);
        Assert.Contains((20L, "twenty"), rows);
        Assert.DoesNotContain(rows, r => r.Id == 11);
    }

    // ---------------------------------------------------------------- #495 read-side type-widening promotion
    //                                                                   gate (DeltaReadSource, protocol-derived)

    [Fact]
    public async Task WidenedTable_WithTypeWideningFeature_PromotesNarrowFile_ThroughReadSource_Issue495()
    {
        // The read facade DERIVES the promotion gate from the SNAPSHOT PROTOCOL:
        //   allowTypeWideningPromotion = TypeWideningFeature.Supports(snapshot.Protocol)  (DeltaReadSource).
        // The per-width promotion mechanics are unit-tested at the reader (ParquetTypeWideningPromotionTests);
        // THIS pins the facade-level gate WIRING end-to-end. Seed a typeWidening-ENABLED table (protocol v3/v7
        // declaring the reader/writer feature + delta.enableTypeWidening) whose committed schema is `long`,
        // over a physical file still written as narrow `int`. Reading through DeltaReadSource must PROMOTE the
        // int values into the long vector (gate derived TRUE from the protocol).
        var longSchema = new StructType(new[] { new StructField("value", DataTypes.LongType, nullable: false) });
        string intFile = await WriteIntValueFileAsync("value-int.parquet", 1, 2, 3);
        var backend = new LocalFileSystemBackend(_root);
        try
        {
            await DeltaTestHarness.WriteCommitAsync(
                backend, 0,
                DeltaTestHarness.ProtocolWithReaderFeature("typeWidening"),
                DeltaTestHarness.MetadataWithSchemaAndConfig(longSchema, new[] { ("delta.enableTypeWidening", "true") }),
                DeltaTestHarness.Add(intFile));
        }
        finally
        {
            backend.Dispose();
        }

        using DeltaReadSource source = ReadSource();
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(DataTypes.LongType, info.Schema["value"].DataType);

        var values = new List<long>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector value = batch.SelectedColumn(0);
            Assert.Equal(DataTypes.LongType, value.Type); // promoted, not the physical int
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                values.Add(value.GetValue<long>(r));
            }
        }

        Assert.Equal(new long[] { 1L, 2L, 3L }, values.OrderBy(v => v).ToArray());
    }

    [Fact]
    public async Task WidenedSchema_WithoutTypeWideningFeature_FailsClosed_ThroughReadSource_Issue495()
    {
        // The negative gate: the SAME wide `long` schema over the SAME narrow `int` file, but a plain protocol
        // that does NOT declare the typeWidening feature (a tampered/malformed log — a widened logical schema
        // without the feature that sanctions it). DeltaReadSource derives allowTypeWideningPromotion = FALSE,
        // so the reader binds the exact physical type and FAILS CLOSED (SchemaMismatch) rather than silently
        // promoting — surfaced across the facade seam as the typed DeltaReadException. This proves the gate is
        // driven by the protocol, not unconditionally open.
        var longSchema = new StructType(new[] { new StructField("value", DataTypes.LongType, nullable: false) });
        string intFile = await WriteIntValueFileAsync("value-int.parquet", 1, 2, 3);
        var backend = new LocalFileSystemBackend(_root);
        try
        {
            await DeltaTestHarness.WriteCommitAsync(
                backend, 0,
                DeltaTestHarness.Protocol(), // plain 1/2, NO typeWidening feature
                DeltaTestHarness.MetadataWithSchema(longSchema),
                DeltaTestHarness.Add(intFile));
        }
        finally
        {
            backend.Dispose();
        }

        using DeltaReadSource source = ReadSource();
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(DataTypes.LongType, info.Schema["value"].DataType); // schema IS widened…

        // …but without the feature the narrow int file is NOT promoted — the read fails closed.
        await Assert.ThrowsAsync<DeltaReadException>(() => source.ReadBatchesAsync(info.Version));
    }

    // ---------------------------------------------------------------- pinning: no analysis→execution TOCTOU

    [Fact]
    public async Task PinnedBaseVersion_SurvivesConcurrentCommit_ReadsPinnedNotLatest()
    {
        // The headline architectural claim (#499): the version is resolved ONCE (LoadSnapshotAsync, at
        // "analysis") and the data is read at that EXACT pinned version (ReadBatchesAsync, at "execution"),
        // so a concurrent commit that lands BETWEEN the two can never shift the data a base read returns.
        using (DeltaWriteTarget target = WriteTarget())
        {
            await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "alice")) }); // v0
        }

        // Resolve/pin latest (=0) — the analysis-time act.
        long pinnedVersion;
        using (DeltaReadSource pin = ReadSource())
        {
            DeltaSnapshotInfo pinned = await pin.LoadSnapshotAsync(null, null);
            pinnedVersion = pinned.Version;
        }

        Assert.Equal(0L, pinnedVersion);

        // A concurrent writer commits v1 AFTER the pin.
        using (DeltaWriteTarget concurrent = WriteTarget())
        {
            await concurrent.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((2, "bob")) }); // v1
        }

        // Executing the PINNED read returns EXACTLY v0's data — not v1's — even though latest is now v1.
        using DeltaReadSource read = ReadSource();
        Assert.Equal(1L, (await read.LoadSnapshotAsync(null, null)).Version); // sanity: latest DID advance
        List<(long, string?)> rows = await ReadFlatAsync(read, pinnedVersion);
        Assert.Equal(new (long, string?)[] { (1L, "alice") }, rows);
    }

    // ---------------------------------------------------------------- larger / multi-batch read

    [Fact]
    public async Task ReadLatest_LargeRowCount_RoundTripsEveryRow()
    {
        // Beyond the tiny fixtures: a few thousand rows exercises the full write→read path (Parquet encode,
        // batch assembly, full-schema materialization) and proves every row round-trips — not just the
        // first handful.
        const int rowCount = 5_000;
        var rows = new (long Id, string? Name)[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            rows[i] = (i, i % 7 == 0 ? null : "n" + i);
        }

        using (DeltaWriteTarget target = WriteTarget())
        {
            await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch(rows) });
        }

        using DeltaReadSource source = ReadSource();
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        List<(long Id, string? Name)> read = await ReadFlatAsync(source, info.Version);

        Assert.Equal(rowCount, read.Count);
        Assert.Equal(
            rows.OrderBy(r => r.Id).ToList(),
            read.OrderBy(r => r.Id).ToList());
    }

    // ---------------------------------------------------------------- read-time fault wrapping (uniform type)

    [Fact]
    public async Task PinnedFileDeletedBetweenPhases_FailsClosed_AsDeltaReadException()
    {
        // A between-phase missing/deleted active file (the pinned-version-vanished window) must surface as
        // the facade's ONE typed, documented DeltaReadException — not an unwrapped internal storage fault.
        using (DeltaWriteTarget target = WriteTarget())
        {
            await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "alice")) });
        }

        using DeltaReadSource source = ReadSource();
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);

        // Delete the active data file AFTER the snapshot was pinned but BEFORE the read.
        foreach (string file in Directory.EnumerateFiles(_root, "*.parquet", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        await Assert.ThrowsAsync<DeltaReadException>(() => source.ReadBatchesAsync(info.Version));
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

    private async Task<string> WriteIntValueFileAsync(string relativePath, params int[] values)
    {
        var schema = new StructType(new[] { new StructField("value", DataTypes.IntegerType, nullable: false) });
        MutableColumnVector col = ColumnVectors.Create(DataTypes.IntegerType, values.Length);
        foreach (int v in values)
        {
            col.AppendValue(v);
        }

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { col }, values.Length);
        using var buffer = new MemoryStream();
        await new ParquetFileWriter().WriteWithStatisticsAsync(
            buffer, schema, new[] { batch }, StatisticsPolicy.Default, CancellationToken.None);
        var backend = new LocalFileSystemBackend(_root);
        try
        {
            await backend.PutIfAbsentAsync(relativePath, buffer.ToArray(), CancellationToken.None);
        }
        finally
        {
            backend.Dispose();
        }

        return relativePath;
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
