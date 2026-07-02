using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// A <b>resolved</b> logical source (Catalyst <c>LogicalRelation</c>) — the analyzer's replacement
/// for an <see cref="UnresolvedRelation"/> once the catalog has bound the by-name table identifier
/// to a concrete ADR-0008 schema. It is a leaf that carries the source <see cref="Identifier"/>,
/// the resolved <see cref="StructType"/> <see cref="Schema"/>, and the <see cref="Output"/>
/// attribute list the analyzer derived from that schema (each field becomes an
/// <see cref="AttributeReference"/> with a stable <see cref="ExprId"/>). It holds no reader, stream,
/// file handle, or backend object and performs no I/O — resolution binds metadata only (AC1, AC4).
/// </summary>
internal sealed class ResolvedRelation : LogicalPlan
{
    /// <summary>Creates a resolved relation.</summary>
    /// <param name="identifier">The multipart source identifier the catalog was keyed by.</param>
    /// <param name="schema">The resolved ADR-0008 schema.</param>
    /// <param name="output">The output attributes derived from <paramref name="schema"/>, one per
    /// field, in field order, each carrying a stable <see cref="ExprId"/>.</param>
    /// <param name="options">The read options carried over from the unresolved relation.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public ResolvedRelation(
        IReadOnlyList<string> identifier,
        StructType schema,
        IReadOnlyList<AttributeReference> output,
        IReadOnlyDictionary<string, string>? options = null)
        : base(PlanCollections.Empty<LogicalPlan>())
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(output);
        Identifier = PlanCollections.ToIdentifier(identifier, nameof(identifier));
        Schema = schema;
        Output = PlanCollections.ToImmutable(output, nameof(output));
        Options = PlanCollections.ToOptions(options);
    }

    /// <summary>The multipart source identifier (for example <c>["db", "t"]</c>).</summary>
    public IReadOnlyList<string> Identifier { get; }

    /// <summary>The resolved ADR-0008 schema (a <see cref="StructType"/>).</summary>
    public StructType Schema { get; }

    /// <summary>The resolved output attributes, one per schema field, in field order.</summary>
    public IReadOnlyList<AttributeReference> Output { get; }

    /// <summary>The read options carried over from the unresolved relation.</summary>
    public IReadOnlyDictionary<string, string> Options { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions => PlanCollections.Empty<Expression>();

    /// <inheritdoc/>
    public override string NodeName => "ResolvedRelation";

    /// <inheritdoc/>
    public override string SimpleString =>
        $"ResolvedRelation [{string.Join(".", Identifier)}], "
        + $"[{string.Join(", ", Output.Select(a => a.SimpleString))}]";

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren)
    {
        ArgumentNullException.ThrowIfNull(newChildren);
        if (newChildren.Count != 0)
        {
            throw new ArgumentException(
                "ResolvedRelation is a leaf and takes no children.", nameof(newChildren));
        }

        return this;
    }

    /// <inheritdoc/>
    public override LogicalPlan WithNewExpressions(IReadOnlyList<Expression> newExpressions)
    {
        PlanNodes.RequireNoExpressions(newExpressions, NodeName);
        return this;
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) =>
        other is ResolvedRelation relation
        && PlanCollections.StringSequenceEquals(Identifier, relation.Identifier)
        && Schema.Equals(relation.Schema)
        && PlanNodes.ExpressionsEqual(Output, relation.Output)
        && PlanCollections.OptionsEqual(Options, relation.Options);

    /// <inheritdoc/>
    protected override int NodeHashCode()
    {
        int hash = PlanHash.CombineStrings(PlanHash.Seed, Identifier);
        hash = PlanHash.Combine(hash, Schema.GetHashCode());
        hash = PlanNodes.HashExpressions(hash, Output);
        return PlanHash.Combine(hash, PlanHash.OfStringMap(Options));
    }
}
