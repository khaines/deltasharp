using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// A test-only <see cref="IStorageBackend"/> decorator that CANCELS the VACUUM <b>mid-CDF-scan</b>: it trips
/// the supplied <see cref="CancellationTokenSource"/> and throws <see cref="OperationCanceledException"/> on
/// the N-th <see cref="OpenReadAsync"/> of a specific commit-JSON object (#641 item 2 abort-path telemetry).
/// The in-window Change-Data-Feed protection scan (<c>CollectInWindowChangeDataPathsAsync</c>) RE-reads each
/// retained in-window commit JSON that snapshot reconstruction already read exactly once, so arming the fault
/// on the <b>second</b> read of the target commit fires it <b>inside</b> the scan's <c>try</c>/<c>finally</c>
/// — after the <c>try</c> is entered, while reading an in-window commit — which is the precise place the
/// scan-cost telemetry must still record its wall-clock and a <c>completed=false</c> terminal. Every other
/// operation delegates verbatim, so nothing but the targeted read is perturbed.
/// </summary>
internal sealed class CdcScanCancelingBackend : IStorageBackend
{
    private readonly IStorageBackend _inner;
    private readonly CancellationTokenSource _cts;
    private readonly string _targetCommitPath;
    private readonly int _cancelOnRead;
    private int _reads;

    public CdcScanCancelingBackend(
        IStorageBackend inner, CancellationTokenSource cts, long targetCommitVersion, int cancelOnRead)
    {
        _inner = inner;
        _cts = cts;
        _targetCommitPath = DeltaLogFiles.CommitPath(targetCommitVersion);
        _cancelOnRead = cancelOnRead;
    }

    /// <summary>How many times the target commit object was opened for read (so a test can prove the fault
    /// was armed on a scan re-read, not the single reconstruction read).</summary>
    public int TargetReads => _reads;

    public StorageBackendKind Kind => _inner.Kind;

    public string TableIdentity => _inner.TableIdentity;

    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        if (string.Equals(path, _targetCommitPath, StringComparison.Ordinal) && ++_reads == _cancelOnRead)
        {
            _cts.Cancel(); // the caller's token trips mid-scan...
            throw new OperationCanceledException(_cts.Token); // ...and the in-window commit read is cancelled.
        }

        return _inner.OpenReadAsync(path, cancellationToken);
    }

    public ValueTask<Stream> ReadRangeAsync(string path, long offset, long length, CancellationToken cancellationToken) =>
        _inner.ReadRangeAsync(path, offset, length, cancellationToken);

    public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken) =>
        _inner.OpenWriteAsync(path, cancellationToken);

    public ValueTask<bool> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        _inner.PutIfAbsentAsync(path, content, cancellationToken);

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken) =>
        _inner.DeleteAsync(path, cancellationToken);

    public ValueTask<StorageObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken) =>
        _inner.HeadAsync(path, cancellationToken);

    public IAsyncEnumerable<StorageObjectInfo> ListAsync(string prefix, CancellationToken cancellationToken) =>
        _inner.ListAsync(prefix, cancellationToken);
}
