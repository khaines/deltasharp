using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.RowFormat;

/// <summary>
/// STORY-02.4.2 AC1: for every supported sort-key type, a bytewise (memcmp) comparison of the
/// order-preserving encoding matches the documented Spark ordering for the configured direction and
/// null placement — signed-int sign-bit flip, IEEE-754 NaN/−0.0 handling, decimal/timestamp order,
/// and lexicographic strings/binary.
/// </summary>
public class ByteSortableOrderingTests
{
    private static byte[] EncodeKey(DataType type, SortKeyOrdering ordering, object? value)
    {
        var schema = new StructType([new StructField("k", type)]);
        var encoder = new SortKeyEncoder(schema, [0], [ordering]);
        return encoder.Encode(new RowData(schema, value));
    }

    /// <summary>Asserts the values are in strictly increasing memcmp order ascending and strictly decreasing descending.</summary>
    private static void AssertOrderPreserved(DataType type, params object?[] ascendingValues)
    {
        AssertByteOrder(type, SortKeyOrdering.Ascending, ascendingValues, ascending: true);
        AssertByteOrder(
            type,
            new SortKeyOrdering(SortKeyDirection.Descending, NullSortOrder.NullsLast),
            ascendingValues,
            ascending: false);
    }

    private static void AssertByteOrder(DataType type, SortKeyOrdering ordering, object?[] values, bool ascending)
    {
        byte[][] keys = new byte[values.Length][];
        for (int i = 0; i < values.Length; i++)
        {
            keys[i] = EncodeKey(type, ordering, values[i]);
        }

        for (int i = 0; i + 1 < keys.Length; i++)
        {
            int cmp = keys[i].AsSpan().SequenceCompareTo(keys[i + 1]);
            if (ascending)
            {
                Assert.True(cmp < 0, $"[{type.SimpleString}] expected value #{i} < #{i + 1} ascending (got memcmp {cmp}).");
            }
            else
            {
                Assert.True(cmp > 0, $"[{type.SimpleString}] expected value #{i} > #{i + 1} descending (got memcmp {cmp}).");
            }
        }
    }

    [Fact]
    public void Boolean_FalseSortsBeforeTrue() =>
        AssertOrderPreserved(BooleanType.Instance, false, true);

    [Fact]
    public void SignedByte_OrdersAcrossSignBoundary() =>
        AssertOrderPreserved(ByteType.Instance, sbyte.MinValue, (sbyte)-1, (sbyte)0, (sbyte)1, sbyte.MaxValue);

    [Fact]
    public void Short_OrdersAcrossSignBoundary() =>
        AssertOrderPreserved(ShortType.Instance, short.MinValue, (short)-1, (short)0, (short)1, short.MaxValue);

    [Fact]
    public void Int_OrdersAcrossSignBoundary() =>
        AssertOrderPreserved(IntegerType.Instance, int.MinValue, -2, -1, 0, 1, 2, int.MaxValue);

    [Fact]
    public void Long_OrdersAcrossSignBoundary() =>
        AssertOrderPreserved(LongType.Instance, long.MinValue, -2L, -1L, 0L, 1L, long.MaxValue);

    [Fact]
    public void Date_OrdersLikeInt32() =>
        AssertOrderPreserved(DateType.Instance, TemporalValues.MinEpochDay, -1, 0, 1, 18000, TemporalValues.MaxEpochDay);

    [Fact]
    public void Timestamp_OrdersLikeInt64() =>
        AssertOrderPreserved(
            TimestampType.Instance,
            TemporalValues.MinEpochMicros, -1L, 0L, 1L, 1_700_000_000_000_000L, TemporalValues.MaxEpochMicros);

    [Fact]
    public void Float_OrdersWithInfinitiesAndNaNLargest() =>
        AssertOrderPreserved(
            FloatType.Instance,
            float.NegativeInfinity, float.MinValue, -1f, -float.Epsilon, 0f, float.Epsilon, 1f,
            float.MaxValue, float.PositiveInfinity, float.NaN);

