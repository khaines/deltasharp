namespace DeltaSharp.Engine.Memory;

/// <summary>
/// The per-task reservation handle against a shared <see cref="UnifiedMemoryManager"/> (ADR-0013; Spark's
/// <c>TaskMemoryManager</c> analog). A task (or operator) reserves execution / storage bytes through this handle before
/// it physically allocates them, and the handle tracks <b>task ownership</b> of those bytes so the manager can report
/// per-task usage and so disposing the task releases everything it still holds.
/// </summary>
/// <remarks>
/// <para>
/// Reservations are charged against the manager's shared pools, not a private per-task arena, so concurrent tasks
/// compete for one budget; what is "per-task" is the <em>ownership accounting</em> (<see cref="ExecutionUsedBytes"/> /
/// <see cref="StorageUsedBytes"/>) and the spill scope — the manager only ever spills the <em>requesting</em> task's own
/// spillable reservations, so one task spilling never decrements or leaks another task's accounting.
/// </para>
/// <para>
/// <b>Release discipline.</b> Each reservation is released by disposing its <see cref="MemoryReservation"/>; disposing
/// the task (e.g. when the task attempt completes) releases every reservation it still holds, so a task cannot leak
/// budget. The handle is thread-safe: its mutating operations run under the manager's coordinating lock.
/// </para>
/// </remarks>
public sealed class TaskMemoryManager : IDisposable
{
    private readonly UnifiedMemoryManager _manager;
    private readonly List<MemoryReservation> _reservations = [];
    private long _executionUsedBytes;
    private long _storageUsedBytes;
    private int _disposed;

    internal TaskMemoryManager(UnifiedMemoryManager manager, long taskId)
    {
        _manager = manager;
        TaskId = taskId;
    }

    /// <summary>This task's identifier (supplied by the caller; deterministic and reproducible across runs).</summary>
    public long TaskId { get; }

    /// <summary>Bytes this task currently holds in the execution pool.</summary>
    public long ExecutionUsedBytes => Interlocked.Read(ref _executionUsedBytes);

    /// <summary>Bytes this task currently holds in the storage pool.</summary>
    public long StorageUsedBytes => Interlocked.Read(ref _storageUsedBytes);

    /// <summary>Bytes this task currently holds across both pools.</summary>
    public long TotalUsedBytes => ExecutionUsedBytes + StorageUsedBytes;

    /// <summary>Whether this task handle has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Bytes this task currently holds in the given <paramref name="kind"/> pool.</summary>
    public long UsedBytes(MemoryPoolKind kind)
        => kind == MemoryPoolKind.Execution ? ExecutionUsedBytes : StorageUsedBytes;

    /// <summary>
    /// Attempts to reserve <paramref name="bytes"/> from the <paramref name="kind"/> pool, borrowing the other pool's
    /// idle capacity and spilling this task's spillable reservations as needed. Returns the reservation, or
    /// <see langword="null"/> when the budget cannot satisfy it even after spilling (the caller should then spill more,
    /// degrade, or fail — never an <see cref="OutOfMemoryException"/>).
    /// </summary>
    /// <param name="kind">The pool to charge.</param>
    /// <param name="bytes">The bytes to reserve (non-negative).</param>
    /// <param name="spillable">An optional spillable consumer the manager may ask to release these bytes under pressure.</param>
    /// <returns>The reservation handle, or <see langword="null"/> if it could not be satisfied.</returns>
    /// <exception cref="ObjectDisposedException">The task handle has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    public MemoryReservation? TryReserve(MemoryPoolKind kind, long bytes, ISpillable? spillable = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return _manager.ReserveCore(this, kind, bytes, spillable, throwOnFailure: false);
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> from the <paramref name="kind"/> pool like
    /// <see cref="TryReserve(MemoryPoolKind, long, ISpillable?)"/>, but throws a deterministic
    /// <see cref="MemoryBudgetExceededException"/> (carrying the task id, requested bytes, and pool state) instead of
    /// returning <see langword="null"/> when the budget cannot satisfy it after spilling.
    /// </summary>
    /// <param name="kind">The pool to charge.</param>
    /// <param name="bytes">The bytes to reserve (non-negative).</param>
    /// <param name="spillable">An optional spillable consumer the manager may ask to release these bytes under pressure.</param>
    /// <returns>The reservation handle.</returns>
    /// <exception cref="ObjectDisposedException">The task handle has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    /// <exception cref="MemoryBudgetExceededException">The reservation could not be satisfied after spilling.</exception>
    public MemoryReservation Reserve(MemoryPoolKind kind, long bytes, ISpillable? spillable = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return _manager.ReserveCore(this, kind, bytes, spillable, throwOnFailure: true)!;
    }

    /// <summary>Releases every reservation this task still holds, exactly once. Disposing twice is a safe no-op.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _manager.ReleaseAll(this);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Internal state mutated ONLY by UnifiedMemoryManager under its coordinating lock. Per-task byte counters use
    // Interlocked so the public gauges above stay torn-free for a lock-free external reader.
    // ------------------------------------------------------------------------------------------------------------

    internal List<MemoryReservation> ReservationsUnlocked => _reservations;

    internal void AddReservationUnlocked(MemoryReservation reservation) => _reservations.Add(reservation);

    internal void RemoveReservationUnlocked(MemoryReservation reservation) => _reservations.Remove(reservation);

    internal void AddUsedUnlocked(MemoryPoolKind kind, long bytes)
    {
        if (kind == MemoryPoolKind.Execution)
        {
            Interlocked.Add(ref _executionUsedBytes, bytes);
        }
        else
        {
            Interlocked.Add(ref _storageUsedBytes, bytes);
        }
    }
}
