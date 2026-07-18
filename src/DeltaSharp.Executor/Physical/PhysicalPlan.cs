using System.Linq;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Plans;
using DeltaSharp.Types;
using EnginePhysicalExpression = DeltaSharp.Engine.Execution.PhysicalExpression;

namespace DeltaSharp.Executor;

/// <summary>
/// A node in the physical operator tree the <see cref="PhysicalPlanner"/> produces from an analyzed
/// logical plan. Each node knows its <see cref="OutputSchema"/> and can <see cref="Execute"/> to a
/// fully materialized <see cref="BatchResult"/> by driving the EPIC-03 backend (ADR-0001) — either by
/// building the mapped Engine <see cref="PhysicalOperator"/> over its child batches, or, for the two
/// operators EPIC-03 has no native node for (limit, union), by a small bridge transform.
/// </summary>
internal abstract class PhysicalPlan
{
    /// <summary>Creates a node with the given output schema.</summary>
    /// <param name="outputSchema">The schema of the rows this node produces.</param>
    protected PhysicalPlan(StructType outputSchema)
    {
        OutputSchema = outputSchema ?? throw new ArgumentNullException(nameof(outputSchema));
    }

    /// <summary>The schema (field names/types/nullability) of this node's output.</summary>
    public StructType OutputSchema { get; }

    /// <summary>This node's inputs, left-to-right.</summary>
    public abstract IReadOnlyList<PhysicalPlan> Children { get; }

    /// <summary>The operator name (for example <c>"Filter"</c>); a constant per node.</summary>
    public abstract string NodeName { get; }

    /// <summary>
    /// A one-line description of <b>this</b> node — its name and inline metadata (output columns, keys,
    /// predicate) — <b>excluding</b> child plans, which render as their own tree lines. Used by
    /// <see cref="TreeString"/> for <c>DataFrame.Explain</c>'s physical section (STORY-04.7.3).
    /// </summary>
    public abstract string SimpleString { get; }

    /// <summary>Executes the subtree rooted here to a fully materialized batch list.</summary>
    /// <param name="runtime">The backend + context driving the operators.</param>
    /// <returns>The output schema and batches.</returns>
    public abstract BatchResult Execute(PhysicalRuntime runtime);

    /// <summary>
    /// Renders this subtree as an indented, multi-line tree string mirroring the Core logical-plan
    /// renderer's connector format (<c>+-</c>/<c>:-</c>, see <c>TreeNode.TreeString</c>). This does
    /// <b>no</b> execution — it walks the already-built physical tree — so it preserves the lazy/eager
    /// invariant (ADR-0001).
    /// </summary>
    public string TreeString() =>
        // Recursion-depth bound: this walk mirrors the physical tree, whose depth is INHERITED from
        // Core's TreeNode (MaxDepth=1000, rejected at logical-plan construction) times the planner's
        // ≤2× node multiplier (e.g. Distinct → Project-over-Aggregate), so no physical-side depth guard
        // is needed today. A physical-only build path (one that never builds the logical tree) or a
        // raised TreeNode.MaxDepth would remove that inherited bound and MUST add a physical depth guard.
        TreeStringRenderer.Render(this, node => node.SimpleString, node => node.Children);

