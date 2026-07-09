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
    public async Task MultiTxnBatch_AllCovered_SkipsIdempotently()
    {
        // All-or-nothing: a batch carrying several txns is idempotently skipped only when EVERY txn is
        // already committed. Here both stream-a@5 and stream-b@3 already landed, so the retry skips.
        await SeedAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Txn("stream-a", 5), DeltaTestHarness.Add("a5.parquet"));
        await DeltaTestHarness.WriteCommitAsync(_backend, 2, DeltaTestHarness.Txn("stream-b", 3), DeltaTestHarness.Add("b3.parquet"));
        Snapshot snapshot = await LoadAsync(); // txn[stream-a]=5, txn[stream-b]=3

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot,
            new DeltaAction[] { Txn("stream-a", 5), Txn("stream-b", 3), Add("retry.parquet") },
            DeltaReadScope.BlindAppend);

        Assert.True(result.Skipped);
        Snapshot reloaded = await LoadAsync();
        Assert.Equal(2L, reloaded.Version); // nothing new published
        Assert.DoesNotContain("retry.parquet", reloaded.ActiveFiles.Select(a => a.Path));
    }

    [Fact]
    public async Task MultiTxnBatch_PartiallyCovered_FailsClosed_NoSilentDrop()
    {
        // A multi-txn batch where only SOME txns are already committed is an inconsistent atomic batch: it
        // must fail closed with PartialTransactionException rather than skip (which would silently drop the
        // uncommitted txn + its data) or double-commit the covered one. stream-a@5 is committed; stream-b@3
        // is not — so the batch [txn a@5, txn b@3, add] is refused, not skipped.
        await SeedAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Txn("stream-a", 5), DeltaTestHarness.Add("a5.parquet"));
        Snapshot snapshot = await LoadAsync(); // txn[stream-a]=5 only

        PartialTransactionException ex = await Assert.ThrowsAsync<PartialTransactionException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot,
                new DeltaAction[] { Txn("stream-a", 5), Txn("stream-b", 3), Add("would-be-lost.parquet") },
                DeltaReadScope.BlindAppend));

        // The message names the covered vs uncovered transactions in the CORRECT sections (pins the
        // committed/uncommitted partition so an inverted ternary is caught, not just that it threw).
        Assert.Contains("already committed [stream-a@5]", ex.Message);
        Assert.Contains("are not [stream-b@3]", ex.Message);

        Snapshot reloaded = await LoadAsync();
        Assert.DoesNotContain("would-be-lost.parquet", reloaded.ActiveFiles.Select(a => a.Path)); // nothing partial published
    }

    [Fact]
    public async Task MultiTxnBatch_PartiallyCovered_OnConflictPath_FailsClosed()
    {
        // Same all-or-nothing guarantee on the conflict path: a stale-snapshot batch loses the race, and the
        // winners cover only ONE of its two txns — it must fail closed, never skip-and-drop the other.
        await SeedAsync();
        Snapshot stale = await LoadAsync(); // v0
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Txn("stream-a", 5), DeltaTestHarness.Add("a5.parquet"));

        await Assert.ThrowsAsync<PartialTransactionException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                stale,
                new DeltaAction[] { Txn("stream-a", 5), Txn("stream-b", 3), Add("would-be-lost.parquet") },
                DeltaReadScope.BlindAppend));

        Snapshot reloaded = await LoadAsync();
        Assert.Equal(1L, reloaded.Version); // no partial commit published on the conflict path
        Assert.DoesNotContain("would-be-lost.parquet", reloaded.ActiveFiles.Select(a => a.Path));
    }

    [Fact]
    public async Task DuplicateAppIdsAtDifferentVersions_PartiallyCovered_FailsClosed()
    {
        // A malformed batch reusing one appId at two versions [txn a@5, txn a@6] against a snapshot at a@5 is
        // partially covered (a@5 covered, a@6 not) → fail closed, not skip.
        await SeedAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Txn("a", 5), DeltaTestHarness.Add("a5.parquet"));
        Snapshot snapshot = await LoadAsync(); // txn[a]=5

        await Assert.ThrowsAsync<PartialTransactionException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot,
                new DeltaAction[] { Txn("a", 5), Txn("a", 6), Add("would-be-lost.parquet") },
                DeltaReadScope.BlindAppend));
    }

    [Fact]
    public async Task DuplicateAppIdsAtDifferentVersions_AllCovered_SkipsIdempotently()
    {
        // The same duplicate-appId batch [txn a@5, txn a@6] against a snapshot at a@6 is fully covered
        // (a@6 covers both 5 and 6) → idempotent skip.
        await SeedAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Txn("a", 6), DeltaTestHarness.Add("a6.parquet"));
        Snapshot snapshot = await LoadAsync(); // txn[a]=6

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot,
            new DeltaAction[] { Txn("a", 5), Txn("a", 6), Add("retry.parquet") },
            DeltaReadScope.BlindAppend);

        Assert.True(result.Skipped);
        Assert.DoesNotContain("retry.parquet", (await LoadAsync()).ActiveFiles.Select(a => a.Path));
    }

    [Fact]
    public async Task AllCovered_BundledWithNewFiles_IsSkipped_IdempotencyKeyIsTheBatchIdentity()
    {
        // Contract documentation: the txn is the batch's idempotency identity. Reusing a committed txn key
        // while bundling genuinely NEW files is caller misuse (an idempotency-key collision) — the engine
        // treats the batch as an exact replay and skips it, so the new files are NOT published. This is the
        // same behavior as Delta and is intentional, not a silent-drop bug (the loud-failure case is the
        // PARTIAL overlap, covered above).
        await SeedAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Txn("stream", 5), DeltaTestHarness.Add("committed.parquet"));
        Snapshot snapshot = await LoadAsync(); // txn[stream]=5

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot,
            new DeltaAction[] { Txn("stream", 5), Add("new-data.parquet") },
            DeltaReadScope.BlindAppend);

        Assert.True(result.Skipped);
        Assert.DoesNotContain("new-data.parquet", (await LoadAsync()).ActiveFiles.Select(a => a.Path));
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
