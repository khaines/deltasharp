using System.Globalization;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The reason a Delta transaction-log read or protocol negotiation failed. Callers branch on
/// <see cref="DeltaProtocolException.Kind"/> rather than parsing messages (design §2.10.5).
/// </summary>
internal enum DeltaProtocolErrorKind
{
    /// <summary>A commit line or checkpoint action was malformed, truncated, or violated the
    /// documented Delta action shape (a corrupt/invalid log — never silently tolerated).</summary>
    MalformedAction,

    /// <summary>The table's <c>protocol</c> action requires a reader/writer version or a named
    /// reader/writer table feature this build does not support. Fail closed — never read past an
    /// unsupported feature (design §2.10.5, checklist <c>Delta log protocol</c> bullet 2).</summary>
    UnsupportedProtocol,

    /// <summary>The reconstructed log was internally inconsistent (e.g. an <c>add</c>/<c>remove</c>
    /// referenced a version out of range, a required <c>metaData</c>/<c>protocol</c> was missing, or a
    /// checkpoint disagreed with JSON replay) — the reader refuses to invent table state.</summary>
    InconsistentLog,

    /// <summary>A time-travel request (by version or timestamp) targets history <b>older than the earliest
    /// retained log</b>: the required <c>&lt;N&gt;.json</c>/checkpoints were removed by log cleanup, so the
    /// state can no longer be reconstructed. Distinct from <see cref="InconsistentLog"/> (an out-of-range
    /// <i>future</i> version, or a genuine gap <i>above</i> the retention floor) — the reader fails closed
    /// with the earliest still-available version rather than silently returning current data (design
    /// §2.12.1; STORY-05.4.1 AC3).</summary>
    RetentionGap,

    /// <summary>A <c>timestampAsOf</c> request targets an instant <b>strictly after the latest commit's</b>
    /// effective timestamp — no such snapshot exists. Mirrors Delta batch reads
    /// (<c>DeltaHistoryManager.getActiveCommitAtTime</c> with <c>canReturnLastCommit=false</c>), which throw
    /// <c>timestampGreaterThanLatestCommit</c> rather than clamping to current data. Kept distinct from
    /// <see cref="RetentionGap"/> (a timestamp before the earliest retained commit); callers may opt into
    /// clamping via <c>canReturnLatest</c> (design §2.12.1).</summary>
    TimestampAfterLatest,

    /// <summary>A commit would delete or change committed data (a <c>remove</c> with <c>dataChange=true</c>,
    /// e.g. DELETE / OVERWRITE) on a table configured append-only (<c>delta.appendOnly=true</c>). Refused
    /// fail-closed (Delta "Append-only Tables"; #549). Distinct from <see cref="UnsupportedProtocol"/> — the
    /// feature IS supported, but the requested operation violates the table's append-only guarantee.</summary>
    AppendOnlyViolation,
}

