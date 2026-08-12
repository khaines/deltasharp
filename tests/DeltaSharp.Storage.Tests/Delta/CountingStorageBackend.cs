using System.Runtime.CompilerServices;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// A test-only <see cref="IStorageBackend"/> decorator that <b>counts</b> the backend operations a read path
/// issues, with no behavioural change whatsoever (every call delegates verbatim). It is the measurement
/// instrument behind the change-feed read-path I/O-budget regression tests (#691): the pre-range
/// column-mapping identity gate (#671 / PR #690) must be provably <b>listing-free</b> and must read each
/// pre-range commit object <b>at most once</b>, so a regression that reintroduces a second <c>_delta_log/</c>
/// listing — or a second pass over the retained commits — turns the pinned budget RED rather than silently
/// restoring the constant factor.
/// <para>Counters are plain fields mutated on the enumerating thread; the reads under test are sequential,
/// so no synchronization is needed (and none is claimed).</para>
/// </summary>
internal sealed class CountingStorageBackend : IStorageBackend
{
    private const string LogPrefix = "_delta_log/";

    private readonly IStorageBackend _inner;
    private readonly List<string> _opens = new();
    private int _logListings;

    public CountingStorageBackend(IStorageBackend inner) => _inner = inner;

    /// <summary>How many times <c>_delta_log/</c> was listed (the LIST budget).</summary>
    public int LogListings => _logListings;

    /// <summary>Every object path opened for read, in order (duplicates retained, so a double-read shows).</summary>
    public IReadOnlyList<string> Opens => _opens;

    /// <summary>How many <c>_delta_log/&lt;N&gt;.json</c> commit objects were opened (the commit-GET budget).</summary>
    public int CommitReads => CountOpens(IsCommitObject);

    /// <summary>How many DISTINCT commit objects were opened — equal to <see cref="CommitReads"/> exactly when
    /// no commit is read twice.</summary>
    public int DistinctCommitReads =>
        _opens.Where(IsCommitObject).Distinct(StringComparer.Ordinal).Count();

    /// <summary>How many times the commit object for <paramref name="version"/> was opened.</summary>
    public int CommitReadsOf(long version)
    {
        string suffix = LogPrefix + version.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".json";
        return CountOpens(path => path.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>Resets every counter (so a test can measure one phase of a multi-phase scenario).</summary>
    public void Reset()
    {
        _logListings = 0;
        _opens.Clear();
    }

    public StorageBackendKind Kind => _inner.Kind;

    public string TableIdentity => _inner.TableIdentity;

    public ValueTask<Stream> ReadRangeAsync(string path, long offset, long length, CancellationToken cancellationToken) =>
        _inner.ReadRangeAsync(path, offset, length, cancellationToken);

    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        _opens.Add(path);
        return _inner.OpenReadAsync(path, cancellationToken);
    }

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
        if (string.Equals(prefix, LogPrefix, StringComparison.Ordinal))
        {
            _logListings++;
        }

        await foreach (StorageObjectInfo info in _inner.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            yield return info;
        }
    }

    private static bool IsCommitObject(string path) =>
        path.Contains(LogPrefix, StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal);

    private int CountOpens(Func<string, bool> predicate)
    {
        int count = 0;
        foreach (string path in _opens)
        {
            if (predicate(path))
            {
                count++;
            }
        }

        return count;
    }
}
