using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

public class SelectionVectorTests
{
    [Fact]
    public void SelectionVector_ExposesCountIndicesAndPositions()
    {
        var selection = new SelectionVector(new[] { 3, 1, 4 });

        Assert.Equal(3, selection.Count);
        Assert.Equal(3, selection[0]);
        Assert.Equal(4, selection[2]);
        Assert.True(selection.Indices.SequenceEqual(new[] { 3, 1, 4 }));
    }

    [Fact]
    public void Range_ProducesIdentitySelection()
    {
        SelectionVector selection = SelectionVector.Range(4);
        Assert.Equal(4, selection.Count);
        Assert.True(selection.Indices.SequenceEqual(new[] { 0, 1, 2, 3 }));
    }

    [Fact]
    public void Compose_ResolvesOuterPositionsThroughBaseSelection()
    {
        var baseSelection = new SelectionVector(new[] { 5, 3, 1 }); // logical 0,1,2 -> physical 5,3,1
        SelectionVector composed = baseSelection.Compose(new SelectionVector(new[] { 2, 0 }));

        Assert.True(composed.Indices.SequenceEqual(new[] { 1, 5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => baseSelection.Compose(new SelectionVector(new[] { 3 })));
    }

    [Fact]
    public void SelectionVector_RejectsNegativeIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SelectionVector(new[] { 0, -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => SelectionVector.Range(-1));
    }

    [Fact]
    public void Batch_WithSelection_IsSelectionAware()
    {
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance) });
        MutableColumnVector ids = ColumnVectors.Create(IntegerType.Instance, 5);
        for (int i = 0; i < 5; i++)
        {
            ids.AppendValue(i * 10);
        }

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { ids }, rowCount: 5);
        var selection = new SelectionVector(new[] { 4, 2, 0 });
        ColumnBatch selected = batch.WithSelection(selection);

        Assert.Same(selection, selected.Selection);
        Assert.Equal(3, selected.LogicalRowCount); // selected cardinality, not RowCount
        Assert.Equal(5, selected.RowCount); // physical rows unchanged (no copy)

        // Resolve a selected logical row to the underlying physical value via the selection.
        int physical = selected.Selection![0];
        Assert.Equal(40, selected.Column(0).GetValue<int>(physical));
    }

    [Fact]
    public void WithSelection_RejectsOutOfRangeIndex()
    {
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance) });
        MutableColumnVector ids = ColumnVectors.Create(IntegerType.Instance, 2);
        ids.AppendValue(1);
        ids.AppendValue(2);
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { ids }, rowCount: 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => batch.WithSelection(new SelectionVector(new[] { 0, 2 })));
    }
}
