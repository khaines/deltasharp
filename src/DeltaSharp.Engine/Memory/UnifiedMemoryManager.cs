namespace DeltaSharp.Engine.Memory;

/// <summary>
/// The unified execution / storage memory manager (ADR-0013; Spark's <c>UnifiedMemoryManager</c> analog) that bounds an
/// executor's in-memory footprint. It governs one total budget split into an <see cref="ExecutionPool"/> (non-evictable
/// scratch for sorts, joins, aggregation) and a <see cref="StoragePool"/> (evictable cache), with a <b>soft, shifting
/// boundary</b> so an idle region lends its free capacity to the busy one (<i>borrowing</i>). Tasks reserve bytes through
/// per-task <see cref="TaskMemoryManager"/> handles before they physically allocate, and when a reservation would push a
/// pool over budget the manager <b>spills</b> the requesting task's spillable reservations (ADR-0004 spill targets)
/// before rejecting — so memory pressure produces a spill or a deterministic
/// <see cref="MemoryBudgetExceededException"/>, never an <see cref="OutOfMemoryException"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Relationship to the ownership layer.</b> This manager is a <em>reservation</em> (budget) ledger over logical
/// bytes; it does not itself allocate memory. Callers reserve here and then allocate the physical buffers from a
/// <see cref="NativeMemoryAllocator"/>, releasing both in the reverse order. It is the machinery the operator-facing
/// <c>IExecutionMemory</c> seam (STORY-03.1.1) fronts; wiring the two together is a later story.
/// </para>
/// <para>
/// <b>Borrowing model (v1).</b> A pool grows by shifting the boundary into the <em>other pool's free capacity</em>;
/// execution may take all of storage's free space (the storage region only caps execution to the extent storage is
/// actively using memory), and storage may take execution's free space (execution is never evicted). Reclaiming the
/// other pool's <em>used</em> (cached) space by eviction is deferred — see
/// <c>docs/engineering/design/memory-model.md</c>. <see cref="StorageRegionBytes"/> sets the initial boundary and is the
/// protected floor that future eviction will respect.
/// </para>
/// <para>
/// <b>Threading.</b> Per-field byte counters are <see cref="Interlocked"/> (torn-free observation, matching
/// <see cref="NativeMemoryAllocator"/>), but every reserve / release / spill <em>decision</em> is serialized by one
/// coordinating lock, because it must atomically read-and-shift two pools and orchestrate spills — a multi-variable
/// invariant <see cref="Interlocked"/> alone cannot maintain. Spill callbacks are invoked under that lock, so a v1
/// <see cref="ISpillable.Spill(long)"/> must be fast and must not re-enter the manager.
/// </para>
/// </remarks>
public sealed class UnifiedMemoryManager
{
    /// <summary>The default fraction of the budget reserved as the storage region (Spark's <c>spark.memory.storageFraction</c> default of 0.5).</summary>
    public const double DefaultStorageRegionFraction = 0.5;

    private readonly object _lock = new();
    private bool _inSpill;
    private readonly long _executionRegionBytes;
    private readonly long _storageRegionBytes;

    /// <summary>Creates a manager over <paramref name="maxMemoryBytes"/> using the <see cref="DefaultStorageRegionFraction"/> split.</summary>
    /// <param name="maxMemoryBytes">The total unified budget in bytes (non-negative).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxMemoryBytes"/> is negative.</exception>
    public UnifiedMemoryManager(long maxMemoryBytes)
        : this(maxMemoryBytes, DefaultStorageRegionFraction)
    {
    }

