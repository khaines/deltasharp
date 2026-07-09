using Microsoft.Extensions.Logging;

namespace DeltaSharp.Storage.Diagnostics;

/// <summary>
/// The <see cref="LoggerMessage"/> source-generated, allocation-free structured log sites for the Delta
/// commit path (design §7.2; checklist 09a — never <c>$"..."</c>/concatenation on a hot path). Every event
/// carries a stable <see cref="EventId"/> in the storage-owned <b>4000–4999</b> range with a PascalCase
/// <see cref="EventId.Name"/> for alert triage. Levels follow §7.2.3: <c>Information</c> for commit
/// lifecycle, <c>Debug</c> for expected retries, <c>Warning</c> for a recoverable OCC conflict (a Delta
/// conflict is a domain outcome, <b>not</b> an unhandled runtime error), and <c>Error</c> for a
/// contention/unknown-state/partial-transaction failure that needs remediation.
/// </summary>
/// <remarks>
/// Messages name only low-cardinality, non-sensitive values — the target/committed <b>version</b> (an
/// integer, safe), the <b>attempt</b> ordinal, the bounded <b>conflict class</b>/<b>retry reason</b>, and a
/// duration in milliseconds. A raw storage path, credential, row value, or statistic value is never
/// rendered (§7.2.2 redaction-by-omission). The logical <c>deltasharp.table</c> identity is not available
/// at the committer seam (it holds only a backend + snapshot), so it is omitted here and attached upstream
/// when a catalog-qualified name exists.
/// </remarks>
internal static partial class DeltaCommitLog
{
    [LoggerMessage(EventId = 4000, EventName = "DeltaCommitStarted", Level = LogLevel.Debug,
        Message = "Delta commit started: targeting version {TargetVersion} on backend {Backend}.")]
    internal static partial void CommitStarted(ILogger logger, long targetVersion, string backend);

    [LoggerMessage(EventId = 4001, EventName = "DeltaCommitCompleted", Level = LogLevel.Information,
        Message = "Delta commit completed: version {Version} published after {Attempts} attempt(s) in {DurationMs} ms.")]
    internal static partial void CommitCompleted(ILogger logger, long version, int attempts, double durationMs);

    [LoggerMessage(EventId = 4002, EventName = "DeltaCommitSkipped", Level = LogLevel.Information,
        Message = "Delta commit skipped: the batch's application transactions are already committed at version {Version} ({Reason}); no new version written.")]
    internal static partial void CommitSkipped(ILogger logger, long version, string reason);

    [LoggerMessage(EventId = 4003, EventName = "DeltaCommitConflict", Level = LogLevel.Warning,
        Message = "Delta commit aborted by a concurrent conflict on attempt {Attempt} at version {TargetVersion}: {ConflictClass}.")]
    internal static partial void CommitConflict(ILogger logger, int attempt, long targetVersion, string conflictClass);

    [LoggerMessage(EventId = 4004, EventName = "DeltaCommitRetry", Level = LogLevel.Debug,
        Message = "Delta commit retry on attempt {Attempt} at version {TargetVersion}: reason {Reason} (rebases so far: {RebaseCount}).")]
    internal static partial void CommitRetry(ILogger logger, int attempt, long targetVersion, string reason, int rebaseCount);

    [LoggerMessage(EventId = 4005, EventName = "DeltaCommitContentionExhausted", Level = LogLevel.Error,
        Message = "Delta commit did not converge within {MaxAttempts} attempts under sustained contention; version {Version} did not land (retryable).")]
    internal static partial void CommitContentionExhausted(ILogger logger, long version, int maxAttempts);

    [LoggerMessage(EventId = 4006, EventName = "DeltaCommitUnknownState", Level = LogLevel.Error,
        Message = "Delta commit at version {Version} reached an unresolved state; the outcome could not be proven committed-or-not (fail-closed).")]
    internal static partial void CommitUnknownState(ILogger logger, long version);

    [LoggerMessage(EventId = 4007, EventName = "DeltaCommitPartialTransaction", Level = LogLevel.Error,
        Message = "Delta commit failed closed: an atomic batch mixed {CommittedCount} already-committed and {UncommittedCount} uncommitted application transactions.")]
    internal static partial void CommitPartialTransaction(ILogger logger, int committedCount, int uncommittedCount);

    [LoggerMessage(EventId = 4008, EventName = "DeltaCommitTransientRetry", Level = LogLevel.Debug,
        Message = "Delta commit retrying a transient storage failure (transient retry {Retry}).")]
    internal static partial void CommitTransientRetry(ILogger logger, int retry);
}
