namespace DeltaSharp.Engine.Memory;

/// <summary>
/// A single memory reservation handed out by a <see cref="TaskMemoryManager"/> (ADR-0013) — the unit of execution /
/// storage budget a task holds against the shared <see cref="UnifiedMemoryManager"/>. It is the budget analogue of an
/// <see cref="OwnedBuffer"/>: a task reserves bytes <em>before</em> it physically allocates them, and releases the
/// reservation exactly once when done.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly-once release.</b> <see cref="Dispose"/> returns the reservation's remaining bytes to its pool and task
/// exactly once, guarded by an <see cref="Interlocked"/> flag, so a double <see cref="Dispose"/> (or a race between two
/// disposers) is a safe no-op and the pool / task totals never go negative.
/// </para>
/// <para>
/// <b>Shrink-on-spill.</b> If the reservation is <see cref="IsSpillable"/> and the manager spills it under memory
/// pressure, the manager reduces <see cref="ReservedBytes"/> by the spilled amount (releasing that much from the pool
/// and task). A later <see cref="Dispose"/> then releases only the <em>remaining</em> bytes, so the lifetime total
/// released always equals the bytes reserved — accounting balances to zero.
/// </para>
/// <para>A reservation is owned by one task and is not intended for concurrent use beyond the disposal race above.</para>
/// </remarks>
public sealed class MemoryReservation : IDisposable
{
    private readonly UnifiedMemoryManager _manager;
    private long _reservedBytes;

    // 0 = live, 1 = released. Interlocked-guarded so the pool/task decrement runs exactly once.
    private int _released;

    internal MemoryReservation(UnifiedMemoryManager manager, TaskMemoryManager task, MemoryPoolKind kind, long bytes, ISpillable? spillable)
    {
        _manager = manager;
        Task = task;
        Kind = kind;
        _reservedBytes = bytes;
        Spillable = spillable;
    }

    /// <summary>The id of the task that owns this reservation.</summary>
    public long TaskId => Task.TaskId;

    /// <summary>Which pool this reservation is charged against.</summary>
    public MemoryPoolKind Kind { get; }

    /// <summary>The bytes still reserved by this handle — the originally reserved amount minus anything already spilled.</summary>
    public long ReservedBytes => Interlocked.Read(ref _reservedBytes);

    /// <summary>Whether this reservation has been released (disposed).</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <summary>Whether this reservation registered an <see cref="ISpillable"/> the manager may spill under pressure.</summary>
    public bool IsSpillable => Spillable is not null;

    internal TaskMemoryManager Task { get; }

    internal ISpillable? Spillable { get; }

    /// <summary>Raw reserved bytes; read by the manager under its lock.</summary>
    internal long ReservedUnlocked => _reservedBytes;

    /// <summary>Reduces the reserved bytes by <paramref name="bytes"/>; called by the manager under its lock when this reservation spills.</summary>
    internal void ReduceByUnlocked(long bytes) => Interlocked.Add(ref _reservedBytes, -bytes);

    /// <summary>Latches the released flag, returning <see langword="true"/> exactly once (the first caller); the manager holds its lock.</summary>
    internal bool TryMarkReleasedUnlocked() => Interlocked.Exchange(ref _released, 1) == 0;

    /// <summary>Zeroes the reserved bytes after release; called by the manager under its lock.</summary>
    internal void ZeroReservedUnlocked() => Interlocked.Exchange(ref _reservedBytes, 0);

    /// <summary>
    /// Releases this reservation's remaining bytes back to its pool and task exactly once. Idempotent: a second
    /// <see cref="Dispose"/> is a no-op, so the totals never double-decrement.
    /// </summary>
    public void Dispose() => _manager.ReleaseReservation(this);
}
