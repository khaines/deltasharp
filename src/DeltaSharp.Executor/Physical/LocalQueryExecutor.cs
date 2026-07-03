using System.Diagnostics;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Execution;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Executor;

/// <summary>
/// The Executor-side implementation of Core's <see cref="IQueryExecutor"/> seam (STORY-04.6.1 / #173,
/// hardened by STORY-04.6.4 / #176): it plans an analyzed logical plan onto EPIC-03 operators, drives
/// them on the selected backend (ADR-0001 — interpreted vectorized by default) under a cancellation/
/// timeout boundary, materializes the result within configured row/byte bounds, and reports planning +
/// execution metrics. Every path releases the run's <see cref="PhysicalRuntime"/> (and its shared
/// <see cref="ExecutionContext"/> / spill store) deterministically. Registering it into a
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

    /// <inheritdoc />
    public IReadOnlyList<Row> Collect(LogicalPlan analyzedPlan, ExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(analyzedPlan);
        ArgumentNullException.ThrowIfNull(options);
        return Execute(
            analyzedPlan,
            options,
            static (result, opts, token) =>
            {
                IReadOnlyList<Row> rows = RowMaterializer.Materialize(
                    result, opts.MaxResultRows, opts.MaxResultBytes, token);
                return (Rows: rows, OutputRows: (long)rows.Count);
            }).Rows!;
    }

    /// <inheritdoc />
    public long Count(LogicalPlan analyzedPlan, ExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(analyzedPlan);
        ArgumentNullException.ThrowIfNull(options);
        return Execute(
            analyzedPlan,
            options,
            static (result, opts, token) =>
            {
                long count = RowMaterializer.CountRows(result);
                return (Rows: (IReadOnlyList<Row>?)null, OutputRows: count);
            }).OutputRows;
    }

    // The single stage-attributed driver shared by Collect/Count. It times planning and execution, drives
    // the plan under an effective token (user cancellation linked with any timeout), materializes via
    // finalize within the configured result bounds, populates options.Metrics on both success and
    // failure, and disposes the runtime on every path (success, cancel/timeout, fault).
    private (IReadOnlyList<Row>? Rows, long OutputRows) Execute(
        LogicalPlan analyzedPlan,
        ExecutionOptions options,
        Func<BatchResult, ExecutionOptions, CancellationToken, (IReadOnlyList<Row>? Rows, long OutputRows)> finalize)
    {
        using CancellationTokenSource? timeoutCts = options.Timeout is { } timeout
            ? CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken)
            : null;
        if (timeoutCts is not null)
        {
            // A non-positive timeout means the deadline has already passed (config never produces this —
            // ExecutionOptions maps <=0 to "disabled"/null — so it only arrives via the direct options
            // API), so cancel synchronously for a deterministic, race-free timeout; otherwise schedule it.
            if (options.Timeout!.Value <= TimeSpan.Zero)
            {
                timeoutCts.Cancel();
            }
            else
            {
                timeoutCts.CancelAfter(options.Timeout.Value);
            }
        }

        CancellationToken effectiveToken = timeoutCts?.Token ?? options.CancellationToken;

        TimeSpan planningDuration = TimeSpan.Zero;
        long executionStart = 0;
        long outputRows = 0;
        long outputBatches = 0;
        PhysicalRuntime? runtime = null;

        ExecutionMetrics BuildMetrics() => new(
            planningDuration,
            executionStart == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(executionStart),
            outputRows,
            outputBatches,
            runtime?.BytesScanned ?? 0,
            runtime?.PeakMemoryBytes ?? 0);

        try
        {
            // Stage: Plan. UnsupportedPlanException is already stage-attributed and must propagate
            // unwrapped (callers assert on it); any other planner fault is attributed to Plan.
            long planningStart = Stopwatch.GetTimestamp();
            PhysicalPlan physical;
            try
            {
                physical = new PhysicalPlanner(_scanSource).Plan(analyzedPlan);
            }
            catch (UnsupportedPlanException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new QueryExecutionException(
                    QueryExecutionStage.Plan,
                    $"Physical planning failed: {ex.Message}",
                    ex,
                    BuildMetrics());
            }

            planningDuration = Stopwatch.GetElapsedTime(planningStart);

            // Stage: Backend + Materialize (both timed as execution).
            executionStart = Stopwatch.GetTimestamp();
            IExecutionBackend backend = ExecutionBackends.Select(_backendOptions);
            runtime = new PhysicalRuntime(backend, _backendOptions, effectiveToken, options.MemoryBudgetBytes);

            BatchResult result;
            try
            {
                result = physical.Execute(runtime);
            }
            catch (OperationCanceledException oce)
            {
                throw MapCancellation(options.CancellationToken, oce);
            }
            catch (UnsupportedPlanException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new QueryExecutionException(
                    QueryExecutionStage.Backend,
                    $"Query execution failed in the backend: {ex.Message}",
                    ex,
                    BuildMetrics());
            }

            outputBatches = result.Batches.Count;

            // Stage: Materialize. Result-bound breaches become a Materialize-attributed public exception;
            // value-mapping failures surface as the (Materialize-attributed) UnsupportedPlanException.
            (IReadOnlyList<Row>? Rows, long OutputRows) finalized;
            try
            {
                finalized = finalize(result, options, effectiveToken);
            }
            catch (OperationCanceledException oce)
            {
                throw MapCancellation(options.CancellationToken, oce);
            }
            catch (UnsupportedPlanException)
            {
                throw;
            }
            catch (ResultLimitExceededException ex)
            {
                throw new QueryExecutionException(
                    QueryExecutionStage.Materialize, ex.Message, ex, BuildMetrics());
            }

            outputRows = finalized.OutputRows;
            options.Metrics = BuildMetrics();
            return finalized;
        }
        finally
        {
            runtime?.Dispose();
        }
    }

    // Distinguishes a user cancellation from a timeout: if the user's own token is cancelled the original
    // OperationCanceledException is rethrown (Spark parity for cancellation); otherwise the effective
    // token was cancelled by CancelAfter, so the boundary is surfaced as a TimeoutException preserving
    // the cancellation as its cause. User cancellation wins a race with the timeout.
    private static Exception MapCancellation(CancellationToken userToken, OperationCanceledException oce)
    {
        if (userToken.IsCancellationRequested)
        {
            return oce;
        }

        return new TimeoutException(
            "The DataFrame action exceeded its configured execution timeout "
            + "('spark.deltasharp.execution.timeoutMs') and was cancelled.", oce);
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
