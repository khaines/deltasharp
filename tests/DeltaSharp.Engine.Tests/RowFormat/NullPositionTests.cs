using DeltaSharp.Engine.Memory;
using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.RowFormat;

/// <summary>
/// STORY-02.4.1 AC2: with null and non-null values in every field position, decoded values match
/// the source and null bits match the source nulls — verified by nulling each position in turn.
/// </summary>
public class NullPositionTests
{
    private static readonly StructType Schema = new(
    [
        new StructField("a", IntegerType.Instance),
        new StructField("b", StringType.Instance),
        new StructField("c", LongType.Instance),
        new StructField("d", BinaryType.Instance),
        new StructField("e", new DecimalType(30, 0)), // 16-byte variable decimal
    ]);

    private static object?[] FullValues() => [7, "x", 11L, new byte[] { 9, 9 }, (Int128)123];

    [Fact]
    public void NoNulls_AllFieldsPresent()
    {
        var encoder = new BinaryRowEncoder(new NativeMemoryAllocator(scratchThreshold: 0));
        var source = new RowData(Schema, FullValues());

        using BinaryRow row = encoder.Encode(source);

        for (int i = 0; i < Schema.Count; i++)
        {
            Assert.False(row.IsNullAt(i));
        }

        Assert.Equal(source, row.ToRowData());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SingleNull_AtEachPosition_PreservesNullAndOtherValues(int nullIndex)
    {
        object?[] values = FullValues();
        values[nullIndex] = null;
        var encoder = new BinaryRowEncoder(new NativeMemoryAllocator(scratchThreshold: 0));
        var source = new RowData(Schema, values);

        using BinaryRow row = encoder.Encode(source);

        for (int i = 0; i < Schema.Count; i++)
        {
            Assert.Equal(i == nullIndex, row.IsNullAt(i));
        }

        Assert.Equal(source, row.ToRowData());
    }

    [Fact]
    public void AllNulls_RoundTrip()
    {
        var encoder = new BinaryRowEncoder(new NativeMemoryAllocator(scratchThreshold: 0));
        var source = new RowData(Schema, null, null, null, null, null);

        using BinaryRow row = encoder.Encode(source);

        for (int i = 0; i < Schema.Count; i++)
        {
            Assert.True(row.IsNullAt(i));
        }

        Assert.Equal(source, row.ToRowData());
    }
}
