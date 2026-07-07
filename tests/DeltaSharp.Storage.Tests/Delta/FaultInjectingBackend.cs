using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// An <see cref="IStorageBackend"/> decorator that injects commit-path faults — an ambiguous put-if-absent
/// acknowledgment, a failed re-GET, or a "lying" lost-race — to exercise <see cref="DeltaCommitter"/>
/// recovery (design §2.11.3/§2.11.6). Every non-injected operation delegates to a real backend.
/// </summary>
internal sealed class FaultInjectingBackend : IStorageBackend
{
    private readonly IStorageBackend _inner;
    private int _putCalls;
    private bool _failNextHead;
    private bool _failNextRead;

    public FaultInjectingBackend(IStorageBackend inner) => _inner = inner;

    /// <summary>The 0-based put-if-absent call index that raises an ambiguous outcome (-1 = never).</summary>
    public int AmbiguousOnPutCall { get; init; } = -1;

    /// <summary>Whether the ambiguous put still writes the object (models ack-lost-but-durable).</summary>
    public bool PerformPutBeforeAmbiguous { get; init; }

    /// <summary>Whether the re-GET <c>Head</c> that follows the ambiguous put also fails (unresolvable).</summary>
    public bool FailReGetHead { get; init; }

    /// <summary>Whether the re-GET <c>OpenRead</c> that follows the ambiguous put fails (unresolvable read).</summary>
    public bool FailReGetRead { get; init; }

    /// <summary>The 0-based put-if-absent call index that returns <see langword="false"/> (lost) WITHOUT
    /// writing anything — a self-inconsistent backend used to drive the "rejected but nothing visible"
    /// fail-closed guard (-1 = never).</summary>
    public int LieLostOnPutCall { get; init; } = -1;

    public async ValueTask<bool> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        int call = _putCalls++;
        if (call == LieLostOnPutCall)
        {
            return false; // report a lost race though the object was never created.
        }

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

            if (FailReGetRead)
            {
                _failNextRead = true;
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

    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        if (_failNextRead)
        {
            _failNextRead = false;
            throw new DeltaStorageException(StorageErrorKind.Transient, "injected re-GET read failure.");
        }

        return _inner.OpenReadAsync(path, cancellationToken);
    }

    public ValueTask<Stream> ReadRangeAsync(string path, long offset, long length, CancellationToken cancellationToken) =>
        _inner.ReadRangeAsync(path, offset, length, cancellationToken);

    public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken) =>
        _inner.OpenWriteAsync(path, cancellationToken);

    public IAsyncEnumerable<StorageObjectInfo> ListAsync(string prefix, CancellationToken cancellationToken) =>
        _inner.ListAsync(prefix, cancellationToken);

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken) =>
        _inner.DeleteAsync(path, cancellationToken);
}
