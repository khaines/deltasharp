using System.Runtime.CompilerServices;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// A test-only <see cref="IStorageBackend"/> decorator that makes a chosen <c>_delta_log/</c> listing (by
/// 1-based ordinal, <c>tearFromOrdinal</c>, and every listing after it) <b>diverge</b> by omitting one or
/// more commit objects — modeling a staler/partial view of the log (object-store eventual consistency, a
/// transient partial LIST, or a concurrent log operation). Candidate discovery (prefix <c>""</c>) and every
/// non-list operation delegate unchanged.
/// <para>With <c>tearFromOrdinal = 2</c> (default) the FIRST log listing passes through complete (so a
/// snapshot built from it is well-formed) and only a hypothetical SECOND listing is torn — proving VACUUM's
/// #489 cdc protection reuses the <b>single</b> listing the snapshot was built from (no second view exists,
/// so nothing is dropped; a regression reintroducing a second listing would tear it and fail the guard).
/// With <c>tearFromOrdinal = 1</c> the FIRST (and only) log listing is torn — modeling a tail-truncated log
/// view that the candidate root listing (fresher) does not share, exercising VACUUM's fail-closed abort.</para>
/// </summary>
internal sealed class DivergentLogListingBackend : IStorageBackend
{
    private const string LogPrefix = "_delta_log/";

    private readonly IStorageBackend _inner;
    private readonly HashSet<string> _omitFromSecondLogListing;
    private readonly int _tearFromOrdinal;
    private int _logListingCount;

    public DivergentLogListingBackend(
        IStorageBackend inner, IEnumerable<string> omitFromSecondLogListing, int tearFromOrdinal = 2)
    {
        _inner = inner;
        _omitFromSecondLogListing = new HashSet<string>(omitFromSecondLogListing, StringComparer.Ordinal);
        _tearFromOrdinal = tearFromOrdinal;
    }

    /// <summary>The number of times <c>_delta_log/</c> was listed — asserts the single-listing invariant.</summary>
    public int LogListingCount => _logListingCount;

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
        bool isLogListing = prefix.StartsWith(LogPrefix, StringComparison.Ordinal);
        bool tearThisListing = isLogListing && ++_logListingCount >= _tearFromOrdinal;

        await foreach (StorageObjectInfo info in _inner.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            if (tearThisListing && _omitFromSecondLogListing.Contains(info.Path))
            {
                continue; // divergent log view: this in-window commit is invisible.
            }

            yield return info;
        }
    }
}
