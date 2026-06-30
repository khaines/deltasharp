using DeltaSharp.Engine.Columnar;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Evaluates a <see cref="ColumnReference"/> leaf by returning the batch's logical view of the
/// referenced column (STORY-03.4.1) — <c>batch.SelectedColumn(ordinal)</c>. This is <b>zero-copy</b>
/// and selection-aware: with no selection it is the column itself; under a selection it is a gathered
/// view of length <see cref="ColumnBatch.LogicalRowCount"/>. No memory is reserved.
/// </summary>
internal sealed class ColumnReferenceEvaluator : ExpressionEvaluator
{
    private readonly int _ordinal;

    public ColumnReferenceEvaluator(ColumnReference reference)
        : base(reference.Type, reference.Nullable)
    {
        _ordinal = reference.Ordinal;
    }

    /// <inheritdoc />
    public override ColumnVector Evaluate(ColumnBatch batch, BatchEvaluationMemory memory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return batch.SelectedColumn(_ordinal);
    }
}
