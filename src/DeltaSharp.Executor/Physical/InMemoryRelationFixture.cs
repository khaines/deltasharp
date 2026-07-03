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
