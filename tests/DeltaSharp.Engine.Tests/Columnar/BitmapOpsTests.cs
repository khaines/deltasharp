using DeltaSharp.Engine.Columnar;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers STORY-02.6.2 (#144) AC1/AC2/AC3 for the low-level branchless bitmap word primitives
/// (<see cref="BitmapOps"/>): the SIMD <c>popcount</c> and bytewise <c>AND</c> are proven equal to an
/// independent per-bit oracle across null densities and non-byte-aligned tail lengths, the trailing
/// padding is canonicalized to zero, and the hot path allocates nothing.
/// </summary>
public class BitmapOpsTests
{
    // Lengths chosen to exercise empty, sub-byte, byte/vector boundaries, and the called-out
    // non-byte-aligned tails (257, 1000). 1024/4096 exercise the wide SIMD path end-to-end.
    public static readonly TheoryData<int> Lengths =
        new() { 0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 255, 256, 257, 1000, 1024, 4096 };

    [Theory]
    [MemberData(nameof(Lengths))]
    public void PopCount_EqualsNaiveSetBitCount_AcrossLengths(int length)
    {
        var rng = new Random(0x50C0 ^ length);
        byte[] bits = RandomBytes(rng, Bitmap.ByteCount(length) + 3); // over-allocate: padding must be ignored

        int expected = NaiveSetBits(bits, length);
        Assert.Equal(expected, BitmapOps.PopCount(bits, length));
        Assert.Equal(length - expected, BitmapOps.CountNulls(bits, length));
    }

    [Fact]
    public void PopCount_IgnoresBitsBeyondLength()
    {
        // Every bit set, but only [0, length) must be counted — the tail byte's high bits are padding.
        var bits = new byte[4];
        Array.Fill(bits, (byte)0xFF);

        Assert.Equal(0, BitmapOps.PopCount(bits, 0));
        Assert.Equal(1, BitmapOps.PopCount(bits, 1));
        Assert.Equal(7, BitmapOps.PopCount(bits, 7));
        Assert.Equal(8, BitmapOps.PopCount(bits, 8));
        Assert.Equal(9, BitmapOps.PopCount(bits, 9));
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void And_EqualsNaivePerBitAnd_WithCanonicalPadding(int length)
    {
        var rng = new Random(0xA4D ^ length);
        int byteCount = Bitmap.ByteCount(length);
        byte[] a = RandomBytes(rng, byteCount);
        byte[] b = RandomBytes(rng, byteCount);
        var dest = new byte[Math.Max(1, byteCount)];
        Array.Fill(dest, (byte)0xAA); // pre-dirty so a missing write or stale padding would be caught

        BitmapOps.And(a, b, dest, length);

        for (int i = 0; i < length; i++)
        {
            bool expected = Bitmap.Get(a, i) && Bitmap.Get(b, i);
            Assert.Equal(expected, Bitmap.Get(dest, i));
        }

        AssertCanonicalPadding(dest, length);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void FillValid_SetsEveryLaneValid_WithCanonicalPadding(int length)
    {
        var dest = new byte[Math.Max(1, Bitmap.ByteCount(length))];
        Array.Fill(dest, (byte)0x5A);

        BitmapOps.FillValid(dest, length);

        for (int i = 0; i < length; i++)
        {
            Assert.True(Bitmap.Get(dest, i));
        }

        Assert.Equal(length, BitmapOps.PopCount(dest, length));
        AssertCanonicalPadding(dest, length);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void CopyValidity_CopiesWindow_WithCanonicalPadding(int length)
    {
        var rng = new Random(0xC0F ^ length);
        byte[] src = RandomBytes(rng, Bitmap.ByteCount(length) + 2);
        var dest = new byte[Math.Max(1, Bitmap.ByteCount(length))];

        BitmapOps.CopyValidity(src, dest, length);

        for (int i = 0; i < length; i++)
        {
            Assert.Equal(Bitmap.Get(src, i), Bitmap.Get(dest, i));
        }

        AssertCanonicalPadding(dest, length);
    }

    [Fact]
    public void TailMask_IsFullByteOnAlignment_ElseLowBitMask()
    {
        Assert.Equal(0xFF, BitmapOps.TailMask(0));   // whole-byte boundary
        Assert.Equal(0xFF, BitmapOps.TailMask(8));
        Assert.Equal(0xFF, BitmapOps.TailMask(64));
        Assert.Equal(0xFF, BitmapOps.TailMask(1000)); // 1000 % 8 == 0 -> aligned
        Assert.Equal(0b0000_0001, BitmapOps.TailMask(1));
        Assert.Equal(0b0000_0001, BitmapOps.TailMask(257)); // 257 % 8 == 1
        Assert.Equal(0b0000_0111, BitmapOps.TailMask(259)); // 259 % 8 == 3
        Assert.Equal(0b0000_1111, BitmapOps.TailMask(1004)); // 1004 % 8 == 4
    }

    [Fact]
    public void PopCountAndAnd_AreAllocationFree_OnTheHotPath()
    {
        const int length = 4096;
        int byteCount = Bitmap.ByteCount(length);
        var a = new byte[byteCount];
        var b = new byte[byteCount];
        var dest = new byte[byteCount];
        var rng = new Random(0xFEED);
        rng.NextBytes(a);
        rng.NextBytes(b);

        // Warm up.
        BitmapOps.And(a, b, dest, length);
        _ = BitmapOps.PopCount(dest, length);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            BitmapOps.And(a, b, dest, length);
            _ = BitmapOps.PopCount(dest, length);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= 64, $"hot path allocated {allocated} bytes (expected ~0)");
    }

    private static int NaiveSetBits(ReadOnlySpan<byte> bits, int length)
    {
        int count = 0;
        for (int i = 0; i < length; i++)
        {
            if (Bitmap.Get(bits, i))
            {
                count++;
            }
        }

        return count;
    }

    private static void AssertCanonicalPadding(ReadOnlySpan<byte> dest, int length)
    {
        int byteCount = Bitmap.ByteCount(length);
        for (int i = length; i < byteCount * 8; i++)
        {
            Assert.False(Bitmap.Get(dest, i), $"padding bit {i} (length {length}) must be canonically zero");
        }
    }

    private static byte[] RandomBytes(Random rng, int count)
    {
        var bytes = new byte[Math.Max(1, count)];
        rng.NextBytes(bytes);
        return bytes;
    }
}
