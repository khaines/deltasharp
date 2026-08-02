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
/// The audit line (AC3) names a discovered candidate <b>file</b> and the bounded <b>decision</b>/<b>reason</b>
/// for why it was kept or deleted, and is the load-bearing evidence an operator needs to audit a
/// data-loss-sensitive delete, so it is rendered here at <c>Debug</c>.
/// <para><b>The candidate is rendered as a DESCRIPTION, not a raw path.</b> DeltaSharp writes Hive-encoded
/// paths, so a candidate key such as <c>email=alice%40example.com/part-….parquet</c> embeds partition VALUES
/// — column values, i.e. table data and potentially PII — and the raw <c>add.path</c> is foreign, untrusted
/// text that can carry CRLF and forge a log line on a text sink. Unlike an exception <c>Message</c>, a
/// <see cref="LoggerMessage"/> emits unconditionally from inside DeltaSharp, so it bypasses any
/// caller-side hygiene entirely; the caller therefore passes
/// <c>DiagnosticText.DescribePath</c>, which keeps the sanitized file name plus the sanitized partition
/// COLUMN NAMES and drops every value. The exact raw path stays on the typed
/// <c>VacuumAuditEntry.Path</c> the caller receives, so a table owner can still act on it deliberately.</para>
/// It is <b>never</b> a metric tag (unbounded); the per-decision <i>counts</i> carry the bounded decision label
/// instead (§7.3). Credentials, row values, and statistics are never rendered (§7.2.2 redaction-by-omission).
/// </remarks>
internal static partial class DeltaVacuumLog
{
    [LoggerMessage(EventId = 4100, EventName = "DeltaVacuumStarted", Level = LogLevel.Information,
        Message = "Delta VACUUM started on backend {Backend}: retention {RetentionHours} h, dryRun={DryRun}, unsafeOverride={UnsafeOverride}.")]
    internal static partial void VacuumStarted(
        ILogger logger, string backend, double retentionHours, bool dryRun, bool unsafeOverride);

    [LoggerMessage(EventId = 4101, EventName = "DeltaVacuumRejectedRetention", Level = LogLevel.Warning,
        Message = "Delta VACUUM rejected (fail-closed): requested retention {RequestedHours} h is below the {ThresholdHours} h safety threshold and the unsafe override was not enabled.")]
    internal static partial void VacuumRejectedRetention(ILogger logger, double requestedHours, double thresholdHours);

    /// <remarks>
    /// <b>Structured-field BREAKING CHANGE.</b> This event previously carried a <c>{Path}</c> field holding
    /// the raw <c>add.path</c>; it now carries <c>{CandidateDescription}</c>, a partition-value-free
    /// rendering. The <see cref="EventId"/> and <see cref="EventId.Name"/> are deliberately UNCHANGED — the
    /// event still means "a VACUUM candidate was classified", so alerting that selects the event keeps
    /// working — but a dashboard or query projecting the <c>Path</c> field on event 4102 will now resolve
    /// null and must be repointed at <c>CandidateDescription</c>. The exact path remains available
    /// programmatically on <c>VacuumAuditEntry.Path</c> in the returned <c>VacuumResult</c>.
    /// <para><b>Audit-evidence impact (Privacy seat, #686):</b> a deletion-evidence / retention trail built by
    /// projecting the <c>Path</c> field of this event is ALSO affected, not only operational dashboards —
    /// event-derived erasure/retention evidence now resolves null and must repoint at <c>CandidateDescription</c>
    /// (which is partition-value-free) AND, if the exact object key is required as durable evidence, PERSIST
    /// <c>VacuumAuditEntry.Path</c> from the in-process <c>VacuumResult</c> (it is not itself durable).</para>
    /// <para>The caller MUST gate this call on <c>IsEnabled(LogLevel.Debug)</c>: the generated method checks
    /// the level internally, but C# evaluates the description argument at the call site regardless, and this
    /// is a per-candidate (not per-fault) site.</para>
    /// </remarks>
    [LoggerMessage(EventId = 4102, EventName = "DeltaVacuumCandidateDecision", Level = LogLevel.Debug,
        Message = "Delta VACUUM candidate {CandidateDescription}: {Decision} (deleted={Deleted}).")]
    internal static partial void VacuumCandidateDecision(
        ILogger logger, string candidateDescription, string decision, bool deleted);

    [LoggerMessage(EventId = 4103, EventName = "DeltaVacuumCompleted", Level = LogLevel.Information,
        Message = "Delta VACUUM completed on snapshot version {Version}: {CandidateCount} candidate(s) examined, {DeletableCount} deletion-eligible, {DeletedCount} deleted (dryRun={DryRun}) in {DurationMs} ms.")]
    internal static partial void VacuumCompleted(
        ILogger logger, long version, int candidateCount, int deletableCount, int deletedCount, bool dryRun, double durationMs);

    [LoggerMessage(EventId = 4104, EventName = "DeltaVacuumCanceled", Level = LogLevel.Information,
        Message = "Delta VACUUM canceled before completion; no terminal outcome was reached (not a failure).")]
    internal static partial void VacuumCanceled(ILogger logger);

    [LoggerMessage(EventId = 4105, EventName = "DeltaVacuumFailed", Level = LogLevel.Error,
        Message = "Delta VACUUM failed: {ExceptionType} (fail-closed; no retained or active file is deleted).")]
    internal static partial void VacuumFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 4106, EventName = "DeltaVacuumWeakSafetyThreshold", Level = LogLevel.Warning,
        Message = "Delta VACUUM retention policy has a weak safety threshold {ThresholdHours} h (below Delta's {DefaultHours} h default): the sub-threshold-retention guard is effectively disabled and a too-short retention can reclaim files a stale reader or recent tombstone still needs.")]
    internal static partial void VacuumWeakSafetyThreshold(ILogger logger, double thresholdHours, double defaultHours);
}
