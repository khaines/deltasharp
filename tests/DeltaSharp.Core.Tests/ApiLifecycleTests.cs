using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Xunit;

namespace DeltaSharp.Core.Tests;

/// <summary>
/// Locks in the public-API lifecycle conventions from STORY-01.5.3: the experimental
/// member must carry <see cref="ExperimentalAttribute"/> with the registered diagnostic ID
/// and a documentation <c>UrlFormat</c>, and the obsolete member must carry
/// <see cref="ObsoleteAttribute"/> whose message states replacement guidance and a removal
/// timeline. See <c>docs/engineering/design/api-lifecycle.md</c>.
/// </summary>
public class ApiLifecycleTests
{
    [Fact]
    public void PreviewReleaseChannel_IsExperimental_WithRegisteredDiagnosticIdAndDocUrlFormat()
    {
        PropertyInfo? preview = typeof(DeltaSharpInfo).GetProperty("PreviewReleaseChannel");
        Assert.NotNull(preview);

        ExperimentalAttribute? experimental = preview!.GetCustomAttribute<ExperimentalAttribute>();
        Assert.NotNull(experimental);

        // AC1: diagnostic ID is the first DS#### registry entry.
        Assert.Equal("DS0001", experimental!.DiagnosticId);

        // AC1: a documentation URL is recorded; the {0} placeholder lets the compiler build a
        // per-diagnostic help link, and it points at the diagnostic-ID registry.
        Assert.False(string.IsNullOrEmpty(experimental.UrlFormat));
        Assert.Contains("{0}", experimental.UrlFormat);
        Assert.Contains("api-lifecycle", experimental.UrlFormat);
    }

    [Fact]
    public void PreviewReleaseChannel_WhenConsumedUnderSuppression_ReturnsInertPreviewValue()
    {
        // Demonstrates the documented opt-in: consuming a DS0001 API requires acknowledging
        // the experimental diagnostic. The value is an inert M1 placeholder.
#pragma warning disable DS0001 // Experimental preview-metadata API (see api-lifecycle.md#DS0001)
        string channel = DeltaSharpInfo.PreviewReleaseChannel;
#pragma warning restore DS0001
        Assert.Equal("preview", channel);
    }

    [Fact]
    public void ProductName_IsObsolete_WithReplacementGuidanceAndRemovalTimeline()
    {
        PropertyInfo? alias = typeof(DeltaSharpInfo).GetProperty("ProductName");
        Assert.NotNull(alias);

        ObsoleteAttribute? obsolete = alias!.GetCustomAttribute<ObsoleteAttribute>();
        Assert.NotNull(obsolete);

        // AC2: a deprecation warning (not a hard error) whose message states the replacement
        // member and a removal timeline.
        Assert.False(obsolete!.IsError);
        Assert.False(string.IsNullOrEmpty(obsolete.Message));
        Assert.Contains("Product", obsolete.Message);
        Assert.Contains("v0.2.0", obsolete.Message);
    }

    [Fact]
    public void ProductName_WhenConsumedUnderSuppression_ForwardsToProduct()
    {
        // The obsolete alias stays source-compatible: it forwards to its replacement.
#pragma warning disable CS0618 // Obsolete alias retained for compatibility
        string alias = DeltaSharpInfo.ProductName;
#pragma warning restore CS0618
        Assert.Equal(DeltaSharpInfo.Product, alias);
    }
}
