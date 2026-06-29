using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.RowFormat;

/// <summary>
/// STORY-02.4.1 AC1: a schema with fixed-width, variable-width, and nullable fields yields an
/// 8-byte-aligned null bitset, fixed region, header, and total row size, and the geometry
/// classifies inline vs. variable fields correctly.
/// </summary>
public class RowLayoutTests
{
    private static StructType MixedSchema() => new(
    [
        new StructField("id", LongType.Instance, nullable: false),     // fixed inline
        new StructField("name", StringType.Instance, nullable: true),  // variable
        new StructField("score", DoubleType.Instance, nullable: true), // fixed inline
        new StructField("blob", BinaryType.Instance, nullable: true),  // variable
        new StructField("big", new DecimalType(30, 4), nullable: true), // 16-byte decimal -> variable
    ]);

    [Fact]
    public void HeaderRegions_AreEightByteAligned()
    {
        var layout = new RowLayout(MixedSchema());

        Assert.Equal(5, layout.FieldCount);
        Assert.Equal(0, layout.NullBitSetBytes % 8);
        Assert.Equal(8, layout.NullBitSetBytes); // ceil(5/64) word == 8 bytes
        Assert.Equal(40, layout.FixedRegionBytes); // 5 * 8
        Assert.Equal(0, layout.FixedRegionBytes % 8);
        Assert.Equal(0, layout.HeaderBytes % 8);
        Assert.Equal(48, layout.HeaderBytes);
    }

    [Fact]
    public void SlotOffsets_FollowBitsetThenEightByteSlots()
    {
        var layout = new RowLayout(MixedSchema());
        for (int i = 0; i < layout.FieldCount; i++)
        {
            Assert.Equal(layout.NullBitSetBytes + (i * 8), layout.SlotOffset(i));
        }
    }

    [Fact]
    public void InlineClassification_MatchesPhysicalLayout()
    {
        var layout = new RowLayout(MixedSchema());
        Assert.True(layout.IsInline(0));   // long
        Assert.False(layout.IsInline(1));  // string
        Assert.True(layout.IsInline(2));   // double
        Assert.False(layout.IsInline(3));  // binary
        Assert.False(layout.IsInline(4));  // decimal(30,4) -> 16 bytes
    }

    [Theory]
    [InlineData(0, 0)]   // ceil(0/64) = 0 words
    [InlineData(1, 8)]
    [InlineData(64, 8)]
    [InlineData(65, 16)]
    public void NullBitset_RoundsUpToWholeWords(int fields, int expectedBytes)
    {
        var struct64 = new StructType(
            Enumerable.Range(0, fields).Select(i => new StructField($"f{i}", IntegerType.Instance)));
        Assert.Equal(expectedBytes, new RowLayout(struct64).NullBitSetBytes);
    }

    [Fact]
    public void CompactDecimal_IsInline_LargeDecimal_IsVariable()
    {
        Assert.True(RowLayout.IsInlineFixedWidth(new DecimalType(18, 2)));   // 8 bytes
        Assert.False(RowLayout.IsInlineFixedWidth(new DecimalType(19, 2)));  // 16 bytes
    }

    [Fact]
    public void VoidField_HasNoPhysicalLayout_Throws()
    {
        var schema = new StructType([new StructField("n", NullType.Instance)]);
        Assert.Throws<UnsupportedTypeException>(() => new RowLayout(schema));
    }
}
