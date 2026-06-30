using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// A bag union (no deduplication) of two or more input plans. Target of
/// <c>union</c>/<c>unionByName</c>. Modelled N-ary for flat chaining. Spark parity:
/// <c>Union</c>.
/// </summary>
internal sealed class Union : LogicalPlan
{
    /// <summary>Creates a union of at least two inputs.</summary>
    public Union(IEnumerable<LogicalPlan> inputs)
        : base(BuildInputs(inputs))
    {
    }

    private static IReadOnlyList<LogicalPlan> BuildInputs(IEnumerable<LogicalPlan> inputs)
    {
        IReadOnlyList<LogicalPlan> copied = PlanCollections.ToImmutable(inputs, nameof(inputs));
        if (copied.Count < 2)
        {
            throw new ArgumentException("Union requires at least two inputs.", nameof(inputs));
        }

        return copied;
    }

    /// <summary>The input plans, in order.</summary>
    public IReadOnlyList<LogicalPlan> Inputs => Children;

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions => PlanCollections.Empty<Expression>();

    /// <inheritdoc/>
    public override string NodeName => "Union";

    /// <inheritdoc/>
    public override string SimpleString => $"{UnresolvedPrefix}Union";

    /// <inheritdoc/>
    /// <remarks>The new children must match the original arity (same count and positions), per the
    /// <see cref="TreeNode{TNode}.WithNewChildren"/> contract.</remarks>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren)
    {
        ArgumentNullException.ThrowIfNull(newChildren);
        if (newChildren.Count != Inputs.Count)
        {
            throw new ArgumentException(
                $"Union expects exactly {Inputs.Count} children (its original arity) but got "
                + $"{newChildren.Count}.",
                nameof(newChildren));
        }

        return new Union(newChildren);
    }

    /// <inheritdoc/>
    public override LogicalPlan WithNewExpressions(IReadOnlyList<Expression> newExpressions)
    {
        PlanNodes.RequireNoExpressions(newExpressions, NodeName);
        return this;
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) => other is Union;

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Seed;
}
