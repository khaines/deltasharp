using System.Buffers;
using System.Runtime.InteropServices;
using DeltaSharp.Engine.Memory;
using Xunit;

namespace DeltaSharp.Engine.Tests.Memory;

/// <summary>
/// Council-driven hardening tests: secure-by-default zeroing vs the uninitialized opt-out, the deterministic
/// scratch clear-on-return (PII defense), the OOM path via an injected failing allocator, SIMD over-read padding,
/// and a same-buffer multi-disposer race. These pin behaviors the original suite left mechanically unprotected.
/// </summary>
public class AllocatorHardeningTests
{
    private const int ScratchSize = 64;
    private static int NativeSize(NativeMemoryAllocator a) => a.ScratchThreshold + 1024;

    [Theory]
    [InlineData(64)]      // scratch
    [InlineData(8192)]    // native
    public void Allocate_IsZeroedByDefault(int byteCount)
    {
        var allocator = new NativeMemoryAllocator();
        using OwnedBuffer buffer = allocator.Allocate(byteCount);
        Assert.True(buffer.AsSpan().IndexOfAnyExcept((byte)0) < 0, "Allocate() must return zeroed memory.");
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(8192, true)]
    public void AllocateUninitialized_RoutesLikeAllocate_ButSkipsZeroing(int byteCount, bool expectNative)
    {
        var allocator = new NativeMemoryAllocator();
        using OwnedBuffer buffer = allocator.AllocateUninitialized(byteCount);
        Assert.Equal(expectNative, buffer.IsNative);
        Assert.Equal(byteCount, buffer.Length);
    }

    [Fact]
    public unsafe void Scratch_ClearsArrayOnReturn_NoBleedToNextRenter()
    {
        // Dedicated pool makes ArrayPool reuse deterministic: the same array is rented again at the same size.
        var pool = ArrayPool<byte>.Create();
        var allocator = new NativeMemoryAllocator(NativeMemoryAllocator.DefaultScratchThresholdBytes, NativeMemory.AlignedAlloc, pool);

        OwnedBuffer first = allocator.AllocateUninitialized(ScratchSize);
        first.AsSpan().Fill(0xAA);
        first.Dispose(); // returns with clearArray:true

        OwnedBuffer second = allocator.AllocateUninitialized(ScratchSize);
        // Even uninitialized, the array was cleared on return, so no 0xAA bleeds into the next renter.
        Assert.True(second.AsSpan().IndexOfAnyExcept((byte)0) < 0, "clearArray:true must wipe scratch on return.");
        second.Dispose();
    }

    [Fact]
    public unsafe void NativeAllocationFailure_ThrowsOom_AndMovesNoCounter()
    {
        var allocator = new NativeMemoryAllocator(NativeMemoryAllocator.DefaultScratchThresholdBytes, FailingAlloc, ArrayPool<byte>.Shared);
        Assert.Throws<OutOfMemoryException>(() => allocator.Allocate(NativeSize(allocator)));
        Assert.Equal(0, allocator.LiveNativeCount);
        Assert.Equal(0, allocator.LiveNativeBytes);
        Assert.Equal(0, allocator.NativeFreeOperations);
        Assert.Equal(0, allocator.FinalizedWithoutDispose);

        static unsafe void* FailingAlloc(nuint byteCount, nuint alignment) => null;
    }

    [Fact]
    public void NativeCapacity_IsPaddedToAlignmentMultiple_ForSimdOverRead()
    {
        var allocator = new NativeMemoryAllocator();
        foreach (int size in new[] { 4097, 5000, 8191, 100003 })
        {
            using var native = (AlignedNativeBuffer)allocator.Allocate(size);
            Assert.Equal(size, native.Length);
            Assert.Equal(0, native.Capacity % AlignedNativeBuffer.Alignment);
            Assert.True(native.Capacity >= size);
        }
    }

    [Fact]
    public void ConcurrentDisposeOnSameBuffer_FreesExactlyOnce()
    {
        var allocator = new NativeMemoryAllocator();
        for (int iter = 0; iter < 500; iter++)
        {
            OwnedBuffer buffer = allocator.Allocate(NativeSize(allocator));
            Parallel.For(0, 16, _ => buffer.Dispose());
            Assert.Equal(iter + 1, allocator.NativeFreeOperations); // one free per buffer despite 16 racers
        }
        Assert.Equal(0, allocator.LiveNativeCount);
        Assert.True(allocator.LiveNativeBytes >= 0);
    }

    [Fact]
    public void BufferGroup_DoubleDetach_Throws()
    {
        var allocator = new NativeMemoryAllocator();
        using var group = new BufferGroup(allocator);
        group.Allocate(ScratchSize);
        OwnedBuffer[] first = group.Detach();
        Assert.Throws<InvalidOperationException>(() => group.Detach());
        foreach (OwnedBuffer b in first)
        {
            b.Dispose();
        }
    }
}
