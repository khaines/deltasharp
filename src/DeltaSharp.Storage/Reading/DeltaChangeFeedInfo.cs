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
public readonly record struct DeltaChangeFeedInfo(long StartVersion, long EndVersion, StructType Schema);
