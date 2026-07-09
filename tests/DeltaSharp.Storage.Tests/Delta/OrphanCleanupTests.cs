using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Tests for the <see cref="OrphanCleanup"/> contract (design §2.11.5, STORY-05.3.2 AC2/AC4): which
/// discovered candidate files VACUUM may delete. Active files and retention-protected files (recently
/// removed, or recently staged) are always excluded; only true orphans past the retention window are
/// returned. The contract is fail-safe — a boundary or unknown-provenance case is retained.
/// </summary>
public sealed class OrphanCleanupTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public OrphanCleanupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orphan-tests-" + Guid.NewGuid().ToString("N"));
        _backend = new LocalFileSystemBackend(_root);
    }

    public void Dispose()
    {
        _backend.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // Builds a snapshot with one active file ("active.parquet") and one tombstone ("removed.parquet",
    // deletionTimestamp = 1 via the harness Remove builder).
    private async Task<Snapshot> BuildSnapshotAsync()
    {
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 1, DeltaTestHarness.Add("active.parquet"), DeltaTestHarness.Add("removed.parquet"));
        await DeltaTestHarness.WriteCommitAsync(_backend, 2, DeltaTestHarness.Remove("removed.parquet"));
        return await new DeltaLog(_backend).LoadSnapshotAsync();
    }

    [Fact]
    public async Task NullDeletionTimestampTombstone_IsAlwaysProtected()
    {
        // Fail-safe: a tombstone with an UNKNOWN deletion time (§2.11.5) is never deletable — protected at
        // any cutoff, including the maximum. Uses a raw remove line with no deletionTimestamp so the parsed
        // RemoveFileAction.DeletionTimestamp is null.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("x.parquet"));
        await DeltaTestHarness.WriteCommitAsync(_backend, 2, """{"remove":{"path":"x.parquet","dataChange":true}}""");
        Snapshot snapshot = await new DeltaLog(_backend).LoadSnapshotAsync();

        Assert.Empty(OrphanCleanup.SelectDeletable(
            snapshot, new[] { new OrphanCandidate("x.parquet", ModificationTimeMillis: 0) }, retentionCutoffMillis: long.MaxValue));
    }

    [Fact]
    public async Task StagedFileExactlyAtCutoff_IsProtected()
    {
        // Boundary: a candidate whose modification time equals the cutoff is within the retention window
        // (>= cutoff) and must be protected — it may belong to an in-flight commit.
        Snapshot snapshot = await BuildSnapshotAsync();

        Assert.Empty(OrphanCleanup.SelectDeletable(
            snapshot, new[] { new OrphanCandidate("edge.parquet", ModificationTimeMillis: 100) }, retentionCutoffMillis: 100));
    }

    [Fact]
    public async Task FileBothActiveAndTombstoned_IsProtected()
    {
        // A file removed then re-added is active again (and also carries a tombstone). Active-first ordering
        // must protect it regardless of the tombstone.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("readd.parquet"));
        await DeltaTestHarness.WriteCommitAsync(_backend, 2, DeltaTestHarness.Remove("readd.parquet"));
        await DeltaTestHarness.WriteCommitAsync(_backend, 3, DeltaTestHarness.Add("readd.parquet"));
        Snapshot snapshot = await new DeltaLog(_backend).LoadSnapshotAsync();

        Assert.Contains("readd.parquet", snapshot.ActiveFiles.Select(a => a.Path)); // active again
        Assert.Empty(OrphanCleanup.SelectDeletable(
            snapshot, new[] { new OrphanCandidate("readd.parquet", ModificationTimeMillis: 0) }, retentionCutoffMillis: 1000));
    }

    [Fact]
    public async Task ActiveFile_IsNeverDeletable()
    {
        Snapshot snapshot = await BuildSnapshotAsync();
        IReadOnlyList<string> deletable = OrphanCleanup.SelectDeletable(
            snapshot,
            new[] { new OrphanCandidate("active.parquet", ModificationTimeMillis: 0) },
            retentionCutoffMillis: 100);

        Assert.Empty(deletable);
    }

    [Fact]
    public async Task RetentionProtectedTombstone_IsNotDeletable_ButExpiredTombstoneIs()
    {
        Snapshot snapshot = await BuildSnapshotAsync(); // removed.parquet tombstoned at deletionTimestamp = 1

        // Cutoff 1: tombstone (1) >= 1 → still within retention → protected.
        Assert.Empty(OrphanCleanup.SelectDeletable(
            snapshot, new[] { new OrphanCandidate("removed.parquet", 0) }, retentionCutoffMillis: 1));

        // Cutoff 2: tombstone (1) < 2 → retention-expired → deletable.
        Assert.Equal(
            new[] { "removed.parquet" },
            OrphanCleanup.SelectDeletable(
                snapshot, new[] { new OrphanCandidate("removed.parquet", 0) }, retentionCutoffMillis: 2).ToArray());
    }

    [Fact]
    public async Task Orphan_PastRetention_IsDeletable_ButRecentlyStagedIsProtected()
    {
        Snapshot snapshot = await BuildSnapshotAsync();

        IReadOnlyList<string> deletable = OrphanCleanup.SelectDeletable(
            snapshot,
            new[]
            {
                new OrphanCandidate("old-orphan.parquet", ModificationTimeMillis: 10),  // < cutoff → deletable
                new OrphanCandidate("staged-now.parquet", ModificationTimeMillis: 500), // >= cutoff → protected (in-flight)
            },
            retentionCutoffMillis: 100);

        Assert.Equal(new[] { "old-orphan.parquet" }, deletable.ToArray());
    }

    [Fact]
    public async Task SelectsOnlyDeletable_AcrossMixedCandidateSet()
    {
        Snapshot snapshot = await BuildSnapshotAsync();

        IReadOnlyList<string> deletable = OrphanCleanup.SelectDeletable(
            snapshot,
            new[]
            {
                new OrphanCandidate("active.parquet", 0),      // active → protected
                new OrphanCandidate("removed.parquet", 0),     // tombstone (1) < cutoff 100 → expired → deletable
                new OrphanCandidate("old-orphan.parquet", 10), // orphan < cutoff → deletable
                new OrphanCandidate("staged-now.parquet", 500),// recent → protected
            },
            retentionCutoffMillis: 100);

        Assert.Equal(
            new[] { "old-orphan.parquet", "removed.parquet" },
            deletable.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task EncodedActiveLogPath_ProtectsUnencodedDiskCandidate()
    {
        // CRITICAL-1 (data-loss): a Spark/Delta-protocol table URI-encodes log paths — the active file
        // literally named "spark active.parquet" on disk is recorded as "spark%20active.parquet" in the
        // log. A directory listing yields the RAW disk key ("spark active.parquet"). Matching only the raw
        // log path would classify the still-active file as an orphan and delete it. The contract must treat
        // the candidate as REFERENCED via the raw ∪ URI-decoded union of the log path.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("spark%20active.parquet"));
        Snapshot snapshot = await new DeltaLog(_backend).LoadSnapshotAsync();

        // mtime 0 is well below the cutoff, so absent the encoding fix this would be classified deletable.
        Assert.Empty(OrphanCleanup.SelectDeletable(
            snapshot,
            new[] { new OrphanCandidate("spark active.parquet", ModificationTimeMillis: 0) },
            retentionCutoffMillis: 1000));
    }

    [Fact]
    public async Task EncodedTombstoneWithinRetention_ProtectsUnencodedDiskCandidate()
    {
        // CRITICAL-1 companion: an encoded tombstone path within the retention window must protect the
        // raw-keyed disk candidate too (a stale reader may still read it), via the same raw ∪ decoded union.
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("enc%20removed.parquet"));
        await DeltaTestHarness.WriteCommitAsync(_backend, 2, DeltaTestHarness.Remove("enc%20removed.parquet"));
        Snapshot snapshot = await new DeltaLog(_backend).LoadSnapshotAsync();

        // Tombstone deletionTimestamp = 1 (harness); cutoff 1 → (1 >= 1) within retention → protected.
        Assert.Empty(OrphanCleanup.SelectDeletable(
            snapshot,
            new[] { new OrphanCandidate("enc removed.parquet", ModificationTimeMillis: 0) },
            retentionCutoffMillis: 1));
    }
}
