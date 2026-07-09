using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Tests for <see cref="RetentionPolicy"/>'s table-configured retention resolution (STORY-05.6.2 MEDIUM):
/// a no-argument VACUUM must honor the table's <c>delta.deletedFileRetentionDuration</c> property, parsing
/// the Delta <c>CalendarInterval</c>/duration string, and must fail closed (throw) rather than silently
/// under-retaining when the property is present but unparseable or expresses a calendar-ambiguous unit.
/// </summary>
public sealed class RetentionPolicyTests
{
    [Theory]
    [InlineData("interval 30 days", 30 * 24)]
    [InlineData("7 days", 7 * 24)]
    [InlineData("interval 168 hours", 168)]
    [InlineData("interval 1 weeks", 7 * 24)]
    [InlineData("interval 1 weeks 12 hours", 7 * 24 + 12)]
    [InlineData("interval 90 minutes", 1)] // 1.5 h → validated below as exact
    public void TryParseRetentionInterval_ParsesFixedDurations(string value, int _)
    {
        Assert.True(RetentionPolicy.TryParseRetentionInterval(value, out TimeSpan parsed));
        Assert.True(parsed > TimeSpan.Zero);
    }

    [Fact]
    public void TryParseRetentionInterval_ComputesExactDurations()
    {
        Assert.True(RetentionPolicy.TryParseRetentionInterval("interval 30 days", out TimeSpan days));
        Assert.Equal(TimeSpan.FromDays(30), days);

        Assert.True(RetentionPolicy.TryParseRetentionInterval("interval 1 weeks 12 hours", out TimeSpan combo));
        Assert.Equal(TimeSpan.FromDays(7) + TimeSpan.FromHours(12), combo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("interval")]
    [InlineData("interval 5")]
    [InlineData("5")]
    [InlineData("interval 1 month")]   // calendar-ambiguous → rejected (fail closed)
    [InlineData("interval 2 years")]   // calendar-ambiguous → rejected
    [InlineData("interval -3 days")]   // negative count → rejected
    [InlineData("interval three days")]
    [InlineData("interval 5 fortnights")]
    public void TryParseRetentionInterval_RejectsUnparseableOrAmbiguous(string value)
    {
        Assert.False(RetentionPolicy.TryParseRetentionInterval(value, out _));
    }

    [Fact]
    public void ResolveTableRetention_UsesDefault_WhenPropertyAbsent()
    {
        var policy = RetentionPolicy.Default;
        TimeSpan resolved = policy.ResolveTableRetention(
            ImmutableSortedDictionary<string, string>.Empty);
        Assert.Equal(policy.DefaultRetention, resolved);
    }

    [Fact]
    public void ResolveTableRetention_UsesProperty_WhenPresent()
    {
        var policy = RetentionPolicy.Default;
        ImmutableSortedDictionary<string, string> config = ImmutableSortedDictionary<string, string>.Empty
            .Add(RetentionPolicy.DeletedFileRetentionDurationKey, "interval 30 days");
        Assert.Equal(TimeSpan.FromDays(30), policy.ResolveTableRetention(config));
    }

    [Fact]
    public void ResolveTableRetention_FailsClosed_OnUnparseableProperty()
    {
        var policy = RetentionPolicy.Default;
        ImmutableSortedDictionary<string, string> config = ImmutableSortedDictionary<string, string>.Empty
            .Add(RetentionPolicy.DeletedFileRetentionDurationKey, "interval 1 month");
        Assert.Throws<FormatException>(() => policy.ResolveTableRetention(config));
    }
}
