using DeltaSharp.Engine.Memory;
using Xunit;

namespace DeltaSharp.Engine.Tests.Memory;

/// <summary>
/// AC4: requests at or below the scratch threshold use the GC-heap path and move only scratch counters;
/// larger requests use aligned native memory and move only native counters. Scratch never serves a large request.
/// </summary>
public class NativeMemoryAllocatorRoutingTests
{
    private const int Threshold = 1024;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(512)]
    [InlineData(Threshold)] // boundary is inclusive for scratch
    public void AtOrBelowThreshold_UsesGcHeapScratch_OnlyScratchCountersMove(int byteCount)
    {
        var allocator = new NativeMemoryAllocator(Threshold);

        OwnedBuffer buffer = allocator.Allocate(byteCount);

        Assert.IsType<GcHeapBuffer>(buffer);
        Assert.False(buffer.IsNative);
        Assert.Equal(byteCount, buffer.AsSpan().Length);

        // Only scratch counters moved.
        Assert.Equal(1L, allocator.LiveScratchCount);
        Assert.Equal((long)byteCount, allocator.LiveScratchBytes);
        Assert.Equal(0L, allocator.LiveNativeCount);
        Assert.Equal(0L, allocator.LiveNativeBytes);

        buffer.Dispose();
        Assert.Equal(0L, allocator.LiveScratchCount);
        Assert.Equal(0L, allocator.LiveScratchBytes);
    }

    [Theory]
    [InlineData(Threshold + 1)] // just over the boundary is native
    [InlineData(4096)]
    [InlineData(100_000)] // well into LOH territory — must be off-heap
    public void AboveThreshold_UsesAlignedNative_OnlyNativeCountersMove(int byteCount)
    {
        var allocator = new NativeMemoryAllocator(Threshold);

        OwnedBuffer buffer = allocator.Allocate(byteCount);

        Assert.IsType<AlignedNativeBuffer>(buffer);
        Assert.True(buffer.IsNative);
        Assert.Equal(byteCount, buffer.AsSpan().Length);

        // Only native counters moved.
        Assert.Equal(1L, allocator.LiveNativeCount);
        Assert.Equal((long)byteCount, allocator.LiveNativeBytes);
        Assert.Equal(0L, allocator.LiveScratchCount);
        Assert.Equal(0L, allocator.LiveScratchBytes);

        buffer.Dispose();
        Assert.Equal(0L, allocator.LiveNativeCount);
        Assert.Equal(0L, allocator.LiveNativeBytes);
    }

    [Fact]
    public void ScratchNeverSatisfiesLargeRequest_AcrossTheBoundary()
    {
        var allocator = new NativeMemoryAllocator(Threshold);

        OwnedBuffer atBoundary = allocator.Allocate(Threshold);
        OwnedBuffer justOver = allocator.Allocate(Threshold + 1);

        Assert.IsType<GcHeapBuffer>(atBoundary);    // <= threshold -> scratch
        Assert.IsType<AlignedNativeBuffer>(justOver); // > threshold -> native, never scratch

        atBoundary.Dispose();
        justOver.Dispose();
    }

    [Fact]
    public void DefaultThreshold_IsAFewKilobytes()
    {
        var allocator = new NativeMemoryAllocator();
        Assert.Equal(NativeMemoryAllocator.DefaultScratchThresholdBytes, allocator.ScratchThreshold);
        Assert.Equal(4096, NativeMemoryAllocator.DefaultScratchThresholdBytes);
    }

    [Fact]
    public void NegativeByteCount_Throws()
    {
        var allocator = new NativeMemoryAllocator();
        Assert.Throws<ArgumentOutOfRangeException>(() => allocator.Allocate(-1));
    }

    [Fact]
    public void NegativeThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeMemoryAllocator(-1));
    }
}
