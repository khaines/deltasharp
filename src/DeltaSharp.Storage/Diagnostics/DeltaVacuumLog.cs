using Microsoft.Extensions.Logging;

namespace DeltaSharp.Storage.Diagnostics;

/// <summary>
/// The <see cref="LoggerMessage"/> source-generated, allocation-free structured log sites for the Delta
/// VACUUM / retention-safety path (design §2.14, STORY-05.6.2; checklist 09a — never <c>$"..."</c> on a hot
/// path). It mirrors <see cref="DeltaCommitLog"/>: every event carries a stable <see cref="EventId"/> in the
/// storage-owned <b>4100–4199</b> sub-range (the 4000–4999 storage band) with a PascalCase
/// <see cref="EventId.Name"/> for alert triage. Levels follow §7.2.3: <c>Information</c> for lifecycle
/// (started / completed), <c>Debug</c> for the per-candidate audit line (an expected, potentially numerous
/// decision record), <c>Warning</c> for a rejected sub-threshold retention (a fail-closed guard, a domain
/// outcome — not a runtime error), and <c>Error</c> for an unexpected failure. A cancellation is logged at
/// <c>Information</c> (an expected control-flow outcome, not a failure).
/// </summary>
/// <remarks>
/// The audit line (AC3) names a discovered candidate <b>path</b> and the bounded <b>decision</b>/<b>reason</b>
/// for why it was kept or deleted. A candidate path under a table directory is a maintenance-time reclamation
/// target — a relative object key, not a credential-bearing endpoint — and is the load-bearing evidence an
/// operator needs to audit a data-loss-sensitive delete, so it is rendered here at <c>Debug</c>. It is
/// <b>never</b> a metric tag (unbounded); the per-decision <i>counts</i> carry the bounded decision label
/// instead (§7.3). Credentials, row values, and statistics are never rendered (§7.2.2 redaction-by-omission).
/// </remarks>
internal static partial class DeltaVacuumLog
{
    [LoggerMessage(EventId = 4100, EventName = "DeltaVacuumStarted", Level = LogLevel.Information,
        Message = "Delta VACUUM started on backend {Backend}: snapshot version {Version}, retention {RetentionHours} h, dryRun={DryRun}, unsafeOverride={UnsafeOverride}.")]
    internal static partial void VacuumStarted(
        ILogger logger, string backend, long version, double retentionHours, bool dryRun, bool unsafeOverride);

    [LoggerMessage(EventId = 4101, EventName = "DeltaVacuumRejectedRetention", Level = LogLevel.Warning,
        Message = "Delta VACUUM rejected (fail-closed): requested retention {RequestedHours} h is below the {ThresholdHours} h safety threshold and the unsafe override was not enabled.")]
    internal static partial void VacuumRejectedRetention(ILogger logger, double requestedHours, double thresholdHours);

    [LoggerMessage(EventId = 4102, EventName = "DeltaVacuumCandidateDecision", Level = LogLevel.Debug,
        Message = "Delta VACUUM candidate {Path}: {Decision} (deleted={Deleted}).")]
    internal static partial void VacuumCandidateDecision(ILogger logger, string path, string decision, bool deleted);

    [LoggerMessage(EventId = 4103, EventName = "DeltaVacuumCompleted", Level = LogLevel.Information,
        Message = "Delta VACUUM completed: {CandidateCount} candidate(s) examined, {DeletableCount} deletion-eligible, {DeletedCount} deleted (dryRun={DryRun}) in {DurationMs} ms.")]
    internal static partial void VacuumCompleted(
        ILogger logger, int candidateCount, int deletableCount, int deletedCount, bool dryRun, double durationMs);

    [LoggerMessage(EventId = 4104, EventName = "DeltaVacuumCanceled", Level = LogLevel.Information,
        Message = "Delta VACUUM canceled before completion; no terminal outcome was reached (not a failure).")]
    internal static partial void VacuumCanceled(ILogger logger);

    [LoggerMessage(EventId = 4105, EventName = "DeltaVacuumFailed", Level = LogLevel.Error,
        Message = "Delta VACUUM failed: {ExceptionType} (fail-closed; no retained or active file is deleted).")]
    internal static partial void VacuumFailed(ILogger logger, string exceptionType);
}
