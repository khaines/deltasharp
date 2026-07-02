using DeltaSharp.Types;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// A binary arithmetic expression (Catalyst <c>Add</c>/<c>Subtract</c>/<c>Multiply</c>/
/// <c>Divide</c>/<c>Remainder</c>) — <c>left ⟨op⟩ right</c> over two operands. At the
/// logical/unresolved stage the result type is <b>unknown</b> (<see cref="Type"/> is
/// <see langword="null"/>): Spark's numeric promotion / decimal result-type coercion is the
/// analyzer's job (FEAT-04.5/EPIC-02). The nullability hint is <b>propagate-on-any-null</b> (the
/// OR of its operands' hints).
/// </summary>
internal sealed class BinaryArithmetic : Expression
{
    /// <summary>Creates <c><paramref name="left"/> ⟨<paramref name="op"/>⟩ <paramref name="right"/></c>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="op">The arithmetic operator.</param>
    /// <exception cref="ArgumentNullException"><paramref name="left"/> or <paramref name="right"/>
    /// is null.</exception>
    public BinaryArithmetic(Expression left, Expression right, ArithmeticOperator op)
        : base(Binary(left, right))
    {
        Operator = op;
    }

    /// <summary>The arithmetic operator.</summary>
    public ArithmeticOperator Operator { get; }

    /// <summary>The left operand.</summary>
    public Expression Left => Children[0];

    /// <summary>The right operand.</summary>
    public Expression Right => Children[1];

    /// <inheritdoc/>
    public override bool Nullable => Left.Nullable || Right.Nullable;

    /// <inheritdoc/>
    public override string NodeName => Operator switch
    {
        ArithmeticOperator.Add => "Add",
        ArithmeticOperator.Subtract => "Subtract",
        ArithmeticOperator.Multiply => "Multiply",
        ArithmeticOperator.Divide => "Divide",
        ArithmeticOperator.Remainder => "Remainder",
        _ => "BinaryArithmetic",
    };

    /// <inheritdoc/>
    public override string SimpleString => $"({Left.SimpleString} {Symbol} {Right.SimpleString})";

    private string Symbol => Operator switch
    {
        ArithmeticOperator.Add => "+",
        ArithmeticOperator.Subtract => "-",
        ArithmeticOperator.Multiply => "*",
        ArithmeticOperator.Divide => "/",
        ArithmeticOperator.Remainder => "%",
        _ => "?",
    };

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 2, NodeName);
        return ReferenceEquals(newChildren[0], Left) && ReferenceEquals(newChildren[1], Right)
            ? this
            : new BinaryArithmetic(newChildren[0], newChildren[1], Operator);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other) =>
        Operator == ((BinaryArithmetic)other).Operator;

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Combine(PlanHash.Seed, (int)Operator);
}

/// <summary>
/// A binary comparison (Catalyst <c>EqualTo</c>/<c>LessThan</c>/…) — <c>left ⟨op⟩ right</c>
/// returning a nullable <see cref="BooleanType"/> (its type is known even before analysis). The
/// nullability hint is propagate-on-any-null.
/// </summary>
internal sealed class BinaryComparison : Expression
{
    /// <summary>Creates <c><paramref name="left"/> ⟨<paramref name="op"/>⟩ <paramref name="right"/></c>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="op">The comparison operator.</param>
    /// <exception cref="ArgumentNullException"><paramref name="left"/> or <paramref name="right"/>
    /// is null.</exception>
    public BinaryComparison(Expression left, Expression right, ComparisonOperator op)
        : base(Binary(left, right))
    {
        Operator = op;
    }

    /// <summary>The comparison operator.</summary>
    public ComparisonOperator Operator { get; }

    /// <summary>The left operand.</summary>
    public Expression Left => Children[0];

    /// <summary>The right operand.</summary>
    public Expression Right => Children[1];

    /// <summary>A comparison is always boolean-typed.</summary>
    public override DataType Type => BooleanType.Instance;

    /// <inheritdoc/>
    public override bool Nullable => Left.Nullable || Right.Nullable;

    /// <inheritdoc/>
    public override string NodeName => Operator switch
    {
        ComparisonOperator.Equal => "EqualTo",
        ComparisonOperator.NotEqual => "NotEqualTo",
        ComparisonOperator.LessThan => "LessThan",
        ComparisonOperator.LessThanOrEqual => "LessThanOrEqual",
        ComparisonOperator.GreaterThan => "GreaterThan",
        ComparisonOperator.GreaterThanOrEqual => "GreaterThanOrEqual",
        _ => "BinaryComparison",
    };

    /// <inheritdoc/>
    public override string SimpleString => $"({Left.SimpleString} {Symbol} {Right.SimpleString})";

    private string Symbol => Operator switch
    {
        ComparisonOperator.Equal => "=",
        ComparisonOperator.NotEqual => "<>",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        _ => "?",
    };

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 2, NodeName);
        return ReferenceEquals(newChildren[0], Left) && ReferenceEquals(newChildren[1], Right)
            ? this
            : new BinaryComparison(newChildren[0], newChildren[1], Operator);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other) =>
        Operator == ((BinaryComparison)other).Operator;

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Combine(PlanHash.Seed, (int)Operator);
}
