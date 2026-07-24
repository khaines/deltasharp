using System.Collections.Immutable;
using System.Globalization;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The retention configuration VACUUM (design §2.14 / §2.12.1, STORY-05.6.2) enforces before it reclaims
/// any file: a <see cref="DefaultRetention"/> applied when a caller does not name an explicit window, and a
/// <see cref="SafetyThreshold"/> — the <b>minimum</b> retention VACUUM will honor without an explicit unsafe
/// override. The threshold exists because a too-short retention is the highest-severity data-loss class:
/// deleting a file a stale reader or a recent tombstone still needs corrupts historical/in-progress readers
/// (design §3.6 safety oracle; collaborator angle: retention/erasure safety for stale readers). It mirrors
/// Delta's <c>delta.deletedFileRetentionDuration</c> default (7 days / 168 h) and its
/// <c>retentionDurationCheck</c> guard.
/// </summary>
/// <remarks>
/// The two windows are independent knobs. <see cref="DefaultRetention"/> is what an operator gets when they
/// call VACUUM with no argument; <see cref="SafetyThreshold"/> is the floor a caller may not silently go
/// under. By default they are equal (168 h): the out-of-the-box behavior retains a week of deleted-file
/// history, and requesting anything shorter fails closed unless the caller explicitly opts into the unsafe
/// override. Both are validated non-negative at construction.
/// </remarks>
internal sealed record RetentionPolicy
{
    /// <summary>Delta's default deleted-file retention: 7 days (168 hours).</summary>
    internal static readonly TimeSpan DefaultRetentionWindow = TimeSpan.FromHours(168);

    /// <summary>Delta's default log retention: 30 days. Bounds the window of retained commit JSONs (and
    /// therefore the <c>cdc</c> <c>_change_data/</c> files knowable only from those commits, #489). Mirrors
    /// Delta's <c>delta.logRetentionDuration</c> default.</summary>
    internal static readonly TimeSpan DefaultLogRetentionWindow = TimeSpan.FromDays(30);

    /// <summary>The process-wide default policy: a 168-hour default retention with a 168-hour safety floor.</summary>
    internal static RetentionPolicy Default { get; } = new(DefaultRetentionWindow, DefaultRetentionWindow);

