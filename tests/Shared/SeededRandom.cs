using System.Runtime.CompilerServices;
using Xunit.Abstractions;
using static System.FormattableString;

namespace DeltaSharp.TestSupport;

/// <summary>
/// A deterministic, reproducible random-number source for DeltaSharp randomized tests
/// (STORY-00.5.1). The <b>base seed</b> comes from <see cref="TestSeed.Resolve"/> (fixed by
/// default, overridable via <see cref="TestSeed.EnvironmentVariable"/>); the <b>effective seed</b>
/// that actually feeds the stream is <see cref="TestSeed.Combine(int, string)"/> of the base seed
/// and a per-test <see cref="Scope"/>, so every test gets an independent-but-reproducible stream
/// from a single override knob.
/// </summary>
/// <remarks>
/// Prefer <see cref="Create(ITestOutputHelper, string)"/>: it logs <see cref="SeedAnnouncement"/>
/// (seed + copy-pasteable reproduction command) to the test output, which the VSTest console
/// logger surfaces for FAILING tests — so a CI failure shows exactly how to replay it locally.
/// This facade exposes only the RNG surface tests need and deliberately does not leak the
/// underlying <c>System.Random</c>.
/// <para><b>Not thread-safe:</b> it wraps a single <c>System.Random</c>; create one instance per
/// test and do not share it across threads (mirrors <c>System.Random</c>).</para>
/// </remarks>
internal sealed class SeededRandom
{
    private readonly Random _random;

    private SeededRandom(int baseSeed, string scope)
    {
        BaseSeed = baseSeed;
        Scope = scope;
        Seed = TestSeed.Combine(baseSeed, scope);
        _random = new Random(Seed);
    }

    /// <summary>The base seed (the value you set <see cref="TestSeed.EnvironmentVariable"/> to in order to reproduce).</summary>
    public int BaseSeed { get; }

    /// <summary>The effective per-scope seed actually fed to the stream (derived from <see cref="BaseSeed"/> and <see cref="Scope"/>).</summary>
    public int Seed { get; }

    /// <summary>The per-test scope (defaults to the calling test method name via <see cref="CallerMemberNameAttribute"/>).</summary>
    public string Scope { get; }

    /// <summary>A copy-pasteable command that replays exactly this stream: it pins the base seed and filters to this scope.</summary>
    public string ReproductionCommand =>
        Invariant($"DELTASHARP_TEST_SEED={BaseSeed} dotnet test --filter \"FullyQualifiedName~{Scope}\"");

    /// <summary>A single log line carrying the seed and <see cref="ReproductionCommand"/>, surfaced by VSTest on failure.</summary>
    public string SeedAnnouncement =>
        Invariant($"[deltasharp-seed] scope={Scope} baseSeed={BaseSeed} effectiveSeed={Seed} | reproduce: {ReproductionCommand}");

    /// <summary>Creates a source from the resolved base seed and logs <see cref="SeedAnnouncement"/> to <paramref name="output"/>.</summary>
    public static SeededRandom Create(ITestOutputHelper output, [CallerMemberName] string scope = "")
    {
        ArgumentNullException.ThrowIfNull(output);

        SeededRandom random = new(TestSeed.Resolve(), scope);
        output.WriteLine(random.SeedAnnouncement);
        return random;
    }

    /// <summary>Creates a source from an explicit base seed, bypassing configuration. Used by harness self-tests.</summary>
    public static SeededRandom ForSeed(int baseSeed, [CallerMemberName] string scope = "") =>
        new(baseSeed, scope);

    /// <summary>Returns a non-negative random integer.</summary>
    public int Next() => _random.Next();

    /// <summary>Returns a non-negative random integer strictly less than <paramref name="maxValue"/>.</summary>
    public int Next(int maxValue) => _random.Next(maxValue);

    /// <summary>Returns a random integer in <c>[minValue, maxValue)</c>.</summary>
    public int Next(int minValue, int maxValue) => _random.Next(minValue, maxValue);

    /// <summary>Returns a random double in <c>[0.0, 1.0)</c>.</summary>
    public double NextDouble() => _random.NextDouble();

    /// <summary>Returns a random boolean.</summary>
    public bool NextBool() => _random.Next(2) == 1;

    /// <summary>Fills <paramref name="buffer"/> with random bytes.</summary>
    public void NextBytes(byte[] buffer) => _random.NextBytes(buffer);
}
