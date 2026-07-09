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
}
