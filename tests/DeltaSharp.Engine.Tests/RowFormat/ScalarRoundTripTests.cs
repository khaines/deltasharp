using DeltaSharp.Engine.Memory;
using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.RowFormat;

/// <summary>
/// STORY-02.4.1 AC1/AC2: every supported scalar type round-trips through encode/decode, and the
/// total encoded size is 8-byte aligned for fixed-width, variable-width, and large-decimal fields.
/// </summary>
public class ScalarRoundTripTests
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
        new StructField("decS", new DecimalType(9, 2)),    // compact, inline
        new StructField("decL", new DecimalType(30, 4)),   // 16-byte, variable
        new StructField("s", StringType.Instance),
        new StructField("bin", BinaryType.Instance),
    ]);

    private static object?[] SampleValues() =>
    [
        true, (sbyte)-7, (short)-1234, 42, 9_000_000_000L, 1.5f, -2.5d, 19000, 1_700_000_000_000_000L,
        (Int128)12345, (Int128)(-987654321012345), "héllo", new byte[] { 1, 2, 3, 0, 255 },
    ];

    [Fact]
    public void AllScalarTypes_RoundTrip_AndTotalSizeIsEightAligned()
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 0);
        var encoder = new BinaryRowEncoder(allocator);
        var source = new RowData(AllScalars, SampleValues());

        using BinaryRow row = encoder.Encode(source);

        Assert.Equal(0, row.Length % 8); // AC1: total size 8-aligned
        Assert.Equal(source, row.ToRowData()); // AC2: decoded values match source
    }

    [Fact]
    public void NegativeAndExtremeNumbers_RoundTrip()
    {
        var allocator = new NativeMemoryAllocator(scratchThreshold: 0);
        var encoder = new BinaryRowEncoder(allocator);
        var source = new RowData(
            AllScalars,
            false, sbyte.MinValue, short.MaxValue, int.MinValue, long.MaxValue, float.NaN,
            double.NegativeInfinity, int.MaxValue, long.MinValue, (Int128)(-1), Int128.MinValue,
            string.Empty, Array.Empty<byte>());

        using BinaryRow row = encoder.Encode(source);
        Assert.Equal(source, row.ToRowData());
    }

    [Fact]
    public void Bytes_AreLittleEndian_ForKnownLongValue()
    {
        var schema = new StructType([new StructField("x", LongType.Instance)]);
        var allocator = new NativeMemoryAllocator(scratchThreshold: 0);
        var encoder = new BinaryRowEncoder(allocator);

        using BinaryRow row = encoder.Encode(new RowData(schema, 0x0102030405060708L));
        ReadOnlySpan<byte> bytes = row.AsSpan();

        // bitset(8) then 8-byte slot; LE means least significant byte first.
        Assert.Equal(0x08, bytes[8]);
        Assert.Equal(0x01, bytes[15]);
        Assert.Equal(16, row.Length);
    }
}
