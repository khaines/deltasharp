using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// A public, backend-owned entry point for evaluating a resolved boolean <see cref="PhysicalExpression"/>
/// predicate over a <see cref="ColumnBatch"/> and returning the per-row nullable-boolean result vector
/// (STORY-03.4.1). It exists for out-of-Engine callers — notably the Delta write seam's per-row constraint
/// enforcement (#581) — that hold a translated predicate but cannot reach the internal
/// <c>ExpressionEvaluators.Build</c> dispatch. The AOT-clean interpreted tier is used (the ADR-0001
/// correctness reference); the caller applies its own domain rule to the returned vector (for a Delta
/// constraint: a row is rejected when the predicate is <b>not TRUE</b> — i.e. false OR null).
/// </summary>
public sealed class BatchPredicateEvaluator
{
    private readonly ExpressionEvaluator _evaluator;

    private BatchPredicateEvaluator(ExpressionEvaluator evaluator) => _evaluator = evaluator;

    /// <summary>Builds a predicate evaluator binding <paramref name="predicate"/> to <paramref name="inputSchema"/>.</summary>
    /// <param name="predicate">A resolved boolean physical expression whose column references index <paramref name="inputSchema"/>.</param>
    /// <param name="inputSchema">The schema the evaluated batches conform to.</param>
    /// <param name="backendName">The backend name attributed in any <see cref="UnsupportedOperatorException"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="predicate"/> is not boolean, or a reference is out of range.</exception>
    /// <exception cref="UnsupportedOperatorException">The predicate shape is not interpretable.</exception>
    public static BatchPredicateEvaluator Build(PhysicalExpression predicate, StructType inputSchema, string backendName)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(inputSchema);
        ArgumentNullException.ThrowIfNull(backendName);
        if (predicate.Type is not BooleanType)
        {
            throw new ArgumentException(
                $"A predicate must be boolean, but the expression is '{predicate.Type.SimpleString}'.",
                nameof(predicate));
        }

        return new BatchPredicateEvaluator(
            ExpressionEvaluators.Build(predicate, inputSchema, backendName, OperatorKind.Filter));
    }

    /// <summary>
    /// Evaluates the predicate over <paramref name="batch"/>'s logical rows, returning a nullable-boolean
    /// vector of <see cref="ColumnBatch.LogicalRowCount"/> rows (true / false / null per Kleene 3VL), in
    /// logical (selection) order. Any evaluation footprint is reserved against <paramref name="memory"/> and
    /// released before returning (the returned vector is an independent allocation and stays valid).
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ExecutionMemoryException">Evaluation exceeds <paramref name="memory"/>'s budget.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled.</exception>
    public ColumnVector Evaluate(ColumnBatch batch, IExecutionMemory memory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(memory);
        var ledger = new BatchEvaluationMemory(memory);
        try
        {
            return _evaluator.Evaluate(batch, ledger, cancellationToken);
        }
        finally
        {
            ledger.Release();
        }
    }
}
