using System.Diagnostics;
using System.Diagnostics.Metrics;
using DeltaSharp.Diagnostics;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;

namespace DeltaSharp.Storage.Diagnostics;

/// <summary>
/// The bounded terminal outcome of a Delta commit — the closed value set behind the shared
/// <see cref="DeltaSharpTelemetry.OutcomeKey"/> label so success, an idempotent skip, an OCC conflict,
/// contention exhaustion, an unresolved outcome, and a failure are never collapsed into one ambiguous
/// count (design §7.1/§7.3, checklist 09b). Each value maps to a low-cardinality string via
/// <see cref="DeltaStorageTelemetry"/>.
/// </summary>
internal enum CommitOutcome
{
    /// <summary>A new version was durably published (won the race, or an ambiguous/own-commit landed).</summary>
    Success,

    /// <summary>The batch was already committed (idempotent <c>txn</c> skip) — no new version written.</summary>
    Skipped,

    /// <summary>A concurrent commit logically conflicted; this writer aborted
    /// (<see cref="DeltaConcurrentModificationException"/>).</summary>
    Conflict,

    /// <summary>The rebase-retry budget was exhausted under sustained contention
    /// (<see cref="DeltaCommitContentionException"/>) — provably did not land, retryable.</summary>
    Contention,

    /// <summary>The outcome could not be resolved to committed-or-not
    /// (<see cref="DeltaCommitUnknownStateException"/>) — fail-closed, non-retryable.</summary>
    UnknownState,

    /// <summary>An atomic batch mixed committed and uncommitted application transactions
    /// (<see cref="PartialTransactionException"/>) — fail-closed caller misuse.</summary>
    PartialTransaction,

    /// <summary>An unexpected/unclassified failure (for example an unsupported writer protocol).</summary>
    Failure,

    /// <summary>The commit was cancelled via its <see cref="System.Threading.CancellationToken"/> before a
    /// terminal outcome was reached. A cancellation is <b>not</b> a commit failure (it does not inflate the
    /// failure SLI and does not mark the span <c>Error</c>).</summary>
    Cancelled,
}

/// <summary>
/// The reason a commit attempt was retried rather than terminating — the low-cardinality <c>reason</c> a
/// per-retry log line carries (design §7.2, checklist 09a: "retry logs include attempt number + retry
/// reason"). Not a metric label (retries are correlation/exemplar-only signals).
/// </summary>
internal enum CommitRetryReason
{
    /// <summary>A definite OCC conflict that was safely rebased onto the new latest version and retried.</summary>
    ConflictRebase,

    /// <summary>An ambiguous put-if-absent acknowledgment resolved to an unclaimed slot — the same version
    /// is retried.</summary>
    AmbiguousSlotFree,

    /// <summary>A transient storage failure was retried with bounded backoff (design §2.11.3).</summary>
    Transient,
}

/// <summary>
/// The commit-path telemetry surface for <c>DeltaSharp.Storage</c> — the two <see cref="Meter"/>s and two
/// <see cref="ActivitySource"/>s the storage layer owns (named <c>DeltaSharp.Storage</c> and
/// <c>DeltaSharp.Delta</c> per design §7.3/§7.4), plus the Delta <b>commit</b> instruments, built on the
/// shared <see cref="DeltaSharpTelemetry"/> vocabulary. It is <b>exporter-agnostic and a safe no-op until a
/// collector subscribes</b>: a <c>Counter.Add</c>/<c>Histogram.Record</c> with no <see cref="MeterListener"/>
/// and a <c>StartActivity</c> with no <see cref="ActivityListener"/> perform no work, mirroring the ambient
/// <c>ExecutionAudit</c> seam. Instruments are created <b>once</b> per instance and reused (checklist 09b);
/// durations are measured with the monotonic <see cref="Stopwatch"/>, never the wall clock (banned).
/// </summary>
/// <remarks>
/// <para>The process-wide production instance is <see cref="Shared"/>. Tests inject a private instance whose
/// distinct <see cref="Meter"/>/<see cref="ActivitySource"/> objects a scoped <see cref="MeterListener"/>/
/// <see cref="ActivityListener"/> can subscribe to in isolation, so parallel test classes never cross-
/// contaminate one another's measurements. The type is AOT/trim-clean: <see cref="Meter"/>,
/// <see cref="ActivitySource"/>, and the instrument types are compile-time and reflection-free.</para>
/// <para>#479 instruments only the commit path, so only the <c>DeltaSharp.Delta</c> meter/source carry
/// instruments today; the <c>DeltaSharp.Storage</c> meter/source are created so the surface is complete and
/// adapter-I/O instruments (design §7.3) can attach without re-establishing the naming.</para>
/// </remarks>
internal sealed class DeltaStorageTelemetry : IDisposable
{
    /// <summary>The <c>DeltaSharp.Delta</c> meter/source name (log/commit/snapshot/maintenance).</summary>
    internal const string DeltaName = DeltaSharpTelemetry.RootName + ".Delta";

