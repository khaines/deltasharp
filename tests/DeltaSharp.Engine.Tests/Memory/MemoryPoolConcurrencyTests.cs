using DeltaSharp.Engine.Memory;
using Xunit;

namespace DeltaSharp.Engine.Tests.Memory;

/// <summary>
/// STORY-02.3.2 AC4 and the release-discipline / thread-safety contract: spilling one task never touches another task's
/// accounting, concurrent reserve / release across tasks balances back to a zero baseline, releasing a reservation is
/// exactly-once (double-dispose and disposer races are safe no-ops), and disposing a task releases everything it holds.
/// </summary>
public class MemoryPoolConcurrencyTests
{
    [Fact]
    public void OneTaskSpilling_DoesNotDecrementOrLeakAnotherTasksAccounting() // AC4
    {
        var manager = new UnifiedMemoryManager(1000);
        using TaskMemoryManager taskA = manager.RegisterTask(1);
        using TaskMemoryManager taskB = manager.RegisterTask(2);

        var spillA = new CountingSpillable();
        var spillB = new CountingSpillable();

        using MemoryReservation a1 = taskA.Reserve(MemoryPoolKind.Execution, 300, spillA);
        using MemoryReservation b1 = taskB.Reserve(MemoryPoolKind.Execution, 300, spillB);

        Assert.Equal(300, taskB.ExecutionUsedBytes);
        Assert.Equal(300, b1.ReservedBytes);

        // Task A grows past the budget, which spills A's own reservation. B must be completely untouched.
        using MemoryReservation a2 = taskA.Reserve(MemoryPoolKind.Execution, 600);

        Assert.Equal(1, spillA.SpillCalls);
        Assert.Equal(0, spillB.SpillCalls);            // B's spill callback never invoked
        Assert.Equal(300, b1.ReservedBytes);           // B's reservation unchanged
        Assert.Equal(300, taskB.ExecutionUsedBytes);   // B's accounting neither decremented nor leaked

        Assert.Equal(100, a1.ReservedBytes);           // A spilled 200 of its 300
        Assert.Equal(700, taskA.ExecutionUsedBytes);   // 100 (shrunk a1) + 600 (a2)
        Assert.Equal(1000, manager.ExecutionPool.UsedBytes);
    }

    [Fact]
    public void ConcurrentReserveAndRelease_AcrossTasks_BalancesToZeroBaseline() // concurrent reserve/release
    {
        var manager = new UnifiedMemoryManager(64 * 1024);
        int threads = Math.Max(4, Environment.ProcessorCount);
        const int perThread = 5000;

        Parallel.For(0, threads, t =>
        {
            using TaskMemoryManager task = manager.RegisterTask(t);
            for (int i = 0; i < perThread; i++)
            {
                // Small reservations alternating pools; reserve-then-release keeps the live set tiny so most succeed,
                // but a transient over-budget failure is fine — the point is that nothing leaks.
                MemoryPoolKind kind = (i % 2 == 0) ? MemoryPoolKind.Execution : MemoryPoolKind.Storage;
                MemoryReservation? r = task.TryReserve(kind, 16);
                r?.Dispose();

                Assert.True(manager.ExecutionPool.FreeBytes >= 0);
                Assert.True(manager.StoragePool.FreeBytes >= 0);
            }
        });

        // Every reservation was disposed and every task scope exited: the whole manager returns to baseline.
        Assert.Equal(0, manager.ExecutionPool.UsedBytes);
        Assert.Equal(0, manager.StoragePool.UsedBytes);
        Assert.Equal(0, manager.TotalUsedBytes);
        Assert.Equal(64 * 1024, manager.TotalFreeBytes);
        Assert.Equal(manager.ExecutionRegionBytes, manager.ExecutionPool.PoolSizeBytes);
        Assert.Equal(manager.StorageRegionBytes, manager.StoragePool.PoolSizeBytes);
    }

