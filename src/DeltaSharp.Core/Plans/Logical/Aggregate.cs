using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// A grouped aggregation: groups child rows by <see cref="GroupingExpressions"/> and computes
/// <see cref="AggregateExpressions"/>. Target of <c>groupBy(...).agg(...)</c>. Spark parity:
/// <c>Aggregate</c>.
/// </summary>
internal sealed class Aggregate : LogicalPlan
{
    /// <summary>Creates an aggregate.</summary>
    public Aggregate(
        IEnumerable<Expression> groupingExpressions,
        IEnumerable<Expression> aggregateExpressions,
        LogicalPlan child)
    {
        GroupingExpressions =
            PlanCollections.ToImmutable(groupingExpressions, nameof(groupingExpressions));
        AggregateExpressions =
            PlanCollections.ToImmutable(aggregateExpressions, nameof(aggregateExpressions));
        Child = child ?? throw new ArgumentNullException(nameof(child));
        _children = PlanCollections.AsReadOnly(Child);
    }

    private readonly IReadOnlyList<LogicalPlan> _children;

    /// <summary>The grouping expressions, in order.</summary>
    public IReadOnlyList<Expression> GroupingExpressions { get; }

    /// <summary>The aggregate output expressions, in order.</summary>
    public IReadOnlyList<Expression> AggregateExpressions { get; }

    /// <summary>The input plan.</summary>
    public LogicalPlan Child { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<LogicalPlan> Children => _children;

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions =>
        GroupingExpressions.Concat(AggregateExpressions).ToArray();

    /// <inheritdoc/>
    public override string NodeName => "Aggregate";

    /// <inheritdoc/>
    public override string SimpleString =>
        $"{UnresolvedPrefix}Aggregate {RenderList(GroupingExpressions)} {RenderList(AggregateExpressions)}";

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren) =>
        new Aggregate(
            GroupingExpressions, AggregateExpressions, PlanNodes.SingleChild(newChildren, NodeName));

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) =>
        other is Aggregate aggregate
        && PlanNodes.ExpressionsEqual(GroupingExpressions, aggregate.GroupingExpressions)
        && PlanNodes.ExpressionsEqual(AggregateExpressions, aggregate.AggregateExpressions);

    /// <inheritdoc/>
    protected override int NodeHashCode()
    {
        int hash = PlanNodes.HashExpressions(PlanHash.Seed, GroupingExpressions);
        return PlanNodes.HashExpressions(hash, AggregateExpressions);
    }
}