    /// <summary>Wraps an Engine operator build so its validation failures become deterministic diagnostics.</summary>
    /// <param name="build">Builds the Engine operator (may throw <see cref="ArgumentException"/>).</param>
    /// <returns>The built operator.</returns>
    /// <exception cref="UnsupportedPlanException">The operator could not be constructed (ill-typed bridge output).</exception>
    protected PhysicalOperator BuildOperator(Func<PhysicalOperator> build)
    {
        try
        {
            return build();
        }
        catch (ArgumentException ex)
        {
            throw new UnsupportedPlanException(
                $"{GetType().Name} could not build its EPIC-03 operator: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Whether <paramref name="plan"/> delivers <b>original source rows</b> to its parent without an
    /// intervening row-producing operator — a <see cref="ScanPlan"/>, or a <c>Limit</c>/<c>Union</c>
    /// bridge whose inputs all read source directly. The parent's <see cref="PhysicalRuntime.ScanOf"/>
    /// over such a child is the genuine source read that <see cref="PhysicalRuntime.BytesScanned"/> counts
    /// once. A child that is itself a mapped operator (Filter/Project/Aggregate/Join/Sort) already had its
    /// source counted below it, so the parent's re-scan of that intermediate is excluded. This keeps
    /// BytesScanned at the true source volume regardless of plan depth (no depth inflation), counts a
    /// union of two sources as their sum, and never zeroes when a Limit/Union bridge sits between the scan
    /// and the nearest operator (STORY-04.6.4 / #176 review).
    /// </summary>
    /// <param name="plan">The child plan to classify.</param>
    /// <returns><see langword="true"/> if the child reads source rows directly; otherwise <see langword="false"/>.</returns>
    internal static bool ReadsSourceDirectly(PhysicalPlan plan) => plan switch
    {
        ScanPlan => true,
        LimitPlan or UnionPlan => plan.Children.Count > 0 && plan.Children.All(ReadsSourceDirectly),
        _ => false,
    };
}

/// <summary>A leaf scan over in-memory batches supplied via a <b>lazy thunk</b> evaluated on first
/// <see cref="Execute"/> — this is how every <see cref="IScanSource"/> scan (a <c>LocalRelation</c>, an
/// in-memory catalog fixture, or a real Delta file read) defers row→batch encoding / data-plane I/O out of
/// physical planning so <see cref="PhysicalPlanner.Plan"/> (and thus #179 <c>Explain</c>) performs no
/// enumeration or I/O (STORY-04.1.2 / #158).</summary>
internal sealed class ScanPlan : PhysicalPlan
{
    private readonly Func<CancellationToken, IReadOnlyList<ColumnBatch>> _batchesFactory;
    private IReadOnlyList<ColumnBatch>? _batches;

    /// <summary>Creates a scan whose batches are produced lazily by <paramref name="batchesFactory"/> on
    /// first <see cref="Execute"/> (no enumeration/encoding happens at planning time). The factory receives
    /// the run's effective cancellation token so a slow/large deferred source can be cancelled/timed out
    /// while it drains (STORY-04.6.4 AC2).</summary>
    public ScanPlan(StructType outputSchema, Func<CancellationToken, IReadOnlyList<ColumnBatch>> batchesFactory)
        : base(outputSchema)
    {
        _batchesFactory = batchesFactory ?? throw new ArgumentNullException(nameof(batchesFactory));
    }

    public override IReadOnlyList<PhysicalPlan> Children => Array.Empty<PhysicalPlan>();

    /// <inheritdoc/>
    public override string NodeName => "Scan";

    /// <inheritdoc/>
    public override string SimpleString => $"Scan {PhysicalPlanText.Columns(OutputSchema)}";

    /// <inheritdoc/>
    public override BatchResult Execute(PhysicalRuntime runtime)
        => new(OutputSchema, _batches ??= _batchesFactory(runtime.CancellationToken));
}

/// <summary>Maps <c>Filter</c> to an EPIC-03 <see cref="FilterOperator"/>.</summary>
internal sealed class FilterPlan : PhysicalPlan
{
    private readonly PhysicalPlan _child;
    private readonly EnginePhysicalExpression _predicate;

    public FilterPlan(PhysicalPlan child, EnginePhysicalExpression predicate)
        : base(child.OutputSchema)
    {
        _child = child;
        _predicate = predicate;
    }

    /// <summary>The filter predicate the EPIC-03 <see cref="FilterOperator"/> evaluates.</summary>
    public EnginePhysicalExpression Predicate => _predicate;

    public override IReadOnlyList<PhysicalPlan> Children => [_child];

    /// <inheritdoc/>
    public override string NodeName => "Filter";

    /// <inheritdoc/>
    public override string SimpleString => $"Filter ({PhysicalPlanText.Expr(_predicate, _child.OutputSchema)})";

    public override BatchResult Execute(PhysicalRuntime runtime)
    {
        BatchResult child = _child.Execute(runtime);
        PhysicalOperator op = BuildOperator(() => new FilterOperator(runtime.ScanOf(child, ReadsSourceDirectly(_child)), _predicate));
        return new BatchResult(OutputSchema, runtime.Run(op));
    }
}

/// <summary>Maps <c>Project</c> to an EPIC-03 <see cref="ProjectOperator"/>.</summary>
internal sealed class ProjectPlan : PhysicalPlan
{
    private readonly PhysicalPlan _child;
    private readonly IReadOnlyList<EnginePhysicalExpression> _projections;

    public ProjectPlan(PhysicalPlan child, StructType outputSchema, IReadOnlyList<EnginePhysicalExpression> projections)
        : base(outputSchema)
    {
        _child = child;
        _projections = projections;
    }

    /// <summary>The projection expressions, one per output field.</summary>
    public IReadOnlyList<EnginePhysicalExpression> Projections => _projections;

    public override IReadOnlyList<PhysicalPlan> Children => [_child];

    /// <inheritdoc/>
    public override string NodeName => "Project";

    /// <inheritdoc/>
    public override string SimpleString =>
        $"Project {PhysicalPlanText.ProjectionList(_projections, _child.OutputSchema, OutputSchema)}";

    public override BatchResult Execute(PhysicalRuntime runtime)
    {
        BatchResult child = _child.Execute(runtime);
        PhysicalOperator op = BuildOperator(
            () => new ProjectOperator(runtime.ScanOf(child, ReadsSourceDirectly(_child)), OutputSchema, _projections));
        return new BatchResult(OutputSchema, runtime.Run(op));
    }
}

/// <summary>Maps <c>Aggregate</c> to an EPIC-03 <see cref="AggregateOperator"/> (grouping keys ⧺ aggregates).</summary>
internal sealed class AggregatePlan : PhysicalPlan
{
    private readonly PhysicalPlan _child;
    private readonly IReadOnlyList<EnginePhysicalExpression> _groupingKeys;
    private readonly IReadOnlyList<AggregateExpression> _aggregates;

    public AggregatePlan(
        PhysicalPlan child,
        StructType outputSchema,
        IReadOnlyList<EnginePhysicalExpression> groupingKeys,
        IReadOnlyList<AggregateExpression> aggregates)
        : base(outputSchema)
    {
        _child = child;
        _groupingKeys = groupingKeys;
        _aggregates = aggregates;
    }

    /// <summary>The grouping-key expressions (leading output columns, Spark <c>retainGroupColumns</c>).</summary>
    public IReadOnlyList<EnginePhysicalExpression> GroupingKeys => _groupingKeys;

    /// <summary>The aggregate terms (trailing output columns).</summary>
    public IReadOnlyList<AggregateExpression> Aggregates => _aggregates;

    public override IReadOnlyList<PhysicalPlan> Children => [_child];

    /// <inheritdoc/>
    public override string NodeName => "Aggregate";

    /// <inheritdoc/>
    public override string SimpleString =>
        $"Aggregate keys={PhysicalPlanText.ExprList(_groupingKeys, _child.OutputSchema)}, "
        + $"functions={PhysicalPlanText.AggregateList(_aggregates, _child.OutputSchema)}";

    public override BatchResult Execute(PhysicalRuntime runtime)
    {
        BatchResult child = _child.Execute(runtime);
        PhysicalOperator op = BuildOperator(
            () => new AggregateOperator(runtime.ScanOf(child, ReadsSourceDirectly(_child)), OutputSchema, _groupingKeys, _aggregates));
        return new BatchResult(OutputSchema, runtime.Run(op));
    }
}

/// <summary>Maps <c>Join</c> to an EPIC-03 <see cref="JoinOperator"/> (equi-join keys extracted from the condition).</summary>
internal sealed class JoinPlan : PhysicalPlan
{
    private readonly PhysicalPlan _left;
    private readonly PhysicalPlan _right;
    private readonly IReadOnlyList<EnginePhysicalExpression> _leftKeys;
    private readonly IReadOnlyList<EnginePhysicalExpression> _rightKeys;

    public JoinPlan(
        PhysicalPlan left,
        PhysicalPlan right,
        StructType outputSchema,
        JoinType joinType,
        IReadOnlyList<EnginePhysicalExpression> leftKeys,
        IReadOnlyList<EnginePhysicalExpression> rightKeys)
        : base(outputSchema)
    {
        _left = left;
        _right = right;
        JoinType = joinType;
        _leftKeys = leftKeys;
        _rightKeys = rightKeys;
    }

    /// <summary>The mapped Engine join shape.</summary>
    public JoinType JoinType { get; }

    /// <summary>Left-side equi-join keys.</summary>
    public IReadOnlyList<EnginePhysicalExpression> LeftKeys => _leftKeys;

    /// <summary>Right-side equi-join keys, pairwise matched to <see cref="LeftKeys"/>.</summary>
    public IReadOnlyList<EnginePhysicalExpression> RightKeys => _rightKeys;

    public override IReadOnlyList<PhysicalPlan> Children => [_left, _right];

    /// <inheritdoc/>
    public override string NodeName => "Join";

    /// <inheritdoc/>
    public override string SimpleString =>
        $"Join {JoinType} {PhysicalPlanText.ExprList(_leftKeys, _left.OutputSchema)} = {PhysicalPlanText.ExprList(_rightKeys, _right.OutputSchema)}";

    public override BatchResult Execute(PhysicalRuntime runtime)
    {
        BatchResult left = _left.Execute(runtime);
        BatchResult right = _right.Execute(runtime);
        PhysicalOperator op = BuildOperator(() => new JoinOperator(
            runtime.ScanOf(left, ReadsSourceDirectly(_left)),
            runtime.ScanOf(right, ReadsSourceDirectly(_right)),
            OutputSchema,
            JoinType,
            _leftKeys,
            _rightKeys));
        return new BatchResult(OutputSchema, runtime.Run(op));
    }
}

/// <summary>Maps <c>Sort</c> to an EPIC-03 <see cref="SortOperator"/>.</summary>
internal sealed class SortPlan : PhysicalPlan
{
    private readonly PhysicalPlan _child;
    private readonly IReadOnlyList<SortOrder> _sortOrders;
    private readonly bool _global;

    public SortPlan(PhysicalPlan child, IReadOnlyList<SortOrder> sortOrders, bool global)
        : base(child.OutputSchema)
    {
        _child = child;
        _sortOrders = sortOrders;
        _global = global;
    }

    /// <summary>The sort keys, in precedence order.</summary>
    public IReadOnlyList<SortOrder> SortOrders => _sortOrders;

    public override IReadOnlyList<PhysicalPlan> Children => [_child];

    /// <inheritdoc/>
    public override string NodeName => "Sort";

    /// <inheritdoc/>
    public override string SimpleString =>
        $"Sort {PhysicalPlanText.SortList(_sortOrders, _child.OutputSchema)}{(_global ? " global" : string.Empty)}";

    public override BatchResult Execute(PhysicalRuntime runtime)
    {
        BatchResult child = _child.Execute(runtime);
        PhysicalOperator op = BuildOperator(
            () => new SortOperator(runtime.ScanOf(child, ReadsSourceDirectly(_child)), _sortOrders, _global));
        return new BatchResult(OutputSchema, runtime.Run(op));
    }
}

/// <summary>
/// A bridge for <c>Limit</c>: EPIC-03 has no limit operator, so this node emits at most <c>N</c>
/// logical rows by truncating the child's materialized batches (respecting any selection vector).
/// </summary>
internal sealed class LimitPlan : PhysicalPlan
{
    private readonly PhysicalPlan _child;
    private readonly int _count;

    public LimitPlan(PhysicalPlan child, int count)
        : base(child.OutputSchema)
    {
        _child = child;
        _count = count;
    }

    /// <summary>The maximum number of rows to emit.</summary>
    public int Count => _count;

    public override IReadOnlyList<PhysicalPlan> Children => [_child];

    /// <inheritdoc/>
    public override string NodeName => "Limit";

    /// <inheritdoc/>
    public override string SimpleString => $"Limit {_count}";

    public override BatchResult Execute(PhysicalRuntime runtime)
    {
        BatchResult child = _child.Execute(runtime);
        var limited = new List<ColumnBatch>();
        int remaining = _count;
        foreach (ColumnBatch batch in child.Batches)
        {
            if (remaining <= 0)
            {
                break;
            }

            int available = batch.LogicalRowCount;
            if (available <= remaining)
            {
                limited.Add(batch);
                remaining -= available;
            }
            else
            {
                // WithSelection indices address current logical rows, so Range(remaining) keeps the
                // first `remaining` logical rows whether or not the batch already carries a selection.
                limited.Add(batch.WithSelection(SelectionVector.Range(remaining)));
                remaining = 0;
            }
        }

        return new BatchResult(OutputSchema, limited);
    }
}

/// <summary>
/// A bridge for <c>Union</c> (UNION ALL): EPIC-03 has no union operator, so this node concatenates
/// its children's batches. The output takes the first input's schema (Spark set-op widening is
/// TODO(#392)); a second input whose column types differ is a deterministic
/// <see cref="UnsupportedPlanException"/>, and one whose names differ is renamed via an identity
/// projection so downstream operators see a single consistent schema.
/// </summary>
internal sealed class UnionPlan : PhysicalPlan
{
    private readonly IReadOnlyList<PhysicalPlan> _children;

    public UnionPlan(StructType outputSchema, IReadOnlyList<PhysicalPlan> children)
        : base(outputSchema)
    {
        _children = children;
    }

    public override IReadOnlyList<PhysicalPlan> Children => _children;

    /// <inheritdoc/>
    public override string NodeName => "Union";

    /// <inheritdoc/>
    public override string SimpleString => $"Union {PhysicalPlanText.Columns(OutputSchema)}";

    public override BatchResult Execute(PhysicalRuntime runtime)
    {
        var batches = new List<ColumnBatch>();
        foreach (PhysicalPlan child in _children)
        {
            BatchResult result = child.Execute(runtime);
            batches.AddRange(Normalize(result, child, runtime));
        }

        return new BatchResult(OutputSchema, batches);
    }

    private IReadOnlyList<ColumnBatch> Normalize(BatchResult result, PhysicalPlan childPlan, PhysicalRuntime runtime)
    {
        if (result.Schema.Equals(OutputSchema))
        {
            return result.Batches;
        }

        if (result.Schema.Count != OutputSchema.Count)
        {
            throw UnsupportedPlanException.ForNode(
                "Union", $"inputs have different column counts ({result.Schema.Count} vs {OutputSchema.Count})");
        }

        for (int i = 0; i < OutputSchema.Count; i++)
        {
            if (!result.Schema[i].DataType.Equals(OutputSchema[i].DataType))
            {
                throw UnsupportedPlanException.ForNode(
                    "Union",
                    $"column {i} types differ ('{result.Schema[i].DataType.SimpleString}' vs "
                    + $"'{OutputSchema[i].DataType.SimpleString}'); union type coercion is deferred (#392)");
            }
        }

        // Types align but field names differ: rename via an identity projection to the target schema.
        var projections = new EnginePhysicalExpression[OutputSchema.Count];
        for (int i = 0; i < OutputSchema.Count; i++)
        {
            projections[i] = new ColumnReference(i, OutputSchema[i].DataType, OutputSchema[i].Nullable);
        }

        // The rename is an internal schema-normalization re-scan (identity projection), NOT a counted
        // source read: whatever operator consumes the Union's output attributes the source bytes via its
        // own ScanOf (ReadsSourceDirectly(union) is true when the branches read source directly), so
        // marking this scan as source too would double-count the renamed branch.
        var op = new ProjectOperator(runtime.ScanOf(result, readsSourceDirectly: false), OutputSchema, projections);
        return runtime.Run(op);
    }
}

/// <summary>
/// Executes a <b>write intent</b> (STORY-04.6.3 / #175): it drains its child subtree, materializes the
/// child rows once, and atomically <see cref="ILocalSink.Commit"/>s them to the resolved local sink,
/// honoring the descriptor's <see cref="SaveMode"/>. It is the physical mirror of a leaf
/// <see cref="ScanPlan"/> at the top of the tree — a sink instead of a source — and is the only physical
/// node that produces a side effect. The commit is atomic (materialize-then-commit), so a mode conflict
/// or a mid-write fault leaves no partial output (AC1/AC3). It captures the sink's <b>authoritative</b>
/// committed row count on <see cref="CommittedRowCount"/> (the value <see cref="ILocalSink.Commit"/>
/// returns — 0 when a <see cref="SaveMode.Ignore"/> skipped an existing target) so the driver's finalize
/// reports the rows actually WRITTEN, not the rows the child produced, without a second execution or any
/// cancellable post-commit work. It returns its child's <see cref="BatchResult"/> unchanged.
/// <para>
/// <b>Early existence short-circuit.</b> For <see cref="SaveMode.Ignore"/>/<see cref="SaveMode.ErrorIfExists"/>
/// onto an ALREADY-EXISTING target, <see cref="Execute"/> probes the sink (<see cref="ILocalSink.ShouldSkipOrThrow"/>)
/// and short-circuits BEFORE executing/materializing the child — an Ignore returns an empty result with
/// <see cref="CommittedRowCount"/> 0, an ErrorIfExists throws its conflict — so a doomed or skipped write
/// never reads the whole input (Spark-parity + OOM safety). This is only an optimization:
/// <see cref="ILocalSink.Commit"/> stays the atomic boundary and re-checks existence, so a race that creates
/// the target after the probe is still caught at commit.
/// </para>
/// </summary>
internal sealed class WriteToSinkPlan : PhysicalPlan
{
    private readonly PhysicalPlan _child;
    private readonly ILocalSink _sink;
    private readonly DeltaSharp.Plans.Logical.SinkDescriptor _descriptor;

    /// <summary>Creates a write node draining <paramref name="child"/> into <paramref name="sink"/>.</summary>
    /// <param name="child">The subtree whose rows are written.</param>
    /// <param name="sink">The resolved local sink the rows commit to.</param>
    /// <param name="descriptor">The logical sink descriptor (for the rendered node metadata; path redacted).</param>
    public WriteToSinkPlan(PhysicalPlan child, ILocalSink sink, DeltaSharp.Plans.Logical.SinkDescriptor descriptor)
        : base(child.OutputSchema)
    {
        _child = child ?? throw new ArgumentNullException(nameof(child));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public override IReadOnlyList<PhysicalPlan> Children => [_child];

    /// <inheritdoc/>
    public override string NodeName => "WriteToSink";

    /// <summary>
    /// The authoritative number of rows the sink committed, captured from <see cref="ILocalSink.Commit"/>
    /// after <see cref="Execute"/> runs (<see langword="null"/> before). It is 0 when a
    /// <see cref="SaveMode.Ignore"/> skipped an existing target, so it is NOT the same as the child's row
    /// count. The driver's write finalize returns this value (never a post-commit re-count), so a skipped
    /// write reports 0 and no cancellable work runs after the commit boundary.
    /// </summary>
    public long? CommittedRowCount { get; private set; }

    /// <inheritdoc/>
    // SinkDescriptor.SimpleString already redacts a credential-bearing path, so rendering a write node in
    // #179 Explain never leaks a secret (#432).
    public override string SimpleString => $"WriteToSink {_descriptor.SimpleString}";

    public override BatchResult Execute(PhysicalRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        // Early existence short-circuit (optimization; Spark parity). Decide an Ignore/ErrorIfExists write
        // onto an ALREADY-EXISTING target BEFORE executing/materializing the child, so a doomed
        // (ErrorIfExists) or skipped (Ignore) write never reads or materializes the whole DataFrame just to
        // throw or return 0 — avoiding an OOM risk on large inputs and matching Spark, which checks
        // existence before running the job for these modes. ShouldSkipOrThrow throws the ErrorIfExists
        // conflict (before any child work), or returns true for an Ignore that must skip; Append/Overwrite
        // (and a fresh target) return false and execute normally below. This is only an optimization —
        // Commit REMAINS the atomic final boundary and re-checks existence under its monitor, so a race that
        // creates the target between this probe and the commit is still caught at commit.
        if (_sink.ShouldSkipOrThrow())
        {
            // SaveMode.Ignore onto an existing target: skip the write entirely — no child execution, no
            // materialization — reporting 0 committed rows (the authoritative count) and an EMPTY result
            // over the child schema (no batches) so the driver's finalize reads 0 without re-executing.
            CommittedRowCount = 0;
            return new BatchResult(OutputSchema, Array.Empty<ColumnBatch>());
        }

        BatchResult child = _child.Execute(runtime);

        // Materialize the whole result once, then commit atomically: the sink never observes a
        // half-written result, so a mode conflict (ErrorIfExists) or a fault commits nothing (AC1/AC3).
        // Cancellation/faults are only possible up to and including the commit — the commit is the final
        // failure boundary. Capture the sink's authoritative committed count and do NO cancellable or
        // fault-prone work afterwards, so a cancel/timeout in the post-commit window can never surface a
        // committed write as a failed Save (MUST-FIX: post-commit cancellation window).
        IReadOnlyList<Row> rows = RowMaterializer.Materialize(child, maxRows: null, maxBytes: null, runtime.CancellationToken);
        CommittedRowCount = _sink.Commit(child.Schema, rows, runtime.MemoryBudgetBytes);

        // Return the child result so the driver's finalize can read CommittedRowCount (no re-execution and
        // no post-commit row count that could re-poll the cancellation token).
        return child;
    }
}
