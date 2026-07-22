using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Unit tests for the increment-1 Change Data Feed protocol seam (§2.7): the optional
/// <see cref="ChangeDataFeedFeature"/> enable-gate (lenient — absent/malformed ⇒ OFF, never throws), the
/// idempotent writer-feature enumeration, and <see cref="ProtocolSupport"/> accepting a writer-v7 protocol
/// that declares <c>changeDataFeed</c> while leaving readers unaffected (CDF is writer-only, INV C1).
/// </summary>
public sealed class ChangeDataFeedFeatureTests
{
    private static IReadOnlyDictionary<string, string> Config(params (string Key, string Value)[] entries)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in entries)
        {
            builder[key] = value;
        }

        return builder.ToImmutable();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void IsEnabled_True_ForTrueTokens(string value)
    {
        Assert.True(ChangeDataFeedFeature.IsEnabled(Config((ChangeDataFeedFeature.PropertyKey, value))));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("tru")]
    [InlineData("")]
    public void IsEnabled_False_ForAbsentFalseOrMalformed(string value)
    {
        // Lenient optional enable-gate: false/malformed leaves the OPTIONAL feature OFF and NEVER throws
        // (mirrors TypeWideningFeature.IsEnabled), unlike the fail-closed AppendOnlyFeature.IsEnabled.
        Assert.False(ChangeDataFeedFeature.IsEnabled(Config((ChangeDataFeedFeature.PropertyKey, value))));
    }

    [Fact]
    public void IsEnabled_False_WhenPropertyAbsent()
    {
        Assert.False(ChangeDataFeedFeature.IsEnabled(Config()));
    }

    [Fact]
    public void WithWriterFeature_AddsFeature_AndIsIdempotent()
    {
        ImmutableArray<string> added = ChangeDataFeedFeature.WithWriterFeature(ImmutableArray<string>.Empty);
        Assert.Contains(ChangeDataFeedFeature.Feature, added);

        // Idempotent: applying again does not duplicate the feature.
        ImmutableArray<string> again = ChangeDataFeedFeature.WithWriterFeature(added);
        Assert.Single(again, ChangeDataFeedFeature.Feature);

        // A default (uninitialized) array is treated as empty.
        ImmutableArray<string> fromDefault = ChangeDataFeedFeature.WithWriterFeature(default);
        Assert.Equal(new[] { ChangeDataFeedFeature.Feature }, fromDefault);
    }

    [Fact]
    public void EnsureWritable_AcceptsWriterV7ProtocolDeclaringChangeDataFeed()
    {
        var protocol = new ProtocolAction(
            1,
            ProtocolSupport.TableFeaturesWriterVersion,
            ImmutableArray<string>.Empty,
            ImmutableArray.Create(ChangeDataFeedFeature.Feature));

        // Writer-only feature is now whitelisted → no throw.
        ProtocolSupport.EnsureWritable(protocol);

        // Readers are unaffected: changeDataFeed is not a reader feature.
        Assert.DoesNotContain(ChangeDataFeedFeature.Feature, ProtocolSupport.SupportedReaderFeatures);
        Assert.Contains(ChangeDataFeedFeature.Feature, ProtocolSupport.SupportedWriterFeatures);
    }
}
