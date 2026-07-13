using Microsoft.Extensions.Logging;

namespace DeltaSharp.Storage.Diagnostics;

/// <summary>
/// Source-generated, allocation-free structured log messages for the Delta merge-on-read DELETE
/// (STORY-05.5.1 / #192), mirroring <c>DeltaOptimizeLog</c>/<c>DeltaVacuumLog</c>. Event ids are in the
/// 4300 block (commit 4000, vacuum 4100, optimize 4200). No message ever carries row data, a predicate
/// value, or a file path (redaction-by-omission, checklist 09b).
/// </summary>
internal static partial class DeltaDeleteLog
{
    [LoggerMessage(EventId = 4300, EventName = "DeltaDeleteStarted", Level = LogLevel.Information,
        Message = "Delta DELETE started on backend {Backend}.")]
    internal static partial void DeleteStarted(ILogger logger, string backend);

    [LoggerMessage(EventId = 4301, EventName = "DeltaDeleteCompleted", Level = LogLevel.Information,
        Message = "Delta DELETE completed on read version {ReadVersion}, committed version {CommittedVersion}: "
            + "{RowsDeleted} row(s) logically deleted via deletion vectors across {FilesWithDeletionVector} "
            + "file(s) (no data file rewritten) in {DurationMs} ms.")]
    internal static partial void DeleteCompleted(
        ILogger logger, long readVersion, long committedVersion, long rowsDeleted, int filesWithDeletionVector, double durationMs);

    [LoggerMessage(EventId = 4302, EventName = "DeltaDeleteNoOp", Level = LogLevel.Information,
        Message = "Delta DELETE on read version {ReadVersion} matched no rows; no deletion vector was written "
            + "and no commit was made, in {DurationMs} ms.")]
    internal static partial void DeleteNoOp(ILogger logger, long readVersion, double durationMs);

    [LoggerMessage(EventId = 4303, EventName = "DeltaDeleteAborted", Level = LogLevel.Warning,
        Message = "Delta DELETE aborted (fail-closed): {ExceptionType}. A concurrent commit changed a file this "
            + "DELETE was removing rows from since the read snapshot; the table is unchanged and no delete was "
            + "lost (retry against the latest version).")]
    internal static partial void DeleteAborted(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 4304, EventName = "DeltaDeleteCanceled", Level = LogLevel.Information,
        Message = "Delta DELETE canceled before completion; no terminal outcome was reached (not a failure).")]
    internal static partial void DeleteCanceled(ILogger logger);

    [LoggerMessage(EventId = 4305, EventName = "DeltaDeleteFailed", Level = LogLevel.Error,
        Message = "Delta DELETE failed: {ExceptionType} (fail-closed; the table is unchanged and any written "
            + "deletion-vector file is an ignorable orphan).")]
    internal static partial void DeleteFailed(ILogger logger, string exceptionType);
}
