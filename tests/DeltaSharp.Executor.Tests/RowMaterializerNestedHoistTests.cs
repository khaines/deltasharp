using System;
using System.Collections.Generic;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// #610 — the nested-column decode path (<see cref="RowMaterializer"/>) hoists per-row structural allocations
/// out of the per-row loop: struct child views are bound once per struct vector (<c>Child(i)</c> is a pure
/// function of the vector's window, so a sliced struct no longer re-slices O(rows × fields) times), and the
/// synthesized array/map <c>element/key/value</c> fields are pre-built once. These tests pin that the hoist
/// is <b>behaviour-preserving</b> on the exact case it optimizes — a <b>sliced</b> (offset ≠ 0) struct view,
/// nested-in-collection structs, and back-to-back batches with distinct struct vectors — and that a sliced
/// struct materialization no longer allocates per row per field.
/// </summary>
public sealed class RowMaterializerNestedHoistTests
{
    private static readonly StructType Point = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField("name", StringType.Instance, nullable: true),
    });

    private static IReadOnlyList<Row> Materialize(StructType schema, ColumnBatch batch) =>
        RowMaterializer.Materialize(
            new BatchResult(schema, new[] { batch }), maxRows: null, maxBytes: null, default);

    [Fact]
    public void Materialize_SlicedStructColumn_DecodesShiftedRowsFromHoistedChildren()
    {
        var schema = new StructType(new[] { new StructField("s", Point, nullable: true) });
        var rows = new[]
        {
            new Row(schema, new object?[] { new Row(Point, 10, "a") }),
            new Row(schema, new object?[] { new Row(Point, 20, "b") }),
            new Row(schema, new object?[] { new Row(Point, 30, "c") }),
            new Row(schema, new object?[] { new Row(Point, 40, "d") }),
        };

        // Slice(1, 2) yields a struct vector with offset 1 — the churny case: Child(i) previously re-sliced
        // each row. The hoisted read must still resolve field values at the correct logical (shifted) index.
        ColumnBatch sliced = LocalRelationBatches.Build(schema, rows)[0].Slice(1, 2);
        IReadOnlyList<Row> got = Materialize(schema, sliced);

        Assert.Equal(2, got.Count);
        Assert.Equal(20, Assert.IsType<Row>(got[0][0]).GetAs<int>("id"));
        Assert.Equal("b", Assert.IsType<Row>(got[0][0]).GetAs<string>("name"));
        Assert.Equal(30, Assert.IsType<Row>(got[1][0]).GetAs<int>("id"));
        Assert.Equal("c", Assert.IsType<Row>(got[1][0]).GetAs<string>("name"));
    }

    [Fact]
    public void Materialize_SlicedNestedStruct_DecodesDeepChildrenFromHoistedViews()
    {
        var inner = new StructType(new[] { new StructField("v", IntegerType.Instance, nullable: false) });
        var outer = new StructType(new[] { new StructField("inner", inner, nullable: true) });
        var schema = new StructType(new[] { new StructField("s", outer, nullable: true) });
        var rows = new[]
        {
            new Row(schema, new object?[] { new Row(outer, new object?[] { new Row(inner, 100) }) }),
            new Row(schema, new object?[] { new Row(outer, new object?[] { new Row(inner, 200) }) }),
            new Row(schema, new object?[] { new Row(outer, new object?[] { new Row(inner, 300) }) }),
        };

        // Two levels of struct on a sliced view: both the outer and inner readers hoist their child views;
        // the deep `s.inner.v` must still read at the shifted index.
        ColumnBatch sliced = LocalRelationBatches.Build(schema, rows)[0].Slice(1, 2);
        IReadOnlyList<Row> got = Materialize(schema, sliced);

        Assert.Equal(2, got.Count);
        Row outer0 = Assert.IsType<Row>(got[0][0]);
        Assert.Equal(200, Assert.IsType<Row>(outer0[0]).GetAs<int>("v"));
        Row outer1 = Assert.IsType<Row>(got[1][0]);
        Assert.Equal(300, Assert.IsType<Row>(outer1[0]).GetAs<int>("v"));
    }

    [Fact]
    public void Materialize_ListOfStructs_DecodesWithoutPerRowFieldSynthesis()
    {
        var elem = new StructType(new[]
        {
            new StructField("a", IntegerType.Instance, nullable: false),
            new StructField("b", StringType.Instance, nullable: true),
        });
        var schema = new StructType(new[]
        {
            new StructField("xs", new ArrayType(elem, containsNull: true), nullable: true),
        });
        var rows = new[]
        {
            new Row(schema, new object?[] { new object?[] { new Row(elem, 1, "x"), new Row(elem, 2, "y") } }),
            new Row(schema, new object?[] { new object?[] { new Row(elem, 3, "z"), null } }), // null element
        };

        IReadOnlyList<Row> got = RowMaterializer.Materialize(
            new BatchResult(schema, LocalRelationBatches.Build(schema, rows)), null, null, default);

        var list0 = Assert.IsType<object[]>(got[0][0]);
        Assert.Equal(1, Assert.IsType<Row>(list0[0]).GetAs<int>("a"));
        Assert.Equal("y", Assert.IsType<Row>(list0[1]).GetAs<string>("b"));

        var list1 = Assert.IsType<object[]>(got[1][0]);
        Assert.Equal(3, Assert.IsType<Row>(list1[0]).GetAs<int>("a"));
        Assert.Null(list1[1]); // the null element survives
    }

    [Fact]
    public void Materialize_MapWithStructValues_DecodesWithoutPerRowFieldSynthesis()
    {
        var valueType = new StructType(new[] { new StructField("n", IntegerType.Instance, nullable: false) });
        var schema = new StructType(new[]
        {
            new StructField(
                "m", new MapType(StringType.Instance, valueType, valueContainsNull: true), nullable: true),
        });
        var entries = new Dictionary<string, Row?> { ["k1"] = new Row(valueType, 7), ["k2"] = null };
        var rows = new[] { new Row(schema, new object?[] { entries }) };

        IReadOnlyList<Row> got = RowMaterializer.Materialize(
            new BatchResult(schema, LocalRelationBatches.Build(schema, rows)), null, null, default);

        var map = Assert.IsType<Dictionary<object, object?>>(got[0][0]);
        Assert.Equal(7, Assert.IsType<Row>(map["k1"]).GetAs<int>("n"));
        Assert.Null(map["k2"]); // the null value survives
    }

    [Fact]
    public void Materialize_ConsecutiveBatchesWithDistinctStructVectors_DecodeIndependently()
    {
        var schema = new StructType(new[] { new StructField("s", Point, nullable: true) });
        ColumnBatch batchA = LocalRelationBatches.Build(
            schema, new[] { new Row(schema, new object?[] { new Row(Point, 1, "a") }) })[0];
        ColumnBatch batchB = LocalRelationBatches.Build(
            schema, new[] { new Row(schema, new object?[] { new Row(Point, 2, "b") }) })[0];

        // Two batches carry two DISTINCT struct-vector instances; the per-vector memo must rebind on the
        // second (a reference-inequality miss), not serve batch A's hoisted children for batch B.
        IReadOnlyList<Row> got = RowMaterializer.Materialize(
            new BatchResult(schema, new[] { batchA, batchB }), null, null, default);

        Assert.Equal(2, got.Count);
        Assert.Equal(1, Assert.IsType<Row>(got[0][0]).GetAs<int>("id"));
        Assert.Equal("a", Assert.IsType<Row>(got[0][0]).GetAs<string>("name"));
        Assert.Equal(2, Assert.IsType<Row>(got[1][0]).GetAs<int>("id"));
        Assert.Equal("b", Assert.IsType<Row>(got[1][0]).GetAs<string>("name"));
    }

    [Fact]
    public void Materialize_SlicedStructWithNullField_PreservesNullThroughHoistedRead()
    {
        // The hoisted, sliced-view read must still honour a null STRUCT FIELD (a non-null struct row whose
        // field is null) — dropping the child null-gate would surface a default ("" / 0) instead of null.
        var schema = new StructType(new[] { new StructField("s", Point, nullable: true) });
        var rows = new[]
        {
            new Row(schema, new object?[] { new Row(Point, 10, "a") }),
            new Row(schema, new object?[] { new Row(Point, new object?[] { 20, null }) }), // null "name"
            new Row(schema, new object?[] { new Row(Point, 30, "c") }),
        };

        ColumnBatch sliced = LocalRelationBatches.Build(schema, rows)[0].Slice(1, 1); // just the null-field row
        IReadOnlyList<Row> got = Materialize(schema, sliced);

        Row s = Assert.IsType<Row>(Assert.Single(got)[0]);
        Assert.Equal(20, s.GetAs<int>("id"));
        Assert.True(s.IsNullAt(1)); // the null field survives, not surfaced as ""
    }

    [Fact]
    public void Materialize_SlicedListColumn_DecodesShiftedCells()
    {
        var schema = new StructType(new[]
        {
            new StructField("xs", new ArrayType(IntegerType.Instance, containsNull: true), nullable: true),
        });
        var rows = new[]
        {
            new Row(schema, new object?[] { new object?[] { 1, 2 } }),
            new Row(schema, new object?[] { new object?[] { 3, 4, 5 } }),
            new Row(schema, new object?[] { new object?[] { 6 } }),
        };

        // The LIST vector itself is sliced (offset 1): ElementsAt must resolve each cell's own offset window.
        ColumnBatch sliced = LocalRelationBatches.Build(schema, rows)[0].Slice(1, 2);
        IReadOnlyList<Row> got = Materialize(schema, sliced);

        Assert.Equal(2, got.Count);
        Assert.Equal(new object[] { 3, 4, 5 }, Assert.IsType<object[]>(got[0][0]));
        Assert.Equal(new object[] { 6 }, Assert.IsType<object[]>(got[1][0]));
    }

    [Fact]
    public void Materialize_SlicedView_EmptyAndNullArray_Decode()
    {
        var schema = new StructType(new[]
        {
            new StructField("xs", new ArrayType(IntegerType.Instance, containsNull: true), nullable: true),
        });
        var rows = new[]
        {
            new Row(schema, new object?[] { new object?[] { 1 } }),
            new Row(schema, new object?[] { Array.Empty<object?>() }), // empty array
            new Row(schema, new object?[] { null }),                   // null array
        };

        ColumnBatch sliced = LocalRelationBatches.Build(schema, rows)[0].Slice(1, 2); // empty + null cells
        IReadOnlyList<Row> got = Materialize(schema, sliced);

        Assert.Equal(2, got.Count);
        Assert.Empty(Assert.IsType<object[]>(got[0][0])); // empty array stays empty
        Assert.True(got[1].IsNullAt(0));                  // null array stays null
    }

    [Fact]
    public void Materialize_SlicedMapColumn_DecodesShiftedCells()
    {
        var schema = new StructType(new[]
        {
            new StructField(
                "m", new MapType(StringType.Instance, IntegerType.Instance, valueContainsNull: true), nullable: true),
        });
        var rows = new[]
        {
            new Row(schema, new object?[] { new Dictionary<string, int> { ["a"] = 1 } }),
            new Row(schema, new object?[] { new Dictionary<string, int> { ["b"] = 2, ["c"] = 3 } }),
        };

        ColumnBatch sliced = LocalRelationBatches.Build(schema, rows)[0].Slice(1, 1);
        IReadOnlyList<Row> got = Materialize(schema, sliced);

        var map = Assert.IsType<Dictionary<object, object?>>(Assert.Single(got)[0]);
        Assert.Equal(2, map.Count);
        Assert.Equal(2, map["b"]);
        Assert.Equal(3, map["c"]);
    }

    [Fact]
    public void Materialize_MapWithStructKey_DecodesStructKey()
    {
        var keyType = new StructType(new[] { new StructField("k", IntegerType.Instance, nullable: false) });
        var schema = new StructType(new[]
        {
            new StructField(
                "m", new MapType(keyType, StringType.Instance, valueContainsNull: false), nullable: true),
        });
        var entries = new Dictionary<Row, string> { [new Row(keyType, 9)] = "nine" };
        var rows = new[] { new Row(schema, new object?[] { entries }) };

        IReadOnlyList<Row> got = RowMaterializer.Materialize(
            new BatchResult(schema, LocalRelationBatches.Build(schema, rows)), null, null, default);

        var map = Assert.IsType<Dictionary<object, object?>>(got[0][0]);
        KeyValuePair<object, object?> entry = Assert.Single(map);
        Assert.Equal(9, Assert.IsType<Row>(entry.Key).GetAs<int>("k")); // the struct key decodes (map-key path)
        Assert.Equal("nine", entry.Value);
    }

    [Fact]
    public void Materialize_SlicedStruct_DoesNotAllocatePerRowPerField()
    {
        const int fieldCount = 8;
        const int rowCount = 1000;
        const int iterations = 10;

        var fields = new StructField[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            fields[i] = new StructField($"f{i}", IntegerType.Instance, nullable: false);
        }

        var inner = new StructType(fields);
        var schema = new StructType(new[] { new StructField("s", inner, nullable: false) });

        Row Make(int r)
        {
            var v = new object?[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                v[i] = (r * fieldCount) + i;
            }

            return new Row(schema, new object?[] { new Row(inner, v) });
        }

        // A WHOLE struct vector (offset 0, full length): Child(i) returns the raw child — zero slice churn.
        var wholeRows = new Row[rowCount];
        for (int r = 0; r < rowCount; r++)
        {
            wholeRows[r] = Make(r);
        }

        var wholeResult = new BatchResult(schema, new[] { LocalRelationBatches.Build(schema, wholeRows)[0] });

        // A SLICED struct vector (offset 1, same rowCount rows): Child(i) would re-slice per field per row
        // under the old decode. Both results materialize the SAME row count, so the per-row Row/boxed-value
        // allocations cancel in the delta, isolating the struct-child-view churn.
        var backingRows = new Row[rowCount + 1];
        for (int r = 0; r < rowCount + 1; r++)
        {
            backingRows[r] = Make(r);
        }

        var slicedResult = new BatchResult(
            schema, new[] { LocalRelationBatches.Build(schema, backingRows)[0].Slice(1, rowCount) });

        long Measure(BatchResult result)
        {
            // GetAllocatedBytesForCurrentThread counts only THIS thread's allocations, so the measured region
            // must stay single-threaded — Materialize is fully synchronous (no await/parallelism), which this
            // relies on (F2: a future async refactor would silently under-measure and blind this gate).
            RowMaterializer.Materialize(result, null, null, default); // warm up
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
            {
                _ = RowMaterializer.Materialize(result, null, null, default);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        long whole = Measure(wholeResult);
        long sliced = Measure(slicedResult);
        long delta = sliced - whole;

        // Hoisted, the sliced case adds only O(fieldCount) child slices per batch (per iteration), not
        // O(rowCount × fieldCount). The old per-row-per-field re-slice would add ~rowCount×fieldCount slice
        // objects per iteration; a fraction of that bound is a robust, machine-independent regression trip.
        // This deliberately uses a differential (sliced−whole cancels the identical Row/boxed-value baseline)
        // plus a wide ~50× margin INSTEAD of the house tight-bound + retry loop (SelectionBenchmarkTests): the
        // margin already absorbs any one-time JIT tier-up transient, so do not "tighten" the bound to need a retry.
        long oldChurnBytes = (long)rowCount * fieldCount * iterations * 24; // ≈ per-row-per-field slice objects
        long bound = oldChurnBytes / 8;
        Assert.True(
            delta < bound,
            $"sliced-vs-whole allocation delta was {delta} bytes (bound {bound}); a value this high indicates "
            + "the struct child views are being re-sliced per row per field (the #610 hoist regressed).");
    }
}
