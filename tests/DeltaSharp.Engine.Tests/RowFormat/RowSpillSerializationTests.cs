using DeltaSharp.Engine.Memory;
using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.RowFormat;

/// <summary>
/// STORY-02.4.2 AC3: a row serialized to a spill/shuffle frame and read back decodes to identical
/// values, nulls, and schema-version metadata. The frame header carries the format version, schema
/// fingerprint, and payload length, all of which round-trip.
/// </summary>
public class RowSpillSerializationTests
{
    private static readonly StructType AllScalars = new(
    [
        new StructField("b", BooleanType.Instance),
        new StructField("i8", ByteType.Instance),
        new StructField("i16", ShortType.Instance),
        new StructField("i32", IntegerType.Instance),
        new StructField("i64", LongType.Instance),
        new StructField("f32", FloatType.Instance),
        new StructField("f64", DoubleType.Instance),
        new StructField("d", DateType.Instance),
        new StructField("ts", TimestampType.Instance),
        new StructField("decS", new DecimalType(9, 2)),
        new StructField("decL", new DecimalType(30, 4)),
        new StructField("s", StringType.Instance),
        new StructField("bin", BinaryType.Instance),
    ]);

    private static BinaryRow Encode(StructType schema, params object?[] values)
    {
        var encoder = new BinaryRowEncoder(new NativeMemoryAllocator(scratchThreshold: 0));
        return encoder.Encode(new RowData(schema, values));
    }

    [Fact]
    public void Frame_RoundTrips_AllScalarValues()
    {
        var source = new RowData(
            AllScalars,
            true, (sbyte)-7, (short)-1234, 42, 9_000_000_000L, -1.5f, double.NaN, 19000,
            1_700_000_000_000_000L, (Int128)12345, (Int128)(-987654321012345), "héllo",
            new byte[] { 1, 2, 3, 0, 255 });

        using BinaryRow row = Encode(AllScalars, source[0], source[1], source[2], source[3], source[4],
            source[5], source[6], source[7], source[8], source[9], source[10], source[11], source[12]);

        byte[] frame = RowSpillSerializer.WriteFrame(row);
        RowData decoded = RowSpillSerializer.ReadFrame(frame, AllScalars, out int consumed);

        Assert.Equal(source, decoded);
        Assert.Equal(frame.Length, consumed);
    }

    [Fact]
    public void Frame_RoundTrips_NullsAndExtremes()
    {
        var source = new RowData(
            AllScalars,
            false, sbyte.MinValue, short.MaxValue, int.MinValue, long.MaxValue, -0.0f,
            double.NegativeInfinity, null, long.MinValue, null, Int128.MinValue, null, Array.Empty<byte>());

        using BinaryRow row = Encode(AllScalars, source[0], source[1], source[2], source[3], source[4],
            source[5], source[6], source[7], source[8], source[9], source[10], source[11], source[12]);

        byte[] frame = RowSpillSerializer.WriteFrame(row);
        RowData decoded = RowSpillSerializer.ReadFrame(frame, AllScalars, out _);

        Assert.Equal(source, decoded);
        Assert.True(decoded.Schema[7].DataType is DateType);
        Assert.Null(decoded[7]);
        Assert.Null(decoded[9]);
        Assert.Null(decoded[11]);
    }

    [Fact]
    public void Frame_RoundTrips_NestedRow()
    {
        var inner = new StructType([new StructField("s", StringType.Instance), new StructField("n", IntegerType.Instance)]);
        var schema = new StructType(
        [
            new StructField("arr", new ArrayType(inner)),
            new StructField("blob", BinaryType.Instance),
        ]);
        var arr = new ArrayData(inner, true, new RowData(inner, "abc", 1), null, new RowData(inner, "x", 2));
        var source = new RowData(schema, arr, new byte[] { 9, 8, 7 });

        using BinaryRow row = Encode(schema, arr, new byte[] { 9, 8, 7 });
        byte[] frame = RowSpillSerializer.WriteFrame(row);
        RowData decoded = RowSpillSerializer.ReadFrame(frame, schema, out _);

        Assert.Equal(source, decoded);
    }

    [Fact]
    public void Header_CarriesSchemaVersionMetadata()
    {
        using BinaryRow row = Encode(AllScalars, true, (sbyte)1, (short)2, 3, 4L, 5f, 6d, 7, 8L,
            (Int128)9, (Int128)10, "x", new byte[] { 1 });
        byte[] frame = RowSpillSerializer.WriteFrame(row);

        RowFrameHeader header = RowSpillSerializer.ReadHeader(frame);

        Assert.Equal(RowSpillSerializer.CurrentFormatVersion, header.FormatVersion);
        Assert.Equal(RowSpillSerializer.SchemaFingerprint(AllScalars), header.SchemaFingerprint);
        Assert.Equal(row.Length, header.PayloadLength);
        Assert.Equal(RowSpillSerializer.HeaderSize + row.Length, frame.Length);
    }

    [Fact]
    public void WriteFrame_SpanAndArray_ProduceIdenticalBytes()
    {
        using BinaryRow row = Encode(AllScalars, true, (sbyte)1, (short)2, 3, 4L, 5f, 6d, 7, 8L,
            (Int128)9, (Int128)10, "x", new byte[] { 1 });

        byte[] viaArray = RowSpillSerializer.WriteFrame(row);
        byte[] viaSpan = new byte[RowSpillSerializer.GetFrameSize(row)];
        int written = RowSpillSerializer.WriteFrame(row, viaSpan);

        Assert.Equal(viaArray.Length, written);
        Assert.True(viaArray.AsSpan().SequenceEqual(viaSpan));
    }

    [Fact]
    public void MultipleFrames_ReadBackToBack_UsingBytesConsumed()
    {
        var schema = new StructType([new StructField("i", IntegerType.Instance), new StructField("s", StringType.Instance)]);
        RowData[] sources =
        [
            new(schema, 1, "one"),
            new(schema, 2, "two-is-longer"),
            new(schema, null, null),
        ];

        // Concatenate three frames into one buffer (a mini spill segment).
        var segment = new List<byte>();
        foreach (RowData src in sources)
        {
            using BinaryRow row = Encode(schema, src[0], src[1]);
            segment.AddRange(RowSpillSerializer.WriteFrame(row));
        }

        byte[] bytes = [.. segment];
        int offset = 0;
        for (int i = 0; i < sources.Length; i++)
        {
            RowData decoded = RowSpillSerializer.ReadFrame(bytes.AsSpan(offset), schema, out int consumed);
            Assert.Equal(sources[i], decoded);
            offset += consumed;
        }

        Assert.Equal(bytes.Length, offset);
    }

    [Fact]
    public void Frame_IsDeterministic_AcrossRuns()
    {
        var schema = new StructType([new StructField("i", IntegerType.Instance), new StructField("s", StringType.Instance)]);
        using BinaryRow r1 = Encode(schema, 5, "hello");
        using BinaryRow r2 = Encode(schema, 5, "hello");

        Assert.True(RowSpillSerializer.WriteFrame(r1).AsSpan().SequenceEqual(RowSpillSerializer.WriteFrame(r2)));
    }
}
