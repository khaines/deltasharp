using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

public class ListColumnVectorTests
{
    private static readonly ArrayType IntList = new(IntegerType.Instance);

    private static MutableColumnVector Ints(params int[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, Math.Max(values.Length, 1));
        foreach (int value in values)
        {
            v.AppendValue(value);
        }

        return v;
    }

    // A 4-row list backed by elements [10,20,30,40,50]:
    //   row 0 -> [10,20]   (offsets 0..2)
    //   row 1 -> []        (offsets 2..2, EMPTY list)
    //   row 2 -> [30,40,50](offsets 2..5)
    //   row 3 -> []        (offsets 5..5, marked NULL)
    private static ListColumnVector Sample() =>
        new(IntList, Ints(10, 20, 30, 40, 50), new[] { 0, 2, 2, 5, 5 }, nulls: new[] { false, false, false, true });

    [Fact]
    public void FromChildAndOffsets_ExposesPerRowElementsAndOffsets()
    {
        ListColumnVector list = Sample();

        Assert.Equal(IntList, list.Type);
        Assert.Equal(4, list.Length);
        Assert.Equal(0, list.Offset);

        // Offsets correctness expressed through per-row element lengths (mutation-sensitive: any
        // off-by-one in the offsets buffer changes these).
        Assert.Equal(2, list.ElementLength(0));
        Assert.Equal(0, list.ElementLength(1));
        Assert.Equal(3, list.ElementLength(2));
        Assert.Equal(0, list.ElementLength(3));

        // Per-row element values.
        ColumnVector row0 = list.ElementsAt(0);
        Assert.Equal(2, row0.Length);
        Assert.Equal(10, row0.GetValue<int>(0));
        Assert.Equal(20, row0.GetValue<int>(1));

        ColumnVector row2 = list.ElementsAt(2);
        Assert.Equal(3, row2.Length);
        Assert.Equal(30, row2.GetValue<int>(0));
        Assert.Equal(50, row2.GetValue<int>(2));

        // The flattened element child spans every element.
        Assert.Equal(5, list.Elements.Length);
        Assert.Equal(30, list.Elements.GetValue<int>(2));
    }

    [Fact]
    public void NullList_IsDistinctFromEmptyList()
    {
        ListColumnVector list = Sample();

        Assert.True(list.HasNulls);
        Assert.Equal(1, list.NullCount);

        // Both row 1 and row 3 have zero elements, but only row 3 is null.
        Assert.Equal(0, list.ElementLength(1));
        Assert.Equal(0, list.ElementLength(3));
        Assert.False(list.IsNull(1)); // empty list
        Assert.True(list.IsNull(3)); // null list

        Assert.Equal(0, list.ElementsAt(1).Length); // empty list has no elements
        Assert.Equal(0, list.ElementsAt(3).Length); // null list has no elements
    }

    [Fact]
    public void Builder_AppendsElementsThenClosesRows()
    {
        var list = new ListColumnVector(IntList, capacity: 4);
        var elements = (MutableColumnVector)list.Elements;

        elements.AppendValue(10);
        elements.AppendValue(20);
        list.EndList(); // row 0 -> [10,20]

        list.EndList(); // row 1 -> [] (no elements appended: empty list)

        elements.AppendValue(30);
        elements.AppendValue(40);
        elements.AppendValue(50);
        list.EndList(); // row 2 -> [30,40,50]

        list.AppendNull(); // row 3 -> null list

        Assert.Equal(4, list.Length);
        Assert.Equal(1, list.NullCount);
        Assert.Equal(2, list.ElementLength(0));
        Assert.Equal(0, list.ElementLength(1));
        Assert.Equal(3, list.ElementLength(2));
        Assert.False(list.IsNull(1));
        Assert.True(list.IsNull(3));
        Assert.Equal(50, list.ElementsAt(2).GetValue<int>(2));
    }

    [Fact]
    public void Slice_ReBasesRowsElementsAndValidity()
    {
        ListColumnVector list = Sample();
        ColumnVector slice = list.Slice(1, 3); // parent rows 1,2,3 -> logical 0,1,2
        var sl = Assert.IsType<ListColumnVector>(slice);

        Assert.Equal(3, sl.Length);
        Assert.Equal(1, sl.Offset);
        Assert.Equal(1, sl.NullCount);

        Assert.Equal(0, sl.ElementLength(0)); // parent row 1 (empty)
        Assert.Equal(3, sl.ElementLength(1)); // parent row 2
        Assert.Equal(0, sl.ElementLength(2)); // parent row 3 (null)

        Assert.False(sl.IsNull(0)); // empty, not null
        Assert.True(sl.IsNull(2)); // null

        // Re-basing works: slice row 1's elements are the parent row 2's elements.
        ColumnVector elems = sl.ElementsAt(1);
        Assert.Equal(3, elems.Length);
        Assert.Equal(30, elems.GetValue<int>(0));
        Assert.Equal(40, elems.GetValue<int>(1));
        Assert.Equal(50, elems.GetValue<int>(2));
    }

    [Fact]
    public void Select_ThrowsNotSupportedNamingTheList()
    {
        ListColumnVector list = Sample();
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => list.Select(new SelectionVector(new[] { 2, 0 })));
        Assert.Contains("list", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScalarAccessors_AreUnavailable()
    {
        ListColumnVector list = Sample();
        Assert.Throws<InvalidOperationException>(() => list.GetValues<int>().Length);
        Assert.Throws<InvalidOperationException>(() => list.GetBytes(0));

        var builder = new ListColumnVector(IntList, capacity: 1);
        Assert.Throws<InvalidOperationException>(() => builder.AppendValue(1));
        Assert.Throws<InvalidOperationException>(() => builder.AppendBytes("x"u8));
    }

    [Fact]
    public void FromChildAndOffsets_RejectsInconsistentInputs()
    {
        // Non-monotonic offsets.
        Assert.Throws<ArgumentException>(() =>
            new ListColumnVector(IntList, Ints(1, 2, 3), new[] { 0, 2, 1 }));

        // Offsets exceed the element count.
        Assert.Throws<ArgumentException>(() =>
            new ListColumnVector(IntList, Ints(1, 2), new[] { 0, 3 }));

        // Element type mismatch (declared int, supplied string).
        MutableColumnVector strings = ColumnVectors.Create(StringType.Instance, 1);
        strings.AppendBytes("a"u8);
        Assert.Throws<ArgumentException>(() =>
            new ListColumnVector(IntList, strings, new[] { 0, 1 }));

        // Empty offsets buffer.
        Assert.Throws<ArgumentException>(() =>
            new ListColumnVector(IntList, Ints(1), ReadOnlySpan<int>.Empty));
    }

    [Fact]
    public void PerRowAccessors_RejectOutOfRange()
    {
        ListColumnVector list = Sample();
        Assert.Throws<ArgumentOutOfRangeException>(() => list.ElementLength(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.ElementsAt(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.IsNull(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Slice(2, 3));
    }
}
