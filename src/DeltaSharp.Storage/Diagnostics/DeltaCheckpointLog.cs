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
/// redaction-by-omission), and the site takes no <see cref="System.Exception"/> object, so a swallowed
/// (attacker-influenced) exception message can never leak. The shared
/// <c>deltasharp.component</c>/<c>deltasharp.operation</c>/<c>deltasharp.backend</c> correlation dimensions
/// ride the <see cref="ILogger.BeginScope"/> the caller opens (design §7.2.1), so the line is routable by
/// the same bounded keys the sibling storage components emit.
/// <para>A discarded checkpoint is logged at <c>Warning</c>: the read still succeeds via JSON replay (or an
/// older checkpoint), but an unreadable checkpoint — e.g. an encrypted one (#681/#698) — is an actionable
/// operator signal that is otherwise invisible until the log ages out (#772).</para>
/// <para><b>Volume:</b> one line per selected checkpoint discarded <i>while seeding</i>, per snapshot load.
/// A persistently unreadable checkpoint (an encrypted one does not self-heal until re-checkpointed) therefore
/// re-emits on every load. This is intentional — a seed-time discard is an exceptional, individually-
/// actionable event, and <see cref="DeltaLog"/> is constructed per-operation, so there is no cross-load
/// dedupe seam. Alert on the <c>deltasharp.delta.checkpoint.fallbacks</c> counter (the rate instrument); the
/// Warning is the per-occurrence detail.</para>
/// <para><b>Production reach:</b> the counter reaches the shared meter and is live today; this log line is a
/// no-op until a host wires a logging provider (the #450 no-op-by-default posture, host wiring tracked on the
/// telemetry-export track). Until then the operator-observable artifact of #772 is the counter, not the
/// version on this line.</para>
/// </remarks>
internal static partial class DeltaCheckpointLog
{
    [LoggerMessage(EventId = 4400, EventName = "DeltaCheckpointFallback", Level = LogLevel.Warning,
        Message = "Delta checkpoint at version {Version} was discarded and not used to seed the snapshot "
            + "(reason {Reason}); reconstruction falls back to an older checkpoint or full JSON replay.")]
    internal static partial void CheckpointFallback(ILogger logger, long version, string reason);
}