    /// <summary>The <c>DeltaSharp.Storage</c> meter/source name (adapter + Parquet I/O).</summary>
    internal const string StorageName = DeltaSharpTelemetry.RootName + ".Storage";

    /// <summary>The bounded <c>deltasharp.backend</c> attribute key — re-exported from the shared vocabulary
    /// (<see cref="DeltaSharpTelemetry.BackendKey"/>) so the storage layer and its tests bind one source of
    /// truth (design §7.3 minted label).</summary>
    internal const string BackendKey = DeltaSharpTelemetry.BackendKey;

    /// <summary>The bounded <c>deltasharp.conflict.class</c> label key sub-classifying an
    /// <c>outcome=conflict</c> — re-exported from the shared vocabulary
    /// (<see cref="DeltaSharpTelemetry.ConflictClassKey"/>) so the registry stays the single source of truth
    /// (design §7.3 minted label).</summary>
    internal const string ConflictClassKey = DeltaSharpTelemetry.ConflictClassKey;

    /// <summary>The stable commit span name (design §7.4): variables live in bounded attributes.</summary>
    internal const string CommitActivityName = DeltaName + ".Commit";

    /// <summary>The bounded <c>deltasharp.operation=commit</c> value for the commit path.</summary>
    internal const string CommitOperation = "commit";

    /// <summary>The bounded <c>deltasharp.component=delta</c> value for the Delta log subsystem.</summary>
    internal const string DeltaComponent = "delta";

    /// <summary>The bounded <c>conflict.class</c> for a concurrent write this writer safely rebased past
    /// (no abort). Part of the design's closed <c>conflict.class</c> value set.</summary>
    internal const string ConcurrentWriteClass = "concurrent_write";

