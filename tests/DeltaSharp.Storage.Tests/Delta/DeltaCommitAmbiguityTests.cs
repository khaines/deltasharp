using System.Collections.Immutable;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Ambiguous-acknowledgment recovery tests for <see cref="DeltaCommitter"/> (design §2.11.3/§2.11.6,
/// STORY-05.3.1 AC4): when a commit put-if-absent raises <see cref="StorageErrorKind.RetryUnsafeAmbiguous"/>,
/// the writer re-reads <c>&lt;N&gt;.json</c> and either confirms its own commit landed (nonce match,
/// exactly-once), determines the slot is free and retries, or fails closed with a precise unknown-state
/// error — never a silent success or a double-commit.
/// </summary>
public sealed class DeltaCommitAmbiguityTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public DeltaCommitAmbiguityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deltacommit-amb-" + Guid.NewGuid().ToString("N"));
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

    private static Task NoBackoff(int attempt, CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<Snapshot> SeedAndLoadAsync()
    {
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        return await new DeltaLog(_backend).LoadSnapshotAsync();
    }

    [Fact]
    public async Task AmbiguousAck_AfterCommitLanded_ResolvesAsSuccessExactlyOnce()
    {
        // The put wrote the commit, then the ack was lost: recovery re-GETs, matches the nonce, and reports
        // success at the same version — no double-commit.
        Snapshot snapshot = await SeedAndLoadAsync();
        var faulty = new FaultInjectingBackend(_backend)
        {
            AmbiguousOnPutCall = 0,
            PerformPutBeforeAmbiguous = true,
        };

        DeltaCommitResult result = await new DeltaCommitter(faulty)
            .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(1L, result.Version);
        Assert.Equal(1, result.Attempts);

        Snapshot reloaded = await new DeltaLog(_backend).LoadSnapshotAsync();
        Assert.Equal(1L, reloaded.Version);
        Assert.Equal("part-0.parquet", Assert.Single(reloaded.ActiveFiles).Path); // committed exactly once
    }

    [Fact]
    public async Task AmbiguousAck_WhenCommitDidNotLand_RetriesSameVersionAndSucceeds()
    {
        // The put did not write before the ack was lost: recovery re-GETs, finds the slot free, and retries
        // the same version.
        Snapshot snapshot = await SeedAndLoadAsync();
        var faulty = new FaultInjectingBackend(_backend)
        {
            AmbiguousOnPutCall = 0,
            PerformPutBeforeAmbiguous = false,
        };

        DeltaCommitResult result = await new DeltaCommitter(faulty)
            .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(1L, result.Version);
        Assert.Equal(2, result.Attempts); // ambiguous attempt + the successful retry

        Snapshot reloaded = await new DeltaLog(_backend).LoadSnapshotAsync();
        Assert.Equal("part-0.parquet", Assert.Single(reloaded.ActiveFiles).Path);
    }

    [Fact]
    public async Task AmbiguousAck_WhenRecoveryCannotDetermineOutcome_FailsClosedWithUnknownState()
    {
        // The put was ambiguous AND the re-GET itself failed: recovery cannot prove committed-or-not, so it
        // fails closed with a precise unknown-state error rather than guessing.
        Snapshot snapshot = await SeedAndLoadAsync();
        var faulty = new FaultInjectingBackend(_backend)
        {
            AmbiguousOnPutCall = 0,
            PerformPutBeforeAmbiguous = true,
            FailReGetHead = true,
        };

        var ex = await Assert.ThrowsAsync<DeltaCommitUnknownStateException>(() =>
            new DeltaCommitter(faulty, DeltaCommitter.DefaultMaxAttempts, nonceFactory: null, transientBackoff: NoBackoff)
                .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend));
        Assert.Equal(1L, ex.Version);
    }

    [Fact]
    public async Task OwnDurableCommitReportedLost_ResolvesAsSuccess_WithoutDoubleCommit()
    {
        // §2.11.6 "after commit, before ack": our own durable commit surfaces as a *lost race* (put reports
        // false though it landed — e.g. an ack lost after a HEAD-lag SlotFree retry). The definite-conflict
        // path must recognize our own nonce and succeed idempotently, never rebasing past our own commit
        // (which would publish the same add at v1 AND v2). This is the regression test for the council's
        // headline double-commit finding.
        Snapshot snapshot = await SeedAndLoadAsync();
        var faulty = new FaultInjectingBackend(_backend) { LieLostOnPutCall = 0, PerformPutBeforeLie = true };

        DeltaCommitResult result = await new DeltaCommitter(faulty)
            .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(1L, result.Version);
        Assert.Equal(1, result.Attempts);

        Snapshot reloaded = await new DeltaLog(_backend).LoadSnapshotAsync();
        Assert.Equal(1L, reloaded.Version); // NOT v2
        Assert.Equal("part-0.parquet", Assert.Single(reloaded.ActiveFiles).Path); // committed exactly once
    }

    [Fact]
    public async Task CorruptWinnerDuringRebase_FailsClosed()
    {
        // A malformed winning commit encountered while classifying a lost race fails closed (the corrupt
        // log surfaces as a precise protocol error rather than being silently skipped).
        Snapshot snapshot = await SeedAndLoadAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, "{ this is not valid json");

        await Assert.ThrowsAsync<DeltaProtocolException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot, new DeltaAction[] { Add("late.parquet") }, DeltaReadScope.BlindAppend));
    }

    [Fact]
    public async Task CorruptCommitDuringAmbiguousReGet_FailsClosedWithUnknownState()
    {
        // The re-GET during ambiguous recovery reads a malformed commit at the target version: it cannot be
        // classified, so recovery fails closed with unknown-state rather than mis-resolving.
        Snapshot snapshot = await SeedAndLoadAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, "{ this is not valid json");
        var faulty = new FaultInjectingBackend(_backend) { AmbiguousOnPutCall = 0, PerformPutBeforeAmbiguous = false };

        await Assert.ThrowsAsync<DeltaCommitUnknownStateException>(() =>
            new DeltaCommitter(faulty).CommitAsync(
                snapshot, new DeltaAction[] { Add("late.parquet") }, DeltaReadScope.BlindAppend));
    }
}
