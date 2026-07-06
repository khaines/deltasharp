using System.Globalization;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Checkpoint fast-path + checkpoint-vs-JSON-replay parity oracle (design §2.10.3/§2.10.4; STORY-05.2.2
/// AC1–AC4; INV I7; HP-04; EE-05). Each parity test reconstructs the same table twice — once from JSON
/// commits only, once seeded from a classic checkpoint — and asserts the queryable state is identical, so
/// a checkpoint-reader/discovery bug cannot hide behind the JSON path.
/// </summary>
public sealed class DeltaLogCheckpointTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    private LocalFileSystemBackend NewBackend()
    {
        string root = Path.Combine(Path.GetTempPath(), "cp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return new LocalFileSystemBackend(root);
    }

    /// <summary>Writes a 3-version JSON history to <paramref name="backend"/>; returns nothing.
    /// v0: protocol+metadata+add(a,stats)+add(b,stats); v1: remove(a)+add(c,pv); v2: add(d)+txn.</summary>
    private static async Task WriteJsonHistoryAsync(IStorageBackend backend)
    {
        await DeltaTestHarness.WriteCommitAsync(backend, 0,
            DeltaTestHarness.Protocol(),
            DeltaTestHarness.Metadata(id: "table-1", partitionColumns: ["year"]),
            DeltaTestHarness.Add("a.parquet", stats: StatsA),
            DeltaTestHarness.Add("b.parquet", stats: StatsB));
        await DeltaTestHarness.WriteCommitAsync(backend, 1,
            DeltaTestHarness.Remove("a.parquet"),
            DeltaTestHarness.Add("c.parquet", partitionValues: [("year", "2026")]));
        await DeltaTestHarness.WriteCommitAsync(backend, 2,
            DeltaTestHarness.Add("d.parquet"),
            DeltaTestHarness.Txn("app-1", 5));
    }

    /// <summary>The hand-computed surviving state at version 1 (protocol, metadata, add(b), add(c)) as a
    /// checkpoint — independent of the production replay, so it is a true oracle.</summary>
    private static CheckpointFixture CheckpointAtV1() =>
        new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata(id: "table-1", schemaString: EmptySchemaUnescaped, partitionColumns: ["year"])
            .Add("b.parquet", size: 1, stats: StatsB, modificationTime: 1)
            .Add("c.parquet", size: 1, partitionValues: [("year", "2026")], modificationTime: 1);

    [Fact]
    public async Task CheckpointSeededReconstruction_EqualsJsonReplay()
    {
        IStorageBackend jsonOnly = NewBackend();
        await WriteJsonHistoryAsync(jsonOnly);
        Snapshot fromJson = await new DeltaLog(jsonOnly).LoadSnapshotAsync();

        IStorageBackend withCheckpoint = NewBackend();
        await WriteJsonHistoryAsync(withCheckpoint);
        await DeltaTestHarness.WriteCheckpointAsync(withCheckpoint, 1, CheckpointAtV1());
        await DeltaTestHarness.WriteLastCheckpointAsync(withCheckpoint, 1);
        Snapshot fromCheckpoint = await new DeltaLog(withCheckpoint).LoadSnapshotAsync();

        // Parity oracle: identical queryable state (INV I7 / HP-04).
        Assert.Equal(DeltaTestHarness.Describe(fromJson), DeltaTestHarness.Describe(fromCheckpoint));

        // And the checkpoint path actually took the fast path: seeded from v1, replayed only v2.
        Assert.Equal(1, fromCheckpoint.Metrics.CheckpointVersion);
        Assert.Equal(1, fromCheckpoint.Metrics.ReplayedCommitCount);
        Assert.Null(fromJson.Metrics.CheckpointVersion);
        Assert.Equal(3, fromJson.Metrics.ReplayedCommitCount);
    }

    [Fact]
    public async Task Checkpoint_UsedWithoutHint_ViaListing()
    {
        IStorageBackend backend = NewBackend();
        await WriteJsonHistoryAsync(backend);
        await DeltaTestHarness.WriteCheckpointAsync(backend, 1, CheckpointAtV1());
        // No _last_checkpoint hint written — discovery must still find it by listing.

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync();

        Assert.Equal(1, snapshot.Metrics.CheckpointVersion);
        Assert.Equal(["b.parquet", "c.parquet", "d.parquet"], snapshot.ActiveFiles.Select(a => a.Path));
    }

    [Fact]
    public async Task MultipartCheckpoint_SeedsFromAllParts()
    {
        IStorageBackend jsonOnly = NewBackend();
        await WriteJsonHistoryAsync(jsonOnly);
        Snapshot fromJson = await new DeltaLog(jsonOnly).LoadSnapshotAsync();

        IStorageBackend backend = NewBackend();
        await WriteJsonHistoryAsync(backend);
        await DeltaTestHarness.WriteMultipartCheckpointAsync(backend, 1, CheckpointAtV1(), parts: 3);
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1, parts: 3);

        Snapshot fromCheckpoint = await new DeltaLog(backend).LoadSnapshotAsync();

        Assert.Equal(1, fromCheckpoint.Metrics.CheckpointVersion);
        Assert.Equal(DeltaTestHarness.Describe(fromJson), DeltaTestHarness.Describe(fromCheckpoint));
    }

    [Fact]
    public async Task CorruptCheckpoint_FallsBackToJsonReplay()
    {
        IStorageBackend jsonOnly = NewBackend();
        await WriteJsonHistoryAsync(jsonOnly);
        Snapshot fromJson = await new DeltaLog(jsonOnly).LoadSnapshotAsync();

        IStorageBackend backend = NewBackend();
        await WriteJsonHistoryAsync(backend);
        await DeltaTestHarness.WriteRawCheckpointAsync(backend, 1, "not a parquet file"u8.ToArray());
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1);

        Snapshot fromCheckpoint = await new DeltaLog(backend).LoadSnapshotAsync();

        // EE-05: corrupt checkpoint → JSON replay from 0, identical state, and it did NOT claim a checkpoint.
        Assert.Null(fromCheckpoint.Metrics.CheckpointVersion);
        Assert.Equal(3, fromCheckpoint.Metrics.ReplayedCommitCount);
        Assert.Equal(DeltaTestHarness.Describe(fromJson), DeltaTestHarness.Describe(fromCheckpoint));
    }

    [Fact]
    public async Task StaleHint_ToMissingCheckpoint_FallsBackToListing()
    {
        IStorageBackend backend = NewBackend();
        await WriteJsonHistoryAsync(backend);
        // Hint points at version 2, but only a checkpoint at version 1 exists.
        await DeltaTestHarness.WriteCheckpointAsync(backend, 1, CheckpointAtV1());
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 2);

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync();

        // Stale hint ignored; listing finds the real checkpoint at 1.
        Assert.Equal(1, snapshot.Metrics.CheckpointVersion);
        Assert.Equal(["b.parquet", "c.parquet", "d.parquet"], snapshot.ActiveFiles.Select(a => a.Path));
    }

    [Fact]
    public async Task V2Checkpoint_IsSkipped_NotParsed()
    {
        IStorageBackend jsonOnly = NewBackend();
        await WriteJsonHistoryAsync(jsonOnly);
        Snapshot fromJson = await new DeltaLog(jsonOnly).LoadSnapshotAsync();

        IStorageBackend backend = NewBackend();
        await WriteJsonHistoryAsync(backend);
        // A V2/UUID checkpoint with garbage bytes: if the reader tried to parse it, the load would throw.
        string uuidName = "_delta_log/" + 1L.ToString(CultureInfo.InvariantCulture).PadLeft(20, '0')
            + ".checkpoint.3a0d1f6e-0000-0000-0000-000000000000.parquet";
        await backend.PutIfAbsentAsync(uuidName, "garbage v2"u8.ToArray(), CancellationToken.None);

        Snapshot fromCheckpoint = await new DeltaLog(backend).LoadSnapshotAsync();

        Assert.Null(fromCheckpoint.Metrics.CheckpointVersion); // V2 skipped, no classic checkpoint → JSON replay
        Assert.Equal(DeltaTestHarness.Describe(fromJson), DeltaTestHarness.Describe(fromCheckpoint));
    }

    [Fact]
    public async Task CheckpointOnly_WithCleanedEarlyCommits_Reconstructs()
    {
        // Reference table with a full JSON history 0..2.
        IStorageBackend full = NewBackend();
        await WriteJsonHistoryAsync(full);
        Snapshot fromJson = await new DeltaLog(full).LoadSnapshotAsync(version: 2);

        // Log-cleaned table: JSON 0 and 1 are gone; only a checkpoint at 1 + JSON commit 2 remain.
        IStorageBackend cleaned = NewBackend();
        await DeltaTestHarness.WriteCheckpointAsync(cleaned, 1, CheckpointAtV1());
        await DeltaTestHarness.WriteLastCheckpointAsync(cleaned, 1);
        await DeltaTestHarness.WriteCommitAsync(cleaned, 2,
            DeltaTestHarness.Add("d.parquet"),
            DeltaTestHarness.Txn("app-1", 5));

        Snapshot fromCheckpoint = await new DeltaLog(cleaned).LoadSnapshotAsync();

        Assert.Equal(2, fromCheckpoint.Version);
        Assert.Equal(1, fromCheckpoint.Metrics.CheckpointVersion);
        Assert.Equal(DeltaTestHarness.Describe(fromJson), DeltaTestHarness.Describe(fromCheckpoint));
    }

    [Fact]
    public async Task TimeTravel_ToCheckpointVersion_ReplaysNothing()
    {
        IStorageBackend backend = NewBackend();
        await WriteJsonHistoryAsync(backend);
        await DeltaTestHarness.WriteCheckpointAsync(backend, 1, CheckpointAtV1());
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1);

        Snapshot atV1 = await new DeltaLog(backend).LoadSnapshotAsync(version: 1);

        Assert.Equal(1, atV1.Version);
        Assert.Equal(1, atV1.Metrics.CheckpointVersion);
        Assert.Equal(0, atV1.Metrics.ReplayedCommitCount); // seeded at exactly the target
        Assert.Equal(["b.parquet", "c.parquet"], atV1.ActiveFiles.Select(a => a.Path));
    }

    [Fact]
    public async Task CorruptNewestCheckpoint_FallsBackToOlderCheckpoint()
    {
        // Reference: full JSON history.
        IStorageBackend full = NewBackend();
        await WriteJsonHistoryAsync(full);
        Snapshot fromJson = await new DeltaLog(full).LoadSnapshotAsync(version: 2);

        // Log-cleaned table: JSON 0 & 1 are gone; a COMPLETE checkpoint at 1, a CORRUPT checkpoint at 2,
        // JSON commit 2, and a hint pointing at the corrupt newest checkpoint.
        IStorageBackend backend = NewBackend();
        await DeltaTestHarness.WriteCheckpointAsync(backend, 1, CheckpointAtV1());
        await DeltaTestHarness.WriteRawCheckpointAsync(backend, 2, "corrupt newest checkpoint"u8.ToArray());
        await DeltaTestHarness.WriteCommitAsync(backend, 2,
            DeltaTestHarness.Add("d.parquet"),
            DeltaTestHarness.Txn("app-1", 5));
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 2);

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync();

        // The corrupt v2 checkpoint is skipped; reconstruction seeds from the older complete v1 checkpoint
        // and replays commit 2 — rather than failing with a gap on the cleaned early commits.
        Assert.Equal(1, snapshot.Metrics.CheckpointVersion);
        Assert.Equal(1, snapshot.Metrics.ReplayedCommitCount);
        Assert.Equal(DeltaTestHarness.Describe(fromJson), DeltaTestHarness.Describe(snapshot));
    }

    [Fact]
    public async Task IncompleteMultipartCheckpoint_IsNotUsed_FallsBackToJsonReplay()
    {
        IStorageBackend jsonOnly = NewBackend();
        await WriteJsonHistoryAsync(jsonOnly);
        Snapshot fromJson = await new DeltaLog(jsonOnly).LoadSnapshotAsync();

        IStorageBackend backend = NewBackend();
        await WriteJsonHistoryAsync(backend);
        // A 3-part checkpoint at v1 with part 2 MISSING → incomplete → must not be selected.
        await DeltaTestHarness.WritePartialMultipartCheckpointAsync(backend, 1, CheckpointAtV1(), parts: 3, partsToWrite: [1, 3]);
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1, parts: 3);

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync();

        Assert.Null(snapshot.Metrics.CheckpointVersion); // incomplete checkpoint ignored → full JSON replay
        Assert.Equal(3, snapshot.Metrics.ReplayedCommitCount);
        Assert.Equal(DeltaTestHarness.Describe(fromJson), DeltaTestHarness.Describe(snapshot));
    }

    [Fact]
    public async Task CheckpointEmbeddedRemove_SurfacesInTombstones()
    {
        // A classic checkpoint retains recent tombstones; seeding from it must surface them in Snapshot
        // .Tombstones through the fast path (not just at the reader/SnapshotState level).
        IStorageBackend backend = NewBackend();
        CheckpointFixture fixture = new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata(id: "table-1", schemaString: EmptySchemaUnescaped)
            .Add("b.parquet", size: 1, modificationTime: 1)
            .Remove("a.parquet", deletionTimestamp: 100, size: 7);
        await DeltaTestHarness.WriteCheckpointAsync(backend, 1, fixture);
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1);

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync();

        Assert.Equal(1, snapshot.Metrics.CheckpointVersion);
        Assert.Equal(["b.parquet"], snapshot.ActiveFiles.Select(a => a.Path));
        Assert.Equal(["a.parquet"], snapshot.Tombstones.Select(r => r.Path));
    }

    [Fact]
    public async Task MidSeedCorruptMultipartPart_DiscardsPartialState_AndFallsBack()
    {
        // A COMPLETE multi-part checkpoint whose FIRST part decodes+applies but a LATER part is corrupt
        // forces a mid-seed throw AFTER partial state was applied. The reader must discard that partial
        // seed (state reset) before falling back — a leaked part-1 "ghost" add would corrupt the result.
        IStorageBackend jsonOnly = NewBackend();
        await WriteJsonHistoryAsync(jsonOnly);
        Snapshot fromJson = await new DeltaLog(jsonOnly).LoadSnapshotAsync();

        IStorageBackend backend = NewBackend();
        await WriteJsonHistoryAsync(backend);
        // Part 1 (valid) seeds protocol/metadata + a "ghost" file that is NOT in the JSON history; part 2
        // is corrupt → the seed throws mid-way, after ghost was applied.
        byte[] part1 = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata(id: "table-1", schemaString: EmptySchemaUnescaped, partitionColumns: ["year"])
            .Add("ghost.parquet", size: 1, modificationTime: 1)
            .ToParquetAsync();
        await DeltaTestHarness.WriteRawMultipartPartAsync(backend, 1, 1, 2, part1);
        await DeltaTestHarness.WriteRawMultipartPartAsync(backend, 1, 2, 2, "corrupt later part"u8.ToArray());
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1, parts: 2);

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync();

        // The partial "ghost" seed is discarded; reconstruction equals the JSON-only replay (no ghost).
        Assert.Null(snapshot.Metrics.CheckpointVersion);
        Assert.DoesNotContain("ghost.parquet", snapshot.ActiveFiles.Select(a => a.Path));
        Assert.Equal(DeltaTestHarness.Describe(fromJson), DeltaTestHarness.Describe(snapshot));
    }

    // The metaData.schemaString stored inside a checkpoint column is the raw (unescaped) JSON string,
    // whereas the JSON-commit form is a JSON-encoded string; both must parse to the same schema.
    private const string EmptySchemaUnescaped = """{"type":"struct","fields":[]}""";

    private const string StatsA = """{"numRecords":10,"minValues":{"id":1},"maxValues":{"id":50},"nullCount":{"id":0}}""";
    private const string StatsB = """{"numRecords":20,"minValues":{"id":51},"maxValues":{"id":99},"nullCount":{"id":2}}""";
}
