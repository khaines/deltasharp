using Microsoft.Extensions.Logging;

namespace DeltaSharp.Storage.Diagnostics;

/// <summary>
/// The <see cref="LoggerMessage"/> source-generated, allocation-free structured log sites for the Delta
/// OPTIMIZE / small-file compaction path (design §2.11.7, STORY-05.6.1 / #195; checklist 09a — never
/// <c>$"..."</c> on a hot path). It mirrors <see cref="DeltaVacuumLog"/>: every event carries a stable
/// <see cref="EventId"/> in the storage-owned <b>4200–4299</b> sub-range (the 4000–4999 storage band) with
/// a PascalCase <see cref="EventId.Name"/> for alert triage. Levels follow §7.2.3: <c>Information</c> for
/// lifecycle (started / completed / no-op / canceled), <c>Warning</c> for a fail-closed abort (a concurrent
/// change to a compaction input — a domain outcome, not a runtime error), and <c>Error</c> for an
/// unexpected failure. A cancellation is logged at <c>Information</c> (an expected control-flow outcome).
/// </summary>
/// <remarks>
/// Only bounded, non-sensitive fields are rendered: backend label, target size, file/byte counts, snapshot
/// versions, and outcome. Row values, column values, statistics, credentials, and file paths are
/// <b>never</b> logged (§7.2.2 redaction-by-omission) — a compaction rearranges data, so no row content is
/// ever a diagnostic field.
/// </remarks>
internal static partial class DeltaOptimizeLog
{
    [LoggerMessage(EventId = 4200, EventName = "DeltaOptimizeStarted", Level = LogLevel.Information,
        Message = "Delta OPTIMIZE started on backend {Backend}: targetFileSize {TargetBytes} B, dryRun={DryRun}.")]
    internal static partial void OptimizeStarted(ILogger logger, string backend, long targetBytes, bool dryRun);

    [LoggerMessage(EventId = 4201, EventName = "DeltaOptimizeCompleted", Level = LogLevel.Information,
        Message = "Delta OPTIMIZE completed on read version {ReadVersion}, committed version {CommittedVersion}: {FilesRemoved} file(s) compacted into {FilesAdded} (dryRun={DryRun}) in {DurationMs} ms.")]
    internal static partial void OptimizeCompleted(
        ILogger logger, long readVersion, long committedVersion, int filesRemoved, int filesAdded, bool dryRun, double durationMs);

    [LoggerMessage(EventId = 4202, EventName = "DeltaOptimizeNoOp", Level = LogLevel.Information,
        Message = "Delta OPTIMIZE on read version {ReadVersion} had nothing to compact (no partition held more than one small file); no commit was made, in {DurationMs} ms.")]
    internal static partial void OptimizeNoOp(ILogger logger, long readVersion, double durationMs);

    [LoggerMessage(EventId = 4203, EventName = "DeltaOptimizeAborted", Level = LogLevel.Warning,
        Message = "Delta OPTIMIZE aborted (fail-closed): {ExceptionType}. A compaction input changed underneath the read snapshot or a pre-commit failure fired; the inputs stay active and any written outputs are ignorable orphans (table unchanged).")]
    internal static partial void OptimizeAborted(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 4204, EventName = "DeltaOptimizeCanceled", Level = LogLevel.Information,
        Message = "Delta OPTIMIZE canceled before completion; no terminal outcome was reached (not a failure).")]
    internal static partial void OptimizeCanceled(ILogger logger);

    [LoggerMessage(EventId = 4205, EventName = "DeltaOptimizeFailed", Level = LogLevel.Error,
        Message = "Delta OPTIMIZE failed: {ExceptionType} (fail-closed; the table is unchanged and any written outputs are orphaned).")]
    internal static partial void OptimizeFailed(ILogger logger, string exceptionType);
}
