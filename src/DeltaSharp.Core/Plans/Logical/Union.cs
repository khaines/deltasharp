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
    {
        Inputs = PlanCollections.ToImmutable(inputs, nameof(inputs));
        if (Inputs.Count < 2)
        {
            throw new ArgumentException("Union requires at least two inputs.", nameof(inputs));
        }
    }

    /// <summary>The input plans, in order.</summary>
    public IReadOnlyList<LogicalPlan> Inputs { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<LogicalPlan> Children => Inputs;

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions => Array.Empty<Expression>();

    /// <inheritdoc/>
    public override string NodeName => "Union";

    /// <inheritdoc/>
    public override string SimpleString => $"{UnresolvedPrefix}Union";

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren) =>
        new Union(newChildren);

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) => other is Union;

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Seed;
}
