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
/// decodes on a door can never exceed that door's <c>maxDetachedDecodes</c> strand cap (atomically reserved
/// BEFORE the decode starts). Each untrusted decode runs on its OWN dedicated background <c>Thread</c>
/// (not the shared ThreadPool and not a shared, count-capped scheduler), so a stranded decode holds only its
/// own thread + reserved slot and NEVER occupies scheduling capacity a healthy decode needs — a healthy decode
/// submitted while N strands exist still starts immediately (up to the cap). At most one crafted door can be
/// saturated at a time because the data-file and checkpoint doors have independent caps.</para>
/// </remarks>
internal sealed class DecodeCapacityExhaustedException : Exception
{
    public DecodeCapacityExhaustedException(string message)
        : base(message)
    {
    }
}
