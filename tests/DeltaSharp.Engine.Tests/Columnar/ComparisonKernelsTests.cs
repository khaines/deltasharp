using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers STORY-03.3.1 (#149) AC2: comparison kernels over primitive/decimal/date/timestamp columns produce a
/// boolean result bitmap plus a propagate-on-any-null validity bitmap that match an independent per-row oracle
/// byte-for-byte — including SQL <c>NULL</c> propagation, Spark's <c>NaN</c>/<c>-0</c> ordering, decimal cross-scale,
/// and date↔timestamp promotion. Forced-tier theories make the Vector256 path reachable and mutation-killable on
/// any host; the high-level entry points are checked on both the SIMD (no-null) and scalar (null/selection) shapes.
/// </summary>
[Collection("KernelParity")]
public class ComparisonKernelsTests
{
    public static TheoryData<int> Lengths => new(KernelTestSupport.Lengths);

    // ===================================================================================================
    // Vector vs vector — per-type parity across all six ops and all null densities
    // ===================================================================================================

    [Theory]
    [MemberData(nameof(Lengths))]
    public void CompareInt_Parity_AcrossOpsAndNullDensities(int length)
    {
        foreach (double density in KernelTestSupport.NullDensities)
        {
            var rng = new Random(unchecked(0x1A7 ^ (length * 31) ^ (int)(density * 1000)));
            int[] l = KernelTestSupport.RandomInts(rng, length, -40, 40); // tight range -> Equal/tie coverage
            int[] r = KernelTestSupport.RandomInts(rng, length, -40, 40);
            bool[] vl = KernelTestSupport.RandomValidity(rng, length, density);
            bool[] vr = KernelTestSupport.RandomValidity(rng, length, density);

            ColumnVector left = KernelTestSupport.Int(l, vl);
            ColumnVector right = KernelTestSupport.Int(r, vr);
            KernelTestSupport.AssertCompareParity(left, right, vl, vr, i => l[i].CompareTo(r[i]));
        }
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void CompareLong_Parity_AcrossOpsAndNullDensities(int length)
    {
        foreach (double density in KernelTestSupport.NullDensities)
        {
            var rng = new Random(unchecked(0x1B8 ^ (length * 31) ^ (int)(density * 1000)));
            long[] l = KernelTestSupport.RandomLongs(rng, length, -40, 40);
            long[] r = KernelTestSupport.RandomLongs(rng, length, -40, 40);
            bool[] vl = KernelTestSupport.RandomValidity(rng, length, density);
            bool[] vr = KernelTestSupport.RandomValidity(rng, length, density);

            ColumnVector left = KernelTestSupport.Long(l, vl);
            ColumnVector right = KernelTestSupport.Long(r, vr);
            KernelTestSupport.AssertCompareParity(left, right, vl, vr, i => l[i].CompareTo(r[i]));
        }
    }

    [Fact]
    public void CompareShort_UsesScalarInt64Path()
    {
        short[] l = { 1, 5, 5, 9 };
        short[] r = { 2, 5, 4, 8 };
        var allValid = new[] { true, true, true, true };
        KernelTestSupport.AssertCompareParity(
            KernelTestSupport.Short(l), KernelTestSupport.Short(r), allValid, allValid, i => l[i].CompareTo(r[i]));
    }

    [Fact]
    public void CompareBoolean_OrdersFalseBeforeTrue()
    {
        bool[] l = { false, true, false, true };
        bool[] r = { false, false, true, true };
        var allValid = new[] { true, true, true, true };
        KernelTestSupport.AssertCompareParity(
            KernelTestSupport.Boolean(l), KernelTestSupport.Boolean(r), allValid, allValid,
            i => (l[i] ? 1 : 0).CompareTo(r[i] ? 1 : 0));
    }

    [Fact]
    public void CompareDate_UsesInt32FastPath()
    {
        int[] l = { 10, 20, 30, 30 };
        int[] r = { 20, 20, 25, 30 };
        var allValid = new[] { true, true, true, true };
        KernelTestSupport.AssertCompareParity(
            KernelTestSupport.Date(l), KernelTestSupport.Date(r), allValid, allValid, i => l[i].CompareTo(r[i]));
    }

    [Fact]
    public void CompareTimestamp_UsesInt64FastPath()
    {
        long[] l = { 100, 200, 300, 300 };
        long[] r = { 200, 200, 250, 300 };
        var allValid = new[] { true, true, true, true };
        KernelTestSupport.AssertCompareParity(
            KernelTestSupport.Timestamp(l), KernelTestSupport.Timestamp(r), allValid, allValid, i => l[i].CompareTo(r[i]));
    }

    // ===================================================================================================
    // Floating — Spark NaN/−0 ordering
    // ===================================================================================================

    [Fact]
    public void CompareDouble_FollowsSparkNanAndNegativeZeroOrder()
    {
        double[] l = { 1.0, double.NaN, double.NaN, 0.0, double.PositiveInfinity, -0.0, 2.0 };
        double[] r = { 2.0, double.NaN, 1.0, -0.0, 1.0, 0.0, double.NaN };
        var allValid = new bool[l.Length];
        Array.Fill(allValid, true);

        KernelTestSupport.AssertCompareParity(
            KernelTestSupport.Double(l), KernelTestSupport.Double(r), allValid, allValid,
            i => KernelTestSupport.OracleCompareDouble(l[i], r[i]));
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void CompareFloat_Parity_WithNullsAndNaN(int length)
    {
        var rng = new Random(unchecked(0xF10A ^ (length * 31)));
        var l = new float[length];
        var r = new float[length];
        for (int i = 0; i < length; i++)
        {
            l[i] = RandomFloatWithNaN(rng);
            r[i] = RandomFloatWithNaN(rng);
        }

        bool[] vl = KernelTestSupport.RandomValidity(rng, length, 0.2);
        bool[] vr = KernelTestSupport.RandomValidity(rng, length, 0.2);
        KernelTestSupport.AssertCompareParity(
            KernelTestSupport.Float(l, vl), KernelTestSupport.Float(r, vr), vl, vr,
            i => KernelTestSupport.OracleCompareDouble(l[i], r[i]));
    }

    // ===================================================================================================
    // Decimal cross-scale and date↔timestamp promotion
    // ===================================================================================================

    [Fact]
    public void CompareDecimal_CrossScale_IsExact()
    {
        // left scale 1, right scale 3: compare at the wider scale.
        long[] lUnscaled = { 15, 12, 20, 18 };  // 1.5, 1.2, 2.0, 1.8
        long[] rUnscaled = { 1500, 1300, 1900, 1800 }; // 1.500, 1.300, 1.900, 1.800
        var allValid = new[] { true, true, true, true };

        KernelTestSupport.AssertCompareParity(
            KernelTestSupport.Decimal(lUnscaled, 1), KernelTestSupport.Decimal(rUnscaled, 3), allValid, allValid,
            i => OracleDecimalSign(lUnscaled[i], 1, rUnscaled[i], 3));
    }

    [Fact]
    public void CompareDateVersusTimestamp_PromotesDateToMicros()
    {
        int[] days = { 1, 2, 0 };
        long[] micros = { TemporalValues.MicrosPerDay, TemporalValues.MicrosPerDay, 1 };
        var allValid = new[] { true, true, true };

        // day 1 == 86_400_000_000 micros (==), day 2 > 1 day (>) , day 0 == 0 < 1 micros (<).
        KernelTestSupport.AssertCompareParity(
            KernelTestSupport.Date(days), KernelTestSupport.Timestamp(micros), allValid, allValid,
            i => (days[i] * TemporalValues.MicrosPerDay).CompareTo(micros[i]));
    }

    // ===================================================================================================
    // Bulk SIMD — forced-tier parity (every tier byte-identical to the oracle on any host)
    // ===================================================================================================

    [Theory]
    [MemberData(nameof(Lengths))]
    public void CompareInt32Bulk_ForcedTierParity(int length)
    {
        var rng = new Random(unchecked(0x32A ^ (length * 31)));
        int[] l = KernelTestSupport.RandomInts(rng, length, -30, 30);
        int[] r = KernelTestSupport.RandomInts(rng, length, -30, 30);
        AssertBulkParity32(l, r);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void CompareInt64Bulk_ForcedTierParity(int length)
    {
        var rng = new Random(unchecked(0x64A ^ (length * 31)));
        long[] l = KernelTestSupport.RandomLongs(rng, length, -30, 30);
        long[] r = KernelTestSupport.RandomLongs(rng, length, -30, 30);
        AssertBulkParity64(l, r);
    }

    // ===================================================================================================
    // Vector vs scalar literal (predicate pushdown)
    // ===================================================================================================

    [Fact]
    public void CompareIntColumnVersusLiteral_MatchesOracle()
    {
        int[] values = { -5, 0, 7, 7, 100, -100 };
        const long literal = 7;
        AssertScalarParityInt(values, literal);
    }

    [Fact]
    public void CompareLongColumnVersusLiteral_OutOfIntRange_UsesScalarFallback()
    {
        long[] values = { 1, 2, 3 };
        const long literal = 5_000_000_000L; // outside int range
        var allValid = new[] { true, true, true };

        foreach (ComparisonOp op in KernelTestSupport.AllOps)
        {
            int byteCount = Math.Max(1, Bitmap.ByteCount(values.Length));
            var actualValues = new byte[byteCount];
            var actualValidity = new byte[byteCount];
            int nulls = ComparisonKernels.Compare(op, KernelTestSupport.Long(values), literal, actualValues, actualValidity);

            var expected = new byte[byteCount];
            for (int i = 0; i < values.Length; i++)
            {
                if (KernelTestSupport.ApplyOp(op, values[i].CompareTo(literal)))
                {
                    Bitmap.Set(expected, i, true);
                }
            }

            Assert.Equal(0, nulls);
            KernelTestSupport.AssertBitmapEqual(expected, actualValues, values.Length, $"{op} long-vs-literal");
            _ = allValid;
        }
    }

    [Fact]
    public void CompareDoubleColumnVersusLiteral_UsesSparkOrder()
    {
        double[] values = { 1.0, double.NaN, 3.5, -2.0 };
        const double literal = 3.5;

        foreach (ComparisonOp op in KernelTestSupport.AllOps)
        {
            int byteCount = Math.Max(1, Bitmap.ByteCount(values.Length));
            var actualValues = new byte[byteCount];
            var actualValidity = new byte[byteCount];
            ComparisonKernels.Compare(op, KernelTestSupport.Double(values), literal, actualValues, actualValidity);

            var expected = new byte[byteCount];
            for (int i = 0; i < values.Length; i++)
            {
                if (KernelTestSupport.ApplyOp(op, KernelTestSupport.OracleCompareDouble(values[i], literal)))
                {
                    Bitmap.Set(expected, i, true);
                }
            }

            KernelTestSupport.AssertBitmapEqual(expected, actualValues, values.Length, $"{op} double-vs-literal");
        }
    }

    [Fact]
    public void CompareScalar_PropagatesColumnNulls()
    {
        ColumnVector column = KernelTestSupport.Int(new[] { 1, 2, 3, 4 }, new[] { true, false, true, false });
        var values = new byte[1];
        var validity = new byte[1];
        int nulls = ComparisonKernels.Compare(ComparisonOp.GreaterThan, column, 0L, values, validity);

        Assert.Equal(2, nulls);
        Assert.True(Bitmap.Get(validity, 0));
        Assert.False(Bitmap.Get(validity, 1)); // null row
        Assert.True(Bitmap.Get(values, 0));    // 1 > 0
        Assert.False(Bitmap.Get(values, 1));   // null -> value bit 0 (value ⊆ valid)
    }

    // ===================================================================================================
    // Selection-aware
    // ===================================================================================================

    [Fact]
    public void Compare_SelectionAware_MatchesSelectedOracle()
    {
        int[] lValues = { 0, 10, 0, 30, 0, 50, 0, 70 };
        int[] rValues = { 0, 20, 0, 25, 0, 50, 0, 65 };
        int[] indices = { 1, 3, 5, 7 };
        ColumnVector left = KernelTestSupport.Selected(KernelTestSupport.Int(lValues), indices);
        ColumnVector right = KernelTestSupport.Selected(KernelTestSupport.Int(rValues), indices);
        var allValid = new[] { true, true, true, true };

        Assert.IsType<SelectedColumnVector>(left);
        KernelTestSupport.AssertCompareParity(left, right, allValid, allValid,
            i => lValues[indices[i]].CompareTo(rValues[indices[i]]));
    }

    // ===================================================================================================
    // Zero allocation and guards
    // ===================================================================================================

    [Fact]
    public void Compare_IsZeroAllocation()
    {
        var rng = new Random(99);
        ColumnVector left = KernelTestSupport.Int(KernelTestSupport.RandomInts(rng, 4096, -1000, 1000));
        ColumnVector right = KernelTestSupport.Int(KernelTestSupport.RandomInts(rng, 4096, -1000, 1000));
        var values = new byte[Bitmap.ByteCount(4096)];
        var validity = new byte[Bitmap.ByteCount(4096)];

        ComparisonKernels.Compare(ComparisonOp.LessThan, left, right, values, validity); // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        long sink = 0;
        for (int i = 0; i < 1000; i++)
        {
            sink += ComparisonKernels.Compare(ComparisonOp.LessThan, left, right, values, validity);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= 64, $"hot path allocated {allocated} bytes (expected ~0); sink={sink}");
    }

    [Fact]
    public void Compare_RejectsStringOperands()
    {
        MutableColumnVector s = ColumnVectors.Create(StringType.Instance, 1);
        s.AppendBytes("a"u8);
        Assert.Throws<NotSupportedException>(() => ComparisonKernels.Compare(ComparisonOp.Equal, s, s, new byte[1], new byte[1]));
    }

    [Fact]
    public void Compare_RejectsMismatchedLengthAndUndersizedSpans()
    {
        ColumnVector a = KernelTestSupport.Int(new[] { 1, 2, 3 });
        ColumnVector b = KernelTestSupport.Int(new[] { 1, 2 });
        Assert.Throws<ArgumentException>(() => ComparisonKernels.Compare(ComparisonOp.Equal, a, b, new byte[1], new byte[1]));
        Assert.Throws<ArgumentException>(() => ComparisonKernels.Compare(ComparisonOp.Equal, a, a, new byte[0], new byte[1]));
    }

    // --- helpers ----------------------------------------------------------------------------------------

    private static float RandomFloatWithNaN(Random rng) => rng.Next(6) switch
    {
        0 => float.NaN,
        1 => 0.0f,
        2 => -0.0f,
        3 => float.PositiveInfinity,
        4 => float.NegativeInfinity,
        _ => (float)((rng.NextDouble() - 0.5) * 20),
    };

    private static int OracleDecimalSign(long leftUnscaled, int leftScale, long rightUnscaled, int rightScale)
    {
        int scale = Math.Max(leftScale, rightScale);
        Int128 l = (Int128)leftUnscaled * Pow10(scale - leftScale);
        Int128 r = (Int128)rightUnscaled * Pow10(scale - rightScale);
        return l.CompareTo(r);
    }

    private static Int128 Pow10(int exponent)
    {
        Int128 result = 1;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }

        return result;
    }

    private static void AssertBulkParity32(int[] l, int[] r)
    {
        int n = l.Length;
        int byteCount = Math.Max(1, Bitmap.ByteCount(n));
        foreach (ComparisonOp op in KernelTestSupport.AllOps)
        {
            var expected = new byte[byteCount];
            for (int i = 0; i < n; i++)
            {
                if (KernelTestSupport.ApplyOp(op, l[i].CompareTo(r[i])))
                {
                    Bitmap.Set(expected, i, true);
                }
            }

            foreach (KernelTier tier in KernelTestSupport.ForcedTiers)
            {
                var actual = new byte[byteCount];
                ComparisonKernels.CompareInt32(op, l, r, actual, tier);
                KernelTestSupport.AssertBitmapEqual(expected, actual, n, $"{op} tier {tier}");
                KernelTestSupport.AssertCanonicalPadding(actual, n, $"{op} tier {tier} padding");
            }
        }
    }

    private static void AssertBulkParity64(long[] l, long[] r)
    {
        int n = l.Length;
        int byteCount = Math.Max(1, Bitmap.ByteCount(n));
        foreach (ComparisonOp op in KernelTestSupport.AllOps)
        {
            var expected = new byte[byteCount];
            for (int i = 0; i < n; i++)
            {
                if (KernelTestSupport.ApplyOp(op, l[i].CompareTo(r[i])))
                {
                    Bitmap.Set(expected, i, true);
                }
            }

            foreach (KernelTier tier in KernelTestSupport.ForcedTiers)
            {
                var actual = new byte[byteCount];
                ComparisonKernels.CompareInt64(op, l, r, actual, tier);
                KernelTestSupport.AssertBitmapEqual(expected, actual, n, $"{op} tier {tier}");
                KernelTestSupport.AssertCanonicalPadding(actual, n, $"{op} tier {tier} padding");
            }
        }
    }

    private static void AssertScalarParityInt(int[] values, long literal)
    {
        int n = values.Length;
        int byteCount = Math.Max(1, Bitmap.ByteCount(n));
        foreach (ComparisonOp op in KernelTestSupport.AllOps)
        {
            var actualValues = new byte[byteCount];
            var actualValidity = new byte[byteCount];
            int nulls = ComparisonKernels.Compare(op, KernelTestSupport.Int(values), literal, actualValues, actualValidity);

            var expected = new byte[byteCount];
            for (int i = 0; i < n; i++)
            {
                if (KernelTestSupport.ApplyOp(op, ((long)values[i]).CompareTo(literal)))
                {
                    Bitmap.Set(expected, i, true);
                }
            }

            Assert.Equal(0, nulls);
            KernelTestSupport.AssertBitmapEqual(expected, actualValues, n, $"{op} int-vs-literal");
        }
    }
}
