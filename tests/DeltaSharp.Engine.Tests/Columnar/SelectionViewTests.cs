using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers STORY-02.1.2 (#134): selection-vector-aware, zero-copy batch/vector views — selected
/// cardinality without copying buffers (AC1), composition of nested selections (AC2), deterministic
/// enumeration over empty/all/partial selections (AC3), and validity through the selection (AC4).
/// </summary>
public class SelectionViewTests
{
    private static MutableColumnVector BuildInts(int count)
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, count);
        for (int i = 0; i < count; i++)
        {
            v.AppendValue(i * 10);
        }

        return v;
    }

    private static int[] RandomSelection(Random rng, int domain, int take)
    {
        var picks = new int[take];
        for (int i = 0; i < take; i++)
        {
            picks[i] = rng.Next(domain);
        }

        return picks;
    }

    // ---- AC1: selected view has selected cardinality, shares (does not copy) physical buffers ----

    [Fact]
    public void Select_LogicalRowCount_EqualsSelectedCardinality()
    {
        MutableColumnVector v = BuildInts(5);
        ColumnVector view = v.Select(new SelectionVector(new[] { 4, 2, 0 }));

        Assert.Equal(3, view.Length); // selected cardinality, not the parent's 5
        Assert.Equal(0, view.Offset); // logical row 0 re-based to the first selected row
        Assert.Equal(40, view.GetValue<int>(0));
        Assert.Equal(20, view.GetValue<int>(1));
        Assert.Equal(0, view.GetValue<int>(2));
    }

    [Fact]
    public void Select_DoesNotCopyValueBuffers_AllocationIsIndependentOfVectorSize()
    {
        // Zero-copy proof: selecting over a tiny vs. a large parent allocates the same small amount
        // (the view object + index array), not bytes proportional to the parent's value buffers.
        ColumnVector small = BuildInts(8).Select(SelectionVector.Range(8)); // warm up the JIT path
        Assert.Equal(8, small.Length);

        MutableColumnVector big = BuildInts(100_000);
        var selection = new SelectionVector(new[] { 1, 2, 3, 4 });
        long before = GC.GetAllocatedBytesForCurrentThread();
        ColumnVector view = big.Select(selection);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(4, view.Length);
        Assert.True(after - before < 4_000, $"Select allocated {after - before} bytes (must not copy the 100k value buffer)");
    }

    [Fact]
    public void Batch_WithSelection_SharesColumnsAndExposesSelectedCardinality()
    {
        MutableColumnVector ids = BuildInts(5);
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance) });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { ids }, rowCount: 5);

        ColumnBatch selected = batch.WithSelection(new SelectionVector(new[] { 3, 1 }));

        Assert.Equal(2, selected.LogicalRowCount);
        Assert.Equal(5, selected.RowCount); // physical rows unchanged: no copy
        ColumnVector col = selected.SelectedColumn(0);
        Assert.Equal(2, col.Length);
        Assert.Equal(30, col.GetValue<int>(0));
        Assert.Equal(10, col.GetValue<int>(1));
    }

    // ---- AC2: composing nested selections == applying the selections in sequence ----

    [Fact]
    public void Select_OverSelected_ComposesToSameRowsAsAppliedInSequence()
    {
        MutableColumnVector v = BuildInts(6); // values 0,10,20,30,40,50
        var first = new SelectionVector(new[] { 5, 3, 1, 0 }); // -> 50,30,10,0
        var second = new SelectionVector(new[] { 2, 0 }); // index into first -> 10, 50

        ColumnVector composed = v.Select(first).Select(second);
        ColumnVector sequence = v.Select(first.Compose(second));

        Assert.Equal(2, composed.Length);
        Assert.Equal(10, composed.GetValue<int>(0));
        Assert.Equal(50, composed.GetValue<int>(1));
        Assert.Equal(sequence.GetValue<int>(0), composed.GetValue<int>(0));
        Assert.Equal(sequence.GetValue<int>(1), composed.GetValue<int>(1));
    }

    [Fact]
    public void Batch_WithSelection_Twice_ComposesAgainstLogicalRows()
    {
        MutableColumnVector ids = BuildInts(6);
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance) });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { ids }, rowCount: 6);

        ColumnBatch twice = batch
            .WithSelection(new SelectionVector(new[] { 5, 3, 1, 0 }))
            .WithSelection(new SelectionVector(new[] { 2, 0 }));

        Assert.Equal(2, twice.LogicalRowCount);
        ColumnVector col = twice.SelectedColumn(0);
        Assert.Equal(10, col.GetValue<int>(0));
        Assert.Equal(50, col.GetValue<int>(1));
    }

    // ---- AC3: empty / all-selected / partial enumerate deterministically with no out-of-range ----

    [Fact]
    public void EmptySelection_HasZeroRows_AndEnumeratesNothing()
    {
        ColumnVector view = BuildInts(4).Select(new SelectionVector(ReadOnlySpan<int>.Empty));
        Assert.Equal(0, view.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.GetValue<int>(0));
    }

    [Fact]
    public void AllSelected_PreservesOrder_PartialSubsetsInSelectionOrder()
    {
        MutableColumnVector v = BuildInts(4); // 0,10,20,30
        ColumnVector all = v.Select(SelectionVector.Range(4));
        ColumnVector partial = all.Select(new SelectionVector(new[] { 3, 0, 2 }));

        Assert.Equal(4, all.Length);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(i * 10, all.GetValue<int>(i)); // identity, deterministic order
        }

        Assert.Equal(new[] { 30, 0, 20 }, new[] { partial.GetValue<int>(0), partial.GetValue<int>(1), partial.GetValue<int>(2) });
        Assert.Throws<ArgumentOutOfRangeException>(() => partial.GetValue<int>(3));
    }

    [Fact]
    public void SelectedView_HasNoContiguousSpan_KernelsGatherPerRow()
    {
        ColumnVector view = BuildInts(4).Select(new SelectionVector(new[] { 2, 0 }));
        Assert.Throws<InvalidOperationException>(() => view.GetValues<int>().Length);
    }

    // ---- AC4: validity for a selected row maps to the parent bitmap at the selected physical index ----

    [Fact]
    public void NullableSelection_ValidityMatchesParentAtSelectedPhysicalIndex()
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, 5);
        v.AppendValue(0);
        v.AppendNull(); // physical 1
        v.AppendValue(20);
        v.AppendNull(); // physical 3
        v.AppendValue(40);

        ColumnVector view = v.Select(new SelectionVector(new[] { 1, 4, 3 })); // null, valid, null
        Assert.Equal(2, view.NullCount);
        Assert.True(view.HasNulls);
        Assert.True(view.IsNull(0)); // physical 1
        Assert.False(view.IsNull(1)); // physical 4
        Assert.True(view.IsNull(2)); // physical 3
        Assert.Equal(40, view.GetValue<int>(1));
    }

    [Fact]
    public void VariableWidthSelection_GathersBytesAndNulls()
    {
        MutableColumnVector v = ColumnVectors.Create(StringType.Instance, 3);
        v.AppendBytes(Encoding.UTF8.GetBytes("a"));
        v.AppendNull();
        v.AppendBytes(Encoding.UTF8.GetBytes("ccc"));

        ColumnVector view = v.Select(new SelectionVector(new[] { 2, 1, 0 }));
        Assert.Equal("ccc", Encoding.UTF8.GetString(view.GetBytes(0)));
        Assert.True(view.IsNull(1));
        Assert.True(view.GetBytes(1).IsEmpty);
        Assert.Equal("a", Encoding.UTF8.GetString(view.GetBytes(2)));
    }

    [Fact]
    public void SelectedView_Slice_StaysWithinSelection()
    {
        ColumnVector view = BuildInts(6).Select(new SelectionVector(new[] { 5, 4, 3, 2 })); // 50,40,30,20
        ColumnVector window = view.Slice(1, 2);
        Assert.Equal(2, window.Length);
        Assert.Equal(40, window.GetValue<int>(0));
        Assert.Equal(30, window.GetValue<int>(1));
    }

    [Fact]
    public void Select_SealsOwner_AndOutOfRangeIndexThrows()
    {
        MutableColumnVector v = BuildInts(3);
        _ = v.Select(new SelectionVector(new[] { 0, 2 }));

        Assert.Throws<InvalidOperationException>(() => v.AppendValue(99)); // sealed against mutation
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildInts(2).Select(new SelectionVector(new[] { 0, 2 })));
    }

    [Fact]
    public void SelectedColumn_WithNoSelection_ReturnsColumnUnchanged()
    {
        MutableColumnVector ids = BuildInts(2);
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance) });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { ids }, rowCount: 2);
        Assert.Same(ids, batch.SelectedColumn(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(99)]
    public void Composed_View_MatchesManualGather_Randomized(int seed)
    {
        // Parity oracle: a composed selection over a vector must equal gathering the same physical
        // rows by hand, for arbitrary partial selections (AC2 + AC3, no out-of-range access).
        var rng = new Random(seed);
        int parent = 64;
        MutableColumnVector v = BuildInts(parent);

        int[] first = RandomSelection(rng, parent, rng.Next(parent + 1));
        int[] second = RandomSelection(rng, first.Length, rng.Next(first.Length + 1));

        ColumnVector view = v.Select(new SelectionVector(first)).Select(new SelectionVector(second));

        Assert.Equal(second.Length, view.Length);
        for (int p = 0; p < second.Length; p++)
        {
            int physical = first[second[p]];
            Assert.Equal(physical * 10, view.GetValue<int>(p)); // deterministic, in selection order
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(101)]
    public void Composed_NullableVarWidth_MatchesManualGather_Randomized(int seed)
    {
        // Parity oracle for the nullable + variable-width path: a composed selection over a string
        // vector with ~30% nulls must gather the same bytes and the same validity as resolving the
        // two selections by hand — exercising the parent-has-nulls IsNull gather (AC2 + AC4).
        var rng = new Random(seed);
        const int parent = 48;
        MutableColumnVector v = ColumnVectors.Create(StringType.Instance, parent);
        var expected = new string?[parent];
        for (int i = 0; i < parent; i++)
        {
            if (rng.Next(10) < 3)
            {
                v.AppendNull();
            }
            else
            {
                string s = new((char)('a' + (i % 26)), 1 + (i % 4));
                expected[i] = s;
                v.AppendBytes(Encoding.UTF8.GetBytes(s));
            }
        }

        int[] first = RandomSelection(rng, parent, rng.Next(parent + 1));
        int[] second = RandomSelection(rng, first.Length, rng.Next(first.Length + 1));
        ColumnVector view = v.Select(new SelectionVector(first)).Select(new SelectionVector(second));

        int expectedNulls = 0;
        Assert.Equal(second.Length, view.Length);
        for (int p = 0; p < second.Length; p++)
        {
            int physical = first[second[p]];
            if (expected[physical] is null)
            {
                Assert.True(view.IsNull(p));
                Assert.True(view.GetBytes(p).IsEmpty); // null gathers to empty, never the wrong row
                expectedNulls++;
            }
            else
            {
                Assert.False(view.IsNull(p));
                Assert.Equal(expected[physical], Encoding.UTF8.GetString(view.GetBytes(p)));
            }
        }

        Assert.Equal(expectedNulls, view.NullCount); // counted once at construction, multiset-aware
    }

    [Theory]
    [InlineData(2)]
    [InlineData(57)]
    public void DuplicateHeavySelection_KeepsMultisetAndCountsRepeatedNulls(int seed)
    {
        // Selections need not be distinct: the same physical row may repeat many times. Count is a
        // multiset cardinality, each duplicate of a null row counts, and the no-null fast path still
        // matches the nullable path for the same picks.
        var rng = new Random(seed);
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, 4);
        v.AppendValue(0);
        v.AppendNull(); // physical 1 is null
        v.AppendValue(20);
        v.AppendValue(30);

        int[] picks = new int[24];
        for (int i = 0; i < picks.Length; i++)
        {
            picks[i] = i % 4 == 0 ? 1 : rng.Next(4); // physical 1 (null) appears at least every 4th
        }

        ColumnVector view = v.Select(new SelectionVector(picks));
        Assert.Equal(picks.Length, view.Length); // duplicates preserved, not deduped
        int expectedNulls = 0;
        for (int i = 0; i < picks.Length; i++)
        {
            bool isNull = picks[i] == 1;
            Assert.Equal(isNull, view.IsNull(i));
            if (isNull)
            {
                expectedNulls++;
            }
            else
            {
                Assert.Equal(picks[i] * 10, view.GetValue<int>(i));
            }
        }

        Assert.Equal(expectedNulls, view.NullCount); // each duplicate null counted

        // No-null parent over the same duplicate picks: fast path yields zero nulls, same values.
        MutableColumnVector dense = BuildInts(4);
        ColumnVector denseView = dense.Select(new SelectionVector(picks));
        Assert.Equal(0, denseView.NullCount);
        Assert.False(denseView.HasNulls);
        Assert.Equal(picks[0] * 10, denseView.GetValue<int>(0));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(255)]
    public void SliceAfterSelect_StaysZeroCopyAndMatchesGather_Randomized(int seed)
    {
        // A slice of a selected view is a sub-range of the same selection over the same parent
        // buffers: values, validity, and null count must equal gathering the windowed picks by hand.
        var rng = new Random(seed);
        const int parent = 50;
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, parent);
        var isNull = new bool[parent];
        for (int i = 0; i < parent; i++)
        {
            if (rng.Next(5) == 0)
            {
                v.AppendNull();
                isNull[i] = true;
            }
            else
            {
                v.AppendValue(i * 10);
            }
        }

        int[] picks = RandomSelection(rng, parent, rng.Next(1, parent + 1));
        ColumnVector selected = v.Select(new SelectionVector(picks));
        int offset = rng.Next(picks.Length);
        int length = rng.Next(picks.Length - offset + 1);
        ColumnVector window = selected.Slice(offset, length);

        Assert.Equal(length, window.Length);
        int windowNulls = 0;
        for (int i = 0; i < length; i++)
        {
            int physical = picks[offset + i];
            Assert.Equal(isNull[physical], window.IsNull(i));
            if (isNull[physical])
            {
                windowNulls++;
            }
            else
            {
                Assert.Equal(physical * 10, window.GetValue<int>(i));
            }
        }

        Assert.Equal(windowNulls, window.NullCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => window.Slice(0, length + 1)); // can't widen past the view
    }
}
