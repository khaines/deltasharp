using DeltaSharp.Analysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Execution;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// The M1 in-memory relation/scan fixture: registers a relation's schema (in a catalog, for the
/// analyzer) and its <see cref="ColumnBatch"/>es (in an <see cref="InMemoryScanSource"/>, for the
/// planner), then builds analyzed logical plans over it and runs them end-to-end. It is the data-in
/// door used to prove <c>filter/project/groupBy/agg/join/sort/limit/distinct/union → collect</c>
/// returns correct <see cref="Row"/>s before the public read-door (STORY-04.1.2 / #158) exists.
/// </summary>
/// <remarks>
/// Lives in the (non-packable) Executor assembly because it needs Core internals (the analyzer,
/// catalog, and logical IR); it is consumed by <c>DeltaSharp.Executor.Tests</c>. When #158 lands, the
/// public read-door registers batches (or a real reader) through the same <see cref="IScanSource"/>.
/// </remarks>
internal sealed class InMemoryRelationFixture
{
    private readonly LocalCatalog _catalog = new();
    private readonly InMemoryScanSource _scanSource;

    /// <summary>Creates a fixture with its own isolated scan source (or the process-wide default).</summary>
    /// <param name="useDefaultScanSource">
    /// When <see langword="true"/>, batches register into <see cref="InMemoryScanSource.Default"/> — the
    /// source the module-initializer-registered <see cref="LocalQueryExecutor"/> reads — so a
    /// <see cref="SparkSession"/>'s executor can run plans built here. Use unique relation names to
    /// avoid cross-test collisions in the shared default.
    /// </param>
    public InMemoryRelationFixture(bool useDefaultScanSource = false)
    {
        _scanSource = useDefaultScanSource ? InMemoryScanSource.Default : new InMemoryScanSource();
    }

    /// <summary>The scan source this fixture registers batches into (M1 data-in seam).</summary>
    public IScanSource ScanSource => _scanSource;

    /// <summary>Registers a single-part relation with its schema and backing batches.</summary>
    /// <param name="name">The relation name (single-part identifier).</param>
    /// <param name="schema">The relation schema.</param>
    /// <param name="batches">The backing batches (each must conform to <paramref name="schema"/>).</param>
    /// <returns>A base <see cref="DataFrame"/> over the relation, ready for transformations.</returns>
    public DataFrame Relation(string name, StructType schema, params ColumnBatch[] batches)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(batches);

