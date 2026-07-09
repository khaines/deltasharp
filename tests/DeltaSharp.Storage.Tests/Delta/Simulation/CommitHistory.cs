using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace DeltaSharp.Storage.Tests.Delta.Simulation;

/// <summary>
/// The <b>non-opaque action manifest</b> a writer declares for a commit attempt (design §3.3.5). It is
/// deliberately transparent so the Jepsen checker can re-run the §2.11.2 conflict rules mechanically rather
/// than trusting any engine-reported outcome. Crucially it records the <b>read-file-set</b> — from which the
/// checker <b>computes</b> <see cref="IsBlindAppend"/> (≡ empty read set) — and never an authoritative
/// blind-append flag: <see cref="SutReportedBlindAppend"/>, if present, is <b>cross-checked</b> against the
/// computed value, not trusted.
/// </summary>
internal sealed record ActionManifest(
    ImmutableArray<string> ReadFileSet,
    ImmutableArray<ManifestFile> Adds,
    ImmutableArray<ManifestFile> Removes,
    bool HasMetadataChange,
    bool HasProtocolChange,
    TxnKey? Txn,
    string ActionSetDigest,
    bool? SutReportedBlindAppend = null)
{
    /// <summary>Computed from the read-file-set (§3.3.5): a blind append read nothing, so it can only
    /// conflict with a concurrent metadata/protocol change — never with a concurrent append.</summary>
    public bool IsBlindAppend => ReadFileSet.IsEmpty;

    /// <summary>The partition scope of the write (here always whole-table for the unpartitioned model).</summary>
    public string PartitionScope => "whole-table";
}

/// <summary>A single <c>add</c>/<c>remove</c> in a manifest: its <see cref="Path"/> digest and
/// <see cref="DataChange"/> flag (design §3.3.5 per-file manifest fields).</summary>
internal readonly record struct ManifestFile(string Path, bool DataChange);

