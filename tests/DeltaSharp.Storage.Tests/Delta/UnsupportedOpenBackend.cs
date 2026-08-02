using System.Runtime.CompilerServices;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// A test-only <see cref="IStorageBackend"/> decorator that raises
/// <see cref="StorageErrorKind.UnsupportedFeature"/> from <see cref="OpenReadAsync"/> for paths containing a
/// marker — i.e. the BACKEND (not the checkpoint reader) declaring the object unreadable. It exists to pin
/// the scope of #681's non-authoritative-checkpoint swallow: that swallow is deliberately scoped to the
/// <c>DeltaCheckpointReader.ReadAsync</c> call, so an <c>UnsupportedFeature</c> from the backend OPEN — which
/// means the table itself cannot be read — must still PROPAGATE rather than be masked behind a silent full
/// JSON replay (PR #698 security review, FIX 1). Every other operation delegates unchanged.
/// </summary>
internal sealed class UnsupportedOpenBackend : IStorageBackend
{
    private readonly IStorageBackend _inner;
    private readonly string _pathMarker;

    public UnsupportedOpenBackend(IStorageBackend inner, string pathMarker)
    {
        _inner = inner;
        _pathMarker = pathMarker;
    }

    public StorageBackendKind Kind => _inner.Kind;

    public ValueTask<Stream> ReadRangeAsync(string path, long offset, long length, CancellationToken cancellationToken) =>
        _inner.ReadRangeAsync(path, offset, length, cancellationToken);

    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
        path.Contains(_pathMarker, StringComparison.Ordinal)
            ? throw DeltaStorageException.UnsupportedFeature(
                "The storage backend cannot read this object: unsupported feature (test injection).")
            : _inner.OpenReadAsync(path, cancellationToken);

    public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken) =>
        _inner.OpenWriteAsync(path, cancellationToken);

    public ValueTask<bool> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        _inner.PutIfAbsentAsync(path, content, cancellationToken);

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken) =>
        _inner.DeleteAsync(path, cancellationToken);

    public ValueTask<StorageObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken) =>
        _inner.HeadAsync(path, cancellationToken);

    public async IAsyncEnumerable<StorageObjectInfo> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (StorageObjectInfo info in _inner.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            yield return info;
        }
    }
}
