using System.Collections.Immutable;
using System.Threading;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// End-to-end optimistic-concurrency tests for <see cref="DeltaCommitter"/> over a real
/// <see cref="LocalFileSystemBackend"/> (design §2.11, STORY-05.3.1 AC1–AC3): exactly one writer wins each
/// version, unsafe commits are rejected before publishing, and append-compatible writers rebase without
/// duplicating data. Ambiguous-ack recovery (AC4) is covered in <see cref="DeltaCommitAmbiguityTests"/>.
/// </summary>
public sealed class DeltaCommitterTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public DeltaCommitterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deltacommit-tests-" + Guid.NewGuid().ToString("N"));
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

    private static readonly ImmutableSortedDictionary<string, string?> NoPartition =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    private static readonly ImmutableSortedDictionary<string, string> NoTags =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private static AddFileAction Add(string path) =>
        new(path, NoPartition, 1L, 1L, DataChange: true, Stats: null, Tags: NoTags);

    private static RemoveFileAction Remove(string path) =>
        new(path, DeletionTimestamp: 1L, DataChange: true, ExtendedFileMetadata: false, NoPartition, Size: null);

    private async Task SeedTableAsync(int minWriter = 2)
    {
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.Protocol(minReader: 1, minWriter: minWriter), DeltaTestHarness.Metadata());
    }

    private Task<Snapshot> LoadAsync(long? version = null) => new DeltaLog(_backend).LoadSnapshotAsync(version);

    private async Task CommitRawAsync(long version, params string[] lines) =>
        await DeltaTestHarness.WriteCommitAsync(_backend, version, lines);

    [Fact]
    public async Task CommitsBlindAppend_AdvancingVersion_ReadYourWrites()
    {
        // AC (happy path): a blind append advances 0 → 1 on the first attempt and is immediately visible.
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync();

        DeltaCommitResult result = await new DeltaCommitter(_backend)
            .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(1L, result.Version);
        Assert.Equal(1, result.Attempts);

        Snapshot reloaded = await LoadAsync();
        Assert.Equal(1L, reloaded.Version);
        Assert.Equal("part-0.parquet", Assert.Single(reloaded.ActiveFiles).Path);
    }

    [Fact]
    public async Task ConcurrentBlindAppends_ExactlyOneWinsEachVersion_WithoutDuplication()
    {
        // AC1 + AC3: two blind appends race for the same next version; one wins v1, the loser observes a
        // retryable conflict, rebases onto v2, and commits — both files land exactly once, none lost.
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync();

        using var barrier = new Barrier(2);
        Func<int, long, CancellationToken, Task> gate = (attempt, _, ct) =>
        {
            if (attempt == 0 && !barrier.SignalAndWait(TimeSpan.FromSeconds(30), ct))
            {
                // Bounded: if a peer writer fails before the barrier, surface a fast timeout instead of
                // hanging the test until the host harness kills it.
                throw new TimeoutException("Race barrier: a peer writer did not reach the put-if-absent in time.");
            }

            return Task.CompletedTask;
        };

        var committerA = new DeltaCommitter(_backend) { BeforePutProbe = gate };
        var committerB = new DeltaCommitter(_backend) { BeforePutProbe = gate };

        Task<DeltaCommitResult> taskA = Task.Run(() =>
            committerA.CommitAsync(snapshot, new DeltaAction[] { Add("a.parquet") }, DeltaReadScope.BlindAppend));
        Task<DeltaCommitResult> taskB = Task.Run(() =>
            committerB.CommitAsync(snapshot, new DeltaAction[] { Add("b.parquet") }, DeltaReadScope.BlindAppend));

        DeltaCommitResult[] results = await Task.WhenAll(taskA, taskB);

        // Exactly one commit at each of v1 and v2.
        Assert.Equal(new[] { 1L, 2L }, results.Select(r => r.Version).OrderBy(v => v).ToArray());
        // The winner committed on attempt 1; the loser rebased and committed on attempt 2.
        Assert.Equal(new[] { 1, 2 }, results.Select(r => r.Attempts).OrderBy(a => a).ToArray());

        // Both files are present in the final snapshot — no duplication, no loss.
        Snapshot reloaded = await LoadAsync();
        Assert.Equal(2L, reloaded.Version);
        Assert.Equal(new[] { "a.parquet", "b.parquet" }, reloaded.ActiveFiles.Select(a => a.Path).OrderBy(p => p).ToArray());
    }

    [Fact]
    public async Task RebasesPastMultipleConcurrentWinners_InOneWinnersPass()
    {
        // Two safe winners (v1, v2) landed since the read snapshot; a blind append reads BOTH in one winners
        // pass and rebases to v3 in a SINGLE rebase — so it wins on its 2nd attempt. (Reading only the first
        // winner per pass would need an extra attempt, so asserting Attempts=2 pins the whole (R,M] range read.)
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync(); // v0
        await CommitRawAsync(1, DeltaTestHarness.Add("a.parquet"));
        await CommitRawAsync(2, DeltaTestHarness.Add("b.parquet"));

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot, new DeltaAction[] { Add("c.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(3L, result.Version);
        Assert.Equal(2, result.Attempts); // one lost put + one winning put after a single (R,M]=2 rebase

        Snapshot reloaded = await LoadAsync();
        Assert.Equal(
            new[] { "a.parquet", "b.parquet", "c.parquet" },
            reloaded.ActiveFiles.Select(a => a.Path).OrderBy(p => p).ToArray());
    }

    [Fact]
    public async Task DetectsConflictInLaterWinner_AcrossMultiVersionRange()
    {
        // Winners v1 (safe append) + v2 (metadata change): the loser must classify the WHOLE (R,M] range and
        // abort on the v2 metadata change — proving multi-winner classification, not just the first winner.
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync(); // v0
        await CommitRawAsync(1, DeltaTestHarness.Add("a.parquet"));
        await CommitRawAsync(2, DeltaTestHarness.Metadata(id: "changed"));

        await Assert.ThrowsAsync<MetadataChangedException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot, new DeltaAction[] { Add("late.parquet") }, DeltaReadScope.BlindAppend));
    }

    [Fact]
    public async Task BlindAppend_AbortsOnConcurrentMetadataChange()
    {
        // AC2: an intervening metadata change is rejected before publishing (blind append vs metaData).
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync();
        await CommitRawAsync(1, DeltaTestHarness.Metadata(id: "changed")); // winner v1 changes metadata

        await Assert.ThrowsAsync<MetadataChangedException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot, new DeltaAction[] { Add("late.parquet") }, DeltaReadScope.BlindAppend));

        Assert.Equal(1L, (await LoadAsync()).Version); // no v2 was published
    }

    [Fact]
    public async Task BlindAppend_AbortsOnConcurrentProtocolChange()
    {
        // AC2: an intervening protocol change is rejected before publishing.
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync();
        await CommitRawAsync(1, DeltaTestHarness.Protocol(minReader: 1, minWriter: 2)); // winner v1 rewrites protocol

        await Assert.ThrowsAsync<ProtocolChangedException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot, new DeltaAction[] { Add("late.parquet") }, DeltaReadScope.BlindAppend));
    }

    [Fact]
    public async Task WholeTableOverwrite_AbortsOnConcurrentAppend()
    {
        // AC2 (overwrite/partition): a whole-table overwrite conflicts with a concurrent append.
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync();
        await CommitRawAsync(1, DeltaTestHarness.Add("winner.parquet")); // winner v1 appends

        await Assert.ThrowsAsync<ConcurrentAppendException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot, new DeltaAction[] { Add("overwrite.parquet") }, DeltaReadScope.WholeTable));
    }

    [Fact]
    public async Task ReadFilesDelete_AbortsWhenConcurrentCommitRemovesReadFile()
    {
        // AC2 (delete): a targeted delete conflicts when a concurrent commit removed a file it read.
        await SeedTableAsync();
        await CommitRawAsync(1, DeltaTestHarness.Add("target.parquet")); // v1: the file our delete will read
        Snapshot snapshot = await LoadAsync();
        await CommitRawAsync(2, DeltaTestHarness.Remove("target.parquet")); // winner v2 removes it first

        await Assert.ThrowsAsync<ConcurrentDeleteReadException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot,
                new DeltaAction[] { Remove("target.parquet") },
                DeltaReadScope.ReadFiles(new[] { "target.parquet" })));
    }

    [Fact]
    public async Task ReadFilesDelete_RebasesWhenConcurrentCommitTouchesDifferentFile()
    {
        // AC3-adjacent: a targeted delete whose read set is disjoint from the winner rebases and commits.
        await SeedTableAsync();
        await CommitRawAsync(1, DeltaTestHarness.Add("mine.parquet"), DeltaTestHarness.Add("other.parquet"));
        Snapshot snapshot = await LoadAsync();
        await CommitRawAsync(2, DeltaTestHarness.Remove("other.parquet")); // winner removes a file we did NOT read

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot,
            new DeltaAction[] { Remove("mine.parquet") },
            DeltaReadScope.ReadFiles(new[] { "mine.parquet" }));

        Assert.Equal(3L, result.Version); // rebased onto v2 → committed v3
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task UnsupportedWriterProtocol_FailsClosed()
    {
        // AC2 / §2.14 P3: a table whose writer version this build does not support fails closed before write.
        await SeedTableAsync(minWriter: 5);
        Snapshot snapshot = await LoadAsync();

        var ex = await Assert.ThrowsAsync<DeltaProtocolException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot, new DeltaAction[] { Add("x.parquet") }, DeltaReadScope.BlindAppend));
        Assert.Equal(DeltaProtocolErrorKind.UnsupportedProtocol, ex.Kind);
        Assert.Equal(0L, (await LoadAsync()).Version); // nothing published
    }
}
