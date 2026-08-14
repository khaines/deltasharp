using DeltaSharp.Storage.Delta;
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
    private static partial void VacuumCandidateDecisionCore(
        ILogger logger, string candidateDescription, string decision, bool deleted);

    /// <summary>
    /// #700: the candidate is accepted as a <see cref="PathDescription"/>, NOT a bare <c>string</c>, so this
    /// unconditionally-emitting sink CANNOT be handed an undescribed (Hive-encoded, partition-value-bearing)
    /// path — the "render via <c>DescribePath</c>" control is now compiler-enforced rather than a documented
    /// convention. The already-sanitized <see cref="PathDescription.Value"/> is forwarded to the
    /// source-generated <see cref="VacuumCandidateDecisionCore"/> so the structured <c>CandidateDescription</c>
    /// field stays a <c>string</c> and the emitted log line is byte-identical; the forward is a field read,
    /// so no allocation is added on this per-candidate path.
    /// </summary>
    internal static void VacuumCandidateDecision(
        ILogger logger, PathDescription candidate, string decision, bool deleted) =>
        VacuumCandidateDecisionCore(logger, candidate.Value, decision, deleted);

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

    /// <remarks>
    /// The Change-Data-Feed protection scan (#489) reads every retained, in-window commit JSON to protect the
    /// <c>_change_data/</c> files they reference; its cost grows with <c>delta.logRetentionDuration</c> depth.
    /// This <c>Information</c> lifecycle line reports the scan's volume and latency so an operator can correlate
    /// a slow VACUUM with a deep log-retention window (the same signals also ride the vacuum activity as
    /// bounded tags and the <c>deltasharp.delta.vacuum.cdc_scan.*</c> metrics, #641 item 2). Fields are bounded
    /// counts/durations plus a bounded <c>completed</c> flag — never a path or a cdc key. The
    /// <c>completed</c> flag is <see langword="false"/> when the scan THREW or was CANCELLED mid-read (the
    /// wall-clock was still spent, but the commit count is unknown and reported as 0), so log-only incident
    /// triage can tell a costly FAILED/cancelled scan apart from a benign zero-commit no-op.
    /// </remarks>
    [LoggerMessage(EventId = 4107, EventName = "DeltaVacuumCdcScanCompleted", Level = LogLevel.Information,
        Message = "Delta VACUUM change-data-feed protection scan read {CommitsScanned} in-window commit(s) in {DurationMs} ms (completed={Completed}).")]
    internal static partial void VacuumCdcScanCompleted(ILogger logger, int commitsScanned, double durationMs, bool completed);

    /// <remarks>
    /// The tail-truncated-log-listing fail-closed abort (#640 red-team): the table root listed a
    /// version-bearing log artifact beyond the version the snapshot resolved to, so the single <c>_delta_log</c>
    /// listing was stale/partial and reclaiming now could delete files referenced by the missing commit(s).
    /// This is a domain outcome (a fail-closed guard), not a runtime fault, so it is a <c>Warning</c> — distinct
    /// from the generic <c>Error</c> <see cref="VacuumFailed"/> — and the terminal is separately counted as
    /// <c>outcome=aborted_stale_listing</c>. The <paramref name="listedVersion"/>/<paramref name="resolvedVersion"/>
    /// fields are the two bounded version numbers (no path or untrusted token is ever rendered).
    /// <para><b>Retryability is CONDITIONAL, not unconditional.</b> If the listing is merely STALE (an
    /// object-store list-after-write lag), it self-heals and the run succeeds on retry. But a durably-orphaned
    /// version-bearing artifact — a forged log, or a foreign writer's stray checkpoint left above the
    /// resolvable version — will trip this guard on EVERY run and never resolve. Such PERSISTENT recurrence is
    /// NOT a transient: it indicates an inconsistent/forged log and an unbounded storage-cost condition
    /// (reclamation is blocked indefinitely), and warrants operator ESCALATION rather than blind retry. The
    /// message says so explicitly so this non-alertable Warning bucket cannot silently hide a permanent fault.
    /// (Attempt-bounding/auto-escalation is a deliberately un-implemented follow-up — the contract is only
    /// reworded truthfully here.)</para>
    /// </remarks>
    [LoggerMessage(EventId = 4108, EventName = "DeltaVacuumAbortedStaleListing", Level = LogLevel.Warning,
        Message = "Delta VACUUM aborted (fail-closed): the table root lists a _delta_log artifact at version {ListedVersion} but the _delta_log listing resolved only to version {ResolvedVersion} (stale/partial, tail-truncated); no file was deleted. Retry if the listing is merely stale; PERSISTENT recurrence indicates an inconsistent/forged log (a durably-orphaned version-bearing artifact) and warrants escalation, not blind retry.")]
    internal static partial void VacuumAbortedStaleListing(ILogger logger, long listedVersion, long resolvedVersion);
}
