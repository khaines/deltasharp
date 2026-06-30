using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// A filter: retains child rows for which <see cref="Condition"/> is true. Target of
/// <c>filter</c>/<c>where</c>. Spark parity: <c>Filter</c>.
/// </summary>
internal sealed class Filter : LogicalPlan
{
    private readonly IReadOnlyList<Expression> _expressions;

    /// <summary>Creates a filter.</summary>
    public Filter(Expression condition, LogicalPlan child)
        : base(PlanCollections.AsReadOnly(child ?? throw new ArgumentNullException(nameof(child))))
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Child = child;
        _expressions = PlanCollections.AsReadOnly(Condition);
    }

    /// <summary>The predicate expression.</summary>
    public Expression Condition { get; }

    /// <summary>The input plan.</summary>
    public LogicalPlan Child { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions => _expressions;

    /// <inheritdoc/>
    public override string NodeName => "Filter";

    /// <inheritdoc/>
    public override string SimpleString => $"{UnresolvedPrefix}Filter ({Condition.SimpleString})";

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren) =>
        new Filter(Condition, PlanNodes.SingleChild(newChildren, NodeName));

    /// <inheritdoc/>
    public override LogicalPlan WithNewExpressions(IReadOnlyList<Expression> newExpressions) =>
        new Filter(PlanNodes.RequireExpressions(newExpressions, 1, NodeName)[0], Child);

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) =>
        other is Filter filter && Condition.Equals(filter.Condition);

    /// <inheritdoc/>
    protected override int NodeHashCode() =>
        PlanHash.Combine(PlanHash.Seed, Condition.GetHashCode());
}