    [Fact]
    public void Double_OrdersWithInfinitiesAndNaNLargest() =>
        AssertOrderPreserved(
            DoubleType.Instance,
            double.NegativeInfinity, double.MinValue, -1d, -double.Epsilon, 0d, double.Epsilon, 1d,
            double.MaxValue, double.PositiveInfinity, double.NaN);

    [Fact]
    public void Double_NegativeZeroEncodesEqualToPositiveZero()
    {
        byte[] negZero = EncodeKey(DoubleType.Instance, SortKeyOrdering.Ascending, -0.0d);
        byte[] posZero = EncodeKey(DoubleType.Instance, SortKeyOrdering.Ascending, 0.0d);
        Assert.True(negZero.AsSpan().SequenceEqual(posZero), "−0.0 and +0.0 must encode identically.");
    }

    [Fact]
    public void Float_NegativeZeroEncodesEqualToPositiveZero()
    {
        byte[] negZero = EncodeKey(FloatType.Instance, SortKeyOrdering.Ascending, -0.0f);
        byte[] posZero = EncodeKey(FloatType.Instance, SortKeyOrdering.Ascending, 0.0f);
        Assert.True(negZero.AsSpan().SequenceEqual(posZero), "−0.0f and +0.0f must encode identically.");
    }

    [Theory]
    [InlineData(0x7FF8_0000_0000_0000L)] // canonical quiet NaN
    [InlineData(unchecked((long)0xFFF8_0000_0000_0000L))] // negative-sign NaN
    [InlineData(0x7FF0_0000_0000_0001L)] // signaling NaN
    public void Double_AllNaNBitPatternsEncodeEqualAndGreaterThanInfinity(long nanBits)
    {
        double nan = BitConverter.Int64BitsToDouble(nanBits);
        Assert.True(double.IsNaN(nan));

        byte[] canonical = EncodeKey(DoubleType.Instance, SortKeyOrdering.Ascending, double.NaN);
        byte[] thisNaN = EncodeKey(DoubleType.Instance, SortKeyOrdering.Ascending, nan);
        byte[] posInf = EncodeKey(DoubleType.Instance, SortKeyOrdering.Ascending, double.PositiveInfinity);

        Assert.True(thisNaN.AsSpan().SequenceEqual(canonical), "every NaN bit pattern must encode to the canonical NaN.");
        Assert.True(thisNaN.AsSpan().SequenceCompareTo(posInf) > 0, "NaN must sort greater than +Infinity.");
    }

    [Fact]
    public void Decimal_OrdersByUnscaledValueAcrossSign() =>
        AssertOrderPreserved(
            new DecimalType(38, 4),
            Int128.MinValue, (Int128)(-100), (Int128)(-1), Int128.Zero, (Int128)1, (Int128)100, Int128.MaxValue);

    [Fact]
    public void CompactDecimal_OrdersByUnscaledValue() =>
        AssertOrderPreserved(new DecimalType(9, 2), (Int128)(-5000), (Int128)(-1), Int128.Zero, (Int128)1, (Int128)5000);

    [Fact]
    public void String_OrdersLexicographicallyIncludingEmptyAndEmbeddedNul() =>
        AssertOrderPreserved(
            StringType.Instance, string.Empty, "\u0000", "\u0000\u0000", "\u0001", "a", "a\u0000", "ab", "b", "héllo", "z");

    [Fact]
    public void Binary_OrdersLexicographicallyAsUnsignedBytes() =>
        AssertOrderPreserved(
            BinaryType.Instance,
            Array.Empty<byte>(),
            new byte[] { 0x00 },
            new byte[] { 0x00, 0x00 },
            new byte[] { 0x00, 0xFF },
            new byte[] { 0x01 },
            new byte[] { 0x7F },
            new byte[] { 0x80 }, // unsigned: 0x80 > 0x7F
            new byte[] { 0xFF });

