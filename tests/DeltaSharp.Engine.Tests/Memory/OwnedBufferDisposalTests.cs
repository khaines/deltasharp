using System.Runtime.CompilerServices;
using DeltaSharp.Engine.Memory;
using Xunit;

namespace DeltaSharp.Engine.Tests.Memory;

/// <summary>
/// AC2: when ownership objects are disposed, memory is released exactly once and the allocator's live
/// counters return to zero — for the happy path, the double-dispose path, and the finalizer safety-net path.
/// </summary>
public class OwnedBufferDisposalTests
{
    [Fact]
    public void AllocateMany_DisposeAll_AllLiveCountersReturnToZero()
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 1024);
        var buffers = new List<OwnedBuffer>();
        for (int i = 0; i < 8; i++)
        {
            buffers.Add(allocator.Allocate(64));     // scratch (<= 1024)
            buffers.Add(allocator.Allocate(8192));   // native (> 1024)
        }

        Assert.Equal(8L, allocator.LiveScratchCount);
        Assert.Equal(8L * 64, allocator.LiveScratchBytes);
        Assert.Equal(8L, allocator.LiveNativeCount);
        Assert.Equal(8L * 8192, allocator.LiveNativeBytes);

        foreach (OwnedBuffer buffer in buffers)
        {
            buffer.Dispose();
        }

        Assert.Equal(0L, allocator.LiveScratchCount);
        Assert.Equal(0L, allocator.LiveScratchBytes);
        Assert.Equal(0L, allocator.LiveNativeCount);
        Assert.Equal(0L, allocator.LiveNativeBytes);
        Assert.Equal(0L, allocator.FinalizedWithoutDispose);
    }

    [Fact]
    public void DoubleDispose_IsSafeNoOp_ReleasesExactlyOnce()
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 0);
        OwnedBuffer buffer = allocator.Allocate(8192);
        Assert.Equal(1L, allocator.LiveNativeCount);
        Assert.False(buffer.IsDisposed);

        buffer.Dispose();
        Assert.True(buffer.IsDisposed);
        Assert.Equal(0L, allocator.LiveNativeCount);
        Assert.Equal(0L, allocator.LiveNativeBytes);

        // A second (and third) dispose must not decrement again — the counters stay at zero, never negative.
        buffer.Dispose();
        buffer.Dispose();
        Assert.Equal(0L, allocator.LiveNativeCount);
        Assert.Equal(0L, allocator.LiveNativeBytes);
        Assert.Equal(0L, allocator.FinalizedWithoutDispose);
    }

    [Fact]
    public void AsSpan_AfterDispose_ThrowsObjectDisposedException()
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 16);

        OwnedBuffer native = allocator.Allocate(8192);
        native.Dispose();
        Assert.Throws<ObjectDisposedException>(() => native.AsSpan());

        OwnedBuffer scratch = allocator.Allocate(8);
        scratch.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scratch.AsSpan());
    }

    [Fact]
    public void NativeBuffer_FinalizedWithoutDispose_FreesMemoryAndFlagsDiagnostic()
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 0);

        AllocateAndAbandon(allocator, 8192);

        // The abandoned buffer holds no live reference; force its finalizer (the safety net) to run.
        for (int attempt = 0; attempt < 10 && allocator.FinalizedWithoutDispose == 0; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.True(
            allocator.FinalizedWithoutDispose >= 1,
            "the leaked native buffer's finalizer should have run and bumped FinalizedWithoutDispose");

        // Even though dispose was missed, the safety net freed the memory and balanced the live counters.
        Assert.Equal(0L, allocator.LiveNativeBytes);
        Assert.Equal(0L, allocator.LiveNativeCount);
    }

    // Allocates without disposing and keeps no reference, so the buffer becomes collectable when this returns.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateAndAbandon(NativeMemoryAllocator allocator, int byteCount)
    {
        OwnedBuffer buffer = allocator.Allocate(byteCount);
        Assert.True(buffer.IsNative);
    }
}
