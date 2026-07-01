using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Evaluates a <see cref="ComparisonExpression"/> lane-by-lane into a nullable boolean vector
/// (STORY-03.4.1). Comparison is <b>propagate-on-any-null</b>: a null operand yields SQL <c>NULL</c>
/// (#143). Numeric lanes compare at the resolved common type; floating lanes use Spark's NaN/−0
/// ordering (<see cref="ScalarReader.CompareDouble"/>); strings/binary compare unsigned
/// byte-lexicographically; a date and a timestamp compare at the date's UTC-midnight instant.
/// </summary>
internal sealed class ComparisonEvaluator : ExpressionEvaluator
{
    private readonly ExpressionEvaluator _left;
    private readonly ExpressionEvaluator _right;
    private readonly ComparisonOperator _op;
    private readonly ComparisonEvalKind _kind;

    public ComparisonEvaluator(ComparisonExpression node, ExpressionEvaluator left, ExpressionEvaluator right)
        : base(node.Type, node.Nullable)
    {
        _left = left;
        _right = right;
        _op = node.Operator;
        _kind = node.EvalKind;
    }

    /// <inheritdoc />
    public override ColumnVector Evaluate(ColumnBatch batch, BatchEvaluationMemory memory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(memory);
        ColumnVector left = _left.Evaluate(batch, memory, cancellationToken);
        ColumnVector right = _right.Evaluate(batch, memory, cancellationToken);
        int rows = batch.LogicalRowCount;

        memory.ReserveVector(Type, rows);
        MutableColumnVector result = ColumnVectors.Create(Type, rows);
        bool leftNulls = left.HasNulls;
        bool rightNulls = right.HasNulls;

        for (int i = 0; i < rows; i++)
        {
            CancellationPolicy.Poll(cancellationToken, i);
            if ((leftNulls && left.IsNull(i)) || (rightNulls && right.IsNull(i)))
            {
                result.AppendNull();
                continue;
            }

            int sign = Compare(left, right, i);
            result.AppendValue(ToBoolean(sign));
        }

        return result;
    }

    private bool ToBoolean(int sign) => _op switch
    {
        ComparisonOperator.Equal => sign == 0,
        ComparisonOperator.NotEqual => sign != 0,
        ComparisonOperator.LessThan => sign < 0,
        ComparisonOperator.LessThanOrEqual => sign <= 0,
        ComparisonOperator.GreaterThan => sign > 0,
        ComparisonOperator.GreaterThanOrEqual => sign >= 0,
        _ => throw new InvalidOperationException($"Unknown comparison operator '{_op}'."),
    };

    private int Compare(ColumnVector left, ColumnVector right, int i)
    {
        switch (_kind)
        {
            case ComparisonEvalKind.Int64:
            case ComparisonEvalKind.Date:
            case ComparisonEvalKind.Timestamp:
                return ScalarReader.ReadInt64(left, i).CompareTo(ScalarReader.ReadInt64(right, i));

            case ComparisonEvalKind.Double:
                return ScalarReader.CompareDouble(ScalarReader.ReadDouble(left, i), ScalarReader.ReadDouble(right, i));

            case ComparisonEvalKind.Decimal:
                return ScalarReader.CompareDecimal(ScalarReader.ReadDecimal(left, i), ScalarReader.ReadDecimal(right, i));

            case ComparisonEvalKind.Boolean:
                return (ScalarReader.ReadBool(left, i) ? 1 : 0).CompareTo(ScalarReader.ReadBool(right, i) ? 1 : 0);

            case ComparisonEvalKind.String:
            case ComparisonEvalKind.Binary:
                return ScalarReader.ReadBytes(left, i).SequenceCompareTo(ScalarReader.ReadBytes(right, i));

            default:
                return PromoteToMicros(left, i).CompareTo(PromoteToMicros(right, i));
        }
    }

    private static long PromoteToMicros(ColumnVector vector, int i) =>
        vector.Type is DateType
            ? ScalarReader.ReadInt64(vector, i) * TemporalValues.MicrosPerDay
            : ScalarReader.ReadInt64(vector, i);
}
