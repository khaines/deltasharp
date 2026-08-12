using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Unit coverage for the process-wide checkpoint decode <see cref="CheckpointDecodeNegativeCache"/> and its
/// stable-identity key (the Round-2 #647/#699/#716 re-keying, I4). The Round-1 cache was keyed on the transient
/// <see cref="IStorageBackend"/> instance, but production builds a FRESH backend per scan/resolve, so it never
/// hit and a non-terminating checkpoint was re-decoded on every load (self-renewing a detached strand). These
/// tests pin: (a) the stable identity is the same across two fresh local backends rooted at the same table and
/// distinct across different tables; (b) a seeded identity is a hit within its TTL and re-probes (misses) after
/// it; (c) the cache is a bounded LRU that never grows without limit under distinct crafted identities.
/// </summary>
public sealed class CheckpointDecodeNegativeCacheTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    private LocalFileSystemBackend NewBackendAt(string root)
    {
        Directory.CreateDirectory(root);
        if (!_roots.Contains(root))
        {
            _roots.Add(root);
        }

        return new LocalFileSystemBackend(root);
    }

    [Fact]
    public void TableIdentity_IsAbstractInterfaceMember_WithNoDefaultImplementation_AndEveryProductionBackendProvidesIt()
    {
        // High #5 — the instance-hash-default reinstatement guard. The Round-1 Critical was a
        // `RuntimeHelpers.GetHashCode(this)` identity that made the negative cache never hit (and could
        // cross-suppress two tables on a recycled hash). This test locks the fix at the type level: the
        // interface member MUST be abstract (NO default interface implementation), so a future backend that
        // forgets to override it fails to COMPILE rather than silently inheriting an instance-hashed default —
        // and every concrete production backend must supply its own implementation.
        System.Reflection.PropertyInfo? property = typeof(IStorageBackend).GetProperty(nameof(IStorageBackend.TableIdentity));
        Assert.NotNull(property);
        System.Reflection.MethodInfo getter = property!.GetMethod!;
        Assert.True(getter.IsAbstract, "IStorageBackend.TableIdentity must be an ABSTRACT interface member (no default implementation).");

        System.Reflection.Assembly production = typeof(IStorageBackend).Assembly;
        List<Type> implementers = production.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IStorageBackend).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(implementers); // at least LocalFileSystemBackend
        foreach (Type implementer in implementers)
        {
            System.Reflection.InterfaceMapping map = implementer.GetInterfaceMap(typeof(IStorageBackend));
            int index = Array.IndexOf(map.InterfaceMethods, getter);
            Assert.True(index >= 0, $"{implementer.Name} does not map IStorageBackend.TableIdentity.");
            System.Reflection.MethodInfo target = map.TargetMethods[index];
            Assert.False(target.IsAbstract, $"{implementer.Name} does not concretely implement TableIdentity.");
            Assert.Equal(implementer, target.DeclaringType);
        }
    }

    [Fact]
    public void TableIdentity_IsStableAcrossFreshInstances_SameRoot_AndDistinctAcrossRoots()
    {
        // THE re-keying fix (I4): two INDEPENDENT backend instances rooted at the SAME table must expose the
        // SAME identity (so a fresh backend per load hits the cache the previous one seeded), while two
        // different table roots must differ (so a poisoned checkpoint in one table never suppresses a healthy
        // checkpoint in another). Under the Round-1 instance-key this cross-instance stability did not hold.
        string rootA = Path.Combine(Path.GetTempPath(), "neg-cache-id-A-" + Guid.NewGuid().ToString("N"));
        string rootB = Path.Combine(Path.GetTempPath(), "neg-cache-id-B-" + Guid.NewGuid().ToString("N"));

        LocalFileSystemBackend first = NewBackendAt(rootA);
        LocalFileSystemBackend second = NewBackendAt(rootA);
        LocalFileSystemBackend other = NewBackendAt(rootB);

        Assert.NotSame(first, second);
        Assert.Equal(first.TableIdentity, second.TableIdentity);
        Assert.NotEqual(first.TableIdentity, other.TableIdentity);

        // The composed cache key inherits that stability/distinctness for the same part+version.
        const string part = "_delta_log/00000000000000000001.checkpoint.parquet";
        Assert.Equal(
            CheckpointDecodeNegativeCache.Key(first.TableIdentity, part, 1),
            CheckpointDecodeNegativeCache.Key(second.TableIdentity, part, 1));
        Assert.NotEqual(
            CheckpointDecodeNegativeCache.Key(first.TableIdentity, part, 1),
            CheckpointDecodeNegativeCache.Key(other.TableIdentity, part, 1));
    }

    [Fact]
    public void Key_DistinguishesPartAndVersion()
    {
        // A part-only key would wrongly skip a healthy checkpoint at a different version (the part path is
        // version-derived but the guard must not collide across versions either).
        const string identity = "pvc:/tables/t";
        string v1 = CheckpointDecodeNegativeCache.Key(identity, "cp.parquet", 1);
        string v2 = CheckpointDecodeNegativeCache.Key(identity, "cp.parquet", 2);
        string otherPart = CheckpointDecodeNegativeCache.Key(identity, "cp2.parquet", 1);

        Assert.NotEqual(v1, v2);
        Assert.NotEqual(v1, otherPart);
    }

    [Fact]
    public void StrikeGate_SuppressesOnlyAfterThreshold_ThenReProbesAfterTtl()
    {
        // High #6 strike-gate + TTL re-probe: a SINGLE proven timeout does NOT poison an identity (a
        // healthy-but-slow decode that timed out once under transient CPU oversubscription must still be
        // re-decoded). Suppression engages only at the SECOND strike; then the entry is a hit within its TTL and
        // re-probes (single-flight miss) after it, so a repaired checkpoint heals without a restart. Driven by a
        // deterministic manual clock (no wall-clock).
        var clock = new ManualClock(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var ttl = TimeSpan.FromMinutes(10);
        var cache = new CheckpointDecodeNegativeCache(capacity: 8, ttl: ttl);
        string key = CheckpointDecodeNegativeCache.Key("pvc:/t", "cp.parquet", 1);

        Assert.False(cache.IsKnownTimedOut(key, clock)); // never seeded → re-decode

        // First proven timeout: strike 1 only — NOT yet suppressed (strike-gate). Still re-decoded next load.
        cache.Seed(key, clock);
        Assert.False(cache.IsKnownTimedOut(key, clock)); // below threshold → probe window (one caller re-decodes)

        // Second proven timeout: strike 2 == threshold → now SUPPRESSED within TTL.
        cache.Seed(key, clock);
        Assert.True(cache.IsKnownTimedOut(key, clock)); // suppressed hit within TTL

        clock.Advance(ttl - TimeSpan.FromSeconds(1));
        Assert.True(cache.IsKnownTimedOut(key, clock)); // still within TTL

        clock.Advance(TimeSpan.FromSeconds(2)); // now past the TTL
        Assert.False(cache.IsKnownTimedOut(key, clock)); // expired → single-flight re-probe (one caller)
    }

    [Fact]
    public void ClearOnSuccess_ResetsStrikeHistory_SoAOneOffSlowDecodeNeverPoisons()
    {
        // High #6 clear-on-success: an identity that timed out ONCE (strike 1) but then decoded cleanly on a
        // later load must have its strike history cleared, so a subsequent lone timeout starts from strike 1
        // again and never accumulates to suppression across unrelated transient slowdowns.
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var cache = new CheckpointDecodeNegativeCache(capacity: 8, ttl: TimeSpan.FromMinutes(10));
        string key = CheckpointDecodeNegativeCache.Key("pvc:/t", "cp.parquet", 1);

        cache.Seed(key, clock); // strike 1
        cache.ClearOnSuccess(key); // decoded cleanly on a later load → history cleared

        // A fresh lone timeout is strike 1 again (not strike 2), so it is NOT suppressed.
        Assert.False(cache.IsKnownTimedOut(key, clock)); // miss, takes probe
        cache.Seed(key, clock); // strike 1
        Assert.False(cache.IsKnownTimedOut(key, clock)); // still below threshold → re-decode
    }

    [Fact]
    public void Seed_EvictsLeastRecentlyUsed_WhenAtCapacity_NeverGrowsUnbounded()
    {
        // Bounded LRU (not a whole-map wipe): a stream of DISTINCT crafted identities can never grow the cache
        // beyond capacity, and the least-recently-used entry is the one evicted. Each identity is driven to the
        // suppression threshold (two strikes) so IsKnownTimedOut reports presence as a hit.
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var cache = new CheckpointDecodeNegativeCache(capacity: 3, ttl: TimeSpan.FromHours(1));

        string k0 = CheckpointDecodeNegativeCache.Key("pvc:/t", "cp.parquet", 0);
        string k1 = CheckpointDecodeNegativeCache.Key("pvc:/t", "cp.parquet", 1);
        string k2 = CheckpointDecodeNegativeCache.Key("pvc:/t", "cp.parquet", 2);
        string k3 = CheckpointDecodeNegativeCache.Key("pvc:/t", "cp.parquet", 3);

        Suppress(cache, k0, clock);
        Suppress(cache, k1, clock);
        Suppress(cache, k2, clock);

        // Touch k0 so k1 becomes the least-recently-used (recency refreshed on a suppressed hit).
        Assert.True(cache.IsKnownTimedOut(k0, clock));

        // Suppressing a 4th distinct identity evicts the LRU (k1) on its first strike, keeping the cache bounded.
        Suppress(cache, k3, clock);

        Assert.True(cache.IsKnownTimedOut(k0, clock));
        Assert.False(cache.IsKnownTimedOut(k1, clock)); // evicted (was LRU)
        Assert.True(cache.IsKnownTimedOut(k2, clock));
        Assert.True(cache.IsKnownTimedOut(k3, clock));
    }

    [Fact]
    public void Seed_RepeatedlyOnSameIdentity_ExtendsTtlExponentially_BoundingReProbeFrequency()
    {
        // High #4 — durably-bad backoff (uptime-OOM fix), under the strike-gate. Once an identity is suppressed
        // (two strikes), each further re-poison of the SAME identity LENGTHENS the TTL exponentially
        // (base × 2^backoffSteps), so a static crafted checkpoint that keeps timing out is re-probed
        // logarithmically-rarely. Proven deterministically against a fixed-TTL cache (which would re-probe on
        // every base-TTL boundary, spawning a strand each time).
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var baseTtl = TimeSpan.FromMinutes(10);
        var cache = new CheckpointDecodeNegativeCache(capacity: 8, ttl: baseTtl, maxTtl: TimeSpan.FromHours(24));
        string key = CheckpointDecodeNegativeCache.Key("pvc:/t", "cp.parquet", 1);

        // Reach suppression (strike 2) → base TTL. Expire it and re-probe (miss) — the entry is RETAINED so its
        // strike history carries forward to the next seed's backoff.
        cache.Seed(key, clock); // strike 1 (not suppressed)
        cache.Seed(key, clock); // strike 2 → suppressed, base TTL
        Assert.True(cache.IsKnownTimedOut(key, clock)); // suppressed
        clock.Advance(baseTtl + TimeSpan.FromSeconds(1));
        Assert.False(cache.IsKnownTimedOut(key, clock)); // expired → re-probe

        // Re-poison (strike 3): the TTL must now be at least 2× the base. Advancing a FULL base TTL keeps it a
        // HIT — a fixed-TTL cache would already have re-probed here (spawning another strand).
        cache.Seed(key, clock); // strike 3 → 2× base
        clock.Advance(baseTtl + TimeSpan.FromSeconds(1));
        Assert.True(cache.IsKnownTimedOut(key, clock)); // still within the EXTENDED (≥ 2×) TTL — no re-probe

        // And a further strike extends it still more (≥ 4× base): advancing 3× base is still a hit.
        clock.Advance(baseTtl); // now ~2× base since strike 3 → re-probe boundary of a fixed cache
        cache.Seed(key, clock); // strike 4 → 4× base
        clock.Advance(baseTtl * 3 + TimeSpan.FromSeconds(1));
        Assert.True(cache.IsKnownTimedOut(key, clock)); // still within the ≥ 4× TTL
    }

    // Drives an identity to the suppression threshold (two proven timeouts) so IsKnownTimedOut reports it as a
    // suppressed hit.
    private static void Suppress(CheckpointDecodeNegativeCache cache, string key, TimeProvider clock)
    {
        cache.Seed(key, clock);
        cache.Seed(key, clock);
    }

    [Fact]
    public void BackoffTtl_IsCappedAtMaxTtl_NeverOverflows()
    {
        // The exponential backoff must saturate at maxTtl (never overflow to a negative/huge TimeSpan): after
        // MANY strikes on the same identity the effective TTL is clamped at maxTtl, so the entry stays a hit for
        // exactly maxTtl and no longer.
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var baseTtl = TimeSpan.FromMinutes(10);
        var maxTtl = TimeSpan.FromHours(2);
        var cache = new CheckpointDecodeNegativeCache(capacity: 8, ttl: baseTtl, maxTtl: maxTtl);
        string key = CheckpointDecodeNegativeCache.Key("pvc:/t", "cp.parquet", 1);

        // Drive ~40 strikes (2^40 × 10 min would overflow ticks if unclamped).
        for (int i = 0; i < 40; i++)
        {
            cache.Seed(key, clock);
        }

        // Just under maxTtl: still a hit. Just over: re-probe. Proves the clamp holds and did not overflow.
        clock.Advance(maxTtl - TimeSpan.FromSeconds(1));
        Assert.True(cache.IsKnownTimedOut(key, clock));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(cache.IsKnownTimedOut(key, clock));
    }

    // A deterministic, settable clock so the TTL re-probe is exercised without any real wall-clock wait. Only
    // GetUtcNow is consulted by the negative cache.
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan by) => _now += by;
    }
}
