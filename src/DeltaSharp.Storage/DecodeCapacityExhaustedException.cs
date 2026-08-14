namespace DeltaSharp.Storage;

/// <summary>
/// A fail-fast admission control signal: a door's bounded-decode <b>memory budget</b> (or its secondary
/// strand-count cap) is already exhausted by decodes running/detached past their wall-clock deadline, so a new
/// one cannot be admitted safely (design §5.4 C-DECODE; the bounded-decoder's byte-aware admission cap).
/// </summary>
/// <remarks>
/// <para>Distinct from a <see cref="StorageErrorKind.DecodeBudgetExceeded"/> timeout: a timeout means <b>this</b>
/// input's decode did not terminate; a capacity exhaustion means the door is already saturated by <b>other</b>
/// non-terminating decodes' reserved bytes, so this call is rejected <b>without starting</b> (it never adds to
/// the stranded-decode residual). It is a transient/resource condition, not corruption — deliberately NOT a
/// <see cref="DeltaStorageException"/> so it is never conflated with a malformed file and never enters a
/// negative cache (a bad-input marker). It maps to the public <see cref="StorageErrorKind.DecoderSaturated"/>,
/// which is <b>fail-closed</b>: there is no automatic retry today (a bounded backoff-retry at the read facade
/// is tracked in #804); a caller may retry once capacity frees.</para>
/// <para>This is the bounded ceiling that makes an attacker-crafted, non-terminating decode a <b>known,
/// byte-bounded</b> resource cost instead of an unbounded, self-renewing leak: the retained bytes of
/// concurrently stranded decodes on a door can never exceed that door's <b>memory budget</b> (a fraction of
/// process/GC memory), reserved atomically BEFORE the decode starts. The <b>data-file</b> door runs the decode
/// on the shared <c>ThreadPool</c> (a dedicated thread bought no isolation there — the async row-group decode
/// resumes on the pool anyway); the <b>checkpoint</b> door keeps a dedicated thread because its decode is
/// synchronous over a pre-buffered <c>byte[]</c>. Neither door has a shared, count-capped scheduler a strand can
/// starve. At most one crafted door can be saturated at a time because the data-file and checkpoint doors have
/// independent memory budgets.</para>
/// </remarks>
internal sealed class DecodeCapacityExhaustedException : Exception
{
    public DecodeCapacityExhaustedException(string message)
        : base(message)
    {
    }
}
