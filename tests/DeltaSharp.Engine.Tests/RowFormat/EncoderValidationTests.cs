using DeltaSharp.Engine.Memory;
using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.RowFormat;

/// <summary>
/// Encoder guardrails and the AC1 alignment invariant under nesting: a CLR value whose type does
/// not match its field throws a bounded <see cref="RowFormatException"/>, and deeply nested rows
/// stay 8-byte aligned in total.
/// </summary>
public class EncoderValidationTests
{
    [Fact]
    public void TypeMismatch_ThrowsRowFormatException()
    {
        var schema = new StructType([new StructField("id", LongType.Instance)]);
        var encoder = new BinaryRowEncoder(new NativeMemoryAllocator(scratchThreshold: 0));

        var ex = Assert.Throws<RowFormatException>(() => encoder.Encode(new RowData(schema, "not-a-long")));
        Assert.Contains("id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedRows_TotalSizeIsEightAligned()
    {
        var inner = new StructType([new StructField("s", StringType.Instance), new StructField("n", IntegerType.Instance)]);
        var schema = new StructType(
        [
            new StructField("arr", new ArrayType(inner)),
            new StructField("blob", BinaryType.Instance),
        ]);
        var arr = new ArrayData(inner, true, new RowData(inner, "abc", 1), null, new RowData(inner, "x", 2));
        var source = new RowData(schema, arr, new byte[] { 1, 2, 3, 4, 5, 6, 7 });

        var encoder = new BinaryRowEncoder(new NativeMemoryAllocator(scratchThreshold: 0));
        using BinaryRow row = encoder.Encode(source);

        Assert.Equal(0, row.Length % 8);
        Assert.Equal(source, row.ToRowData());
    }

    [Fact]
    public void EncodeDecode_IsDeterministic_AcrossRuns()
    {
        var schema = new StructType([new StructField("a", IntegerType.Instance), new StructField("b", StringType.Instance)]);
        var encoder = new BinaryRowEncoder(new NativeMemoryAllocator(scratchThreshold: 0));

        using BinaryRow r1 = encoder.Encode(new RowData(schema, 5, "hello"));
        using BinaryRow r2 = encoder.Encode(new RowData(schema, 5, "hello"));
        Assert.True(r1.AsSpan().SequenceEqual(r2.AsSpan()));
    }
}
