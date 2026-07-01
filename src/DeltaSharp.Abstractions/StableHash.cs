namespace DeltaSharp.Types;

/// <summary>
/// Deterministic, process-independent hashing for the type system. The CLR's default
/// <see cref="string.GetHashCode()"/> is randomized per process, which would make type
/// hash codes vary across runs. Reproducible planning and caching
/// (see <c>.github/copilot-instructions.md</c>) require stable hashes, so the type
/// descriptors derive their <c>GetHashCode</c> from this FNV-1a implementation instead.
/// </summary>
/// <remarks>
/// These are 32-bit hash codes intended for hash-based collections. Like any fixed-width
/// hash they admit collisions between <i>distinct</i> values (an adversary can always craft
/// one in a 32-bit space); that is correctness-preserving because <c>Equals</c> — not the
/// hash — is authoritative for equality. They are deterministic and process-stable, not
/// collision-resistant cryptographic digests.
/// </remarks>
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
    /// value <paramref name="right"/>. The accumulator is advanced by the FNV prime, then each
    /// byte of <paramref name="right"/> is folded in FNV-1a style. Advancing <paramref name="left"/>
    /// before the first xor avoids the commutative collapse of a bare xor-then-multiply (which
    /// would map e.g. <c>Combine(8,4)</c> and <c>Combine(12,0)</c> — since <c>8^4 == 12^0</c> —
    /// to the same value); folding all four bytes (not just the low one) spreads small operands.
    /// </summary>
    public static int Combine(int left, int right)
    {
        unchecked
        {
            uint hash = (uint)left;
            hash *= FnvPrime;

            uint value = (uint)right;
            hash ^= value & 0xFF;
            hash *= FnvPrime;
            hash ^= (value >> 8) & 0xFF;
            hash *= FnvPrime;
            hash ^= (value >> 16) & 0xFF;
            hash *= FnvPrime;
            hash ^= (value >> 24) & 0xFF;
            hash *= FnvPrime;

            return (int)hash;
        }
    }
}
