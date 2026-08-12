namespace DeltaSharp.Storage;

/// <summary>
/// A fail-fast admission control signal: too many untrusted decodes are already <b>detached</b> (running
/// past their wall-clock deadline on the bounded-decode worker) for a new one to be admitted safely
/// (design §5.4 C-DECODE; the bounded-decoder's <c>maxDetachedDecodes</c> admission cap).
/// </summary>
/// <remarks>
/// <para>Distinct from a <see cref="StorageErrorKind.DecodeBudgetExceeded"/> timeout: a timeout means <b>this</b>
/// input's decode did not terminate; a capacity exhaustion means the shared, hard-capped decode worker is
/// already saturated by <b>other</b> non-terminating decodes, so this call is rejected <b>without starting</b>
/// (it never adds to the stranded-decode residual). It is a transient/resource condition, not corruption —
/// deliberately NOT a <see cref="DeltaStorageException"/> so it is never conflated with a malformed file and
/// never enters a negative cache (a bad-input marker); a caller may retry once capacity frees.</para>
/// <para>This is the bounded ceiling that makes an attacker-crafted, non-terminating decode a <b>known,
/// finite</b> resource cost instead of an unbounded, self-renewing leak: the number of concurrently stranded
/// decodes can never exceed the bounded-decoder's <c>maxDetachedDecodes</c> admission cap, and the shared
/// ThreadPool stays healthy because at most <c>maxConcurrentDecodes</c> of them run at once on a dedicated,
/// hard-capped scheduler.</para>
/// </remarks>
internal sealed class DecodeCapacityExhaustedException : Exception
{
    public DecodeCapacityExhaustedException(string message)
        : base(message)
    {
    }
}
