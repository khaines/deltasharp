namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// An unresolved column reference — a (possibly multipart) name that the analyzer (FEAT-04.5)
/// later binds to a resolved attribute with a stable id, type, and nullability. Spark parity:
/// <c>UnresolvedAttribute</c>.
/// </summary>
/// <remarks>It is a leaf expression and is never resolved at construction (it renders with a
/// leading apostrophe), satisfying the unresolved-before-analysis invariant (AC4).</remarks>
internal sealed class UnresolvedAttribute : Expression
{
    /// <summary>Creates an unresolved attribute from its name parts (for example
    /// <c>["t", "a"]</c> for <c>t.a</c>).</summary>
    public UnresolvedAttribute(IEnumerable<string> nameParts)
        : base(PlanCollections.Empty<Expression>())
    {
        NameParts = PlanCollections.ToIdentifier(nameParts, nameof(nameParts));
    }

    /// <summary>Convenience overload for a single-part name.</summary>
    public UnresolvedAttribute(string name)
        : this(new[] { name })
    {
    }

    /// <summary>The reference name parts, in order.</summary>
    public IReadOnlyList<string> NameParts { get; }

    /// <inheritdoc/>
    public override string NodeName => "UnresolvedAttribute";

    /// <inheritdoc/>
    public override bool Resolved => false;

    /// <inheritdoc/>
    public override string SimpleString => "'" + string.Join(".", NameParts);

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        ArgumentNullException.ThrowIfNull(newChildren);
        if (newChildren.Count != 0)
        {
            throw new ArgumentException(
                "UnresolvedAttribute is a leaf and takes no children.", nameof(newChildren));
        }

        return this;
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other) =>
        other is UnresolvedAttribute attribute
        && PlanCollections.StringSequenceEquals(NameParts, attribute.NameParts);

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.CombineStrings(PlanHash.Seed, NameParts);
}
