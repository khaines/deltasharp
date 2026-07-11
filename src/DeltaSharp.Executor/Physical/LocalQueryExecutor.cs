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
    private readonly ILocalSinkFactory _sinkFactory;
    private readonly ExecutionBackendOptions _backendOptions;

    /// <summary>
    /// A test-only seam (STORY-04.6.4 disposal-observability) that builds the run's
    /// <see cref="PhysicalRuntime"/>. Defaults to the real constructor; a test supplies a factory that
    /// captures the created runtime so it can assert the executor disposed it on the cancel/timeout/fault
    /// path (the assertion fails if the disposal <c>finally</c> below is removed). Never set in production.
    /// </summary>
    internal Func<IExecutionBackend, ExecutionBackendOptions, CancellationToken, long?, PhysicalRuntime>? RuntimeFactory { get; init; }

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
        : this(scanSource, backendOptions, DeltaStorageAdapter.DefaultSinkFactory)
    {
    }

    /// <summary>Creates an executor with explicit backend options and an explicit sink factory (the write
    /// door's data-out seam, STORY-04.6.3). Tests supply a fresh <see cref="InMemorySinkRegistry"/> for
    /// isolation; the production paths use the process-wide <see cref="DeltaStorageAdapter.DefaultSinkFactory"/>
    /// (the in-memory sink composed with the Delta write sink, #487).</summary>
    /// <param name="scanSource">The data-in seam resolving scans to in-memory batches.</param>
    /// <param name="backendOptions">The backend selection options.</param>
    /// <param name="sinkFactory">The data-out seam resolving a write intent to a local sink.</param>
    public LocalQueryExecutor(IScanSource scanSource, ExecutionBackendOptions backendOptions, ILocalSinkFactory sinkFactory)
    {
        _scanSource = scanSource ?? throw new ArgumentNullException(nameof(scanSource));
        _backendOptions = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
        _sinkFactory = sinkFactory ?? throw new ArgumentNullException(nameof(sinkFactory));
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
            PhysicalPlan physical = new PhysicalPlanner(_scanSource, _sinkFactory).Plan(analyzedPlan);
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
    public IReadOnlyList<Row> Collect(LogicalPlan analyzedPlan, ExecutionOptions options, ExecutionMetricsSink? metricsSink = null)
    {
        ArgumentNullException.ThrowIfNull(analyzedPlan);
        ArgumentNullException.ThrowIfNull(options);
        return Execute(
            analyzedPlan,
            options,
            metricsSink,
            static (physical, result, opts, token) =>
            {
                IReadOnlyList<Row> rows = RowMaterializer.Materialize(
                    result, opts.MaxResultRows, opts.MaxResultBytes, token);
                return (Rows: rows, OutputRows: (long)rows.Count);
            }).Rows!;
    }

    /// <inheritdoc />
    public long Count(LogicalPlan analyzedPlan, ExecutionOptions options, ExecutionMetricsSink? metricsSink = null)
    {
        ArgumentNullException.ThrowIfNull(analyzedPlan);
        ArgumentNullException.ThrowIfNull(options);
        return Execute(
            analyzedPlan,
            options,
            metricsSink,
            static (physical, result, opts, token) =>
            {
                long count = RowMaterializer.CountRows(result, token);
                return (Rows: (IReadOnlyList<Row>?)null, OutputRows: count);
            }).OutputRows;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Drives the write plan through the SAME stage-attributed <see cref="Execute"/> driver as
    /// <see cref="Collect"/>/<see cref="Count"/>: planning maps the <c>WriteToSource</c> to a
    /// <see cref="WriteToSinkPlan"/> (resolving the sink through the data-out seam), the backend stage
    /// executes it — draining the child and committing to the sink atomically — and the finalize returns
    /// the sink's AUTHORITATIVE committed count (<see cref="WriteToSinkPlan.CommittedRowCount"/>), which is
    /// 0 for an <see cref="SaveMode.Ignore"/> that skipped an existing target — NOT the child's produced
    /// row count. The finalize does <b>no</b> cancellable or fault-prone work: the commit is the final
    /// failure boundary, so a cancel/timeout is observed before the commit or not at all, and a committed
    /// write never surfaces as a cancelled/failed <c>Save</c>. A sink-resolution miss is a Plan-stage
    /// <see cref="UnsupportedPlanException"/>; a commit conflict (e.g. <see cref="SaveMode.ErrorIfExists"/>
    /// onto an existing target) surfaces as a Backend-stage <see cref="QueryExecutionException"/>, so a
    /// write failure is stage-attributed exactly like a read failure (STORY-04.6.4).
    /// </remarks>
    public long Write(LogicalPlan analyzedPlan, ExecutionOptions options, ExecutionMetricsSink? metricsSink = null)
    {
        ArgumentNullException.ThrowIfNull(analyzedPlan);
        ArgumentNullException.ThrowIfNull(options);
        return Execute(
            analyzedPlan,
            options,
            metricsSink,
            static (physical, result, opts, token) =>
            {
                // The WriteToSinkPlan already committed during the backend stage; report the rows it
                // actually WROTE (its captured authoritative count) — not a post-commit CountRows over the
                // child, which would (a) over-report a skipped Ignore and (b) re-poll the cancellation
                // token in the post-commit window, turning a committed write into a failed Save.
                long written = ((WriteToSinkPlan)physical).CommittedRowCount ?? 0;
                return (Rows: (IReadOnlyList<Row>?)null, OutputRows: written);
            }).OutputRows;
    }

    // The single stage-attributed driver shared by Collect/Count. It times planning and execution, drives
    // the plan under an effective token (user cancellation linked with any timeout), materializes via
    // finalize within the configured result bounds, publishes the run's metrics into the per-call sink on
    // both the success and failure paths (in a finally), and disposes the runtime on every path (success,
    // cancel/timeout, fault).
    private (IReadOnlyList<Row>? Rows, long OutputRows) Execute(
        LogicalPlan analyzedPlan,
        ExecutionOptions options,
        ExecutionMetricsSink? metricsSink,
        Func<PhysicalPlan, BatchResult, ExecutionOptions, CancellationToken, (IReadOnlyList<Row>? Rows, long OutputRows)> finalize)
    {
        using CancellationTokenSource? timeoutCts = options.Timeout is { } timeout
            ? CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken)
            : null;
        if (timeoutCts is not null)
        {
            // A non-positive timeout means the deadline has already passed (config never produces this —
            // ExecutionOptions maps <=0 to "disabled"/null — so it only arrives via the direct options
            // API), so cancel synchronously for a deterministic, race-free timeout; otherwise schedule it.
            // The timeout has already been clamped to CancelAfter's ceiling in ExecutionOptions.From, but
            // clamp defensively here too so a direct-options huge timeout never leaks a raw framework throw.
            if (options.Timeout!.Value <= TimeSpan.Zero)
            {
                timeoutCts.Cancel();
            }
            else
            {
                TimeSpan delay = options.Timeout.Value;
                if (delay.TotalMilliseconds > ExecutionOptions.MaxTimeoutMilliseconds)
                {
                    delay = TimeSpan.FromMilliseconds(ExecutionOptions.MaxTimeoutMilliseconds);
                }

                timeoutCts.CancelAfter(delay);
            }
        }

        CancellationToken effectiveToken = timeoutCts?.Token ?? options.CancellationToken;

        TimeSpan planningDuration = TimeSpan.Zero;
        long planningStart = 0;
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
            runtime?.PeakMemoryBytes ?? 0,
            runtime?.SpilledBytes ?? 0);

        try
        {
            // Upfront cancellation/timeout gate (criterion 1). Checked BEFORE any stage runs so a
            // pre-cancelled token or an already-elapsed timeout stops the action deterministically for
            // BOTH Collect and Count and for EVERY plan shape — including a bare ScanPlan/LimitPlan/
            // UnionPlan root that never reaches PhysicalRuntime.Run's per-batch poll, and Count (which
            // never materializes rows). Mapped so a timeout surfaces as TimeoutException, not OCE.
            if (effectiveToken.IsCancellationRequested)
            {
                throw MapCancellation(options.CancellationToken, new OperationCanceledException(effectiveToken));
            }

            // Stage: Plan. UnsupportedPlanException is already stage-attributed and must propagate
            // unwrapped (callers assert on it); any other planner fault is attributed to Plan.
            planningStart = Stopwatch.GetTimestamp();
            PhysicalPlan physical;
            try
            {
                physical = new PhysicalPlanner(_scanSource, _sinkFactory).Plan(analyzedPlan);
            }
            catch (UnsupportedPlanException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Record the elapsed planning time even on a planning fault so PlanningDuration reflects
                // real work rather than 0 (criterion 4 / #176 #11).
                planningDuration = Stopwatch.GetElapsedTime(planningStart);
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
            runtime = (RuntimeFactory ?? DefaultRuntimeFactory)(
                backend, _backendOptions, effectiveToken, options.MemoryBudgetBytes);

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

            // Stage: Materialize. Result-bound breaches become a Materialize-attributed public exception;
            // value-mapping failures surface as the (Materialize-attributed) UnsupportedPlanException; any
            // other unforeseen materialization fault is wrapped as Stage = Materialize (mirrors the Backend
            // stage's general catch — #176 #2). Counting the output batches is part of materialization, so
            // it lives inside this try too (a lazily-faulting batch list is attributed here, not left to
            // escape unwrapped through the outer finally).
            (IReadOnlyList<Row>? Rows, long OutputRows) finalized;
            try
            {
                outputBatches = result.Batches.Count;
                finalized = finalize(physical, result, options, effectiveToken);
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
            catch (Exception ex)
            {
                throw new QueryExecutionException(
                    QueryExecutionStage.Materialize,
                    $"Result materialization failed: {ex.Message}",
                    ex,
                    BuildMetrics());
            }

            outputRows = finalized.OutputRows;
            return finalized;
        }
        finally
        {
            // Publish the run's metrics into the per-call sink on EVERY path (success, cancel/timeout,
            // fault) so the out-metrics overloads surface partial counters even when the action throws
            // (#176 #4/#5). The sink is per-action, so this never mutates shared/static state.
            if (metricsSink is not null)
            {
                metricsSink.Metrics = BuildMetrics();
            }

            runtime?.Dispose();
        }
    }

    private static readonly Func<IExecutionBackend, ExecutionBackendOptions, CancellationToken, long?, PhysicalRuntime>
        DefaultRuntimeFactory = static (backend, options, token, budget) =>
            new PhysicalRuntime(backend, options, token, budget);

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
