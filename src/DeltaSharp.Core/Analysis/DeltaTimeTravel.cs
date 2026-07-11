using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Analysis;

/// <summary>
/// Parses a Delta read's time-travel intent (#499) from the reader <c>versionAsOf</c>/<c>timestampAsOf</c>
/// options and the <c>path@v&lt;n&gt;</c> / <c>path@yyyyMMddHHmmssSSS</c> path suffix into a normalized
/// spec — an exact version XOR a timestamp XOR neither (a base read) — and the time-travel-suffix-stripped
/// path. This is Spark-faithful <b>Core</b> semantics (option/path parsing and the both-specified error);
/// the storage seam only maps a valid spec onto a snapshot.
/// </summary>
/// <remarks>
/// <para><b>Both-specified rule (fail closed).</b> Spark disallows specifying both a version and a
/// timestamp. DeltaSharp additionally rejects specifying the <i>same</i> dimension twice (an option and the
/// path suffix), so any redundant/conflicting time-travel spec is a deterministic
/// <see cref="AnalysisException"/> — never a silently-ignored option.</para>
///
/// <para><b>Timestamp timezone.</b> Timestamps (both the option string and the path suffix) are interpreted
/// as <b>UTC</b> and compared in UTC by the storage layer. Spark resolves <c>timestampAsOf</c> in the
/// session timezone; DeltaSharp pins UTC for determinism until a session-timezone conf lands. This is
/// documented deliberately so a caller supplies a UTC instant (or a zone-qualified <c>DateTimeOffset</c>
/// string, whose offset is honored).</para>
/// </remarks>
internal static class DeltaTimeTravel
{
    /// <summary>The reader option pinning an exact version (Spark's <c>versionAsOf</c>).</summary>
    public const string VersionAsOfOption = "versionAsOf";

    /// <summary>The reader option pinning a timestamp (Spark's <c>timestampAsOf</c>).</summary>
    public const string TimestampAsOfOption = "timestampAsOf";

    // The path-suffix timestamp forms (Delta's @yyyyMMddHHmmssSSS and the seconds-granularity @yyyyMMddHHmmss),
    // parsed exactly and interpreted as UTC.
    private static readonly string[] PathTimestampFormats =
    {
        "yyyyMMddHHmmssfff",
        "yyyyMMddHHmmss",
    };

    // The timestampAsOf OPTION forms (a friendlier superset of the compact path form), interpreted as UTC
    // when no offset is present.
    private static readonly string[] OptionTimestampFormats =
    {
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd",
    };

    /// <summary>Parses <paramref name="path"/> + <paramref name="options"/> into a normalized time-travel spec.</summary>
    /// <param name="path">The load path, possibly carrying a <c>@v…</c>/<c>@…</c> suffix.</param>
    /// <param name="options">The reader options (may hold <c>versionAsOf</c>/<c>timestampAsOf</c>).</param>
    /// <returns>The stripped path and the pinned version XOR timestamp (both null for a base read).</returns>
    /// <exception cref="AnalysisException">Time travel was specified more than one way, or a value is
    /// unparseable.</exception>
    public static DeltaTimeTravelSpec Parse(string path, IReadOnlyDictionary<string, string> options)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(options);

        (string strippedPath, long? pathVersion, DateTimeOffset? pathTimestamp) = ParsePathSuffix(path);

        long? optionVersion = ParseOptionVersion(options);
        DateTimeOffset? optionTimestamp = ParseOptionTimestamp(options);

        long? version = Combine(
            path, "version", pathVersion, optionVersion,
            "the path '@v<n>' suffix and the versionAsOf option both pin a version");
        DateTimeOffset? timestamp = Combine(
            path, "timestamp", pathTimestamp, optionTimestamp,
            "the path timestamp suffix and the timestampAsOf option both pin a timestamp");

        if (version is not null && timestamp is not null)
        {
            throw AnalysisException.ConflictingTimeTravel(
                path, "a version and a timestamp were both specified");
        }

