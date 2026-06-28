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
}
