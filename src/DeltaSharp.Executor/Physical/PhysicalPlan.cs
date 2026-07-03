using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
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
    public string TreeString()
    {
        // Recursion-depth bound: this walk mirrors the physical tree, whose depth is INHERITED from
        // Core's TreeNode (MaxDepth=1000, rejected at logical-plan construction) times the planner's
        // ≤2× node multiplier (e.g. Distinct → Project-over-Aggregate), so no physical-side depth guard
        // is needed today. A physical-only build path (one that never builds the logical tree) or a
        // raised TreeNode.MaxDepth would remove that inherited bound and MUST add a physical depth guard.
        var builder = new System.Text.StringBuilder();
        GenerateTreeString(0, new List<bool>(), builder);
        return builder.ToString();
    }

    private void GenerateTreeString(int depth, List<bool> lastChildFlags, System.Text.StringBuilder builder)
    {
        if (depth > 0)
        {
            for (int i = 0; i < lastChildFlags.Count - 1; i++)
            {
                builder.Append(lastChildFlags[i] ? "   " : ":  ");
            }

            builder.Append(lastChildFlags[^1] ? "+- " : ":- ");
        }

        builder.Append(SimpleString);
        builder.Append('\n');

        IReadOnlyList<PhysicalPlan> children = Children;
        for (int i = 0; i < children.Count; i++)
        {
            lastChildFlags.Add(i == children.Count - 1);
            children[i].GenerateTreeString(depth + 1, lastChildFlags, builder);
            lastChildFlags.RemoveAt(lastChildFlags.Count - 1);
        }
    }

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
}

/// <summary>A leaf scan over in-memory batches supplied by an <see cref="IScanSource"/> (the M1 data-in door; the public read-door is STORY-04.1.2 / #158).</summary>
internal sealed class ScanPlan : PhysicalPlan
{
    private readonly IReadOnlyList<ColumnBatch> _batches;

    public ScanPlan(StructType outputSchema, IReadOnlyList<ColumnBatch> batches)
        : base(outputSchema)
    {
        _batches = batches ?? throw new ArgumentNullException(nameof(batches));
    }

    public override IReadOnlyList<PhysicalPlan> Children => Array.Empty<PhysicalPlan>();

    /// <inheritdoc/>
    public override string NodeName => "Scan";

    /// <inheritdoc/>
    public override string SimpleString => $"Scan {PhysicalPlanText.Columns(OutputSchema)}";

    public override BatchResult Execute(PhysicalRuntime runtime) => new(OutputSchema, _batches);
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
        PhysicalOperator op = BuildOperator(() => new FilterOperator(PhysicalRuntime.ScanOf(child), _predicate));
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
            () => new ProjectOperator(PhysicalRuntime.ScanOf(child), OutputSchema, _projections));
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
            () => new AggregateOperator(PhysicalRuntime.ScanOf(child), OutputSchema, _groupingKeys, _aggregates));
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
            PhysicalRuntime.ScanOf(left),
            PhysicalRuntime.ScanOf(right),
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
            () => new SortOperator(PhysicalRuntime.ScanOf(child), _sortOrders, _global));
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
            batches.AddRange(Normalize(result, runtime));
        }

        return new BatchResult(OutputSchema, batches);
    }

    private IReadOnlyList<ColumnBatch> Normalize(BatchResult result, PhysicalRuntime runtime)
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

        var op = new ProjectOperator(PhysicalRuntime.ScanOf(result), OutputSchema, projections);
        return runtime.Run(op);
    }
}
