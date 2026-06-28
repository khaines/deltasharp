namespace DeltaSharp;

/// <summary>
/// Central registry of DeltaSharp's compile-time diagnostic identifiers (the
/// <c>DS####</c> namespace) and the canonical messages attached to API-lifecycle
/// attributes.
/// </summary>
/// <remarks>
/// Holding the identifiers, the documentation-URL template, and the obsolete messages in
/// one place keeps every DeltaSharp diagnostic ID unique and traceable to the policy in
/// <c>docs/engineering/design/api-lifecycle.md</c>. This type is intentionally
/// <see langword="internal"/>: the constants steer lifecycle-attribute usage but are not
/// part of the public API surface.
/// </remarks>
internal static class DeltaSharpDiagnostics
{
    /// <summary>
    /// Documentation-URL template for DeltaSharp diagnostics. The <c>{0}</c> placeholder is
    /// replaced with the diagnostic identifier (for example <c>DS0001</c>) by
    /// <see cref="System.Diagnostics.CodeAnalysis.ExperimentalAttribute.UrlFormat"/>.
    /// </summary>
    internal const string UrlFormat =
        "https://github.com/khaines/deltasharp/blob/main/docs/engineering/design/api-lifecycle.md#{0}";

    /// <summary>
    /// <c>DS0001</c> — experimental DeltaSharp preview-metadata APIs and the first entry in
    /// the diagnostic-ID registry. Consumers opt in with <c>#pragma warning disable DS0001</c>
    /// or project-wide with <c>&lt;NoWarn&gt;DS0001&lt;/NoWarn&gt;</c>.
    /// </summary>
    internal const string PreviewMetadataApis = "DS0001";

    /// <summary>
    /// Canonical deprecation message for the obsolete <c>DeltaSharpInfo.ProductName</c>
    /// alias: it states the replacement member and the planned removal release. Obsolete
    /// APIs surface through the framework's built-in <c>CS0618</c> diagnostic and therefore
    /// do not consume a <c>DS####</c> identifier.
    /// </summary>
    internal const string ProductNameObsoleteMessage =
        "Use DeltaSharpInfo.Product instead. The ProductName alias is obsolete and is scheduled for removal in DeltaSharp v0.2.0.";
}
