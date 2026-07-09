using System.Runtime.CompilerServices;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// A test-only <see cref="IStorageBackend"/> decorator that injects a <b>torn / stale LIST</b> fault
/// (STORY-05.6.2 AC3): it can omit an on-disk object from <see cref="ListAsync"/> (a file present on disk
/// but missing from the listing, e.g. object-store eventual consistency) and/or report a <b>lagging</b>
/// modification time for a listed object (a preserved-timestamp move / clock-skewed listing). Every other
/// operation — including <c>_delta_log</c> reads that reconstruct the snapshot — delegates unchanged, so the
/// fault is confined to the discovery listing. It proves VACUUM's fail-safe: a protected (active) file is
/// never deleted even when the listing view is torn.
/// </summary>
internal sealed class StaleListingBackend : IStorageBackend
{
    private readonly IStorageBackend _inner;
    private readonly HashSet<string> _omittedFromList;
    private readonly IReadOnlyDictionary<string, DateTime> _mtimeOverrides;

    public StaleListingBackend(
        IStorageBackend inner,
        IEnumerable<string>? omittedFromList = null,
        IReadOnlyDictionary<string, DateTime>? mtimeOverrides = null)
    {
        _inner = inner;
        _omittedFromList = new HashSet<string>(omittedFromList ?? Array.Empty<string>(), StringComparer.Ordinal);
        _mtimeOverrides = mtimeOverrides ?? new Dictionary<string, DateTime>(StringComparer.Ordinal);
    }

    public StorageBackendKind Kind => _inner.Kind;

    public ValueTask<Stream> ReadRangeAsync(string path, long offset, long length, CancellationToken cancellationToken) =>
        _inner.ReadRangeAsync(path, offset, length, cancellationToken);

    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
        _inner.OpenReadAsync(path, cancellationToken);

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
            if (_omittedFromList.Contains(info.Path))
            {
                continue; // present on disk, invisible to the listing (torn view).
            }

            yield return _mtimeOverrides.TryGetValue(info.Path, out DateTime lagging)
                ? info with { LastModifiedUtc = lagging }
                : info;
        }
    }
}
