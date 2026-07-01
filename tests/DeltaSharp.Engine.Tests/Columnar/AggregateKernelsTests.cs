using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers STORY-03.3.1 (#149) AC1 (sum/min/max/count over numeric vectors with nulls, selections, tails, and the
/// no-null SIMD fast path, matching the scalar reference exactly or within documented FP tolerance) and AC4
/// (integral overflow follows the EPIC-02 ANSI/Legacy contract). Each parity assertion uses an independent
/// in-test oracle, and the forced-tier theories make the Vector256 body reachable and mutation-killable even on
/// an arm64 host where <see cref="KernelTier.Auto"/> folds it away.
/// </summary>
[Collection("KernelParity")]
public class AggregateKernelsTests
{
    public static TheoryData<int> Lengths => new(KernelTestSupport.Lengths);

    public static TheoryData<int> NonEmptyLengths => new(KernelTestSupport.Lengths.Where(static n => n > 0));

    // ===================================================================================================
    // COUNT
    // ===================================================================================================

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Count_MatchesOracle_AcrossNullDensities(int length)
    {
        foreach (double density in KernelTestSupport.NullDensities)
        {
            var rng = new Random(unchecked(0xC0 ^ (length * 31) ^ (int)(density * 1000)));
            bool[] valid = KernelTestSupport.RandomValidity(rng, length, density);
            ColumnVector vector = KernelTestSupport.Long(KernelTestSupport.RandomLongs(rng, length, -100, 100), valid);

            long expectedNonNull = valid.Count(static v => v);
            Assert.Equal(length, AggregateKernels.CountAll(vector));
            Assert.Equal(expectedNonNull, AggregateKernels.CountNonNull(vector));
        }
    }

    // ===================================================================================================
    // SUM — integral, with the no-null SIMD fast path and the null/selection scalar reference
    // ===================================================================================================

    [Theory]
    [MemberData(nameof(Lengths))]
    public void SumInt_MatchesOracle_NoNullAndNullAndSelection(int length)
    {
        foreach (double density in KernelTestSupport.NullDensities)
        {
            var rng = new Random(unchecked(0x5011 ^ (length * 31) ^ (int)(density * 1000)));
            int[] values = KernelTestSupport.RandomInts(rng, length, -1000, 1000);
            bool[] valid = density == 0.0 ? AllTrue(length) : KernelTestSupport.RandomValidity(rng, length, density);

            long? oracle = SumOracle(values, valid);
            ColumnVector vector = KernelTestSupport.Int(values, density == 0.0 ? null : valid);
            Assert.Equal(oracle, AggregateKernels.SumInt64(vector, AnsiMode.Ansi));
        }
    }

    [Fact]
    public void SumInt_SelectionAware_MatchesSelectedOracle()
    {
        int[] values = { 10, 20, 30, 40, 50, 60, 70, 80 };
        int[] indices = { 1, 3, 5, 7 }; // 20+40+60+80 = 200
        ColumnVector selected = KernelTestSupport.Selected(KernelTestSupport.Int(values), indices);

        Assert.IsType<SelectedColumnVector>(selected);
        Assert.Equal(200L, AggregateKernels.SumInt64(selected, AnsiMode.Ansi));
    }

    [Fact]
    public void Sum_EmptyAndAllNull_AreNull()
    {
        Assert.Null(AggregateKernels.SumInt64(KernelTestSupport.Int(Array.Empty<int>()), AnsiMode.Ansi));
        Assert.Null(AggregateKernels.SumInt64(KernelTestSupport.Int(new[] { 1, 2, 3 }, new[] { false, false, false }), AnsiMode.Ansi));
        Assert.Null(AggregateKernels.SumDouble(KernelTestSupport.Double(Array.Empty<double>())));
        Assert.Null(AggregateKernels.AverageDouble(KernelTestSupport.Int(new[] { 1, 2 }, new[] { false, false })));
    }

    // ===================================================================================================
    // SUM — ANSI overflow contract (AC4)
    // ===================================================================================================

    [Fact]
    public void SumLong_OverflowUnderAnsi_Throws()
    {
        ColumnVector vector = KernelTestSupport.Long(new[] { long.MaxValue, 1L });
        Assert.Throws<ArithmeticOverflowException>(() => AggregateKernels.SumInt64(vector, AnsiMode.Ansi));
    }

    [Fact]
    public void SumLong_OverflowUnderLegacy_IsNull()
    {
        ColumnVector vector = KernelTestSupport.Long(new[] { long.MaxValue, 1L });
        Assert.Null(AggregateKernels.SumInt64(vector, AnsiMode.Legacy));
    }

    [Fact]
    public void SumLong_NegativeOverflowUnderAnsi_Throws()
    {
        ColumnVector vector = KernelTestSupport.Long(new[] { long.MinValue, -1L });
        Assert.Throws<ArithmeticOverflowException>(() => AggregateKernels.SumInt64(vector, AnsiMode.Ansi));
    }

    [Fact]
    public void SumLong_BoundaryNoOverflow_IsExact()
    {
        // (MaxValue - 1) + 1 = MaxValue: the AddOverflows predicate must NOT false-positive at the boundary.
        ColumnVector vector = KernelTestSupport.Long(new[] { long.MaxValue - 1, 1L });
        Assert.Equal(long.MaxValue, AggregateKernels.SumInt64(vector, AnsiMode.Ansi));
    }

    // ===================================================================================================
    // SUM — floating (deterministic scalar reference) and decimal (exact, cross-scale, result-type fit)
    // ===================================================================================================

    [Theory]
    [MemberData(nameof(Lengths))]
    public void SumDouble_MatchesSequentialOracle(int length)
    {
        var rng = new Random(unchecked(0xD0B1 ^ (length * 31)));
        var values = new double[length];
        for (int i = 0; i < length; i++)
        {
            values[i] = (rng.NextDouble() - 0.5) * 1000;
        }

        double oracle = 0;
        foreach (double v in values)
        {
            oracle += v;
        }

        double? actual = AggregateKernels.SumDouble(KernelTestSupport.Double(values));
        if (length == 0)
        {
            Assert.Null(actual);
        }
        else
        {
            Assert.Equal(oracle, actual!.Value);
        }
    }

    [Fact]
    public void SumDecimal_CrossScale_IsExactAndFitsResultType()
    {
        // 1.25 (scale 2) summed five times = 6.25; result type decimal(18,2).
        ColumnVector vector = KernelTestSupport.Decimal(new[] { 125L, 125L, 125L, 125L, 125L }, scale: 2);
        var resultType = new DecimalType(18, 2);
        DecimalValue? sum = AggregateKernels.SumDecimal(vector, resultType, AnsiMode.Ansi);
        Assert.NotNull(sum);
        Assert.Equal(new DecimalValue(625, 2), sum!.Value);
    }

    [Fact]
    public void SumDecimal_OverflowResultType_FollowsAnsiContract()
    {
        // 9.9 + 9.9 = 19.8 cannot fit decimal(2,1) (max 9.9); ANSI throws, Legacy is null.
        ColumnVector vector = KernelTestSupport.Decimal(new[] { 99L, 99L }, scale: 1);
        var tight = new DecimalType(2, 1);
        Assert.Throws<ArithmeticOverflowException>(() => AggregateKernels.SumDecimal(vector, tight, AnsiMode.Ansi));
        Assert.Null(AggregateKernels.SumDecimal(vector, tight, AnsiMode.Legacy));
    }

    // ===================================================================================================
    // MIN / MAX — integral/temporal, floating (Spark NaN order), decimal cross-scale
    // ===================================================================================================

    [Theory]
    [MemberData(nameof(Lengths))]
    public void MinMaxInt_MatchesOracle_AcrossNullDensities(int length)
    {
        foreach (double density in KernelTestSupport.NullDensities)
        {
            var rng = new Random(unchecked(0x319A ^ (length * 31) ^ (int)(density * 1000)));
            int[] values = KernelTestSupport.RandomInts(rng, length, -10_000, 10_000);
            bool[] valid = density == 0.0 ? AllTrue(length) : KernelTestSupport.RandomValidity(rng, length, density);

            (long? oracleMin, long? oracleMax) = MinMaxOracle(values, valid);
            ColumnVector vector = KernelTestSupport.Int(values, density == 0.0 ? null : valid);
            Assert.Equal(oracleMin, AggregateKernels.MinInt64(vector));
            Assert.Equal(oracleMax, AggregateKernels.MaxInt64(vector));
        }
    }

    [Fact]
    public void MinMax_Timestamp_UsesInt64FastPath()
    {
        long[] micros = { 50, 10, 90, 30, 70, 20, 80, 40, 60 };
        ColumnVector vector = KernelTestSupport.Timestamp(micros);
        Assert.Equal(10L, AggregateKernels.MinInt64(vector));
        Assert.Equal(90L, AggregateKernels.MaxInt64(vector));
    }

    [Fact]
    public void MinMaxDouble_FollowsSparkNanOrder()
    {
        // NaN is greatest; MAX returns NaN if any NaN present, MIN ignores NaN unless all are NaN.
        double[] withNaN = { 3.0, double.NaN, -2.0, 5.0 };
        ColumnVector vector = KernelTestSupport.Double(withNaN);
        Assert.Equal(-2.0, AggregateKernels.MinDouble(vector)!.Value);
        Assert.True(double.IsNaN(AggregateKernels.MaxDouble(vector)!.Value));

        double[] allNaN = { double.NaN, double.NaN };
        Assert.True(double.IsNaN(AggregateKernels.MinDouble(KernelTestSupport.Double(allNaN))!.Value));
    }

    [Fact]
    public void MinMaxDouble_NegativeZeroTiesPositiveZero()
    {
        // −0.0 == +0.0 under Spark's order, so the FIRST-SEEN extreme wins (no flip on a ±0 tie). Assert
        // the exact BIT PATTERN, not numeric equality — Assert.Equal(0.0, …) cannot tell +0.0 from −0.0,
        // so a tie-comparison regression (strict `<` → `<=`, or `>` → `>=`) would survive it. Cover MIN
        // and MAX in both tie orderings.
        static long Bits(double d) => BitConverter.DoubleToInt64Bits(d);
        long posZero = Bits(0.0);   // 0x0000000000000000
        long negZero = Bits(-0.0);  // unchecked((long)0x8000000000000000)

        Assert.Equal(posZero, Bits(AggregateKernels.MinDouble(KernelTestSupport.Double(new[] { 0.0, -0.0, 1.0 }))!.Value));
        Assert.Equal(negZero, Bits(AggregateKernels.MinDouble(KernelTestSupport.Double(new[] { -0.0, 0.0, 1.0 }))!.Value));
        Assert.Equal(posZero, Bits(AggregateKernels.MaxDouble(KernelTestSupport.Double(new[] { 0.0, -0.0, -1.0 }))!.Value));
        Assert.Equal(negZero, Bits(AggregateKernels.MaxDouble(KernelTestSupport.Double(new[] { -0.0, 0.0, -1.0 }))!.Value));
    }

    [Fact]
    public void MinMaxDecimal_CrossScale_IsExact()
    {
        // 1.5 (scale 1) vs 1.25 (scale 2) vs 1.500 (scale 3): min 1.25, max 1.5.
        ColumnVector vector = KernelTestSupport.Decimal(new[] { 15L, 13L, 18L }, scale: 1);
        Assert.Equal(new DecimalValue(13, 1), AggregateKernels.MinDecimal(vector)!.Value);
        Assert.Equal(new DecimalValue(18, 1), AggregateKernels.MaxDecimal(vector)!.Value);
    }

    [Fact]
    public void MinMax_EmptyAndAllNull_AreNull()
    {
        Assert.Null(AggregateKernels.MinInt64(KernelTestSupport.Int(Array.Empty<int>())));
        Assert.Null(AggregateKernels.MaxInt64(KernelTestSupport.Int(new[] { 1 }, new[] { false })));
        Assert.Null(AggregateKernels.MinDouble(KernelTestSupport.Double(Array.Empty<double>())));
        Assert.Null(AggregateKernels.MaxDecimal(KernelTestSupport.Decimal(Array.Empty<long>(), 2)));
    }

    // ===================================================================================================
    // AVG
    // ===================================================================================================

    [Fact]
    public void Average_IsSumOverCount_SkippingNulls()
    {
        ColumnVector vector = KernelTestSupport.Int(new[] { 10, 0, 20, 0, 30 }, new[] { true, false, true, false, true });
        Assert.Equal(20.0, AggregateKernels.AverageDouble(vector)!.Value); // (10+20+30)/3
    }

    // ===================================================================================================
    // Bulk SIMD reductions — forced-tier parity (every tier identical to the oracle, on any host)
    // ===================================================================================================

    [Theory]
    [MemberData(nameof(Lengths))]
    public void SumInt32_ForcedTierParity_MatchesOracle(int length)
    {
        var rng = new Random(unchecked(0x5132 ^ (length * 31)));
        int[] values = KernelTestSupport.RandomInts(rng, length, -1000, 1000);
        long oracle = 0;
        foreach (int v in values)
        {
            oracle += v;
        }

        foreach (KernelTier tier in KernelTestSupport.ForcedTiers)
        {
            Assert.Equal(oracle, AggregateKernels.SumInt32(values, tier));
        }
    }

    [Theory]
    [MemberData(nameof(NonEmptyLengths))]
    public void MinMaxInt32And64_ForcedTierParity_MatchesOracle(int length)
    {
        var rng = new Random(unchecked(0x319B ^ (length * 31)));
        int[] i32 = KernelTestSupport.RandomInts(rng, length, int.MinValue, int.MaxValue);
        long[] i64 = KernelTestSupport.RandomLongs(rng, length, long.MinValue, long.MaxValue);

        int min32 = i32[0];
        int max32 = i32[0];
        foreach (int v in i32)
        {
            min32 = Math.Min(min32, v);
            max32 = Math.Max(max32, v);
        }

        long min64 = i64[0];
        long max64 = i64[0];
        foreach (long v in i64)
        {
            min64 = Math.Min(min64, v);
            max64 = Math.Max(max64, v);
        }

        foreach (KernelTier tier in KernelTestSupport.ForcedTiers)
        {
            Assert.Equal(min32, AggregateKernels.MinInt32(i32, tier));
            Assert.Equal(max32, AggregateKernels.MaxInt32(i32, tier));
            Assert.Equal(min64, AggregateKernels.MinInt64(i64, tier));
            Assert.Equal(max64, AggregateKernels.MaxInt64(i64, tier));
        }
    }

    // ===================================================================================================
    // Group-aware bulk update (the #148 consumption contract)
    // ===================================================================================================

    [Fact]
    public void GroupSum_ScattersByGroupId_SkippingNulls()
    {
        // groups: 0 -> {10,30}=40, 1 -> {20}=20 (row 3 null skipped), 2 -> none.
        ColumnVector values = KernelTestSupport.Long(new[] { 10L, 20L, 30L, 99L }, new[] { true, true, true, false });
        int[] groupIds = { 0, 1, 0, 1 };
        Span<long> sums = stackalloc long[3];
        Span<long> counts = stackalloc long[3];
        Span<bool> overflowed = stackalloc bool[3];

        AggregateKernels.GroupSumInt64(values, groupIds, sums, counts, overflowed, AnsiMode.Ansi);

        Assert.Equal(40L, sums[0]);
        Assert.Equal(20L, sums[1]);
        Assert.Equal(0L, sums[2]);
        Assert.Equal(2L, counts[0]);
        Assert.Equal(1L, counts[1]);
        Assert.Equal(0L, counts[2]);
    }

    [Fact]
    public void GroupSum_OverflowLegacy_PoisonsGroupOnly()
    {
        ColumnVector values = KernelTestSupport.Long(new[] { long.MaxValue, 1L, 5L });
        int[] groupIds = { 0, 0, 1 };
        Span<long> sums = stackalloc long[2];
        Span<long> counts = stackalloc long[2];
        Span<bool> overflowed = stackalloc bool[2];

        AggregateKernels.GroupSumInt64(values, groupIds, sums, counts, overflowed, AnsiMode.Legacy);

        Assert.True(overflowed[0]);   // group 0 poisoned -> operator finalizes NULL
        Assert.False(overflowed[1]);
        Assert.Equal(5L, sums[1]);    // group 1 unaffected
    }

    [Fact]
    public void GroupSum_OverflowAnsi_Throws()
    {
        ColumnVector values = KernelTestSupport.Long(new[] { long.MaxValue, 1L });
        int[] groupIds = { 0, 0 };
        Assert.Throws<ArithmeticOverflowException>(() =>
        {
            Span<long> sums = stackalloc long[1];
            Span<long> counts = stackalloc long[1];
            Span<bool> overflowed = stackalloc bool[1];
            AggregateKernels.GroupSumInt64(values, groupIds, sums, counts, overflowed, AnsiMode.Ansi);
        });
    }

    [Fact]
    public void GroupCount_CountsNonNullPerGroup()
    {
        ColumnVector values = KernelTestSupport.Long(new[] { 1L, 2L, 3L, 4L }, new[] { true, false, true, true });
        int[] groupIds = { 0, 0, 1, 1 };
        Span<long> counts = stackalloc long[2];

        AggregateKernels.GroupCountNonNull(values, groupIds, counts);

        Assert.Equal(1L, counts[0]); // row 1 null skipped
        Assert.Equal(2L, counts[1]);
    }

    [Fact]
    public void GroupSum_OutOfRangeGroupId_Throws()
    {
        ColumnVector values = KernelTestSupport.Long(new[] { 1L });
        int[] groupIds = { 5 };
        Assert.Throws<ArgumentException>(() =>
        {
            Span<long> sums = stackalloc long[2];
            Span<long> counts = stackalloc long[2];
            Span<bool> overflowed = stackalloc bool[2];
            AggregateKernels.GroupSumInt64(values, groupIds, sums, counts, overflowed, AnsiMode.Ansi);
        });
    }

    // ===================================================================================================
    // Zero allocation on the hot path
    // ===================================================================================================

    [Fact]
    public void Aggregates_AreZeroAllocation()
    {
        ColumnVector intVector = KernelTestSupport.Int(KernelTestSupport.RandomInts(new Random(7), 4096, -1000, 1000));
        ColumnVector longVector = KernelTestSupport.Long(KernelTestSupport.RandomLongs(new Random(11), 4096, -1000, 1000));

        // Warm up.
        _ = AggregateKernels.SumInt64(intVector, AnsiMode.Ansi);
        _ = AggregateKernels.MinInt64(longVector);
        _ = AggregateKernels.CountNonNull(intVector);

        long before = GC.GetAllocatedBytesForCurrentThread();
        long sink = 0;
        for (int i = 0; i < 1000; i++)
        {
            sink += AggregateKernels.SumInt64(intVector, AnsiMode.Ansi)!.Value;
            sink += AggregateKernels.MinInt64(longVector)!.Value;
            sink += AggregateKernels.MaxInt64(longVector)!.Value;
            sink += AggregateKernels.CountNonNull(intVector);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= 64, $"hot path allocated {allocated} bytes (expected ~0); sink={sink}");
    }

    // ===================================================================================================
    // Type guards
    // ===================================================================================================

    [Fact]
    public void Sum_RejectsNonIntegralColumn()
    {
        Assert.Throws<InvalidOperationException>(() => AggregateKernels.SumInt64(KernelTestSupport.Double(new[] { 1.0 }), AnsiMode.Ansi));
    }

    // --- oracles ----------------------------------------------------------------------------------------

    private static bool[] AllTrue(int length)
    {
        var v = new bool[length];
        Array.Fill(v, true);
        return v;
    }

    private static long? SumOracle(int[] values, bool[] valid)
    {
        long acc = 0;
        bool saw = false;
        for (int i = 0; i < values.Length; i++)
        {
            if (!valid[i])
            {
                continue;
            }

            acc += values[i];
            saw = true;
        }

        return saw ? acc : null;
    }

    private static (long? Min, long? Max) MinMaxOracle(int[] values, bool[] valid)
    {
        long min = 0;
        long max = 0;
        bool saw = false;
        for (int i = 0; i < values.Length; i++)
        {
            if (!valid[i])
            {
                continue;
            }

            if (!saw)
            {
                min = max = values[i];
                saw = true;
            }
            else
            {
                min = Math.Min(min, values[i]);
                max = Math.Max(max, values[i]);
            }
        }

        return saw ? (min, max) : (null, null);
    }
}
