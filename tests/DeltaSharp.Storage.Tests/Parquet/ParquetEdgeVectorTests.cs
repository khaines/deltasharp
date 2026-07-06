using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Deterministic edge-vector round-trips that complement the randomized parity oracle: zero rows, an
/// all-null column, empty-vs-null strings, &gt;64&#160;KB string/binary, integer min/max, negative and
/// wide decimals (precision 19–28), pre-1970 and extreme date/timestamp boundaries, and the special
/// floating-point values <c>NaN</c>/<c>+Inf</c>/<c>-Inf</c>/<c>-0.0</c> (asserted bit-for-bit).
/// </summary>
public sealed class ParquetEdgeVectorTests
{
    private static ColumnBatch SingleColumn(StructType schema, int rows, Action<MutableColumnVector> fill)
    {
        MutableColumnVector vector = ColumnVectors.Create(schema[0].DataType, Math.Max(rows, 1));
        fill(vector);
        return new ManagedColumnBatch(schema, new ColumnVector[] { vector }, rows);
    }

    private static async Task RoundTripAssertAsync(StructType schema, ColumnBatch batch)
    {
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        List<ColumnBatch> result = await ParquetTestHelpers.ReadAllAsync(file, schema);
        if (batch.LogicalRowCount == 0)
        {
            Assert.Empty(result);
            return;
        }

        ColumnBatch read = Assert.Single(result);
        TestData.AssertBatchesEqual(batch, read);
    }

    [Fact]
    public Task ZeroRows_RoundTripsToEmpty()
    {
        var schema = new StructType(new[] { new StructField("v", DataTypes.LongType, nullable: true) });
        ColumnBatch batch = SingleColumn(schema, rows: 0, _ => { });
        return RoundTripAssertAsync(schema, batch);
    }

    [Fact]
    public Task AllNullNullableColumn_RoundTrips()
    {
        var schema = new StructType(new[] { new StructField("v", DataTypes.IntegerType, nullable: true) });
        ColumnBatch batch = SingleColumn(schema, rows: 5, v =>
        {
            for (int i = 0; i < 5; i++)
            {
                v.AppendNull();
            }
        });
        return RoundTripAssertAsync(schema, batch);
    }

    [Fact]
    public Task EmptyStringVersusNull_ArePreserved()
    {
        var schema = new StructType(new[] { new StructField("s", DataTypes.StringType, nullable: true) });
        ColumnBatch batch = SingleColumn(schema, rows: 4, v =>
        {
            v.AppendBytes(Encoding.UTF8.GetBytes(string.Empty)); // empty string
            v.AppendNull();                                       // null (distinct from empty)
            v.AppendBytes(Encoding.UTF8.GetBytes("a"));
            v.AppendBytes(Array.Empty<byte>());                   // empty again
        });
        return RoundTripAssertAsync(schema, batch);
    }

    [Fact]
    public async Task EmptyStringIsNotNull_AfterRoundTrip()
    {
        var schema = new StructType(new[] { new StructField("s", DataTypes.StringType, nullable: true) });
        ColumnBatch batch = SingleColumn(schema, rows: 2, v =>
        {
            v.AppendBytes(Array.Empty<byte>()); // empty string
            v.AppendNull();                      // null
        });

        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        ColumnBatch read = Assert.Single(await ParquetTestHelpers.ReadAllAsync(file, schema));
        ColumnVector column = read.SelectedColumn(0);
        Assert.False(column.IsNull(0));
        Assert.Empty(column.GetBytes(0).ToArray());
        Assert.True(column.IsNull(1));
    }