        _catalog.Register(name, schema);
        _scanSource.Register(new[] { name }, schema, batches);
        return new DataFrame(new UnresolvedRelation(new[] { name }));
    }

    /// <summary>
    /// Registers a relation and returns a <see cref="DataFrame"/> <b>bound to <paramref name="session"/></b>
    /// (so <c>DataFrame.Explain</c>/<c>ToExplainString</c>, which require a session, render through the
    /// full Core↔Executor seam). The schema registers in both the fixture catalog and the session catalog,
    /// and the batches register in this fixture's scan source — use <c>useDefaultScanSource: true</c> so the
    /// session's own <see cref="LocalQueryExecutor"/> (backed by <see cref="InMemoryScanSource.Default"/>)
    /// resolves the same batches.
    /// </summary>
    /// <param name="session">The session to bind the frame to (and register the schema in).</param>
    /// <param name="name">The relation name (single-part identifier).</param>
    /// <param name="schema">The relation schema.</param>
    /// <param name="batches">The backing batches (each must conform to <paramref name="schema"/>).</param>
    /// <returns>A session-bound <see cref="DataFrame"/> over the relation.</returns>
    public DataFrame RelationBoundTo(SparkSession session, string name, StructType schema, params ColumnBatch[] batches)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(batches);

        _catalog.Register(name, schema);
        session.Catalog.Register(name, schema);
        _scanSource.Register(new[] { name }, schema, batches);
        return new DataFrame(session, new UnresolvedRelation(new[] { name }));
    }

    /// <summary>
    /// Registers a relation's SCHEMA in the catalog only (not the scan source), so the analyzer resolves
    /// the relation but the planner's scan resolution misses it. This drives the Scan-stage failure path
    /// (STORY-04.6.4 criterion 2): the planner raises an <see cref="UnsupportedPlanException"/> attributed
    /// to <see cref="QueryExecutionStage.Scan"/>.
    /// </summary>
    /// <param name="name">The relation name (single-part identifier).</param>
    /// <param name="schema">The relation schema.</param>
    /// <returns>A base <see cref="DataFrame"/> over the schema-only relation.</returns>
    public DataFrame RelationSchemaOnly(string name, StructType schema)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(schema);

        _catalog.Register(name, schema);
        return new DataFrame(new UnresolvedRelation(new[] { name }));
    }

    /// <summary>
    /// Builds a <see cref="DataFrame"/> over an in-memory <c>LocalRelation</c> (#158 read-door) carrying
    /// <paramref name="rows"/> inline, WITHOUT registering a scan source: the planner defers the row→batch
    /// encode into <c>ScanPlan.Execute</c>, so a schema/CLR-type/encode mismatch surfaces at execution as a
    /// <see cref="QueryExecutionStage.Scan"/>-attributed <see cref="UnsupportedPlanException"/> (#176 #6).
    /// </summary>
    /// <param name="schema">The authoritative relation schema.</param>
    /// <param name="rows">The inline rows (read positionally against <paramref name="schema"/> on the first action).</param>
    /// <returns>A base <see cref="DataFrame"/> over the local relation.</returns>
    public DataFrame LocalRelationFrame(StructType schema, IEnumerable<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rows);
        return new DataFrame(new LocalRelation(schema, rows));
    }

    /// <summary>Analyzes a DataFrame's logical plan through the real Core analyzer.</summary>
    /// <param name="frame">The DataFrame whose plan to analyze.</param>
    /// <returns>The analyzed logical plan.</returns>
    public LogicalPlan Analyze(DataFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new Analyzer(_catalog).Resolve(frame.Plan);
    }

    /// <summary>Plans a DataFrame into its physical operator tree (analyze + plan).</summary>
    /// <param name="frame">The DataFrame to plan.</param>
    /// <returns>The physical plan tree.</returns>
    public PhysicalPlan Plan(DataFrame frame) =>
        new PhysicalPlanner(_scanSource).Plan(Analyze(frame));

    /// <summary>Renders a DataFrame's physical plan string through a <see cref="LocalQueryExecutor"/>
    /// (analyze + <see cref="LocalQueryExecutor.ExplainPhysical"/>) — plans, never executes.</summary>
    /// <param name="frame">The DataFrame to explain.</param>
    /// <returns>The rendered physical-plan tree (or a diagnostic line for an unsupported plan).</returns>
    public string ExplainPhysical(DataFrame frame) =>
        new LocalQueryExecutor(_scanSource, ExecutionBackendOptions.Default).ExplainPhysical(Analyze(frame));

    /// <summary>
    /// Renders a DataFrame's physical plan through a <see cref="LocalQueryExecutor"/> backed by an
    /// <see cref="ExecutionSentinelScanSource"/> whose batches trip on any read, returning the tree and
    /// the sentinel's batch-access count. Pure physical planning stores only the batch reference, so a
    /// correct (non-executing) <see cref="LocalQueryExecutor.ExplainPhysical"/> leaves the count at 0.
    /// </summary>
    /// <param name="frame">The DataFrame to explain.</param>
    /// <returns>The rendered tree and the number of times the sentinel batches were accessed.</returns>
    public (string Tree, int BatchAccessCount) ExplainPhysicalWatched(DataFrame frame)
    {
        var sentinel = new ExecutionSentinelScanSource();
        var executor = new LocalQueryExecutor(sentinel, ExecutionBackendOptions.Default);
        string tree = executor.ExplainPhysical(Analyze(frame));
        return (tree, sentinel.BatchAccessCount);
    }

    /// <summary>
    /// Attempts to execute (<c>Collect</c>) a DataFrame through a <see cref="LocalQueryExecutor"/> backed
    /// by an <see cref="ExecutionSentinelScanSource"/>, returning the sentinel's batch-access count. Real
    /// execution reads the batches, so the count is <c>&gt; 0</c> (proving the sentinel discriminates
    /// execution from planning); the sentinel's trip exception is swallowed.
    /// </summary>
    /// <param name="frame">The DataFrame to execute against the sentinel source.</param>
    /// <returns>The number of times the sentinel batches were accessed during the execution attempt.</returns>
    public int CountBatchAccessesDuringCollect(DataFrame frame)
    {
        var sentinel = new ExecutionSentinelScanSource();
        var executor = new LocalQueryExecutor(sentinel, ExecutionBackendOptions.Default);
        try
        {
            _ = executor.Collect(Analyze(frame), ExecutionOptions.Default);
        }
        catch (Exception)
        {
            // Expected: executing the plan reads the sentinel batches, which trip. Any wrapping is fine —
            // the discriminator is the batch-access count, asserted by the caller.
        }

        return sentinel.BatchAccessCount;
    }

    /// <summary>Renders a DataFrame's physical plan through a <see cref="SparkSession"/>'s registered
    /// executor (full Core↔Executor seam).</summary>
    /// <param name="session">The session whose <c>QueryExecutor</c> renders the plan.</param>
    /// <param name="frame">The DataFrame to explain (its relation must be registered in the default source).</param>
    /// <returns>The rendered physical-plan tree.</returns>
    public string ExplainPhysicalViaSession(SparkSession session, DataFrame frame)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.QueryExecutor.ExplainPhysical(Analyze(frame));
    }

    /// <summary>Executes a DataFrame end-to-end and materializes all rows.</summary>
    /// <param name="frame">The DataFrame to collect.</param>
    /// <param name="options">Backend options (defaults to <see cref="ExecutionBackendOptions.Default"/>).</param>
    /// <returns>The materialized rows.</returns>
    public IReadOnlyList<Row> Collect(DataFrame frame, ExecutionBackendOptions? options = null) =>
        new LocalQueryExecutor(_scanSource, options ?? ExecutionBackendOptions.Default)
            .Collect(Analyze(frame), ExecutionOptions.Default);

    /// <summary>Executes a DataFrame end-to-end and counts rows without full materialization.</summary>
    /// <param name="frame">The DataFrame to count.</param>
    /// <param name="options">Backend options (defaults to <see cref="ExecutionBackendOptions.Default"/>).</param>
    /// <returns>The row count.</returns>
    public long Count(DataFrame frame, ExecutionBackendOptions? options = null) =>
        new LocalQueryExecutor(_scanSource, options ?? ExecutionBackendOptions.Default)
            .Count(Analyze(frame), ExecutionOptions.Default);

    /// <summary>
    /// Executes a DataFrame end-to-end under STORY-04.6.4 boundaries (cancellation/timeout/result bounds/
    /// memory budget) and returns the rows plus the planning/execution <see cref="ExecutionMetrics"/>. It
    /// is the fixture seam <c>DeltaSharp.Executor.Tests</c> drives the failure-mode tests through, since
    /// Core's <c>ExecutionOptions</c> is a Core internal the test assembly cannot name directly.
    /// </summary>
    /// <param name="frame">The DataFrame to collect.</param>
    /// <param name="cancellationToken">Cooperative cancellation observed at batch/row boundaries.</param>
    /// <param name="timeout">An optional execution timeout.</param>
    /// <param name="maxResultRows">An optional result row cap.</param>
    /// <param name="maxResultBytes">An optional result byte cap.</param>
    /// <param name="memoryBudgetBytes">An optional per-run operator memory budget.</param>
    /// <param name="backendOptions">Backend options (defaults to <see cref="ExecutionBackendOptions.Default"/>).</param>
    /// <returns>The materialized rows and the metrics gathered on success.</returns>
    public (IReadOnlyList<Row> Rows, ExecutionMetrics Metrics) CollectWithMetrics(
        DataFrame frame,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        long? maxResultRows = null,
        long? maxResultBytes = null,
        long? memoryBudgetBytes = null,
        ExecutionBackendOptions? backendOptions = null)
    {
        var options = BuildOptions(
            cancellationToken, timeout, maxResultRows, maxResultBytes, memoryBudgetBytes);
        var sink = new ExecutionMetricsSink();
        IReadOnlyList<Row> rows = new LocalQueryExecutor(_scanSource, backendOptions ?? ExecutionBackendOptions.Default)
            .Collect(Analyze(frame), options, sink);
        return (rows, sink.Metrics ?? ExecutionMetrics.Empty);
    }

    /// <summary>The <see cref="CollectWithMetrics"/> counterpart for <c>count</c>.</summary>
    /// <param name="frame">The DataFrame to count.</param>
    /// <param name="cancellationToken">Cooperative cancellation observed at batch boundaries.</param>
    /// <param name="timeout">An optional execution timeout.</param>
    /// <param name="memoryBudgetBytes">An optional per-run operator memory budget.</param>
    /// <param name="backendOptions">Backend options (defaults to <see cref="ExecutionBackendOptions.Default"/>).</param>
    /// <returns>The count and the metrics gathered on success.</returns>
    public (long Count, ExecutionMetrics Metrics) CountWithMetrics(
        DataFrame frame,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        long? memoryBudgetBytes = null,
        ExecutionBackendOptions? backendOptions = null)
    {
        var options = BuildOptions(cancellationToken, timeout, null, null, memoryBudgetBytes);
        var sink = new ExecutionMetricsSink();
        long count = new LocalQueryExecutor(_scanSource, backendOptions ?? ExecutionBackendOptions.Default)
            .Count(Analyze(frame), options, sink);
        return (count, sink.Metrics ?? ExecutionMetrics.Empty);
    }

    /// <summary>
    /// The <see cref="CollectWithMetrics"/> counterpart for the write door (STORY-04.6.3): it wraps
    /// <paramref name="frame"/> in an analyzed <c>WriteToSource</c> over a <see cref="SinkDescriptor"/>
    /// built from the supplied write intent, drives it through a <see cref="LocalQueryExecutor"/> bound to
    /// the given <paramref name="sinkFactory"/> (the data-out seam), and returns the AUTHORITATIVE
    /// committed row count the executor reports plus the run's metrics. It is the count-returning seam the
    /// executor tests assert the committed count (and the post-commit cancellation boundary) through,
    /// since Core's <c>ExecutionOptions</c>/<c>WriteToSource</c> are Core internals the test assembly
    /// cannot name directly.
    /// </summary>
    /// <param name="frame">The DataFrame whose rows are written.</param>
    /// <param name="format">The sink format (for example <c>"memory"</c>).</param>
    /// <param name="mode">The save mode.</param>
    /// <param name="path">The write target path.</param>
    /// <param name="sinkFactory">The data-out seam resolving the write intent to a local sink.</param>
    /// <param name="cancellationToken">Cooperative cancellation observed while the result drains.</param>
    /// <param name="partitionColumns">Optional partition columns carried on the descriptor.</param>
    /// <param name="options">Optional writer options carried on the descriptor (e.g. a <c>path</c> option).</param>
    /// <returns>The committed row count and the metrics gathered.</returns>
    public (long Count, ExecutionMetrics Metrics) WriteWithMetrics(
        DataFrame frame,
        string format,
        SaveMode mode,
        string? path,
        ILocalSinkFactory sinkFactory,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? partitionColumns = null,
        IReadOnlyDictionary<string, string>? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(sinkFactory);

        var sink = new SinkDescriptor(
            format, mode, path, tableIdentifier: null, partitionColumns: partitionColumns, options: options);
        LogicalPlan analyzed = new Analyzer(_catalog).Resolve(new WriteToSource(frame.Plan, sink));
        var execOptions = BuildOptions(cancellationToken, null, null, null, null);
        var metricsSink = new ExecutionMetricsSink();
        long count = new LocalQueryExecutor(_scanSource, ExecutionBackendOptions.Default, sinkFactory)
            .Write(analyzed, execOptions, metricsSink);
        return (count, metricsSink.Metrics ?? ExecutionMetrics.Empty);
    }

    private static ExecutionOptions BuildOptions(
        CancellationToken cancellationToken,
        TimeSpan? timeout,
        long? maxResultRows,
        long? maxResultBytes,
        long? memoryBudgetBytes) =>
        new()
        {
            CancellationToken = cancellationToken,
            Timeout = timeout,
            MaxResultRows = maxResultRows,
            MaxResultBytes = maxResultBytes,
            MemoryBudgetBytes = memoryBudgetBytes,
        };

    /// <summary>
    /// Runs a collect that is expected to <b>throw</b>, capturing the metrics the executor published into
    /// the per-call sink even though the action failed (the executor fills the sink in a <c>finally</c>).
    /// Returns the captured metrics and the thrown exception, so failure-path metrics (STORY-04.6.4
    /// criterion 4 — planning duration present, partial counters) can be asserted through the sink the
    /// <c>out</c>-metrics overloads read.
    /// </summary>
    /// <param name="frame">The DataFrame to collect.</param>
    /// <param name="cancellationToken">Cooperative cancellation observed at batch/row boundaries.</param>
    /// <param name="timeout">An optional execution timeout.</param>
    /// <param name="maxResultRows">An optional result row cap.</param>
    /// <param name="maxResultBytes">An optional result byte cap.</param>
    /// <param name="memoryBudgetBytes">An optional per-run operator memory budget.</param>
    /// <returns>The captured metrics (from the sink) and the exception the action threw, if any.</returns>
    public (ExecutionMetrics Metrics, Exception? Error) CollectCapturingMetrics(
        DataFrame frame,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        long? maxResultRows = null,
        long? maxResultBytes = null,
        long? memoryBudgetBytes = null)
    {
        var options = BuildOptions(cancellationToken, timeout, maxResultRows, maxResultBytes, memoryBudgetBytes);
        var sink = new ExecutionMetricsSink();
        Exception? error = null;
        try
        {
            _ = new LocalQueryExecutor(_scanSource, ExecutionBackendOptions.Default)
                .Collect(Analyze(frame), options, sink);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        return (sink.Metrics ?? ExecutionMetrics.Empty, error);
    }

    /// <summary>
    /// Collects using the process-wide shared <see cref="ExecutionOptions.Default"/> with a FRESH per-call
    /// metrics sink, so a concurrency test can prove two actions over the shared default options do not
    /// corrupt each other's metrics — the sink is per-call and the options carry no mutable per-run state
    /// (STORY-04.6.4 / #176 #4/#5). The plan is pre-analyzed by the caller to avoid analyzer concurrency.
    /// </summary>
    /// <param name="frame">The DataFrame to collect (analyzed here).</param>
    /// <returns>The materialized rows and the metrics gathered from this call's own sink.</returns>
    public (IReadOnlyList<Row> Rows, ExecutionMetrics Metrics) CollectViaDefaultOptions(DataFrame frame)
    {
        LogicalPlan analyzed = Analyze(frame);
        var sink = new ExecutionMetricsSink();
        IReadOnlyList<Row> rows = new LocalQueryExecutor(_scanSource, ExecutionBackendOptions.Default)
            .Collect(analyzed, ExecutionOptions.Default, sink);
        return (rows, sink.Metrics ?? ExecutionMetrics.Empty);
    }

    /// <summary>Collects a DataFrame through a <see cref="SparkSession"/>'s registered executor (full seam).</summary>
    /// <param name="session">The session whose <c>QueryExecutor</c> runs the plan.</param>
    /// <param name="frame">The DataFrame to collect (its relation must be registered in the default source).</param>
    /// <returns>The materialized rows.</returns>
    public IReadOnlyList<Row> CollectViaSession(SparkSession session, DataFrame frame)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.QueryExecutor.Collect(Analyze(frame), ExecutionOptions.Default);
    }

    /// <summary>
    /// Executes <paramref name="frame"/> against a <see cref="CancellationTriggerScanSource"/> that cancels
    /// the action in flight on the first batch read, capturing the thrown exception, the trigger's batch-
    /// access count (prompt-stop evidence), and — via a disposal-observing <see cref="PhysicalRuntime"/>
    /// factory — whether the executor disposed the run's runtime on the cancellation path. Drives
    /// STORY-04.6.4 (#176) criterion 1: the returned <c>RuntimeDisposed</c> is <see langword="false"/>
    /// (failing the caller's assertion) if <see cref="LocalQueryExecutor"/>'s disposal <c>finally</c> is removed.
    /// </summary>
    /// <param name="frame">A frame whose relation is registered schema-only, resolved by the trigger source.</param>
    /// <returns>The thrown exception, whether the runtime was disposed, and the trigger's access count.</returns>
    public (Exception? Error, bool RuntimeDisposed, int BatchAccessCount) RunInFlightCancelDisposalProbe(DataFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var trigger = new CancellationTriggerScanSource();
        PhysicalRuntime? captured = null;
        var executor = new LocalQueryExecutor(trigger, ExecutionBackendOptions.Default)
        {
            RuntimeFactory = (backend, options, token, budget) =>
            {
                captured = new PhysicalRuntime(backend, options, token, budget);
                return captured;
            },
        };

        var runOptions = BuildOptions(trigger.Token, null, null, null, null);
        Exception? error = null;
        try
        {
            _ = executor.Collect(Analyze(frame), runOptions);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        return (error, captured?.IsDisposed ?? false, trigger.AccessCount);
    }

    /// <summary>
    /// Collects <paramref name="frame"/> against an <see cref="ExecutionSentinelScanSource"/> whose batches
    /// throw a generic <see cref="ExecutionSentinelScanSource.BatchExecutedException"/> when the materializer
    /// reads them, returning the exception the executor surfaces. Because a bare scan returns the batch
    /// reference unread from the backend stage and the fault is neither cancellation, an unsupported plan,
    /// nor a result-limit breach, it exercises the executor's <b>general</b> Materialize catch (#176 #2):
    /// the surfaced exception is a <see cref="QueryExecutionException"/> with
    /// <see cref="QueryExecutionStage.Materialize"/> wrapping the sentinel exception as its root cause.
    /// </summary>
    /// <param name="frame">A bare-scan frame whose relation is registered schema-only.</param>
    /// <returns>The exception the executor surfaced (expected: a Materialize-attributed wrapper).</returns>
    public Exception CollectExpectingMaterializeFault(DataFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var sentinel = new ExecutionSentinelScanSource();
        var executor = new LocalQueryExecutor(sentinel, ExecutionBackendOptions.Default);
        try
        {
            _ = executor.Collect(Analyze(frame), ExecutionOptions.Default);
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected the sentinel materialization to fault, but the collect succeeded.");
    }
}

/// <summary>
/// A test-support <see cref="ILocalSinkFactory"/> decorator that invokes a hook AFTER each successful
/// <see cref="ILocalSink.Commit"/> (once the inner sink has committed and returned its count). It lets an
/// executor test fire a cancellation in the narrow post-commit window to prove the write path treats the
/// commit as its final failure boundary: a cancel/timeout observed only after the commit must NOT surface
/// a committed write as a cancelled/failed <c>Save</c> (MUST-FIX: post-commit cancellation window). Lives
/// in the Executor assembly because <see cref="ILocalSinkFactory"/>/<see cref="SinkDescriptor"/> are
/// internal to it and Core; the test assembly consumes it by name.
/// </summary>
internal sealed class PostCommitHookSinkFactory : ILocalSinkFactory
{
    private readonly ILocalSinkFactory _inner;
    private readonly Action _afterCommit;

    /// <summary>Wraps <paramref name="inner"/>, invoking <paramref name="afterCommit"/> after each commit.</summary>
    public PostCommitHookSinkFactory(ILocalSinkFactory inner, Action afterCommit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _afterCommit = afterCommit ?? throw new ArgumentNullException(nameof(afterCommit));
    }

    /// <inheritdoc/>
    public bool TryCreate(SinkDescriptor descriptor, StructType schema, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ILocalSink? sink)
    {
        if (_inner.TryCreate(descriptor, schema, out ILocalSink? innerSink))
        {
            sink = new HookSink(innerSink, _afterCommit);
            return true;
        }

        sink = null;
        return false;
    }

    private sealed class HookSink : ILocalSink
    {
        private readonly ILocalSink _inner;
        private readonly Action _afterCommit;

        public HookSink(ILocalSink inner, Action afterCommit)
        {
            _inner = inner;
            _afterCommit = afterCommit;
        }

        public long Commit(StructType schema, IReadOnlyList<Row> rows)
        {
            long written = _inner.Commit(schema, rows);
            _afterCommit();
            return written;
        }
    }
}
