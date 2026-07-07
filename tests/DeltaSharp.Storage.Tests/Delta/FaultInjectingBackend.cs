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
    private int _transientPutsThrown;
    private bool _failHead;
    private bool _failRead;

    public FaultInjectingBackend(IStorageBackend inner) => _inner = inner;

    /// <summary>The 0-based put-if-absent call index that raises an ambiguous outcome (-1 = never).</summary>
    public int AmbiguousOnPutCall { get; init; } = -1;

    /// <summary>Whether the ambiguous put still writes the object (models ack-lost-but-durable).</summary>
    public bool PerformPutBeforeAmbiguous { get; init; }

    /// <summary>Whether the re-GET <c>Head</c> that follows the ambiguous put fails <b>persistently</b> —
    /// modelling a re-GET that cannot confirm the outcome (the transient-retry budget is exhausted →
    /// unknown-state).</summary>
    public bool FailReGetHead { get; init; }

    /// <summary>Whether the re-GET <c>OpenRead</c> that follows the ambiguous put fails persistently
    /// (unresolvable read → unknown-state).</summary>
    public bool FailReGetRead { get; init; }

    /// <summary>The 0-based put-if-absent call index that returns <see langword="false"/> (lost) — a
    /// self-inconsistent backend used to drive the "rejected but nothing visible" fail-closed guard, and
    /// (with <see cref="PerformPutBeforeLie"/>) the own-commit-durable-but-reported-lost double-commit guard
    /// (-1 = never).</summary>
    public int LieLostOnPutCall { get; init; } = -1;

    /// <summary>Whether the lying-lost put still writes the object (models our own durable commit surfacing
    /// as a lost race — the §2.11.6 "after commit, before ack" case).</summary>
    public bool PerformPutBeforeLie { get; init; }

    /// <summary>The number of leading put-if-absent invocations that raise a transient failure before any
    /// real put (drives the §2.11.3 bounded transient-retry path).</summary>
    public int TransientPutCalls { get; init; }

    public async ValueTask<bool> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (_transientPutsThrown < TransientPutCalls)
        {
            _transientPutsThrown++;
            throw new DeltaStorageException(StorageErrorKind.Transient, "injected transient put-if-absent failure.");
        }

        int call = _putCalls++;
        if (call == LieLostOnPutCall)
        {
            if (PerformPutBeforeLie)
            {
                await _inner.PutIfAbsentAsync(path, content, cancellationToken); // our commit lands...
            }

            return false; // ...but the backend reports a lost race.
        }

        if (call == AmbiguousOnPutCall)
        {
            if (PerformPutBeforeAmbiguous)
            {
                await _inner.PutIfAbsentAsync(path, content, cancellationToken);
            }

            if (FailReGetHead)
            {
                _failHead = true;
            }

            if (FailReGetRead)
            {
                _failRead = true;
            }

            throw new DeltaStorageException(StorageErrorKind.RetryUnsafeAmbiguous, "injected ambiguous commit acknowledgment.");
        }

        return await _inner.PutIfAbsentAsync(path, content, cancellationToken);
    }

    public ValueTask<StorageObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken)
    {
        if (_failHead)
        {
            throw new DeltaStorageException(StorageErrorKind.Transient, "injected persistent re-GET head failure.");
        }

        return _inner.HeadAsync(path, cancellationToken);
    }

    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        if (_failRead)
        {
            throw new DeltaStorageException(StorageErrorKind.Transient, "injected persistent re-GET read failure.");
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
