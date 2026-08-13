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
/// <para><b>Strike-gated poisoning (High #6 — checkpoint healthy-part poisoning fix).</b> A single decode
/// timeout does NOT poison an identity. A healthy-but-slow checkpoint decode on one tenant can exceed its floor
/// once because data-file strands (on the shared pool) oversubscribed CPU — a transient, not a corrupt input.
/// Poisoning requires <b>≥ <see cref="PoisonStrikeThreshold"/> proven timeouts of the SAME identity across
/// SEPARATE loads</b>: the first timeout only records a strike (the checkpoint is still re-decoded next load),
/// and a subsequent SUCCESSFUL decode <b>decrements the strike history</b> (<see cref="ClearOnSuccess"/>, floored
/// at zero) so a one-off slow decode never accumulates toward suppression, while a mostly-failing identity keeps
/// its accumulated suspicion. Only an identity that keeps timing out across loads is suppressed.</para>
/// <para><b>Single-flight probe (High #7 + Round-8 #11).</b> On the FIRST encounter of an unknown identity, and
/// when a suppressed identity's TTL expires (the re-probe window) — or while an identity is still accumulating
/// strikes below the threshold — exactly ONE caller is let through to re-decode (it takes the
/// <see cref="Entry.ProbeInFlight"/> marker under the lock, creating a zero-strike entry on first encounter);
/// every concurrent caller takes the SKIP path (reported known-timed-out) so N concurrent snapshot loads do not
/// each spawn a fresh strand for the same identity — including N concurrent FIRST loads. The marker is cleared
/// when the prober reports its outcome (<see cref="Seed"/>, <see cref="ClearOnSuccess"/>, or
/// <see cref="ReleaseProbe"/>).</para>
/// <para><b>Bounded LRU with per-entry TTL + backoff re-probe.</b> The cache holds at most <c>capacity</c>
/// entries; on overflow the least-recently-used entry is evicted (never a whole-map wipe). A suppressed entry
/// expires after a (backoff-extended) TTL; a lookup of an expired entry lets one re-probe through so a repaired
/// checkpoint heals without a process restart. Each re-poison of the SAME identity lengthens the TTL
/// exponentially (base × 2^backoffSteps, capped at <c>maxTtl</c>) so a static crafted checkpoint is re-probed
/// logarithmically-rarely over uptime (bounding strand accumulation). A healed checkpoint drifts to the LRU
/// tail and is evicted, so its entry does not leak.</para>
/// <para><b>Seed only on a proven timeout.</b> The caller records a strike ONLY after a decode ran past an
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
    /// <summary>The number of proven timeouts of the SAME identity (across separate loads) required before it
    /// is suppressed (poisoned). The first timeout only records a strike so a healthy-but-slow decode that
    /// timed out once (e.g. under transient CPU oversubscription) is not poisoned into up-to-24h suppression;
    /// a subsequent success clears the history (High #6).</summary>
    internal const int PoisonStrikeThreshold = 2;

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

    /// <summary>Whether <paramref name="key"/> should be SKIPPED (treated as known-timed-out) rather than
    /// re-decoded. Returns <see langword="true"/> when the identity is suppressed and still within its
    /// (backoff-extended) TTL, OR when another caller already holds the single-flight re-probe for this
    /// identity's current window (High #7 — the concurrent SKIP path). Returns <see langword="false"/> — and
    /// takes the single-flight probe marker — for exactly ONE caller per window: the identity is unknown, or
    /// it is in a re-probe/strike-accumulation window and no probe is yet in flight. A caller that gets
    /// <see langword="false"/> MUST report its outcome via <see cref="Seed"/> (timed out again) or
    /// <see cref="ClearOnSuccess"/> (decoded), which releases the probe marker.</summary>
    internal bool IsKnownTimedOut(string key, TimeProvider clock)
    {
        DateTimeOffset now = clock.GetUtcNow();
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                // First encounter of an UNKNOWN (presumed-healthy) identity: admit it WITHOUT taking a probe
                // (Round-8 #11 — the deliberate NON-single-flighted path). Single-flighting a first encounter
                // would force every CONCURRENT first-load of the SAME healthy checkpoint onto the SKIP path →
                // JSON replay; when the log floor is above 0 (commits log-cleaned) that replay hits a gap and
                // the read fails spuriously — a worse outcome than the bounded, rare stranding of concurrent
                // first-loads of a NON-TERMINATING (crafted) checkpoint (each such strand is already capped by
                // the door's residual + strand-count cap). Single-flight therefore engages only ONCE an identity
                // is KNOWN (a re-probe of a suppressed identity or one accumulating strikes below the
                // threshold), where SKIP → JSON replay is the correct outcome anyway.
                return false;
            }

            Entry entry = node.Value;
            bool suppressed = entry.Strikes >= PoisonStrikeThreshold;
            if (suppressed && now < entry.ExpiresAt)
            {
                // Actively suppressed within TTL: refresh LRU recency and skip the re-decode.
                _lru.Remove(node);
                _lru.AddFirst(node);
                return true;
            }

            // Probe window: either an expired suppression (re-probe) or an identity still accumulating strikes
            // below the threshold. Single-flight it (High #7): the first caller takes the probe and re-decodes;
            // every concurrent caller skips so they do not each spawn a strand. Do NOT refresh recency here so a
            // healed entry drifts toward the LRU tail for eviction.
            if (entry.ProbeInFlight)
            {
                return true;
            }

            entry.ProbeInFlight = true;
            return false;
        }
    }

    /// <summary>Records a proven decode timeout for <paramref name="key"/> (increments its strike count) and
    /// releases the single-flight probe marker. The identity is suppressed only once its strike count reaches
    /// <see cref="PoisonStrikeThreshold"/>; each re-poison after that lengthens the TTL exponentially (backoff),
    /// so a static crafted checkpoint is re-probed logarithmically-rarely over uptime. Evicts the
    /// least-recently-used entry if the cache is at capacity.</summary>
    internal void Seed(string key, TimeProvider clock)
    {
        DateTimeOffset now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                Entry e = existing.Value;
                e.Strikes = SaturatingIncrement(e.Strikes);
                e.ProbeInFlight = false;
                // The TTL only matters once suppressed; back it off by how far past the threshold we are so the
                // first poison (Strikes == threshold) gets the base TTL.
                int backoffSteps = Math.Max(e.Strikes - PoisonStrikeThreshold, 0);
                e.ExpiresAt = now + BackoffTtl(backoffSteps);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            // First-ever timeout for this identity: record one strike (NOT yet suppressed — the strike-gate
            // requires >= threshold), with the base TTL as a placeholder (unused until suppressed).
            var node = new LinkedListNode<Entry>(new Entry(key, now + _baseTtl) { Strikes = 1, ProbeInFlight = false });
            _lru.AddFirst(node);
            _byKey[key] = node;
            EvictIfOverCapacity();
        }
    }

    /// <summary>Decrements an identity's strike history by one (floored at zero) after a SUCCESSFUL decode
    /// (High #6 / Round-8 #12) and releases the single-flight probe marker. Pre-fix this REMOVED the entry
    /// entirely, so an input that alternates timeout/success never accumulated toward suppression AND lost all
    /// history each success; a genuinely intermittent input still decays gracefully while a mostly-failing one
    /// keeps its accumulated suspicion (3 timeouts then 1 success stays suppressed at 2, not wiped to 0). The
    /// entry is retained (LRU-subject: a consistently-healthy identity drifts to the LRU tail and is evicted).
    /// A no-op for an identity that was never seeded/encountered.</summary>
    internal void ClearOnSuccess(string key)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                Entry entry = node.Value;
                entry.Strikes = Math.Max(entry.Strikes - 1, 0);
                entry.ProbeInFlight = false;
            }
        }
    }

    // Evicts the least-recently-used entry when the cache is over capacity (never a whole-map wipe). Called
    // under _gate after any insertion.
    private void EvictIfOverCapacity()
    {
        if (_byKey.Count > _capacity)
        {
            LinkedListNode<Entry> lruNode = _lru.Last!;
            _lru.RemoveLast();
            _byKey.Remove(lruNode.Value.Key);
        }
    }

    /// <summary>Releases the single-flight probe marker for <paramref name="key"/> WITHOUT recording a strike or
    /// clearing history — for a prober whose part terminated on an outcome that is neither a proven timeout nor a
    /// clean decode (saturation, an unsupported/encrypted checkpoint, a forged reject, or a corrupt part). The
    /// probe must always be released so a later window is not permanently skipped by a stale in-flight marker.
    /// A no-op for an identity that is not currently cached.</summary>
    internal void ReleaseProbe(string key)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                node.Value.ProbeInFlight = false;
            }
        }
    }

    // base × 2^backoffSteps, capped at maxTtl. Overflow-safe: once the shift would exceed the cap (or overflow),
    // the cap wins.
    private TimeSpan BackoffTtl(int backoffSteps)
    {
        long baseTicks = _baseTtl.Ticks;
        long maxTicks = _maxTtl.Ticks;
        if (backoffSteps <= 0)
        {
            return _baseTtl;
        }

        // Guard against a huge shift: 62 doublings already exceeds any realistic TTL; clamp before shifting.
        int shift = Math.Min(backoffSteps, 62);
        if (backoffSteps >= 62 || baseTicks > (maxTicks >> shift))
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

        // Proven decode timeouts recorded for this identity (across separate loads). Suppression engages at
        // PoisonStrikeThreshold (High #6 strike-gate).
        internal int Strikes { get; set; }

        // The single-flight re-probe marker (High #7): set when exactly one caller is let through to re-decode
        // this identity in its current window; cleared when that caller reports Seed/ClearOnSuccess.
        internal bool ProbeInFlight { get; set; }
    }
}
