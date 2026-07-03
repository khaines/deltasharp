using DeltaSharp.Analysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
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
            _ = executor.Collect(Analyze(frame));
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
        new LocalQueryExecutor(_scanSource, options ?? ExecutionBackendOptions.Default).Collect(Analyze(frame));

    /// <summary>Executes a DataFrame end-to-end and counts rows without full materialization.</summary>
    /// <param name="frame">The DataFrame to count.</param>
    /// <param name="options">Backend options (defaults to <see cref="ExecutionBackendOptions.Default"/>).</param>
    /// <returns>The row count.</returns>
    public long Count(DataFrame frame, ExecutionBackendOptions? options = null) =>
        new LocalQueryExecutor(_scanSource, options ?? ExecutionBackendOptions.Default).Count(Analyze(frame));

    /// <summary>Collects a DataFrame through a <see cref="SparkSession"/>'s registered executor (full seam).</summary>
    /// <param name="session">The session whose <c>QueryExecutor</c> runs the plan.</param>
    /// <param name="frame">The DataFrame to collect (its relation must be registered in the default source).</param>
    /// <returns>The materialized rows.</returns>
    public IReadOnlyList<Row> CollectViaSession(SparkSession session, DataFrame frame)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.QueryExecutor.Collect(Analyze(frame));
    }
}
