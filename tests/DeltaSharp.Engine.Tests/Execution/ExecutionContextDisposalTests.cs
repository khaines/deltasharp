using DeltaSharp.Engine.Execution;
using DeltaSharp.Engine.Execution.Spill;
using Xunit;
using ExecutionContext = DeltaSharp.Engine.Execution.ExecutionContext;

namespace DeltaSharp.Engine.Tests.Execution;

/// <summary>
/// STORY-04.6.4 (#176) / deferral #420: the <see cref="ExecutionContext"/> owns the run's spill store
/// and is <see cref="IDisposable"/>, so disposing the context releases the store's resources on every
/// path (normal, cancellation, failure). These tests pin that disposal contract — the Engine half of
/// the deterministic-resource-release requirement the executor lane drives.
/// </summary>
public sealed class ExecutionContextDisposalTests
{
    [Fact]
    public void Dispose_DisposesADisposableSpillStore()
    {
        var store = new DisposeTrackingSpillStore();
        var context = new ExecutionContext(BoundedExecutionMemory.Unbounded) { SpillStore = store };

        Assert.False(store.Disposed);
        context.Dispose();

        Assert.True(store.Disposed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var store = new DisposeTrackingSpillStore();
        var context = new ExecutionContext(BoundedExecutionMemory.Unbounded) { SpillStore = store };

        context.Dispose();
        context.Dispose();

        // Genuinely idempotent: the _disposed guard makes the second Dispose a no-op, so the store is
        // disposed exactly once (a pass-through Dispose would forward both calls — DisposeCalls == 2).
        Assert.Equal(1, store.DisposeCalls);
    }

    [Fact]
    public void Dispose_WithNonDisposableStore_DoesNotThrow()
    {
        // The default MemorySpillStore is not IDisposable; disposing a context that owns it is a no-op.
        var context = new ExecutionContext(BoundedExecutionMemory.Unbounded) { SpillStore = new MemorySpillStore() };

        context.Dispose();
    }

    [Fact]
    public void Dispose_DefaultContext_NeverSpilled_IsSafe()
    {
        // A context that never spilled owns a lazy TempFileSpillStore that created no disk; disposing it
        // is safe and allocates/deletes nothing.
        var context = new ExecutionContext(BoundedExecutionMemory.Unbounded);

        context.Dispose();
    }

    private sealed class DisposeTrackingSpillStore : ISpillStore, IDisposable
    {
        public int DisposeCalls { get; private set; }

        public bool Disposed => DisposeCalls > 0;

        public ISpillSegment CreateSegment(string label) => throw new NotSupportedException();

        public void Dispose() => DisposeCalls++;
    }
}
