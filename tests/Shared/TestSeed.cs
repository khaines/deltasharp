using System.Globalization;

namespace DeltaSharp.TestSupport;

/// <summary>
/// Deterministic seed policy for DeltaSharp randomized tests (STORY-00.5.1). A randomized test
/// draws from a <see cref="SeededRandom"/> whose base seed is fixed by default but overridable
/// from configuration through the <see cref="EnvironmentVariable"/>, so a failure observed in CI
/// can be replayed byte-for-byte locally. See
/// <c>docs/engineering/design/test-harness-conventions.md</c>.
/// </summary>
/// <remarks>
/// This type is compiled INTO each <c>*.Tests</c> assembly (a linked shared source file), never
/// shipped, and never referenced by production code — production determinism is enforced
/// separately by the <c>System.Random</c>/<c>Guid.NewGuid</c> bans in <c>BannedSymbols.txt</c>.
/// </remarks>
internal static class TestSeed
{
    /// <summary>
    /// Environment variable (also settable from CI configuration) that overrides the base seed.
    /// A valid 32-bit integer wins; anything else falls back to <see cref="Default"/>.
    /// </summary>
    public const string EnvironmentVariable = "DELTASHARP_TEST_SEED";

    /// <summary>
    /// The fixed default base seed used when <see cref="EnvironmentVariable"/> is unset or invalid.
    /// The exact value is arbitrary; only that it is constant matters, so unattended runs are
    /// reproducible without any configuration.
    /// </summary>
    public const int Default = 0x0DE17A5D;

    /// <summary>Resolves the base seed from the process environment, falling back to <see cref="Default"/>.</summary>
    public static int Resolve() => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// Pure parse used by <see cref="Resolve"/> and by unit tests: a valid invariant-culture
    /// integer wins; <see langword="null"/>, blank, non-numeric, or out-of-range input falls back
    /// to <see cref="Default"/>.
    /// </summary>
    public static int Parse(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : Default;

    /// <summary>
    /// Mixes a per-scope salt into the base seed so different tests draw independent streams from a
    /// single override knob. The mix is FNV-1a over the scope characters, deliberately NOT
    /// <see cref="string.GetHashCode()"/> (which is randomized per process and therefore not
    /// reproducible across runs or machines).
    /// </summary>
    public static int Combine(int baseSeed, string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        const uint fnvOffsetBasis = 2166136261;
        const uint fnvPrime = 16777619;

        uint hash = fnvOffsetBasis;
        foreach (char c in scope)
        {
            hash ^= c;
            hash *= fnvPrime;
        }

        return unchecked((int)hash ^ baseSeed);
    }
}
