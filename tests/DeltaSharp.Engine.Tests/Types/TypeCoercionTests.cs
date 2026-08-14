using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

/// <summary>
/// STORY-02.5.2 AC1/AC4/AC5: numeric width parity matrix, nested-type coercion error paths,
/// and SQL null-type promotion. Asserts against Spark's TypeCoercion answers for v1 types.
/// </summary>
public class TypeCoercionTests
{
    // AC1: Spark numericPrecedence common type — widen to the rightmost of the pair.
    public static IEnumerable<object[]> WideningMatrix() => new[]
    {
        new object[] { ByteType.Instance, ShortType.Instance, ShortType.Instance },
        new object[] { ByteType.Instance, IntegerType.Instance, IntegerType.Instance },
        new object[] { ShortType.Instance, IntegerType.Instance, IntegerType.Instance },
        new object[] { IntegerType.Instance, LongType.Instance, LongType.Instance },
        new object[] { IntegerType.Instance, FloatType.Instance, FloatType.Instance },
        new object[] { LongType.Instance, FloatType.Instance, FloatType.Instance },
        new object[] { LongType.Instance, DoubleType.Instance, DoubleType.Instance },
        new object[] { FloatType.Instance, DoubleType.Instance, DoubleType.Instance },
        new object[] { IntegerType.Instance, IntegerType.Instance, IntegerType.Instance },
    };

    [Theory]
    [MemberData(nameof(WideningMatrix))]
    public void FindWiderType_MatchesSparkNumericPrecedence(DataType a, DataType b, DataType expected)
    {
        Assert.Equal(expected, TypeCoercion.FindWiderTypeForTwo(a, b));
        Assert.Equal(expected, TypeCoercion.FindWiderTypeForTwo(b, a)); // commutative
        Assert.Equal(expected, TypeCoercion.FindTightestCommonType(a, b));
    }

    [Theory]
    [InlineData(10, 2, 12, 2)] // decimal(10,2) ⊕ int(10,0) → range 10, scale 2 → (12,2)
    [InlineData(38, 10, 38, 10)] // already wide enough to hold a long
    public void IntDecimal_WidensToDecimal(int p, int s, int rp, int rs)
    {
        DataType wider = TypeCoercion.FindWiderTypeForTwo(new DecimalType(p, s), IntegerType.Instance)!;
        Assert.Equal(new DecimalType(rp, rs), wider);
    }

    [Fact]
    public void DecimalWithFloatOrDouble_WidensToDouble()
    {
        Assert.Equal(DoubleType.Instance, TypeCoercion.FindWiderTypeForTwo(new DecimalType(10, 2), FloatType.Instance));
        Assert.Equal(DoubleType.Instance, TypeCoercion.FindWiderTypeForTwo(new DecimalType(10, 2), DoubleType.Instance));
    }

    [Fact]
    public void DecimalWithDecimal_WidensToHoldBoth()
    {
        // scale = max(2,4) = 4; range = max(10-2, 8-4) = 8 → decimal(12,4)
        Assert.Equal(new DecimalType(12, 4), TypeCoercion.FindWiderTypeForTwo(new DecimalType(10, 2), new DecimalType(8, 4)));
    }

    [Fact]
    public void NonNumeric_OnlyEqualTypesShareACommonType()
    {
        Assert.Equal(StringType.Instance, TypeCoercion.FindWiderTypeForTwo(StringType.Instance, StringType.Instance));
        Assert.Null(TypeCoercion.FindWiderTypeForTwo(StringType.Instance, IntegerType.Instance));
        Assert.True(TypeCoercion.CanCoerce(StringType.Instance, StringType.Instance));
        Assert.False(TypeCoercion.CanCoerce(IntegerType.Instance, StringType.Instance));
    }

    [Fact]
    public void IntDecimal_HasNoTightestCommonType()
    {
        // Widening an integer into a decimal is lossy precision-wise; tightest must refuse it.
        Assert.Null(TypeCoercion.FindTightestCommonType(IntegerType.Instance, new DecimalType(10, 2)));
        Assert.NotNull(TypeCoercion.FindWiderTypeForTwo(IntegerType.Instance, new DecimalType(10, 2)));
    }

    [Fact]
    public void IntDecimal_TightenWhenDecimalLosslesslyHoldsInt()
    {
        // decimal(12,2) keeps 10 integer digits — holds every int(10,0) value, so it is tight.
        Assert.Equal(new DecimalType(12, 2), TypeCoercion.FindTightestCommonType(IntegerType.Instance, new DecimalType(12, 2)));
        Assert.Equal(new DecimalType(12, 2), TypeCoercion.FindTightestCommonType(new DecimalType(12, 2), IntegerType.Instance));
        // byte fits decimal(5,0) (needs 3 int digits); short→decimal(5,0) is exact too.
        Assert.Equal(new DecimalType(5, 0), TypeCoercion.FindTightestCommonType(ByteType.Instance, new DecimalType(5, 0)));
        Assert.Equal(new DecimalType(5, 0), TypeCoercion.FindTightestCommonType(ShortType.Instance, new DecimalType(5, 0)));
    }

    [Fact]
    public void DateAndTimestamp_TightenToTimestamp()
    {
        Assert.Equal(TimestampType.Instance, TypeCoercion.FindTightestCommonType(DateType.Instance, TimestampType.Instance));
        Assert.Equal(TimestampType.Instance, TypeCoercion.FindTightestCommonType(TimestampType.Instance, DateType.Instance));
    }

