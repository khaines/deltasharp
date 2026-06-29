using DeltaSharp.Engine.Memory;
using Xunit;

namespace DeltaSharp.Engine.Tests.Memory;

/// <summary>
/// STORY-02.3.2 AC1/AC3 and the borrowing contract: the unified manager reports available / used bytes and task
/// ownership accurately, lets one pool borrow the other's idle capacity across the shared budget, balances every
/// reservation back to a zero baseline on release, and fails an over-budget reservation with a deterministic
/// budget-exceeded error (never an <see cref="OutOfMemoryException"/>).
/// </summary>
public class UnifiedMemoryManagerTests
{
    [Fact]
    public void NewManager_StartsAtConfiguredRegions_WithEverythingFree()
    {
        var manager = new UnifiedMemoryManager(1000, 0.5);

        Assert.Equal(1000, manager.MaxMemoryBytes);
        Assert.Equal(500, manager.StorageRegionBytes);
        Assert.Equal(500, manager.ExecutionRegionBytes);

        Assert.Equal(MemoryPoolKind.Execution, manager.ExecutionPool.Kind);
        Assert.Equal(500, manager.ExecutionPool.PoolSizeBytes);
        Assert.Equal(0, manager.ExecutionPool.UsedBytes);
        Assert.Equal(500, manager.ExecutionPool.FreeBytes);

        Assert.Equal(MemoryPoolKind.Storage, manager.StoragePool.Kind);
        Assert.Equal(500, manager.StoragePool.PoolSizeBytes);
        Assert.Equal(0, manager.StoragePool.UsedBytes);
        Assert.Equal(500, manager.StoragePool.FreeBytes);

        Assert.Equal(0, manager.TotalUsedBytes);
        Assert.Equal(1000, manager.TotalFreeBytes);
    }

    [Fact]
    public void Reserve_ChargesPoolAndTask_ReportsUsedAndOwnershipAccurately() // AC1
    {
        var manager = new UnifiedMemoryManager(1000);
        using TaskMemoryManager task = manager.RegisterTask(taskId: 42);

        MemoryReservation reservation = task.Reserve(MemoryPoolKind.Execution, 120);

        // Pool accounting.
        Assert.Equal(120, manager.ExecutionPool.UsedBytes);
        Assert.Equal(380, manager.ExecutionPool.FreeBytes);
        Assert.Equal(0, manager.StoragePool.UsedBytes);
        Assert.Equal(120, manager.TotalUsedBytes);
        Assert.Equal(880, manager.TotalFreeBytes);

        // Task ownership.
        Assert.Equal(42, task.TaskId);
        Assert.Equal(120, task.ExecutionUsedBytes);
        Assert.Equal(0, task.StorageUsedBytes);
        Assert.Equal(120, task.TotalUsedBytes);
        Assert.Equal(120, task.UsedBytes(MemoryPoolKind.Execution));

        // Reservation handle.
        Assert.Equal(42, reservation.TaskId);
        Assert.Equal(MemoryPoolKind.Execution, reservation.Kind);
        Assert.Equal(120, reservation.ReservedBytes);
        Assert.False(reservation.IsReleased);
        Assert.False(reservation.IsSpillable);
    }

    [Fact]
    public void ExecutionPool_BorrowsStorageFreeSpace_WhenOverItsRegion() // borrowing across pools
    {
        var manager = new UnifiedMemoryManager(1000, 0.5);
        using TaskMemoryManager task = manager.RegisterTask(1);

        // 800 > the 500 execution region, so execution borrows 300 of storage's free capacity.
        MemoryReservation big = task.Reserve(MemoryPoolKind.Execution, 800);

        Assert.Equal(800, manager.ExecutionPool.PoolSizeBytes);
        Assert.Equal(800, manager.ExecutionPool.UsedBytes);
        Assert.Equal(0, manager.ExecutionPool.FreeBytes);
        Assert.Equal(200, manager.StoragePool.PoolSizeBytes); // shrunk by the 300 lent to execution
        Assert.Equal(200, manager.StoragePool.FreeBytes);
        Assert.Equal(800, task.ExecutionUsedBytes);

        // Storage can still use what is left of its (shrunk) pool, but not more — there is nothing free to borrow back.
        using TaskMemoryManager storageTask = manager.RegisterTask(2);
        Assert.NotNull(storageTask.TryReserve(MemoryPoolKind.Storage, 200));
        Assert.Null(storageTask.TryReserve(MemoryPoolKind.Storage, 1)); // pool exhausted, execution has no free space to lend

        big.Dispose();
    }

    [Fact]
    public void StoragePool_BorrowsExecutionFreeSpace_WhenOverItsRegion() // borrowing the other direction
    {
        var manager = new UnifiedMemoryManager(1000, 0.5);
        using TaskMemoryManager task = manager.RegisterTask(7);

        MemoryReservation cached = task.Reserve(MemoryPoolKind.Storage, 800);

        Assert.Equal(800, manager.StoragePool.PoolSizeBytes);
        Assert.Equal(800, manager.StoragePool.UsedBytes);
        Assert.Equal(200, manager.ExecutionPool.PoolSizeBytes); // shrunk by the 300 lent to storage
        Assert.Equal(800, task.StorageUsedBytes);

        cached.Dispose();
    }

