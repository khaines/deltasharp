using DeltaSharp.Engine.Columnar;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers STORY-02.6.2 (#144) AC1/AC2 for the vectorized Kleene kernels (<see cref="NullMasks"/>): over
/// randomized batches across null densities and non-byte-aligned tail lengths, the bit-packed
/// <c>AND</c>/<c>OR</c>/<c>NOT</c> produce a <b>byte-identical validity bitmap</b>, a <b>byte-identical
/// value bitmap</b>, the <b>same null count</b>, and the <b>same value lanes</b> as the scalar
/// <see cref="NullPropagation"/> three-valued-logic reference — the parity oracle the vectorized tier must
/// match (ADR-0001).
/// </summary>
/// <remarks>
/// Two council findings are pinned here. <b>Finding #1 (tier forcing):</b> every SIMD/scalar tier is driven
/// explicitly through <see cref="NullMaskTier"/> so the <see cref="Vector256{T}"/> body — which is
/// constant-folded away under <see cref="NullMaskTier.Auto"/> on this arm64/NEON host — is exercised and
/// mutation-killable on any architecture. <b>Finding #2 (value-bitmap oracle):</b> the parity check asserts
/// the output VALUE bitmap byte-for-byte (not just validity), and a dedicated set of cases seeds garbage
/// value bits into null/padding input lanes so the kernels' <c>&amp; validity</c> masking is proven.
/// </remarks>
public class NullMasksKleeneTests
{
    private delegate int VectorizedBinary(
        ReadOnlySpan<byte> leftValues,
        ReadOnlySpan<byte> leftValidity,
        ReadOnlySpan<byte> rightValues,
        ReadOnlySpan<byte> rightValidity,
        Span<byte> resultValues,
        Span<byte> resultValidity,
        int length,
        NullMaskTier tier);

    private delegate int BulkScalarBinary(
        ReadOnlySpan<bool> leftValues,
        Validity leftValidity,
        ReadOnlySpan<bool> rightValues,
        Validity rightValidity,
        Span<bool> resultValues,
        Span<byte> resultValidity);

    public static readonly TheoryData<int> Lengths =
        new() { 0, 1, 7, 8, 9, 63, 64, 65, 127, 128, 255, 256, 257, 1000, 1024, 4096 };

    /// <summary>
    /// Lengths whose byte count is >= 32, so a forced <see cref="NullMaskTier.Vector256"/> loop body
    /// (32-byte stride) executes at least once — including the sub-byte tail (257) and vector-width tail
    /// (1000 bits = 125 bytes) called out in the design doc.
    /// </summary>
    public static readonly TheoryData<int> WideLengths =
        new() { 256, 257, 320, 511, 512, 1000, 1024, 4096 };

    /// <summary>Every tier including <see cref="NullMaskTier.Auto"/> (the production dispatch).</summary>
    private static readonly NullMaskTier[] AllTiers =
    {
        NullMaskTier.Auto, NullMaskTier.Scalar, NullMaskTier.Word, NullMaskTier.Vector128, NullMaskTier.Vector256,
    };

    /// <summary>The four explicitly-forced tiers (every tier except <see cref="NullMaskTier.Auto"/>).</summary>
    private static readonly NullMaskTier[] ForcedTiers =
    {
        NullMaskTier.Scalar, NullMaskTier.Word, NullMaskTier.Vector128, NullMaskTier.Vector256,
    };

    private static readonly double[] Densities = { 0.0, 0.1, 0.5, 0.9, 1.0 };

    /// <summary>Densities that guarantee null lanes exist, so the garbage-value-bit injection is non-vacuous.</summary>
    private static readonly double[] NullBearingDensities = { 0.1, 0.5, 0.9, 1.0 };

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
        foreach (double density in Densities)
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
        foreach (double density in Densities)
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
        foreach (double density in Densities)
        {
            var rng = new Random(unchecked(0x70C ^ (length * 31) ^ (int)(density * 1000)));
            bool?[] input = NullMaskTestSupport.RandomLanes(rng, length, density);

            AssertNotParity(input);
        }
    }

    // ----------------------------------------------------------------------------------------
    // Finding #1: force every tier (incl. Vector256, which Auto folds away on arm64) and assert
    // byte-identical parity with the scalar NullPropagation oracle. WideLengths guarantee the
    // 32-byte Vector256 body runs. Mutating any tier's lane formula breaks the matching tier here.
    // ----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(WideLengths))]
    public void KleeneAnd_ForcedTierParity_MatchesOracle_OnAnyHost(int length)
    {
        foreach (NullMaskTier tier in ForcedTiers)
        {
            foreach (double density in Densities)
            {
                var rng = new Random(unchecked(0x7A41 ^ (length * 31) ^ ((int)tier * 7) ^ (int)(density * 1000)));
                bool?[] left = NullMaskTestSupport.RandomLanes(rng, length, density);
                bool?[] right = NullMaskTestSupport.RandomLanes(rng, length, density);

                AssertBinaryParity(left, right, NullMasks.KleeneAnd, NullPropagation.KleeneAnd, NullPropagation.KleeneAnd, tier);
            }
        }
    }

    [Theory]
    [MemberData(nameof(WideLengths))]
    public void KleeneOr_ForcedTierParity_MatchesOracle_OnAnyHost(int length)
    {
        foreach (NullMaskTier tier in ForcedTiers)
        {
            foreach (double density in Densities)
            {
                var rng = new Random(unchecked(0x7012 ^ (length * 31) ^ ((int)tier * 7) ^ (int)(density * 1000)));
                bool?[] left = NullMaskTestSupport.RandomLanes(rng, length, density);
                bool?[] right = NullMaskTestSupport.RandomLanes(rng, length, density);

                AssertBinaryParity(left, right, NullMasks.KleeneOr, NullPropagation.KleeneOr, NullPropagation.KleeneOr, tier);
            }
        }
    }

    [Theory]
    [MemberData(nameof(WideLengths))]
    public void KleeneNot_ForcedTierParity_MatchesOracle_OnAnyHost(int length)
    {
        foreach (NullMaskTier tier in ForcedTiers)
        {
            foreach (double density in Densities)
            {
                var rng = new Random(unchecked(0x7707 ^ (length * 31) ^ ((int)tier * 7) ^ (int)(density * 1000)));
                bool?[] input = NullMaskTestSupport.RandomLanes(rng, length, density);

                AssertNotParity(input, tier);
            }
        }
    }

    // ----------------------------------------------------------------------------------------
    // Finding #2: feed garbage value bits into null/padding input lanes (v=0,b=1) and assert the
    // output VALUE bitmap is still byte-identical to the oracle. This is what makes the
    // `& validity` masking observable: dropping it (e.g. AND value = bL & bR) leaks the garbage and
    // fails value-bitmap parity. Run across every tier so whichever lane formula is mutated is hit.
    // ----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(WideLengths))]
    public void KleeneAnd_GarbageNullValueBits_StillCanonical_AcrossTiers(int length)
    {
        foreach (NullMaskTier tier in AllTiers)
        {
            foreach (double density in NullBearingDensities)
            {
                var rng = new Random(unchecked(0x6A41 ^ (length * 31) ^ ((int)tier * 7) ^ (int)(density * 1000)));
                bool?[] left = NullMaskTestSupport.RandomLanes(rng, length, density);
                bool?[] right = NullMaskTestSupport.RandomLanes(rng, length, density);

                AssertBinaryParity(
                    left, right, NullMasks.KleeneAnd, NullPropagation.KleeneAnd, NullPropagation.KleeneAnd, tier, garbageNullValues: true);
            }
        }
    }

    [Theory]
    [MemberData(nameof(WideLengths))]
    public void KleeneOr_GarbageNullValueBits_StillCanonical_AcrossTiers(int length)
    {
        foreach (NullMaskTier tier in AllTiers)
        {
            foreach (double density in NullBearingDensities)
            {
                var rng = new Random(unchecked(0x6012 ^ (length * 31) ^ ((int)tier * 7) ^ (int)(density * 1000)));
                bool?[] left = NullMaskTestSupport.RandomLanes(rng, length, density);
                bool?[] right = NullMaskTestSupport.RandomLanes(rng, length, density);

                AssertBinaryParity(
                    left, right, NullMasks.KleeneOr, NullPropagation.KleeneOr, NullPropagation.KleeneOr, tier, garbageNullValues: true);
            }
        }
    }

    [Theory]
    [MemberData(nameof(WideLengths))]
    public void KleeneNot_GarbageNullValueBits_StillCanonical_AcrossTiers(int length)
    {
        foreach (NullMaskTier tier in AllTiers)
        {
            foreach (double density in NullBearingDensities)
            {
                var rng = new Random(unchecked(0x6707 ^ (length * 31) ^ ((int)tier * 7) ^ (int)(density * 1000)));
                bool?[] input = NullMaskTestSupport.RandomLanes(rng, length, density);

                AssertNotParity(input, tier, garbageNullValues: true);
            }
        }
    }

    /// <summary>
    /// A minimal, explicit witness for council finding #2: a single null lane carrying a garbage value bit
    /// (<c>v=0, b=1</c>) must decode to a canonical <c>0</c> value bit in the output. The forced-<c>Word</c>
    /// tier guarantees the <c>ulong</c> lane formula processes the whole buffer, so the
    /// <c>AND value = (vL&amp;bL)&amp;(vR&amp;bR)</c> → <c>bL&amp;bR</c> mutation (which drops <c>&amp; validity</c>)
    /// leaks the garbage and trips the value-bitmap assertion.
    /// </summary>
    [Fact]
    public void KleeneAnd_NullLaneWithGarbageValueBit_DecodesToCanonicalZero()
    {
        // 64 lanes (one full ulong word) so the forced Word tier covers every lane.
        var left = new bool?[64];
        var right = new bool?[64];
        for (int i = 0; i < 64; i++)
        {
            left[i] = null;       // every left lane null -> garbage value bit injected
            right[i] = true;      // valid TRUE on the right
        }

        // Sanity: KleeneAnd(null, true) == null for every lane, so the canonical output value bitmap is all 0.
        AssertBinaryParity(
            left, right, NullMasks.KleeneAnd, NullPropagation.KleeneAnd, NullPropagation.KleeneAnd,
            NullMaskTier.Word, garbageNullValues: true);
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
        Func<bool?, bool?, bool?> oracle,
        NullMaskTier tier = NullMaskTier.Auto,
        bool garbageNullValues = false)
    {
        int length = left.Length;
        int byteCount = Math.Max(1, Bitmap.ByteCount(length));

        (byte[] lv, byte[] lValid) = garbageNullValues
            ? NullMaskTestSupport.EncodePackedGarbageNulls(left)
            : NullMaskTestSupport.EncodePacked(left);
        (byte[] rv, byte[] rValid) = garbageNullValues
            ? NullMaskTestSupport.EncodePackedGarbageNulls(right)
            : NullMaskTestSupport.EncodePacked(right);
        var myValues = new byte[byteCount];
        var myValidity = new byte[byteCount];
        int myNulls = vectorized(lv, lValid, rv, rValid, myValues, myValidity, length, tier);

        (bool[] sLv, byte[] sLValid) = NullMaskTestSupport.EncodeScalar(left);
        (bool[] sRv, byte[] sRValid) = NullMaskTestSupport.EncodeScalar(right);
        var theirValues = new bool[length];
        var theirValidity = new byte[byteCount];
        int theirNulls = bulkScalar(
            sLv, new Validity(sLValid, 0, length), sRv, new Validity(sRValid, 0, length), theirValues, theirValidity);

        // Oracle-derived canonical expected packed value + validity bitmaps (padding lanes are 0).
        (byte[] expValues, byte[] expValidity, int expNulls) =
            ExpectedPacked(length, i => oracle(left[i], right[i]));

        // AC2: identical null count (vectorized == scalar bulk == lane-by-lane oracle).
        Assert.Equal(theirNulls, myNulls);
        Assert.Equal(expNulls, myNulls);

        // AC2: byte-identical validity bitmap (canonical padding makes this a true memcmp).
        string context = $"length {length}, tier {tier}, garbage {garbageNullValues}";
        AssertBytesIdentical(theirValidity, myValidity, length, "validity", context);
        AssertBytesIdentical(expValidity, myValidity, length, "validity(oracle)", context);

        // Council finding #2: byte-identical VALUE bitmap vs the oracle and the scalar bulk reference.
        AssertBytesIdentical(expValues, myValues, length, "value(oracle)", context);
        AssertBytesIdentical(
            NullMaskTestSupport.PackValues(theirValues, theirValidity, length), myValues, length, "value(scalar)", context);

        // AC2: identical value lanes, both equal to the single-lane scalar oracle.
        for (int i = 0; i < length; i++)
        {
            bool? expected = oracle(left[i], right[i]);
            Assert.Equal(expected, NullMaskTestSupport.DecodePacked(myValues, myValidity, i));
            Assert.Equal(expected, NullMaskTestSupport.DecodeScalar(theirValues, theirValidity, i));
        }
    }

    private static void AssertNotParity(bool?[] input, NullMaskTier tier = NullMaskTier.Auto, bool garbageNullValues = false)
    {
        int length = input.Length;
        int byteCount = Math.Max(1, Bitmap.ByteCount(length));

        (byte[] values, byte[] validity) = garbageNullValues
            ? NullMaskTestSupport.EncodePackedGarbageNulls(input)
            : NullMaskTestSupport.EncodePacked(input);
        var myValues = new byte[byteCount];
        var myValidity = new byte[byteCount];
        int myNulls = NullMasks.KleeneNot(values, validity, myValues, myValidity, length, tier);

        (bool[] scalarValues, byte[] scalarValidity) = NullMaskTestSupport.EncodeScalar(input);
        var theirValues = new bool[length];
        var theirValidity = new byte[byteCount];
        int theirNulls = NullPropagation.KleeneNot(
            scalarValues, new Validity(scalarValidity, 0, length), theirValues, theirValidity);

        (byte[] expValues, byte[] expValidity, int expNulls) =
            ExpectedPacked(length, i => NullPropagation.KleeneNot(input[i]));

        string context = $"length {length}, tier {tier}, garbage {garbageNullValues}";
        Assert.Equal(theirNulls, myNulls);
        Assert.Equal(expNulls, myNulls);
        AssertBytesIdentical(theirValidity, myValidity, length, "validity", context);
        AssertBytesIdentical(expValidity, myValidity, length, "validity(oracle)", context);
        AssertBytesIdentical(expValues, myValues, length, "value(oracle)", context);
        AssertBytesIdentical(
            NullMaskTestSupport.PackValues(theirValues, theirValidity, length), myValues, length, "value(scalar)", context);

        for (int i = 0; i < length; i++)
        {
            bool? expected = NullPropagation.KleeneNot(input[i]);
            Assert.Equal(expected, NullMaskTestSupport.DecodePacked(myValues, myValidity, i));
        }
    }

    private static (byte[] Values, byte[] Validity, int Nulls) ExpectedPacked(int length, Func<int, bool?> oracleAt)
    {
        int byteCount = Math.Max(1, Bitmap.ByteCount(length));
        var values = new byte[byteCount];
        var validity = new byte[byteCount];
        int nulls = 0;
        for (int i = 0; i < length; i++)
        {
            if (oracleAt(i) is bool b)
            {
                Bitmap.Set(validity, i, true);
                if (b)
                {
                    Bitmap.Set(values, i, true);
                }
            }
            else
            {
                nulls++;
            }
        }

        return (values, validity, nulls);
    }

    private static void AssertBytesIdentical(byte[] expected, byte[] actual, int length, string what, string context)
    {
        int byteCount = Bitmap.ByteCount(length);
        Assert.True(
            expected.AsSpan(0, byteCount).SequenceEqual(actual.AsSpan(0, byteCount)),
            $"{what} bitmap mismatch ({context}): expected [{Convert.ToHexString(expected.AsSpan(0, byteCount))}] "
                + $"but got [{Convert.ToHexString(actual.AsSpan(0, byteCount))}]");
    }
}