    [Fact]
    public void FindWiderCommonType_FoldsList()
    {
        Assert.Equal(LongType.Instance, TypeCoercion.FindWiderCommonType(
            new DataType[] { ByteType.Instance, IntegerType.Instance, LongType.Instance }));
        Assert.Equal(DoubleType.Instance, TypeCoercion.FindWiderCommonType(
            new DataType[] { IntegerType.Instance, FloatType.Instance, DoubleType.Instance }));
        Assert.Null(TypeCoercion.FindWiderCommonType(new DataType[] { IntegerType.Instance, StringType.Instance }));
    }

    // AC5: SQL null propagation — the null type widens to any peer; it is not a CLR default.
    [Fact]
    public void NullType_PromotesToPeer()
    {
        Assert.Equal(IntegerType.Instance, TypeCoercion.FindWiderTypeForTwo(NullType.Instance, IntegerType.Instance));
        Assert.Equal(StringType.Instance, TypeCoercion.FindWiderTypeForTwo(StringType.Instance, NullType.Instance));
        Assert.Equal(NullType.Instance, TypeCoercion.FindWiderTypeForTwo(NullType.Instance, NullType.Instance));
        Assert.True(TypeCoercion.CanCoerce(NullType.Instance, new DecimalType(10, 2)));
    }

    // AC4: nested errors name source, target, and the dotted expression path.
    [Fact]
    public void ArrayElement_MismatchNamesElementPath()
    {
        TypeCoercionException ex = Assert.Throws<TypeCoercionException>(() =>
            TypeCoercion.EnsureCoercible(new ArrayType(IntegerType.Instance), new ArrayType(StringType.Instance)));
        Assert.Equal("int", ex.SourceType);
        Assert.Equal("string", ex.TargetType);
        Assert.Equal("value.element", ex.Path);
        Assert.Contains("value.element", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapValue_MismatchNamesValuePath()
    {
        TypeCoercionException ex = Assert.Throws<TypeCoercionException>(() => TypeCoercion.EnsureCoercible(
            new MapType(StringType.Instance, IntegerType.Instance),
            new MapType(StringType.Instance, BinaryType.Instance)));
        Assert.Equal("value.value", ex.Path);
    }

    [Fact]
    public void StructField_MismatchNamesFieldPath()
    {
        var src = new StructType(new[] { new StructField("price", IntegerType.Instance) });
        var dst = new StructType(new[] { new StructField("price", StringType.Instance) });
        TypeCoercionException ex = Assert.Throws<TypeCoercionException>(() =>
            TypeCoercion.EnsureCoercible(src, dst, "order"));
        Assert.Equal("order.price", ex.Path);
        Assert.Equal("int", ex.SourceType);
    }

    [Fact]
    public void NumericWidening_AcrossStructAndArray_IsAllowed()
    {
        var src = new StructType(new[] { new StructField("a", new ArrayType(IntegerType.Instance)) });
        var dst = new StructType(new[] { new StructField("a", new ArrayType(LongType.Instance)) });
        TypeCoercion.EnsureCoercible(src, dst); // widening int→long inside the array does not throw
        Assert.True(TypeCoercion.CanCoerce(src, dst));
    }

    // #707: at the throw site source/target CAN be a non-atomic StructType (adjudicated by predicate), whose
    // SimpleString recursively appends every nested field name verbatim. The MESSAGE must echo the bounded
    // KIND and neutralize the schema path, even though the exact type stays on the typed channel.
    [Fact]
    public void NonAtomicSource_MessageEchoesBoundedKind_NotRecursiveFieldNames()
    {
        // A wide hostile struct with newline-injecting field names, coerced (as an array element) to int so
        // the failing source is the struct itself.
        var fields = new StructField[64];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = new StructField(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"secret_col_{i}\r\nINJECTED"),
                StringType.Instance);
        }

        var hostile = new StructType(fields);
        TypeCoercionException ex = Assert.Throws<TypeCoercionException>(() =>
            TypeCoercion.EnsureCoercible(new ArrayType(hostile), new ArrayType(IntegerType.Instance)));

        // The message names the bounded kind, never a nested field name, and is short + control-char free.
        Assert.Contains("'struct'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_col_", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.True(ex.Message.Length < 256, $"message was {ex.Message.Length} chars; expected bounded.");

        // The exact type is still recoverable on the typed channel.
        Assert.StartsWith("struct<", ex.SourceType, StringComparison.Ordinal);
    }

    // #707: the expression PATH is built from foreign field names, so it must be sanitized in the message.
    [Fact]
    public void HostileFieldNameInPath_IsNeutralizedInTheMessage()
    {
        var src = new StructType(new[] { new StructField("a\r\nb\u2028c", IntegerType.Instance) });
        var dst = new StructType(new[] { new StructField("a\r\nb\u2028c", StringType.Instance) });
        TypeCoercionException ex = Assert.Throws<TypeCoercionException>(() =>
            TypeCoercion.EnsureCoercible(src, dst));

        // The raw path survives on the typed channel; the message is neutralized.
        Assert.Contains("\r\n", ex.Path!, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\u2028', ex.Message);
    }
}