    [Fact]
    public void DisposingReservationsAndTask_ReturnsAllPoolsAndBoundaryToBaseline() // reservation/release balances to zero
    {
        var manager = new UnifiedMemoryManager(1000, 0.5);
        var task = manager.RegisterTask(99);

        // Execution borrows from storage so the boundary is shifted away from the regions mid-flight.
        MemoryReservation e = task.Reserve(MemoryPoolKind.Execution, 700); // borrows 200 from storage (boundary now 700/300)
        MemoryReservation s = task.Reserve(MemoryPoolKind.Storage, 250);   // fits in storage's remaining 300, no borrow
        Assert.True(manager.TotalUsedBytes > 0);

        e.Dispose();
        s.Dispose();
        task.Dispose();

        // Used balances to zero AND the soft boundary resets to the configured regions (deterministic baseline).
        Assert.Equal(0, manager.ExecutionPool.UsedBytes);
        Assert.Equal(0, manager.StoragePool.UsedBytes);
        Assert.Equal(0, manager.TotalUsedBytes);
        Assert.Equal(1000, manager.TotalFreeBytes);
        Assert.Equal(500, manager.ExecutionPool.PoolSizeBytes);
        Assert.Equal(500, manager.StoragePool.PoolSizeBytes);
    }

    [Fact]
    public void Reserve_BeyondBudget_NoSpillables_ThrowsDeterministicBudgetExceeded() // AC3
    {
        var manager = new UnifiedMemoryManager(1000);
        using TaskMemoryManager task = manager.RegisterTask(taskId: 5);
        MemoryReservation first = task.Reserve(MemoryPoolKind.Execution, 600); // borrows 100 from storage

        MemoryBudgetExceededException ex = Assert.Throws<MemoryBudgetExceededException>(
            () => task.Reserve(MemoryPoolKind.Execution, 600));

        Assert.Equal(5, ex.TaskId);
        Assert.Equal(MemoryPoolKind.Execution, ex.Pool);
        Assert.Equal(600, ex.RequestedBytes);
        Assert.Equal(600, ex.PoolUsedBytes);     // the first reservation still held
        Assert.Equal(1000, ex.PoolSizeBytes);    // execution grew to the whole budget by borrowing
        Assert.Equal(1000, ex.MaxMemoryBytes);

        // Deterministic, reproducible message carries the task id, requested bytes, and pool.
        Assert.Contains("Task 5", ex.Message, StringComparison.Ordinal);
        Assert.Contains("600 bytes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Execution", ex.Message, StringComparison.Ordinal);

        // The failed reservation left no leaked accounting.
        Assert.Equal(600, manager.ExecutionPool.UsedBytes);
        first.Dispose();
    }

    [Fact]
    public void TryReserve_BeyondBudget_ReturnsNull_NotOom() // spill-or-fail, never OOM
    {
        var manager = new UnifiedMemoryManager(1000);
        using TaskMemoryManager task = manager.RegisterTask(5);
        using MemoryReservation first = task.Reserve(MemoryPoolKind.Execution, 600);

        MemoryReservation? second = task.TryReserve(MemoryPoolKind.Execution, 600);

        Assert.Null(second);
        Assert.Equal(600, manager.ExecutionPool.UsedBytes); // unchanged: no partial charge, no leak
    }

    [Fact]
    public void ZeroByteReservation_Succeeds_AndTracksADisposableHandle()
    {
        var manager = new UnifiedMemoryManager(1000);
        using TaskMemoryManager task = manager.RegisterTask(1);

        MemoryReservation zero = task.Reserve(MemoryPoolKind.Execution, 0);

        Assert.Equal(0, zero.ReservedBytes);
        Assert.Equal(0, manager.TotalUsedBytes);
        zero.Dispose();
        Assert.True(zero.IsReleased);
    }

    [Theory]
    [InlineData(-1)]
    public void NegativeBudget_Throws(long maxMemory)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new UnifiedMemoryManager(maxMemory));

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void StorageRegionFraction_OutOfRange_Throws(double fraction)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new UnifiedMemoryManager(1000, fraction));

    [Fact]
    public void NegativeReservation_Throws()
    {
        var manager = new UnifiedMemoryManager(1000);
        using TaskMemoryManager task = manager.RegisterTask(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => task.TryReserve(MemoryPoolKind.Execution, -1));
    }

    [Fact]
    public void ReserveAfterDispose_Throws_NoPostDisposeReservation()
    {
        var manager = new UnifiedMemoryManager(1000);
        TaskMemoryManager task = manager.RegisterTask(1);
        task.Dispose();

        // The in-lock disposed re-check rejects a reserve on a disposed task (TOCTOU guard).
        Assert.Throws<ObjectDisposedException>(() => task.Reserve(MemoryPoolKind.Execution, 100));
        Assert.Throws<ObjectDisposedException>(() => task.TryReserve(MemoryPoolKind.Execution, 100));
        Assert.Equal(0, manager.ExecutionPool.UsedBytes);
    }

    [Fact]
    public void ReserveFromWithinSpillCallback_Throws()
    {
        var manager = new UnifiedMemoryManager(200, storageRegionFraction: 0.0);
        using TaskMemoryManager task = manager.RegisterTask(1);

        // A spillable that re-enters Reserve during its own Spill must fail fast (reentrancy guard), not corrupt
        // the reservation list being iterated under the reentrant lock.
        var reentrant = new DelegateSpillable(_ =>
        {
            task.TryReserve(MemoryPoolKind.Execution, 1);
            return 0;
        });
        task.Reserve(MemoryPoolKind.Execution, 150, reentrant);

        // The next reservation forces a spill, which invokes the reentrant callback.
        Assert.Throws<InvalidOperationException>(() => task.Reserve(MemoryPoolKind.Execution, 100));
    }
}
