using System.Runtime.CompilerServices;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// A test-only <see cref="IStorageBackend"/> decorator that makes the <b>second (and later)</b> listing of
/// <c>_delta_log/</c> <b>diverge</b> from the first by omitting one or more commit objects — modeling a
/// staler/partial second view of the log (object-store eventual consistency, a transient partial LIST, or a
/// concurrent log operation between two independent listings). The FIRST log listing is passed through
/// complete, so a snapshot reconstructed from it is well-formed; only a SECOND, independent log listing sees
/// the torn view. Candidate discovery (prefix <c>""</c>) and every non-list operation delegate unchanged.
/// <para>This proves VACUUM's #489 cdc protection reuses the <b>single</b> listing the snapshot was built
/// from: with one log listing there is no second, divergent view, so an in-window commit's <c>cdc</c> path
/// can never be dropped. If a regression reintroduced a second independent log listing, this backend would
/// tear it and the guarded test would fail.</para>
/// </summary>
internal sealed class DivergentLogListingBackend : IStorageBackend
{
    private const string LogPrefix = "_delta_log/";

    private readonly IStorageBackend _inner;
    private readonly HashSet<string> _omitFromSecondLogListing;
    private int _logListingCount;

    public DivergentLogListingBackend(IStorageBackend inner, IEnumerable<string> omitFromSecondLogListing)
    {
        _inner = inner;
        _omitFromSecondLogListing = new HashSet<string>(omitFromSecondLogListing, StringComparer.Ordinal);
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
        bool tearThisListing = isLogListing && ++_logListingCount >= 2;

        await foreach (StorageObjectInfo info in _inner.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            if (tearThisListing && _omitFromSecondLogListing.Contains(info.Path))
            {
                continue; // second, divergent log view: this in-window commit is invisible.
            }

            yield return info;
        }
    }
}
