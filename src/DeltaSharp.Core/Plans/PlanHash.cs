namespace DeltaSharp.Plans;

/// <summary>
/// Deterministic, process-independent hashing for the logical-plan IR. The CLR's
/// <see cref="string.GetHashCode()"/> and <see cref="System.HashCode"/> are randomized per
/// process, which would make plan hash codes vary across runs; reproducible planning,
/// caching, and tests require stable hashes (see <c>.github/copilot-instructions.md</c>), so
/// the IR derives its <c>GetHashCode</c> from this FNV-1a implementation instead.
/// </summary>
/// <remarks>
/// These are 32-bit hash codes for hash-based collections. Like any fixed-width hash they admit
/// collisions between distinct values; that is correctness-preserving because <c>Equals</c> —
/// not the hash — is authoritative for equality. This mirrors the Engine's
/// <c>DeltaSharp.Types.StableHash</c> rationale, re-homed as <see langword="internal"/>
/// to <c>DeltaSharp.Core</c> because Core cannot reference the <c>net10.0</c>-only Engine.
/// </remarks>
internal static class PlanHash
{
    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>The FNV-1a seed; the starting accumulator for a fresh hash chain.</summary>
    public static int Seed => unchecked((int)FnvOffsetBasis);

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
    /// Order-sensitive combination of a running hash <paramref name="left"/> with another value
    /// <paramref name="right"/>. The accumulator is advanced by the FNV prime, then all four
    /// bytes of <paramref name="right"/> are folded in FNV-1a style so small operands spread and
    /// the combination does not commutatively collapse.
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

    /// <summary>Order-sensitively folds the deterministic hash of each string in
    /// <paramref name="values"/> into <paramref name="seed"/>.</summary>
    public static int CombineStrings(int seed, IEnumerable<string> values)
    {
        int hash = seed;
        foreach (string value in values)
        {
            hash = Combine(hash, OfString(value));
        }

        return hash;
    }

    /// <summary>
    /// Order-independent hash of a string-keyed string map whose keys are compared
    /// <b>case-insensitively</b> (as the IR's option maps are — <see cref="PlanCollections.ToOptions"/>
    /// builds them with <see cref="System.StringComparer.OrdinalIgnoreCase"/>). Each entry contributes
    /// <c>Combine(OfString(lowercased key), OfString(value))</c> so two maps that are
    /// <see cref="PlanCollections.OptionsEqual"/> but differ only in key case hash IDENTICALLY (keeping
    /// the <c>Equals</c>/<c>GetHashCode</c> contract); values are hashed case-sensitively. The per-entry
    /// hashes are XOR-folded so the result does not depend on enumeration order.
    /// </summary>
    public static int OfStringMap(IReadOnlyDictionary<string, string> map)
    {
        int acc = 0;
        foreach (KeyValuePair<string, string> entry in map)
        {
            acc ^= Combine(OfString(entry.Key.ToLowerInvariant()), OfString(entry.Value));
        }

        return acc;
    }
}
