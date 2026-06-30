using DeltaSharp.Engine.Columnar;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Evaluates an <see cref="IsNullExpression"/> (<c>IS NULL</c> / <c>IS NOT NULL</c>) into a boolean
/// vector that is <b>never</b> null (STORY-03.4.1): it reads the child's validity bit and emits a
/// defined <see langword="true"/>/<see langword="false"/> for every lane, so a null input maps to a
/// boolean rather than propagating.
/// </summary>
internal sealed class NullCheckEvaluator : ExpressionEvaluator
{
    private readonly ExpressionEvaluator _child;
    private readonly bool _negated;

    public NullCheckEvaluator(IsNullExpression node, ExpressionEvaluator child)
        : base(node.Type, nullable: false)
    {
        _child = child;
        _negated = node.Negated;
    }

    /// <inheritdoc />
    public override ColumnVector Evaluate(ColumnBatch batch, BatchEvaluationMemory memory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(memory);
        ColumnVector child = _child.Evaluate(batch, memory, cancellationToken);
        int rows = batch.LogicalRowCount;

        memory.ReserveVector(Type, rows);
        MutableColumnVector result = ColumnVectors.Create(Type, rows);
        bool hasNulls = child.HasNulls;

        for (int i = 0; i < rows; i++)
        {
            CancellationPolicy.Poll(cancellationToken, i);
            bool isNull = hasNulls && child.IsNull(i);
            result.AppendValue(_negated ? !isNull : isNull);
        }

        return result;
    }
}
