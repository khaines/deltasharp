using DeltaSharp.TestSupport;
using Xunit;

namespace DeltaSharp.Core.Tests.TestHarness;

/// <summary>
/// Unit tests for <see cref="TestSeed"/> — the deterministic seed policy (STORY-00.5.1). These are
/// pure and touch no process-wide state, so they parallelize freely.
/// </summary>
public sealed class TestSeedTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("12345", 12345)]
    [InlineData("-7", -7)]
    [InlineData(" 42 ", 42)] // NumberStyles.Integer tolerates surrounding whitespace
    [InlineData("2147483647", int.MaxValue)]
    public void Parse_ValidInteger_ReturnsValue(string raw, int expected) =>
        Assert.Equal(expected, TestSeed.Parse(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("3.14")]
    [InlineData("9999999999999")] // overflows Int32 -> falls back
    public void Parse_MissingOrInvalid_FallsBackToDefault(string? raw) =>
        Assert.Equal(TestSeed.Default, TestSeed.Parse(raw));

    [Fact]
    public void Combine_IsDeterministicForSameInputs() =>
        Assert.Equal(TestSeed.Combine(123, "Scope_A"), TestSeed.Combine(123, "Scope_A"));

    [Fact]
    public void Combine_DiffersByScope() =>
        Assert.NotEqual(TestSeed.Combine(123, "Scope_A"), TestSeed.Combine(123, "Scope_B"));

    [Fact]
    public void Combine_DiffersByBaseSeed() =>
        Assert.NotEqual(TestSeed.Combine(1, "Scope_A"), TestSeed.Combine(2, "Scope_A"));

    [Fact]
    public void Combine_IsStableAcrossProcesses()
    {
        // Pin the exact mix so the stable FNV-1a hash cannot silently change: a change would
        // invalidate every reproduction seed recorded in a past CI log. If the mixing function is
        // intentionally changed, update this literal in the same commit and note it in the design
        // note (docs/engineering/design/test-harness-conventions.md).
        Assert.Equal(1399832719, TestSeed.Combine(0, "pinned"));
    }
}
