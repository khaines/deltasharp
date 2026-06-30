using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// A relational join of two child plans, recording the <see cref="JoinType"/> and an optional
/// <see cref="Condition"/> without reading either side. Spark parity: <c>Join</c>.
/// </summary>
internal sealed class Join : LogicalPlan
{
    /// <summary>Creates a join.</summary>
    public Join(LogicalPlan left, LogicalPlan right, JoinType joinType, Expression? condition = null)
    {
        Left = left ?? throw new ArgumentNullException(nameof(left));
        Right = right ?? throw new ArgumentNullException(nameof(right));
        JoinType = joinType;
        Condition = condition;
        _children = PlanCollections.AsReadOnly(Left, Right);
    }

    private readonly IReadOnlyList<LogicalPlan> _children;

    /// <summary>The left input plan.</summary>
    public LogicalPlan Left { get; }

    /// <summary>The right input plan.</summary>
    public LogicalPlan Right { get; }

    /// <summary>The join kind.</summary>
    public JoinType JoinType { get; }

    /// <summary>The optional join condition.</summary>
    public Expression? Condition { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<LogicalPlan> Children => _children;

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions =>
        Condition is null ? Array.Empty<Expression>() : new[] { Condition };

    /// <inheritdoc/>
    public override string NodeName => "Join";

    /// <inheritdoc/>
    public override string SimpleString =>
        Condition is null
            ? $"{UnresolvedPrefix}Join {JoinType}"
            : $"{UnresolvedPrefix}Join {JoinType}, ({Condition.SimpleString})";

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren)
    {
        (LogicalPlan left, LogicalPlan right) = PlanNodes.TwoChildren(newChildren, NodeName);
        return new Join(left, right, JoinType, Condition);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) =>
        other is Join join
        && JoinType == join.JoinType
        && ((Condition is null && join.Condition is null)
            || (Condition is not null && Condition.Equals(join.Condition)));

    /// <inheritdoc/>
    protected override int NodeHashCode()
    {
        int hash = PlanHash.Combine(PlanHash.Seed, (int)JoinType);
        return PlanHash.Combine(hash, Condition?.GetHashCode() ?? 0);
    }
}
