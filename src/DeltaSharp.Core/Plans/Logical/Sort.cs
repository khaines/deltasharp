using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// An ordering: sorts child rows by <see cref="Order"/>. <see cref="Global"/> distinguishes a
/// total order (<c>orderBy</c>) from a per-partition order (<c>sortWithinPartitions</c>). Spark
/// parity: <c>Sort</c>.
/// </summary>
internal sealed class Sort : LogicalPlan
{
    /// <summary>Creates a sort.</summary>
    /// <param name="order">The ordering expressions (<c>SortOrder</c> once #168 lands).</param>
    /// <param name="global">Whether the ordering is global (total) rather than per-partition.</param>
    /// <param name="child">The input plan.</param>
    public Sort(IEnumerable<Expression> order, bool global, LogicalPlan child)
    {
        Order = PlanCollections.ToImmutable(order, nameof(order));
        Global = global;
        Child = child ?? throw new ArgumentNullException(nameof(child));
        _children = PlanCollections.AsReadOnly(Child);
    }

    private readonly IReadOnlyList<LogicalPlan> _children;

    /// <summary>The ordering expressions, in order.</summary>
    public IReadOnlyList<Expression> Order { get; }

    /// <summary>Whether the ordering is global (total) rather than per-partition.</summary>
    public bool Global { get; }

    /// <summary>The input plan.</summary>
    public LogicalPlan Child { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<LogicalPlan> Children => _children;

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions => Order;

    /// <inheritdoc/>
    public override string NodeName => "Sort";

    /// <inheritdoc/>
    public override string SimpleString =>
        $"{UnresolvedPrefix}Sort {RenderList(Order)}, global={(Global ? "true" : "false")}";

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren) =>
        new Sort(Order, Global, PlanNodes.SingleChild(newChildren, NodeName));

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) =>
        other is Sort sort && Global == sort.Global
        && PlanNodes.ExpressionsEqual(Order, sort.Order);

    /// <inheritdoc/>
    protected override int NodeHashCode()
    {
        int hash = PlanHash.Combine(PlanHash.Seed, Global ? 1 : 0);
        return PlanNodes.HashExpressions(hash, Order);
    }
}
