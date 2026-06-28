using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

        // AC1: the exact documentation-URL template is recorded, and its {0} placeholder resolves
        // to the registry anchor for this diagnostic (api-lifecycle.md#DS0001). Pinning both the
        // template and the formatted link stops a wrong or non-registry URL from passing.
        const string expectedUrlFormat =
            "https://github.com/khaines/deltasharp/blob/main/docs/engineering/design/api-lifecycle.md#{0}";
        Assert.Equal(expectedUrlFormat, experimental.UrlFormat);
        Assert.Equal(
            "https://github.com/khaines/deltasharp/blob/main/docs/engineering/design/api-lifecycle.md#DS0001",
            string.Format(CultureInfo.InvariantCulture, experimental.UrlFormat!, experimental.DiagnosticId));
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
        // member and a removal timeline. Pin the exact message: asserting only that it contains
        // "Product" would be satisfied by the obsolete member's own name ("ProductName") even if
        // the replacement guidance were dropped.
        Assert.False(obsolete!.IsError);
        Assert.Equal(
            "Use DeltaSharpInfo.Product instead. The ProductName alias is obsolete and is " +
            "scheduled for removal in DeltaSharp v0.2.0.",
            obsolete.Message);
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
