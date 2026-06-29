using Apache.Arrow;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Columnar.Arrow;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar.Arrow;

/// <summary>
/// Boolean export is bit-packed LSB-first, not byte-per-value (council columnar F1). The AC1 round-trip
/// only carries a boolean <c>true</c> at index 0, where byte 0 and bit 0 coincide, so a byte-per-value
/// export regression would survive it. This pins the packing with <c>true</c> values at index >= 1 (a
/// bit offset within the first byte) and >= 8 (a second-byte offset), interleaved with nulls, across a
/// full <c>FromArrow -&gt; ToArrow -&gt; FromArrow</c> cycle so the re-import reads the exact bit pattern.
/// </summary>
public class ArrowBatchConverterBooleanTests
{
    [Fact]
    public void RoundTrip_BooleanTrueAtHighIndices_PreservesExactBitPattern()
    {
        const int length = 12;
        static bool IsTrue(int i) => i is 1 or 9 or 11; // true past bit 0 and past the first byte
        static bool IsNullRow(int i) => i is 3 or 7;

        var builder = new BooleanArray.Builder();
        for (int i = 0; i < length; i++)
        {
            _ = IsNullRow(i) ? builder.AppendNull() : builder.Append(IsTrue(i));
        }

        BooleanArray source = builder.Build();

        using RecordBatch rb1 = ArrowConverterTestSupport.RecordBatchOf(("b", source, true));
        using ArrowColumnBatch import1 = ArrowBatchConverter.FromArrow(rb1);
        using RecordBatch rb2 = ArrowBatchConverter.ToArrow(import1);
        using ArrowColumnBatch import2 = ArrowBatchConverter.FromArrow(rb2);

        ColumnVector column = import2.Column(0);
        Assert.Equal(length, column.Length);
        Assert.Equal(2, column.NullCount);
        for (int i = 0; i < length; i++)
        {
            if (IsNullRow(i))
            {
                Assert.True(column.IsNull(i));
                continue;
            }

            Assert.False(column.IsNull(i));
            Assert.Equal(IsTrue(i), column.GetValue<bool>(i));
        }
    }
}
