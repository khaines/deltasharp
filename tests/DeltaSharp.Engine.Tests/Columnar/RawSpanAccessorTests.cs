using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// The raw element/entry span accessors (#841 §2.3) that the Parquet nested-write shredder uses to derive
/// element counts.
/// </summary>
/// <remarks>
/// These accessors exist because a Dremel shredder must know a row's PHYSICAL element extent even for rows
/// the validity mask hides: a null container still occupies an offsets pair, and a sliced vector's offsets
/// are absolute while its <c>Elements</c>/<c>Keys</c>/<c>Values</c> views are already rebased to the slice.
/// Getting either rebasing wrong silently shifts every subsequent row's values, so the contract is pinned
/// here rather than only indirectly through the storage round trips.
/// </remarks>
public class RawSpanAccessorTests
{
    private static readonly ArrayType IntList = new(IntegerType.Instance);

    private static readonly MapType StringIntMap = new(StringType.Instance, IntegerType.Instance);

    private static MutableColumnVector Ints(params int[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, Math.Max(values.Length, 1));
        foreach (int value in values)
        {
            v.AppendValue(value);
        }

        return v;
    }

    private static MutableColumnVector Strings(params string[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(StringType.Instance, Math.Max(values.Length, 1));
        foreach (string value in values)
        {
            v.AppendBytes(System.Text.Encoding.UTF8.GetBytes(value));
        }

        return v;
    }

    // row 0 -> [10,20]  row 1 -> [] (EMPTY)  row 2 -> [30,40,50]  row 3 -> [] but NULL
    private static ListColumnVector SampleList() =>
        new(IntList, Ints(10, 20, 30, 40, 50), new[] { 0, 2, 2, 5, 5 }, nulls: new[] { false, false, false, true });

    // row 0 -> {a:1}  row 1 -> {} (EMPTY)  row 2 -> {b:2, c:3}  row 3 -> NULL, but physically retains one entry
    private static MapColumnVector SampleMap() =>
        new(
            StringIntMap, Strings("a", "b", "c", "d"), Ints(1, 2, 3, 4), new[] { 0, 1, 1, 3, 4 },
            nulls: new[] { false, false, false, true });

    [Fact]
    public void RawElementSpan_ReportsTheOffsetsPairForEveryRowShape()
    {
        ListColumnVector vector = SampleList();

        Assert.Equal((0, 2), vector.RawElementSpan(0));
        Assert.Equal((2, 0), vector.RawElementSpan(1));
        Assert.Equal((2, 3), vector.RawElementSpan(2));
    }

    [Fact]
    public void RawElementSpan_IsUnmaskedByValidity()
    {
        // A null row's physically retained span is reported as-is: the shredder needs the extent to skip the
        // right number of elements, and MUST NOT be told a null row is empty (they are different Dremel
        // encodings).
        var vector = new ListColumnVector(
            IntList, Ints(10, 20, 30), new[] { 0, 1, 3, 3 }, nulls: new[] { false, true, false });

        Assert.True(vector.IsNull(1));
        Assert.Equal((1, 2), vector.RawElementSpan(1));
    }

    [Fact]
    public void RawElementSpan_RebasesTheIndexAndTheStartForASlice()
    {
        // Rows 2..3 of the sample: the slice's own Elements view starts at physical element 2, so row 0 of
        // the slice must report Start 0 — an absolute 2 would double-count the slice's element base.
        var sliced = (ListColumnVector)SampleList().Slice(2, 2);

        Assert.Equal(2, sliced.Length);
        Assert.Equal((0, 3), sliced.RawElementSpan(0));
        Assert.Equal((3, 0), sliced.RawElementSpan(1));

        ColumnVector elements = sliced.Elements;
        (int start, int length) = sliced.RawElementSpan(0);
        for (int i = 0; i < length; i++)
        {
            Assert.Equal(30 + (i * 10), elements.GetValue<int>(start + i));
        }
    }

    [Fact]
    public void RawElementSpan_RejectsAnOutOfRangeIndex()
    {
        var sliced = (ListColumnVector)SampleList().Slice(1, 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => sliced.RawElementSpan(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => sliced.RawElementSpan(-1));
    }

    [Fact]
    public void RawEntrySpan_ReportsTheOffsetsPairForEveryRowShape()
    {
        MapColumnVector vector = SampleMap();

        Assert.Equal((0, 1), vector.RawEntrySpan(0));
        Assert.Equal((1, 0), vector.RawEntrySpan(1));
        Assert.Equal((1, 2), vector.RawEntrySpan(2));
        Assert.True(vector.IsNull(3));
        Assert.Equal((3, 1), vector.RawEntrySpan(3));
    }

    [Fact]
    public void RawEntrySpan_RebasesTheIndexAndTheStartForASlice()
    {
        var sliced = (MapColumnVector)SampleMap().Slice(2, 2);

        Assert.Equal((0, 2), sliced.RawEntrySpan(0));
        Assert.Equal((2, 1), sliced.RawEntrySpan(1));

        (int start, int length) = sliced.RawEntrySpan(0);
        Assert.Equal(2, length);
        Assert.Equal("b", System.Text.Encoding.UTF8.GetString(sliced.Keys.GetBytes(start)));
        Assert.Equal(3, sliced.Values.GetValue<int>(start + 1));
    }

    [Fact]
    public void RawEntrySpan_RejectsAnOutOfRangeIndex()
    {
        MapColumnVector vector = SampleMap();

        Assert.Throws<ArgumentOutOfRangeException>(() => vector.RawEntrySpan(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => vector.RawEntrySpan(-1));
    }

    [Fact]
    public void RawSpans_AgreeWithTheSlicedElementViewsOnALiveBuilder()
    {
        // A mutable list vector's Elements view can carry an uncommitted tail, which is exactly why the
        // shredder derives element COUNTS from the raw offsets and never from Elements.Length.
        var builder = new ListColumnVector(IntList, capacity: 4);
        MutableColumnVector elements = (MutableColumnVector)builder.Elements;
        elements.AppendValue(1);
        elements.AppendValue(2);
        builder.EndList();
        builder.AppendNull();
        elements.AppendValue(3);
        builder.EndList();

        Assert.Equal((0, 2), builder.RawElementSpan(0));
        Assert.Equal((2, 0), builder.RawElementSpan(1));
        Assert.Equal((2, 1), builder.RawElementSpan(2));

        int total = 0;
        for (int i = 0; i < builder.Length; i++)
        {
            total += builder.RawElementSpan(i).Length;
        }

        Assert.Equal(3, total);
    }
}
