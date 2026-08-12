using System.Collections.Generic;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// A process-wide, bounded <b>negative cache</b> of checkpoint-part identities whose decode provably ran past
/// the bounded wall-clock decode budget (design §5.4 C-DECODE; #647/#699/#716). It stops a persistently
/// non-terminating (crafted) checkpoint from being re-decoded on <b>every</b> snapshot load — which would
/// spawn a fresh detached-decode strand each time, a self-renewing leak (the Round-1 Critical). It is keyed on
/// <b>stable table identity</b> (<c>backend.Kind</c> + canonical table root + part path + checkpoint version),
/// NOT the transient <see cref="Backends.IStorageBackend"/> instance — production constructs a fresh backend
/// per scan/resolve, so an instance-keyed cache never hit and the strand stayed self-renewing.
/// </summary>
/// <remarks>
/// <para><b>Bounded LRU with per-entry TTL + re-probe.</b> The cache holds at most <c>capacity</c> entries;
/// on overflow the least-recently-used entry is evicted (never a whole-map wipe). Each entry expires after a
/// TTL; a lookup of an <b>expired</b> entry evicts it and reports "not known" so the checkpoint is re-decoded
/// once (the re-probe path) and re-seeded only if it times out again — so a checkpoint that has since been
/// repaired/replaced heals without a process restart, and a stale key can never permanently suppress a now-good
/// checkpoint.</para>
/// <para><b>Seed only on a proven timeout.</b> The caller seeds an identity ONLY after its decode ran past the
/// budget (a <see cref="StorageErrorKind.DecodeBudgetExceeded"/>), never on saturation/queue-starvation (a
/// <see cref="DecodeCapacityExhaustedException"/>, which means the decode never started and the checkpoint may
/// be perfectly healthy).</para>
/// <para>Thread-safe via a single lock; NativeAOT-safe (a <see cref="Dictionary{TKey, TValue}"/> + a
/// <see cref="LinkedList{T}"/>, no reflection or codegen). The clock is injected per call so a TTL re-probe is
/// deterministically testable.</para>
/// </remarks>
internal sealed class CheckpointDecodeNegativeCache
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byKey;
    private readonly LinkedList<Entry> _lru = new(); // most-recently-used at the front

    internal CheckpointDecodeNegativeCache(int capacity, TimeSpan ttl)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttl.Ticks, nameof(ttl));
        _capacity = capacity;
        _ttl = ttl;
        _byKey = new Dictionary<string, LinkedListNode<Entry>>(StringComparer.Ordinal);
    }

    /// <summary>Composes the stable, cross-instance cache key from the table identity, the checkpoint part
    /// path, and the checkpoint version (I4). The NUL separators keep the fields unambiguous.</summary>
    internal static string Key(string tableIdentity, string partPath, long version) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{tableIdentity}\0{partPath}\0{version}");

    /// <summary>Whether <paramref name="key"/> is known-timed-out and still within its TTL. An EXPIRED entry is
    /// evicted and reported as not-known (the re-probe path), so the checkpoint is re-decoded once and only
    /// re-seeded if it times out again.</summary>
    internal bool IsKnownTimedOut(string key, TimeProvider clock)
    {
        DateTimeOffset now = clock.GetUtcNow();
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                return false;
            }

            if (now < node.Value.ExpiresAt)
            {
                // Still valid: refresh LRU recency and report known-timed-out (skip the re-decode).
                _lru.Remove(node);
                _lru.AddFirst(node);
                return true;
            }

            // Expired: evict and re-probe (the checkpoint is decoded once more).
            _lru.Remove(node);
            _byKey.Remove(key);
            return false;
        }
    }

    /// <summary>Records <paramref name="key"/> as timed-out with a fresh TTL, evicting the least-recently-used
    /// entry if the cache is at capacity (LRU eviction, never a whole-map wipe).</summary>
    internal void Seed(string key, TimeProvider clock)
    {
        DateTimeOffset expiresAt = clock.GetUtcNow() + _ttl;
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                existing.Value.ExpiresAt = expiresAt;
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<Entry>(new Entry(key, expiresAt));
            _lru.AddFirst(node);
            _byKey[key] = node;

            if (_byKey.Count > _capacity)
            {
                LinkedListNode<Entry> lruNode = _lru.Last!;
                _lru.RemoveLast();
                _byKey.Remove(lruNode.Value.Key);
            }
        }
    }

    private sealed class Entry(string key, DateTimeOffset expiresAt)
    {
        internal string Key { get; } = key;

        internal DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    }
}
