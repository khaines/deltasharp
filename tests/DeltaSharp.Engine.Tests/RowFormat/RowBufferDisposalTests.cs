using DeltaSharp.Engine.Memory;
using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.RowFormat;

/// <summary>
/// STORY-02.4.1 AC4: off-heap row buffers carry explicit, exactly-once disposal; ownership can be
/// transferred to shuffle/spill serialization; and double-free is a safe no-op (allocator live
/// counters return to zero, never negative).
/// </summary>
public class RowBufferDisposalTests
{
    private static readonly StructType Schema = new(
    [
        new StructField("id", LongType.Instance, nullable: false),
        new StructField("name", StringType.Instance),
    ]);

    private static RowData Sample => new(Schema, 1L, "off-heap-row-payload-large-enough-for-native");

    [Fact]
    public void Encode_UsesOffHeapBuffer_DisposeReturnsLiveCountersToZero()
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 0); // everything off-heap
        var encoder = new BinaryRowEncoder(allocator);

        BinaryRow row = encoder.Encode(Sample);
        Assert.Equal(1L, allocator.LiveNativeCount);
        Assert.Equal(1L, allocator.LiveNativeBytes / row.Length);

        row.Dispose();
        Assert.Equal(0L, allocator.LiveNativeCount);
        Assert.Equal(0L, allocator.LiveNativeBytes);
        Assert.Equal(1L, allocator.NativeFreeOperations);
    }

    [Fact]
    public void DoubleDispose_IsSafeNoOp()
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 0);
        BinaryRow row = new BinaryRowEncoder(allocator).Encode(Sample);

        row.Dispose();
        row.Dispose();
        row.Dispose();

        Assert.Equal(0L, allocator.LiveNativeCount);
        Assert.Equal(0L, allocator.LiveNativeBytes);
        Assert.Equal(1L, allocator.NativeFreeOperations); // freed exactly once, never negative
        Assert.Equal(0L, allocator.FinalizedWithoutDispose);
    }

    [Fact]
    public void TransferBuffer_MovesOwnership_RowDisposeNoOps_ThenOwnerDisposes()
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 0);
        BinaryRow row = new BinaryRowEncoder(allocator).Encode(Sample);

        OwnedBuffer transferred = row.TransferBuffer();
        Assert.Equal(1L, allocator.LiveNativeCount);

        row.Dispose(); // no-op: ownership transferred to the shuffle/spill owner
        Assert.Equal(1L, allocator.LiveNativeCount);

        transferred.Dispose(); // the new owner releases exactly once
        Assert.Equal(0L, allocator.LiveNativeCount);
        Assert.Equal(1L, allocator.NativeFreeOperations);
    }

    [Fact]
    public void TransferBuffer_DoubleFreeBetweenRowAndOwner_IsSafe()
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 0);
        BinaryRow row = new BinaryRowEncoder(allocator).Encode(Sample);
        OwnedBuffer transferred = row.TransferBuffer();

        transferred.Dispose();
        transferred.Dispose(); // double-free on the transferred buffer
        row.Dispose();         // and on the original row holder

        Assert.Equal(0L, allocator.LiveNativeCount);
        Assert.Equal(1L, allocator.NativeFreeOperations);
    }

    [Fact]
    public void AsSpan_AfterDispose_Throws()
    {
        var encoder = new BinaryRowEncoder(new NativeMemoryAllocator(scratchThreshold: 0));
        BinaryRow row = encoder.Encode(Sample);
        row.Dispose();
        Assert.Throws<ObjectDisposedException>(() => row.ToRowData());
    }

    [Fact]
    public void TransferBuffer_AfterDispose_Throws()
    {
        var encoder = new BinaryRowEncoder(new NativeMemoryAllocator(scratchThreshold: 0));
        BinaryRow row = encoder.Encode(Sample);
        row.Dispose();
        Assert.Throws<ObjectDisposedException>(() => row.TransferBuffer());
    }

    [Fact]
    public void ManyRows_AllDisposed_NoLeak()
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 0);
        var encoder = new BinaryRowEncoder(allocator);
        for (int i = 0; i < 100; i++)
        {
            using BinaryRow row = encoder.Encode(new RowData(Schema, (long)i, $"row-{i}-payload-padding-bytes"));
        }

        Assert.Equal(0L, allocator.LiveNativeCount);
        Assert.Equal(0L, allocator.LiveNativeBytes);
        Assert.Equal(100L, allocator.NativeFreeOperations);
    }
}
