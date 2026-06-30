using DeltaSharp.Engine.Columnar;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Evaluates a <see cref="LogicalExpression"/> (<c>AND</c>/<c>OR</c>/<c>NOT</c>) under Kleene
/// three-valued logic (STORY-03.4.1). It defers per-lane truth to the #143 scalar reference
/// (<see cref="NullPropagation.KleeneAnd"/>/<see cref="NullPropagation.KleeneOr"/>/
/// <see cref="NullPropagation.KleeneNot"/>), so a valid <c>FALSE</c> rescues <c>AND</c> and a valid
/// <c>TRUE</c> rescues <c>OR</c> even when the other operand is null. The output is a nullable boolean.
/// </summary>
internal sealed class LogicalEvaluator : ExpressionEvaluator
{
    private readonly ExpressionEvaluator _left;
    private readonly ExpressionEvaluator? _right;
    private readonly LogicalOperator _op;

    public LogicalEvaluator(LogicalExpression node, ExpressionEvaluator left, ExpressionEvaluator? right)
        : base(node.Type, node.Nullable)
    {
        _left = left;
        _right = right;
        _op = node.Operator;
    }

    /// <inheritdoc />
    public override ColumnVector Evaluate(ColumnBatch batch, BatchEvaluationMemory memory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(memory);
        int rows = batch.LogicalRowCount;
        memory.ReserveVector(Type, rows);
        MutableColumnVector result = ColumnVectors.Create(Type, rows);

        ColumnVector left = _left.Evaluate(batch, memory, cancellationToken);

        if (_op == LogicalOperator.Not)
        {
            bool leftNulls = left.HasNulls;
            for (int i = 0; i < rows; i++)
            {
                CancellationPolicy.Poll(cancellationToken, i);
                Append(result, NullPropagation.KleeneNot(ReadLane(left, leftNulls, i)));
            }

            return result;
        }

        ColumnVector right = _right!.Evaluate(batch, memory, cancellationToken);
        bool lhsNulls = left.HasNulls;
        bool rhsNulls = right.HasNulls;
        for (int i = 0; i < rows; i++)
        {
            CancellationPolicy.Poll(cancellationToken, i);
            bool? a = ReadLane(left, lhsNulls, i);
            bool? b = ReadLane(right, rhsNulls, i);
            Append(result, _op == LogicalOperator.And ? NullPropagation.KleeneAnd(a, b) : NullPropagation.KleeneOr(a, b));
        }

        return result;
    }

    private static bool? ReadLane(ColumnVector vector, bool hasNulls, int i) =>
        hasNulls && vector.IsNull(i) ? null : vector.GetValue<bool>(i);

    private static void Append(MutableColumnVector result, bool? value)
    {
        if (value is bool b)
        {
            result.AppendValue(b);
        }
        else
        {
            result.AppendNull();
        }
    }
}
