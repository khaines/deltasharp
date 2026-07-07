using System.Collections.Immutable;
using System.Linq;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Guard, writer-negotiation, and recovery edge-case tests for <see cref="DeltaCommitter"/> and
/// <see cref="ProtocolSupport.EnsureWritable"/> — the argument guards, the table-features writer gate, the
/// caller-<c>commitInfo</c> merge, and the fail-closed unresolved/unknown-state paths (design §2.11.3/§2.14).
/// </summary>
public sealed class DeltaCommitterEdgeCaseTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public DeltaCommitterEdgeCaseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deltacommit-edge-" + Guid.NewGuid().ToString("N"));
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

    private static ProtocolAction Writer(int minWriter, params string[] writerFeatures) =>
        new(1, minWriter, ImmutableArray<string>.Empty, writerFeatures.ToImmutableArray());

    private async Task<Snapshot> SeedAndLoadAsync()
    {
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        return await new DeltaLog(_backend).LoadSnapshotAsync();
    }

    // ---- ProtocolSupport.EnsureWritable ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EnsureWritable_AllowsBasicWriterVersions(int minWriter) =>
        ProtocolSupport.EnsureWritable(Writer(minWriter)); // does not throw

    [Fact]
    public void EnsureWritable_AllowsTableFeaturesWriter_WithNoFeatures() =>
        ProtocolSupport.EnsureWritable(Writer(ProtocolSupport.TableFeaturesWriterVersion)); // does not throw

    [Fact]
    public void EnsureWritable_RejectsTableFeaturesWriter_WithUnsupportedFeature()
    {
        var ex = Assert.Throws<DeltaProtocolException>(() =>
            ProtocolSupport.EnsureWritable(Writer(ProtocolSupport.TableFeaturesWriterVersion, "generatedColumns")));
        Assert.Equal(DeltaProtocolErrorKind.UnsupportedProtocol, ex.Kind);
        Assert.Contains("generatedColumns", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void EnsureWritable_RejectsUnsupportedLegacyWriterVersions(int minWriter)
    {
        var ex = Assert.Throws<DeltaProtocolException>(() => ProtocolSupport.EnsureWritable(Writer(minWriter)));
        Assert.Equal(DeltaProtocolErrorKind.UnsupportedProtocol, ex.Kind);
    }

    // ---- Argument guards ----

    [Fact]
    public void Constructor_RejectsNonPositiveMaxAttempts() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeltaCommitter(_backend, maxAttempts: 0, nonceFactory: null));

    [Fact]
    public async Task CommitAsync_RejectsEmptyActionSet()
    {
        Snapshot snapshot = await SeedAndLoadAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new DeltaCommitter(_backend).CommitAsync(snapshot, Array.Empty<DeltaAction>(), DeltaReadScope.BlindAppend));
    }

    // ---- commitInfo merge ----

    [Fact]
    public async Task CommitAsync_MergesCallerCommitInfo_AndAddsNonce()
    {
        Snapshot snapshot = await SeedAndLoadAsync();
        var callerCommitInfo = new CommitInfoAction(
            ImmutableSortedDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("operation", "WRITE"));

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot, new DeltaAction[] { callerCommitInfo, Add("part-0.parquet") }, DeltaReadScope.BlindAppend);

        IReadOnlyList<DeltaAction> committed = await new DeltaLog(_backend).ReadCommitActionsAsync(result.Version, default);
        CommitInfoAction persisted = committed.OfType<CommitInfoAction>().Single();
        Assert.Equal("WRITE", persisted.Entries["operation"]); // caller entry preserved
        Assert.True(persisted.Entries.ContainsKey(DeltaCommitter.CommitNonceKey)); // nonce added
    }

    // ---- recovery / fail-closed paths ----

    [Fact]
    public async Task AmbiguousAck_WhenAnotherWriterHoldsVersion_RebasesPastIt()
    {
        // Ambiguous put whose slot was actually taken by a different writer (no nonce match): recovery
        // classifies it as a definite conflict, and the blind append rebases past the winner.
        Snapshot snapshot = await SeedAndLoadAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("winner.parquet")); // someone else won v1
        var faulty = new FaultInjectingBackend(_backend) { AmbiguousOnPutCall = 0, PerformPutBeforeAmbiguous = false };

        DeltaCommitResult result = await new DeltaCommitter(faulty)
            .CommitAsync(snapshot, new DeltaAction[] { Add("mine.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(2L, result.Version);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task SustainedContention_BeyondMaxAttempts_FailsClosed()
    {
        // A single-attempt committer that must rebase (a winner already holds v1) exhausts its budget and
        // fails closed with a RETRYABLE contention error (the commit provably did not land) — distinct from
        // the genuine unknown-state paths.
        Snapshot snapshot = await SeedAndLoadAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("winner.parquet"));

        var ex = await Assert.ThrowsAsync<DeltaCommitContentionException>(() =>
            new DeltaCommitter(_backend, maxAttempts: 1, nonceFactory: null)
                .CommitAsync(snapshot, new DeltaAction[] { Add("mine.parquet") }, DeltaReadScope.BlindAppend));
        Assert.Equal(1, ex.MaxAttempts);
    }

    [Fact]
    public async Task BlindAppend_WithRemoveAction_IsRejected()
    {
        // A BlindAppend scope performs no data-conflict detection, so a remove-bearing payload (which could
        // silently rebase past a concurrent same-file remove — the deferred ConcurrentDeleteDelete cell) is
        // rejected up front; such a commit must use WholeTable or ReadFiles.
        Snapshot snapshot = await SeedAndLoadAsync();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot,
                new DeltaAction[] { Add("a.parquet"), Remove("gone.parquet") },
                DeltaReadScope.BlindAppend));
        Assert.Equal("actions", ex.ParamName);
    }

    [Fact]
    public async Task CommitWithUnsupportedProtocolUpgrade_FailsClosed()
    {
        // A commit that installs a protocol this writer cannot honor is rejected before any write, even
        // though the current table protocol is writable.
        Snapshot snapshot = await SeedAndLoadAsync();
        var upgrade = new ProtocolAction(1, 5, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

        var ex = await Assert.ThrowsAsync<DeltaProtocolException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot, new DeltaAction[] { upgrade, Add("a.parquet") }, DeltaReadScope.WholeTable));
        Assert.Equal(DeltaProtocolErrorKind.UnsupportedProtocol, ex.Kind);
        Assert.Equal(0L, (await new DeltaLog(_backend).LoadSnapshotAsync()).Version);
    }

    [Fact]
    public async Task TransientPutFailure_IsRetriedWithinTheAttempt_AndSucceeds()
    {
        // A transient storage failure on the commit put is retried with (test-injected no-op) backoff and
        // succeeds without consuming a rebase attempt (design §2.11.3).
        Snapshot snapshot = await SeedAndLoadAsync();
        var faulty = new FaultInjectingBackend(_backend) { TransientPutCalls = 3 };

        DeltaCommitResult result = await new DeltaCommitter(
                faulty, DeltaCommitter.DefaultMaxAttempts, nonceFactory: null, transientBackoff: NoBackoff)
            .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(1L, result.Version);
        Assert.Equal(1, result.Attempts); // transient retries are within the attempt, not a rebase
    }

    [Fact]
    public async Task TransientPutFailure_BeyondBudget_Propagates()
    {
        // A transient failure that never clears exhausts the bounded transient-retry budget and surfaces
        // rather than looping forever.
        Snapshot snapshot = await SeedAndLoadAsync();
        var faulty = new FaultInjectingBackend(_backend) { TransientPutCalls = DeltaCommitter.MaxTransientRetries + 5 };

        var ex = await Assert.ThrowsAsync<DeltaStorageException>(() =>
            new DeltaCommitter(faulty, DeltaCommitter.DefaultMaxAttempts, nonceFactory: null, transientBackoff: NoBackoff)
                .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend));
        Assert.Equal(StorageErrorKind.Transient, ex.Kind);
    }

    [Fact]
    public async Task Reentrant_SharedCommitter_CommitsConcurrentlyWithoutLoss()
    {
        // A single committer instance is safe to share across concurrent commits (documented reentrancy).
        Snapshot snapshot = await SeedAndLoadAsync();
        var committer = new DeltaCommitter(_backend);

        Task<DeltaCommitResult>[] commits = Enumerable.Range(0, 5)
            .Select(i => Task.Run(() => committer.CommitAsync(
                snapshot, new DeltaAction[] { Add($"f{i}.parquet") }, DeltaReadScope.BlindAppend)))
            .ToArray();
        await Task.WhenAll(commits);

        Snapshot reloaded = await new DeltaLog(_backend).LoadSnapshotAsync();
        Assert.Equal(5L, reloaded.Version);
        Assert.Equal(5, reloaded.ActiveFiles.Length); // all five landed exactly once
        Assert.Equal(new[] { 1L, 2L, 3L, 4L, 5L }, commits.Select(c => c.Result.Version).OrderBy(v => v).ToArray());
    }

    [Fact]
    public async Task CommitInstallingUnreadableProtocol_FailsClosed()
    {
        // A commit that installs a protocol this build could not read back (unsupported reader version) is
        // rejected before any write — never publish a table this build cannot itself read.
        Snapshot snapshot = await SeedAndLoadAsync();
        var unreadable = new ProtocolAction(
            ProtocolSupport.ColumnMappingReaderVersion, 2, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

        var ex = await Assert.ThrowsAsync<DeltaProtocolException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot, new DeltaAction[] { unreadable, Add("a.parquet") }, DeltaReadScope.WholeTable));
        Assert.Equal(DeltaProtocolErrorKind.UnsupportedProtocol, ex.Kind);
        Assert.Equal(0L, (await new DeltaLog(_backend).LoadSnapshotAsync()).Version);
    }

    [Fact]
    public async Task TransientPutFailure_WithDefaultBackoff_RetriesAndSucceeds()
    {
        // Exercises the production exponential-jitter backoff: one transient failure → one real (short)
        // backoff → success. Uses the default committer (no injected backoff).
        Snapshot snapshot = await SeedAndLoadAsync();
        var faulty = new FaultInjectingBackend(_backend) { TransientPutCalls = 1 };

        DeltaCommitResult result = await new DeltaCommitter(faulty)
            .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(1L, result.Version);
    }

    private static Task NoBackoff(int attempt, CancellationToken cancellationToken) => Task.CompletedTask;

    [Fact]
    public async Task LostRaceWithNoVisibleWinner_FailsClosedWithUnknownState()
    {
        // A self-inconsistent backend that reports a lost race while nothing was written: the writer refuses
        // to silently retry (which could double-commit) and fails closed.
        Snapshot snapshot = await SeedAndLoadAsync();
        var faulty = new FaultInjectingBackend(_backend) { LieLostOnPutCall = 0 };

        var ex = await Assert.ThrowsAsync<DeltaCommitUnknownStateException>(() =>
            new DeltaCommitter(faulty).CommitAsync(
                snapshot, new DeltaAction[] { Add("mine.parquet") }, DeltaReadScope.BlindAppend));
        Assert.Equal(1L, ex.Version);
    }

    [Fact]
    public async Task AmbiguousAck_WhenReGetReadFails_FailsClosedWithUnknownState()
    {
        // The commit landed but the re-GET *read* (not just the head) failed: recovery cannot confirm and
        // fails closed.
        Snapshot snapshot = await SeedAndLoadAsync();
        var faulty = new FaultInjectingBackend(_backend)
        {
            AmbiguousOnPutCall = 0,
            PerformPutBeforeAmbiguous = true,
            FailReGetRead = true,
        };

        await Assert.ThrowsAsync<DeltaCommitUnknownStateException>(() =>
            new DeltaCommitter(faulty, DeltaCommitter.DefaultMaxAttempts, nonceFactory: null, transientBackoff: NoBackoff)
                .CommitAsync(
                snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend));
    }
}
