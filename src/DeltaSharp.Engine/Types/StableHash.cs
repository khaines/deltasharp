namespace DeltaSharp.Engine.Types;

/// <summary>
/// Deterministic, process-independent hashing for the type system. The CLR's default
/// <see cref="string.GetHashCode()"/> is randomized per process, which would make type
/// hash codes vary across runs. Reproducible planning and caching
/// (see <c>.github/copilot-instructions.md</c>) require stable hashes, so the type
/// descriptors derive their <c>GetHashCode</c> from this FNV-1a implementation instead.
/// </summary>
internal static class StableHash
{
    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>FNV-1a over the UTF-16 code units of <paramref name="value"/>.</summary>
    public static int OfString(string value)
    {
        unchecked
        {
            uint hash = FnvOffsetBasis;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= FnvPrime;
            }

            return (int)hash;
        }
    }

    /// <summary>
    /// Order-sensitive combination of a running hash <paramref name="left"/> with another
    /// value <paramref name="right"/>. The accumulator is advanced by the FNV prime <b>before</b>
    /// mixing in <paramref name="right"/>, so the result is non-commutative and well-distributed
    /// (a plain xor-then-multiply is commutative, collapsing e.g. <c>Combine(8,4)</c> and
    /// <c>Combine(12,0)</c> — since <c>8^4 == 12^0</c> — to the same value).
    /// </summary>
    public static int Combine(int left, int right)
    {
        unchecked
        {
            uint hash = (uint)left;
            hash *= FnvPrime;
            hash ^= (uint)right;
            hash *= FnvPrime;
            return (int)hash;
        }
    }
}
