using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests;

/// <summary>
/// STORY-04.3.1 (#164) <b>AC2/AC4</b>: <see cref="Functions.Lit(object?)"/> maps supported .NET
/// scalar types (including null, decimal, date, and timestamp) to an ADR-0008 <see cref="DataType"/>
/// (AC2), and throws a deterministic public error naming the offending type for an unsupported .NET
/// type (AC4).
/// </summary>
public class FunctionsLitTests
{
    private static Literal LiteralOf(object? value)
        => Assert.IsType<Literal>(Functions.Lit(value).Expr);

    [Fact] // AC2
    public void Lit_Bool_MapsToBooleanType()
    {
        Literal literal = LiteralOf(true);
        Assert.IsType<BooleanType>(literal.Type);
        Assert.Equal(true, literal.Value);
        Assert.True(literal.Resolved);
    }

    [Fact] // AC2
    public void Lit_Sbyte_MapsToByteType()
    {
        Literal literal = LiteralOf((sbyte)-5);
        Assert.IsType<ByteType>(literal.Type);
        Assert.Equal((sbyte)-5, literal.Value);
    }

    [Fact] // AC2: byte widens to ShortType to avoid silent truncation of 128..255
    public void Lit_Byte_WidensToShortType()
    {
        Literal literal = LiteralOf((byte)200);
        Assert.IsType<ShortType>(literal.Type);
        Assert.Equal((short)200, literal.Value);
    }

    [Fact] // AC2
    public void Lit_Short_MapsToShortType()
    {
        Literal literal = LiteralOf((short)42);
        Assert.IsType<ShortType>(literal.Type);
        Assert.Equal((short)42, literal.Value);
    }

    [Fact] // AC2
    public void Lit_Int_MapsToIntegerType()
    {
        Literal literal = LiteralOf(7);
        Assert.IsType<IntegerType>(literal.Type);
        Assert.Equal(7, literal.Value);
    }

    [Fact] // AC2
    public void Lit_Long_MapsToLongType()
    {
        Literal literal = LiteralOf(9_000_000_000L);
        Assert.IsType<LongType>(literal.Type);
        Assert.Equal(9_000_000_000L, literal.Value);
    }

    [Fact] // AC2
    public void Lit_Float_MapsToFloatType()
    {
        Literal literal = LiteralOf(1.5f);
        Assert.IsType<FloatType>(literal.Type);
        Assert.Equal(1.5f, literal.Value);
    }

    [Fact] // AC2
    public void Lit_Double_MapsToDoubleType()
    {
        Literal literal = LiteralOf(2.5d);
        Assert.IsType<DoubleType>(literal.Type);
        Assert.Equal(2.5d, literal.Value);
    }

    [Fact] // AC2
    public void Lit_String_MapsToStringType()
    {
        Literal literal = LiteralOf("hello");
        Assert.IsType<StringType>(literal.Type);
        Assert.Equal("hello", literal.Value);
    }

    [Fact] // AC2
    public void Lit_Bytes_MapsToBinaryType()
    {
        byte[] bytes = { 1, 2, 3 };
        Literal literal = LiteralOf(bytes);
        Assert.IsType<BinaryType>(literal.Type);
        Assert.Equal(bytes, (byte[])literal.Value!);
    }

    [Fact] // AC2: decimal records precision/scale from the value
    public void Lit_Decimal_MapsToDecimalTypeWithValueScale()
    {
        Literal literal = LiteralOf(123.45m);
        var type = Assert.IsType<DecimalType>(literal.Type);
        Assert.Equal(5, type.Precision);
        Assert.Equal(2, type.Scale);
        Assert.Equal((Int128)12345, literal.Value);
    }

    [Fact] // AC2: negative decimal preserves sign in the unscaled value
    public void Lit_NegativeDecimal_PreservesSign()
    {
        Literal literal = LiteralOf(-0.001m);
        var type = Assert.IsType<DecimalType>(literal.Type);
        Assert.Equal(3, type.Scale);
        Assert.Equal(3, type.Precision); // precision >= scale
        Assert.Equal((Int128)(-1), literal.Value);
    }

    [Fact] // AC2
    public void Lit_DateOnly_MapsToDateTypeAsEpochDay()
    {
        Literal literal = LiteralOf(new DateOnly(1970, 1, 11));
        Assert.IsType<DateType>(literal.Type);
        Assert.Equal(10, literal.Value); // 10 days after the unix epoch
    }

    [Fact] // AC2: DateTime maps to DateType using its date component
    public void Lit_DateTime_MapsToDateType()
    {
        Literal literal = LiteralOf(new DateTime(1970, 1, 2, 13, 30, 0, DateTimeKind.Utc));
        Assert.IsType<DateType>(literal.Type);
        Assert.Equal(1, literal.Value);
    }

    [Fact] // AC2
    public void Lit_DateTimeOffset_MapsToTimestampTypeAsEpochMicros()
    {
        var value = new DateTimeOffset(1970, 1, 1, 0, 0, 1, TimeSpan.Zero);
        Literal literal = LiteralOf(value);
        Assert.IsType<TimestampType>(literal.Type);
        Assert.Equal(1_000_000L, literal.Value); // one second = 1e6 microseconds
    }

    [Fact] // AC2: null becomes a typed SQL NULL of NullType
    public void Lit_Null_MapsToNullTypeSqlNull()
    {
        Literal literal = LiteralOf(null);
        Assert.IsType<NullType>(literal.Type);
        Assert.True(literal.IsNull);
        Assert.True(literal.Nullable);
        Assert.Null(literal.Value);
    }

    [Fact] // AC4: unsupported type throws a deterministic error naming the type
    public void Lit_UnsupportedType_ThrowsNamingType()
    {
        var ex = Assert.Throws<ArgumentException>(() => Functions.Lit(Guid.Empty));
        Assert.Contains("System.Guid", ex.Message, StringComparison.Ordinal);
    }

    [Fact] // AC4: another unsupported type (char) is also rejected deterministically
    public void Lit_UnsupportedChar_ThrowsNamingType()
    {
        var ex = Assert.Throws<ArgumentException>(() => Functions.Lit('x'));
        Assert.Contains("System.Char", ex.Message, StringComparison.Ordinal);
    }
}
