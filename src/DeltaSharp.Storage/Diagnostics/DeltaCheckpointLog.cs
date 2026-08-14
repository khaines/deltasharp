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

    /// <summary>
    /// A distinct signal (EventId 4403) for a classic checkpoint whose decode was rejected fail-fast because the
    /// checkpoint bounded-decode door's strand cap was already full (too many non-terminating decodes already
    /// detached, <c>BoundedDecode</c>, #647/#699/#716) — the decode NEVER RAN. This is a transient CAPACITY
    /// fault, categorically distinct from the decode-TIMEOUT at 4402 (where the decode ran past budget): kept
    /// separate so an operator can alert on decoder saturation (sustained crafted-input pressure exhausting the
    /// strand budget) independently of both routine bit-rot (4400) and a single non-terminating decode (4402).
    /// The distinction is carried by this EventId and the sibling <c>reason=decoder_saturated</c> metric label
    /// plus the <c>deltasharp.storage.decode.capacity_exhausted{door=checkpoint}</c> counter; it deliberately
    /// does NOT increment <c>decode.budget_exceeded</c>. The checkpoint may be perfectly healthy, so its
    /// identity is NOT negatively cached — the read simply falls back to JSON replay this time and the decode is
    /// re-attempted once capacity frees. Renders only the discarded <b>version</b> (an integer, safe) and takes
    /// no <see cref="System.Exception"/> object (§7.2.2 redaction-by-omission).
    /// <para>Logged at <c>Warning</c>: the read still succeeds via fallback, but sustained saturation is an
    /// actionable operator signal — alert on the counter.</para>
    /// </summary>
    [LoggerMessage(EventId = 4403, EventName = "DeltaCheckpointDecoderSaturated", Level = LogLevel.Warning,
        Message = "Delta checkpoint at version {Version} was not decoded because the bounded-decode checkpoint "
            + "door was at strand capacity (sustained non-terminating-decode pressure); reconstruction falls "
            + "back to an older checkpoint or full JSON replay this load, and the checkpoint is re-attempted "
            + "when capacity frees (it is not negatively cached).")]
    internal static partial void CheckpointDecoderSaturated(ILogger logger, long version);

    /// <summary>
    /// A distinct signal (EventId 4404) for a classic checkpoint part that was SKIPPED without decoding because
    /// its identity is suppressed in the process-wide negative cache (strike-gated, ≥2 proven timeouts, High
    /// #6) — or because another concurrent snapshot load already holds the single-flight re-probe for it (High
    /// #7). NO decode ran, so this is categorically distinct from the decode-TIMEOUT at 4402 (where the decode
    /// actually ran past budget): kept separate so log-based alerting on a decode-DoS trip (4402) is not
    /// conflated with the far higher-volume steady-state skip of an already-known-bad checkpoint (Medium
    /// finding). The distinction is carried by this EventId and the sibling <c>reason=negative_cache_skip</c>
    /// metric label plus the <c>deltasharp.storage.decode.negative_cache_skip{door=checkpoint}</c> counter; it
    /// deliberately does NOT increment <c>decode.budget_exceeded</c>. Renders only the discarded <b>version</b>
    /// (an integer, safe) and takes no <see cref="System.Exception"/> object (§7.2.2 redaction-by-omission).
    /// <para>Logged at <c>Warning</c>: the read still succeeds via fallback, but a persistently-suppressed
    /// checkpoint is an actionable operator signal — alert on the counter (the steady-state rate instrument).</para>
    /// </summary>
    [LoggerMessage(EventId = 4404, EventName = "DeltaCheckpointNegativeCacheSkip", Level = LogLevel.Warning,
        Message = "Delta checkpoint at version {Version} was skipped without decoding because its part is "
            + "suppressed in the decode negative cache (a persistently non-terminating checkpoint) or is being "
            + "re-probed by another load; reconstruction falls back to an older checkpoint or full JSON replay.")]
    internal static partial void CheckpointNegativeCacheSkip(ILogger logger, long version);

    /// <summary>
    /// A lower-severity signal (EventId 4405, <c>Information</c>) for a SELECTION-time checkpoint skip that led
    /// to full JSON replay (#787): a classic multi-part checkpoint group at or below the resolved version was
    /// INCOMPLETE (a crashed/interrupted or partially-uploaded multi-part upload), it was skipped before any
    /// seed attempt, and no complete checkpoint seeded the read — so reconstruction fell all the way back to
    /// full JSON replay. Distinct from the Warning seed-time discards (4400/4401): the checkpoint was never
    /// opened (no <see cref="DeltaProtocolException"/>), and a permanently-failed multi-part upload is a
    /// PERSISTENT full-replay condition whose only other symptom (<c>CheckpointVersion == null</c>) is
    /// indistinguishable from a healthy table with no checkpoint. It rides <c>Information</c>, not
    /// <c>Warning</c>, deliberately: the read still succeeds, and the selection skip can also occur benignly
    /// (a checkpoint mid-write) — routing it here keeps a persistent condition discoverable via the sibling
    /// <c>deltasharp.delta.checkpoint.fallbacks{reason=incomplete}</c> counter (the alertable rate instrument)
    /// without adding Warning noise on concurrent-write transients. The caller only emits it when the
    /// full-replay fallback actually occurred, so a mid-write newest checkpoint skipped while a complete OLDER
    /// checkpoint still seeds the read does NOT fire. Renders only the skipped <b>version</b> (an integer,
    /// safe) and takes no <see cref="System.Exception"/> object (§7.2.2 redaction-by-omission).
    /// </summary>
    [LoggerMessage(EventId = 4405, EventName = "DeltaCheckpointSelectionSkipped", Level = LogLevel.Information,
        Message = "Delta checkpoint at version {Version} was skipped at selection because it is an incomplete "
            + "multi-part checkpoint group, and no complete checkpoint seeded the read; reconstruction fell "
            + "back to full JSON replay.")]
    internal static partial void CheckpointSelectionSkipped(ILogger logger, long version);
}
