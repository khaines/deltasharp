using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Execution;

/// <summary>
/// Exercises nested struct field access (#580): the interpreted evaluator for a
/// <see cref="StructFieldExpression"/> extracts a field from a struct column and applies the struct's
/// own validity — extracting a field of a null struct yields null (Spark semantics), even where the
/// field child holds a non-null slot at that row. Covers the zero-copy fast path (no null structs),
/// null-struct masking, null-field propagation, a non-primitive (string) field, multi-part chaining
/// (<c>s.a.b</c>), and end-to-end use inside a comparison (<c>s.f &gt; 0</c>).
/// </summary>
public class NestedFieldAccessEvaluatorTests
{
    private const string BackendName = "interpreted-vectorized";

    private static StructType Struct(params StructField[] fields) => new(fields);

    private static ColumnVector IntCol(params int?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.IntegerType, Math.Max(values.Length, 1));
        foreach (int? x in values)
        {
            if (x.HasValue)
            {
                v.AppendValue(x.Value);
            }
            else
            {
                v.AppendNull();
            }
        }

        return v;
    }

    private static ColumnVector StrCol(params string?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, Math.Max(values.Length, 1));
        foreach (string? x in values)
        {
            if (x is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendBytes(Encoding.UTF8.GetBytes(x));
            }
        }

        return v;
    }

    private static ColumnBatch Batch(StructType schema, ColumnVector column) =>
        new ManagedColumnBatch(schema, new[] { column }, column.Length);

    private static ColumnVector Eval(PhysicalExpression expression, StructType schema, ColumnBatch batch)
    {
        ExpressionEvaluator evaluator = ExpressionEvaluators.Build(expression, schema, BackendName, OperatorKind.Project);
        var ledger = new BatchEvaluationMemory(BoundedExecutionMemory.Unbounded);
        try
        {
            return evaluator.Evaluate(batch, ledger, CancellationToken.None);
        }
        finally
        {
            ledger.Release();
        }
    }

    private static int?[] Ints(ColumnVector v)
    {
        var result = new int?[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            result[i] = v.IsNull(i) ? null : v.GetValue<int>(i);
        }

        return result;
    }

    private static bool?[] Bools(ColumnVector v)
    {
        var result = new bool?[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            result[i] = v.IsNull(i) ? null : v.GetValue<bool>(i);
        }

        return result;
    }

    [Fact]
    public void NonNullableStruct_ExtractsIntField_FastPath()
    {
        StructType s = Struct(new StructField("f", DataTypes.IntegerType, nullable: false));
        StructType schema = Struct(new StructField("s", s, nullable: false));
        var column = new StructColumnVector(s, new[] { IntCol(10, 20, 30) });
        ColumnBatch batch = Batch(schema, column);

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: false), 0, DataTypes.IntegerType, false);
        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(DataTypes.IntegerType, result.Type);
        Assert.Equal(new int?[] { 10, 20, 30 }, Ints(result));
    }

    [Fact]
    public void NullStruct_MasksNonNullChildSlot_YieldsNull()
    {
        // The int child holds a non-null sentinel (999) at the null-struct row; the extractor must
        // return NULL there (struct validity is separate from the child's own validity), not 999.
        StructType s = Struct(new StructField("f", DataTypes.IntegerType, nullable: false));
        StructType schema = Struct(new StructField("s", s, nullable: true));
        var column = new StructColumnVector(
            s, new[] { IntCol(10, 999, 30) }, new[] { false, true, false });
        ColumnBatch batch = Batch(schema, column);

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: true), 0, DataTypes.IntegerType, true);
        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(new int?[] { 10, null, 30 }, Ints(result));
    }

    [Fact]
    public void NullField_WithinNonNullStruct_PropagatesNull()
    {
        StructType s = Struct(new StructField("f", DataTypes.IntegerType, nullable: true));
        StructType schema = Struct(new StructField("s", s, nullable: true));
        // Row 1: struct is null AND field is null; row 2: struct non-null but field null.
        var column = new StructColumnVector(
            s, new[] { IntCol(10, null, null) }, new[] { false, true, false });
        ColumnBatch batch = Batch(schema, column);

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: true), 0, DataTypes.IntegerType, true);
        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(new int?[] { 10, null, null }, Ints(result));
    }

    [Fact]
    public void ExtractsStringFieldAtSecondOrdinal()
    {
        StructType s = Struct(
            new StructField("f", DataTypes.IntegerType, nullable: false),
            new StructField("g", DataTypes.StringType, nullable: true));
        StructType schema = Struct(new StructField("s", s, nullable: true));
        var column = new StructColumnVector(
            s, new ColumnVector[] { IntCol(1, 2, 3), StrCol("a", "b", "c") }, new[] { false, true, false });
        ColumnBatch batch = Batch(schema, column);

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: true), 1, DataTypes.StringType, true);
        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(DataTypes.StringType, result.Type);
        Assert.Equal("a", Encoding.UTF8.GetString(result.GetBytes(0)));
        Assert.True(result.IsNull(1)); // null struct masks the non-null "b"
        Assert.Equal("c", Encoding.UTF8.GetString(result.GetBytes(2)));
    }

    [Fact]
    public void ChainedAccess_ResolvesLeaf_AndPropagatesNullsAtEveryLevel()
    {
        StructType inner = Struct(new StructField("b", DataTypes.IntegerType, nullable: false));
        StructType outer = Struct(new StructField("a", inner, nullable: true));
        StructType schema = Struct(new StructField("s", outer, nullable: true));

        var innerStruct = new StructColumnVector(inner, new[] { IntCol(1, 2, 3) }, new[] { false, true, false });
        var outerStruct = new StructColumnVector(outer, new ColumnVector[] { innerStruct }, new[] { false, false, true });
        ColumnBatch batch = Batch(schema, outerStruct);

        // s.a.b : row 0 -> 1; row 1 -> a is null; row 2 -> s is null.
        var sa = new StructFieldExpression(new ColumnReference(0, outer, nullable: true), 0, inner, true);
        var sab = new StructFieldExpression(sa, 0, DataTypes.IntegerType, true);
        ColumnVector result = Eval(sab, schema, batch);

        Assert.Equal(new int?[] { 1, null, null }, Ints(result));
    }

    [Fact]
    public void NestedFieldAccess_ComposesInsideComparison()
    {
        StructType s = Struct(new StructField("f", DataTypes.IntegerType, nullable: false));
        StructType schema = Struct(new StructField("s", s, nullable: true));
        var column = new StructColumnVector(s, new[] { IntCol(5, 999, -1) }, new[] { false, true, false });
        ColumnBatch batch = Batch(schema, column);

        // s.f > 0 : row 0 -> true; row 1 -> null (null struct); row 2 -> false.
        var field = new StructFieldExpression(new ColumnReference(0, s, nullable: true), 0, DataTypes.IntegerType, true);
        var predicate = new ComparisonExpression(field, Literal.OfInt(0), ComparisonOperator.GreaterThan);
        ColumnVector result = Eval(predicate, schema, batch);

        Assert.Equal(DataTypes.BooleanType, result.Type);
        Assert.Equal(new bool?[] { true, null, false }, Bools(result));
    }

    [Fact]
    public void SlicedStructParent_MasksCorrectly_RespectingOffset()
    {
        // A sliced (offset≠0) struct parent must still combine the struct's validity with the field's
        // own at the right physical rows. Struct rows: [F,T,F,F]; child holds a non-null sentinel (999)
        // at the null-struct row. Slice [1,3) -> logical rows {orig 1(null),2,3}.
        StructType s = Struct(new StructField("f", DataTypes.IntegerType, nullable: false));
        StructType schema = Struct(new StructField("s", s, nullable: true));
        var full = new StructColumnVector(
            s, new[] { IntCol(10, 999, 30, 40) }, new[] { false, true, false, false });
        ColumnVector sliced = full.Slice(1, 3);
        ColumnBatch batch = Batch(schema, sliced);

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: true), 0, DataTypes.IntegerType, true);
        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(new int?[] { null, 30, 40 }, Ints(result));
    }

    [Fact]
    public void AllNullStruct_YieldsAllNull_WithoutLeakingChildSlots()
    {
        StructType s = Struct(new StructField("f", DataTypes.IntegerType, nullable: false));
        StructType schema = Struct(new StructField("s", s, nullable: true));
        var column = new StructColumnVector(s, new[] { IntCol(1, 2, 3) }, new[] { true, true, true });
        ColumnBatch batch = Batch(schema, column);

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: true), 0, DataTypes.IntegerType, true);
        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(new int?[] { null, null, null }, Ints(result));
    }

    [Fact]
    public void EmptyBatch_YieldsEmptyResult()
    {
        StructType s = Struct(new StructField("f", DataTypes.IntegerType, nullable: false));
        StructType schema = Struct(new StructField("s", s, nullable: true));
        var column = new StructColumnVector(s, new[] { IntCol() });
        ColumnBatch batch = Batch(schema, column);

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: true), 0, DataTypes.IntegerType, true);
        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(0, result.Length);
    }

    [Fact]
    public void NestedStructField_UnderNullableOuterStruct_ReWrapsWithCombinedValidity()
    {
        // Extracting a STRUCT field `s.a` (not a leaf) from a nullable outer struct re-wraps the inner
        // struct with the combined (outer-null OR inner-null) validity.
        StructType inner = Struct(new StructField("b", DataTypes.IntegerType, nullable: false));
        StructType outer = Struct(new StructField("a", inner, nullable: true));
        StructType schema = Struct(new StructField("s", outer, nullable: true));

        var innerStruct = new StructColumnVector(inner, new[] { IntCol(1, 2, 3) }, new[] { false, true, false });
        var outerStruct = new StructColumnVector(outer, new ColumnVector[] { innerStruct }, new[] { false, false, true });
        ColumnBatch batch = Batch(schema, outerStruct);

        var expr = new StructFieldExpression(new ColumnReference(0, outer, nullable: true), 0, inner, true);
        ColumnVector result = Eval(expr, schema, batch);

        var resultStruct = Assert.IsType<StructColumnVector>(result);
        Assert.False(resultStruct.IsNull(0)); // outer & inner both present
        Assert.True(resultStruct.IsNull(1));  // inner `a` null
        Assert.True(resultStruct.IsNull(2));  // outer `s` null
    }

    [Fact]
    public void FlatFieldUnderSelection_ExtractsThenGathers()
    {
        // A struct column cannot row-gather (#546), but a FLAT extracted field can: under a selection
        // the field is extracted over the unselected rows and gathered through the selection, so
        // `filter(...).select(s.f)` works. Selection {2,0} over struct rows [10,·null·,30] (struct row 1
        // null): expect the gathered flat field [30, 10].
        StructType s = Struct(new StructField("f", DataTypes.IntegerType, nullable: false));
        StructType schema = Struct(new StructField("s", s, nullable: true));
        var column = new StructColumnVector(s, new[] { IntCol(10, 999, 30) }, new[] { false, true, false });
        ColumnBatch selected = Batch(schema, column).WithSelection(new SelectionVector(new[] { 2, 0 }));

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: true), 0, DataTypes.IntegerType, true);
        ColumnVector result = Eval(expr, schema, selected);

        Assert.Equal(new int?[] { 30, 10 }, Ints(result));
    }

    [Fact]
    public void FlatFieldUnderSelection_MasksNullStructRow()
    {
        // Selection {1,2} lands on struct row 1 (null struct, non-null child slot 999) and row 2 (30):
        // expect [null, 30] — the null-struct mask is applied before the gather.
        StructType s = Struct(new StructField("f", DataTypes.IntegerType, nullable: false));
        StructType schema = Struct(new StructField("s", s, nullable: true));
        var column = new StructColumnVector(s, new[] { IntCol(10, 999, 30) }, new[] { false, true, false });
        ColumnBatch selected = Batch(schema, column).WithSelection(new SelectionVector(new[] { 1, 2 }));

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: true), 0, DataTypes.IntegerType, true);
        ColumnVector result = Eval(expr, schema, selected);

        Assert.Equal(new int?[] { null, 30 }, Ints(result));
    }

    [Fact]
    public void StructTypedFieldUnderSelection_StillHitsTheGatherWall()
    {
        // A struct-TYPED field downstream of a selection still hits the #546 wall (the gathered result
        // is itself a struct, which cannot row-gather) — a clean, deterministic boundary.
        StructType inner = Struct(new StructField("b", DataTypes.IntegerType, nullable: false));
        StructType outer = Struct(new StructField("a", inner, nullable: true));
        StructType schema = Struct(new StructField("s", outer, nullable: true));

        var innerStruct = new StructColumnVector(inner, new[] { IntCol(1, 2, 3) });
        var outerStruct = new StructColumnVector(outer, new ColumnVector[] { innerStruct }, new[] { false, false, false });
        ColumnBatch selected = Batch(schema, outerStruct).WithSelection(new SelectionVector(new[] { 2, 0 }));

        var expr = new StructFieldExpression(new ColumnReference(0, outer, nullable: true), 0, inner, true);

        Assert.Throws<NotSupportedException>(() => Eval(expr, schema, selected));
    }

    private static ListColumnVector ArrCol(params int[]?[] rows)
    {
        var arrayType = new ArrayType(DataTypes.IntegerType);
        MutableColumnVector elements = ColumnVectors.Create(DataTypes.IntegerType, 8);
        var offsets = new int[rows.Length + 1];
        var nulls = new bool[rows.Length];
        int off = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] is { } row)
            {
                foreach (int e in row)
                {
                    elements.AppendValue(e);
                    off++;
                }
            }
            else
            {
                nulls[i] = true;
            }

            offsets[i + 1] = off;
        }

        return new ListColumnVector(arrayType, elements, offsets, nulls);
    }

    private static MapColumnVector MapCol(params (int Key, int Value)[]?[] rows)
    {
        var mapType = new MapType(DataTypes.IntegerType, DataTypes.IntegerType);
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.IntegerType, 8);
        MutableColumnVector values = ColumnVectors.Create(DataTypes.IntegerType, 8);
        var offsets = new int[rows.Length + 1];
        var nulls = new bool[rows.Length];
        int off = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] is { } row)
            {
                foreach ((int k, int v) in row)
                {
                    keys.AppendValue(k);
                    values.AppendValue(v);
                    off++;
                }
            }
            else
            {
                nulls[i] = true;
            }

            offsets[i + 1] = off;
        }

        return new MapColumnVector(mapType, keys, values, offsets, nulls);
    }

    private static int[]?[] ListRows(ColumnVector v)
    {
        ListColumnVector list = Assert.IsType<ListColumnVector>(v);
        var rows = new int[]?[list.Length];
        for (int i = 0; i < list.Length; i++)
        {
            if (list.IsNull(i))
            {
                continue;
            }

            ColumnVector elems = list.ElementsAt(i);
            var row = new int[elems.Length];
            for (int j = 0; j < elems.Length; j++)
            {
                row[j] = elems.GetValue<int>(j);
            }

            rows[i] = row;
        }

        return rows;
    }

    private static (int Key, int Value)[]?[] MapRows(ColumnVector v)
    {
        MapColumnVector map = Assert.IsType<MapColumnVector>(v);
        var rows = new (int, int)[]?[map.Length];
        for (int i = 0; i < map.Length; i++)
        {
            if (map.IsNull(i))
            {
                continue;
            }

            ColumnVector keys = map.KeysAt(i);
            ColumnVector values = map.ValuesAt(i);
            var entries = new (int, int)[keys.Length];
            for (int j = 0; j < keys.Length; j++)
            {
                entries[j] = (keys.GetValue<int>(j), values.GetValue<int>(j));
            }

            rows[i] = entries;
        }

        return rows;
    }

    [Fact]
    public void ArrayField_FastPath_NonNullableStruct_ExtractsList()
    {
        // #589: a nested ARRAY field extracts zero-copy when no struct row is null, preserving the field's own
        // per-row nulls (row 2 is a null list).
        var arr = new ArrayType(DataTypes.IntegerType);
        StructType s = Struct(new StructField("arr", arr, nullable: true));
        StructType schema = Struct(new StructField("s", s, nullable: false));
        var column = new StructColumnVector(s, new ColumnVector[] { ArrCol(new[] { 1, 2 }, new[] { 3 }, null) });
        ColumnBatch batch = Batch(schema, column);

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: false), 0, arr, true);
        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(arr, result.Type);
        Assert.Equal(new int[]?[] { new[] { 1, 2 }, new[] { 3 }, null }, ListRows(result));
    }

    [Fact]
    public void ArrayField_NullStruct_MasksFieldToNull()
    {
        // #589: extracting an ARRAY field of a NULL struct yields a null list — even though the child list at
        // that row holds a non-null [9,9] — sharing the element buffers and masking only the top-level validity.
        var arr = new ArrayType(DataTypes.IntegerType);
        StructType s = Struct(new StructField("arr", arr, nullable: true));
        StructType schema = Struct(new StructField("s", s, nullable: true));
        var column = new StructColumnVector(
            s, new ColumnVector[] { ArrCol(new[] { 1, 2 }, new[] { 9, 9 }, new[] { 3 }) }, new[] { false, true, false });
        ColumnBatch batch = Batch(schema, column);

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: true), 0, arr, true);
        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(new int[]?[] { new[] { 1, 2 }, null, new[] { 3 } }, ListRows(result));
    }

    [Fact]
    public void MapField_FastPath_NonNullableStruct_ExtractsMap()
    {
        // #589: a nested MAP field extracts zero-copy when no struct row is null, preserving the field's own
        // per-row nulls (row 1 is a null map).
        var map = new MapType(DataTypes.IntegerType, DataTypes.IntegerType);
        StructType s = Struct(new StructField("m", map, nullable: true));
        StructType schema = Struct(new StructField("s", s, nullable: false));
        var column = new StructColumnVector(
            s, new ColumnVector[] { MapCol(new[] { (1, 10) }, null, new[] { (2, 20), (3, 30) }) });
        ColumnBatch batch = Batch(schema, column);

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: false), 0, map, true);
        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(map, result.Type);
        Assert.Equal(new (int, int)[]?[] { new[] { (1, 10) }, null, new[] { (2, 20), (3, 30) } }, MapRows(result));
    }

    [Fact]
    public void MapField_NullStruct_MasksFieldToNull()
    {
        // #589: extracting a MAP field of a NULL struct yields a null map — even though the child map at that
        // row holds a non-null {9:99} — sharing the key/value buffers and masking only the top-level validity.
        var map = new MapType(DataTypes.IntegerType, DataTypes.IntegerType);
        StructType s = Struct(new StructField("m", map, nullable: true));
        StructType schema = Struct(new StructField("s", s, nullable: true));
        var column = new StructColumnVector(
            s, new ColumnVector[] { MapCol(new[] { (1, 10) }, new[] { (9, 99) }, new[] { (2, 20) }) },
            new[] { false, true, false });
        ColumnBatch batch = Batch(schema, column);

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: true), 0, map, true);
        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(new (int, int)[]?[] { new[] { (1, 10) }, null, new[] { (2, 20) } }, MapRows(result));
    }

    [Fact]
    public void SlicedStructParent_ArrayField_MasksRespectingOffset()
    {
        // A sliced (offset≠0) struct parent masks the ARRAY field at the right physical rows. Struct rows
        // [F,T,F,F]; lists [10],[99],[30],[40]. Slice [1,3) -> {null-struct, [30], [40]}.
        var arr = new ArrayType(DataTypes.IntegerType);
        StructType s = Struct(new StructField("arr", arr, nullable: true));
        StructType schema = Struct(new StructField("s", s, nullable: true));
        var full = new StructColumnVector(
            s, new ColumnVector[] { ArrCol(new[] { 10 }, new[] { 99 }, new[] { 30 }, new[] { 40 }) },
            new[] { false, true, false, false });
        ColumnVector sliced = full.Slice(1, 3);
        ColumnBatch batch = Batch(schema, sliced);

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: true), 0, arr, true);
        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(new int[]?[] { null, new[] { 30 }, new[] { 40 } }, ListRows(result));
    }

    [Fact]
    public void CollectionField_UnderSelection_HitsTheGatherWall()
    {
        // A collection-TYPED field downstream of a selection still hits the #546 wall (list/map cannot
        // row-gather), consistent with a struct-typed field — a clean, deterministic per-shape boundary.
        var arr = new ArrayType(DataTypes.IntegerType);
        StructType s = Struct(new StructField("arr", arr, nullable: true));
        StructType schema = Struct(new StructField("s", s, nullable: true));
        var column = new StructColumnVector(
            s, new ColumnVector[] { ArrCol(new[] { 1 }, new[] { 2 }, new[] { 3 }) }, new[] { false, false, false });
        ColumnBatch selected = Batch(schema, column).WithSelection(new SelectionVector(new[] { 2, 0 }));

        var expr = new StructFieldExpression(new ColumnReference(0, s, nullable: true), 0, arr, true);

        Assert.Throws<NotSupportedException>(() => Eval(expr, schema, selected));
    }
}
