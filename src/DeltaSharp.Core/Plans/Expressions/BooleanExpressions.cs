using DeltaSharp.Types;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// Boolean conjunction (Catalyst <c>And</c>) — <c>left AND right</c> under SQL three-valued logic.
/// The result is a nullable <see cref="BooleanType"/> (its type is known before analysis); the
/// nullability hint is the OR of its operands' hints (a definite operand can still pin the result
/// under 3VL, but the hint stays conservative).
/// </summary>
internal sealed class And : Expression
{
    /// <summary>Creates <c><paramref name="left"/> AND <paramref name="right"/></c>.</summary>
    /// <param name="left">The left boolean operand.</param>
    /// <param name="right">The right boolean operand.</param>
    /// <exception cref="ArgumentNullException"><paramref name="left"/> or <paramref name="right"/>
    /// is null.</exception>
    public And(Expression left, Expression right)
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
    public override bool Nullable => Left.Nullable || Right.Nullable;

    /// <inheritdoc/>
    public override string NodeName => "And";

    /// <inheritdoc/>
    public override string SimpleString => $"({Left.SimpleString} AND {Right.SimpleString})";

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 2, NodeName);
        return ReferenceEquals(newChildren[0], Left) && ReferenceEquals(newChildren[1], Right)
            ? this
            : new And(newChildren[0], newChildren[1]);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other) => true;

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Seed;
}

/// <summary>
/// Boolean disjunction (Catalyst <c>Or</c>) — <c>left OR right</c> under SQL three-valued logic,
/// returning a nullable <see cref="BooleanType"/>.
/// </summary>
internal sealed class Or : Expression
{
    /// <summary>Creates <c><paramref name="left"/> OR <paramref name="right"/></c>.</summary>
    /// <param name="left">The left boolean operand.</param>
    /// <param name="right">The right boolean operand.</param>
    /// <exception cref="ArgumentNullException"><paramref name="left"/> or <paramref name="right"/>
    /// is null.</exception>
    public Or(Expression left, Expression right)
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
    public override bool Nullable => Left.Nullable || Right.Nullable;

    /// <inheritdoc/>
    public override string NodeName => "Or";

    /// <inheritdoc/>
    public override string SimpleString => $"({Left.SimpleString} OR {Right.SimpleString})";

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 2, NodeName);
        return ReferenceEquals(newChildren[0], Left) && ReferenceEquals(newChildren[1], Right)
            ? this
            : new Or(newChildren[0], newChildren[1]);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other) => true;

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Seed;
}

/// <summary>
/// Boolean negation (Catalyst <c>Not</c>) — <c>NOT child</c> under SQL three-valued logic
/// (<c>NOT NULL = NULL</c>), returning a <see cref="BooleanType"/> whose nullability hint follows
/// the child.
/// </summary>
internal sealed class Not : Expression
{
    /// <summary>Creates <c>NOT <paramref name="child"/></c>.</summary>
    /// <param name="child">The boolean operand to negate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    public Not(Expression child)
        : base(Unary(child))
    {
    }

    /// <summary>The negated operand.</summary>
    public Expression Child => Children[0];

    /// <inheritdoc/>
    public override DataType Type => BooleanType.Instance;

    /// <inheritdoc/>
    public override bool Nullable => Child.Nullable;

    /// <inheritdoc/>
    public override string NodeName => "Not";

    /// <inheritdoc/>
    public override string SimpleString => $"(NOT {Child.SimpleString})";

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 1, NodeName);
        return ReferenceEquals(newChildren[0], Child) ? this : new Not(newChildren[0]);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other) => true;

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Seed;
}
