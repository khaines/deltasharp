using Apache.Arrow;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Columnar.Arrow;
using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar.Arrow;

/// <summary>
/// Export applies a batch's <see cref="ColumnBatch.Selection"/> (council columnar F3). A flat column
/// emits its logical rows in selection order; a selection layered over an opaque nested column is the
/// documented v1 limit and throws <see cref="System.NotSupportedException"/>. AC1/AC3 exercised slices
/// but never drove a selection through <see cref="ArrowBatchConverter.ToArrow"/>.
/// </summary>
public class ArrowBatchConverterSelectionTests
{
    [Fact]
    public void ToArrow_FlatColumnUnderSelection_EmitsRowsInSelectionOrder()
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, 3);
        v.AppendValue(10);
        v.AppendValue(20);
        v.AppendValue(30);
        var batch = new ManagedColumnBatch(
            new StructType(new[] { new StructField("i", IntegerType.Instance) }), new[] { v }, 3);

        // Reverse-and-drop selection: logical rows become physical rows 2 then 0.
        ColumnBatch selected = batch.WithSelection(new SelectionVector(new[] { 2, 0 }));

        using RecordBatch arrow = ArrowBatchConverter.ToArrow(selected);

        Assert.Equal(2, arrow.Length);
        Int32Array array = Assert.IsType<Int32Array>(arrow.Column(0));
        Assert.Equal(2, array.Length);
        Assert.Equal(30, array.GetValue(0)!.Value); // selection position 0 -> physical row 2
        Assert.Equal(10, array.GetValue(1)!.Value); // selection position 1 -> physical row 0
    }

    [Fact]
    public void ToArrow_NestedColumnUnderSelection_ThrowsNotSupported()
    {
        StructArray structArray = ArrowConverterTestSupport.BuildStructArray(new[] { 10, 20, 30 });
        using RecordBatch source = ArrowConverterTestSupport.RecordBatchOf(("s", structArray, true));
        using ArrowColumnBatch imported = ArrowBatchConverter.FromArrow(source);

        ColumnBatch selected = imported.WithSelection(new SelectionVector(new[] { 2, 0 }));

        // An opaque Arrow nested column can't be logically re-indexed in v1 (materialize the selection
        // first), so exporting a nested column under a selection is rejected rather than mis-emitted.
        Assert.Throws<NotSupportedException>(() => ArrowBatchConverter.ToArrow(selected));
    }
}
