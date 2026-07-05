using DeltaSharp.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace DeltaSharp.Core.Tests.TestHarness;

/// <summary>
/// Proves the deterministic seed can be overridden from configuration via the
/// <c>DELTASHARP_TEST_SEED</c> environment variable (STORY-00.5.1 AC1). Environment variables are
/// process-wide mutable state, so these tests join the serialized environment-sensitive collection
/// (STORY-00.5.1 AC2) — the same boundary rule <c>SparkSessionTestCollection</c> applies to the
/// process-wide session slots. Each test restores the prior value in a <c>finally</c>.
/// </summary>
[Collection(EnvironmentSensitiveTestCollection.Name)]
public sealed class SeedEnvironmentOverrideTests
{
    private readonly ITestOutputHelper _output;

    public SeedEnvironmentOverrideTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Resolve_UsesEnvironmentOverride_WhenSet()
    {
        string? original = Environment.GetEnvironmentVariable(TestSeed.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(TestSeed.EnvironmentVariable, "13579");

            Assert.Equal(13579, TestSeed.Resolve());

            SeededRandom random = SeededRandom.Create(_output, "scenario");
            Assert.Equal(13579, random.BaseSeed);
            Assert.Equal(TestSeed.Combine(13579, "scenario"), random.Seed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestSeed.EnvironmentVariable, original);
        }
    }

    [Fact]
    public void Resolve_FallsBackToDefault_WhenOverrideInvalid()
    {
        string? original = Environment.GetEnvironmentVariable(TestSeed.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(TestSeed.EnvironmentVariable, "not-an-int");

            Assert.Equal(TestSeed.Default, TestSeed.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestSeed.EnvironmentVariable, original);
        }
    }
}
