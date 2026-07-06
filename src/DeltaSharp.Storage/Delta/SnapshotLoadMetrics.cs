namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Observability for a single snapshot load (design §2.10.4 "replay depth, checkpoint age, action counts
/// surfaced as metrics"; STORY-05.2.2 AC4). Lets a caller see whether a checkpoint was used and how much
/// JSON was replayed, so a slow open (deep replay / stale checkpoint) is diagnosable.
/// </summary>
internal sealed record SnapshotLoadMetrics(
    long? CheckpointVersion,
    int ReplayedCommitCount,
    int ActiveFileCount,
    TimeSpan LoadDuration)
{
    /// <summary>Metrics for a load that used no checkpoint and replayed no commits (an empty/initial state).</summary>
    public static SnapshotLoadMetrics Empty { get; } =
        new(CheckpointVersion: null, ReplayedCommitCount: 0, ActiveFileCount: 0, LoadDuration: TimeSpan.Zero);
}