    [Fact]
    public Task LargeAndEmptyStringBinary_RoundTrip()
    {
        var schema = new StructType(new[]
        {
            new StructField("s", DataTypes.StringType, nullable: true),
            new StructField("b", DataTypes.BinaryType, nullable: true),
        });

        string large = new string('x', 70_000); // > 64 KB
        var largeBinary = new byte[70_000];
        for (int i = 0; i < largeBinary.Length; i++)
        {
            largeBinary[i] = (byte)(i % 251);
        }

        MutableColumnVector s = ColumnVectors.Create(DataTypes.StringType, 3);
        MutableColumnVector b = ColumnVectors.Create(DataTypes.BinaryType, 3);
        s.AppendBytes(Encoding.UTF8.GetBytes(string.Empty));
        b.AppendBytes(Array.Empty<byte>());
        s.AppendBytes(Encoding.UTF8.GetBytes(large));
        b.AppendBytes(largeBinary);
        s.AppendNull();
        b.AppendNull();

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { s, b }, 3);
        return RoundTripAssertAsync(schema, batch);
    }

    [Fact]
    public Task IntegralMinMax_RoundTrip()
    {
        var schema = new StructType(new[]
        {
            new StructField("byte", DataTypes.ByteType, nullable: false),
            new StructField("short", DataTypes.ShortType, nullable: false),
            new StructField("int", DataTypes.IntegerType, nullable: false),
            new StructField("long", DataTypes.LongType, nullable: false),
        });

        MutableColumnVector bytes = ColumnVectors.Create(DataTypes.ByteType, 3);
        MutableColumnVector shorts = ColumnVectors.Create(DataTypes.ShortType, 3);
        MutableColumnVector ints = ColumnVectors.Create(DataTypes.IntegerType, 3);
        MutableColumnVector longs = ColumnVectors.Create(DataTypes.LongType, 3);

        // Byte lane holds the raw byte; signed tinyint extremes are lane 0x80 (-128) and 0x7F (127).
        byte[] byteLanes = { 0x80, 0x7F, 0x00 };
        short[] shortLanes = { short.MinValue, short.MaxValue, 0 };
        int[] intLanes = { int.MinValue, int.MaxValue, -1 };
        long[] longLanes = { long.MinValue, long.MaxValue, -1 };
        for (int i = 0; i < 3; i++)
        {
            bytes.AppendValue(byteLanes[i]);
            shorts.AppendValue(shortLanes[i]);
            ints.AppendValue(intLanes[i]);
            longs.AppendValue(longLanes[i]);
        }

        var batch = new ManagedColumnBatch(
            schema, new ColumnVector[] { bytes, shorts, ints, longs }, 3);
        return RoundTripAssertAsync(schema, batch);
    }

    [Fact]
    public Task CompactDecimalBoundaries_RoundTrip()
    {
        DataType dec = DataTypes.CreateDecimalType(18, 0);
        var schema = new StructType(new[] { new StructField("v", dec, nullable: true) });
        ColumnBatch batch = SingleColumn(schema, rows: 4, v =>
        {
            v.AppendValue(999_999_999_999_999_999L);  // 10^18 - 1 (max magnitude)
            v.AppendValue(-999_999_999_999_999_999L); // negative max magnitude
            v.AppendValue(0L);
            v.AppendNull();
        });
        return RoundTripAssertAsync(schema, batch);
    }

    [Fact]
    public Task WideDecimalBoundaries_RoundTrip()
    {
        // Precision 19 and 28 exercise the Int128 (wide) lane, incl. a negative wide magnitude.
        DataType dec19 = DataTypes.CreateDecimalType(19, 4);
        var schema19 = new StructType(new[] { new StructField("v", dec19, nullable: false) });
        ColumnBatch batch19 = SingleColumn(schema19, rows: 2, v =>
        {
            v.AppendValue((Int128)1_234_567_890_123_456_789L);
            v.AppendValue((Int128)(-1_234_567_890_123_456_789L));
        });

        DataType dec28 = DataTypes.CreateDecimalType(28, 4);
        var schema28 = new StructType(new[] { new StructField("v", dec28, nullable: false) });
        Int128 wide = Int128.Parse("1234567890123456789012345", System.Globalization.CultureInfo.InvariantCulture);
        ColumnBatch batch28 = SingleColumn(schema28, rows: 2, v =>
        {
            v.AppendValue(wide);
            v.AppendValue(-wide);
        });

        return Task.WhenAll(
            RoundTripAssertAsync(schema19, batch19),
            RoundTripAssertAsync(schema28, batch28));
    }

    [Fact]
    public Task DateBoundaries_RoundTrip()
    {
        var schema = new StructType(new[] { new StructField("d", DataTypes.DateType, nullable: false) });
        ColumnBatch batch = SingleColumn(schema, rows: 5, v =>
        {
            v.AppendValue(-719_162); // 0001-01-01 (minimum representable date)
            v.AppendValue(-1);       // 1969-12-31 (pre-1970 negative epoch-day)
            v.AppendValue(0);        // 1970-01-01
            v.AppendValue(19_000);   // a recent date
            v.AppendValue(2_932_896); // 9999-12-31 (maximum representable date)
        });
        return RoundTripAssertAsync(schema, batch);
    }

    [Fact]
    public Task TimestampBoundaries_RoundTrip()
    {
        var schema = new StructType(new[] { new StructField("ts", DataTypes.TimestampType, nullable: false) });
        ColumnBatch batch = SingleColumn(schema, rows: 5, v =>
        {
            v.AppendValue(-62_135_596_800_000_000L); // year 1 UTC (minimum whole-second instant)
            v.AppendValue(-1_000_000L);              // pre-1970 negative epoch-micros
            v.AppendValue(0L);                        // epoch
            v.AppendValue(1_700_000_000_000_000L);    // a recent instant
            v.AppendValue(253_402_300_799_000_000L);  // year 9999 (maximum whole-second instant)
        });
        return RoundTripAssertAsync(schema, batch);
    }

    [Fact]
    public async Task FloatingPointSpecials_RoundTripBitForBit()
    {
        var schema = new StructType(new[]
        {
            new StructField("f", DataTypes.FloatType, nullable: false),
            new StructField("d", DataTypes.DoubleType, nullable: false),
        });

        float[] floats = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0.0f, 0.0f };
        double[] doubles = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.0d, 0.0d };

        MutableColumnVector f = ColumnVectors.Create(DataTypes.FloatType, floats.Length);
        MutableColumnVector d = ColumnVectors.Create(DataTypes.DoubleType, doubles.Length);
        for (int i = 0; i < floats.Length; i++)
        {
            f.AppendValue(floats[i]);
            d.AppendValue(doubles[i]);
        }

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { f, d }, floats.Length);
        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        ColumnBatch read = Assert.Single(await ParquetTestHelpers.ReadAllAsync(file, schema));

        ColumnVector readF = read.SelectedColumn(0);
        ColumnVector readD = read.SelectedColumn(1);
        for (int i = 0; i < floats.Length; i++)
        {
            // Bit-for-bit equality distinguishes -0.0 from +0.0 and confirms NaN/Inf survive.
            Assert.Equal(BitConverter.SingleToInt32Bits(floats[i]), BitConverter.SingleToInt32Bits(readF.GetValue<float>(i)));
            Assert.Equal(BitConverter.DoubleToInt64Bits(doubles[i]), BitConverter.DoubleToInt64Bits(readD.GetValue<double>(i)));
        }
    }
}
