namespace DeltaSharp.Engine.Memory;

/// <summary>
/// The two regions of the unified memory budget (ADR-0013; Spark's <c>UnifiedMemoryManager</c> analog). The budget
/// is shared between them with a soft, shifting boundary so an idle region lends capacity to the busy one.
/// </summary>
public enum MemoryPoolKind
{
    /// <summary>
    /// Non-evictable scratch for sorts, hash tables, joins, and aggregation. It is reclaimed only by spilling the
    /// owning task's spillable reservations (writing in-memory state to a spill target), never by eviction from another
    /// task or region.
    /// </summary>
    Execution,

    /// <summary>
    /// Evictable storage for cached / broadcast batches. It lends its free capacity to execution while idle and is
    /// reclaimed by spilling its own spillable reservations.
    /// </summary>
    Storage,
}

/// <summary>
/// Accounting for one region (<see cref="MemoryPoolKind"/>) of the unified budget — its soft size boundary
/// (<see cref="PoolSizeBytes"/>, which the manager shifts when one region borrows from the other) and the bytes
/// currently reserved (<see cref="UsedBytes"/>).
/// </summary>
/// <remarks>
/// <para>
/// All mutation is performed by the owning <see cref="UnifiedMemoryManager"/> under its single coordinating lock, so a
/// pool is never updated by two threads concurrently and <see cref="UsedBytes"/> never exceeds <see cref="PoolSizeBytes"/>.
/// The public gauges are <see cref="Interlocked"/> reads so an external observer (metrics, a leak assertion at
/// quiescence) never sees a torn 64-bit value; they are <b>not</b> a jointly-consistent multi-field snapshot during
/// live concurrency — read them when the manager is quiescent.
/// </para>
/// </remarks>
public sealed class MemoryPool
{
    private long _poolSizeBytes;
    private long _usedBytes;

    /// <summary>Creates a pool of the given kind sized at <paramref name="initialPoolSizeBytes"/> (its configured region).</summary>
    internal MemoryPool(MemoryPoolKind kind, long initialPoolSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialPoolSizeBytes);
        Kind = kind;
        _poolSizeBytes = initialPoolSizeBytes;
    }

    /// <summary>Which region of the unified budget this pool accounts for.</summary>
    public MemoryPoolKind Kind { get; }

    /// <summary>The pool's current soft size in bytes — its configured region plus/minus any capacity borrowed across the boundary.</summary>
    public long PoolSizeBytes => Interlocked.Read(ref _poolSizeBytes);

    /// <summary>Bytes currently reserved from this pool across all tasks.</summary>
    public long UsedBytes => Interlocked.Read(ref _usedBytes);

    /// <summary>Bytes still reservable from this pool right now (<see cref="PoolSizeBytes"/> − <see cref="UsedBytes"/>), clamped at zero.</summary>
    public long FreeBytes
    {
        get
        {
            long free = Interlocked.Read(ref _poolSizeBytes) - Interlocked.Read(ref _usedBytes);
            return free > 0 ? free : 0;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Mutators and raw reads below are invoked ONLY by UnifiedMemoryManager while it holds its coordinating lock.
    // Writes go through Interlocked so the public gauges above stay torn-free for a lock-free external reader; the
    // *Unlocked reads return the raw fields because the manager already holds the lock (no concurrent writer).
    // ------------------------------------------------------------------------------------------------------------

    internal long PoolSizeUnlocked => _poolSizeBytes;

    internal long UsedUnlocked => _usedBytes;

    internal long FreeUnlocked => _poolSizeBytes - _usedBytes;

    internal void IncrementPoolSize(long bytes) => Interlocked.Add(ref _poolSizeBytes, bytes);

    internal void DecrementPoolSize(long bytes) => Interlocked.Add(ref _poolSizeBytes, -bytes);

    internal void SetPoolSize(long bytes) => Interlocked.Exchange(ref _poolSizeBytes, bytes);

    internal void Acquire(long bytes) => Interlocked.Add(ref _usedBytes, bytes);

    internal void Release(long bytes) => Interlocked.Add(ref _usedBytes, -bytes);
}
