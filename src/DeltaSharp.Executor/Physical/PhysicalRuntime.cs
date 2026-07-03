using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Types;
using ExecutionContext = DeltaSharp.Engine.Execution.ExecutionContext;

namespace DeltaSharp.Executor;

/// <summary>
/// The materialized output of executing a <see cref="PhysicalPlan"/> subtree: the output
/// <see cref="StructType"/> plus the ordered <see cref="ColumnBatch"/>es it produced. STORY-04.6.2's
/// M1 execution model materializes at every operator boundary (each node fully drains its child into
/// a batch list before the next operator opens over it); intra-subtree streaming/fusion is a future
/// optimization documented in <c>docs/engineering/design/physical-planning.md</c>.
/// </summary>
/// <param name="Schema">The output schema (field names/types) the batches conform to.</param>
/// <param name="Batches">The output batches in order; each conforms to <paramref name="Schema"/>.</param>
internal readonly record struct BatchResult(StructType Schema, IReadOnlyList<ColumnBatch> Batches);

/// <summary>
/// The per-execution context a <see cref="PhysicalPlan"/> tree is driven with: the selected EPIC-03
/// <see cref="IExecutionBackend"/> (ADR-0001 — interpreted vectorized by default), the backend
/// options, and the cancellation token. It also builds the Engine <see cref="ExecutionContext"/> each
/// operator open needs and drives a shallow Engine operator to completion.
/// </summary>
internal sealed class PhysicalRuntime
{
    private readonly IExecutionBackend _backend;
    private readonly ExecutionBackendOptions _options;
    private readonly CancellationToken _cancellationToken;

    /// <summary>Creates a runtime bound to a backend, its options, and a cancellation token.</summary>
    /// <param name="backend">The EPIC-03 execution backend chosen for this run.</param>
    /// <param name="options">Backend options (e.g. force-interpreter) threaded into each context.</param>
    /// <param name="cancellationToken">Cancellation observed at batch boundaries.</param>
    public PhysicalRuntime(
        IExecutionBackend backend,
        ExecutionBackendOptions options,
        CancellationToken cancellationToken)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cancellationToken = cancellationToken;
    }

    /// <summary>The ANSI strictness lens M1 evaluates arithmetic/casts under (Spark/Engine default).</summary>
    public AnsiMode AnsiMode => AnsiMode.Ansi;

    /// <summary>
    /// Opens <paramref name="op"/> on the backend and drains its <see cref="IBatchStream"/> to a fully
    /// materialized batch list. Engine construction/validation failures (an ill-typed operator the
    /// bridge could not have foreseen) are surfaced as a deterministic <see cref="UnsupportedPlanException"/>.
    /// </summary>
    /// <param name="op">The shallow Engine operator (built over an in-memory scan of child batches).</param>
    /// <returns>Every batch the operator emitted, in order.</returns>
    public IReadOnlyList<ColumnBatch> Run(PhysicalOperator op)
    {
        ArgumentNullException.ThrowIfNull(op);

        var context = new ExecutionContext(BoundedExecutionMemory.Unbounded, _cancellationToken, _options);
        var batches = new List<ColumnBatch>();
        IBatchStream stream = _backend.Open(op, context);
        try
        {
            while (stream.TryGetNext(out ColumnBatch? batch))
            {
                batches.Add(batch);
            }
        }
        finally
        {
            stream.Dispose();
        }

        return batches;
    }

    /// <summary>Wraps a child result's batches in an EPIC-03 in-memory scan so an operator can open over them.</summary>
    /// <param name="child">The already-materialized child result.</param>
    /// <returns>A leaf <see cref="PhysicalOperator"/> streaming the child batches verbatim.</returns>
    public static PhysicalOperator ScanOf(BatchResult child) =>
        new InMemoryScanOperator(child.Schema, child.Batches);
}
