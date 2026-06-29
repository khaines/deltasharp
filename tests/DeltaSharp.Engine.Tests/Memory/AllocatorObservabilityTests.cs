using System.Runtime.CompilerServices;
using DeltaSharp.Engine.Memory;
using Xunit;

namespace DeltaSharp.Engine.Tests.Memory;

/// <summary>
/// Proves the allocator's release path is <em>observable</em> — that disposing a buffer actually frees native
/// memory / returns the pooled array (not merely that a live counter balances), that the accounting holds under
/// concurrency, and that an abandoned native buffer is reclaimed and flagged by the finalizer safety net.
/// </summary>
public class AllocatorObservabilityTests
{
    private const int ScratchSize = 64; // <= default threshold -> GC-heap scratch
    private static int NativeSize(NativeMemoryAllocator a) => a.ScratchThreshold + 1024; // > threshold -> off-heap

    [Fact]
    public void DisposingNativeBuffers_ActuallyFreesNativeMemory()
    {
        var allocator = new NativeMemoryAllocator();
        Assert.Equal(0, allocator.NativeFreeOperations);

        var buffers = new OwnedBuffer[8];
        for (int i = 0; i < buffers.Length; i++)
        {
            buffers[i] = allocator.Allocate(NativeSize(allocator));
        }

        Assert.Equal(0, allocator.NativeFreeOperations); // no frees yet
        foreach (OwnedBuffer buffer in buffers)
        {
            buffer.Dispose();
        }

        // A no-op or skipped Release() would leave this at 0 even though the live counters reached 0.
        Assert.Equal(buffers.Length, allocator.NativeFreeOperations);
        Assert.Equal(0, allocator.LiveNativeCount);
        Assert.Equal(0, allocator.LiveNativeBytes);
    }

    [Fact]
    public void DoubleDispose_FreesNativeMemoryExactlyOnce()
    {
        var allocator = new NativeMemoryAllocator();
        OwnedBuffer buffer = allocator.Allocate(NativeSize(allocator));

        buffer.Dispose();
        buffer.Dispose();
        buffer.Dispose();

        Assert.Equal(1, allocator.NativeFreeOperations); // freed once, not three times
        Assert.Equal(0, allocator.LiveNativeCount);
    }

    [Fact]
    public void DisposingScratchBuffers_ActuallyReturnsToPool()
    {
        var allocator = new NativeMemoryAllocator();
        var buffers = new OwnedBuffer[5];
        for (int i = 0; i < buffers.Length; i++)
        {
            buffers[i] = allocator.Allocate(ScratchSize);
            Assert.False(buffers[i].IsNative);
        }

        foreach (OwnedBuffer buffer in buffers)
        {
            buffer.Dispose();
        }

        Assert.Equal(buffers.Length, allocator.ScratchReturnOperations);
        Assert.Equal(0, allocator.LiveScratchCount);
        Assert.Equal(0, allocator.LiveScratchBytes);
    }

    [Fact]
    public void ConcurrentAllocateAndDispose_BalancesAllCounters()
    {
        var allocator = new NativeMemoryAllocator();
        const int perThread = 2000;
        int threads = Math.Max(4, Environment.ProcessorCount);

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < perThread; i++)
            {
                // Alternate scratch / native so both pools see concurrent alloc+dispose.
                int size = (i % 2 == 0) ? ScratchSize : NativeSize(allocator);
                OwnedBuffer buffer = allocator.Allocate(size);
                buffer.AsSpan()[0] = 1;
                buffer.Dispose();
            }
        });

        Assert.Equal(0, allocator.LiveNativeBytes);
        Assert.Equal(0, allocator.LiveNativeCount);
        Assert.Equal(0, allocator.LiveScratchBytes);
        Assert.Equal(0, allocator.LiveScratchCount);

        long totalOps = (long)threads * perThread;
        Assert.Equal(totalOps / 2, allocator.NativeFreeOperations);
        Assert.Equal(totalOps / 2, allocator.ScratchReturnOperations);
        Assert.Equal(0, allocator.FinalizedWithoutDispose);
    }

    [Fact]
    public void AbandonedNativeBuffer_IsFreedAndFlaggedByFinalizer()
    {
        var allocator = new NativeMemoryAllocator();
        AllocateAndAbandonNative(allocator);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // The finalizer safety net frees the native memory, decrements the live counters, and flags the miss.
        Assert.Equal(1, allocator.FinalizedWithoutDispose);
        Assert.Equal(1, allocator.NativeFreeOperations);
        Assert.Equal(0, allocator.LiveNativeCount);
        Assert.Equal(0, allocator.LiveNativeBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateAndAbandonNative(NativeMemoryAllocator allocator)
    {
        OwnedBuffer buffer = allocator.Allocate(allocator.ScratchThreshold + 1024);
        Assert.True(buffer.IsNative);
        // Intentionally not disposed: the buffer becomes unreachable when this method returns, so the finalizer runs.
    }
}
