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
/// <para><b>Bounded LRU with per-entry TTL + backoff re-probe.</b> The cache holds at most <c>capacity</c>
/// entries; on overflow the least-recently-used entry is evicted (never a whole-map wipe). Each entry expires
/// after a TTL; a lookup of an <b>expired</b> entry reports "not known" so the checkpoint is re-decoded once
/// (the re-probe path) — so a checkpoint that has since been repaired/replaced heals without a process restart,
/// and a stale key can never permanently suppress a now-good checkpoint.</para>
/// <para><b>Durably-bad backoff (uptime-OOM fix).</b> A single static crafted checkpoint that is NEVER
/// rewritten would otherwise spawn a NEW permanent strand on every TTL re-probe (a 10-minute cadence ⇒ an
/// uptime-driven strand accumulation). To bound that, each re-seed of the SAME identity <b>increments a strike
/// count and lengthens the TTL exponentially</b> (base × 2^strikes, capped at <c>maxTtl</c>): a repeatedly
/// timing-out identity is re-probed logarithmically-rarely over uptime, so a single bad input cannot accumulate
/// strands. An expired entry is <b>retained</b> (not removed) across the re-probe so its strike history carries
/// forward; it only leaves the cache by LRU capacity eviction once it stops being re-seeded (a healed
/// checkpoint drifts to the LRU tail and is evicted, so it does not leak). A never-re-decoded healthy
/// checkpoint is never affected — only an identity whose decode keeps timing out backs off.</para>
/// <para><b>Seed only on a proven timeout.</b> The caller seeds an identity ONLY after its decode ran past an
/// ADEQUATE budget (a <see cref="StorageErrorKind.DecodeBudgetExceeded"/> from a part given its full size-aware
/// budget), never on saturation/queue-starvation (a <see cref="DecodeCapacityExhaustedException"/>, which means
/// the decode never started and the checkpoint may be perfectly healthy) and never on a part starved by an
/// earlier part's consumption of a shared budget.</para>
/// <para>Thread-safe via a single lock; NativeAOT-safe (a <see cref="Dictionary{TKey, TValue}"/> + a
/// <see cref="LinkedList{T}"/>, no reflection or codegen). The clock is injected per call so a TTL re-probe is
/// deterministically testable.</para>
/// </remarks>
internal sealed class CheckpointDecodeNegativeCache
{
    private readonly int _capacity;
    private readonly TimeSpan _baseTtl;
    private readonly TimeSpan _maxTtl;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byKey;
    private readonly LinkedList<Entry> _lru = new(); // most-recently-used at the front

    internal CheckpointDecodeNegativeCache(int capacity, TimeSpan ttl, TimeSpan? maxTtl = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttl.Ticks, nameof(ttl));
        TimeSpan cappedMax = maxTtl ?? TimeSpan.FromHours(24);
        ArgumentOutOfRangeException.ThrowIfLessThan(cappedMax, ttl, nameof(maxTtl));
        _capacity = capacity;
        _baseTtl = ttl;
        _maxTtl = cappedMax;
        _byKey = new Dictionary<string, LinkedListNode<Entry>>(StringComparer.Ordinal);
    }

    /// <summary>Composes the stable, cross-instance cache key from the table identity, the checkpoint part
    /// path, and the checkpoint version (I4). The NUL separators keep the fields unambiguous.</summary>
    internal static string Key(string tableIdentity, string partPath, long version) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{tableIdentity}\0{partPath}\0{version}");

    /// <summary>Whether <paramref name="key"/> is known-timed-out and still within its (backoff-extended) TTL.
    /// An EXPIRED entry is reported as not-known (the re-probe path) so the checkpoint is re-decoded once; the
    /// entry is RETAINED so a subsequent <see cref="Seed"/> can extend its backoff instead of resetting it.</summary>
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

            // Expired: re-probe (the checkpoint is decoded once more). The node is RETAINED (not removed) and
            // NOT refreshed for recency, so it drifts toward the LRU tail: a healed checkpoint is eventually
            // capacity-evicted, while a still-bad one keeps its strike history for the next Seed's backoff.
            return false;
        }
    }

    /// <summary>Records <paramref name="key"/> as timed-out. On the FIRST seed the TTL is the base; on each
    /// re-seed of the same identity the strike count increments and the TTL lengthens exponentially
    /// (base × 2^strikes, capped at <c>maxTtl</c>), so a static crafted checkpoint is re-probed
    /// logarithmically-rarely over uptime (bounding strand accumulation). Evicts the least-recently-used entry
    /// if the cache is at capacity.</summary>
    internal void Seed(string key, TimeProvider clock)
    {
        DateTimeOffset now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                existing.Value.Strikes = SaturatingIncrement(existing.Value.Strikes);
                existing.Value.ExpiresAt = now + BackoffTtl(existing.Value.Strikes);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<Entry>(new Entry(key, now + BackoffTtl(0)) { Strikes = 0 });
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

    // base × 2^strikes, capped at maxTtl. Overflow-safe: once the shift would exceed the cap (or overflow), the
    // cap wins.
    private TimeSpan BackoffTtl(int strikes)
    {
        long baseTicks = _baseTtl.Ticks;
        long maxTicks = _maxTtl.Ticks;
        if (strikes <= 0)
        {
            return _baseTtl;
        }

        // Guard against a huge shift: 62 doublings already exceeds any realistic TTL; clamp before shifting.
        int shift = Math.Min(strikes, 62);
        if (strikes >= 62 || baseTicks > (maxTicks >> shift))
        {
            return _maxTtl;
        }

        long scaled = baseTicks << shift;
        return scaled >= maxTicks ? _maxTtl : TimeSpan.FromTicks(scaled);
    }

    private static int SaturatingIncrement(int value) => value >= int.MaxValue ? int.MaxValue : value + 1;

    private sealed class Entry(string key, DateTimeOffset expiresAt)
    {
        internal string Key { get; } = key;

        internal DateTimeOffset ExpiresAt { get; set; } = expiresAt;

        internal int Strikes { get; set; }
    }
}