    private static readonly string AssemblyVersion =
        typeof(DeltaStorageTelemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>The process-wide production surface (no-op until an exporter subscribes).</summary>
    internal static DeltaStorageTelemetry Shared { get; } = new();

    private readonly Meter _deltaMeter;
    private readonly Meter _storageMeter;
    private readonly Histogram<double> _commitDuration;
    private readonly Counter<long> _commitCount;
    private readonly Histogram<int> _commitAttempts;
    private readonly Counter<long> _commitConflicts;
    private readonly Counter<long> _commitTransientRetries;

    internal DeltaStorageTelemetry()
    {
        _deltaMeter = new Meter(DeltaName, AssemblyVersion);
        _storageMeter = new Meter(StorageName, AssemblyVersion);
        DeltaActivitySource = new ActivitySource(DeltaName, AssemblyVersion);
        StorageActivitySource = new ActivitySource(StorageName, AssemblyVersion);

        _commitDuration = _deltaMeter.CreateHistogram<double>(
            "deltasharp.delta.commit.duration", unit: "s",
            description: "Elapsed (monotonic) duration of a Delta commit, by terminal outcome.");
        _commitCount = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.commit.count", unit: "{commit}",
            description: "Terminal Delta commit outcomes (committed-version and failure-classification count).");
        _commitAttempts = _deltaMeter.CreateHistogram<int>(
            "deltasharp.delta.commit.attempts", unit: "{attempt}",
            description: "Optimistic-concurrency put-if-absent attempts per commit (retry depth), by outcome.");
        _commitConflicts = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.commit.conflicts", unit: "{conflict}",
            description: "Detected commit conflicts (aborted or safely rebased), by conflict class.");
        _commitTransientRetries = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.commit.transient_retries", unit: "{retry}",
            description: "Transient storage-failure retries within a commit's put-if-absent attempts (degradation signal).");
    }

    /// <summary>The <c>DeltaSharp.Delta</c> meter (commit instruments). Exposed for reference-identity
    /// filtering by a per-instance <see cref="MeterListener"/> in tests, so parallel test classes that each
    /// build their own surface never observe one another's measurements despite the shared meter name.</summary>
    internal Meter DeltaMeter => _deltaMeter;

    /// <summary>The <c>DeltaSharp.Storage</c> meter (adapter/Parquet I/O instruments; none on the commit
    /// path today).</summary>
    internal Meter StorageMeter => _storageMeter;

    /// <summary>The <c>DeltaSharp.Delta</c> trace source (log/commit/snapshot spans).</summary>
    internal ActivitySource DeltaActivitySource { get; }

    /// <summary>The <c>DeltaSharp.Storage</c> trace source (adapter/Parquet I/O spans; unused on the commit
    /// path today, created so the surface is complete).</summary>
    internal ActivitySource StorageActivitySource { get; }

    /// <summary>Starts the commit span if a listener samples the Delta source; returns <see langword="null"/>
    /// (a cheap no-op) otherwise. The caller sets low-cardinality attributes and status.</summary>
    internal Activity? StartCommitActivity(StorageBackendKind backend)
    {
        Activity? activity = DeltaActivitySource.StartActivity(CommitActivityName, ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(DeltaSharpTelemetry.ComponentKey, DeltaComponent);
            activity.SetTag(DeltaSharpTelemetry.OperationKey, CommitOperation);
            activity.SetTag(BackendKey, backend.ToLabel());
        }

        return activity;
    }

    /// <summary>Records the terminal signals for one commit: the duration histogram, the outcome counter,
    /// and the attempt-depth histogram — all tagged with the bounded <see cref="DeltaSharpTelemetry.OutcomeKey"/>.
    /// A single measurement per commit (never per row/attempt), so it is allocation-light and export-safe.</summary>
    internal void RecordCommitTerminal(CommitOutcome outcome, double durationSeconds, int attempts)
    {
        var outcomeTag = new KeyValuePair<string, object?>(DeltaSharpTelemetry.OutcomeKey, ToLabel(outcome));
        _commitDuration.Record(durationSeconds, outcomeTag);
        _commitCount.Add(1, outcomeTag);
        _commitAttempts.Record(attempts, outcomeTag);
    }

    /// <summary>Increments the conflict counter for one detected conflict, tagged with the bounded
    /// <see cref="ConflictClassKey"/>. The input is a bounded <see cref="DeltaConflictKind"/> (never a raw
    /// string, so no unbounded/free-text value can reach the metric tag): an aborted conflict passes the
    /// winner-driven <c>ex.Kind</c>; a safely rebased conflict passes <see langword="null"/>, which maps to
    /// <see cref="ConcurrentWriteClass"/>.</summary>
    internal void RecordConflict(DeltaConflictKind? kind)
    {
        string conflictClass = kind is { } value ? ToConflictClass(value) : ConcurrentWriteClass;
        _commitConflicts.Add(1, new KeyValuePair<string, object?>(ConflictClassKey, conflictClass));
    }

    /// <summary>Increments the transient-retry counter for one bounded transient storage-failure retry
    /// within a commit's put-if-absent attempts (design §2.11.3). A clean commit records zero; a
    /// transient-degraded-but-successful commit records &gt;0, so an SRE can distinguish the two even though
    /// both terminate as <c>outcome=success</c> with <c>attempts=1</c>.</summary>
    internal void RecordTransientRetry() => _commitTransientRetries.Add(1);

    /// <summary>The bounded <c>deltasharp.outcome</c> string for a <see cref="CommitOutcome"/>.</summary>
    internal static string ToLabel(CommitOutcome outcome) => outcome switch
    {
        CommitOutcome.Success => "success",
        CommitOutcome.Skipped => "skipped",
        CommitOutcome.Conflict => "conflict",
        CommitOutcome.Contention => "contention",
        CommitOutcome.UnknownState => "unknown_state",
        CommitOutcome.PartialTransaction => "partial_transaction",
        CommitOutcome.Cancelled => "cancelled",
        _ => "failure",
    };

    /// <summary>The bounded <c>deltasharp.conflict.class</c> string for a <see cref="DeltaConflictKind"/>
    /// (design §7.3 value set). A safe rebase does not throw and instead records
    /// <see cref="ConcurrentWriteClass"/>.</summary>
    internal static string ToConflictClass(DeltaConflictKind kind) => kind switch
    {
        DeltaConflictKind.ConcurrentAppend => "concurrent_append",
        DeltaConflictKind.ConcurrentDeleteRead => "concurrent_delete_read",
        DeltaConflictKind.MetadataChanged => "metadata_changed",
        DeltaConflictKind.ProtocolChanged => "protocol_changed",
        DeltaConflictKind.ConcurrentTransaction => "concurrent_transaction",
        _ => ConcurrentWriteClass,
    };

    /// <summary>The bounded retry <c>reason</c> string for a per-retry log line.</summary>
    internal static string ToLabel(CommitRetryReason reason) => reason switch
    {
        CommitRetryReason.ConflictRebase => "conflict_rebase",
        CommitRetryReason.AmbiguousSlotFree => "ambiguous_slot_free",
        _ => "transient",
    };

    public void Dispose()
    {
        _deltaMeter.Dispose();
        _storageMeter.Dispose();
        DeltaActivitySource.Dispose();
        StorageActivitySource.Dispose();
    }
}