    [Fact]
    public void Int_KnownEncoding_IsPresentMarkerThenSignFlippedBigEndian()
    {
        byte[] zero = EncodeKey(IntegerType.Instance, SortKeyOrdering.Ascending, 0);
        Assert.Equal(new byte[] { 0x01, 0x80, 0x00, 0x00, 0x00 }, zero);

        byte[] minusOne = EncodeKey(IntegerType.Instance, SortKeyOrdering.Ascending, -1);
        Assert.Equal(new byte[] { 0x01, 0x7F, 0xFF, 0xFF, 0xFF }, minusOne);
    }

    [Fact]
    public void MultiColumnKey_BreaksTiesOnSecondField()
    {
        var schema = new StructType(
        [
            new StructField("a", IntegerType.Instance),
            new StructField("b", StringType.Instance),
        ]);
        var encoder = new SortKeyEncoder(schema, [0, 1], [SortKeyOrdering.Ascending, SortKeyOrdering.Ascending]);

        byte[] k1 = encoder.Encode(new RowData(schema, 5, "alpha"));
        byte[] k2 = encoder.Encode(new RowData(schema, 5, "beta"));
        byte[] k3 = encoder.Encode(new RowData(schema, 6, "aardvark"));

        Assert.True(k1.AsSpan().SequenceCompareTo(k2) < 0, "equal first field falls through to second.");
        Assert.True(k2.AsSpan().SequenceCompareTo(k3) < 0, "smaller first field wins regardless of second.");
    }

    [Fact]
    public void MultiColumnKey_PerFieldDirectionIsIndependent()
    {
        var schema = new StructType(
        [
            new StructField("a", IntegerType.Instance),
            new StructField("b", IntegerType.Instance),
        ]);
        // a ascending, b descending.
        var encoder = new SortKeyEncoder(
            schema, [0, 1], [SortKeyOrdering.Ascending, SortKeyOrdering.Descending]);

        byte[] k1 = encoder.Encode(new RowData(schema, 5, 100));
        byte[] k2 = encoder.Encode(new RowData(schema, 5, 99));
        Assert.True(k1.AsSpan().SequenceCompareTo(k2) < 0, "within equal a, larger b sorts first (descending b).");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullPlacement_IsIndependentOfDirection(bool descending)
    {
        var direction = descending ? SortKeyDirection.Descending : SortKeyDirection.Ascending;
        var type = IntegerType.Instance;

        byte[] nullFirst = EncodeKey(type, new SortKeyOrdering(direction, NullSortOrder.NullsFirst), null);
        byte[] presentFirst = EncodeKey(type, new SortKeyOrdering(direction, NullSortOrder.NullsFirst), 5);
        Assert.True(nullFirst.AsSpan().SequenceCompareTo(presentFirst) < 0, "nulls-first: null sorts before present.");

        byte[] nullLast = EncodeKey(type, new SortKeyOrdering(direction, NullSortOrder.NullsLast), null);
        byte[] presentLast = EncodeKey(type, new SortKeyOrdering(direction, NullSortOrder.NullsLast), 5);
        Assert.True(nullLast.AsSpan().SequenceCompareTo(presentLast) > 0, "nulls-last: null sorts after present.");
    }

    [Fact]
    public void TwoNulls_CompareEqualOnThatField()
    {
        var schema = new StructType(
        [
            new StructField("a", IntegerType.Instance),
            new StructField("b", IntegerType.Instance),
        ]);
        var encoder = new SortKeyEncoder(schema, [0, 1], [SortKeyOrdering.Ascending, SortKeyOrdering.Ascending]);

        byte[] k1 = encoder.Encode(new RowData(schema, null, 7));
        byte[] k2 = encoder.Encode(new RowData(schema, null, 9));
        Assert.True(k1.AsSpan().SequenceCompareTo(k2) < 0, "two nulls tie on field a; field b breaks the tie.");
    }

    [Fact]
    public void UnsupportedKeyType_Throws()
    {
        var schema = new StructType([new StructField("xs", new ArrayType(IntegerType.Instance))]);
        Assert.Throws<RowFormatException>(() => new SortKeyEncoder(schema, [0], [SortKeyOrdering.Ascending]));
    }
}