    /// <summary>Creates a policy, validating both windows are non-negative and the default is not below the
    /// safety threshold (a default that is itself unsafe would reject every no-argument VACUUM).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Either window is negative, or
    /// <paramref name="defaultRetention"/> is below <paramref name="safetyThreshold"/>.</exception>
    internal RetentionPolicy(TimeSpan defaultRetention, TimeSpan safetyThreshold)
    {
        if (defaultRetention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultRetention), defaultRetention, "Default retention must be non-negative.");
        }

        if (safetyThreshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(safetyThreshold), safetyThreshold, "Safety threshold must be non-negative.");
        }

        if (defaultRetention < safetyThreshold)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultRetention),
                defaultRetention,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Default retention {defaultRetention} must not be below the safety threshold {safetyThreshold}."));
        }

        DefaultRetention = defaultRetention;
        SafetyThreshold = safetyThreshold;
    }

    /// <summary>The retention window applied when a VACUUM caller does not name an explicit one.</summary>
    internal TimeSpan DefaultRetention { get; }

    /// <summary>The minimum retention a VACUUM caller may request without enabling the unsafe override; a
    /// shorter window is rejected fail-closed (STORY-05.6.2 AC2).</summary>
    internal TimeSpan SafetyThreshold { get; }

    /// <summary>The Delta table property naming the deleted-file retention window a no-argument VACUUM must
    /// honor (design §2.14; Delta <c>delta.deletedFileRetentionDuration</c>). Read from
    /// <see cref="MetadataAction.Configuration"/> so a table configured for (say) 30 days does not silently
    /// lose history after the 7-day process default.</summary>
    internal const string DeletedFileRetentionDurationKey = "delta.deletedFileRetentionDuration";

    /// <summary>
    /// Resolves the effective retention for a no-argument VACUUM from a table's
    /// <see cref="MetadataAction.Configuration"/>: the parsed <see cref="DeletedFileRetentionDurationKey"/>
    /// when present and valid, else this policy's <see cref="DefaultRetention"/>. Fail-closed: a property
    /// that is present but <b>unparseable</b> (or expresses a calendar unit — months/years — whose length is
    /// not fixed) throws rather than silently falling back to a shorter default and under-retaining history.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    /// <exception cref="FormatException">The property is present but cannot be parsed to a fixed duration.</exception>
    internal TimeSpan ResolveTableRetention(ImmutableSortedDictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.TryGetValue(DeletedFileRetentionDurationKey, out string? raw))
        {
            return DefaultRetention;
        }

        if (!TryParseRetentionInterval(raw, out TimeSpan configured))
        {
            throw new FormatException(string.Create(
                CultureInfo.InvariantCulture,
                $"Table property '{DeletedFileRetentionDurationKey}' value '{raw}' is not a parseable fixed-length " +
                $"retention duration; VACUUM fails closed rather than under-retaining deleted-file history."));
        }

        return configured;
    }

    /// <summary>The Delta table property naming the log retention window that bounds how long a table's
    /// commit JSONs are retained (design §2.6; Delta <c>delta.logRetentionDuration</c>, default 30 days).
    /// Read from <see cref="MetadataAction.Configuration"/>. VACUUM uses it to bound the set of retained
    /// commit JSONs it scans for <c>cdc</c> (<c>_change_data/</c>) file paths to protect (#489), because a
    /// <c>cdc</c> action is ignored by snapshot replay (INV C1) and not retained in checkpoints — so its
    /// referenced path is knowable only from an in-window commit JSON.</summary>
    internal const string LogRetentionDurationKey = "delta.logRetentionDuration";

    /// <summary>
    /// Resolves the effective log retention from a table's <see cref="MetadataAction.Configuration"/>: the
    /// parsed <see cref="LogRetentionDurationKey"/> when present and valid, else
    /// <see cref="DefaultLogRetentionWindow"/> (30 days). Fail-closed: a property that is present but
    /// <b>unparseable</b> (or expresses a calendar unit whose length is not fixed) throws rather than
    /// silently falling back to a shorter default and under-protecting in-window <c>cdc</c> files.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    /// <exception cref="FormatException">The property is present but cannot be parsed to a fixed duration.</exception>
    internal TimeSpan ResolveTableLogRetention(ImmutableSortedDictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.TryGetValue(LogRetentionDurationKey, out string? raw))
        {
            return DefaultLogRetentionWindow;
        }

        if (!TryParseRetentionInterval(raw, out TimeSpan configured))
        {
            throw new FormatException(string.Create(
                CultureInfo.InvariantCulture,
                $"Table property '{LogRetentionDurationKey}' value '{raw}' is not a parseable fixed-length " +
                $"retention duration; VACUUM fails closed rather than under-protecting in-window change files."));
        }

        return configured;
    }

    /// <summary>
    /// Parses a Delta <c>CalendarInterval</c>/duration string (e.g. <c>"interval 30 days"</c>,
    /// <c>"7 days"</c>, <c>"interval 1 weeks 12 hours"</c>) into a fixed <see cref="TimeSpan"/>. Accepts an
    /// optional leading <c>interval</c> keyword followed by one or more <c>&lt;number&gt; &lt;unit&gt;</c>
    /// pairs. Calendar units whose length is not fixed (<c>month</c>/<c>year</c>) are <b>rejected</b>
    /// (returns <see langword="false"/>) — mirroring Delta's <c>getMilliSeconds</c>, which asserts zero
    /// months — so VACUUM never derives a retention from an ambiguous-length unit.
    /// </summary>
    internal static bool TryParseRetentionInterval(string? value, out TimeSpan retention)
    {
        retention = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] tokens = value.Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        if (tokens.Length > 0 && string.Equals(tokens[0], "interval", StringComparison.Ordinal))
        {
            i = 1;
        }

        if (i >= tokens.Length || (tokens.Length - i) % 2 != 0)
        {
            return false; // no value/unit pairs, or a dangling token.
        }

        TimeSpan total = TimeSpan.Zero;
        try
        {
            for (; i < tokens.Length; i += 2)
            {
                if (!long.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out long count)
                    || count < 0)
                {
                    return false;
                }

                if (!TryUnitToTimeSpan(tokens[i + 1], count, out TimeSpan part))
                {
                    return false;
                }

                total += part;
            }
        }
        catch (OverflowException)
        {
            // An absurd magnitude (e.g. "interval 10000000 weeks") exceeds the TimeSpan range. Fail closed as
            // "unparseable" (ResolveTableRetention then throws FormatException and VACUUM aborts) rather than
            // letting an OverflowException escape a Try* method.
            return false;
        }

        retention = total;
        return true;
    }

    private static bool TryUnitToTimeSpan(string unit, long count, out TimeSpan value)
    {
        // Trim a trailing plural 's' so "day"/"days" both match. Reject calendar-ambiguous units. Arithmetic
        // uses double operands so an out-of-range magnitude throws OverflowException (caught by the caller and
        // failed closed) instead of silently wrapping a long multiply to a negative window.
        string singular = unit.Length > 1 && unit[^1] == 's' ? unit[..^1] : unit;
        value = singular switch
        {
            "week" => TimeSpan.FromDays(7d * count),
            "day" => TimeSpan.FromDays((double)count),
            "hour" => TimeSpan.FromHours((double)count),
            "minute" => TimeSpan.FromMinutes((double)count),
            "min" => TimeSpan.FromMinutes((double)count),
            "second" => TimeSpan.FromSeconds((double)count),
            "sec" => TimeSpan.FromSeconds((double)count),
            "millisecond" => TimeSpan.FromMilliseconds((double)count),
            "microsecond" => TimeSpan.FromMicroseconds((double)count),
            _ => TimeSpan.MinValue,
        };

        return value != TimeSpan.MinValue;
    }
}
