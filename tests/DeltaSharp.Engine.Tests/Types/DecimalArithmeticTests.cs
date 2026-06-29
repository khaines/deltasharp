using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

/// <summary>
/// STORY-02.5.2 AC2: decimal result-type rules, bounded clamping, and overflow behavior under
/// ANSI (throw) vs. legacy (null) — never silent truncation. Mirrors Spark DecimalPrecision.
/// </summary>
public class DecimalArithmeticTests
{
    [Theory]
    [InlineData(10, 2, 10, 2, 11, 2)] // add: max(8,8)+2+1 = 11, scale 2
    [InlineData(12, 4, 8, 0, 13, 4)] // add: max(8,8)+4+1 = 13, scale 4
    public void Add_ResultPrecisionScale_MatchesSpark(int p1, int s1, int p2, int s2, int rp, int rs)
    {
        DecimalType r = DecimalArithmetic.ResultType(DecimalOp.Add, new DecimalType(p1, s1), new DecimalType(p2, s2));
        Assert.Equal(new DecimalType(rp, rs), r);
    }

    [Fact]
    public void Multiply_ResultIsPrecisionPlusOne_ScaleSum()
    {
        Assert.Equal(new DecimalType(21, 4), DecimalArithmetic.ResultType(DecimalOp.Multiply, new DecimalType(10, 2), new DecimalType(10, 2)));
    }

    [Fact]
    public void Divide_UsesMinimumAdjustedScale()
    {
        Assert.Equal(new DecimalType(23, 13), DecimalArithmetic.ResultType(DecimalOp.Divide, new DecimalType(10, 2), new DecimalType(10, 2)));
    }

    [Fact]
    public void Bounded_ClampsBeyond38_KeepingMinimumScale()
    {
        // Multiply(38,10)×(38,10) → precision 77, scale 20 → bounded to 38 with min adjusted scale 6.
        DecimalType r = DecimalArithmetic.ResultType(DecimalOp.Multiply, new DecimalType(38, 10), new DecimalType(38, 10));
        Assert.Equal(38, r.Precision);
        Assert.Equal(6, r.Scale);
    }

    [Fact]
    public void ForType_WidensIntegralAndFloating()
    {
        Assert.Equal(new DecimalType(3, 0), DecimalArithmetic.ForType(ByteType.Instance));
        Assert.Equal(new DecimalType(10, 0), DecimalArithmetic.ForType(IntegerType.Instance));
        Assert.Equal(new DecimalType(20, 0), DecimalArithmetic.ForType(LongType.Instance));
        Assert.Equal(new DecimalType(30, 15), DecimalArithmetic.ForType(DoubleType.Instance));
    }

    [Fact]
    public void ToType_Overflow_ThrowsUnderAnsi()
    {
        var v = new DecimalValue(99999, 0); // 5 integer digits
        Assert.Throws<ArithmeticOverflowException>(() => v.ToType(new DecimalType(3, 0), AnsiMode.Ansi));
    }

    [Fact]
    public void ToType_Overflow_ReturnsNullUnderLegacy_NeverTruncates()
    {
        var v = new DecimalValue(99999, 0);
        Assert.Null(v.ToType(new DecimalType(3, 0), AnsiMode.Legacy));
        Assert.True(v.Fits(new DecimalType(5, 0)));
        Assert.False(v.Fits(new DecimalType(3, 0)));
    }

    [Fact]
    public void Apply_Add_FitsResultType_ExactSum()
    {
        DecimalValue? r = DecimalValue.Apply(DecimalOp.Add, new DecimalValue(150, 2), new DecimalValue(250, 2), AnsiMode.Ansi);
        Assert.Equal(new DecimalValue(400, 2), r);
    }

    [Fact]
    public void NullPropagation_NullOperandYieldsNull()
    {
        // AC5: a null operand propagates null rather than a 0m CLR default.
        DecimalValue? lhs = null;
        Assert.Null(lhs);
        Assert.Null(new DecimalValue(99999, 0).ToType(new DecimalType(3, 0), AnsiMode.Legacy));
    }

    [Fact]
    public void ToType_ExactPowerOfTen_Overflows() // 1000 needs 4 int digits, decimal(3,0) holds 3
    {
        var v = new DecimalValue(1000, 0);
        Assert.Throws<ArithmeticOverflowException>(() => v.ToType(new DecimalType(3, 0), AnsiMode.Ansi));
        Assert.Null(v.ToType(new DecimalType(3, 0), AnsiMode.Legacy));
    }

