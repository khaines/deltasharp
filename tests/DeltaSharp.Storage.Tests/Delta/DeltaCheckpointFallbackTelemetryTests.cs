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
    private const string FallbackInstrument = "deltasharp.delta.checkpoint.fallback";
    private const string ReasonKey = "deltasharp.checkpoint.fallback.reason";
    private const string FallbackEvent = "DeltaCheckpointFallback";

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

        // Fast path taken (seeded from v1), so there is no discard and no fallback signal.
        Assert.Equal(1, snapshot.Metrics.CheckpointVersion);
        Assert.Empty(meters.ForInstrument(FallbackInstrument));
        Assert.False(logger.Has(FallbackEvent));
    }

    private async Task<(Snapshot Snapshot, MeterCapture.Measurement Metric, RecordingLogger<DeltaLog>.Entry Log)>
        LoadWithCaptureAsync(IStorageBackend backend)
    {
        var logger = new RecordingLogger<DeltaLog>();
        using var telemetry = new DeltaStorageTelemetry();
        using var meters = new MeterCapture(telemetry.DeltaMeter);

        Snapshot snapshot = await new DeltaLog(backend, DeltaLog.MaxLogObjectBytes, logger, telemetry)
            .LoadSnapshotAsync();

        MeterCapture.Measurement metric = Assert.Single(meters.ForInstrument(FallbackInstrument));
        RecordingLogger<DeltaLog>.Entry log = logger.Single(FallbackEvent);
        return (snapshot, metric, log);
    }
}