        return new DeltaTimeTravelSpec(strippedPath, version, timestamp);
    }

    // A path-suffix `@v<digits>` pins a version; `@<14-or-17 digits>` pins a UTC timestamp; any other `@…`
    // (or no `@`) is part of the path. Only the LAST `@` segment is considered (a path may legitimately
    // contain earlier `@`s, e.g. userinfo), matching Delta's suffix parsing.
    private static (string Path, long? Version, DateTimeOffset? Timestamp) ParsePathSuffix(string path)
    {
        int at = path.LastIndexOf('@');
        if (at < 0)
        {
            return (path, null, null);
        }

        string prefix = path[..at];
        string suffix = path[(at + 1)..];
        if (prefix.Length == 0 || suffix.Length == 0)
        {
            return (path, null, null);
        }

        if (suffix[0] is 'v' or 'V')
        {
            string digits = suffix[1..];
            if (digits.Length > 0
                && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long version))
            {
                return (prefix, version, null);
            }

            return (path, null, null);
        }

        if (IsAllDigits(suffix)
            && DateTime.TryParseExact(
                suffix, PathTimestampFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
        {
            return (prefix, null, new DateTimeOffset(parsed, TimeSpan.Zero));
        }

        return (path, null, null);
    }

    private static long? ParseOptionVersion(IReadOnlyDictionary<string, string> options)
    {
        if (!TryGetOption(options, VersionAsOfOption, out string? raw))
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long version)
            || version < 0)
        {
            throw AnalysisException.InvalidTimeTravelValue(
                VersionAsOfOption, raw, "expected a non-negative integer version");
        }

        return version;
    }

    private static DateTimeOffset? ParseOptionTimestamp(IReadOnlyDictionary<string, string> options)
    {
        if (!TryGetOption(options, TimestampAsOfOption, out string? raw))
        {
            return null;
        }

        // Honor an explicit offset if present; otherwise assume UTC. Try the fixed forms first for
        // determinism, then a lenient round-trip parse for offset-qualified strings.
        if (DateTime.TryParseExact(
                raw, OptionTimestampFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime exact))
        {
            return new DateTimeOffset(exact, TimeSpan.Zero);
        }

        if (DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset offset))
        {
            return offset.ToUniversalTime();
        }

        throw AnalysisException.InvalidTimeTravelValue(
            TimestampAsOfOption, raw,
            "expected a timestamp like 'yyyy-MM-dd', 'yyyy-MM-dd HH:mm:ss[.fff]', or an ISO-8601 instant "
            + "(interpreted as UTC when no offset is given)");
    }

    private static T? Combine<T>(
        string path, string dimension, T? fromPath, T? fromOption, string detail)
        where T : struct
    {
        if (fromPath is not null && fromOption is not null)
        {
            throw AnalysisException.ConflictingTimeTravel(path, detail);
        }

        _ = dimension;
        return fromPath ?? fromOption;
    }

    private static bool TryGetOption(
        IReadOnlyDictionary<string, string> options, string key, [NotNullWhen(true)] out string? value)
    {
        // The reader stores options under their canonical spelling (case-insensitive), so a direct lookup
        // by the canonical key suffices; guard against a null/empty value defensively.
        if (options.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
        {
            return true;
        }

        value = null;
        return false;
    }

    private static bool IsAllDigits(string value)
    {
        foreach (char c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}

/// <summary>A normalized Delta time-travel spec: the suffix-stripped <see cref="Path"/> and an exact
/// <see cref="Version"/> XOR a <see cref="Timestamp"/> (both null for a base/latest read).</summary>
/// <param name="Path">The table path with any time-travel suffix removed.</param>
/// <param name="Version">A pinned exact version, or <see langword="null"/>.</param>
/// <param name="Timestamp">A pinned UTC timestamp, or <see langword="null"/>.</param>
internal readonly record struct DeltaTimeTravelSpec(string Path, long? Version, DateTimeOffset? Timestamp);
