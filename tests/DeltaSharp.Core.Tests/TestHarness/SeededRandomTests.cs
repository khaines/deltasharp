using System.Globalization;
using DeltaSharp.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace DeltaSharp.Core.Tests.TestHarness;

/// <summary>
/// Unit tests for <see cref="SeededRandom"/> plus a demonstration randomized (property) test that
/// uses the harness the way a real randomized test should (STORY-00.5.1). Every method here is
/// deterministic and self-contained, so they parallelize freely.
/// </summary>
public sealed class SeededRandomTests
{
    private readonly ITestOutputHelper _output;

    public SeededRandomTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ForSeed_SameSeedAndScope_ProducesIdenticalSequence()
    {
        SeededRandom a = SeededRandom.ForSeed(4242, "seq");
        SeededRandom b = SeededRandom.ForSeed(4242, "seq");

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(a.Next(), b.Next());
        }
    }

    [Fact]
    public void ForSeed_DifferentBaseSeed_DivergesSequence()
    {
        SeededRandom a = SeededRandom.ForSeed(1, "seq");
        SeededRandom b = SeededRandom.ForSeed(2, "seq");

        bool diverged = false;
        for (int i = 0; i < 32 && !diverged; i++)
        {
            diverged = a.Next() != b.Next();
        }

        Assert.True(diverged, "distinct base seeds must yield distinct streams");
    }

    [Fact]
    public void EffectiveSeed_IsDerivedFromBaseSeedAndScope()
    {
        SeededRandom random = SeededRandom.ForSeed(2024, "MyScenario");

        Assert.Equal(2024, random.BaseSeed);
        Assert.Equal("MyScenario", random.Scope);
        Assert.Equal(TestSeed.Combine(2024, "MyScenario"), random.Seed);
    }

    [Fact]
    public void ReproductionCommand_PinsBaseSeedAndFiltersToScope()
    {
        SeededRandom random = SeededRandom.ForSeed(2024, "MyScenario");

        Assert.Contains("DELTASHARP_TEST_SEED=2024", random.ReproductionCommand, StringComparison.Ordinal);
        Assert.Contains("dotnet test --filter", random.ReproductionCommand, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName~MyScenario", random.ReproductionCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void SeedAnnouncement_CarriesSeedAndReproductionCommand()
    {
        SeededRandom random = SeededRandom.ForSeed(7, "Ann");

        Assert.Contains("baseSeed=7", random.SeedAnnouncement, StringComparison.Ordinal);
        Assert.Contains("effectiveSeed=", random.SeedAnnouncement, StringComparison.Ordinal);
        Assert.Contains(random.ReproductionCommand, random.SeedAnnouncement, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WithOutput_LogsReproducibleSeedLine()
    {
        // Prove Create(output) actually WRITES the seed line to the test output — not merely that
        // the SeedAnnouncement property renders correctly. A capturing ITestOutputHelper records the
        // WriteLine call, so this reddens if Create's `output.WriteLine(random.SeedAnnouncement)` is
        // ever removed (the capture would be empty and Assert.Single would throw). On a FAILING test
        // VSTest surfaces this output, making the seed + reproduction command visible in CI.
        var capturing = new CapturingTestOutputHelper();

        SeededRandom random = SeededRandom.Create(capturing, "SomeScope");

        string logged = Assert.Single(capturing.Lines);
        Assert.Equal(random.SeedAnnouncement, logged);
        Assert.Contains("[deltasharp-seed]", logged, StringComparison.Ordinal);
        Assert.Contains("DELTASHARP_TEST_SEED=", logged, StringComparison.Ordinal);
        Assert.Contains("dotnet test --filter", logged, StringComparison.Ordinal);
    }

    [Fact]
    public void Next_RangeOverloads_StayWithinBounds()
    {
        SeededRandom random = SeededRandom.ForSeed(99, "bounds");

        for (int i = 0; i < 1000; i++)
        {
            Assert.InRange(random.Next(10, 20), 10, 19);
            Assert.InRange(random.Next(5), 0, 4);
            Assert.InRange(random.NextDouble(), 0.0, 0.9999999999);
            _ = random.NextBool();
        }
    }

    [Fact]
    public void NextBool_IsDeterministicAndProducesBothValues()
    {
        // Pin the exact first draws AND require BOTH values across N draws, so mutating NextBool to a
        // constant (always true / always false) reddens this test — the range test above only
        // discards NextBool, so it alone would stay green under such a mutation. ForSeed bypasses the
        // env override and the seeded System.Random algorithm is version-stable, so the pinned prefix
        // is identical on every target framework.
        SeededRandom random = SeededRandom.ForSeed(99, "boolseq");

        bool[] expectedPrefix = { true, true, true, true, false, true, false, false, false, false };
        foreach (bool expected in expectedPrefix)
        {
            Assert.Equal(expected, random.NextBool());
        }

        bool sawTrue = false;
        bool sawFalse = false;
        for (int i = 0; i < 100; i++)
        {
            if (random.NextBool())
            {
                sawTrue = true;
            }
            else
            {
                sawFalse = true;
            }
        }

        Assert.True(sawTrue, "NextBool must produce at least one true over many draws");
        Assert.True(sawFalse, "NextBool must produce at least one false over many draws");
    }

    [Fact]
    public void NextBytes_IsReproducibleForSameSeed()
    {
        var first = new byte[16];
        var second = new byte[16];
        SeededRandom.ForSeed(3, "bytes").NextBytes(first);
        SeededRandom.ForSeed(3, "bytes").NextBytes(second);

        Assert.Equal(first, second);
    }

    // ------------------------------------------------------------------------------------------
    // Demonstration randomized (property) test. It uses the harness exactly as a real test should,
    // holds for EVERY seed (so it passes deterministically and is robust to a seed override), and
    // logs the seed + reproduction command via SeededRandom.Create(output). To see the seed-on-
    // failure behavior, inject a fault (e.g. replace the swap with `items[i] = items[j];`, which
    // drops an element) and inspect the failing test's Standard Output Messages.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void FisherYatesShuffle_IsAlwaysAPermutation()
    {
        SeededRandom random = SeededRandom.Create(_output);

        const int n = 256;
        int[] items = new int[n];
        for (int i = 0; i < n; i++)
        {
            items[i] = i;
        }

        for (int i = n - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        Array.Sort(items);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(i, items[i]);
        }
    }

    /// <summary>
    /// A test double for <see cref="ITestOutputHelper"/> that records every <c>WriteLine</c> call so
    /// a test can assert on what was actually written, rather than only on the source of the text.
    /// </summary>
    private sealed class CapturingTestOutputHelper : ITestOutputHelper
    {
        private readonly List<string> _lines = new();

        public IReadOnlyList<string> Lines => _lines;

        public void WriteLine(string message) => _lines.Add(message);

        public void WriteLine(string format, params object[] args) =>
            _lines.Add(string.Format(CultureInfo.InvariantCulture, format, args));
    }
}
