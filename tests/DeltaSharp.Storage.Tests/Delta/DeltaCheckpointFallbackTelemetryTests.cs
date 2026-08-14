using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Diagnostics;
using DeltaSharp.Storage.Tests.Parquet;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Observability coverage for the checkpoint-reconstruction fallback signal (#772, design §2.10.3/§7): when
/// a classic checkpoint is discarded during reconstruction (an encrypted #681/#698 checkpoint the reader
/// cannot read, or a malformed/corrupt one) and reconstruction falls back to JSON replay, a structured
/// event must be emitted — otherwise the actionable "your checkpoint is unreadable" diagnosis is invisible
/// to an operator (a successful read hides it; an aged-out table surfaces only a generic "missing commit").
/// These tests assert the metric (instrument + bounded <c>reason</c> label) and the Warning log (fields +
/// <see cref="EventId"/>) on an isolated telemetry surface, and that the emission is side-effect-free on the
/// reconstruction result (it still falls back to JSON replay and produces the identical table). The signal
/// is a safe no-op until a host wires a meter/logging provider — the existing parity tests in
/// <see cref="DeltaLogCheckpointTests"/> exercise the default (<see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger{T}"/>
/// / <see cref="DeltaStorageTelemetry.Shared"/>) construction path with no listener.
/// </summary>
public sealed class DeltaCheckpointFallbackTelemetryTests : IDisposable
{
    // The stable literal strings operators/exporters key on (the metric instrument name and its bounded
    // reason label), asserted here so a rename cannot silently break a dashboard/alert.
    private const string FallbackInstrument = "deltasharp.delta.checkpoint.fallbacks";
    private const string DecodeBudgetInstrument = "deltasharp.storage.decode.budget_exceeded";
    private const string DecodeDoorKey = "deltasharp.decode.door";
    private const string ReasonKey = "deltasharp.checkpoint.fallback.reason";
    private const string TableVersionKey = "deltasharp.table.version";
    private const string ComponentKey = "deltasharp.component";
    private const string OperationKey = "deltasharp.operation";
    private const string BackendKey = "deltasharp.backend";
    private const string FallbackEvent = "DeltaCheckpointFallback";
    private const string ForgedEvent = "DeltaCheckpointForgedMultiMetadataRejected";

    private const string EmptySchemaUnescaped = """{"type":"struct","fields":[]}""";

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
        string root = Path.Combine(Path.GetTempPath(), "cp-fallback-telemetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return new LocalFileSystemBackend(root);
    }

    // A SECOND, independent backend instance rooted at the SAME table root — modelling production, which
    // constructs a fresh LocalFileSystemBackend per scan/resolve (DeltaScanSource/DeltaFileRelationResolver/
    // DeltaReadSource). The checkpoint decode negative cache must key on the STABLE table identity so this
    // fresh instance still hits the cache the first instance seeded (I4/#647-Round2); an instance-keyed cache
    // (the Round-1 bug) would miss here and re-decode the known-bad checkpoint on every load.
    private static LocalFileSystemBackend FreshBackendAtSameRoot(LocalFileSystemBackend original) =>
        new(original.TableRootId);

    // A PER-LOAD isolated checkpoint decoder for the hanging-decode telemetry tests (Round-8 test isolation).
    // These tests deliberately drive a NON-TERMINATING checkpoint decode that DETACHES and strands its door
    // FOREVER, across multiple loads. With the honest per-part strand charge (Round-8 #3, GiB-scale) a stranded
    // decode on the PROCESS-GLOBAL static BoundedDecode.CheckpointDecoder would permanently consume its residual
    // and saturate it for every other test (pre-Round-8 the charge was ~KB, so pollution was harmless). Under the
    // Round-10 #3 re-scoping the sustained-pressure guard reads the DECODER ACTUALLY USED (not the static global)
    // and gates on the TRANSIENT strand delta, so a FRESH per-load decoder starts at zero strands and each load
    // seeds a strike — the strike accumulation these tests assert accrues on the shared static negative CACHE
    // (keyed on stable table identity, not the decoder) exactly as production does.
    //
    // PRODUCTION-SIZED (Round-10 test sizing): sized from the REAL checkpoint door footprint
    // (CheckpointMaxFootprintBytes) with a matching residual, NOT the 1-byte default — otherwise every strand
    // CHARGE clamps to 1 byte and the charge oracles are vacuous.
    private static BoundedDecoder IsolatedCheckpointDecoder() =>
        BoundedDecoder.FromSizing(
            BoundedDecode.DeriveDoorSizing(
                256L * 1024 * 1024 * 1024, BoundedDecode.CheckpointMaxFootprintBytes, processorCount: 8),
            DecodeExecution.DedicatedThread);

    /// <summary>A 2-version JSON history (v0: protocol+metadata+add(a); v1: add(b)) so a discarded checkpoint
    /// at v1 falls back to a fully replayable log.</summary>
    private static async Task WriteHistoryAsync(IStorageBackend backend)
    {
        await DeltaTestHarness.WriteCommitAsync(backend, 0,
            DeltaTestHarness.Protocol(),
            DeltaTestHarness.Metadata(id: "t"),
            DeltaTestHarness.Add("a.parquet"));
        await DeltaTestHarness.WriteCommitAsync(backend, 1,
            DeltaTestHarness.Add("b.parquet"));
    }

    private static CheckpointFixture CheckpointAtV1() =>
        new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata(id: "t", schemaString: EmptySchemaUnescaped)
            .Add("a.parquet", size: 1, modificationTime: 1)
            .Add("b.parquet", size: 1, modificationTime: 1);

    [Fact]
    public async Task MalformedCheckpoint_EmitsFallbackEvent_WithMalformedReason()
    {
        IStorageBackend backend = NewBackend();
        await WriteHistoryAsync(backend);
        await DeltaTestHarness.WriteRawCheckpointAsync(backend, 1, "not a parquet file"u8.ToArray());
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1);

        (Snapshot snapshot, MeterCapture.Measurement metric, RecordingLogger<DeltaLog>.Entry log) =
            await LoadWithCaptureAsync(backend);

        // Still fell back to JSON replay and reconstructed the identical table (no checkpoint claimed).
        Assert.Null(snapshot.Metrics.CheckpointVersion);
        Assert.Equal(2, snapshot.Metrics.ReplayedCommitCount);

        Assert.Equal(1, metric.Value);
        Assert.Equal("malformed", metric.Tags[ReasonKey]);

        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal(4400, log.EventId.Id);
        Assert.Equal(1L, log.Field("Version"));
        Assert.Equal("malformed", log.Field("Reason"));
    }

    [Fact]
    public async Task EncryptedCheckpoint_EmitsFallbackEvent_WithUnsupportedFeatureReason()
    {
        IStorageBackend backend = NewBackend();
        await WriteHistoryAsync(backend);
        byte[] checkpoint = await CheckpointAtV1().ToParquetAsync();
        await DeltaTestHarness.WriteRawCheckpointAsync(
            backend, 1, await ParquetTestHelpers.PlaintextFooterEncryptedFileAsync(checkpoint));
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1);

        (Snapshot snapshot, MeterCapture.Measurement metric, RecordingLogger<DeltaLog>.Entry log) =
            await LoadWithCaptureAsync(backend);

        Assert.Null(snapshot.Metrics.CheckpointVersion);
        Assert.Equal(2, snapshot.Metrics.ReplayedCommitCount);

        Assert.Equal(1, metric.Value);
        Assert.Equal("unsupported_feature", metric.Tags[ReasonKey]);

        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal(4400, log.EventId.Id);
        Assert.Equal(1L, log.Field("Version"));
        Assert.Equal("unsupported_feature", log.Field("Reason"));
    }

    [Fact]
    public async Task IntactCheckpoint_EmitsNoFallbackEvent()
    {
        IStorageBackend backend = NewBackend();
        await WriteHistoryAsync(backend);
        await DeltaTestHarness.WriteCheckpointAsync(backend, 1, CheckpointAtV1());
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1);

        var logger = new RecordingLogger<DeltaLog>();
        using var telemetry = new DeltaStorageTelemetry();
        using var meters = new MeterCapture(telemetry.DeltaMeter);

        Snapshot snapshot = await new DeltaLog(backend, DeltaLog.MaxLogObjectBytes, logger, telemetry)
            .LoadSnapshotAsync();

        // Fast path taken (seeded from v1), so there is no discard and no fallback signal — neither the generic
        // 4400 nor the forged-reject 4401 (a spurious 4401 here would be a false identity-forgery alert).
        Assert.Equal(1, snapshot.Metrics.CheckpointVersion);
        Assert.Empty(meters.ForInstrument(FallbackInstrument));
        Assert.False(logger.Has(FallbackEvent));
        Assert.False(logger.Has(ForgedEvent));
    }

    [Fact]
    public async Task ForgedMultiMetadataCheckpoint_EmitsForgedRejectSignal_WithForgedReason_NoLeak()
    {
        IStorageBackend backend = NewBackend();
        await WriteHistoryAsync(backend);
        // A forged checkpoint carrying TWO metaData actions (split across parts) — the #671 cross-part
        // forgery the single-metaData guard rejects. It must be discarded (never seeded) and attributed to the
        // DISTINGUISHED `forged_multi_metadata` reason (a security signal, #763) — not the generic `malformed`
        // that bit-rot uses — via a distinct EventId 4401. The swallowed reject's detail (which embeds the
        // attacker-chosen metaData id/count) must NOT leak: only {Version} may be rendered.
        CheckpointFixture forged = new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata(id: "t-clean-identity", schemaString: EmptySchemaUnescaped)
            .Metadata(id: "t-forged-identity", schemaString: EmptySchemaUnescaped)
            .Add("a.parquet", size: 1, modificationTime: 1);
        await DeltaTestHarness.WriteMultipartCheckpointAsync(backend, 1, forged, parts: 2);
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1, parts: 2);

        (Snapshot snapshot, MeterCapture.Measurement metric, RecordingLogger<DeltaLog>.Entry log) =
            await LoadWithCaptureAsync(backend, ForgedEvent);

        Assert.Null(snapshot.Metrics.CheckpointVersion); // forged checkpoint discarded → JSON replay
        Assert.Equal(2, snapshot.Metrics.ReplayedCommitCount);

        // Distinguished from bit-rot `malformed`: bounded metric reason + the security-specific EventId 4401.
        Assert.Equal(1, metric.Value);
        Assert.Equal("forged_multi_metadata", metric.Tags[ReasonKey]);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal(4401, log.EventId.Id);
        Assert.Equal(1L, log.Field("Version"));

        // No-leak (#763/#671): the log line is a compile-time template with only {Version} substituted, so no
        // attacker-planted metaData id or count can surface. The full-message equality below is the decisive
        // redaction-by-omission guard (the #786 `_NoLeak` template); the DoesNotContain sentinels — asserting
        // NEITHER the forged NOR the clean fixture metaData id appears — document intent and guard against a
        // future refactor that keeps a sentinel but drops the equality pin.
        Assert.DoesNotContain("t-forged-identity", log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("t-clean-identity", log.Message, StringComparison.Ordinal);
        Assert.Equal(
            "Delta checkpoint at version 1 was rejected as forged and not used to seed the snapshot: it carries "
            + "more than one metaData action across its parts (a checkpoint must summarize at most one); "
            + "reconstruction falls back to an older checkpoint or full JSON replay.",
            log.Message);
    }

    [Fact]
    public async Task NewerCheckpointDiscarded_RecoveredFromOlder_SignalsTheDiscardedVersion()
    {
        IStorageBackend backend = NewBackend();
        await WriteHistoryAsync(backend); // v0, v1
        await DeltaTestHarness.WriteCommitAsync(backend, 2, DeltaTestHarness.Add("c.parquet"));
        await DeltaTestHarness.WriteCheckpointAsync(backend, 1, CheckpointAtV1());          // GOOD older @1
        await DeltaTestHarness.WriteRawCheckpointAsync(backend, 2, "corrupt"u8.ToArray());  // BAD newer @2
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 2);

        (Snapshot snapshot, MeterCapture.Measurement metric, RecordingLogger<DeltaLog>.Entry log) =
            await LoadWithCaptureAsync(backend);

        // Recovered from the older complete checkpoint @1 — so CheckpointVersion is 1, NOT null. This is the
        // one case where CheckpointVersion alone cannot reveal the fallback: the signal must name the
        // DISCARDED checkpoint (v2), not the seeded one, or a "healthy" table hides a corrupt newest checkpoint.
        Assert.Equal(1, snapshot.Metrics.CheckpointVersion);
        Assert.Equal("malformed", metric.Tags[ReasonKey]);
        Assert.Equal(2L, log.Field("Version"));
        Assert.Equal("malformed", log.Field("Reason"));
    }

    [Fact]
    public async Task IncompleteMultipartCheckpoint_EmitsNoFallbackSignal()
    {
        IStorageBackend backend = NewBackend();
        await WriteHistoryAsync(backend);
        // A partial 3-part checkpoint (parts 1 and 3 present, part 2 missing) is skipped at SELECTION, before
        // any seed attempt — so it is intentionally NOT a seed-time discard and emits no fallback signal. This
        // pins the counter's documented scope boundary (design §2.10.4) so the exclusion is a decision, not
        // drift; the same holds for V2/UUID checkpoints and a failed _last_checkpoint hint read.
        await DeltaTestHarness.WritePartialMultipartCheckpointAsync(backend, 1, CheckpointAtV1(), parts: 3, partsToWrite: [1, 3]);
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1, parts: 3);

        var logger = new RecordingLogger<DeltaLog>();
        using var telemetry = new DeltaStorageTelemetry();
        using var meters = new MeterCapture(telemetry.DeltaMeter);

        Snapshot snapshot = await new DeltaLog(backend, DeltaLog.MaxLogObjectBytes, logger, telemetry)
            .LoadSnapshotAsync();

        Assert.Null(snapshot.Metrics.CheckpointVersion); // incomplete group skipped → full JSON replay
        Assert.Empty(meters.ForInstrument(FallbackInstrument));
        Assert.False(logger.Has(FallbackEvent));
        Assert.False(logger.Has(ForgedEvent));
    }

    [Fact]
    public async Task DecodeTimeoutCheckpoint_EmitsDecodeTimeoutSignal_AndNegativeCacheSkipsReDecode_ThroughAFreshBackendPerLoad()
    {
        LocalFileSystemBackend backend = NewBackend();
        await WriteHistoryAsync(backend);

        // A hanging checkpoint: a single bit flip in the LAST footer byte (the terminal Thrift STOP of
        // FileMetaData, index len-9 before the footer_length + PAR1 magic) drives DeltaCheckpointReader into
        // unbounded, cancellation-ignoring work (the #699/#716 decode-DoS class). Under a LOW injected budget
        // the reader must fail closed with the DISTINCT decode-timeout signal (not the generic `malformed`
        // bit-rot reason) and still fall back to JSON replay.
        byte[] hanging = await CheckpointAtV1().ToParquetAsync();
        hanging[^9] ^= 1;
        await DeltaTestHarness.WriteRawCheckpointAsync(backend, 1, hanging);
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1);

        var budget = TimeSpan.FromMilliseconds(300);

        // FIRST load: the decode runs the full budget, then is failed closed and routed to JSON replay under
        // the decode_timeout reason (EventId 4402) with the door-dimensioned decode.budget_exceeded counter.
        var logger = new RecordingLogger<DeltaLog>();
        using (var telemetry = new DeltaStorageTelemetry())
        using (var deltaMeter = new MeterCapture(telemetry.DeltaMeter))
        using (var storageMeter = new MeterCapture(telemetry.StorageMeter))
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Snapshot snapshot = await new DeltaLog(
                backend, DeltaLog.MaxLogObjectBytes, logger, telemetry, checkpointDecodeBudget: budget,
                checkpointDecoder: IsolatedCheckpointDecoder())
                .LoadSnapshotAsync();
            stopwatch.Stop();

            // Fell back to JSON replay (checkpoint discarded, not seeded).
            Assert.Null(snapshot.Metrics.CheckpointVersion);
            Assert.Equal(2, snapshot.Metrics.ReplayedCommitCount);

            // Distinct decode-timeout fallback reason (NOT `malformed`) + EventId 4402.
            MeterCapture.Measurement fallback = Assert.Single(deltaMeter.ForInstrument(FallbackInstrument));
            Assert.Equal("decode_timeout", fallback.Tags[ReasonKey]);
            RecordingLogger<DeltaLog>.Entry log = logger.Single("DeltaCheckpointDecodeTimeout");
            Assert.Equal(4402, log.EventId.Id);
            Assert.Equal(1L, log.Field("Version"));

            // The door-dimensioned decode.budget_exceeded counter fired for the checkpoint door with the
            // stage=whole discriminator (a checkpoint decodes the WHOLE pre-buffered part in one shot, unlike the
            // data-file door's open/metadata/row_group sub-stages) — MEDIUM stage-label assertion.
            MeterCapture.Measurement door = Assert.Single(storageMeter.ForInstrument(DecodeBudgetInstrument));
            Assert.Equal("checkpoint", door.Tags[DecodeDoorKey]);
            Assert.Equal("whole", door.Tags["deltasharp.decode.stage"]);

            // The decode actually ran the budget on the first (uncached) load.
            Assert.True(stopwatch.Elapsed >= budget, $"expected the first load to run the budget, took {stopwatch.Elapsed}.");
        }

        // SECOND load: the SAME identity times out AGAIN (strike 2). Per the strike-gate (High #6) a single
        // timeout does NOT poison — a healthy-but-slow decode that timed out once must still be re-decoded — so
        // this load STILL runs the full budget and emits decode_timeout; only now does the identity cross the
        // suppression threshold. (A fresh backend rooted at the same table, to also prove strikes accumulate
        // across the per-scan/resolve backend instances.)
        LocalFileSystemBackend strikeTwoBackend = FreshBackendAtSameRoot(backend);
        using (var telemetryS2 = new DeltaStorageTelemetry())
        using (var deltaMeterS2 = new MeterCapture(telemetryS2.DeltaMeter))
        {
            var stopwatchS2 = System.Diagnostics.Stopwatch.StartNew();
            Snapshot snapshotS2 = await new DeltaLog(
                strikeTwoBackend, DeltaLog.MaxLogObjectBytes, new RecordingLogger<DeltaLog>(), telemetryS2, checkpointDecodeBudget: budget,
                checkpointDecoder: IsolatedCheckpointDecoder())
                .LoadSnapshotAsync();
            stopwatchS2.Stop();

            Assert.Null(snapshotS2.Metrics.CheckpointVersion);
            MeterCapture.Measurement fallbackS2 = Assert.Single(deltaMeterS2.ForInstrument(FallbackInstrument));
            Assert.Equal("decode_timeout", fallbackS2.Tags[ReasonKey]); // still re-decoded (strike-gate)
            Assert.True(stopwatchS2.Elapsed >= budget, $"expected the strike-2 load to run the budget, took {stopwatchS2.Elapsed}.");
        }

        // THIRD load through a FRESH backend instance rooted at the SAME table (production builds a new
        // LocalFileSystemBackend per scan/resolve): now that the identity has crossed the strike threshold the
        // negative cache — keyed on STABLE table identity, not the backend instance — short-circuits the read,
        // so the known-bad checkpoint is NOT re-decoded. Proven by the load returning WELL under the decode
        // budget (no decode ran) while re-emitting the DISTINCT negative_cache_skip signal (NOT decode_timeout —
        // no decode ran, the de-conflation fix) so a persistently bad checkpoint stays observable. Under the
        // Round-1 instance-keyed cache this fresh backend would MISS and re-run the full budget — so this
        // assertion is red if the re-keying is reverted.
        LocalFileSystemBackend freshBackend = FreshBackendAtSameRoot(backend);
        var logger2 = new RecordingLogger<DeltaLog>();
        using (var telemetry2 = new DeltaStorageTelemetry())
        using (var deltaMeter2 = new MeterCapture(telemetry2.DeltaMeter))
        using (var storageMeter2 = new MeterCapture(telemetry2.StorageMeter))
        {
            Snapshot snapshot2 = await new DeltaLog(
                freshBackend, DeltaLog.MaxLogObjectBytes, logger2, telemetry2, checkpointDecodeBudget: budget)
                .LoadSnapshotAsync();

            Assert.Null(snapshot2.Metrics.CheckpointVersion);
            Assert.Equal(2, snapshot2.Metrics.ReplayedCommitCount);
            MeterCapture.Measurement fallback2 = Assert.Single(deltaMeter2.ForInstrument(FallbackInstrument));
            Assert.Equal("negative_cache_skip", fallback2.Tags[ReasonKey]);

            // The de-conflation fix + the MECHANICAL skip proof: the SKIP path emits the DISTINCT
            // negative_cache_skip counter and NEVER the decode.budget_exceeded counter (no decode ran on this
            // load). This absence of a decode signal — not a flaky wall-clock margin — is the deterministic
            // proof the negative cache short-circuited the re-decode (the open-count oracle in
            // NegativeCache_OpenCountOracle pins the same invariant by open count).
            MeterCapture.Measurement skip = Assert.Single(storageMeter2.ForInstrument("deltasharp.storage.decode.negative_cache_skip"));
            Assert.Equal("checkpoint", skip.Tags[DecodeDoorKey]);
            Assert.Empty(storageMeter2.ForInstrument(DecodeBudgetInstrument));
        }
    }

    [Fact]
    public async Task NegativeCache_OpenCountOracle_PartOpenedOncePerTtlWindow_TwiceAfterTtlAdvance()
    {
        // High #10 — the MECHANICAL open-count oracle that replaces the flaky wall-clock (2% margin) oracle. A
        // CountingStorageBackend records every OpenReadAsync of the checkpoint part; the injectable TimeProvider
        // on DeltaLog drives the negative cache's TTL clock (WITHOUT touching the decode's real-time budget
        // timer — see OffsetUtcTimeProvider). The invariant under the strike-gate (High #6): the crafted part is
        // opened TWICE across the first two in-TTL loads (a single timeout only records a strike — it does NOT
        // poison — so the second load re-decodes and reaches the suppression threshold), is NOT opened on a
        // THIRD in-TTL load (now a negative-cache skip), and is opened ONCE MORE only after the TTL advances
        // (the re-probe). No sleeps, no timing margins.
        LocalFileSystemBackend inner = NewBackend();
        await WriteHistoryAsync(inner);
        byte[] hanging = await CheckpointAtV1().ToParquetAsync();
        hanging[^9] ^= 1; // terminal Thrift STOP flip → non-terminating decode (as in the decode-timeout test)
        await DeltaTestHarness.WriteRawCheckpointAsync(inner, 1, hanging);
        await DeltaTestHarness.WriteLastCheckpointAsync(inner, 1);

        var counting = new CountingStorageBackend(inner);
        var budget = TimeSpan.FromMilliseconds(300);
        string checkpointPart = "_delta_log/" + 1L.ToString("D20", System.Globalization.CultureInfo.InvariantCulture)
            + ".checkpoint.parquet";

        // Load 1 (T0): the decode times out on its OWN adequate budget → the identity records STRIKE 1 (not yet
        // suppressed). The part was opened once.
        Snapshot s1 = await new DeltaLog(counting, DeltaLog.MaxLogObjectBytes, checkpointDecodeBudget: budget,
            checkpointDecoder: IsolatedCheckpointDecoder())
            .LoadSnapshotAsync();
        Assert.Null(s1.Metrics.CheckpointVersion);
        Assert.Equal(1, CountOpensOf(counting, checkpointPart));

        // Load 2 (still T0, within the 10-min TTL): the strike-gate has NOT yet suppressed the identity (only
        // one strike so far), so this load RE-DECODES — opening the part a second time — times out again, and
        // now records STRIKE 2, crossing the suppression threshold.
        Snapshot s2 = await new DeltaLog(counting, DeltaLog.MaxLogObjectBytes, checkpointDecodeBudget: budget,
            checkpointDecoder: IsolatedCheckpointDecoder())
            .LoadSnapshotAsync();
        Assert.Null(s2.Metrics.CheckpointVersion);
        Assert.Equal(2, CountOpensOf(counting, checkpointPart));

        // Load 3 (still T0, within the TTL): now SUPPRESSED → the negative cache short-circuits the read; the
        // part is NOT opened again, so the open-count stays at exactly 2.
        Snapshot s3 = await new DeltaLog(counting, DeltaLog.MaxLogObjectBytes, checkpointDecodeBudget: budget)
            .LoadSnapshotAsync();
        Assert.Null(s3.Metrics.CheckpointVersion);
        Assert.Equal(2, CountOpensOf(counting, checkpointPart));

        // Load 4, with the TTL clock advanced 11 minutes (> the 10-min negative-cache TTL): the entry re-probes,
        // so the part is decoded ONCE MORE (opened a third time) — proving the cache is a TTL re-probe, not a
        // permanent blacklist. The decode budget still fires on REAL time (OffsetUtcTimeProvider delegates its
        // timer to the system clock), so this load still times out and falls back.
        var advancedClock = new OffsetUtcTimeProvider(TimeSpan.FromMinutes(11));
        Snapshot s4 = await new DeltaLog(
            counting, DeltaLog.MaxLogObjectBytes, checkpointDecodeBudget: budget, timeProvider: advancedClock,
            checkpointDecoder: IsolatedCheckpointDecoder())
            .LoadSnapshotAsync();
        Assert.Null(s4.Metrics.CheckpointVersion);
        Assert.Equal(3, CountOpensOf(counting, checkpointPart));
    }

    [Fact]
    public async Task SustainedPressureGuard_SameNonTerminatingIdentity_OnOneSharedDecoder_ReachesStrikeTwo_ThenSuppressed()
    {
        // Round-10 #3 (self-renewal wedge) — the DECISIVE behavioral catch for the re-scoped sustained-pressure
        // guard. The SAME non-terminating checkpoint identity is loaded TWICE through ONE SHARED checkpoint
        // decoder (so load 1's PERMANENT strand is still live, and VISIBLE to the guard, when load 2 runs). The
        // guard must still SEED on load 2 — a strand attributable to THIS identity's OWN prior load is exactly
        // the self-inflicted case that SHOULD seed — so the identity reaches STRIKE 2 and load 3 is SUPPRESSED
        // (the part is NOT opened a third time). Under the pre-Round-10 MONOTONE guard (`strandsBeforeDecode == 0`)
        // load 2 would see the load-1 strand (count 1) and REFUSE to seed → the identity NEVER reaches strike 2 →
        // load 3 re-decodes (opens == 3) → self-renewal → wedge. This test kills the MONOTONE mutation (reverting
        // the door-scoped delta to `strandsBeforeDecode == 0` makes it RED). It deliberately does NOT exercise the
        // door-scoped-vs-process-global mutation: the shared injected decoder's strands never touch the
        // process-global BoundedDecode.DetachedDecodeCount, so a process-global revert would read zero here and
        // stay green. That mutation (door-scoped read vs process-global sum) is killed by
        // SustainedPressureGuard_UnrelatedDoorStrand_DoesNotBlockSeeding, which strands the REAL static data-file
        // door so its strand inflates the process-global sum.
        LocalFileSystemBackend inner = NewBackend();
        await WriteHistoryAsync(inner);
        byte[] hanging = await CheckpointAtV1().ToParquetAsync();
        hanging[^9] ^= 1; // terminal Thrift STOP flip → non-terminating decode
        await DeltaTestHarness.WriteRawCheckpointAsync(inner, 1, hanging);
        await DeltaTestHarness.WriteLastCheckpointAsync(inner, 1);

        var counting = new CountingStorageBackend(inner);
        var budget = TimeSpan.FromMilliseconds(300);
        string checkpointPart = "_delta_log/" + 1L.ToString("D20", System.Globalization.CultureInfo.InvariantCulture)
            + ".checkpoint.parquet";

        // ONE shared decoder across loads 1 and 2 — load 1's strand stays live and visible to the guard on load 2.
        BoundedDecoder sharedDecoder = IsolatedCheckpointDecoder();

        Snapshot s1 = await new DeltaLog(counting, DeltaLog.MaxLogObjectBytes, checkpointDecodeBudget: budget,
            checkpointDecoder: sharedDecoder)
            .LoadSnapshotAsync();
        Assert.Null(s1.Metrics.CheckpointVersion);
        Assert.Equal(1, CountOpensOf(counting, checkpointPart));
        await WaitForDetachedAsync(sharedDecoder, 1); // load 1 stranded (permanent), visible on load 2

        Snapshot s2 = await new DeltaLog(counting, DeltaLog.MaxLogObjectBytes, checkpointDecodeBudget: budget,
            checkpointDecoder: sharedDecoder)
            .LoadSnapshotAsync();
        Assert.Null(s2.Metrics.CheckpointVersion);
        Assert.Equal(2, CountOpensOf(counting, checkpointPart)); // re-decoded (strike-gate), NOW strike 2

        // Load 3 (fresh decoder — the suppression is on the identity-keyed negative cache, not the decoder): the
        // identity is now SUPPRESSED, so the part is NOT opened again. Under the monotone/global revert the
        // identity never crossed strike 2 and this would open the part a THIRD time.
        Snapshot s3 = await new DeltaLog(counting, DeltaLog.MaxLogObjectBytes, checkpointDecodeBudget: budget,
            checkpointDecoder: IsolatedCheckpointDecoder())
            .LoadSnapshotAsync();
        Assert.Null(s3.Metrics.CheckpointVersion);
        Assert.Equal(2, CountOpensOf(counting, checkpointPart)); // SUPPRESSED — not re-opened
    }

    [Fact]
    public async Task SustainedPressureGuard_UnrelatedDoorStrand_DoesNotBlockSeeding()
    {
        // Round-10 #3a (door-scoping) — a permanent strand on an UNRELATED door must NOT block seeding on the
        // checkpoint door. The REAL static data-file door (BoundedDecode.DataFileDecoder — an UNRELATED door to
        // the injected checkpoint door under test) is stranded FOREVER, so its strand inflates the process-global
        // BoundedDecode.DetachedDecodeCount sum. The target identity is then loaded twice through its OWN clean
        // checkpoint decoder. Because the guard reads the DECODER ACTUALLY USED (_checkpointDecoder), not the
        // process-global sum, the unrelated data-file strand is invisible: seeding proceeds normally, the identity
        // reaches strike 2, and load 3 is SUPPRESSED. This is the DECISIVE catch for the door-scoped-vs-process-
        // global mutation: reverting the guard to its pre-Round-10 form (reading the process-global
        // BoundedDecode.DetachedDecodeCount) makes the data-file strand visible → it BLOCKS seeding process-wide →
        // the identity never reaches strike 2 → load 3 re-decodes (opens == 3) → RED. Stranding an ISOLATED
        // decoder (as an earlier revision did) would NOT touch the global sum, so a process-global revert would
        // stay green — under-guarded. The static door is drained in the finally so it never pollutes other tests.
        using var unrelatedGate = new ManualResetEventSlim(initialState: false);
        BoundedDecoder unrelatedDoor = BoundedDecode.DataFileDecoder;
        int detachedBefore = unrelatedDoor.DetachedDecodeCount;
        StrandForever(unrelatedDoor, unrelatedGate);
        await WaitForDetachedAsync(unrelatedDoor, detachedBefore + 1);

        try
        {
            LocalFileSystemBackend inner = NewBackend();
            await WriteHistoryAsync(inner);
            byte[] hanging = await CheckpointAtV1().ToParquetAsync();
            hanging[^9] ^= 1;
            await DeltaTestHarness.WriteRawCheckpointAsync(inner, 1, hanging);
            await DeltaTestHarness.WriteLastCheckpointAsync(inner, 1);

            var counting = new CountingStorageBackend(inner);
            var budget = TimeSpan.FromMilliseconds(300);
            string checkpointPart = "_delta_log/" + 1L.ToString("D20", System.Globalization.CultureInfo.InvariantCulture)
                + ".checkpoint.parquet";

            // Two loads through CLEAN checkpoint decoders (each unrelated to the stranded door) reach strike 2…
            Snapshot s1 = await new DeltaLog(counting, DeltaLog.MaxLogObjectBytes, checkpointDecodeBudget: budget,
                checkpointDecoder: IsolatedCheckpointDecoder()).LoadSnapshotAsync();
            Assert.Null(s1.Metrics.CheckpointVersion);
            Assert.Equal(1, CountOpensOf(counting, checkpointPart));

            Snapshot s2 = await new DeltaLog(counting, DeltaLog.MaxLogObjectBytes, checkpointDecodeBudget: budget,
                checkpointDecoder: IsolatedCheckpointDecoder()).LoadSnapshotAsync();
            Assert.Null(s2.Metrics.CheckpointVersion);
            Assert.Equal(2, CountOpensOf(counting, checkpointPart));

            // …and load 3 is suppressed (NOT re-opened) — the unrelated-door strand never blocked seeding.
            Snapshot s3 = await new DeltaLog(counting, DeltaLog.MaxLogObjectBytes, checkpointDecodeBudget: budget,
                checkpointDecoder: IsolatedCheckpointDecoder()).LoadSnapshotAsync();
            Assert.Null(s3.Metrics.CheckpointVersion);
            Assert.Equal(2, CountOpensOf(counting, checkpointPart));
        }
        finally
        {
            unrelatedGate.Set(); // release the unrelated strand so it drains off the shared static door
            // Drain wait (never pollute the shared static data-file door for other tests): poll until the door's
            // detached count returns to its pre-test value.
            await WaitForDrainAsync(unrelatedDoor, detachedBefore);
        }
    }

    // Strands ONE gated, never-terminating decode on the given decoder FOREVER (until the gate is set), charging
    // a real footprint. Used to place a permanent strand on an UNRELATED door for the door-scoping guard test.
    private static void StrandForever(BoundedDecoder decoder, ManualResetEventSlim gate)
    {
        _ = Task.Run(() => decoder.RunAsync<int>(
            token => { gate.Wait(); token.ThrowIfCancellationRequested(); return Task.FromResult(0); },
            TimeSpan.FromMilliseconds(150),
            static _ => DeltaStorageException.DecodeBudgetExceeded("stranded"),
            CancellationToken.None,
            estimatedRetainedBytes: BoundedDecode.MinStrandChargeBytes).GetAwaiter().GetResult());
    }

    private static async Task WaitForDetachedAsync(BoundedDecoder decoder, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (decoder.DetachedDecodeCount < expected)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"expected {expected} detached strand(s), saw {decoder.DetachedDecodeCount}.");
            }

            await Task.Delay(20);
        }
    }

    // Polls until the decoder's detached-strand count drains back to (or below) `target` — used to guarantee a
    // strand placed on a SHARED static door (BoundedDecode.DataFileDecoder) is drained before the test returns,
    // so it never pollutes that door for other tests in the suite.
    private static async Task WaitForDrainAsync(BoundedDecoder decoder, int target)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (decoder.DetachedDecodeCount > target)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"expected detached strand(s) to drain to {target}, still {decoder.DetachedDecodeCount}.");
            }

            await Task.Delay(20);
        }
    }

    private static int CountOpensOf(CountingStorageBackend backend, string suffix) =>
        backend.Opens.Count(p => p.EndsWith(suffix, StringComparison.Ordinal));

    // A TimeProvider whose WALL CLOCK (GetUtcNow — the only thing the negative cache consults) is shifted by a
    // fixed offset, but whose TIMER/timestamp primitives delegate to the system clock so a bounded decode's
    // Task.Delay(budget, this) still fires on REAL time. This decouples the negative-cache TTL (which we want to
    // advance deterministically) from the decode budget (which must keep timing out the crafted part).
    private sealed class OffsetUtcTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + offset;

        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }

    [Fact]
    public async Task DecoderSaturatedCheckpoint_EmitsSaturatedSignal_NotSeeded_DrivenByInjectedCapOneDecoder()
    {
        // High #7 — the injectable-decoder seam driving the REAL checkpoint read path through ALL capacity
        // branches with a cap=1 decoder and a releasable gated strand. A VALID (decodable) checkpoint is used,
        // so the ONLY reason the load falls back is the injected door saturation — proving the capacity path in
        // isolation from corruption/timeout. Asserts: (1) the DecoderSaturated fallback reason; (2) exactly one
        // decode.capacity_exhausted{door=checkpoint}; (3) decode.budget_exceeded EMPTY (a saturation is not a
        // timeout — the de-conflation contract); (4) the negative cache is NOT seeded (mechanically, via
        // open-count: a SUBSEQUENT load with a free decoder still DECODES and SEEDS the checkpoint — if the
        // saturation had poisoned the negative cache, the second load would skip it → CheckpointVersion null).
        LocalFileSystemBackend backend = NewBackend();
        await WriteHistoryAsync(backend);
        await DeltaTestHarness.WriteCheckpointAsync(backend, 1, CheckpointAtV1());
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1);

        // A cap=1 checkpoint decoder whose single slot is occupied by a gated strand (it times out and detaches,
        // holding the only slot), so the REAL checkpoint decode is rejected fail-fast WITHOUT starting.
        var saturatedDecoder = new BoundedDecoder(strandCountCap: 1, execution: DecodeExecution.DedicatedThread);
        using var strandGate = new ManualResetEventSlim(initialState: false);
        DeltaStorageException strandTimeout = await Assert.ThrowsAsync<DeltaStorageException>(() =>
            saturatedDecoder.RunAsync<int>(
                _ => { strandGate.Wait(); return Task.FromResult(0); },
                TimeSpan.FromMilliseconds(80),
                static _ => DeltaStorageException.DecodeBudgetExceeded("occupying strand"),
                CancellationToken.None));
        Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, strandTimeout.Kind);
        await WaitUntilAsync(() => saturatedDecoder.DetachedDecodeCount == 1);

        var logger = new RecordingLogger<DeltaLog>();
        using (var telemetry = new DeltaStorageTelemetry())
        using (var deltaMeter = new MeterCapture(telemetry.DeltaMeter))
        using (var storageMeter = new MeterCapture(telemetry.StorageMeter))
        {
            Snapshot snapshot = await new DeltaLog(
                backend, DeltaLog.MaxLogObjectBytes, logger, telemetry, checkpointDecoder: saturatedDecoder)
                .LoadSnapshotAsync();

            // Fell back to JSON replay (checkpoint not seeded).
            Assert.Null(snapshot.Metrics.CheckpointVersion);
            Assert.Equal(2, snapshot.Metrics.ReplayedCommitCount);

            // (1) The DecoderSaturated fallback reason (never decode_timeout / malformed).
            MeterCapture.Measurement fallback = Assert.Single(deltaMeter.ForInstrument(FallbackInstrument));
            Assert.Equal("decoder_saturated", fallback.Tags[ReasonKey]);

            // (2) exactly one decode.capacity_exhausted{door=checkpoint}.
            MeterCapture.Measurement capacity = Assert.Single(storageMeter.ForInstrument("deltasharp.storage.decode.capacity_exhausted"));
            Assert.Equal(1, capacity.Value);
            Assert.Equal("checkpoint", capacity.Tags[DecodeDoorKey]);

            // (3) decode.budget_exceeded EMPTY — a saturation is NOT a timeout (de-conflation, I8).
            Assert.Empty(storageMeter.ForInstrument(DecodeBudgetInstrument));
        }

        // Release the occupying strand and drain, then load AGAIN with a FRESH, unoccupied decoder.
        strandGate.Set();
        await WaitUntilAsync(() => saturatedDecoder.DetachedDecodeCount == 0);

        Snapshot healthy = await new DeltaLog(backend, DeltaLog.MaxLogObjectBytes)
            .LoadSnapshotAsync();

        // (4) NOT seeded on capacity: the valid checkpoint is decoded and SEEDS the snapshot this time
        // (CheckpointVersion == 1). Had the saturation poisoned the negative cache, this load would skip the
        // part and fall back to JSON replay (CheckpointVersion null) — so this assertion is red if a saturation
        // is ever wrongly cached.
        Assert.Equal(1, healthy.Metrics.CheckpointVersion);
    }

    [Fact]
    public async Task CheckpointStrandChargeOracle_ChargesTheLiveRetainedFootprint_NotFlatCeiling_OnProductionSizedDoor()
    {
        // Round-10 #1 end-to-end checkpoint charge oracle — strand a REAL checkpoint decode (a terminal-STOP
        // bit-flip drives a non-terminating decode that hangs at open) through a PRODUCTION-SIZED injected door
        // (CheckpointMaxFootprintBytes + a matching residual) and assert the strand books its LIVE retained
        // footprint. A part hanging at open has retained only its buffered COMPRESSED bytes (small) → the LIVE
        // charge floors to MinStrandChargeBytes (64 MiB). Pre-Round-10 the charge was a FLAT
        // `length + MaxCheckpointPartDecodedBytes` (~8 GiB) regardless of progress — so this oracle asserting the
        // 64 MiB live floor is RED under a revert to the flat ceiling (it would book ~8 GiB), and RED if the
        // charge collapses to the raw compressed length (a few KB, un-floored). Under the pre-Round-10 1-byte
        // test door this oracle clamped to 1 byte and was vacuous; production sizing makes it bite.
        LocalFileSystemBackend backend = NewBackend();
        await WriteHistoryAsync(backend);
        byte[] hanging = await CheckpointAtV1().ToParquetAsync();
        hanging[^9] ^= 1; // terminal Thrift STOP flip → non-terminating decode that hangs at open
        await DeltaTestHarness.WriteRawCheckpointAsync(backend, 1, hanging);
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1);

        BoundedDecoder decoder = IsolatedCheckpointDecoder(); // production-sized (CheckpointMaxFootprintBytes)
        var budget = TimeSpan.FromMilliseconds(300);

        Snapshot snapshot = await new DeltaLog(
            backend, DeltaLog.MaxLogObjectBytes, checkpointDecodeBudget: budget, checkpointDecoder: decoder)
            .LoadSnapshotAsync();
        Assert.Null(snapshot.Metrics.CheckpointVersion); // failed closed → JSON replay

        await WaitForDetachedAsync(decoder, 1);

        // The LIVE charge: retained = compressed length (small, hung at open) floored at MinStrandChargeBytes,
        // clamped to the door footprint → exactly MinStrandChargeBytes. NOT the flat ~8 GiB ceiling.
        Assert.Equal(BoundedDecode.MinStrandChargeBytes, decoder.StrandedDecodeBytes);
        Assert.True(
            decoder.StrandedDecodeBytes < BoundedDecode.CheckpointMaxFootprintBytes / 2,
            $"the live charge ({decoder.StrandedDecodeBytes}) must be far below the flat ceiling "
            + $"({BoundedDecode.CheckpointMaxFootprintBytes}) — a revert to the flat charge is red here.");
    }

    [Fact]
    public async Task DecodeDoorGauges_ArePublishedPerDoor_ViaMeterCapture()
    {
        // Round-10 #7/#10/#11 gauge-wiring oracle — the wedged, detached_cancelled, and door_under_provisioned
        // gauges are all PUBLISHED on the storage meter and sampled per door (data-file + checkpoint) via a
        // MeterCapture observing the observable instruments. In a healthy process the STATIC doors are neither
        // wedged nor cancelled, so both report 0 — the wiring pin (a dropped registration would leave the
        // instrument absent, RED here). The 0→1 transition of the underlying IsWedged predicate and the cancelled
        // sub-count are covered behaviorally by the BoundedDecode predicate/laundered-strand tests.
        using var telemetry = new DeltaStorageTelemetry();
        using var storageMeter = new MeterCapture(telemetry.StorageMeter);

        storageMeter.ObserveGauges();
        await Task.Yield();

        foreach (string gauge in new[]
        {
            "deltasharp.storage.decode.wedged",
            "deltasharp.storage.decode.detached_cancelled",
            "deltasharp.storage.decode.door_under_provisioned",
        })
        {
            MeterCapture.Measurement[] samples = storageMeter.ForInstrument(gauge).ToArray();
            Assert.Contains(samples, m => Equals(m.Tags[DecodeDoorKey], "data_file"));
            Assert.Contains(samples, m => Equals(m.Tags[DecodeDoorKey], "checkpoint"));
        }
    }

    [Fact]
    public async Task DecodeCeilingExceededCheckpoint_EmitsDecodeCeilingReason_JsonReplay_NoShortSnapshot_AndReleasesProbe()
    {
        // Round-10 #4 end-to-end — a VALID checkpoint whose cumulative decode crosses a (tiny, injected) per-part
        // ceiling must fail closed as a DISTINCT decode-ceiling fallback: JSON replay, the decode_ceiling_exceeded
        // reason (NOT malformed — a resource ceiling is not corruption), NO short snapshot (CheckpointVersion
        // null, full replay), and the single-flight probe RELEASED (not seeded) so a SUBSEQUENT load under the
        // production ceiling re-opens and SEEDS the part. RED if the reader throw reverts to Malformed (reason
        // would be `malformed`), if the ceiling breach seeds the negative cache (the second load would skip →
        // CheckpointVersion null), or if the probe is not released (the part would be un-re-openable).
        LocalFileSystemBackend backend = NewBackend();
        await WriteHistoryAsync(backend);
        await DeltaTestHarness.WriteCheckpointAsync(backend, 1, CheckpointAtV1());
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1);

        var logger = new RecordingLogger<DeltaLog>();
        using (var telemetry = new DeltaStorageTelemetry())
        using (var deltaMeter = new MeterCapture(telemetry.DeltaMeter))
        {
            Snapshot snapshot = await new DeltaLog(
                backend, DeltaLog.MaxLogObjectBytes, logger, telemetry,
                checkpointMaxPartDecodedBytes: 10) // tiny ceiling → the valid part crosses it
                .LoadSnapshotAsync();

            // JSON replay, NO short snapshot (checkpoint discarded, full replay).
            Assert.Null(snapshot.Metrics.CheckpointVersion);
            Assert.Equal(2, snapshot.Metrics.ReplayedCommitCount);

            // The DISTINCT decode_ceiling_exceeded reason — NOT malformed.
            MeterCapture.Measurement fallback = Assert.Single(deltaMeter.ForInstrument(FallbackInstrument));
            Assert.Equal("decode_ceiling_exceeded", fallback.Tags[ReasonKey]);
        }

        // The probe was RELEASED (not seeded): a SUBSEQUENT load under the PRODUCTION ceiling re-opens the part
        // and SEEDS the snapshot — proving the ceiling breach neither poisoned the negative cache nor stranded
        // the single-flight probe.
        Snapshot healthy = await new DeltaLog(backend, DeltaLog.MaxLogObjectBytes).LoadSnapshotAsync();
        Assert.Equal(1, healthy.Metrics.CheckpointVersion);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(20))
            {
                Assert.Fail("The expected bounded-decode state was not reached within the watchdog.");
            }

            await Task.Delay(10);
        }
    }

    private async Task<(Snapshot Snapshot, MeterCapture.Measurement Metric, RecordingLogger<DeltaLog>.Entry Log)>
        LoadWithCaptureAsync(IStorageBackend backend, string eventName = FallbackEvent)
    {
        var logger = new RecordingLogger<DeltaLog>();
        using var telemetry = new DeltaStorageTelemetry();
        using var meters = new MeterCapture(telemetry.DeltaMeter);

        Snapshot snapshot = await new DeltaLog(backend, DeltaLog.MaxLogObjectBytes, logger, telemetry)
            .LoadSnapshotAsync();

        MeterCapture.Measurement metric = Assert.Single(meters.ForInstrument(FallbackInstrument));
        RecordingLogger<DeltaLog>.Entry log = logger.Single(eventName);

        // Signal exclusivity (#763): a checkpoint discard emits EXACTLY ONE of the two log sites — the generic
        // fallback (4400) OR the forged-reject (4401), never both. This is the log-side half of the code's
        // "exactly-once" claim (the metric half is the Assert.Single on the meter above) and pins the
        // operator-facing distinction in BOTH directions: a forged reject must not also fire 4400, and a
        // routine bit-rot discard must not fire the 4401 security signal (which would page a false
        // identity-forgery incident on every corrupt/encrypted checkpoint).
        Assert.False(logger.Has(eventName == FallbackEvent ? ForgedEvent : FallbackEvent));
        // Cardinality invariant (design 09b / #772): the counter carries EXACTLY the bounded reason tag. The
        // discarded checkpoint version is correlation/exemplar-only and must NEVER become a metric label
        // (deltasharp.table.version is the doc's canonical "never a metric label" example). A mutation adding
        // an unbounded tag here must turn this red.
        Assert.Single(metric.Tags);
        Assert.True(metric.Tags.ContainsKey(ReasonKey));
        Assert.DoesNotContain(TableVersionKey, metric.Tags.Keys);

        // The log line carries the shared component/operation/backend correlation scope (design §7.2.1) so it
        // is routable by the same bounded dimensions the sibling storage components emit.
        IReadOnlyList<KeyValuePair<string, object?>> scope = Assert.Single(logger.Scopes);
        Assert.Equal("delta", ScopeValue(scope, ComponentKey));
        Assert.Equal("reconstruct", ScopeValue(scope, OperationKey));
        Assert.Equal("pvc", ScopeValue(scope, BackendKey));

        return (snapshot, metric, log);
    }

    private static object? ScopeValue(IReadOnlyList<KeyValuePair<string, object?>> scope, string key)
    {
        foreach (KeyValuePair<string, object?> kvp in scope)
        {
            if (string.Equals(kvp.Key, key, StringComparison.Ordinal))
            {
                return kvp.Value;
            }
        }

        return null;
    }
}
