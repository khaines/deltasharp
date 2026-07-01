using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// A row limit: returns at most <see cref="Count"/> rows of its child. The count is a literal
/// integer (not an expression). Target of <c>limit</c>. Spark parity: <c>Limit</c> (Spark splits
/// it into <c>GlobalLimit</c>/<c>LocalLimit</c> during planning — see the design doc).
/// </summary>
internal sealed class Limit : LogicalPlan
{
    /// <summary>Creates a limit.</summary>
    public Limit(int count, LogicalPlan child)
        : base(PlanCollections.AsReadOnly(child ?? throw new ArgumentNullException(nameof(child))))
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Count = count;
        Child = child;
    }

    /// <summary>The maximum number of rows to return (non-negative).</summary>
    public int Count { get; }

    /// <summary>The input plan.</summary>
    public LogicalPlan Child { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions => PlanCollections.Empty<Expression>();

    /// <inheritdoc/>
    public override string NodeName => "Limit";

    /// <inheritdoc/>
    public override string SimpleString => $"{UnresolvedPrefix}Limit {Count}";

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren) =>
        new Limit(Count, PlanNodes.SingleChild(newChildren, NodeName));

    /// <inheritdoc/>
    public override LogicalPlan WithNewExpressions(IReadOnlyList<Expression> newExpressions)
    {
        PlanNodes.RequireNoExpressions(newExpressions, NodeName);
        return this;
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) =>
        other is Limit limit && Count == limit.Count;

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Combine(PlanHash.Seed, Count);
}
