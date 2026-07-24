using DeltaSharp.Types;

namespace DeltaSharp.Storage;

/// <summary>
/// The resolved identity of a Change Data Feed read (design §2.6/§2.9) — the counterpart of
/// <see cref="DeltaSnapshotInfo"/> for the snapshot door. <see cref="DeltaReadSource.LoadChangeFeedAsync"/>
/// resolves a <see cref="DeltaChangeFeedRange"/> ONCE (endpoint resolution + full range validation, so the
/// resolved range is <b>pinned</b> against a concurrent commit shifting it between analysis and execution),
/// and <see cref="DeltaReadSource.ReadChangeBatchesAsync"/> later replays exactly this
/// <c>[<see cref="StartVersion"/>, <see cref="EndVersion"/>]</c> (inclusive) into full-schema change batches.
/// </summary>
/// <param name="StartVersion">The resolved inclusive start version (a version endpoint verbatim; a timestamp
/// endpoint rounded UP to the first commit at/after it).</param>
/// <param name="EndVersion">The resolved inclusive end version (a version endpoint verbatim; a timestamp
/// endpoint rounded DOWN to the last commit at/before it; the latest committed version when no end was set).</param>
/// <param name="Schema">The reconciled CDF output schema (§2.4/§2.8): the table's data columns at
/// <see cref="EndVersion"/> followed by the three engine-synthesized metadata columns
/// <c>_change_type</c> (string), <c>_commit_version</c> (long) and <c>_commit_timestamp</c> (timestamp), in
/// that order. Every batch <see cref="DeltaReadSource.ReadChangeBatchesAsync"/> yields carries this schema.</param>
public readonly record struct DeltaChangeFeedInfo(long StartVersion, long EndVersion, StructType Schema)
{
    /// <summary>
    /// The non-forgeable resolution proof — the evidence that this info was produced by
    /// <see cref="DeltaReadSource.LoadChangeFeedAsync"/>, i.e. that its range passed the full resolve-time
    /// validation (bounds, availability, and the §2.7 conservative CDF-enablement gate) — carrying the pinned
    /// effective-commit-millis map used to stamp <c>_commit_timestamp</c> (item 4 / query-exec L2, §2.8).
    /// <see cref="DeltaReadSource.LoadChangeFeedAsync"/> mints it; it is <see langword="null"/> for an info
    /// built via the public constructor or <c>default</c>, in which case
    /// <see cref="DeltaReadSource.ReadChangeBatchesAsync"/> fails closed BEFORE any I/O rather than reading an
    /// unvalidated range (a forged info can never bypass range/CDF-enablement validation). It is
    /// <see langword="internal"/> — engine plumbing, not part of the public resolved identity — and is
    /// DELIBERATELY excluded from <see cref="Equals(DeltaChangeFeedInfo)"/> / <see cref="GetHashCode"/> so two
    /// infos with the same <c>[start, end]</c> + schema remain equal regardless of the resolution instance.
    /// </summary>
    internal ChangeFeedResolution? Resolution { get; init; }

    /// <summary>
    /// Value equality over the PUBLIC resolved identity only — <see cref="StartVersion"/>,
    /// <see cref="EndVersion"/> and <see cref="Schema"/>. The internal <see cref="Resolution"/> carrier
    /// is excluded so equality matches the documented contract (a resolved info and an equivalent
    /// user-constructed info compare equal even though only the former carries a resolution proof).
    /// </summary>
    public bool Equals(DeltaChangeFeedInfo other) =>
        StartVersion == other.StartVersion
        && EndVersion == other.EndVersion
        && EqualityComparer<StructType>.Default.Equals(Schema, other.Schema);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(StartVersion, EndVersion, EqualityComparer<StructType>.Default.GetHashCode(Schema!));
}
