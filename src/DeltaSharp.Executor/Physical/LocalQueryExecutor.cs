using DeltaSharp.Engine.Execution;
using DeltaSharp.Execution;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Executor;

/// <summary>
/// The Executor-side implementation of Core's <see cref="IQueryExecutor"/> seam (STORY-04.6.1 / #173):
/// it plans an analyzed logical plan onto EPIC-03 operators, drives them on the selected backend
/// (ADR-0001 — interpreted vectorized by default), and materializes the result. Registering it into a
/// <see cref="SparkSession"/> is what finally makes a DeltaSharp query run end-to-end.
/// </summary>
internal sealed class LocalQueryExecutor : IQueryExecutor
{
    private readonly IScanSource _scanSource;
    private readonly ExecutionBackendOptions _backendOptions;

    /// <summary>Creates an executor bound to a session (backend chosen from the session's config).</summary>
    /// <param name="scanSource">The data-in seam resolving scans to in-memory batches.</param>
    /// <param name="session">The owning session; its execution-backend config selects the backend.</param>
    public LocalQueryExecutor(IScanSource scanSource, SparkSession session)
        : this(scanSource, OptionsFor(session))
    {
    }

    /// <summary>Creates an executor with explicit backend options (used by tests for determinism).</summary>
    /// <param name="scanSource">The data-in seam resolving scans to in-memory batches.</param>
    /// <param name="backendOptions">The backend selection options.</param>
    public LocalQueryExecutor(IScanSource scanSource, ExecutionBackendOptions backendOptions)
    {
        _scanSource = scanSource ?? throw new ArgumentNullException(nameof(scanSource));
        _backendOptions = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
    }

    /// <inheritdoc />
    public IReadOnlyList<Row> Collect(LogicalPlan analyzedPlan) =>
        RowMaterializer.Materialize(ExecutePlan(analyzedPlan));

    /// <inheritdoc />
    public long Count(LogicalPlan analyzedPlan) =>
        RowMaterializer.CountRows(ExecutePlan(analyzedPlan));

    private BatchResult ExecutePlan(LogicalPlan analyzedPlan)
    {
        ArgumentNullException.ThrowIfNull(analyzedPlan);
        var planner = new PhysicalPlanner(_scanSource);
        PhysicalPlan physical = planner.Plan(analyzedPlan);
        IExecutionBackend backend = ExecutionBackends.Select(_backendOptions);
        var runtime = new PhysicalRuntime(backend, _backendOptions, CancellationToken.None);
        return physical.Execute(runtime);
    }

    private static ExecutionBackendOptions OptionsFor(SparkSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        // ADR-0001: the interpreted vectorized backend is the default and the correctness reference.
        // In M1 the "compiled" tier is not yet wired (intra-operator Expression.Compile fusion is #148),
        // so both backend selections currently delegate to the same InterpretedOperators.Open — the
        // end-to-end backend-parity check is therefore a smoke test today, not yet a true differential
        // oracle; it becomes one once the compiled tier lands.
        return session.ExecutionBackend == ExecutionBackend.Interpreted
            ? new ExecutionBackendOptions { ForceInterpreted = true }
            : ExecutionBackendOptions.Default;
    }
}
