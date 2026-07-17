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
}
