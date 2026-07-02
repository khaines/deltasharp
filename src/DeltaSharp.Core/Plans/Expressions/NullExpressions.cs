using DeltaSharp.Types;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// A null predicate (Catalyst <c>IsNull</c>) — <c>child IS NULL</c>. Unlike value expressions it
/// <b>never</b> yields SQL <c>NULL</c>: it inspects the operand's validity and always produces a
/// defined <see cref="BooleanType"/>, so its nullability hint is <see langword="false"/>.
/// </summary>
internal sealed class IsNull : Expression
{
    /// <summary>Creates <c><paramref name="child"/> IS NULL</c>.</summary>
    /// <param name="child">The operand whose validity is tested.</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    public IsNull(Expression child)
        : base(Unary(child))
    {
    }

    /// <summary>The tested operand.</summary>
    public Expression Child => Children[0];

    /// <inheritdoc/>
    public override DataType Type => BooleanType.Instance;

    /// <inheritdoc/>
    public override bool Nullable => false;

    /// <inheritdoc/>
    public override string NodeName => "IsNull";

    /// <inheritdoc/>
    public override string SimpleString => $"({Child.SimpleString} IS NULL)";

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 1, NodeName);
        return ReferenceEquals(newChildren[0], Child) ? this : new IsNull(newChildren[0]);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other) => true;

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Seed;
}

/// <summary>
/// A null predicate (Catalyst <c>IsNotNull</c>) — <c>child IS NOT NULL</c>. Always produces a
/// defined <see cref="BooleanType"/>; its nullability hint is <see langword="false"/>.
/// </summary>
internal sealed class IsNotNull : Expression
{
    /// <summary>Creates <c><paramref name="child"/> IS NOT NULL</c>.</summary>
    /// <param name="child">The operand whose validity is tested.</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    public IsNotNull(Expression child)
        : base(Unary(child))
    {
    }

    /// <summary>The tested operand.</summary>
    public Expression Child => Children[0];

    /// <inheritdoc/>
    public override DataType Type => BooleanType.Instance;

    /// <inheritdoc/>
    public override bool Nullable => false;

    /// <inheritdoc/>
    public override string NodeName => "IsNotNull";

    /// <inheritdoc/>
    public override string SimpleString => $"({Child.SimpleString} IS NOT NULL)";

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 1, NodeName);
        return ReferenceEquals(newChildren[0], Child) ? this : new IsNotNull(newChildren[0]);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other) => true;

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Seed;
}

/// <summary>
/// Null-safe equality (Catalyst <c>EqualNullSafe</c>, Spark <c>&lt;=&gt;</c>) — treats two
/// <c>NULL</c>s as equal and a <c>NULL</c> vs non-<c>NULL</c> as not equal, so it <b>never</b>
/// yields SQL <c>NULL</c>. Returns a <see cref="BooleanType"/> with a <see langword="false"/>
/// nullability hint.
/// </summary>
internal sealed class EqualNullSafe : Expression
{
    /// <summary>Creates <c><paramref name="left"/> &lt;=&gt; <paramref name="right"/></c>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <exception cref="ArgumentNullException"><paramref name="left"/> or <paramref name="right"/>
    /// is null.</exception>
    public EqualNullSafe(Expression left, Expression right)
        : base(Binary(left, right))
    {
    }

    /// <summary>The left operand.</summary>
    public Expression Left => Children[0];

    /// <summary>The right operand.</summary>
    public Expression Right => Children[1];

    /// <inheritdoc/>
    public override DataType Type => BooleanType.Instance;

    /// <inheritdoc/>
    public override bool Nullable => false;

    /// <inheritdoc/>
    public override string NodeName => "EqualNullSafe";

    /// <inheritdoc/>
    public override string SimpleString => $"({Left.SimpleString} <=> {Right.SimpleString})";

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 2, NodeName);
        return ReferenceEquals(newChildren[0], Left) && ReferenceEquals(newChildren[1], Right)
            ? this
            : new EqualNullSafe(newChildren[0], newChildren[1]);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other) => true;

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Seed;
}
