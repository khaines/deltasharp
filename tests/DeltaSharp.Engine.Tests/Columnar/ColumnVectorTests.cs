using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

public class ColumnVectorTests
{
    [Fact]
    public void Int32Vector_ExposesLengthOffsetNullabilityAndTypedSpan()
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, capacity: 4);
        v.AppendValue(10);
        v.AppendNull();
        v.AppendValue(30);

        Assert.Equal(IntegerType.Instance, v.Type);
        Assert.Equal(3, v.Length);
        Assert.Equal(0, v.Offset);
        Assert.True(v.HasNulls);
        Assert.Equal(1, v.NullCount);
        Assert.False(v.IsNull(0));
        Assert.True(v.IsNull(1));

        ReadOnlySpan<int> values = v.GetValues<int>();
        Assert.Equal(3, values.Length);
        Assert.Equal(10, values[0]);
        Assert.Equal(30, values[2]);
        Assert.Equal(10, v.GetValue<int>(0));
    }

    [Theory]
    [MemberData(nameof(FixedWidthCases))]
    public void EveryFixedWidthPrimitive_RoundTripsThroughTypedAccess(string name)
    {
        // Exercises construction, append, and typed span/value read for each v1 fixed-width type.
        switch (name)
        {
            case "boolean":
                AssertRoundTrip(BooleanType.Instance, true, false, (v, x) => v.AppendValue(x), v => v.GetValue<bool>(0));
                break;
            case "byte":
                AssertRoundTrip(ByteType.Instance, (byte)7, (byte)9, (v, x) => v.AppendValue(x), v => v.GetValue<byte>(0));
                break;
            case "short":
                AssertRoundTrip(ShortType.Instance, (short)-5, (short)5, (v, x) => v.AppendValue(x), v => v.GetValue<short>(0));
                break;
            case "int":
                AssertRoundTrip(IntegerType.Instance, 42, 43, (v, x) => v.AppendValue(x), v => v.GetValue<int>(0));
                break;
            case "long":
                AssertRoundTrip(LongType.Instance, 42L, 43L, (v, x) => v.AppendValue(x), v => v.GetValue<long>(0));
                break;
            case "float":
                AssertRoundTrip(FloatType.Instance, 1.5f, 2.5f, (v, x) => v.AppendValue(x), v => v.GetValue<float>(0));
                break;
            case "double":
                AssertRoundTrip(DoubleType.Instance, 1.25d, 2.5d, (v, x) => v.AppendValue(x), v => v.GetValue<double>(0));
                break;
            case "date":
                AssertRoundTrip(DateType.Instance, 19000, 19001, (v, x) => v.AppendValue(x), v => v.GetValue<int>(0));
                break;
            case "timestamp":
                AssertRoundTrip(TimestampType.Instance, 1_700_000_000_000_000L, 1L, (v, x) => v.AppendValue(x), v => v.GetValue<long>(0));
                break;
            case "decimal-compact":
                AssertRoundTrip(new DecimalType(18, 2), 12345L, 1L, (v, x) => v.AppendValue(x), v => v.GetValue<long>(0));
                break;
            case "decimal-full":
                AssertRoundTrip(new DecimalType(38, 4), (Int128)12345, (Int128)1, (v, x) => v.AppendValue(x), v => v.GetValue<Int128>(0));
                break;
            default:
                Assert.Fail($"unhandled case {name}");
                break;
        }
    }

    public static IEnumerable<object[]> FixedWidthCases() => new[]
    {
        new object[] { "boolean" }, new object[] { "byte" }, new object[] { "short" },
        new object[] { "int" }, new object[] { "long" }, new object[] { "float" },
        new object[] { "double" }, new object[] { "date" }, new object[] { "timestamp" },
        new object[] { "decimal-compact" }, new object[] { "decimal-full" },
    };

    [Fact]
    public void StringVector_StoresUtf8Bytes()
    {
        MutableColumnVector v = ColumnVectors.Create(StringType.Instance, capacity: 2);
        v.AppendBytes(Encoding.UTF8.GetBytes("héllo"));
        v.AppendNull();

        Assert.Equal(2, v.Length);
        Assert.Equal("héllo", Encoding.UTF8.GetString(v.GetBytes(0)));
        Assert.True(v.IsNull(1));
        Assert.True(v.GetBytes(1).IsEmpty);
    }

    [Fact]
    public void BinaryVector_StoresRawBytes()
    {
        MutableColumnVector v = ColumnVectors.Create(BinaryType.Instance, capacity: 1);
        byte[] payload = { 0x00, 0xFF, 0x10 };
        v.AppendBytes(payload);

        Assert.True(v.GetBytes(0).SequenceEqual(payload));
    }

    [Fact]
    public void TypedAccessors_RejectWrongElementType()
    {
        MutableColumnVector ints = ColumnVectors.Create(IntegerType.Instance, capacity: 1);
        ints.AppendValue(1);
        Assert.Throws<InvalidOperationException>(() => ints.GetValues<long>().Length);
        Assert.Throws<InvalidOperationException>(() => ints.GetBytes(0));

        MutableColumnVector strings = ColumnVectors.Create(StringType.Instance, capacity: 1);
        strings.AppendBytes("x"u8);
        Assert.Throws<InvalidOperationException>(() => strings.GetValues<int>().Length);
    }

    [Fact]
    public void NullType_HasNoManagedVector_ButNestedTypesDo()
    {
        // NullType remains unrepresentable; nested types now build the reference nested vectors
        // (#570) instead of throwing.
        Assert.Throws<UnsupportedTypeException>(() => ColumnVectors.Create(NullType.Instance, 1));

        Assert.IsType<ListColumnVector>(ColumnVectors.Create(new ArrayType(IntegerType.Instance), 1));
        Assert.IsType<StructColumnVector>(ColumnVectors.Create(
            new StructType(new[] { new StructField("f", IntegerType.Instance) }), 1));
        Assert.IsType<MapColumnVector>(ColumnVectors.Create(
            new MapType(StringType.Instance, IntegerType.Instance), 1));
    }

    [Fact]
    public void IsNull_RejectsOutOfRangeIndex()
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, capacity: 1);
        v.AppendValue(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => v.IsNull(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => v.IsNull(-1));
    }

    private static void AssertRoundTrip<T>(
        DataType type, T first, T second, Action<MutableColumnVector, T> append, Func<MutableColumnVector, T> readFirst)
        where T : unmanaged
    {
        MutableColumnVector v = ColumnVectors.Create(type, capacity: 2);
        append(v, first);
        append(v, second);

        Assert.Equal(type, v.Type);
        Assert.Equal(2, v.Length);
        Assert.Equal(first, readFirst(v));
        Assert.Equal(second, v.GetValues<T>()[1]);
    }
}
