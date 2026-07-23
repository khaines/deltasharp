using System.Collections.Immutable;
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
    /// The effective <c>&lt;N&gt;.json</c>-mtime (epoch millis) for every version in
    /// <c>[<see cref="StartVersion"/>, <see cref="EndVersion"/>]</c>, PINNED at resolve time by
    /// <see cref="DeltaReadSource.LoadChangeFeedAsync"/> (item 4 / query-exec L2). <c>_commit_timestamp</c> is
    /// stamped from THIS map at read time so an intervening log-cleanup — which can advance the earliest
    /// reconstructable floor between resolve and read — cannot shift a near-floor version's stamped timestamp
    /// (the versions and rows are already pinned; this pins the timestamp lane too, §2.8). It is
    /// <see langword="internal"/> — it is engine plumbing, not part of the public resolved identity — and is
    /// DELIBERATELY excluded from <see cref="Equals(DeltaChangeFeedInfo)"/> / <see cref="GetHashCode"/> so two
    /// infos with the same <c>[start, end]</c> + schema remain equal regardless of the pinned map instance. It
    /// is <see langword="null"/> only for an info built via the public constructor (no resolve step), in which
    /// case <see cref="DeltaReadSource.ReadChangeBatchesAsync"/> falls back to deriving the timestamps from the
    /// current log.
    /// </summary>
    internal ImmutableSortedDictionary<long, long>? PinnedCommitMillis { get; init; }

    /// <summary>
    /// Value equality over the PUBLIC resolved identity only — <see cref="StartVersion"/>,
    /// <see cref="EndVersion"/> and <see cref="Schema"/>. The internal <see cref="PinnedCommitMillis"/> carrier
    /// is excluded so equality matches the documented contract (a resolved info and an equivalent
    /// user-constructed info compare equal even though only the former carries a pinned map).
    /// </summary>
    public bool Equals(DeltaChangeFeedInfo other) =>
        StartVersion == other.StartVersion
        && EndVersion == other.EndVersion
        && EqualityComparer<StructType>.Default.Equals(Schema, other.Schema);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(StartVersion, EndVersion, EqualityComparer<StructType>.Default.GetHashCode(Schema!));
}
