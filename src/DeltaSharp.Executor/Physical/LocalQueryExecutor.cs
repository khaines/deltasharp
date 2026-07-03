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

    /// <inheritdoc />
    /// <remarks>
    /// Plans the query with the SAME <see cref="PhysicalPlanner"/> <see cref="Collect"/>/<see cref="Count"/>
    /// use, then renders the tree — it does <b>not</b> call <see cref="PhysicalPlan.Execute"/>, so no
    /// operator opens, no batch is read, and no backend runs (ADR-0001, lazy/eager). The seam is
    /// contractually <b>non-throwing</b> (STORY-04.7.3 AC4): an <see cref="UnsupportedPlanException"/>
    /// (an operator/expression with no M1 mapping, e.g. a cross join or a write plan) renders its precise
    /// diagnostic, and ANY other planning-time fault is also rendered as a diagnostic line rather than
    /// rethrown. The broad fallback is required because <see cref="PhysicalPlanner.Plan"/> eagerly builds
    /// Engine expressions during planning, and some Engine expression constructors (e.g.
    /// <c>ComparisonExpression</c>/<c>ArithmeticExpression</c>) throw a raw <see cref="ArgumentException"/>
    /// on ill-typed operand combinations the analyzer accepts (e.g. <c>lit(null) == lit(null)</c> or a
    /// complex-typed equality) — those must degrade to a diagnostic, never crash a debugging aid.
    /// </remarks>
    public string ExplainPhysical(LogicalPlan analyzedPlan)
    {
        ArgumentNullException.ThrowIfNull(analyzedPlan);
        try
        {
            PhysicalPlan physical = new PhysicalPlanner(_scanSource).Plan(analyzedPlan);
            return physical.TreeString();
        }
        catch (UnsupportedPlanException ex)
        {
            return $"<cannot plan physically: {ex.Message}>";
        }
        catch (Exception ex)
        {
            // Non-throwing seam (AC4): any other planning-time fault — most notably a raw
            // ArgumentException from an Engine expression constructor for an operand-type combination
            // the analyzer permitted but the interpreted backend has no kernel for — becomes a
            // diagnostic line so Explain still renders the logical sections instead of throwing.
            return $"<cannot plan physically: {ex.Message}>";
        }
    }

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
        // Both ExecutionBackend selections execute operators through the SAME InterpretedOperators.Open
        // dispatch, which always builds interpreted ExpressionEvaluators (the backend name only attributes
        // exceptions). CompiledBackend's Expression.Compile scalar fusion (STORY-03.4.2) is NOT wired into
        // the operator Open() path — that wiring is deferred to the operator layer — so both selections
        // currently run byte-identical interpreted code. The end-to-end backend-parity check is therefore a
        // plumbing/smoke cross-check (result-identity across the selection seam), not an interpreted-vs-
        // compiled differential; the genuine expression-level differential lives in the Engine parity oracle
        // (BackendParityOracle, #154), which calls CompiledBackend.BuildExpressionEvaluator directly. The
        // end-to-end check becomes a differential once operator-level fusion wiring lands (ADR-0001
        // §Follow-ups / EPIC-13, #309/#310).
        return session.ExecutionBackend == ExecutionBackend.Interpreted
            ? new ExecutionBackendOptions { ForceInterpreted = true }
            : ExecutionBackendOptions.Default;
    }
}
