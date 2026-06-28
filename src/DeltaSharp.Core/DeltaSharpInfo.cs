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
}
