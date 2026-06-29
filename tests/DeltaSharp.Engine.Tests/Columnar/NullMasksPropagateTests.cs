using DeltaSharp.Engine.Columnar;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers STORY-02.6.2 (#144) AC1/AC2/AC3 for the vectorized propagate-on-any-null kernels
/// (<see cref="NullMasks.PropagateBinary"/> / <see cref="NullMasks.PropagateUnary"/>): the SIMD AND of
/// two validity bitmaps yields a <b>byte-identical</b> bitmap and the <b>same null count</b> as the
/// scalar <see cref="NullPropagation"/> reference across null densities, non-byte-aligned tail lengths,
/// byte-aligned slices, the absent-bitmap (all-valid) fast paths, and bit-unaligned fallback — and the
/// hot path allocates nothing.
/// </summary>
public class NullMasksPropagateTests
{
    public static readonly TheoryData<int> Lengths =
        new() { 0, 1, 7, 8, 9, 63, 64, 65, 127, 128, 255, 256, 257, 1000, 1024, 4096 };

    [Theory]
    [MemberData(nameof(Lengths))]
    public void PropagateBinary_RandomizedParity_AcrossNullDensitiesAndTails(int length)
    {
        foreach (double density in new[] { 0.0, 0.1, 0.5, 0.9, 1.0 })
        {
            var rng = new Random(unchecked(0x9B11 ^ (length * 31) ^ (int)(density * 1000)));
            byte[] leftBits = ValidityBits(rng, length, density);
            byte[] rightBits = ValidityBits(rng, length, density);

            AssertBinaryParity(leftBits, 0, rightBits, 0, length);
        }
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void PropagateUnary_RandomizedParity_AcrossNullDensitiesAndTails(int length)
    {
        foreach (double density in new[] { 0.0, 0.1, 0.5, 0.9, 1.0 })
        {
            var rng = new Random(unchecked(0x5E11 ^ (length * 31) ^ (int)(density * 1000)));
            byte[] bits = ValidityBits(rng, length, density);

            Validity input = new(bits, 0, length);
            int byteCount = Math.Max(1, Bitmap.ByteCount(length));
            var mine = new byte[byteCount];
            var theirs = new byte[byteCount];

            int myNulls = NullMasks.PropagateUnary(input, mine);
            int theirNulls = NullPropagation.PropagateUnary(input, theirs);

            Assert.Equal(theirNulls, myNulls);
            AssertBytesIdentical(theirs, mine, length);
        }
    }

    [Theory]
    [InlineData(0, 0)]   // both byte-aligned at the start (pure SIMD AND)
    [InlineData(8, 8)]   // both byte-aligned at the same byte
    [InlineData(0, 8)]   // both byte-aligned, different bytes
    [InlineData(16, 24)] // both byte-aligned, different bytes
    [InlineData(3, 3)]   // bit-unaligned -> scalar fallback, still canonical
    [InlineData(5, 11)]  // bit-unaligned both -> scalar fallback
    [InlineData(8, 3)]   // mixed aligned/unaligned -> scalar fallback
    public void PropagateBinary_ParityAcrossOffsets(int leftOffset, int rightOffset)
    {
        const int length = 1000;
        var rng = new Random(unchecked(0x0FF5E7 ^ (leftOffset << 8) ^ rightOffset));
        byte[] leftBits = ValidityBits(rng, leftOffset + length, 0.4);
        byte[] rightBits = ValidityBits(rng, rightOffset + length, 0.4);

        AssertBinaryParity(leftBits, leftOffset, rightBits, rightOffset, length);
    }

    [Fact]
    public void PropagateBinary_AllValidFastPaths_MatchScalar()
    {
        const int length = 257;
        var rng = new Random(0xA11);
        byte[] bits = ValidityBits(rng, length, 0.3);

        // Both absent (all-valid): result all-valid, zero nulls.
        AssertBinaryParity(null, 0, null, 0, length);

        // One absent + one present (byte-aligned): result equals the present operand.
        AssertBinaryParity(bits, 0, null, 0, length);
        AssertBinaryParity(null, 0, bits, 0, length);
    }

    [Fact]
    public void PropagateUnary_AbsentBitmap_IsAllValid()
    {
        const int length = 100;
        var output = new byte[Bitmap.ByteCount(length)];

        int nulls = NullMasks.PropagateUnary(Validity.AllValid(length), output);

        Assert.Equal(0, nulls);
        Assert.Equal(length, BitmapOps.PopCount(output, length));
    }

    [Fact]
    public void PropagateBinary_AllNullAndAllValid_EdgeCounts()
    {
        const int length = 130;
        int byteCount = Bitmap.ByteCount(length);
        var allValidBits = new byte[byteCount];
        Array.Fill(allValidBits, (byte)0xFF);
        var allNullBits = new byte[byteCount]; // every bit cleared

        var output = new byte[byteCount];

        int allValidNulls = NullMasks.PropagateBinary(
            new Validity(allValidBits, 0, length), new Validity(allValidBits, 0, length), output);
        Assert.Equal(0, allValidNulls);

        int allNullNulls = NullMasks.PropagateBinary(
            new Validity(allNullBits, 0, length), new Validity(allValidBits, 0, length), output);
        Assert.Equal(length, allNullNulls);
    }

    [Fact]
    public void PropagateBinary_IsAllocationFree_OnTheHotPath()
    {
        const int length = 4096;
        int byteCount = Bitmap.ByteCount(length);
        var rng = new Random(0xB10C);
        byte[] leftBits = ValidityBits(rng, length, 0.25);
        byte[] rightBits = ValidityBits(rng, length, 0.25);
        var output = new byte[byteCount];

        long Run()
        {
            Validity left = new(leftBits, 0, length);
            Validity right = new(rightBits, 0, length);
            return NullMasks.PropagateBinary(left, right, output);
        }

        Run(); // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        long sink = 0;
        for (int i = 0; i < 1000; i++)
        {
            sink += Run();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= 64, $"hot path allocated {allocated} bytes (expected ~0); sink={sink}");
    }

    [Fact]
    public void PropagateBinary_RejectsMismatchedShapesAndUndersizedOutput()
    {
        Assert.Throws<ArgumentException>(() =>
            NullMasks.PropagateBinary(Validity.AllValid(4), Validity.AllValid(5), new byte[1]));
        Assert.Throws<ArgumentException>(() =>
            NullMasks.PropagateUnary(Validity.AllValid(16), new byte[1])); // needs 2 bytes
    }

    private static void AssertBinaryParity(byte[]? leftBits, int leftOffset, byte[]? rightBits, int rightOffset, int length)
    {
        Validity left = leftBits is null ? Validity.AllValid(length) : new Validity(leftBits, leftOffset, length);
        Validity right = rightBits is null ? Validity.AllValid(length) : new Validity(rightBits, rightOffset, length);

        int byteCount = Math.Max(1, Bitmap.ByteCount(length));
        var mine = new byte[byteCount];
        var theirs = new byte[byteCount];

        int myNulls = NullMasks.PropagateBinary(left, right, mine);
        int theirNulls = NullPropagation.PropagateBinary(left, right, theirs);

        Assert.Equal(theirNulls, myNulls);
        AssertBytesIdentical(theirs, mine, length);
    }

    private static void AssertBytesIdentical(byte[] expected, byte[] actual, int length)
    {
        int byteCount = Bitmap.ByteCount(length);
        Assert.True(
            expected.AsSpan(0, byteCount).SequenceEqual(actual.AsSpan(0, byteCount)),
            $"validity bitmap mismatch (length {length}): expected [{Convert.ToHexString(expected.AsSpan(0, byteCount))}] "
                + $"but got [{Convert.ToHexString(actual.AsSpan(0, byteCount))}]");
    }

    private static byte[] ValidityBits(Random rng, int bitCount, double nullDensity)
    {
        var bits = new byte[Math.Max(1, Bitmap.ByteCount(bitCount))];
        for (int i = 0; i < bitCount; i++)
        {
            if (rng.NextDouble() >= nullDensity)
            {
                Bitmap.Set(bits, i, true); // valid
            }
        }

        return bits;
    }
}
