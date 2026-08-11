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
