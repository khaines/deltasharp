using System.Collections.Generic;
using System.Globalization;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// The reserved <see cref="ResolvedRelation.Options"/> keys the read door (#499) uses to carry a resolved
/// Delta file scan across the Core↔Executor seam. When the <see cref="DeltaSharp.Analysis.Analyzer"/>
/// resolves a <c>delta</c> <see cref="UnresolvedFileRelation"/>, it emits a <see cref="ResolvedRelation"/>
/// whose options carry the real table <see cref="PathKey">path</see> and the pinned
/// <see cref="VersionKey">resolved version</see> (plus a <see cref="FormatKey">format marker</see>), so the
/// Executor's Delta scan-source recognizes the relation and reads that exact snapshot. This mirrors how the
/// write door threads the sink format/path through the internal <see cref="SinkDescriptor"/>.
/// </summary>
/// <remarks>
/// The keys are namespaced with a reserved <c>__deltasharp.read.</c> prefix that can never collide with a
/// user-supplied read option, and their <b>values</b> (which can include the table path) are never
/// rendered by <see cref="ResolvedRelation.SimpleString"/> (which renders only the identifier and output
/// attributes), so a credential-bearing cloud path — deferred to #431 — cannot leak through a plan string.
/// The rendered identifier is the redacted path (see the analyzer).
/// </remarks>
internal static class DeltaReadRelation
{
    /// <summary>The reserved option key marking a resolved Delta file scan (value: the format name).</summary>
    public const string FormatKey = "__deltasharp.read.format";

    /// <summary>The reserved option key carrying the real table path (value, never rendered).</summary>
    public const string PathKey = "__deltasharp.read.path";

    /// <summary>The reserved option key carrying the pinned resolved snapshot version.</summary>
    public const string VersionKey = "__deltasharp.read.version";

    /// <summary>The <see cref="FormatKey"/> value identifying a Delta scan.</summary>
    public const string DeltaFormat = "delta";

    /// <summary>Builds the reserved options that carry a resolved Delta scan (real path + pinned version).</summary>
    /// <param name="path">The real table path (with any time-travel suffix already stripped).</param>
    /// <param name="resolvedVersion">The pinned snapshot version the scan reads.</param>
    public static IReadOnlyDictionary<string, string> BuildOptions(string path, long resolvedVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FormatKey] = DeltaFormat,
            [PathKey] = path,
            [VersionKey] = resolvedVersion.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Tries to read a resolved Delta scan's real path + pinned version out of a relation's
    /// options. Returns <see langword="false"/> for any non-Delta relation (an in-memory catalog scan),
    /// so the Executor's Delta scan-source cleanly declines a relation it does not own.</summary>
    /// <param name="options">The relation's options.</param>
    /// <param name="path">The real table path when this is a Delta scan.</param>
    /// <param name="resolvedVersion">The pinned snapshot version when this is a Delta scan.</param>
    /// <returns><see langword="true"/> if <paramref name="options"/> describe a resolved Delta scan.</returns>
    public static bool TryGet(
        IReadOnlyDictionary<string, string> options, out string path, out long resolvedVersion)
    {
        ArgumentNullException.ThrowIfNull(options);
        path = string.Empty;
        resolvedVersion = 0;

        if (!options.TryGetValue(FormatKey, out string? format)
            || !string.Equals(format, DeltaFormat, StringComparison.Ordinal)
            || !options.TryGetValue(PathKey, out string? resolvedPath)
            || string.IsNullOrEmpty(resolvedPath)
            || !options.TryGetValue(VersionKey, out string? versionText)
            || !long.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long version))
        {
            return false;
        }

        path = resolvedPath;
        resolvedVersion = version;
        return true;
    }
}
