using DeltaSharp.Types;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// A named projection element (Catalyst <c>Alias</c>) — <c>child AS name</c>. It wraps a child
/// expression with a user-visible output name the analyzer preserves for name resolution. An alias
/// is resolved exactly when its child is, and forwards the child's type/nullability hints.
/// </summary>
internal sealed class Alias : Expression
{
    /// <summary>Creates <c><paramref name="child"/> AS <paramref name="name"/></c>.</summary>
    /// <param name="child">The aliased expression.</param>
    /// <param name="name">The output name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    public Alias(Expression child, string name)
        : base(Unary(child))
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <summary>The output name.</summary>
    public string Name { get; }

    /// <summary>The aliased expression.</summary>
    public Expression Child => Children[0];

    /// <inheritdoc/>
    public override DataType? Type => Child.Type;

    /// <inheritdoc/>
    public override bool Nullable => Child.Nullable;

    /// <inheritdoc/>
    public override string NodeName => "Alias";

    /// <inheritdoc/>
    public override string SimpleString => $"{Child.SimpleString} AS {Name}";

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 1, NodeName);
        return ReferenceEquals(newChildren[0], Child) ? this : new Alias(newChildren[0], Name);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other) =>
        string.Equals(Name, ((Alias)other).Name, StringComparison.Ordinal);

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Combine(PlanHash.Seed, PlanHash.OfString(Name));
}
