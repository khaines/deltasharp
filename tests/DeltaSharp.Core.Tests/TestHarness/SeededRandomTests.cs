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
        // The default entry point logs the seed line to the test output. On a FAILING test the
        // VSTest console logger surfaces this output, so the seed + reproduction command are
        // visible in CI. (Robust to a concurrent env override: only substring presence is asserted.)
        SeededRandom random = SeededRandom.Create(_output);

        Assert.Contains("DELTASHARP_TEST_SEED=", random.SeedAnnouncement, StringComparison.Ordinal);
        Assert.Contains("dotnet test --filter", random.SeedAnnouncement, StringComparison.Ordinal);
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
}
