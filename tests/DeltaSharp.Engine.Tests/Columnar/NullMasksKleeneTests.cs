using DeltaSharp.Engine.Columnar;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers STORY-02.6.2 (#144) AC1/AC2 for the vectorized Kleene kernels (<see cref="NullMasks"/>): over
/// randomized batches across null densities and non-byte-aligned tail lengths, the bit-packed
/// <c>AND</c>/<c>OR</c>/<c>NOT</c> produce a <b>byte-identical validity bitmap</b>, the <b>same null
/// count</b>, and the <b>same value lanes</b> as the scalar <see cref="NullPropagation"/> three-valued-logic
/// reference — the parity oracle the vectorized tier must match (ADR-0001).
/// </summary>
public class NullMasksKleeneTests
{
    private delegate int VectorizedBinary(
        ReadOnlySpan<byte> leftValues,
        ReadOnlySpan<byte> leftValidity,
        ReadOnlySpan<byte> rightValues,
        ReadOnlySpan<byte> rightValidity,
        Span<byte> resultValues,
        Span<byte> resultValidity,
        int length);

    private delegate int BulkScalarBinary(
        ReadOnlySpan<bool> leftValues,
        Validity leftValidity,
        ReadOnlySpan<bool> rightValues,
        Validity rightValidity,
        Span<bool> resultValues,
        Span<byte> resultValidity);

    public static readonly TheoryData<int> Lengths =
        new() { 0, 1, 7, 8, 9, 63, 64, 65, 127, 128, 255, 256, 257, 1000, 1024, 4096 };

    [Fact]
    public void KleeneAnd_MatchesScalar_OverEveryStateCombination()
    {
        bool?[] left = { true, true, true, false, false, false, null, null, null };
        bool?[] right = { true, false, null, true, false, null, true, false, null };

        AssertBinaryParity(left, right, NullMasks.KleeneAnd, NullPropagation.KleeneAnd, NullPropagation.KleeneAnd);
    }

    [Fact]
    public void KleeneOr_MatchesScalar_OverEveryStateCombination()
    {
        bool?[] left = { true, true, true, false, false, false, null, null, null };
        bool?[] right = { true, false, null, true, false, null, true, false, null };

        AssertBinaryParity(left, right, NullMasks.KleeneOr, NullPropagation.KleeneOr, NullPropagation.KleeneOr);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void KleeneAnd_RandomizedParity_AcrossNullDensitiesAndTails(int length)
    {
        foreach (double density in new[] { 0.0, 0.1, 0.5, 0.9, 1.0 })
        {
            var rng = new Random(unchecked(0x4117 ^ (length * 31) ^ (int)(density * 1000)));
            bool?[] left = NullMaskTestSupport.RandomLanes(rng, length, density);
            bool?[] right = NullMaskTestSupport.RandomLanes(rng, length, density);

            AssertBinaryParity(left, right, NullMasks.KleeneAnd, NullPropagation.KleeneAnd, NullPropagation.KleeneAnd);
        }
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void KleeneOr_RandomizedParity_AcrossNullDensitiesAndTails(int length)
    {
        foreach (double density in new[] { 0.0, 0.1, 0.5, 0.9, 1.0 })
        {
            var rng = new Random(unchecked(0x0211 ^ (length * 31) ^ (int)(density * 1000)));
            bool?[] left = NullMaskTestSupport.RandomLanes(rng, length, density);
            bool?[] right = NullMaskTestSupport.RandomLanes(rng, length, density);

            AssertBinaryParity(left, right, NullMasks.KleeneOr, NullPropagation.KleeneOr, NullPropagation.KleeneOr);
        }
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void KleeneNot_RandomizedParity_AcrossNullDensitiesAndTails(int length)
    {
        foreach (double density in new[] { 0.0, 0.1, 0.5, 0.9, 1.0 })
        {
            var rng = new Random(unchecked(0x70C ^ (length * 31) ^ (int)(density * 1000)));
            bool?[] input = NullMaskTestSupport.RandomLanes(rng, length, density);
            (byte[] values, byte[] validity) = NullMaskTestSupport.EncodePacked(input);

            int byteCount = Math.Max(1, Bitmap.ByteCount(length));
            var myValues = new byte[byteCount];
            var myValidity = new byte[byteCount];
            int myNulls = NullMasks.KleeneNot(values, validity, myValues, myValidity, length);

            (bool[] scalarValues, byte[] scalarValidity) = NullMaskTestSupport.EncodeScalar(input);
            var theirValues = new bool[length];
            var theirValidity = new byte[byteCount];
            int theirNulls = NullPropagation.KleeneNot(
                scalarValues, new Validity(scalarValidity, 0, length), theirValues, theirValidity);

            Assert.Equal(theirNulls, myNulls);
            AssertValidityBytesIdentical(theirValidity, myValidity, length);
            for (int i = 0; i < length; i++)
            {
                bool? oracle = NullPropagation.KleeneNot(input[i]);
                Assert.Equal(oracle, NullMaskTestSupport.DecodePacked(myValues, myValidity, i));
            }
        }
    }

    [Fact]
    public void KleeneAnd_IsAllocationFree_OnTheHotPath()
    {
        const int length = 4096;
        int byteCount = Bitmap.ByteCount(length);
        var rng = new Random(0xA11C);
        bool?[] lanes = NullMaskTestSupport.RandomLanes(rng, length, 0.3);
        (byte[] lv, byte[] lValid) = NullMaskTestSupport.EncodePacked(lanes);
        (byte[] rv, byte[] rValid) = NullMaskTestSupport.EncodePacked(NullMaskTestSupport.RandomLanes(rng, length, 0.3));
        var oValues = new byte[byteCount];
        var oValid = new byte[byteCount];

        NullMasks.KleeneAnd(lv, lValid, rv, rValid, oValues, oValid, length); // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            NullMasks.KleeneAnd(lv, lValid, rv, rValid, oValues, oValid, length);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= 64, $"hot path allocated {allocated} bytes (expected ~0)");
    }

    [Fact]
    public void Kleene_RejectsUndersizedBuffers()
    {
        var ok = new byte[Bitmap.ByteCount(16)];
        var tooSmall = new byte[1];

        Assert.Throws<ArgumentException>(() =>
            NullMasks.KleeneAnd(tooSmall, ok, ok, ok, ok, ok, 16));
        Assert.Throws<ArgumentException>(() =>
            NullMasks.KleeneOr(ok, ok, ok, ok, tooSmall, ok, 16));
        Assert.Throws<ArgumentException>(() =>
            NullMasks.KleeneNot(ok, ok, tooSmall, ok, 16));
    }

    private static void AssertBinaryParity(
        bool?[] left,
        bool?[] right,
        VectorizedBinary vectorized,
        BulkScalarBinary bulkScalar,
        Func<bool?, bool?, bool?> oracle)
    {
        int length = left.Length;
        int byteCount = Math.Max(1, Bitmap.ByteCount(length));

        (byte[] lv, byte[] lValid) = NullMaskTestSupport.EncodePacked(left);
        (byte[] rv, byte[] rValid) = NullMaskTestSupport.EncodePacked(right);
        var myValues = new byte[byteCount];
        var myValidity = new byte[byteCount];
        int myNulls = vectorized(lv, lValid, rv, rValid, myValues, myValidity, length);

        (bool[] sLv, byte[] sLValid) = NullMaskTestSupport.EncodeScalar(left);
        (bool[] sRv, byte[] sRValid) = NullMaskTestSupport.EncodeScalar(right);
        var theirValues = new bool[length];
        var theirValidity = new byte[byteCount];
        int theirNulls = bulkScalar(
            sLv, new Validity(sLValid, 0, length), sRv, new Validity(sRValid, 0, length), theirValues, theirValidity);

        // AC2: identical null count.
        Assert.Equal(theirNulls, myNulls);

        // AC2: byte-identical validity bitmap (canonical padding makes this a true memcmp).
        AssertValidityBytesIdentical(theirValidity, myValidity, length);

        // AC2: identical value lanes, both equal to the single-lane scalar oracle.
        int expectedNulls = 0;
        for (int i = 0; i < length; i++)
        {
            bool? expected = oracle(left[i], right[i]);
            Assert.Equal(expected, NullMaskTestSupport.DecodePacked(myValues, myValidity, i));
            Assert.Equal(expected, NullMaskTestSupport.DecodeScalar(theirValues, theirValidity, i));
            if (expected is null)
            {
                expectedNulls++;
            }
        }

        Assert.Equal(expectedNulls, myNulls);
    }

    private static void AssertValidityBytesIdentical(byte[] expected, byte[] actual, int length)
    {
        int byteCount = Bitmap.ByteCount(length);
        Assert.True(
            expected.AsSpan(0, byteCount).SequenceEqual(actual.AsSpan(0, byteCount)),
            $"validity bitmap mismatch (length {length}): expected [{Convert.ToHexString(expected.AsSpan(0, byteCount))}] "
                + $"but got [{Convert.ToHexString(actual.AsSpan(0, byteCount))}]");
    }
}
