using DeltaSharp.Types;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// A <b>resolved</b> column reference (Catalyst <c>AttributeReference</c>) — the analyzer's
/// replacement for an <see cref="UnresolvedAttribute"/>. It carries a resolved ADR-0008
/// <see cref="DataType"/>, a recorded nullability flag, and a stable <see cref="ExprId"/> identity,
/// demonstrating the unresolved→resolved transition the analyzer (FEAT-04.5) performs.
/// </summary>
internal sealed class AttributeReference : Expression
{
    /// <summary>Creates a resolved attribute reference.</summary>
    /// <param name="name">The (already-resolved) column name.</param>
    /// <param name="type">The resolved ADR-0008 type.</param>
    /// <param name="nullable">Whether the column is nullable.</param>
    /// <param name="exprId">The stable identity assigned by the analyzer.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    public AttributeReference(string name, DataType type, bool nullable, ExprId exprId)
        : base(PlanCollections.Empty<Expression>())
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(type);
        Name = name;
        Type = type;
        Nullable = nullable;
        ExprId = exprId;
    }

    /// <summary>The resolved column name.</summary>
    public string Name { get; }

    /// <inheritdoc/>
    public override DataType Type { get; }

    /// <inheritdoc/>
    public override bool Nullable { get; }

    /// <summary>The stable identity of this resolved attribute.</summary>
    public ExprId ExprId { get; }

    /// <inheritdoc/>
    public override string NodeName => "AttributeReference";

    /// <inheritdoc/>
    public override string SimpleString => $"{Name}#{ExprId}";

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 0, NodeName);
        return this;
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other)
    {
        var reference = (AttributeReference)other;
        return ExprId.Equals(reference.ExprId)
            && Nullable == reference.Nullable
            && string.Equals(Name, reference.Name, StringComparison.Ordinal)
            && Type.Equals(reference.Type);
    }

    /// <inheritdoc/>
    protected override int NodeHashCode()
    {
        int hash = PlanHash.Combine(PlanHash.Seed, PlanHash.OfString(Name));
        hash = PlanHash.Combine(hash, ExprId.Value.GetHashCode());
        hash = PlanHash.Combine(hash, Type.GetHashCode());
        return PlanHash.Combine(hash, Nullable ? 1 : 0);
    }
}

/// <summary>
/// An <b>unresolved</b> star (Catalyst <c>UnresolvedStar</c>) — bare <c>*</c> or a qualified
/// <c>t.*</c>. It expands to a column list at analysis, so it is always unresolved with no known
/// type.
/// </summary>
internal sealed class UnresolvedStar : Expression
{
    private readonly IReadOnlyList<string>? _target;

    /// <summary>Creates a star, optionally qualified by <paramref name="target"/> (for <c>t.*</c>).</summary>
    /// <param name="target">The qualifier parts, or <see langword="null"/> for a bare <c>*</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="target"/> is empty or has a null/empty
    /// element.</exception>
    public UnresolvedStar(IReadOnlyList<string>? target = null)
        : base(PlanCollections.Empty<Expression>())
    {
        _target = target is null ? null : PlanCollections.ToIdentifier(target, nameof(target));
    }

    /// <summary>The qualifier parts (for <c>t.*</c>), or <see langword="null"/> for a bare <c>*</c>.</summary>
    public IReadOnlyList<string>? Target => _target;

    /// <summary>A star is never resolved.</summary>
    public override bool Resolved => false;

    /// <inheritdoc/>
    public override string NodeName => "UnresolvedStar";

    /// <inheritdoc/>
    public override string SimpleString => _target is null ? "*" : $"{string.Join('.', _target)}.*";

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 0, NodeName);
        return this;
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other)
    {
        IReadOnlyList<string>? otherTarget = ((UnresolvedStar)other)._target;
        if (_target is null || otherTarget is null)
        {
            return _target is null && otherTarget is null;
        }

        return PlanCollections.StringSequenceEquals(_target, otherTarget);
    }

    /// <inheritdoc/>
    protected override int NodeHashCode() =>
        _target is null ? 0 : PlanHash.CombineStrings(PlanHash.Seed, _target);
}
