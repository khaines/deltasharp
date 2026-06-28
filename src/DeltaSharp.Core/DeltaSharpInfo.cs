using System.Diagnostics.CodeAnalysis;

namespace DeltaSharp;

/// <summary>
/// Exposes build and version metadata for the DeltaSharp libraries.
/// </summary>
/// <remarks>
/// This type is an intentionally inert placeholder for the DeltaSharp M1 solution
/// skeleton. It deliberately exposes no Apache Spark or Delta Lake behavior; the real
/// <c>SparkSession</c>, <c>DataFrame</c>, and Delta surface arrive in later milestones.
/// </remarks>
public static class DeltaSharpInfo
{
    /// <summary>
    /// Gets the informational product name (<c>"DeltaSharp"</c>).
    /// </summary>
    public static string Product => "DeltaSharp";

    private static readonly string _version =
        typeof(DeltaSharpInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// Gets the version of the containing assembly, formatted as a dotted version string.
    /// </summary>
    public static string Version => _version;

    /// <summary>
    /// Gets the release channel for this build (for example <c>"preview"</c> while the
    /// product is pre-1.0).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Experimental — diagnostic <c>DS0001</c>.</b> This is a preview API: its shape and
    /// the channel taxonomy it returns may change without notice and make no Spark-parity or
    /// compatibility promise. Consuming it raises the <c>DS0001</c> compiler diagnostic;
    /// acknowledge it per call site with <c>#pragma warning disable DS0001</c> or
    /// project-wide with <c>&lt;NoWarn&gt;DS0001&lt;/NoWarn&gt;</c>.
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Diagnostic ID:</b> <c>DS0001</c>.</description></item>
    ///   <item><description><b>Documentation:</b> <see href="https://github.com/khaines/deltasharp/blob/main/docs/engineering/design/api-lifecycle.md#DS0001">DS0001 in the diagnostic-ID registry</see>.</description></item>
    ///   <item><description><b>Owner:</b> <c>dotnet-library-platform-engineer</c>.</description></item>
    ///   <item><description><b>Expected review point:</b> when the release-channel taxonomy (preview/rc/stable) is finalized for the v0.1 release.</description></item>
    /// </list>
    /// <para>
    /// Inert M1 placeholder: it reports <c>"preview"</c> for every pre-1.0 build and exposes
    /// no Apache Spark or Delta Lake behavior.
    /// </para>
    /// </remarks>
    [Experimental(DeltaSharpDiagnostics.PreviewMetadataApis, UrlFormat = DeltaSharpDiagnostics.UrlFormat)]
    public static string PreviewReleaseChannel =>
        _version.StartsWith("0.", StringComparison.Ordinal) ? "preview" : "stable";

    /// <summary>
    /// Gets the informational product name. <b>Obsolete alias</b> for <see cref="Product"/>.
    /// </summary>
    /// <remarks>
    /// Retained only for source compatibility with the earliest preview drafts; prefer
    /// <see cref="Product"/>. Referencing this member raises the framework's <c>CS0618</c>
    /// deprecation warning carrying the replacement guidance and removal timeline recorded in
    /// the <see cref="ObsoleteAttribute.Message"/>.
    /// </remarks>
    [Obsolete(DeltaSharpDiagnostics.ProductNameObsoleteMessage, error: false)]
    public static string ProductName => Product;
}
