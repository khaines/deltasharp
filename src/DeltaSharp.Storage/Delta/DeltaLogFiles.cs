using System.Globalization;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Classifies a <c>_delta_log</c> file name into the Delta artifacts a reader cares about (design
/// §2.10.2/§2.10.3): JSON commits (<c>&lt;v&gt;.json</c>), classic checkpoints
/// (<c>&lt;v&gt;.checkpoint.parquet</c> and multi-part <c>&lt;v&gt;.checkpoint.&lt;p&gt;.&lt;n&gt;.parquet</c>),
/// and V2/UUID checkpoints (<c>&lt;v&gt;.checkpoint.&lt;uuid&gt;.{parquet,json}</c>). All version fields are
/// the fixed 20-digit zero-padded form. Parsing is purely lexical and total: an unrecognized name is
/// <see cref="DeltaLogFileKind.Other"/> and ignored, never guessed at.
/// </summary>
internal static class DeltaLogFiles
{
    internal const int VersionDigits = 20;

    public static DeltaLogFile Classify(string fileName)
    {
        // Commit: <20-digit>.json
        if (fileName.Length == VersionDigits + 5
            && fileName.EndsWith(".json", StringComparison.Ordinal)
            && TryParseVersion(fileName.AsSpan(0, VersionDigits), out long commitVersion))
        {
            return DeltaLogFile.Commit(commitVersion);
        }

        // Every checkpoint is "<20-digit>.checkpoint.<rest>".
        const string marker = ".checkpoint.";
        if (fileName.Length <= VersionDigits + marker.Length
            || !TryParseVersion(fileName.AsSpan(0, VersionDigits), out long checkpointVersion)
            || !fileName.AsSpan(VersionDigits, marker.Length).SequenceEqual(marker))
        {
            return DeltaLogFile.Other();
        }

        ReadOnlySpan<char> rest = fileName.AsSpan(VersionDigits + marker.Length);

        // Single-part classic: "<v>.checkpoint.parquet" → rest is exactly "parquet".
        if (rest.SequenceEqual("parquet"))
        {
            return DeltaLogFile.ClassicCheckpoint(checkpointVersion, part: 1, parts: 1);
        }

        if (rest.EndsWith(".parquet", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> tokens = rest[..^".parquet".Length];
            int dot = tokens.IndexOf('.');

            // Multi-part classic: "<p-digits>.<n-digits>.parquet".
            if (dot > 0
                && TryParseVersion(tokens[..dot], out long part)
                && TryParseVersion(tokens[(dot + 1)..], out long parts)
                && part >= 1 && parts >= 1 && part <= parts)
            {
                return DeltaLogFile.ClassicCheckpoint(checkpointVersion, (int)part, (int)parts);
            }

            // A single non-numeric token (a UUID) before .parquet is a V2 checkpoint top file.
            if (dot < 0)
            {
                return DeltaLogFile.V2Checkpoint(checkpointVersion);
            }
        }

        // "<uuid>.json" V2 checkpoint top file (single token before the extension).
        if (rest.EndsWith(".json", StringComparison.Ordinal)
            && rest.IndexOf('.') == rest.Length - ".json".Length)
        {
            return DeltaLogFile.V2Checkpoint(checkpointVersion);
        }

        return DeltaLogFile.Other();
    }

    private static bool TryParseVersion(ReadOnlySpan<char> digits, out long version)
    {
        version = 0;
        if (digits.Length == 0)
        {
            return false;
        }

        foreach (char c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}

/// <summary>The kind of a classified <c>_delta_log</c> file.</summary>
internal enum DeltaLogFileKind
{
    /// <summary>Not a recognized Delta artifact — ignored.</summary>
    Other,

    /// <summary>A JSON commit <c>&lt;v&gt;.json</c>.</summary>
    Commit,

    /// <summary>A classic checkpoint part (single- or multi-part).</summary>
    ClassicCheckpoint,

    /// <summary>A V2/UUID checkpoint top file (accepted only under the <c>v2Checkpoint</c> reader feature).</summary>
    V2Checkpoint,
}

/// <summary>A classified <c>_delta_log</c> file: its <see cref="Kind"/>, <see cref="Version"/>, and — for a
/// classic checkpoint — its 1-based <see cref="Part"/> of <see cref="Parts"/>.</summary>
internal readonly record struct DeltaLogFile(DeltaLogFileKind Kind, long Version, int Part, int Parts)
{
    public static DeltaLogFile Other() => new(DeltaLogFileKind.Other, 0, 0, 0);

    public static DeltaLogFile Commit(long version) => new(DeltaLogFileKind.Commit, version, 0, 0);

    public static DeltaLogFile ClassicCheckpoint(long version, int part, int parts) =>
        new(DeltaLogFileKind.ClassicCheckpoint, version, part, parts);

    public static DeltaLogFile V2Checkpoint(long version) => new(DeltaLogFileKind.V2Checkpoint, version, 0, 0);
}