    [Fact]
    public void ConcurrentReserveRelease_WithinOneTask_NeverGoesNegative_AndBalances()
    {
        var manager = new UnifiedMemoryManager(1024 * 1024);
        using TaskMemoryManager task = manager.RegisterTask(1);
        int threads = Math.Max(4, Environment.ProcessorCount);
        const int perThread = 4000;

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < perThread; i++)
            {
                MemoryReservation? r = task.TryReserve(MemoryPoolKind.Execution, 32);
                if (r is not null)
                {
                    Assert.Equal(32, r.ReservedBytes);
                    r.Dispose();
                    r.Dispose(); // racing double-dispose must stay a no-op
                }
            }
        });

        Assert.Equal(0, manager.ExecutionPool.UsedBytes);
        Assert.Equal(0, task.ExecutionUsedBytes);
    }

    [Fact]
    public void DoubleDispose_OfReservation_IsSafeNoOp_ReleasesExactlyOnce() // double-release safety
    {
        var manager = new UnifiedMemoryManager(1000);
        using TaskMemoryManager task = manager.RegisterTask(1);
        MemoryReservation r = task.Reserve(MemoryPoolKind.Execution, 100);
        Assert.Equal(100, manager.ExecutionPool.UsedBytes);

        r.Dispose();
        Assert.True(r.IsReleased);
        Assert.Equal(0, manager.ExecutionPool.UsedBytes);
        Assert.Equal(0, task.ExecutionUsedBytes);

        // Second and third dispose must not decrement again (never negative).
        r.Dispose();
        r.Dispose();
        Assert.Equal(0, manager.ExecutionPool.UsedBytes);
        Assert.Equal(0, task.ExecutionUsedBytes);

        // The pool is fully usable again after the safe double-release.
        using MemoryReservation again = task.Reserve(MemoryPoolKind.Execution, 100);
        Assert.Equal(100, manager.ExecutionPool.UsedBytes);
    }

    [Fact]
    public void ConcurrentDispose_OfSameReservation_ReleasesExactlyOnce()
    {
        var manager = new UnifiedMemoryManager(1000);
        using TaskMemoryManager task = manager.RegisterTask(1);

        for (int iter = 0; iter < 500; iter++)
        {
            MemoryReservation r = task.Reserve(MemoryPoolKind.Execution, 100);
            Parallel.For(0, 16, _ => r.Dispose());

            Assert.Equal(0, manager.ExecutionPool.UsedBytes); // 16 racers, released exactly once
            Assert.Equal(0, task.ExecutionUsedBytes);
        }
    }

    [Fact]
    public void DisposingTask_ReleasesEveryReservation_AndRejectsFurtherReservations() // release discipline
    {
        var manager = new UnifiedMemoryManager(1000, 0.5);
        var task = manager.RegisterTask(1);

        task.Reserve(MemoryPoolKind.Execution, 100);
        task.Reserve(MemoryPoolKind.Storage, 100);
        task.Reserve(MemoryPoolKind.Execution, 50);
        Assert.Equal(250, manager.TotalUsedBytes);

        task.Dispose();

        // Everything the task held is released and the boundary returns to baseline.
        Assert.Equal(0, manager.ExecutionPool.UsedBytes);
        Assert.Equal(0, manager.StoragePool.UsedBytes);
        Assert.Equal(500, manager.ExecutionPool.PoolSizeBytes);
        Assert.Equal(500, manager.StoragePool.PoolSizeBytes);
        Assert.True(task.IsDisposed);

        // A disposed task cannot reserve again.
        Assert.Throws<ObjectDisposedException>(() => task.TryReserve(MemoryPoolKind.Execution, 1));
        Assert.Throws<ObjectDisposedException>(() => task.Reserve(MemoryPoolKind.Execution, 1));

        task.Dispose(); // idempotent
    }

    [Fact]
    public void ConcurrentTasks_SpillingAndReleasing_KeepThePoolConsistent()
    {
        var manager = new UnifiedMemoryManager(256 * 1024);
        int threads = Math.Max(4, Environment.ProcessorCount);
        const int perThread = 2000;

        Parallel.For(0, threads, t =>
        {
            using TaskMemoryManager task = manager.RegisterTask(t);
            var spill = new CountingSpillable();
            for (int i = 0; i < perThread; i++)
            {
                // A spillable plus a pinned reservation, both released each iteration.
                MemoryReservation? spillable = task.TryReserve(MemoryPoolKind.Execution, 64, spill);
                MemoryReservation? pinned = task.TryReserve(MemoryPoolKind.Storage, 64);
                pinned?.Dispose();
                spillable?.Dispose();
            }
        });

        Assert.Equal(0, manager.ExecutionPool.UsedBytes);
        Assert.Equal(0, manager.StoragePool.UsedBytes);
        Assert.Equal(0, manager.TotalUsedBytes);
    }
}
