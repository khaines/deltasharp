using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// A grouped aggregation: groups child rows by <see cref="GroupingExpressions"/> and computes
/// <see cref="AggregateExpressions"/>. Target of <c>groupBy(...).agg(...)</c>. Spark parity:
/// <c>Aggregate</c>.
/// </summary>
internal sealed class Aggregate : LogicalPlan
{
    private readonly IReadOnlyList<Expression> _expressions;

    /// <summary>Creates an aggregate.</summary>
    public Aggregate(
        IEnumerable<Expression> groupingExpressions,
        IEnumerable<Expression> aggregateExpressions,
        LogicalPlan child)
        : base(PlanCollections.AsReadOnly(child ?? throw new ArgumentNullException(nameof(child))))
    {
        GroupingExpressions =
            PlanCollections.ToImmutable(groupingExpressions, nameof(groupingExpressions));
        AggregateExpressions =
            PlanCollections.ToImmutable(aggregateExpressions, nameof(aggregateExpressions));
        Child = child;

        // Cache the combined grouping ⧺ aggregate view once: grouping first, then aggregate, so
        // WithNewExpressions can split it back deterministically.
        var combined = new Expression[GroupingExpressions.Count + AggregateExpressions.Count];
        int i = 0;
        foreach (Expression expression in GroupingExpressions)
        {
            combined[i++] = expression;
        }

        foreach (Expression expression in AggregateExpressions)
        {
            combined[i++] = expression;
        }

        _expressions = PlanCollections.AsReadOnly(combined);
    }

    /// <summary>The grouping expressions, in order.</summary>
    public IReadOnlyList<Expression> GroupingExpressions { get; }

    /// <summary>The aggregate output expressions, in order.</summary>
    public IReadOnlyList<Expression> AggregateExpressions { get; }

    /// <summary>The input plan.</summary>
    public LogicalPlan Child { get; }

    /// <inheritdoc/>
    /// <remarks>The grouping expressions followed by the aggregate expressions; the split point
    /// is <see cref="GroupingExpressions"/><c>.Count</c>.</remarks>
    public override IReadOnlyList<Expression> Expressions => _expressions;

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
    /// <remarks>Honours the grouping ⧺ aggregate split: the first
    /// <see cref="GroupingExpressions"/><c>.Count</c> elements become the new grouping
    /// expressions and the remainder the new aggregate expressions.</remarks>
    public override LogicalPlan WithNewExpressions(IReadOnlyList<Expression> newExpressions)
    {
        int groupingCount = GroupingExpressions.Count;
        IReadOnlyList<Expression> all = PlanNodes.RequireExpressions(
            newExpressions, groupingCount + AggregateExpressions.Count, NodeName);

        var grouping = new Expression[groupingCount];
        var aggregate = new Expression[all.Count - groupingCount];
        for (int i = 0; i < all.Count; i++)
        {
            if (i < groupingCount)
            {
                grouping[i] = all[i];
            }
            else
            {
                aggregate[i - groupingCount] = all[i];
            }
        }

        return new Aggregate(grouping, aggregate, Child);
    }

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
