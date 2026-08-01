using System.Globalization;
using System.Text.Json;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Tests.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// Pins the change-feed read path's <c>_delta_log</c> I/O budget (#691) — the perf follow-up to the #671 /
/// PR #690 full-history column-mapping IDENTITY-immutability gate.
///
/// <para>The gate validates <c>[earliest, end]</c> in two halves: <c>DeltaLog</c> scans every retained commit
/// strictly BEFORE the range and <c>ChangeFeedReader</c>'s replay loop covers <c>[start, end]</c>. As shipped,
/// the pre-range half (a) re-LISTed <c>_delta_log</c> even though the read had already listed it, twice — once
/// for the scan itself and once more for its baseline snapshot — and (b) re-GET every pre-range commit object
/// that the read's own start-snapshot reconstruction was about to read anyway. Both are pure constant-factor
/// waste on a CDF read over a long retained history.</para>
///
/// <para>These tests are the MEASUREMENT the optimization is claimed against: a
/// <see cref="CountingStorageBackend"/> counts the real backend LISTs and GETs a read issues, so a regression
/// that reintroduces either cost turns the pinned budget RED instead of silently restoring it. They are
/// deliberately paired with coverage assertions — the very same scenarios also assert that the gate STILL
/// fails closed, and that the commits the reconstruction cannot reach (a stray surviving strictly below a
/// compacting checkpoint floor) are STILL read and validated — so the budget can never be met by narrowing
/// the gate.</para>
/// </summary>
public sealed class ChangeFeedReadIoBudgetTests : IDisposable
{
    private const string Protocol =
        "{\"protocol\":{\"minReaderVersion\":3,\"minWriterVersion\":7,"
        + "\"readerFeatures\":[\"columnMapping\"],\"writerFeatures\":[\"columnMapping\",\"changeDataFeed\"]}}";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ds-cdf-io-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
        + "-" + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));

    public ChangeFeedReadIoBudgetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort fixture cleanup.
        }
    }

    [Fact]
    public async Task Cdf_ReadOverLongRetainedHistory_ListsTheLogOnce_AndReadsNoCommitAThirdTime()
    {
        // #691 budget pin for the BEST case: a checkpoint-FREE history, where the start-snapshot replay begins
        // at v0 and therefore covers the whole pre-range window. Real tables are checkpointed — see
        // Cdf_ReadOverACheckpointedHistory_* for the representative layout and the honest numbers. History:
        // v0 (protocol + metaData) .. v40; the CDF range is [40, 40], so the pre-range window is [0, 39] — 39
        // commits the pre-range identity gate must cover.
        //
        // BEFORE (main @ a6ff45f), the READ phase issued 4 log LISTs and 123 commit GETs:
        //     end snapshot  : 1 LIST + 41 GETs   (replay 0..40)
        //     pre-range gate: 1 LIST + 39 GETs   (scan of commits 1..39)
        //       its baseline: 1 LIST +  1 GET    (LoadSnapshotAsync(earliest = 0))
        //     start snapshot: 1 LIST + 41 GETs   (replay 0..40)
        //     in-range loop :          1 GET     (commit 40)
        // AFTER, the read phase issues 1 log LIST and 84 commit GETs: the gate runs off the listing the END
        // snapshot was reconstructed from (0 LISTs of its own) and consumes the start-snapshot replay's own
        // observations for every pre-range commit that replay covered (0 GETs of its own here).
        //
        // The load-bearing assertions are therefore: exactly ONE listing for the whole read, and NO commit
        // object read more than twice (once per surviving snapshot reconstruction) — i.e. the gate's O(retained
        // commits before start) third pass over the history is gone.
        const int latest = 40;
        using var local = new LocalFileSystemBackend(_root);
        await WriteLongHistoryAsync(local, latest, forgedIdentityAtVersion: null);

        var counting = new CountingStorageBackend(local);
        var log = new DeltaLog(counting);
        var reader = new ChangeFeedReader(counting, "io-budget", log, new ParquetFileReader());

        DeltaChangeFeedInfo info = await reader.ResolveAsync(
            DeltaChangeFeedRange.FromVersion(latest, latest), CancellationToken.None);
        counting.Reset();   // measure ONLY the read phase — resolution's budget is untouched by #691.

        await foreach (ColumnBatch batch in reader.ReadAsync(info, CancellationToken.None))
        {
            _ = batch;   // v40 commits no add/remove/cdc, so a well-formed read yields nothing.
        }

        Assert.Equal(1, counting.LogListings);

        // 41 (end replay) + 41 (start replay) + 1 (baseline at the earliest reconstructable version)
        // + 1 (the in-range loop's own read of v40) = 84. Anything more means a pass over the history came
        // back; the pre-range gate contributes ZERO commit GETs of its own on this layout.
        Assert.Equal((2 * (latest + 1)) + 2, counting.CommitReads);
        for (long v = 1; v < latest; v++)
        {
            Assert.Equal(2, counting.CommitReadsOf(v));   // the two reconstructions only — no gate re-read.
        }
    }

    [Fact]
    public async Task Cdf_ReadOverACheckpointedHistory_ListsTheLogOnce_AndOnlyReReadsBelowTheCheckpointFloor()
    {
        // #691 budget pin for the REPRESENTATIVE layout (council R1, architect seat). Every table Spark and
        // delta-rs write is checkpointed (default interval 10), so the checkpoint-FREE fixture above — where
        // the start-snapshot replay happens to cover the entire pre-range window and the gate's own GETs fall
        // to zero — is the optimization's BEST case, not its typical one. Here `checkpoint@30` splits the
        // pre-range window: both reconstructions seed at 30 and replay only 31..40, so the gate can reuse
        // observations for 31..39 and must still READ 1..30 itself.
        //
        //                         main @ a6ff45f   this PR
        //   log LISTs                          4         1     (universal: threading the resolution's listing)
        //   commit GETs                       61        53     (-13%)
        //     of which the gate's own         40        32     (-20%)
        //
        // The LIST reduction is layout-independent. The GET reduction is NOT: it is bounded by how much of the
        // pre-range window the replay covers, i.e. by the checkpoint interval — so for a long retained history
        // the scan is asymptotically unchanged. This test exists so that DOMINANT term (the below-floor scan)
        // is pinned rather than invisible: a regression that doubled it would be caught here.
        const int latest = 40;
        using var local = new LocalFileSystemBackend(_root);
        await WriteLongHistoryAsync(local, latest, forgedIdentityAtVersion: null);
        await WriteCheckpointAtAsync(local, 30);

        var counting = new CountingStorageBackend(local);
        var log = new DeltaLog(counting);
        var reader = new ChangeFeedReader(counting, "io-budget", log, new ParquetFileReader());

        DeltaChangeFeedInfo info = await reader.ResolveAsync(
            DeltaChangeFeedRange.FromVersion(latest, latest), CancellationToken.None);
        counting.Reset();

        await foreach (ColumnBatch batch in reader.ReadAsync(info, CancellationToken.None))
        {
            _ = batch;
        }

        Assert.Equal(1, counting.LogListings);

        // 10 (end replay 31..40) + 10 (start replay 31..40) + 2 (baseline snapshot at v0, PLUS the gate now
        // reads v0's OWN metaData — the baseline reconstructs the checkpoint-baked identity, which a forger can
        // make disagree with <v0>.json, so the floor commit's own declaration is validated too, #691 2nd fix)
        // + 30 (the gate's own reads of 1..30, which NO replay reaches) + 1 (in-range read of v40) = 53.
        Assert.Equal(53, counting.CommitReads);
        Assert.Equal(2, counting.CommitReadsOf(0));                    // baseline snapshot + the floor commit's own metaData
        for (long v = 1; v <= 30; v++)
        {
            Assert.Equal(1, counting.CommitReadsOf(v));                 // below the floor: the gate reads it once
        }

        for (long v = 31; v < latest; v++)
        {
            Assert.Equal(2, counting.CommitReadsOf(v));                 // two reconstructions; the gate reuses
        }
    }

    [Fact]
    public async Task Cdf_PreRangeIdentityGate_StillFailsClosed_OnAForgedCommitDeepInALongHistory()
    {
        // Coverage half of the budget pin: reusing the start-snapshot replay's observations must not make the
        // gate vacuous. A forged identity-changing metaData at v20 — deep inside the pre-range window, read
        // ONLY through the replay observation (the gate issues no GET of its own for it) — still fails the
        // read closed, naming exactly that version and leaking no path (#653).
        const int latest = 40;
        using var local = new LocalFileSystemBackend(_root);
        await WriteLongHistoryAsync(local, latest, forgedIdentityAtVersion: 20);

        var counting = new CountingStorageBackend(local);
        var log = new DeltaLog(counting);
        var reader = new ChangeFeedReader(counting, "io-budget", log, new ParquetFileReader());

        DeltaChangeFeedInfo info = await reader.ResolveAsync(
            DeltaChangeFeedRange.FromVersion(latest, latest), CancellationToken.None);
        counting.Reset();

        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(async () =>
        {
            await foreach (ColumnBatch batch in reader.ReadAsync(info, CancellationToken.None))
            {
                _ = batch;
            }
        });

        Assert.Contains("column-mapping identity", ex.Message, StringComparison.Ordinal);
        Assert.Contains("immutable", ex.Message, StringComparison.Ordinal);
        Assert.Contains("version 20", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("data/", ex.Message, StringComparison.Ordinal);       // #653: no path
        Assert.DoesNotContain("_delta_log", ex.Message, StringComparison.Ordinal);  // #653: no path
        Assert.DoesNotContain("col-A", ex.Message, StringComparison.Ordinal);       // #653: no physical name
        Assert.DoesNotContain("col-B", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, counting.LogListings);
        Assert.Equal(2, counting.CommitReadsOf(20));   // the two reconstructions; the gate re-read nothing.
    }

    [Fact]
    public async Task Cdf_PreRangeIdentityGate_StillReadsASurvivingSubFloorCommitTheReplayCannotReach()
    {
        // The reconstruction-reuse optimization must NOT narrow the scan to "whatever the replay happened to
        // read". A commit whose JSON survives strictly BELOW a compacting checkpoint floor is never replayed
        // (the start-snapshot reconstruction seeds from checkpoint@2 and replays only 3..), so the gate MUST
        // still GET it. Layout mirrors Cdf_SurvivingSubFloorCommitIdentityDiffers_FailsClosed_*: v0 aged out,
        // checkpoint@2 bakes identity X (== end), a RETAINED sub-floor v1.json forges identity Y, end v3 == X.
        // Asserting the GET of v1 pins the un-replayed branch as load-bearing; the fail-closed asserts pin
        // that it is still validated.
        string SchemaJson(int idForId, int idForName) =>
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + "{\"delta.columnMapping.id\":" + idForId.ToString(CultureInfo.InvariantCulture)
            + ",\"delta.columnMapping.physicalName\":\"col-A\"}},"
            + "{\"name\":\"name\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":" + idForName.ToString(CultureInfo.InvariantCulture)
            + ",\"delta.columnMapping.physicalName\":\"col-B\"}}]}";
        var config = new[]
        {
            ("delta.columnMapping.mode", "id"),
            ("delta.columnMapping.maxColumnId", "2"),
            ("delta.enableChangeDataFeed", "true"),
        };
        string MetaLine(int idForId, int idForName) =>
            "{\"metaData\":{\"id\":\"rt\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
            + "\"schemaString\":" + JsonSerializer.Serialize(SchemaJson(idForId, idForName))
            + ",\"partitionColumns\":[],\"configuration\":{"
            + "\"delta.columnMapping.mode\":\"id\",\"delta.columnMapping.maxColumnId\":\"2\","
            + "\"delta.enableChangeDataFeed\":\"true\"}}}";

        const string histFile = "data/subfloor-hist.parquet";
        using var local = new LocalFileSystemBackend(_root);
        await DeltaTestHarness.WriteCheckpointAsync(local, 2, new CheckpointFixture()
            .Protocol(3, 7, new[] { "columnMapping" }, new[] { "columnMapping", "changeDataFeed" })
            .Metadata("rt", SchemaJson(1, 2), partitionColumns: null, configuration: config)
            .Add(histFile, size: 1, modificationTime: 1));
        await DeltaTestHarness.WriteLastCheckpointAsync(local, 2);
        await DeltaTestHarness.WriteCommitAsync(local, 1, MetaLine(2, 1));                        // sub-floor forgery
        await DeltaTestHarness.WriteCommitAsync(local, 3, MetaLine(1, 2), DeltaTestHarness.Remove(histFile));

        var counting = new CountingStorageBackend(local);
        var log = new DeltaLog(counting);
        var reader = new ChangeFeedReader(counting, "io-budget", log, new ParquetFileReader());

        DeltaChangeFeedInfo info = await reader.ResolveAsync(
            DeltaChangeFeedRange.FromVersion(3, 3), CancellationToken.None);
        counting.Reset();

        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(async () =>
        {
            await foreach (ColumnBatch batch in reader.ReadAsync(info, CancellationToken.None))
            {
                _ = batch;
            }
        });

        Assert.Contains("column-mapping identity", ex.Message, StringComparison.Ordinal);
        Assert.Contains("version 1", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(histFile, ex.Message, StringComparison.Ordinal);
        // The sub-floor commit is unreachable by ANY replay, so the gate read it itself — exactly once.
        Assert.Equal(1, counting.CommitReadsOf(1));
        Assert.Equal(1, counting.LogListings);
    }

    [Fact]
    public async Task Cdf_PreRangeCommitBelowTheSeedingCheckpoint_IsReadFromDisk_AndStillFailsClosed()
    {
        // Council R1 (quality seat): pins the DISK-FALLBACK branch of the observation seam for an ORDINARY
        // pre-range commit — not just the sub-floor edge. Layout: v0..v40 all retained (so the earliest
        // reconstructable version is 0) with a complete checkpoint@20. Both snapshot reconstructions seed from
        // that checkpoint and replay only 21..40, so pre-range commits 1..19 are covered by NO observation at
        // all. The gate must therefore READ them, and a forged identity at v10 (reverted at v11, so the end
        // identity is untouched and only the deep-history scan can see it) must still fail the read closed.
        //
        // This is the property that makes an observer defect cost only PERFORMANCE: "not observed" can never
        // be read as "nothing to validate".
        const int latest = 40;
        using var local = new LocalFileSystemBackend(_root);
        await WriteLongHistoryAsync(local, latest, forgedIdentityAtVersion: 10);
        await WriteCheckpointAtAsync(local, 20);

        var counting = new CountingStorageBackend(local);
        var log = new DeltaLog(counting);
        var reader = new ChangeFeedReader(counting, "io-budget", log, new ParquetFileReader());

        DeltaChangeFeedInfo info = await reader.ResolveAsync(
            DeltaChangeFeedRange.FromVersion(latest, latest), CancellationToken.None);
        counting.Reset();

        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(async () =>
        {
            await foreach (ColumnBatch batch in reader.ReadAsync(info, CancellationToken.None))
            {
                _ = batch;
            }
        });

        Assert.Contains("column-mapping identity", ex.Message, StringComparison.Ordinal);
        Assert.Contains("version 10", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("_delta_log", ex.Message, StringComparison.Ordinal);  // #653: no path
        Assert.DoesNotContain("col-A", ex.Message, StringComparison.Ordinal);       // #653: no physical name
        Assert.DoesNotContain("col-B", ex.Message, StringComparison.Ordinal);

        // No replay reaches v10, so the gate read it itself — exactly once, and only because the seam reported
        // "not covered" rather than inferring silence.
        Assert.Equal(1, counting.CommitReadsOf(10));
        Assert.Equal(1, counting.LogListings);
    }

    [Fact]
    public async Task StartSnapshot_LoadedThroughTheObservedPath_IsIdenticalToTheUnobservedLoad()
    {
        // Council R1 (quality seat, low): the observation seam is claimed to be BEHAVIOUR-NEUTRAL for the
        // snapshot itself — the observer is a pure sink, and passing `null` (every ordinary load) leaves the
        // reconstruction byte-for-byte on its pre-#691 path. Nothing asserted that. This pins it: the snapshot
        // the fused change-feed load returns is equal, field by field, to the one the plain (null-observer)
        // LoadSnapshotAsync returns for the same version, and the reconstruction costs the same commit GETs.
        const int latest = 40;
        using var local = new LocalFileSystemBackend(_root);
        await WriteLongHistoryAsync(local, latest, forgedIdentityAtVersion: null);

        var counting = new CountingStorageBackend(local);
        var log = new DeltaLog(counting);

        // Unobserved: the ordinary public load path, exactly as it behaved before #691.
        counting.Reset();
        Snapshot unobserved = await log.LoadSnapshotAsync(30, CancellationToken.None);
        int unobservedCommitReads = counting.CommitReads;

        // Observed: the fused change-feed start load, which threads a ReplayedMetadataLog through the SAME
        // reconstruction and then runs the pre-range gate off its observations.
        (Snapshot End, DeltaLog.ChangeFeedEndView View) end =
            await log.LoadChangeFeedEndViewAsync(latest, CancellationToken.None);
        counting.Reset();
        Snapshot observed = await log.LoadChangeFeedStartSnapshotAsync(
            end.View, 30, CancellationToken.None);
        int observedCommitReads = counting.CommitReads;

        Assert.Equal(unobserved.Version, observed.Version);
        Assert.Equal(unobserved.Protocol.MinReaderVersion, observed.Protocol.MinReaderVersion);
        Assert.Equal(unobserved.Protocol.MinWriterVersion, observed.Protocol.MinWriterVersion);
        Assert.Equal(unobserved.Protocol.ReaderFeatures.ToArray(), observed.Protocol.ReaderFeatures.ToArray());
        Assert.Equal(unobserved.Protocol.WriterFeatures.ToArray(), observed.Protocol.WriterFeatures.ToArray());
        Assert.Equal(unobserved.Metadata.Id, observed.Metadata.Id);
        Assert.Equal(unobserved.Metadata.SchemaString, observed.Metadata.SchemaString);
        Assert.Equal(unobserved.Metadata.PartitionColumns.ToArray(), observed.Metadata.PartitionColumns.ToArray());
        Assert.Equal(unobserved.Metadata.Configuration.ToArray(), observed.Metadata.Configuration.ToArray());
        Assert.Equal(unobserved.Schema.ToString(), observed.Schema.ToString());
        Assert.Equal(
            unobserved.ActiveFiles.Select(f => f.Path).ToArray(),
            observed.ActiveFiles.Select(f => f.Path).ToArray());
        Assert.Equal(
            unobserved.Tombstones.Select(f => f.Path).ToArray(),
            observed.Tombstones.Select(f => f.Path).ToArray());
        Assert.Equal(unobserved.Transactions.ToArray(), observed.Transactions.ToArray());

        // The observed load reads the same 31 commits (0..30) the unobserved one does; its ONLY extra read is
        // the pre-range gate's baseline snapshot at the earliest reconstructable version (v0). The gate itself
        // contributes nothing further, because every other pre-range commit is a proven, corroborated
        // observation of the replay above.
        Assert.Equal(unobservedCommitReads + 1, observedCommitReads);
    }

    // Writes v0 = protocol + an id-mode, CDF-enabled metaData, v1..(latest-1) = single-`add` commits, and
    // `latest` = a metadata-free txn commit so a well-formed read of [latest, latest] touches no data file.
    // When `forgedIdentityAtVersion` is set, that version carries a FORGED identity-changing metaData (the
    // field ids swapped) which the NEXT version REVERTS — a transient pre-range flip, so the end identity
    // still equals v0's and ONLY the deep-history scan can catch it.
    private static async Task WriteLongHistoryAsync(
        IStorageBackend backend, int latest, long? forgedIdentityAtVersion)
    {
        string SchemaJson(int idForId, int idForName) =>
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + "{\"delta.columnMapping.id\":" + idForId.ToString(CultureInfo.InvariantCulture)
            + ",\"delta.columnMapping.physicalName\":\"col-A\"}},"
            + "{\"name\":\"name\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":" + idForName.ToString(CultureInfo.InvariantCulture)
            + ",\"delta.columnMapping.physicalName\":\"col-B\"}}]}";
        string MetaLine(int idForId, int idForName) =>
            "{\"metaData\":{\"id\":\"rt\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
            + "\"schemaString\":" + JsonSerializer.Serialize(SchemaJson(idForId, idForName))
            + ",\"partitionColumns\":[],\"configuration\":{"
            + "\"delta.columnMapping.mode\":\"id\",\"delta.columnMapping.maxColumnId\":\"2\","
            + "\"delta.enableChangeDataFeed\":\"true\"}}}";

        await DeltaTestHarness.WriteCommitAsync(backend, 0, Protocol, MetaLine(1, 2));
        for (long v = 1; v < latest; v++)
        {
            string add = DeltaTestHarness.Add(
                "data/f" + v.ToString(CultureInfo.InvariantCulture) + ".parquet");
            await DeltaTestHarness.WriteCommitAsync(
                backend,
                v,
                v == forgedIdentityAtVersion ? MetaLine(2, 1)                    // forged: field ids SWAPPED
                    : v == forgedIdentityAtVersion + 1 ? MetaLine(1, 2)           // reverted before the end
                    : add,
                v == forgedIdentityAtVersion || v == forgedIdentityAtVersion + 1
                    ? add
                    : DeltaTestHarness.Txn("filler", v));
        }

        await DeltaTestHarness.WriteCommitAsync(backend, latest, DeltaTestHarness.Txn("tail", 1));
    }

    // Writes a COMPLETE single-part checkpoint at `version` over the history WriteLongHistoryAsync produced,
    // plus the _last_checkpoint hint, so a snapshot reconstruction seeds there and never replays [1, version].
    private static async Task WriteCheckpointAtAsync(IStorageBackend backend, int version)
    {
        var fixture = new CheckpointFixture()
            .Protocol(3, 7, new[] { "columnMapping" }, new[] { "columnMapping", "changeDataFeed" })
            .Metadata(
                "rt",
                "{\"type\":\"struct\",\"fields\":["
                + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
                + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
                + "{\"name\":\"name\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
                + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"col-B\"}}]}",
                partitionColumns: null,
                configuration: new[]
                {
                    ("delta.columnMapping.mode", "id"),
                    ("delta.columnMapping.maxColumnId", "2"),
                    ("delta.enableChangeDataFeed", "true"),
                });
        for (int v = 1; v <= version; v++)
        {
            fixture = fixture.Add(
                "data/f" + v.ToString(CultureInfo.InvariantCulture) + ".parquet", size: 1, modificationTime: 1);
        }

        await DeltaTestHarness.WriteCheckpointAsync(backend, version, fixture);
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, version);
    }
}
