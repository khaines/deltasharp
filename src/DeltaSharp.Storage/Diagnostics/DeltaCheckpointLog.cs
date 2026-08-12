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
/// integer, safe) and, on the generic fallback site (4400) only, the bounded <b>reason</b>
/// (<c>unsupported_feature</c>/<c>malformed</c>). The forged-reject site (4401) carries <b>no</b> <c>Reason</c>
/// field — its <c>forged_multi_metadata</c> attribution rides its distinct <see cref="EventId"/> and the
/// sibling <c>deltasharp.delta.checkpoint.fallbacks</c> metric label, not a rendered field. A raw storage
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

    /// <summary>
    /// A distinct <b>security</b> signal (EventId 4401) for a rejected forged multi-<c>metaData</c> checkpoint (the
    /// #671 cross-part identity forgery): the selected checkpoint carried more than one <c>metaData</c> action across
    /// its parts, so it was rejected as non-authoritative and never seeded the snapshot. Kept separate from the
    /// generic <see cref="CheckpointFallback"/> (4400) so an operator can alert on identity forgery independently of
    /// routine bit-rot — the two are indistinguishable by exception type downstream, so the distinction is carried by
    /// this EventId and the sibling <c>reason=forged_multi_metadata</c> metric label. Renders only the discarded
    /// <b>version</b> (an integer, safe) and takes no <see cref="System.Exception"/> object, so the attacker-chosen
    /// <c>metaData</c> content the reject message would embed can never leak (§7.2.2 redaction-by-omission).
    /// <para>Logged at <c>Warning</c>, not <c>Critical</c>/<c>Error</c>, deliberately: the forgery was
    /// <i>successfully blocked</i> — the control worked and the table still reads correctly via fallback — so this
    /// is a detected-and-contained event, not the unrecoverable security/data-integrity failure the
    /// <c>Critical</c> tier is reserved for (logging checklist 09a). It sits alongside the sibling 4400 discard at
    /// the same level; operators page on it by <see cref="EventId"/> (4401) or the bounded metric label.</para>
    /// </summary>
    [LoggerMessage(EventId = 4401, EventName = "DeltaCheckpointForgedMultiMetadataRejected", Level = LogLevel.Warning,
        Message = "Delta checkpoint at version {Version} was rejected as forged and not used to seed the snapshot: "
            + "it carries more than one metaData action across its parts (a checkpoint must summarize at most one); "
            + "reconstruction falls back to an older checkpoint or full JSON replay.")]
    internal static partial void CheckpointForgedMultiMetadataRejected(ILogger logger, long version);

    /// <summary>
    /// A distinct signal (EventId 4402) for a classic checkpoint whose decode did not terminate within the
    /// wall-clock decode budget (<c>BoundedDecode</c>, #647/#699/#716) and was failed closed: the checkpoint
    /// was discarded (non-authoritative) and reconstruction fell back to an older checkpoint or full JSON
    /// replay. Kept separate from the generic <see cref="CheckpointFallback"/> (4400) so an operator can alert
    /// on a decode-DoS attempt (a crafted checkpoint driving a non-terminating decode) independently of routine
    /// bit-rot — the two are indistinguishable by exception type downstream, so the distinction is carried by
    /// this EventId and the sibling <c>reason=decode_timeout</c> metric label plus the
    /// <c>deltasharp.storage.decode.budget_exceeded{door=checkpoint}</c> counter. Renders only the discarded
    /// <b>version</b> (an integer, safe) and takes no <see cref="System.Exception"/> object, so no crafted byte
    /// content can leak (§7.2.2 redaction-by-omission).
    /// <para>Logged at <c>Warning</c>: the read still succeeds via fallback (the control worked), but a
    /// checkpoint that persistently times out is an actionable operator signal. The checkpoint layer negatively
    /// caches the timed-out identity, so the decode is not re-attempted (no new stranded decode) on subsequent
    /// loads; this log line still re-emits per load as the per-occurrence detail — alert on the counter.</para>
    /// </summary>
    [LoggerMessage(EventId = 4402, EventName = "DeltaCheckpointDecodeTimeout", Level = LogLevel.Warning,
        Message = "Delta checkpoint at version {Version} was discarded because its decode did not terminate within "
            + "the bounded-decode budget (a crafted checkpoint driving a non-terminating decode); reconstruction "
            + "falls back to an older checkpoint or full JSON replay, and the timed-out checkpoint is negatively "
            + "cached so it is not re-decoded.")]
    internal static partial void CheckpointDecodeTimeout(ILogger logger, long version);
}
