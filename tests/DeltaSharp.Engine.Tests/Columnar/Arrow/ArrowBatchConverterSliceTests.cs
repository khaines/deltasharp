using Apache.Arrow;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Columnar.Arrow;
using Xunit;
using Decimal128Type = Apache.Arrow.Types.Decimal128Type;

namespace DeltaSharp.Engine.Tests.Columnar.Arrow;

/// <summary>
/// AC3 (STORY-02.2.2, #136): a sliced Arrow array imports as a DeltaSharp vector with its logical
/// row order intact. Zero-copy columns (primitive, string, nested) also preserve the physical
/// <see cref="ColumnVector.Offset"/>; the two materialized columns (boolean, decimal) preserve
/// logical order and validity but reset the offset to <c>0</c>, since materializing copies the
/// logical rows into a fresh buffer — the documented layout-mismatch caveat.
/// </summary>
public class ArrowBatchConverterSliceTests
{
    [Fact]
    public void Import_SlicedInt32_PreservesLogicalOrderAndOffset()
    {
        Int32Array full = new Int32Array.Builder().Append(10).Append(20).AppendNull().Append(40).Append(50).Build();
        var sliced = (Int32Array)full.Slice(2, 3); // logical [null, 40, 50], Arrow offset 2

        WithImportedColumn(sliced, column =>
        {
            Assert.Equal(3, column.Length);
            Assert.Equal(2, column.Offset); // zero-copy: physical offset preserved
            Assert.True(column.IsNull(0));
            Assert.Equal(40, column.GetValue<int>(1));
            Assert.Equal(50, column.GetValue<int>(2));
            Assert.Equal(1, column.NullCount);
        });
    }

    [Fact]
    public void Import_SlicedString_PreservesLogicalOrderAndOffset()
    {
        StringArray full = new StringArray.Builder().Append("a").Append("bb").AppendNull().Append("dddd").Build();
        var sliced = (StringArray)full.Slice(1, 3); // logical ["bb", null, "dddd"], Arrow offset 1

        WithImportedColumn(sliced, column =>
        {
            Assert.Equal(3, column.Length);
            Assert.Equal(1, column.Offset); // zero-copy: physical offset preserved
            Assert.True(column.GetBytes(0).SequenceEqual("bb"u8));
            Assert.True(column.IsNull(1));
            Assert.True(column.GetBytes(2).SequenceEqual("dddd"u8));
        });
    }

    [Fact]
    public void Import_SlicedBoolean_PreservesLogicalOrder_ResetsOffset()
    {
        BooleanArray full = new BooleanArray.Builder().Append(true).AppendNull().Append(false).Append(true).Build();
        var sliced = (BooleanArray)full.Slice(1, 3); // logical [null, false, true]

        WithImportedColumn(sliced, column =>
        {
            Assert.Equal(3, column.Length);
            Assert.Equal(0, column.Offset); // materialized: offset resets (documented caveat)
            Assert.True(column.IsNull(0));
            Assert.False(column.GetValue<bool>(1));
            Assert.True(column.GetValue<bool>(2));
            Assert.Equal(1, column.NullCount);
        });
    }

    [Fact]
    public void Import_SlicedDecimal_PreservesLogicalOrder_ResetsOffset()
    {
        var builder = new Decimal128Array.Builder(new Decimal128Type(10, 2));
        Decimal128Array full = builder.Append(1.00m).Append(2.00m).AppendNull().Append(4.00m).Build();
        var sliced = (Decimal128Array)full.Slice(1, 3); // logical [2.00, null, 4.00]

        WithImportedColumn(sliced, column =>
        {
            Assert.Equal(3, column.Length);
            Assert.Equal(0, column.Offset); // materialized: offset resets (documented caveat)
            Assert.Equal(200L, column.GetValue<long>(0)); // 2.00 unscaled
            Assert.True(column.IsNull(1));
            Assert.Equal(400L, column.GetValue<long>(2)); // 4.00 unscaled
        });
    }

    [Fact]
    public void Import_SlicedStruct_PreservesLogicalOrderAndOffset()
    {
        StructArray full = ArrowConverterTestSupport.BuildStructArray(new[] { 10, 20, 30, 40 });
        var sliced = (StructArray)full.Slice(1, 3); // logical rows carrying child [20, 30, 40], offset 1

        using RecordBatch source = ArrowConverterTestSupport.RecordBatchOf(("s", sliced, true));
        using ArrowColumnBatch imported = ArrowBatchConverter.FromArrow(source);

        ColumnVector column = imported.Column(0);
        Assert.Equal(3, column.Length);
        Assert.Equal(1, column.Offset); // nested zero-copy pass-through: offset preserved

        // Export and read the child logically (Arrow offset-adjusts) to confirm row order is intact.
        using RecordBatch exported = ArrowBatchConverter.ToArrow(imported);
        StructArray exportedStruct = Assert.IsType<StructArray>(exported.Column(0));
        Assert.Equal(3, exportedStruct.Length);
        var child = (Int32Array)exportedStruct.Fields[0];
        Assert.Equal(20, child.GetValue(0)!.Value);
        Assert.Equal(30, child.GetValue(1)!.Value);
        Assert.Equal(40, child.GetValue(2)!.Value);
    }

    // Keeps the borrowed Arrow source alive for the duration of the assertions: a zero-copy column
    // points at the source's buffers, so reading it after the source is disposed would be a
    // use-after-free.
    private static void WithImportedColumn(IArrowArray array, Action<ColumnVector> assert)
    {
        using RecordBatch source = ArrowConverterTestSupport.RecordBatchOf(("c", array, true));
        using ArrowColumnBatch imported = ArrowBatchConverter.FromArrow(source);
        assert(imported.Column(0));
    }
}
