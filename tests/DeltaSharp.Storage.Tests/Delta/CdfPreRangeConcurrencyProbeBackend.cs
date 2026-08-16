using System.Globalization;
using System.Runtime.CompilerServices;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// #808: a thread-safe <see cref="IStorageBackend"/> decorator for the CDF pre-range identity scan's
/// bounded-concurrency fan-out. Unlike <see cref="CountingStorageBackend"/> (single-threaded), this is safe
/// under concurrent <see cref="OpenReadAsync"/> and measures the concurrency the fan-out actually achieves:
/// the max simultaneous in-flight <c>_delta_log/&lt;N&gt;.json</c> reads, per-version read counts, and it can
/// inject a per-version DELAY (to widen the concurrency window and force completion-order skew) or a per-version
/// FAULT (a transient infra error) so the min-faulting-version reduction and fail-closed paths are testable.
/// </summary>
internal sealed class CdfPreRangeConcurrencyProbeBackend : IStorageBackend
{
    private const string LogPrefix = "_delta_log/";

    private readonly IStorageBackend _inner;
    private readonly object _gate = new();
    private readonly List<string> _commitOpens = new();
    private readonly Dictionary<long, TimeSpan> _delays = new();
    private readonly Dictionary<long, Func<Exception>> _faults = new();
    private int _inFlightCommits;
    private int _maxInFlightCommits;
    private volatile bool _gateReturned;
    private int _commitOpensAfterGateReturned;

    public CdfPreRangeConcurrencyProbeBackend(IStorageBackend inner) => _inner = inner;

    /// <summary>Peak simultaneous in-flight commit-object reads observed (the achieved fan-out concurrency).</summary>
    public int MaxInFlightCommits { get { lock (_gate) { return _maxInFlightCommits; } } }

    /// <summary>Currently in-flight commit-object reads (0 after the gate has fully drained).</summary>
    public int InFlightCommits { get { lock (_gate) { return _inFlightCommits; } } }

    /// <summary>Distinct commit versions read (each should appear at most once — exactly-once coverage).</summary>
    public int DistinctCommitReads
    {
        get { lock (_gate) { return _commitOpens.Distinct(StringComparer.Ordinal).Count(); } }
    }

    public int CommitReadsOf(long version)
    {
        string suffix = LogPrefix + version.ToString("D20", CultureInfo.InvariantCulture) + ".json";
        lock (_gate) { return _commitOpens.Count(p => p.EndsWith(suffix, StringComparison.Ordinal)); }
    }

    public IReadOnlyList<long> CommitVersionsRead
    {
        get { lock (_gate) { return _commitOpens.Select(VersionOf).Where(v => v >= 0).Distinct().OrderBy(v => v).ToArray(); } }
    }

    /// <summary>Commit opens that STARTED after the gate method returned/threw — must be 0 (no orphaned read).</summary>
    public int CommitOpensAfterGateReturned { get { lock (_gate) { return _commitOpensAfterGateReturned; } } }

    public void Delay(long version, TimeSpan delay) => _delays[version] = delay;

    public void Fault(long version, Func<Exception> fault) => _faults[version] = fault;

    public void MarkGateReturned() => _gateReturned = true;

    public StorageBackendKind Kind => _inner.Kind;

    public string TableIdentity => _inner.TableIdentity;

    public ValueTask<Stream> ReadRangeAsync(string path, long offset, long length, CancellationToken cancellationToken) =>
        _inner.ReadRangeAsync(path, offset, length, cancellationToken);

    public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        long version = VersionOf(path);
        if (version < 0)
        {
            return await _inner.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _commitOpens.Add(path);
            if (_gateReturned)
            {
                _commitOpensAfterGateReturned++;
            }

            _inFlightCommits++;
            _maxInFlightCommits = Math.Max(_maxInFlightCommits, _inFlightCommits);
        }

        try
        {
            if (_delays.TryGetValue(version, out TimeSpan delay))
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            if (_faults.TryGetValue(version, out Func<Exception>? fault))
            {
                throw fault();
            }

            return await _inner.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inFlightCommits--;
            }
        }
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
        await foreach (StorageObjectInfo info in _inner.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            yield return info;
        }
    }

    // Parses the version from a `_delta_log/<20-digit>.json` commit path, or -1 for a non-commit object.
    private static long VersionOf(string path)
    {
        int idx = path.IndexOf(LogPrefix, StringComparison.Ordinal);
        if (idx < 0 || !path.EndsWith(".json", StringComparison.Ordinal))
        {
            return -1;
        }

        string name = path[(idx + LogPrefix.Length)..^5];
        return name.Length == 20 && long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : -1;
    }
}
