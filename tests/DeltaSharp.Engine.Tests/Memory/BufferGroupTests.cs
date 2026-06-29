using DeltaSharp.Engine.Memory;
using Xunit;

namespace DeltaSharp.Engine.Tests.Memory;

/// <summary>
/// AC3: a <see cref="BufferGroup"/> reclaims every already-allocated buffer when construction throws partway
/// through, and transfers ownership cleanly on the success path via <see cref="BufferGroup.Detach"/>.
/// </summary>
public class BufferGroupTests
{
    [Fact]
    public void ConstructionThrowsPartway_ReclaimsEveryAllocatedBuffer()
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 16);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var group = new BufferGroup(allocator);
            group.Allocate(8192);   // native
            group.Allocate(8);      // scratch
            group.Allocate(4096);   // native

            // Live accounting reflects the three in-flight buffers before the failure.
            Assert.Equal(2L, allocator.LiveNativeCount);
            Assert.Equal(1L, allocator.LiveScratchCount);

            throw new InvalidOperationException("boom during construction");
        }));

        Assert.Equal("boom during construction", failure.Message);

        // The using disposed the group on the way out, reclaiming all three buffers.
        Assert.Equal(0L, allocator.LiveNativeBytes);
        Assert.Equal(0L, allocator.LiveNativeCount);
        Assert.Equal(0L, allocator.LiveScratchBytes);
        Assert.Equal(0L, allocator.LiveScratchCount);
    }

    [Fact]
    public void Detach_OnSuccess_KeepsBuffersAlive_ThenManualDisposeReturnsToZero()
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 16);

        OwnedBuffer[] detached;
        using (var group = new BufferGroup(allocator))
        {
            group.Allocate(8192);
            group.Allocate(4096);
            detached = group.Detach();
        }

        // Disposing the group after a successful Detach releases nothing — the buffers live on.
        Assert.Equal(2, detached.Length);
        Assert.Equal(2L, allocator.LiveNativeCount);
        Assert.Equal(8192L + 4096L, allocator.LiveNativeBytes);

        foreach (OwnedBuffer buffer in detached)
        {
            Assert.False(buffer.IsDisposed);
            buffer.Dispose();
        }

        Assert.Equal(0L, allocator.LiveNativeCount);
        Assert.Equal(0L, allocator.LiveNativeBytes);
    }

    [Fact]
    public void Detach_OnEmptyGroup_ReturnsEmptyArray()
    {
        var allocator = new NativeMemoryAllocator();
        using var group = new BufferGroup(allocator);

        Assert.Empty(group.Detach());
    }

    [Fact]
    public void Allocate_AfterDispose_Throws()
    {
        var allocator = new NativeMemoryAllocator();
        var group = new BufferGroup(allocator);
        group.Dispose();

        Assert.Throws<ObjectDisposedException>(() => group.Allocate(8));
        Assert.Throws<ObjectDisposedException>(() => group.Detach());
    }

    [Fact]
    public void NullAllocator_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BufferGroup(null!));
    }
}