/// <summary>An application-transaction idempotency key <c>txn(appId, version)</c> (design §2.11.4).</summary>
internal readonly record struct TxnKey(string AppId, long Version)
{
    public override string ToString() => AppId + "@" + Version.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The observed outcome of a commit attempt, as classified by the simulation from the committer's
/// result or the exception it threw. The checker asserts these against the invariant catalogue and against
/// the class it <b>re-derives</b> from the manifest.</summary>
internal enum CommitOutcome
{
    /// <summary>A new version was published carrying this writer's actions.</summary>
    Committed,

    /// <summary>Idempotently skipped: the txn was already committed, so no new version/rows (I6).</summary>
    Skipped,

    /// <summary>Aborted with a classified data conflict (see <see cref="HistoryEvent.ConflictClass"/>).</summary>
    Conflict,

    /// <summary>Did not converge within the rebase budget under sustained contention (retryable).</summary>
    Contention,

    /// <summary>An ambiguous outcome could not be resolved — fail-closed unknown state.</summary>
    UnknownState,
}

/// <summary>
/// One record in the Jepsen-style history (design §3.3.5): the classification tuple for a single logical
/// operation. <see cref="InvokeTime"/>/<see cref="OkTime"/> are a monotonic <b>logical</b> clock (the
/// scheduler advances it) rather than a wall clock, so the history is byte-reproducible.
/// </summary>
internal sealed record HistoryEvent
{
    /// <summary>The logical writer (process) that performed the operation.</summary>
    public required int ProcessId { get; init; }

    /// <summary>The op type token, e.g. <c>read@0</c>, <c>commit target 1</c>, <c>retry(stream@5)</c>.</summary>
    public required string OpType { get; init; }

    /// <summary>The logical time the operation was invoked.</summary>
    public required long InvokeTime { get; init; }

    /// <summary>The logical time the operation returned (ok/fail).</summary>
    public required long OkTime { get; init; }

    /// <summary>The version this operation read its input snapshot at.</summary>
    public required long SnapshotReadVersion { get; init; }

    /// <summary>The active file set the read actually observed (for a read op) — the raw material for the
    /// snapshot-isolation (I4) check. Empty for a pure commit record.</summary>
    public ImmutableArray<string> ObservedReadFiles { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>The non-opaque action manifest (null for a pure read record).</summary>
    public ActionManifest? Manifest { get; init; }

    /// <summary>The observed commit outcome (null for a pure read record).</summary>
    public CommitOutcome? Outcome { get; init; }

    /// <summary>The version that became visible for a <see cref="CommitOutcome.Committed"/>/<see cref="CommitOutcome.Skipped"/>.</summary>
    public long? CommittedVersion { get; init; }

    /// <summary>The writer's <b>observed latest-at-abort</b> version <c>M</c> — the highest committed
    /// version durable when this operation resolved. For a conflicted/contended/unknown outcome it bounds
    /// the winners this writer actually raced to <c>(SnapshotReadVersion, M]</c> (design §2.11.2), so the
    /// conflict-class re-derivation never scans versions committed <i>after</i> the writer aborted. Null for
    /// a pure read record or when unavailable (the checker then falls back to the log's latest).</summary>
    public long? ObservedLatestVersion { get; init; }

    /// <summary>The number of put-if-absent attempts the commit took (1 ⇒ won first try). <c>-1</c> means the
    /// attempt count was not available (an aborted/contended/unknown-state outcome carries no honest count).</summary>
    public int Attempts { get; init; }

    /// <summary>The classified conflict type name for a <see cref="CommitOutcome.Conflict"/> outcome.</summary>
    public string? ConflictClass { get; init; }

    /// <summary>The expected post-state the model predicts after this operation (design §3.3.5) — the
    /// writer's own file becomes active on a successful commit, or the state is unchanged on a skip/conflict.</summary>
    public string ExpectedPostState { get; init; } = string.Empty;
}

/// <summary>
/// Records the ordered Jepsen-style history of a simulation run (design §3.3.5). One recorder is shared by
/// all writers; because the scheduler runs them on a single thread the append order and the logical clock
/// are deterministic. <see cref="Events"/> is the byte-reproducible history the
/// <see cref="JepsenHistoryChecker"/> validates.
/// </summary>
internal sealed class HistoryRecorder
{
    private readonly List<HistoryEvent> _events = new();
    private long _clock;

    /// <summary>The immutable, ordered history collected so far.</summary>
    public ImmutableArray<HistoryEvent> Events => _events.ToImmutableArray();

    /// <summary>Advances and returns the monotonic logical clock (an operation's invoke/ok bound).</summary>
    public long Tick() => ++_clock;

    /// <summary>Records a snapshot <b>read</b>: the version pinned and the active files observed (I4 input).</summary>
    public void RecordRead(int processId, long readVersion, IEnumerable<string> observedFiles, long invokeTime)
    {
        _events.Add(new HistoryEvent
        {
            ProcessId = processId,
            OpType = "read@" + readVersion.ToString(CultureInfo.InvariantCulture),
            InvokeTime = invokeTime,
            OkTime = Tick(),
            SnapshotReadVersion = readVersion,
            ObservedReadFiles = observedFiles.OrderBy(p => p, StringComparer.Ordinal).ToImmutableArray(),
            ExpectedPostState = "unchanged (read)",
        });
    }

    /// <summary>Records a <b>commit</b> attempt's declared manifest and observed outcome.</summary>
    public void RecordCommit(
        int processId,
        long readVersion,
        ActionManifest manifest,
        CommitOutcome outcome,
        long? committedVersion,
        int attempts,
        string? conflictClass,
        long invokeTime,
        string expectedPostState,
        long? observedLatestVersion = null)
    {
        string opType = manifest.Txn is { } txn
            ? "retry(" + txn + ")"
            : "commit target " + (readVersion + 1).ToString(CultureInfo.InvariantCulture);

        _events.Add(new HistoryEvent
        {
            ProcessId = processId,
            OpType = opType,
            InvokeTime = invokeTime,
            OkTime = Tick(),
            SnapshotReadVersion = readVersion,
            Manifest = manifest,
            Outcome = outcome,
            CommittedVersion = committedVersion,
            ObservedLatestVersion = observedLatestVersion,
            Attempts = attempts,
            ConflictClass = conflictClass,
            ExpectedPostState = expectedPostState,
        });
    }
}
