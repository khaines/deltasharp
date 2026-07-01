using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// Deduplicates its child's rows. Target of <c>distinct</c>. Spark parity: <c>Distinct</c> (the
/// analyzer later rewrites it to an <see cref="Aggregate"/> — not at construction).
/// </summary>
internal sealed class Distinct : LogicalPlan
{
    /// <summary>Creates a distinct.</summary>
    public Distinct(LogicalPlan child)
        : base(PlanCollections.AsReadOnly(child ?? throw new ArgumentNullException(nameof(child))))
    {
        Child = child;
    }

    /// <summary>The input plan.</summary>
    public LogicalPlan Child { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions => PlanCollections.Empty<Expression>();

    /// <inheritdoc/>
    public override string NodeName => "Distinct";

    /// <inheritdoc/>
    public override string SimpleString => $"{UnresolvedPrefix}Distinct";

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren) =>
        new Distinct(PlanNodes.SingleChild(newChildren, NodeName));

    /// <inheritdoc/>
    public override LogicalPlan WithNewExpressions(IReadOnlyList<Expression> newExpressions)
    {
        PlanNodes.RequireNoExpressions(newExpressions, NodeName);
        return this;
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) => other is Distinct;

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Seed;
}
