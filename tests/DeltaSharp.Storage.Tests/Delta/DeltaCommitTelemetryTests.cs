using System.Collections.Immutable;
using System.Diagnostics;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Diagnostics;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Commit-path observability tests for <see cref="DeltaCommitter"/> (#479, design §7): a representative
/// commit scenario is exercised with an isolated telemetry surface, and the emitted structured logs
/// (fields + <see cref="EventId"/>), metric measurements (instrument + bounded labels), and trace span
/// (name + attributes + status) are asserted. The observability is side-effect-free on commit semantics —
/// these tests only add assertions; the existing seam-driven behavior tests in
/// <see cref="DeltaCommitterTests"/>/<see cref="DeltaCommitAmbiguityTests"/> remain the semantics oracle.
/// </summary>
public sealed class DeltaCommitTelemetryTests : IDisposable
{
    // The shared DeltaSharpTelemetry keys (Abstractions-internal, not IVT-visible to this test assembly);
    // asserted by their stable literal string, which is the contract operators/exporters key on.
    private const string OutcomeKey = "deltasharp.outcome";
    private const string TableVersionKey = "deltasharp.table.version";
    private const string AttemptKey = "deltasharp.attempt";
    private const string ComponentKey = "deltasharp.component";
    private const string OperationKey = "deltasharp.operation";

    private const string DurationInstrument = "deltasharp.delta.commit.duration";
    private const string CountInstrument = "deltasharp.delta.commit.count";
    private const string AttemptsInstrument = "deltasharp.delta.commit.attempts";
    private const string ConflictsInstrument = "deltasharp.delta.commit.conflicts";

    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public DeltaCommitTelemetryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deltacommit-telemetry-" + Guid.NewGuid().ToString("N"));
        _backend = new LocalFileSystemBackend(_root);
    }

    public void Dispose()
    {
        _backend.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static readonly ImmutableSortedDictionary<string, string?> NoPartition =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    private static readonly ImmutableSortedDictionary<string, string> NoTags =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private static AddFileAction Add(string path) =>
        new(path, NoPartition, 1L, 1L, DataChange: true, Stats: null, Tags: NoTags);

    private static TxnAction Txn(string appId, long version) => new(appId, version, LastUpdated: null);

    private static Task NoBackoff(int attempt, CancellationToken cancellationToken) => Task.CompletedTask;

    private Task<Snapshot> LoadAsync(long? version = null) => new DeltaLog(_backend).LoadSnapshotAsync(version);

    private async Task<Snapshot> SeedAndLoadAsync() =>
        await Seeded(async () => await LoadAsync());

    private async Task<T> Seeded<T>(Func<Task<T>> after)
    {
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        return await after();
    }

    private DeltaCommitter Committer(
        IStorageBackend backend,
        RecordingLogger<DeltaCommitter> logger,
        DeltaStorageTelemetry telemetry,
        int maxAttempts = DeltaCommitter.DefaultMaxAttempts,
        Func<int, CancellationToken, Task>? rebaseJitter = null) =>
        new(backend, maxAttempts, nonceFactory: null, transientBackoff: NoBackoff, logger: logger, telemetry: telemetry, rebaseJitter: rebaseJitter);

    private static Activity SingleCommitActivity(ActivityCapture activities)
    {
        Activity activity = Assert.Single(activities.Stopped);
        Assert.Equal(DeltaStorageTelemetry.CommitActivityName, activity.OperationName);
        Assert.Equal("pvc", activity.GetTagItem(DeltaStorageTelemetry.BackendKey));
        Assert.Equal("delta", activity.GetTagItem(ComponentKey));
        Assert.Equal("commit", activity.GetTagItem(OperationKey));
        return activity;
    }

    [Fact]
    public async Task Success_EmitsCompletedLog_SuccessMetrics_AndOkSpan()
    {
        Snapshot snapshot = await SeedAndLoadAsync();
        using var telemetry = new DeltaStorageTelemetry();
        var logger = new RecordingLogger<DeltaCommitter>();
        using var meters = new MeterCapture(telemetry.DeltaMeter, telemetry.StorageMeter);
        using var activities = new ActivityCapture(telemetry.DeltaActivitySource);

        DeltaCommitResult result = await Committer(_backend, logger, telemetry)
            .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(1L, result.Version);

        // Logs: a start (Debug) and a completed (Information) event carrying the committed version + attempts.
        Assert.True(logger.Has("DeltaCommitStarted"));
        RecordingLogger<DeltaCommitter>.Entry completed = logger.Single("DeltaCommitCompleted");
        Assert.Equal(4001, completed.EventId.Id);
        Assert.Equal(1L, completed.Field("Version"));
        Assert.Equal(1, completed.Field("Attempts"));
        // Correlation scope carries the bounded component/operation keys.
        IReadOnlyList<KeyValuePair<string, object?>> scope = Assert.Single(logger.Scopes);
        Assert.Contains(scope, kvp => kvp.Key == ComponentKey && Equals(kvp.Value, "delta"));
        Assert.Contains(scope, kvp => kvp.Key == OperationKey && Equals(kvp.Value, "commit"));

        // Metrics: one duration + count + attempts measurement, all tagged outcome=success. No conflicts.
        MeterCapture.Measurement count = Assert.Single(meters.ForInstrument(CountInstrument));
        Assert.Equal(1d, count.Value);
        Assert.Equal("success", count.Tags[OutcomeKey]);
        Assert.Equal(1d, Assert.Single(meters.ForInstrument(AttemptsInstrument)).Value); // 1 attempt
        Assert.Equal("success", Assert.Single(meters.ForInstrument(DurationInstrument)).Tags[OutcomeKey]);
        Assert.Empty(meters.ForInstrument(ConflictsInstrument));

        // Span: name, backend, outcome, version, Ok status.
        Activity activity = SingleCommitActivity(activities);
        Assert.Equal("success", activity.GetTagItem(OutcomeKey));
        Assert.Equal(1L, activity.GetTagItem(TableVersionKey));
        Assert.Equal(1, activity.GetTagItem(AttemptKey));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);

        // Redaction: the data-file path is never rendered in any commit log message (§7.2.2).
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("part-0.parquet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConflictRebase_EmitsRetryLog_ConflictCounter_AndRebaseEvent()
    {
        Snapshot snapshot = await SeedAndLoadAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("winner.parquet")); // safe concurrent append
        using var telemetry = new DeltaStorageTelemetry();
        var logger = new RecordingLogger<DeltaCommitter>();
        using var meters = new MeterCapture(telemetry.DeltaMeter, telemetry.StorageMeter);
        using var activities = new ActivityCapture(telemetry.DeltaActivitySource);

        DeltaCommitResult result = await Committer(_backend, logger, telemetry)
            .CommitAsync(snapshot, new DeltaAction[] { Add("mine.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(2L, result.Version);
        Assert.Equal(2, result.Attempts);

        // Retry log: attempt 1, reason conflict_rebase, one rebase so far.
        RecordingLogger<DeltaCommitter>.Entry retry = logger.Single("DeltaCommitRetry");
        Assert.Equal("conflict_rebase", retry.Field("Reason"));
        Assert.Equal(1, retry.Field("RebaseCount"));
        Assert.Equal(1, retry.Field("Attempt"));

        // Conflict counter: one measurement classed concurrent_write (a rebased-past concurrent write).
        MeterCapture.Measurement conflict = Assert.Single(meters.ForInstrument(ConflictsInstrument));
        Assert.Equal("concurrent_write", conflict.Tags[DeltaStorageTelemetry.ConflictClassKey]);

        // Terminal is still a success at v2 on the 2nd attempt.
        MeterCapture.Measurement count = Assert.Single(meters.ForInstrument(CountInstrument));
        Assert.Equal("success", count.Tags[OutcomeKey]);
        Assert.Equal(2d, Assert.Single(meters.ForInstrument(AttemptsInstrument)).Value);

        Activity activity = SingleCommitActivity(activities);
        Assert.Equal("success", activity.GetTagItem(OutcomeKey));
        Assert.Contains(activity.Events, e => e.Name == "retry.conflict_rebase");
    }

    [Fact]
    public async Task AbortedConflict_EmitsWarningLog_ConflictClass_AndErrorSpan()
    {
        Snapshot snapshot = await SeedAndLoadAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Metadata(id: "changed")); // aborts a blind append
        using var telemetry = new DeltaStorageTelemetry();
        var logger = new RecordingLogger<DeltaCommitter>();
        using var meters = new MeterCapture(telemetry.DeltaMeter, telemetry.StorageMeter);
        using var activities = new ActivityCapture(telemetry.DeltaActivitySource);

        await Assert.ThrowsAsync<MetadataChangedException>(() =>
            Committer(_backend, logger, telemetry)
                .CommitAsync(snapshot, new DeltaAction[] { Add("late.parquet") }, DeltaReadScope.BlindAppend));

        RecordingLogger<DeltaCommitter>.Entry conflict = logger.Single("DeltaCommitConflict");
        Assert.Equal(4003, conflict.EventId.Id);
        Assert.Equal("metadata_changed", conflict.Field("ConflictClass"));

        Assert.Equal("metadata_changed", Assert.Single(meters.ForInstrument(ConflictsInstrument)).Tags[DeltaStorageTelemetry.ConflictClassKey]);
        Assert.Equal("conflict", Assert.Single(meters.ForInstrument(CountInstrument)).Tags[OutcomeKey]);

        Activity activity = SingleCommitActivity(activities);
        Assert.Equal("conflict", activity.GetTagItem(OutcomeKey));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Contains(activity.Events, e => e.Name == "conflict.detected");
    }

    [Fact]
    public async Task IdempotentSkip_EmitsSkippedLog_SkippedMetric_AndOkSpan()
    {
        Snapshot snapshot = await Seeded(async () =>
        {
            await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Txn("stream", 5), DeltaTestHarness.Add("batch5.parquet"));
            return await LoadAsync(); // v1, Transactions["stream"]=5
        });
        using var telemetry = new DeltaStorageTelemetry();
        var logger = new RecordingLogger<DeltaCommitter>();
        using var meters = new MeterCapture(telemetry.DeltaMeter, telemetry.StorageMeter);
        using var activities = new ActivityCapture(telemetry.DeltaActivitySource);

        DeltaCommitResult result = await Committer(_backend, logger, telemetry)
            .CommitAsync(snapshot, new DeltaAction[] { Txn("stream", 5), Add("retry.parquet") }, DeltaReadScope.BlindAppend);

        Assert.True(result.Skipped);

        RecordingLogger<DeltaCommitter>.Entry skip = logger.Single("DeltaCommitSkipped");
        Assert.Equal(4002, skip.EventId.Id);
        Assert.Equal(1L, skip.Field("Version"));
        Assert.False(logger.Has("DeltaCommitStarted")); // an up-front skip never begins a put loop

        MeterCapture.Measurement count = Assert.Single(meters.ForInstrument(CountInstrument));
        Assert.Equal("skipped", count.Tags[OutcomeKey]);
        Assert.Equal(0d, Assert.Single(meters.ForInstrument(AttemptsInstrument)).Value); // no put attempted

        Activity activity = SingleCommitActivity(activities);
        Assert.Equal("skipped", activity.GetTagItem(OutcomeKey));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
    }

    [Fact]
    public async Task PartialTransaction_EmitsErrorLog_PartialMetric_AndErrorSpan()
    {
        Snapshot snapshot = await Seeded(async () =>
        {
            await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Txn("a", 5), DeltaTestHarness.Add("a5.parquet"));
            return await LoadAsync(); // v1, Transactions["a"]=5, "b" absent
        });
        using var telemetry = new DeltaStorageTelemetry();
        var logger = new RecordingLogger<DeltaCommitter>();
        using var meters = new MeterCapture(telemetry.DeltaMeter, telemetry.StorageMeter);
        using var activities = new ActivityCapture(telemetry.DeltaActivitySource);

        // txn "a"@5 is already committed but "b"@5 is not — a partial atomic batch fails closed.
        await Assert.ThrowsAsync<PartialTransactionException>(() =>
            Committer(_backend, logger, telemetry).CommitAsync(
                snapshot,
                new DeltaAction[] { Txn("a", 5), Txn("b", 5), Add("mixed.parquet") },
                DeltaReadScope.BlindAppend));

        RecordingLogger<DeltaCommitter>.Entry partial = logger.Single("DeltaCommitPartialTransaction");
        Assert.Equal(4007, partial.EventId.Id);
        Assert.Equal(1, partial.Field("CommittedCount"));
        Assert.Equal(1, partial.Field("UncommittedCount"));

        Assert.Equal("partial_transaction", Assert.Single(meters.ForInstrument(CountInstrument)).Tags[OutcomeKey]);
        Activity activity = Assert.Single(activities.Stopped);
        Assert.Equal("partial_transaction", activity.GetTagItem(OutcomeKey));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }

    [Fact]
    public async Task AmbiguousSlotFree_EmitsRetryLog_AndSucceeds()
    {
        Snapshot snapshot = await SeedAndLoadAsync();
        var faulty = new FaultInjectingBackend(_backend) { AmbiguousOnPutCall = 0, PerformPutBeforeAmbiguous = false };
        using var telemetry = new DeltaStorageTelemetry();
        var logger = new RecordingLogger<DeltaCommitter>();
        using var meters = new MeterCapture(telemetry.DeltaMeter, telemetry.StorageMeter);
        using var activities = new ActivityCapture(telemetry.DeltaActivitySource);

        DeltaCommitResult result = await Committer(faulty, logger, telemetry)
            .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(2, result.Attempts); // ambiguous attempt + successful retry

        RecordingLogger<DeltaCommitter>.Entry retry = logger.Single("DeltaCommitRetry");
        Assert.Equal("ambiguous_slot_free", retry.Field("Reason"));

        Activity activity = SingleCommitActivity(activities);
        Assert.Equal("success", activity.GetTagItem(OutcomeKey));
        Assert.Contains(activity.Events, e => e.Name == "retry.ambiguous_slot_free");
    }

    [Fact]
    public async Task ContentionExhausted_EmitsErrorLog_ContentionMetric_AndErrorSpan()
    {
        Snapshot snapshot = await SeedAndLoadAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("winner.parquet"));
        using var telemetry = new DeltaStorageTelemetry();
        var logger = new RecordingLogger<DeltaCommitter>();
        using var meters = new MeterCapture(telemetry.DeltaMeter, telemetry.StorageMeter);
        using var activities = new ActivityCapture(telemetry.DeltaActivitySource);

        await Assert.ThrowsAsync<DeltaCommitContentionException>(() =>
            Committer(_backend, logger, telemetry, maxAttempts: 1)
                .CommitAsync(snapshot, new DeltaAction[] { Add("mine.parquet") }, DeltaReadScope.BlindAppend));

        RecordingLogger<DeltaCommitter>.Entry exhausted = logger.Single("DeltaCommitContentionExhausted");
        Assert.Equal(4005, exhausted.EventId.Id);
        Assert.Equal(1, exhausted.Field("MaxAttempts"));

        Assert.Equal("contention", Assert.Single(meters.ForInstrument(CountInstrument)).Tags[OutcomeKey]);
        Assert.Single(meters.ForInstrument(ConflictsInstrument)); // one safe rebase before exhaustion

        Activity activity = SingleCommitActivity(activities);
        Assert.Equal("contention", activity.GetTagItem(OutcomeKey));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }

    [Fact]
    public async Task UnknownState_EmitsErrorLog_UnknownStateMetric_AndErrorSpan()
    {
        Snapshot snapshot = await SeedAndLoadAsync();
        var faulty = new FaultInjectingBackend(_backend)
        {
            AmbiguousOnPutCall = 0,
            PerformPutBeforeAmbiguous = true,
            FailReGetHead = true,
        };
        using var telemetry = new DeltaStorageTelemetry();
        var logger = new RecordingLogger<DeltaCommitter>();
        using var meters = new MeterCapture(telemetry.DeltaMeter, telemetry.StorageMeter);
        using var activities = new ActivityCapture(telemetry.DeltaActivitySource);

        await Assert.ThrowsAsync<DeltaCommitUnknownStateException>(() =>
            Committer(faulty, logger, telemetry)
                .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend));

        RecordingLogger<DeltaCommitter>.Entry unknown = logger.Single("DeltaCommitUnknownState");
        Assert.Equal(4006, unknown.EventId.Id);
        Assert.Equal(1L, unknown.Field("Version"));

        Assert.Equal("unknown_state", Assert.Single(meters.ForInstrument(CountInstrument)).Tags[OutcomeKey]);
        Activity activity = SingleCommitActivity(activities);
        Assert.Equal("unknown_state", activity.GetTagItem(OutcomeKey));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }

    [Fact]
    public async Task RebaseJitter_WhenSupplied_IsInvokedOnRebase_WithoutChangingOutcome()
    {
        // AC3: the opt-in rebase jitter runs on a safe rebase, and the commit still lands at the rebased
        // version — proving jitter is side-effect-free on commit semantics.
        Snapshot snapshot = await SeedAndLoadAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("winner.parquet"));
        using var telemetry = new DeltaStorageTelemetry();
        var logger = new RecordingLogger<DeltaCommitter>();
        int jitterCalls = 0;
        Func<int, CancellationToken, Task> jitter = (attempt, ct) =>
        {
            jitterCalls++;
            return Task.CompletedTask;
        };

        DeltaCommitResult result = await Committer(_backend, logger, telemetry, rebaseJitter: jitter)
            .CommitAsync(snapshot, new DeltaAction[] { Add("mine.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(2L, result.Version);
        Assert.Equal(1, jitterCalls); // one rebase → one jitter await
    }

    [Fact]
    public async Task NoListeners_CommitIsAZeroCostNoOp()
    {
        // The production defaults (Shared telemetry + NullLogger) emit through a surface with no subscriber:
        // no Activity is created (StartActivity returns null → Activity.Current stays null) and the commit
        // succeeds normally. This is the exporter-agnostic, no-op-safe guarantee (design §7 / STORY-00.4.2).
        Snapshot snapshot = await SeedAndLoadAsync();

        DeltaCommitResult result = await new DeltaCommitter(_backend)
            .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(1L, result.Version);
        Assert.Null(Activity.Current); // no listener sampled the commit span
    }

    [Fact]
    public async Task TransientPutFailure_EmitsTransientRetryLog_ThenSucceeds()
    {
        // The bounded transient-retry seam (design §2.11.3) logs each transient retry at Debug (EventId 4008)
        // without changing the terminal outcome: the commit still lands at v1 on the same attempt.
        Snapshot snapshot = await SeedAndLoadAsync();
        var faulty = new FaultInjectingBackend(_backend) { TransientPutCalls = 2 };
        using var telemetry = new DeltaStorageTelemetry();
        var logger = new RecordingLogger<DeltaCommitter>();

        DeltaCommitResult result = await Committer(faulty, logger, telemetry)
            .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(1L, result.Version);
        Assert.Equal(1, result.Attempts); // transient retries are within one put-if-absent attempt

        RecordingLogger<DeltaCommitter>.Entry[] transient =
            logger.Entries.Where(e => e.EventId.Name == "DeltaCommitTransientRetry").ToArray();
        Assert.Equal(2, transient.Length);
        Assert.All(transient, e => Assert.Equal(4008, e.EventId.Id));
    }

    [Fact]
    public async Task WholeTableOverwrite_ConcurrentAppend_ClassifiesConcurrentAppend()
    {
        // A whole-table overwrite conflicts with a concurrent append (design §2.11): the conflict counter is
        // classed concurrent_append and the Warning log names that class — a distinct conflict.class arm.
        Snapshot snapshot = await SeedAndLoadAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("winner.parquet")); // concurrent append
        using var telemetry = new DeltaStorageTelemetry();
        var logger = new RecordingLogger<DeltaCommitter>();
        using var meters = new MeterCapture(telemetry.DeltaMeter, telemetry.StorageMeter);

        await Assert.ThrowsAsync<ConcurrentAppendException>(() =>
            Committer(_backend, logger, telemetry).CommitAsync(
                snapshot, new DeltaAction[] { Add("overwrite.parquet") }, DeltaReadScope.WholeTable));

        Assert.Equal("concurrent_append", Assert.Single(meters.ForInstrument(ConflictsInstrument)).Tags[DeltaStorageTelemetry.ConflictClassKey]);
        Assert.Equal("concurrent_append", logger.Single("DeltaCommitConflict").Field("ConflictClass"));
    }

    [Fact]
    public async Task DefaultRebaseJitter_IsBounded_AndCompletes()
    {
        // AC3: the built-in deterministic-friendly jitter seam is bounded (full-jitter, crypto RNG, never the
        // banned System.Random) and always completes — a host can opt in without unbounded delay.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            await DeltaCommitter.DefaultRebaseJitterAsync(attempt, CancellationToken.None);
        }
    }
}