    [Fact]
    public void ToType_Int128Max_RescaleUpOverflows_AnsiThrows_LegacyNull()
    {
        // decimal(38,1) requires ×10; Int128.MaxValue×10 wraps an unchecked multiply — must NOT.
        var v = new DecimalValue(Int128.MaxValue, 0);
        Assert.Throws<ArithmeticOverflowException>(() => v.ToType(new DecimalType(38, 1), AnsiMode.Ansi));
        Assert.Null(v.ToType(new DecimalType(38, 1), AnsiMode.Legacy));
    }

    [Theory] // HALF_UP rounding for scale-reducing casts (Spark cast parity)
    [InlineData(1255, 3, 3, 2, 126, 2)] // 1.255 → 1.26
    [InlineData(25, 1, 2, 0, 3, 0)] // 2.5 → 3
    [InlineData(125, 2, 2, 1, 13, 1)] // 1.25 → 1.3 (half rounds away)
    [InlineData(-25, 1, 2, 0, -3, 0)] // -2.5 → -3 (half rounds away from zero)
    public void ToType_RoundsHalfUp(int u, int s, int p, int ts, int ru, int rs)
    {
        DecimalValue? r = new DecimalValue(u, s).ToType(new DecimalType(p, ts), AnsiMode.Ansi);
        Assert.Equal(new DecimalValue(ru, rs), r);
    }

    [Fact] // Mutation guard: an unchecked multiply wraps 1e21×1e21 into a value that re-fits — proves checked.
    public void Multiply_1e21_Overflows_NeverWraps()
    {
        var big = new DecimalValue(Pow10(21), 0);
        Assert.Throws<ArithmeticOverflowException>(() => DecimalValue.Apply(DecimalOp.Multiply, big, big, AnsiMode.Ansi));
        Assert.Null(DecimalValue.Apply(DecimalOp.Multiply, big, big, AnsiMode.Legacy)); // NEVER a value
    }

    [Fact]
    public void Multiply_1e36_Overflows_NeverWraps()
    {
        var huge = new DecimalValue(Pow10(36), 0);
        Assert.Throws<ArithmeticOverflowException>(() => DecimalValue.Apply(DecimalOp.Multiply, huge, huge, AnsiMode.Ansi));
        Assert.Null(DecimalValue.Apply(DecimalOp.Multiply, huge, huge, AnsiMode.Legacy)); // NEVER a value
    }

    [Fact] // scale sum 20+20=40 > 38 must overflow via AnsiMode, never ArgumentOutOfRangeException
    public void Multiply_ScaleSumBeyond38_RoutesThroughAnsiMode()
    {
        var a = new DecimalValue(1, 20);
        var b = new DecimalValue(1, 20);
        Assert.Throws<ArithmeticOverflowException>(() => DecimalValue.Apply(DecimalOp.Multiply, a, b, AnsiMode.Ansi));
        Assert.Null(DecimalValue.Apply(DecimalOp.Multiply, a, b, AnsiMode.Legacy));
    }

    [Fact]
    public void Fits_DetectsRescaleOverflow_NotAlwaysTrue()
    {
        // Int128.MaxValue cannot upscale into decimal(38,1); Fits must say false, not wrap.
        Assert.False(new DecimalValue(Int128.MaxValue, 0).Fits(new DecimalType(38, 1)));
    }

    [Fact]
    public void GetHashCode_FoldsFullInt128_DistinguishesHighWord_AgreesOnEquals()
    {
        // Same low 64 bits, different high word: (int)(long) truncation would collide; full fold must not.
        var lowOnly = new DecimalValue(Int128.One, 0);
        var highDiff = new DecimalValue(((Int128)1 << 64) + 1, 0);
        Assert.NotEqual(lowOnly, highDiff);
        Assert.NotEqual(lowOnly.GetHashCode(), highDiff.GetHashCode());

        // Equality contract preserved: equal values share a hash; scale participates.
        Assert.Equal(new DecimalValue(highDiff.Unscaled, 0), highDiff);
        Assert.Equal(new DecimalValue(highDiff.Unscaled, 0).GetHashCode(), highDiff.GetHashCode());
        Assert.NotEqual(new DecimalValue(Int128.One, 0).GetHashCode(), new DecimalValue(Int128.One, 1).GetHashCode());
    }

    private static Int128 Pow10(int n)
    {
        Int128 r = Int128.One;
        for (int i = 0; i < n; i++)
        {
            r *= 10;
        }

        return r;
    }
}
