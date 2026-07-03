using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// STORY-04.4.2 (#168) <b>AC3</b>: literals and casts represent types using the ADR-0008 / ADR-0016
/// shared type-system model (the <see cref="DataType"/> hierarchy from DeltaSharp.Abstractions), in
/// the natural CLR storage shape, and equality is type-aware.
/// </summary>
public class ExpressionTypeModelTests
{
    [Fact]
    public void Literals_RecordSharedTypeAndStorageShape()
    {
        AssertLiteral(Literal.OfBoolean(true), BooleanType.Instance, true);
        AssertLiteral(Literal.OfByte(1), ByteType.Instance, (sbyte)1);
        AssertLiteral(Literal.OfShort(2), ShortType.Instance, (short)2);
        AssertLiteral(Literal.OfInt(3), IntegerType.Instance, 3);
        AssertLiteral(Literal.OfLong(4), LongType.Instance, 4L);
        AssertLiteral(Literal.OfFloat(1.5f), FloatType.Instance, 1.5f);
        AssertLiteral(Literal.OfDouble(2.5d), DoubleType.Instance, 2.5d);
        AssertLiteral(Literal.OfString("hi"), StringType.Instance, "hi");
        AssertLiteral(Literal.OfDate(19000), DateType.Instance, 19000);          // epoch-day (int)
        AssertLiteral(Literal.OfTimestamp(1_700_000_000_000_000L), TimestampType.Instance, 1_700_000_000_000_000L); // epoch-micros (long)
    }

    [Fact]
    public void DecimalLiteral_RecordsDecimalTypeAndUnscaledInt128()
    {
        var type = new DecimalType(10, 2);

        var literal = Literal.OfDecimal((Int128)12345, type);

        Assert.Same(type, literal.Type);
        Assert.Equal((Int128)12345, Assert.IsType<Int128>(literal.Value));
        Assert.False(literal.IsNull);
    }

    [Fact]
    public void BinaryLiteral_CopiesBytesDefensively()
    {
        byte[] source = [1, 2, 3];

        var literal = Literal.OfBinary(source);
        source[0] = 99;

        Assert.Equal(BinaryType.Instance, literal.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(literal.Value));
    }

    [Fact]
    public void NullLiteral_CarriesTypedNullOfSharedType()
    {
        var type = new DecimalType(20, 4);

        var literal = Literal.Null(type);

        Assert.Same(type, literal.Type);
        Assert.True(literal.IsNull);
        Assert.Null(literal.Value);
        Assert.True(literal.Nullable);
    }

    [Fact]
    public void Cast_TargetTypeIsSharedTypeAndIsTheResultType()
    {
        var target = new DecimalType(38, 10);
        var cast = new Cast(new UnresolvedAttribute("x"), target);

        Assert.Same(target, cast.TargetType);
        Assert.Same(target, cast.Type);
    }

    [Fact]
    public void Comparison_IsAlwaysBooleanTyped()
    {
        var comparison = new BinaryComparison(Literal.OfInt(1), Literal.OfInt(2), ComparisonOperator.LessThan);

        Assert.Equal(BooleanType.Instance, comparison.Type);
    }

    [Fact]
    public void Arithmetic_ResultTypeDerivesFromResolvedOperands()
    {
        // Once both operands are typed, the result type is a function of the children (Spark parity:
        // Add.dataType). int + long widens to bigint under ADR-0008 numeric promotion.
        var arithmetic = new BinaryArithmetic(Literal.OfInt(1), Literal.OfLong(2), ArithmeticOperator.Add);

        Assert.Equal(LongType.Instance, arithmetic.Type);
    }

    [Fact]
    public void Arithmetic_TypeIsNullWhileOperandUnresolved()
    {
        // With an unresolved operand the result type cannot be derived and stays null until analysis.
        var arithmetic = new BinaryArithmetic(
            new UnresolvedAttribute("x"), Literal.OfInt(2), ArithmeticOperator.Add);

        Assert.Null(arithmetic.Type);
    }

    [Fact]
    public void UnresolvedMarkers_HaveNoKnownType()
    {
        Assert.Null(new UnresolvedAttribute("x").Type);
        Assert.Null(new UnresolvedStar().Type);
        Assert.Null(new UnresolvedFunction("f", []).Type);
    }

    [Fact]
    public void Literal_StructuralEquality_IsTypeAware()
    {
        Assert.Equal(Literal.OfInt(5), Literal.OfInt(5));
        Assert.NotEqual<Expression>(Literal.OfInt(5), Literal.OfLong(5));     // same value, different type
        Assert.NotEqual<Expression>(Literal.OfInt(5), Literal.OfInt(6));
        Assert.Equal(Literal.OfBinary([1, 2]), Literal.OfBinary([1, 2]));     // value equality over bytes
        Assert.Equal(Literal.Null(IntegerType.Instance), Literal.Null(IntegerType.Instance));
        Assert.NotEqual<Expression>(Literal.Null(IntegerType.Instance), Literal.Null(LongType.Instance));
    }

    private static void AssertLiteral(Literal literal, DataType expectedType, object expectedValue)
    {
        Assert.Same(expectedType, literal.Type);
        Assert.False(literal.IsNull);
        Assert.Equal(expectedValue, literal.Value);
    }
}
