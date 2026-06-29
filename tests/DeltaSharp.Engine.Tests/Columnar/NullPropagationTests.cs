using DeltaSharp.Engine.Columnar;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers STORY-02.6.1 (#143) AC3: unary and binary null propagation under SQL three-valued logic.
/// The single-lane <see cref="bool"/><c>?</c> methods are pinned to the canonical 3VL truth tables,
/// the bulk span kernels are proven equal to that scalar reference lane-by-lane (the parity oracle a
/// later SIMD path inherits), and the propagate-on-any-null path stays allocation-free when every
/// input is valid.
/// </summary>
public class NullPropagationTests
{
    // ----------------------------------------------------------------------------------------
    // AC3 — scalar three-valued-logic truth tables (null = SQL UNKNOWN).
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, null, null)]
    [InlineData(null, true, null)]
    [InlineData(false, null, false)] // FALSE dominates: rescues the null
    [InlineData(null, false, false)] // FALSE dominates: rescues the null
    [InlineData(null, null, null)]
    public void KleeneAnd_ScalarTruthTable(bool? left, bool? right, bool? expected)
        => Assert.Equal(expected, NullPropagation.KleeneAnd(left, right));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, null, true)]  // TRUE dominates: rescues the null
    [InlineData(null, true, true)]  // TRUE dominates: rescues the null
    [InlineData(false, null, null)]
    [InlineData(null, false, null)]
    [InlineData(null, null, null)]
    public void KleeneOr_ScalarTruthTable(bool? left, bool? right, bool? expected)
        => Assert.Equal(expected, NullPropagation.KleeneOr(left, right));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(null, null)]
    public void KleeneNot_ScalarTruthTable(bool? value, bool? expected)
        => Assert.Equal(expected, NullPropagation.KleeneNot(value));

    // ----------------------------------------------------------------------------------------
    // AC3 — bulk Kleene kernels equal the scalar reference, including the rescue cases.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void KleeneAnd_Bulk_MatchesScalar_OverEveryStateCombination()
    {
        // All nine (left x right) 3VL combinations in one batch.
        bool?[] left = { true, true, true, false, false, false, null, null, null };
        bool?[] right = { true, false, null, true, false, null, true, false, null };

        AssertBinaryParity(left, right, NullPropagation.KleeneAnd, BulkKleeneAnd);
    }

    [Fact]
    public void KleeneOr_Bulk_MatchesScalar_OverEveryStateCombination()
    {
        bool?[] left = { true, true, true, false, false, false, null, null, null };
        bool?[] right = { true, false, null, true, false, null, true, false, null };

        AssertBinaryParity(left, right, NullPropagation.KleeneOr, BulkKleeneOr);
    }

    [Fact]
    public void KleeneNot_Bulk_MatchesScalar()
    {
        bool?[] input = { true, false, null };
        (bool[] values, byte[] bitmap) = Encode(input);
        var outValues = new bool[input.Length];
        var outValidity = new byte[Bitmap.ByteCount(input.Length)];

        int nulls = NullPropagation.KleeneNot(
            values, new Validity(bitmap, 0, input.Length), outValues, outValidity);

        Assert.Equal(1, nulls); // NOT null = null
        for (int i = 0; i < input.Length; i++)
        {
            Assert.Equal(NullPropagation.KleeneNot(input[i]), Decode(outValues, outValidity, i));
        }
    }

    [Theory]
    [InlineData(0xBADF00D, 64)]
    [InlineData(0x1234, 257)]   // a tail-crossing, non-byte-aligned length
    [InlineData(0x5EED, 1000)]
    public void KleeneAndOr_Bulk_RandomizedParityWithScalar(int seed, int length)
    {
        var rng = new Random(seed);
        bool?[] left = RandomLanes(rng, length);
        bool?[] right = RandomLanes(rng, length);

        AssertBinaryParity(left, right, NullPropagation.KleeneAnd, BulkKleeneAnd);
        AssertBinaryParity(left, right, NullPropagation.KleeneOr, BulkKleeneOr);
    }

    [Fact]
    public void Kleene_Bulk_WorksOverAbsentValidity_AllValidInputs()
    {
        // Inputs with no bitmap (all valid) collapse Kleene to ordinary boolean logic with no nulls.
        bool[] left = { true, true, false, false };
        bool[] right = { true, false, true, false };
        var outValues = new bool[4];
        var outValidity = new byte[Bitmap.ByteCount(4)];

        int nulls = NullPropagation.KleeneAnd(
            left, Validity.AllValid(4), right, Validity.AllValid(4), outValues, outValidity);

        bool[] expectedValues = { true, false, false, false };
        Assert.Equal(0, nulls);
        Assert.Equal(expectedValues, outValues);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(Bitmap.Get(outValidity, i)); // every output valid
        }
    }

    // ----------------------------------------------------------------------------------------
    // AC3 — propagate-on-any-null (arithmetic / comparison) and its contrast with Kleene.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void PropagateBinary_OutputIsNull_WhereverEitherOperandIsNull()
    {
        // left validity: [valid, valid, null, null]; right validity: [valid, null, valid, null].
        (byte[] leftBits, byte[] rightBits) = (new byte[1], new byte[1]);
        Bitmap.Set(leftBits, 0, true);
        Bitmap.Set(leftBits, 1, true);
        Bitmap.Set(rightBits, 0, true);
        Bitmap.Set(rightBits, 2, true);

        var output = new byte[Bitmap.ByteCount(4)];
        int nulls = NullPropagation.PropagateBinary(
            new Validity(leftBits, 0, 4), new Validity(rightBits, 0, 4), output);

        Assert.Equal(3, nulls); // only row 0 is valid in both
        Assert.True(Bitmap.Get(output, 0));
        Assert.False(Bitmap.Get(output, 1));
        Assert.False(Bitmap.Get(output, 2));
        Assert.False(Bitmap.Get(output, 3));
    }

    [Fact]
    public void PropagateUnary_CopiesValidity()
    {
        var bits = new byte[1];
        Bitmap.Set(bits, 0, true);
        Bitmap.Set(bits, 2, true);

        var output = new byte[Bitmap.ByteCount(3)];
        int nulls = NullPropagation.PropagateUnary(new Validity(bits, 0, 3), output);

        Assert.Equal(1, nulls); // row 1 null
        Assert.True(Bitmap.Get(output, 0));
        Assert.False(Bitmap.Get(output, 1));
        Assert.True(Bitmap.Get(output, 2));
    }

    [Fact]
    public void KleeneAnd_RescuesNull_WherePropagateOnAnyNull_WouldNullify()
    {
        // The defining 3VL difference: FALSE AND NULL is a *valid* FALSE under Kleene, but the same
        // operands are NULL under propagate-on-any-null (the rule arithmetic/comparison use).
        bool[] leftValues = { false };
        bool[] rightValues = { true }; // value irrelevant; the row is null
        var rightBits = new byte[1];   // right row 0 is null (bit cleared)
        var outValues = new bool[1];
        var outValidity = new byte[1];

        int kleeneNulls = NullPropagation.KleeneAnd(
            leftValues, Validity.AllValid(1),
            rightValues, new Validity(rightBits, 0, 1),
            outValues, outValidity);

        Assert.Equal(0, kleeneNulls);          // rescued: not null
        Assert.True(Bitmap.Get(outValidity, 0));
        Assert.False(outValues[0]);            // value is FALSE

        var propagateOut = new byte[1];
        int propagateNulls = NullPropagation.PropagateBinary(
            Validity.AllValid(1), new Validity(rightBits, 0, 1), propagateOut);

        Assert.Equal(1, propagateNulls);       // not rescued: null
        Assert.False(Bitmap.Get(propagateOut, 0));
    }

    [Fact]
    public void NeedsValidityBitmap_GatesTheNoAllocPath()
    {
        // AC1 x AC3: when both operands are all-valid the propagate result is all-valid, so the gate
        // says "no bitmap" and the caller allocates nothing. The gate call itself allocates nothing.
        bool Probe() => NullPropagation.NeedsValidityBitmap(Validity.AllValid(4096), Validity.AllValid(4096));

        Probe(); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool needs = Probe();
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.False(needs);
        Assert.True(after - before <= 64, $"gate allocated {after - before} bytes (expected ~0)");

        var bits = new byte[1];
        Assert.True(NullPropagation.NeedsValidityBitmap(new Validity(bits, 0, 8)));
        Assert.True(NullPropagation.NeedsValidityBitmap(Validity.AllValid(8), new Validity(bits, 0, 8)));
    }

    [Fact]
    public void Bulk_RejectsMismatchedShapes()
    {
        Assert.Throws<ArgumentException>(() =>
            NullPropagation.PropagateBinary(Validity.AllValid(4), Validity.AllValid(5), new byte[1]));

        Assert.Throws<ArgumentException>(() =>
            NullPropagation.PropagateUnary(Validity.AllValid(16), new byte[1])); // needs 2 bytes
    }

    // ----------------------------------------------------------------------------------------
    // Helpers: encode bool?[] lanes into (values, validity-bitmap) and back.
    // ----------------------------------------------------------------------------------------

    private delegate int BulkBinary(
        ReadOnlySpan<bool> leftValues,
        Validity leftValidity,
        ReadOnlySpan<bool> rightValues,
        Validity rightValidity,
        Span<bool> resultValues,
        Span<byte> resultValidity);

    private static int BulkKleeneAnd(
        ReadOnlySpan<bool> lv, Validity la, ReadOnlySpan<bool> rv, Validity ra, Span<bool> ov, Span<byte> obm)
        => NullPropagation.KleeneAnd(lv, la, rv, ra, ov, obm);

    private static int BulkKleeneOr(
        ReadOnlySpan<bool> lv, Validity la, ReadOnlySpan<bool> rv, Validity ra, Span<bool> ov, Span<byte> obm)
        => NullPropagation.KleeneOr(lv, la, rv, ra, ov, obm);

    private static void AssertBinaryParity(
        bool?[] left, bool?[] right, Func<bool?, bool?, bool?> scalar, BulkBinary bulk)
    {
        int n = left.Length;
        (bool[] leftValues, byte[] leftBitmap) = Encode(left);
        (bool[] rightValues, byte[] rightBitmap) = Encode(right);
        var outValues = new bool[n];
        var outValidity = new byte[Bitmap.ByteCount(n)];

        int nulls = bulk(
            leftValues, new Validity(leftBitmap, 0, n),
            rightValues, new Validity(rightBitmap, 0, n),
            outValues, outValidity);

        int expectedNulls = 0;
        for (int i = 0; i < n; i++)
        {
            bool? expected = scalar(left[i], right[i]);
            Assert.Equal(expected, Decode(outValues, outValidity, i));
            if (expected is null)
            {
                expectedNulls++;
            }
        }

        Assert.Equal(expectedNulls, nulls);
    }

    private static (bool[] Values, byte[] Bitmap) Encode(bool?[] lanes)
    {
        var values = new bool[lanes.Length];
        var bitmap = new byte[Bitmap.ByteCount(lanes.Length)];
        for (int i = 0; i < lanes.Length; i++)
        {
            if (lanes[i] is bool b)
            {
                values[i] = b;
                Bitmap.Set(bitmap, i, true); // valid
            }
            // null lane: value stays false (placeholder), validity bit stays cleared
        }

        return (values, bitmap);
    }

    private static bool? Decode(ReadOnlySpan<bool> values, ReadOnlySpan<byte> validity, int index)
        => Bitmap.Get(validity, index) ? values[index] : null;

    private static bool?[] RandomLanes(Random rng, int length)
    {
        var lanes = new bool?[length];
        for (int i = 0; i < length; i++)
        {
            lanes[i] = rng.Next(3) switch
            {
                0 => null,
                1 => false,
                _ => true,
            };
        }

        return lanes;
    }
}
