using Microsoft.Extensions.Logging;

namespace DeltaSharp.Storage.Diagnostics;

/// <summary>
/// The <see cref="LoggerMessage"/> source-generated, allocation-free structured log sites for the Delta
/// snapshot-reconstruction / checkpoint path (design §7.2; checklist 09a — never <c>$"..."</c>/concatenation
/// on a read path). Events carry a stable <see cref="EventId"/> in the storage-owned <b>4400–4499</b>
/// reconstruction sub-range (commit uses 4000–4099) with a PascalCase <see cref="EventId.Name"/> for alert
/// triage.
/// </summary>
/// <remarks>
/// Messages name only low-cardinality, non-sensitive values — the discarded checkpoint <b>version</b> (an
/// integer, safe) and the bounded <b>reason</b> (<c>unsupported_feature</c>/<c>malformed</c>). A raw storage
/// path, the checkpoint's contents, or any table/credential value is never rendered (§7.2.2
/// redaction-by-omission). A discarded checkpoint is logged at <c>Warning</c>: the read still succeeds via
/// JSON replay (or an older checkpoint), but an unreadable checkpoint — e.g. an encrypted one (#681/#698) —
/// is an actionable operator signal that is otherwise invisible until the log ages out (#772).
/// </remarks>
internal static partial class DeltaCheckpointLog
{
    [LoggerMessage(EventId = 4400, EventName = "DeltaCheckpointFallback", Level = LogLevel.Warning,
        Message = "Delta checkpoint at version {Version} was discarded and not used to seed the snapshot "
            + "(reason {Reason}); reconstruction falls back to an older checkpoint or full JSON replay.")]
    internal static partial void CheckpointFallback(ILogger logger, long version, string reason);
}
