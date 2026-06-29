using DeltaSharp.Engine.Memory;
using Xunit;

namespace DeltaSharp.Engine.Tests.Memory;

/// <summary>
/// AC1: every native allocation is 64-byte aligned at its base address and exposes exactly the requested
/// usable byte count through <see cref="OwnedBuffer.AsSpan"/>.
/// </summary>
public class AlignedNativeBufferTests
{
    // A scratch threshold of 0 forces every non-empty request onto the aligned native path, so a single
    // table exercises alignment across many sizes (small, sub-alignment, on-boundary, and large).
    public static TheoryData<int> Sizes => new()
    {
        1, 2, 3, 7, 8, 31, 32, 63, 64, 65, 100, 127, 128, 255, 256, 1000, 4096, 4097, 65535, 65536, 1_000_003,
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void NativeBuffer_BaseAddressIs64ByteAligned_AndUsableLengthMatchesRequest(int byteCount)
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 0);
        var buffer = (AlignedNativeBuffer)allocator.Allocate(byteCount);
        try
        {
            Assert.True(buffer.IsNative);

            // BaseAddress is the exact pointer AsSpan() wraps; assert the contract's 64-byte alignment on it.
            Assert.True(
                buffer.BaseAddress % AlignedNativeBuffer.Alignment == 0,
                $"base address {buffer.BaseAddress:X} is not {AlignedNativeBuffer.Alignment}-byte aligned");

            Span<byte> span = buffer.AsSpan();
            Assert.Equal(byteCount, span.Length);

            // The full requested length must be usable: write the first and last byte and read them back
            // (for a 1-byte buffer those are the same element).
            span[0] = 0xAB;
            span[^1] = 0xCD;
            Span<byte> readback = buffer.AsSpan();
            Assert.Equal((byte)0xCD, readback[byteCount - 1]);
            if (byteCount > 1)
            {
                Assert.Equal((byte)0xAB, readback[0]);
            }
        }
        finally
        {
            buffer.Dispose();
        }

        Assert.Equal(0L, allocator.LiveNativeBytes);
        Assert.Equal(0L, allocator.LiveNativeCount);
    }

    [Fact]
    public void Alignment_Is64Bytes()
    {
        Assert.Equal(64, AlignedNativeBuffer.Alignment);
    }
}
