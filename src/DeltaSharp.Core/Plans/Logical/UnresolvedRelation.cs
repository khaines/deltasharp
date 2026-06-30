using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// The unresolved <b>logical source descriptor</b>: a multipart table identifier plus read
/// options, before the analyzer binds it to a schema. It is a leaf, holds no schema, reader, or
/// handle, and is never resolved at construction (AC3, AC4). Spark parity:
/// <c>UnresolvedRelation</c>.
/// </summary>
internal sealed class UnresolvedRelation : LogicalPlan
{
    /// <summary>Creates an unresolved relation.</summary>
    /// <param name="identifier">The multipart table identifier (for example <c>["db", "t"]</c>).</param>
    /// <param name="options">Read options.</param>
    public UnresolvedRelation(
        IEnumerable<string> identifier,
        IReadOnlyDictionary<string, string>? options = null)
    {
        Identifier = PlanCollections.ToIdentifier(identifier, nameof(identifier));
        Options = PlanCollections.ToOptions(options);
    }

    /// <summary>The multipart table identifier, in order.</summary>
    public IReadOnlyList<string> Identifier { get; }

    /// <summary>The read options.</summary>
    public IReadOnlyDictionary<string, string> Options { get; }

    private static readonly IReadOnlyList<LogicalPlan> NoChildren =
        PlanCollections.AsReadOnly<LogicalPlan>();

    /// <inheritdoc/>
    public override IReadOnlyList<LogicalPlan> Children => NoChildren;

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions => Array.Empty<Expression>();

    /// <inheritdoc/>
    public override bool Resolved => false;

    /// <inheritdoc/>
    public override string NodeName => "UnresolvedRelation";

    /// <inheritdoc/>
    public override string SimpleString => $"{UnresolvedPrefix}UnresolvedRelation [{string.Join(".", Identifier)}]";

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren)
    {
        ArgumentNullException.ThrowIfNull(newChildren);
        if (newChildren.Count != 0)
        {
            throw new ArgumentException(
                "UnresolvedRelation is a leaf and takes no children.", nameof(newChildren));
        }

        return this;
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) =>
        other is UnresolvedRelation relation
        && PlanCollections.StringSequenceEquals(Identifier, relation.Identifier)
        && PlanCollections.OptionsEqual(Options, relation.Options);

    /// <inheritdoc/>
    protected override int NodeHashCode()
    {
        int hash = PlanHash.CombineStrings(PlanHash.Seed, Identifier);
        return PlanHash.Combine(hash, PlanHash.OfStringMap(Options));
    }
}
