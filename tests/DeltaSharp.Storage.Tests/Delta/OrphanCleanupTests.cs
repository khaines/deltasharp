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
}