    /// <summary>Creates a manager over <paramref name="maxMemoryBytes"/> with the storage region sized at <paramref name="storageRegionFraction"/> of the budget.</summary>
    /// <param name="maxMemoryBytes">The total unified budget in bytes (non-negative).</param>
    /// <param name="storageRegionFraction">The fraction of the budget reserved as the storage region, in [0, 1].</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxMemoryBytes"/> is negative, or <paramref name="storageRegionFraction"/> is NaN or outside [0, 1].</exception>
    public UnifiedMemoryManager(long maxMemoryBytes, double storageRegionFraction)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxMemoryBytes);
        if (double.IsNaN(storageRegionFraction) || storageRegionFraction < 0.0 || storageRegionFraction > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(storageRegionFraction), storageRegionFraction, "Storage region fraction must be in [0, 1].");
        }

        MaxMemoryBytes = maxMemoryBytes;
        _storageRegionBytes = (long)(maxMemoryBytes * storageRegionFraction);
        _executionRegionBytes = maxMemoryBytes - _storageRegionBytes;
        ExecutionPool = new MemoryPool(MemoryPoolKind.Execution, _executionRegionBytes);
        StoragePool = new MemoryPool(MemoryPoolKind.Storage, _storageRegionBytes);
    }

    /// <summary>The total unified budget in bytes shared by the execution and storage pools.</summary>
    public long MaxMemoryBytes { get; }

    /// <summary>The storage region floor: the storage pool's initial size and the capacity future eviction will keep reserved for storage.</summary>
    public long StorageRegionBytes => _storageRegionBytes;

    /// <summary>The execution region: the execution pool's initial size (<see cref="MaxMemoryBytes"/> − <see cref="StorageRegionBytes"/>).</summary>
    public long ExecutionRegionBytes => _executionRegionBytes;

    /// <summary>The execution pool — non-evictable scratch, reclaimed only by spilling its owning tasks' reservations.</summary>
    public MemoryPool ExecutionPool { get; }

    /// <summary>The storage pool — evictable cache that lends idle capacity to execution.</summary>
    public MemoryPool StoragePool { get; }

    /// <summary>Bytes currently reserved across both pools.</summary>
    public long TotalUsedBytes => ExecutionPool.UsedBytes + StoragePool.UsedBytes;

    /// <summary>Bytes still reservable across the whole budget (<see cref="MaxMemoryBytes"/> − <see cref="TotalUsedBytes"/>).</summary>
    public long TotalFreeBytes => MaxMemoryBytes - TotalUsedBytes;

    /// <summary>Creates a per-task reservation handle for <paramref name="taskId"/>. Dispose it to release the task's remaining reservations.</summary>
    /// <param name="taskId">A caller-supplied task identifier (deterministic; surfaced in budget-exceeded errors).</param>
    public TaskMemoryManager RegisterTask(long taskId) => new(this, taskId);

    /// <summary>
    /// Core reservation path used by <see cref="TaskMemoryManager"/>. Under the coordinating lock it borrows the other
    /// pool's free capacity, then spills the requesting task's own spillable reservations, and finally either charges
    /// the pool / task or fails (returning <see langword="null"/> or throwing a deterministic
    /// <see cref="MemoryBudgetExceededException"/> per <paramref name="throwOnFailure"/>).
    /// </summary>
    internal MemoryReservation? ReserveCore(
        TaskMemoryManager task, MemoryPoolKind kind, long bytes, ISpillable? spillable, bool throwOnFailure)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        lock (_lock)
        {
            // Re-check disposal under the lock: the public Try/Reserve pre-check runs outside the lock, so a
            // concurrent Dispose() could otherwise win the race and let a reservation be charged to a disposed
            // task (TOCTOU). A disposed task always rejects, matching the documented ObjectDisposedException.
            ObjectDisposedException.ThrowIf(task.IsDisposed, task);

            // `lock` is reentrant on the same thread, so a misbehaving ISpillable.Spill that re-enters Reserve
            // would silently mutate the reservation list being iterated. Fail fast instead of corrupting accounting.
            if (_inSpill)
            {
                throw new InvalidOperationException("Cannot reserve memory from within an ISpillable.Spill callback.");
            }

            MemoryPool pool = PoolFor(kind);

            // A zero-byte reservation always succeeds and tracks a 0-byte handle (so callers get uniform dispose
            // semantics regardless of size); it never moves a counter.
            if (bytes > 0)
            {
                // (1) Borrow the other pool's free capacity by shifting the soft boundary toward this pool.
                long shortfall = bytes - pool.FreeUnlocked;
                if (shortfall > 0)
                {
                    MemoryPool other = PoolFor(Other(kind));
                    long borrow = Math.Min(shortfall, other.FreeUnlocked);
                    if (borrow > 0)
                    {
                        other.DecrementPoolSize(borrow);
                        pool.IncrementPoolSize(borrow);
                    }
                }

                // (2) If still short, spill THIS task's own spillable reservations in this pool. Spilling only the
                //     requesting task's reservations keeps a concurrent task's accounting untouched (AC4).
                shortfall = bytes - pool.FreeUnlocked;
                if (shortfall > 0)
                {
                    SpillTaskReservations(task, kind, shortfall);

                    // A spill callback runs arbitrary user code under the reentrant lock and may have
                    // disposed THIS task (its ReleaseAll then releases and clears every reservation).
                    // The top-of-method disposed check ran *before* the spill, so re-check now: charging
                    // or appending a reservation to a task disposed mid-spill orphans it permanently (the
                    // task's ReleaseAll has already run and will not run again) — an unrecoverable leak.
                    ObjectDisposedException.ThrowIf(task.IsDisposed, task);
                }

                // (3) Charge the pool and task if it now fits; otherwise spill-or-fail (never OOM).
                if (pool.FreeUnlocked < bytes)
                {
                    return throwOnFailure ? throw BuildBudgetExceeded(task, kind, bytes) : null;
                }

                pool.Acquire(bytes);
                task.AddUsedUnlocked(kind, bytes);
            }

            var reservation = new MemoryReservation(this, task, kind, bytes, spillable);
            task.AddReservationUnlocked(reservation);
            return reservation;
        }
    }

    /// <summary>Releases a single reservation's remaining bytes to its pool and task exactly once (idempotent under double-dispose and disposer races).</summary>
    internal void ReleaseReservation(MemoryReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        lock (_lock)
        {
            if (_inSpill)
            {
                throw new InvalidOperationException("Cannot release memory from within an ISpillable.Spill callback.");
            }

            if (!reservation.TryMarkReleasedUnlocked())
            {
                return; // already released — double-dispose is a safe no-op.
            }

            ReleaseLatchedReservationUnlocked(reservation);
            reservation.Task.RemoveReservationUnlocked(reservation);
            ResetBoundaryIfIdleUnlocked();
        }
    }

    /// <summary>Releases every reservation a task still holds, then resets the boundary if the manager is now idle (task disposal / release discipline).</summary>
    internal void ReleaseAll(TaskMemoryManager task)
    {
        ArgumentNullException.ThrowIfNull(task);

        lock (_lock)
        {
            List<MemoryReservation> reservations = task.ReservationsUnlocked;
            for (int i = reservations.Count - 1; i >= 0; i--)
            {
                MemoryReservation reservation = reservations[i];
                if (reservation.TryMarkReleasedUnlocked())
                {
                    ReleaseLatchedReservationUnlocked(reservation);
                }
            }

            reservations.Clear();
            ResetBoundaryIfIdleUnlocked();
        }
    }

    // Decrements the pool and task by a reservation's remaining bytes and zeroes it. Caller holds the lock and has
    // already latched the released flag exactly once.
    private void ReleaseLatchedReservationUnlocked(MemoryReservation reservation)
    {
        long bytes = reservation.ReservedUnlocked;
        if (bytes > 0)
        {
            PoolFor(reservation.Kind).Release(bytes);
            reservation.Task.AddUsedUnlocked(reservation.Kind, -bytes);
            reservation.ZeroReservedUnlocked();
        }
    }

    // Asks the requesting task's spillable reservations in `kind` to release up to `target` bytes, charging the freed
    // amount back to the pool and task (AC5: a successful spill releases the corresponding reservation). Spilling in
    // list order is correct; largest-first is a future optimization. Invoked under the lock.
    private void SpillTaskReservations(TaskMemoryManager task, MemoryPoolKind kind, long target)
    {
        MemoryPool pool = PoolFor(kind);
        long remaining = target;
        List<MemoryReservation> reservations = task.ReservationsUnlocked;

        _inSpill = true;
        try
        {
            for (int i = 0; i < reservations.Count && remaining > 0; i++)
            {
                MemoryReservation reservation = reservations[i];
                if (reservation.Kind != kind || reservation.Spillable is null || reservation.ReservedUnlocked <= 0)
                {
                    continue;
                }

                long freed = reservation.Spillable.Spill(remaining);
                if (freed <= 0)
                {
                    continue;
                }

                // Defensive clamp: a well-behaved spillable never frees more than it reserved, but the manager must not
                // over-release the pool/task if it does.
                if (freed > reservation.ReservedUnlocked)
                {
                    freed = reservation.ReservedUnlocked;
                }

                pool.Release(freed);
                task.AddUsedUnlocked(kind, -freed);
                reservation.ReduceByUnlocked(freed);
                remaining -= freed;
            }
        }
        finally
        {
            _inSpill = false;
        }
    }

    // When both pools are fully released, restore the soft boundary to the configured regions so the manager returns
    // to its exact initial accounting baseline (deterministic accounting). Invoked under the lock.
    private void ResetBoundaryIfIdleUnlocked()
    {
        if (ExecutionPool.UsedUnlocked == 0 && StoragePool.UsedUnlocked == 0)
        {
            ExecutionPool.SetPoolSize(_executionRegionBytes);
            StoragePool.SetPoolSize(_storageRegionBytes);
        }
    }

    private MemoryBudgetExceededException BuildBudgetExceeded(TaskMemoryManager task, MemoryPoolKind kind, long bytes)
    {
        MemoryPool pool = PoolFor(kind);
        return new MemoryBudgetExceededException(
            taskId: task.TaskId,
            pool: kind,
            requestedBytes: bytes,
            poolUsedBytes: pool.UsedUnlocked,
            poolSizeBytes: pool.PoolSizeUnlocked,
            executionUsedBytes: ExecutionPool.UsedUnlocked,
            executionPoolSizeBytes: ExecutionPool.PoolSizeUnlocked,
            storageUsedBytes: StoragePool.UsedUnlocked,
            storagePoolSizeBytes: StoragePool.PoolSizeUnlocked,
            maxMemoryBytes: MaxMemoryBytes);
    }

    private MemoryPool PoolFor(MemoryPoolKind kind)
        => kind == MemoryPoolKind.Execution ? ExecutionPool : StoragePool;

    private static MemoryPoolKind Other(MemoryPoolKind kind)
        => kind == MemoryPoolKind.Execution ? MemoryPoolKind.Storage : MemoryPoolKind.Execution;
}
