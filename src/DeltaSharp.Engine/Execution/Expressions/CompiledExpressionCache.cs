using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>A compiled fused kernel plus the dense input-slot map its driver gathers columns into.</summary>
/// <param name="Kernel">The JIT-compiled per-row delegate.</param>
/// <param name="SlotOrdinals">The distinct referenced column ordinals, in first-encounter (slot) order.</param>
internal readonly record struct CompiledFusion(FusedRowKernel Kernel, int[] SlotOrdinals);

/// <summary>An immutable snapshot of a <see cref="CompiledExpressionCache"/>'s counters.</summary>
/// <param name="Compilations">Number of distinct kernels actually JIT-compiled (cache misses).</param>
/// <param name="Hits">Number of lookups served from an existing entry (no recompile).</param>
/// <param name="Evictions">Number of entries removed by the bounded-capacity policy.</param>
/// <param name="Count">The current number of live entries.</param>
internal readonly record struct CompiledExpressionCacheMetrics(long Compilations, long Hits, long Evictions, int Count);

/// <summary>
/// A bounded, lock-free cache of compiled <see cref="FusedRowKernel"/> delegates keyed by a structural
/// signature of the expression tree (STORY-03.4.2). It guarantees <b>compile-once-per-shape</b>: a hot
/// predicate or projection is lowered and JIT-compiled the first time it is opened and the delegate is
/// reused across every subsequent batch and across structurally-identical expressions, so there is no
/// per-batch recompilation. The cache is consulted at operator <c>Open</c> time (never per row), so it
/// stays off the hot path and the engine remains lock-free: entries use a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> of <see cref="Lazy{T}"/>
/// (<see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>, so a key compiles exactly once even
/// under concurrent first use) and insertion-order eviction uses a <see cref="ConcurrentQueue{T}"/> and
/// <see cref="Interlocked"/> counters — no <c>lock</c>.
/// </summary>
/// <remarks>
/// Annotated <see cref="RequiresDynamicCodeAttribute"/> because it invokes
/// <see cref="CompiledExpressionLowering.Lower"/>; like the rest of the tier it is reachable only behind
/// the dynamic-code feature guard and is elided from NativeAOT.
/// </remarks>
[RequiresDynamicCode(
    "Compiles fused kernels via Expression.Compile (ADR-0001 optional codegen tier); reachable only " +
    "behind the IsCompiledBackendAvailable feature guard and elided from NativeAOT.")]
internal sealed class CompiledExpressionCache
{
    /// <summary>The default bound on live entries before insertion-order eviction begins.</summary>
    public const int DefaultCapacity = 1024;

    private readonly int _capacity;
    private readonly ConcurrentDictionary<string, Lazy<CompiledFusion>> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private long _compilations;
    private long _hits;
    private long _evictions;

    /// <summary>Creates a cache bounded to <paramref name="capacity"/> live entries.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public CompiledExpressionCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <summary>A point-in-time snapshot of the cache counters (for diagnostics and tests).</summary>
    public CompiledExpressionCacheMetrics Metrics => new(
        Interlocked.Read(ref _compilations),
        Interlocked.Read(ref _hits),
        Interlocked.Read(ref _evictions),
        _entries.Count);

    /// <summary>
    /// Returns the compiled kernel for <paramref name="expression"/>, compiling and caching it on the
    /// first encounter of its structural shape and reusing it thereafter.
    /// </summary>
    public CompiledFusion GetOrCompile(PhysicalExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        string key = CompiledExpressionKey.Of(expression);

        if (_entries.TryGetValue(key, out Lazy<CompiledFusion>? existing))
        {
            Interlocked.Increment(ref _hits);
            return existing.Value;
        }

        // The compile runs at most once per key: the winning Lazy's factory increments _compilations;
        // a thread that loses the GetOrAdd race never materializes its own Lazy, so it counts a hit.
        var created = new Lazy<CompiledFusion>(
            () =>
            {
                CompiledFusion fusion = CompiledExpressionLowering.Lower(expression);
                Interlocked.Increment(ref _compilations);
                return fusion;
            },
            LazyThreadSafetyMode.ExecutionAndPublication);

        Lazy<CompiledFusion> actual = _entries.GetOrAdd(key, created);
        if (!ReferenceEquals(actual, created))
        {
            Interlocked.Increment(ref _hits);
            return actual.Value;
        }

        _insertionOrder.Enqueue(key);
        CompiledFusion result = actual.Value; // force compilation outside the dictionary lock
        EvictIfOverCapacity();
        return result;
    }

    private void EvictIfOverCapacity()
    {
        while (_entries.Count > _capacity && _insertionOrder.TryDequeue(out string? oldest))
        {
            // A stale queue entry (already removed) simply advances the queue without counting.
            if (_entries.TryRemove(oldest, out _))
            {
                Interlocked.Increment(ref _evictions);
            }
        }
    }
}