/// <summary>
/// A versioned Delta transaction-log / protocol error (design §2.10, §2.10.5). Carries the failing
/// <see cref="DeltaProtocolErrorKind"/> and, for <see cref="DeltaProtocolErrorKind.UnsupportedProtocol"/>,
/// the exact reader/writer version or feature name so the failure is precise and actionable — the
/// reader always fails closed on an unsupported or corrupt log rather than silently degrading.
/// </summary>
internal sealed class DeltaProtocolException : Exception
{
    private DeltaProtocolException(DeltaProtocolErrorKind kind, string message, Exception? innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>The classified failure reason.</summary>
    public DeltaProtocolErrorKind Kind { get; }

    /// <summary>A malformed/truncated commit line or checkpoint action.</summary>
    public static DeltaProtocolException Malformed(string message, Exception? innerException = null) =>
        new(DeltaProtocolErrorKind.MalformedAction, message, innerException);

    /// <summary>An unsupported reader/writer protocol version or named table feature (fail closed).</summary>
    public static DeltaProtocolException Unsupported(string message) =>
        new(DeltaProtocolErrorKind.UnsupportedProtocol, message, innerException: null);

    /// <summary>A commit that changes committed data on an append-only table (<c>delta.appendOnly=true</c>),
    /// refused fail-closed (#549).</summary>
    public static DeltaProtocolException AppendOnly(string message) =>
        new(DeltaProtocolErrorKind.AppendOnlyViolation, message, innerException: null);

    /// <summary>Builds an <see cref="DeltaProtocolErrorKind.UnsupportedProtocol"/> error naming the
    /// unsupported reader/writer version (design §2.10.5 protocol negotiation).</summary>
    public static DeltaProtocolException UnsupportedVersion(
        string role, int required, int supported) =>
        Unsupported(string.Create(
            CultureInfo.InvariantCulture,
            $"The table requires Delta {role} version {required} but this build supports up to {supported}. The table cannot be read safely."));

    /// <summary>Builds an <see cref="DeltaProtocolErrorKind.UnsupportedProtocol"/> error naming the
    /// unsupported reader/writer table feature(s).</summary>
    public static DeltaProtocolException UnsupportedFeatures(string role, IEnumerable<string> features) =>
        Unsupported(string.Create(
            CultureInfo.InvariantCulture,
            $"The table requires unsupported Delta {role} feature(s): {string.Join(", ", features)}. The table cannot be read safely."));

    /// <summary>An internally inconsistent reconstructed log.</summary>
    public static DeltaProtocolException Inconsistent(string message, Exception? innerException = null) =>
        new(DeltaProtocolErrorKind.InconsistentLog, message, innerException);

    /// <summary>A time-travel target older than the earliest retained log (a log-cleanup retention gap).</summary>
    public static DeltaProtocolException RetentionGap(string message) =>
        new(DeltaProtocolErrorKind.RetentionGap, message, innerException: null);

    /// <summary>Builds a <see cref="DeltaProtocolErrorKind.RetentionGap"/> error for a requested <b>version</b>
    /// that is below the earliest retained version — its <c>&lt;N&gt;.json</c>/checkpoints were log-cleaned, so
    /// the reader fails closed with the earliest version still reconstructable (STORY-05.4.1 AC3).</summary>
    public static DeltaProtocolException VersionNoLongerRetained(long requested, long earliestAvailable) =>
        RetentionGap(string.Create(
            CultureInfo.InvariantCulture,
            $"Delta version {requested} is no longer retained; the earliest available version is {earliestAvailable}. "
            + $"Its log files were removed by log cleanup and the snapshot can no longer be reconstructed."));

    /// <summary>Builds a <see cref="DeltaProtocolErrorKind.RetentionGap"/> error for a requested <b>timestamp</b>
    /// that predates the earliest retained commit — no version's commit timestamp is at or before it, so the
    /// reader fails closed rather than returning the earliest/current state (STORY-05.4.1 AC3).</summary>
    public static DeltaProtocolException TimestampBeforeRetention(
        DateTimeOffset requested, long earliestVersion, DateTimeOffset earliestTimestamp) =>
        RetentionGap(string.Create(
            CultureInfo.InvariantCulture,
            $"The requested timestamp {requested:O} is before the earliest retained Delta commit "
            + $"(version {earliestVersion} at {earliestTimestamp:O}); earlier history was removed by log cleanup "
            + $"and is no longer available for time travel."));

    /// <summary>Builds a <see cref="DeltaProtocolErrorKind.RetentionGap"/> error for a requested <b>timestamp</b>
    /// that predates the table's <b>first commit</b> (version 0 is still retained, so this is genuine table
    /// creation — not log cleanup). Mirrors Delta's <c>timestampEarlierThanTableFirstCommit</c>; kept fail
    /// closed (the reader never returns the earliest/current state).</summary>
    public static DeltaProtocolException TimestampBeforeFirstCommit(
        DateTimeOffset requested, DateTimeOffset firstCommitTimestamp) =>
        RetentionGap(string.Create(
            CultureInfo.InvariantCulture,
            $"The requested timestamp {requested:O} is before the table's first commit "
            + $"(version 0 at {firstCommitTimestamp:O}); no such snapshot exists."));

    /// <summary>Builds a <see cref="DeltaProtocolErrorKind.TimestampAfterLatest"/> error for a requested
    /// <b>timestamp</b> strictly after the latest commit's effective timestamp — no such snapshot exists.
    /// Mirrors Delta batch reads (<c>canReturnLastCommit=false</c>), which throw rather than clamp; the caller
    /// can opt into clamping to the latest version via <c>canReturnLatest</c> (STORY-05.4.1, design §2.12.1).</summary>
    public static DeltaProtocolException TimestampAfterLatest(
        DateTimeOffset requested, long latestVersion, DateTimeOffset latestTimestamp) =>
        new(DeltaProtocolErrorKind.TimestampAfterLatest, string.Create(
            CultureInfo.InvariantCulture,
            $"The requested timestamp {requested:O} is after the latest Delta commit "
            + $"(version {latestVersion} at {latestTimestamp:O}); no such snapshot exists."), innerException: null);
}
