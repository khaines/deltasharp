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
            new DeltaCommitter(faulty)
                .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend));
        Assert.Equal(1L, ex.Version);
    }

    /// <summary>An <see cref="IStorageBackend"/> decorator that injects an ambiguous commit-ack (and,
    /// optionally, a failed re-GET) to exercise §2.11.3 recovery; every other operation delegates to the
    /// real backend.</summary>
    private sealed class FaultInjectingBackend : IStorageBackend
    {
        private readonly IStorageBackend _inner;
        private int _putCalls;
        private bool _failNextHead;

        public FaultInjectingBackend(IStorageBackend inner) => _inner = inner;

        /// <summary>The 0-based put-if-absent call index that raises an ambiguous outcome (-1 = never).</summary>
        public int AmbiguousOnPutCall { get; init; } = -1;

        /// <summary>Whether the ambiguous put still writes the object (models ack-lost-but-durable).</summary>
        public bool PerformPutBeforeAmbiguous { get; init; }

        /// <summary>Whether the re-GET that follows the ambiguous put also fails (models unresolvable).</summary>
        public bool FailReGetHead { get; init; }

        public async ValueTask<bool> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            int call = _putCalls++;
            if (call == AmbiguousOnPutCall)
            {
                if (PerformPutBeforeAmbiguous)
                {
                    await _inner.PutIfAbsentAsync(path, content, cancellationToken);
                }

                if (FailReGetHead)
                {
                    _failNextHead = true;
                }

                throw new DeltaStorageException(StorageErrorKind.RetryUnsafeAmbiguous, "injected ambiguous commit acknowledgment.");
            }

            return await _inner.PutIfAbsentAsync(path, content, cancellationToken);
        }

        public ValueTask<StorageObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken)
        {
            if (_failNextHead)
            {
                _failNextHead = false;
                throw new DeltaStorageException(StorageErrorKind.Transient, "injected re-GET head failure.");
            }

            return _inner.HeadAsync(path, cancellationToken);
        }

        public ValueTask<Stream> ReadRangeAsync(string path, long offset, long length, CancellationToken cancellationToken) =>
            _inner.ReadRangeAsync(path, offset, length, cancellationToken);

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
            _inner.OpenReadAsync(path, cancellationToken);

        public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken) =>
            _inner.OpenWriteAsync(path, cancellationToken);

        public IAsyncEnumerable<StorageObjectInfo> ListAsync(string prefix, CancellationToken cancellationToken) =>
            _inner.ListAsync(prefix, cancellationToken);

        public ValueTask DeleteAsync(string path, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(path, cancellationToken);
    }
}
