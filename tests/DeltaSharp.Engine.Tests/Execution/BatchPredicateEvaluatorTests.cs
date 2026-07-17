using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Execution;

/// <summary>
/// Tests the public <see cref="BatchPredicateEvaluator"/> facade (#581) — the out-of-Engine entry point the
/// Delta write seam uses to evaluate a resolved boolean predicate over a write <see cref="ColumnBatch"/> and
/// obtain the per-row nullable-boolean result (true / false / null), so the caller can apply Delta's
/// "reject a row that is not TRUE" rule.
/// </summary>
public class BatchPredicateEvaluatorTests
{
    private static ColumnVector IntCol(params int?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.IntegerType, System.Math.Max(values.Length, 1));
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

    private static ColumnBatch Batch(StructType schema, params ColumnVector[] columns) =>
        new ManagedColumnBatch(schema, columns, columns.Length > 0 ? columns[0].Length : 0);

    private static bool?[] Read(ColumnVector v)
    {
        var result = new bool?[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            result[i] = v.IsNull(i) ? null : v.GetValue<bool>(i);
        }

        return result;
    }

    [Fact]
    public void Evaluate_ComparisonPredicate_ReturnsPerRowNullableBoolean()
    {
        StructType schema = new(new[] { new StructField("amount", DataTypes.IntegerType, nullable: true) });
        ColumnBatch batch = Batch(schema, IntCol(5, -1, null));
        // amount > 0 : 5 -> true, -1 -> false, null -> null (Kleene 3VL).
        var predicate = new ComparisonExpression(
            new ColumnReference(0, DataTypes.IntegerType, nullable: true),
            Literal.OfInt(0),
            ComparisonOperator.GreaterThan);

        BatchPredicateEvaluator evaluator = BatchPredicateEvaluator.Build(predicate, schema, "test");
        ColumnVector result = evaluator.Evaluate(batch, BoundedExecutionMemory.Unbounded);

        Assert.Equal(DataTypes.BooleanType, result.Type);
        Assert.Equal(new bool?[] { true, false, null }, Read(result));
    }

    [Fact]
    public void Build_NonBooleanPredicate_Throws()
    {
        StructType schema = new(new[] { new StructField("id", DataTypes.IntegerType, nullable: false) });
        var nonBoolean = new ColumnReference(0, DataTypes.IntegerType, nullable: false);

        Assert.Throws<System.ArgumentException>(() => BatchPredicateEvaluator.Build(nonBoolean, schema, "test"));
    }
}
