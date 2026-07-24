namespace DeltaSharp.Storage;

/// <summary>
/// The requested bounds of a Change Data Feed read (design §2.6/§2.9) — an inclusive
/// <c>[start, end]</c> version range, each endpoint expressed <b>independently</b> as either a
/// <b>version</b> or a <b>timestamp</b>. It is the CDF counterpart of the snapshot door's
/// <c>versionAsOf</c>/<c>timestampAsOf</c> inputs and, like <see cref="DeltaSnapshotInfo"/>/<c>DeltaWriteResult</c>,
/// a <see cref="System.Runtime.CompilerServices.IsReadOnlyAttribute">readonly</see> record struct with value semantics.
///
/// <para><b>Per-endpoint rule (mirrors <see cref="DeltaReadSource.LoadSnapshotAsync"/>).</b> A <i>single</i>
/// endpoint may carry a version <b>xor</b> a timestamp, never both: setting both
/// <see cref="StartingVersion"/> and <see cref="StartingTimestamp"/> (or both <see cref="EndingVersion"/> and
/// <see cref="EndingTimestamp"/>) is rejected by <see cref="DeltaReadSource.LoadChangeFeedAsync"/> with an
/// <see cref="System.ArgumentException"/>. Mixing <i>across</i> endpoints (e.g. <see cref="StartingVersion"/>
/// plus <see cref="EndingTimestamp"/>) is <b>allowed</b> — Spark parity.</para>
///
/// <para><b>Start is required; end defaults to latest.</b> A read must carry a start bound
/// (<see cref="StartingVersion"/> xor <see cref="StartingTimestamp"/>); omitting both is rejected. Omitting
/// <b>both</b> end bounds defaults the end to the latest committed version at resolution time.</para>
/// </summary>
public readonly record struct DeltaChangeFeedRange
{
    /// <summary>The inclusive start version, or <see langword="null"/> to resolve the start from
    /// <see cref="StartingTimestamp"/>. Setting both this and <see cref="StartingTimestamp"/> is rejected.</summary>
    public long? StartingVersion { get; init; }

    /// <summary>The inclusive start timestamp (resolved to the FIRST commit whose effective timestamp is at or
    /// after it — round <b>up</b>, Spark parity), or <see langword="null"/> to use <see cref="StartingVersion"/>.
    /// Setting both this and <see cref="StartingVersion"/> is rejected.</summary>
    public DateTimeOffset? StartingTimestamp { get; init; }

    /// <summary>The inclusive end version, or <see langword="null"/> to resolve the end from
    /// <see cref="EndingTimestamp"/> — or, when both end bounds are null, to default to the latest committed
    /// version. Setting both this and <see cref="EndingTimestamp"/> is rejected.</summary>
    public long? EndingVersion { get; init; }

    /// <summary>The inclusive end timestamp (resolved to the LAST commit whose effective timestamp is at or
    /// before it — round <b>down</b>, matching <c>timestampAsOf</c> time travel), or <see langword="null"/>.
    /// Setting both this and <see cref="EndingVersion"/> is rejected.</summary>
    public DateTimeOffset? EndingTimestamp { get; init; }

    /// <summary>A version-addressed range: from inclusive <paramref name="startingVersion"/> to inclusive
    /// <paramref name="endingVersion"/> (or the latest committed version when <see langword="null"/>).</summary>
    /// <param name="startingVersion">The inclusive start version.</param>
    /// <param name="endingVersion">The inclusive end version, or <see langword="null"/> for latest.</param>
    public static DeltaChangeFeedRange FromVersion(long startingVersion, long? endingVersion = null) =>
        new() { StartingVersion = startingVersion, EndingVersion = endingVersion };

    /// <summary>A timestamp-addressed range: from inclusive <paramref name="startingTimestamp"/> to inclusive
    /// <paramref name="endingTimestamp"/> (or the latest committed version when <see langword="null"/>).</summary>
    /// <param name="startingTimestamp">The inclusive start timestamp (resolved round-up).</param>
    /// <param name="endingTimestamp">The inclusive end timestamp (resolved round-down), or <see langword="null"/>.</param>
    public static DeltaChangeFeedRange FromTimestamp(
        DateTimeOffset startingTimestamp, DateTimeOffset? endingTimestamp = null) =>
        new() { StartingTimestamp = startingTimestamp, EndingTimestamp = endingTimestamp };
}
