using System.Collections.Immutable;
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
        // fails closed rather than spinning.
        Snapshot snapshot = await SeedAndLoadAsync();
        await DeltaTestHarness.WriteCommitAsync(_backend, 1, DeltaTestHarness.Add("winner.parquet"));

        await Assert.ThrowsAsync<DeltaCommitUnknownStateException>(() =>
            new DeltaCommitter(_backend, maxAttempts: 1, nonceFactory: null)
                .CommitAsync(snapshot, new DeltaAction[] { Add("mine.parquet") }, DeltaReadScope.BlindAppend));
    }

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
            new DeltaCommitter(faulty).CommitAsync(
                snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend));
    }
}
