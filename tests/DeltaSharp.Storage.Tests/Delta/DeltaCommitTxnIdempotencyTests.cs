using System.Collections.Immutable;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Idempotent-write-transaction tests for <see cref="DeltaCommitter"/> (design §2.11.4, STORY-05.3.2 AC1):
/// a retry whose application <c>txn{appId,version}</c> is already committed is idempotently skipped — no
/// duplicate files, no new version — whether the retry sees the prior commit in its read snapshot
/// (up-front skip) or only discovers it as a winner on a lost race (conflict-path skip).
/// </summary>
public sealed class DeltaCommitTxnIdempotencyTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public DeltaCommitTxnIdempotencyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deltacommit-txn-" + Guid.NewGuid().ToString("N"));
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

    private static TxnAction Txn(string appId, long version) => new(appId, version, LastUpdated: null);

    private Task<Snapshot> LoadAsync(long? version = null) => new DeltaLog(_backend).LoadSnapshotAsync(version);

    private async Task SeedAsync() =>
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());

    [Fact]
    public async Task Retry_WhenTxnAlreadyCommitted_SkipsIdempotently_UpFront()
    {
        // AC1: the retry re-reads a fresh snapshot that already reflects its txn, so the commit is skipped
        // up front — no v2, no duplicate file.
        await SeedAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Txn("stream", 5), DeltaTestHarness.Add("batch5.parquet"));
        Snapshot snapshot = await LoadAsync(); // v1; Transactions["stream"] = 5

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot, new DeltaAction[] { Txn("stream", 5), Add("retry-batch5.parquet") }, DeltaReadScope.BlindAppend);

        Assert.True(result.Skipped);
        Assert.Equal(0, result.Attempts); // no put-if-absent attempted

        Snapshot reloaded = await LoadAsync();
        Assert.Equal(1L, reloaded.Version); // no new version published
        Assert.Equal("batch5.parquet", Assert.Single(reloaded.ActiveFiles).Path); // retry file NOT added
    }

    [Fact]
    public async Task Retry_WhenTxnAlreadyCommitted_SkipsIdempotently_OnConflictPath()
    {
        // AC1 (stale snapshot): the retry reads an OLD snapshot (before its txn landed), loses the race, and
        // discovers its own txn among the winners — it must skip idempotently, not rebase or raise
        // ConcurrentTransactionException.
        await SeedAsync();
        Snapshot stale = await LoadAsync(); // v0, Transactions empty
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Txn("stream", 5), DeltaTestHarness.Add("batch5.parquet"));

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            stale, new DeltaAction[] { Txn("stream", 5), Add("retry-batch5.parquet") }, DeltaReadScope.BlindAppend);

        Assert.True(result.Skipped);

        Snapshot reloaded = await LoadAsync();
        Assert.Equal(1L, reloaded.Version);
        Assert.Equal("batch5.parquet", Assert.Single(reloaded.ActiveFiles).Path); // retry file NOT added
    }

    [Fact]
    public async Task NewTxnVersion_CommitsNormally_NotSkipped()
    {
        // A genuinely new micro-batch (higher txn version) is NOT a duplicate — it commits normally.
        await SeedAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Txn("stream", 5), DeltaTestHarness.Add("batch5.parquet"));
        Snapshot snapshot = await LoadAsync();

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot, new DeltaAction[] { Txn("stream", 6), Add("batch6.parquet") }, DeltaReadScope.BlindAppend);

        Assert.False(result.Skipped);
        Assert.Equal(2L, result.Version);

        Snapshot reloaded = await LoadAsync();
        Assert.Equal(new[] { "batch5.parquet", "batch6.parquet" }, reloaded.ActiveFiles.Select(a => a.Path).OrderBy(p => p).ToArray());
    }

    [Fact]
    public async Task ConflictPathSkip_UsesMaxWinnerVersion_AcrossMultipleSameAppTxns()
    {
        // A stale-snapshot retry loses the race and reads MULTIPLE winner commits for the same appId; the
        // conflict-path skip keys on the MAX recorded version, so a retry at that version is covered and
        // skipped. (Pins TxnStateOf's Math.Max reduction — a Min reduction would use v3, miss the v5 retry,
        // and raise ConcurrentTransactionException.)
        await SeedAsync();
        Snapshot stale = await LoadAsync(); // v0
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Txn("stream", 3), DeltaTestHarness.Add("batch3.parquet"));
        await DeltaTestHarness.WriteCommitAsync(_backend, 2, DeltaTestHarness.Txn("stream", 5), DeltaTestHarness.Add("batch5.parquet"));

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            stale, new DeltaAction[] { Txn("stream", 5), Add("retry-batch5.parquet") }, DeltaReadScope.BlindAppend);

        Assert.True(result.Skipped);
        Snapshot reloaded = await LoadAsync();
        Assert.Equal(2L, reloaded.Version); // no v3 published
        Assert.Equal(new[] { "batch3.parquet", "batch5.parquet" }, reloaded.ActiveFiles.Select(a => a.Path).OrderBy(p => p).ToArray());
    }

    [Fact]
    public async Task GenuineConcurrentSameAppId_LowerWinnerVersion_ThrowsConcurrentTransaction()
    {
        // A genuine concurrent writer sharing the appId at a NON-covering (lower) version is not our retry:
        // the conflict-path skip does not fire (winner v3 < our v5) and the commit aborts with
        // ConcurrentTransactionException end-to-end through CommitAsync.
        await SeedAsync();
        Snapshot stale = await LoadAsync(); // v0
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Txn("stream", 3), DeltaTestHarness.Add("other.parquet"));

        await Assert.ThrowsAsync<ConcurrentTransactionException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                stale, new DeltaAction[] { Txn("stream", 5), Add("mine.parquet") }, DeltaReadScope.BlindAppend));
    }

    [Fact]
    public async Task CommitWithoutTxn_IsNeverSkipped()
    {
        // A plain append with no txn has no idempotency key and always commits.
        await SeedAsync();
        Snapshot snapshot = await LoadAsync();

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot, new DeltaAction[] { Add("a.parquet") }, DeltaReadScope.BlindAppend);

        Assert.False(result.Skipped);
        Assert.Equal(1L, result.Version);
    }
}
