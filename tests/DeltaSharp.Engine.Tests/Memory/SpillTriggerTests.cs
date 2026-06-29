using DeltaSharp.Engine.Memory;
using Xunit;

namespace DeltaSharp.Engine.Tests.Memory;

/// <summary>
/// STORY-02.3.2 AC2/AC5: when a reservation would push a pool over budget the manager invokes the requesting task's
/// spill callbacks <b>before</b> rejecting, a successful spill releases the corresponding reservation (shrinking it and
/// freeing the pool/task bytes), and an exhausted-but-still-insufficient spill fails deterministically rather than
/// throwing <see cref="OutOfMemoryException"/>.
/// </summary>
public class SpillTriggerTests
{
    [Fact]
    public void OverBudgetReservation_SpillsRequestingTask_ThenSucceeds() // AC2
    {
        var manager = new UnifiedMemoryManager(1000);
        using TaskMemoryManager task = manager.RegisterTask(1);

        var spill = new CountingSpillable();
        MemoryReservation spillable = task.Reserve(MemoryPoolKind.Execution, 600, spill); // borrows 100 from storage

        // Wants 600 more; after borrowing the rest of storage's free 400 there is still a 200 shortfall, which the
        // spillable reservation must cover before the allocation is granted.
        MemoryReservation pinned = task.Reserve(MemoryPoolKind.Execution, 600);

        Assert.Equal(1, spill.SpillCalls);                // the callback fired before any rejection
        Assert.Equal(200, spill.TotalSpilled);            // exactly the shortfall was spilled
        Assert.Equal(400, spillable.ReservedBytes);       // the spilled reservation shrank by 200
        Assert.Equal(600, pinned.ReservedBytes);
        Assert.Equal(1000, manager.ExecutionPool.UsedBytes);
        Assert.Equal(1000, task.ExecutionUsedBytes);
    }

    [Fact]
    public void SpillTrigger_FiresExactlyWhenAReservationCrossesTheBudget() // spill fires at threshold
    {
        // All-execution budget (storage region 0) so there is no borrowing to mask the threshold crossing.
        var manager = new UnifiedMemoryManager(200, storageRegionFraction: 0.0);
        using TaskMemoryManager task = manager.RegisterTask(1);

        var spill = new CountingSpillable();
        MemoryReservation full = task.Reserve(MemoryPoolKind.Execution, 200, spill); // fills the pool exactly
        Assert.Equal(0, spill.SpillCalls);                                            // at the threshold, not over it

        MemoryReservation more = task.Reserve(MemoryPoolKind.Execution, 50);          // crosses the budget -> spill

        Assert.Equal(1, spill.SpillCalls);
        Assert.Equal(50, spill.TotalSpilled);
        Assert.Equal(150, full.ReservedBytes);
        Assert.Equal(50, more.ReservedBytes);
        Assert.Equal(200, manager.ExecutionPool.UsedBytes);
    }

    [Fact]
    public void SuccessfulSpill_ReleasesTheCorrespondingReservation() // AC5
    {
        var manager = new UnifiedMemoryManager(500);
        using TaskMemoryManager task = manager.RegisterTask(3);

        var spill = new CountingSpillable();
        MemoryReservation cached = task.Reserve(MemoryPoolKind.Execution, 400, spill);
        Assert.Equal(400, manager.ExecutionPool.UsedBytes);
        Assert.Equal(400, cached.ReservedBytes);

        // Force a spill of 150 by reserving past what borrowing can cover.
        MemoryReservation scratch = task.Reserve(MemoryPoolKind.Execution, 250);

        // The manager released exactly the spilled bytes from the corresponding reservation and the pool/task totals.
        Assert.Equal(150, spill.TotalSpilled);
        Assert.Equal(250, cached.ReservedBytes);              // 400 - 150 spilled
        Assert.Equal(250, scratch.ReservedBytes);
        Assert.Equal(500, manager.ExecutionPool.UsedBytes);   // 250 (shrunk) + 250 (new)
        Assert.Equal(500, task.ExecutionUsedBytes);
    }

    [Fact]
    public void OverBudget_WithPartialSpill_InvokesSpillThenFailsDeterministically() // AC2 + AC3 (spill insufficient)
    {
        var manager = new UnifiedMemoryManager(200, storageRegionFraction: 0.0);
        using TaskMemoryManager task = manager.RegisterTask(8);

        // A consumer that can only free 30 of its 200 bytes (the rest is pinned).
        var partial = new CountingSpillable(held: 30);
        using MemoryReservation pinnedish = task.Reserve(MemoryPoolKind.Execution, 200, partial);

        MemoryBudgetExceededException ex = Assert.Throws<MemoryBudgetExceededException>(
            () => task.Reserve(MemoryPoolKind.Execution, 100));

        Assert.True(partial.SpillCalls >= 1, "the manager must invoke the spill callback before rejecting");
        Assert.Equal(30, partial.TotalSpilled);
        Assert.Equal(8, ex.TaskId);
        Assert.Equal(100, ex.RequestedBytes);
        Assert.Equal(MemoryPoolKind.Execution, ex.Pool);
    }

    [Fact]
    public void UnspillableConsumer_RejectsGracefully_NotOom() // spill-or-fail, never OOM
    {
        var manager = new UnifiedMemoryManager(200, storageRegionFraction: 0.0);
        using TaskMemoryManager task = manager.RegisterTask(1);

        var stuck = new CountingSpillable { Enabled = false }; // cannot spill right now
        using MemoryReservation full = task.Reserve(MemoryPoolKind.Execution, 200, stuck);

        MemoryReservation? more = task.TryReserve(MemoryPoolKind.Execution, 50);

        Assert.True(stuck.SpillCalls >= 1, "the manager still attempts the spill before giving up");
        Assert.Null(more);                                  // graceful failure, no exception, no OOM
        Assert.Equal(200, manager.ExecutionPool.UsedBytes); // unchanged
    }

    [Fact]
    public void StoragePool_HasTheSameSpillTrigger_AsExecution()
    {
        // All-storage budget (storage region == whole budget) so the storage pool drives the test.
        var manager = new UnifiedMemoryManager(200, storageRegionFraction: 1.0);
        using TaskMemoryManager task = manager.RegisterTask(1);

        var spill = new CountingSpillable();
        MemoryReservation cached = task.Reserve(MemoryPoolKind.Storage, 200, spill);
        MemoryReservation more = task.Reserve(MemoryPoolKind.Storage, 60);

        Assert.Equal(1, spill.SpillCalls);
        Assert.Equal(60, spill.TotalSpilled);
        Assert.Equal(140, cached.ReservedBytes);
        Assert.Equal(200, manager.StoragePool.UsedBytes);
    }

    [Fact]
    public void DelegateSpillable_AdaptsACallback()
    {
        long requested = -1;
        var manager = new UnifiedMemoryManager(200, storageRegionFraction: 0.0);
        using TaskMemoryManager task = manager.RegisterTask(1);

        var spill = new DelegateSpillable(bytes =>
        {
            requested = bytes;
            return bytes; // free whatever is asked
        });
        using MemoryReservation full = task.Reserve(MemoryPoolKind.Execution, 200, spill);
        using MemoryReservation more = task.Reserve(MemoryPoolKind.Execution, 40);

        Assert.Equal(40, requested);
        Assert.Equal(200, manager.ExecutionPool.UsedBytes);
    }

    [Fact]
    public void DelegateSpillable_NullCallback_Throws()
        => Assert.Throws<ArgumentNullException>(() => new DelegateSpillable(null!));
}
