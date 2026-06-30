using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>The binary arithmetic operators the interpreted evaluator supports (Spark <c>+ - * / %</c>).</summary>
public enum ArithmeticOperator
{
    /// <summary>Addition (<c>+</c>).</summary>
    Add,

    /// <summary>Subtraction (<c>-</c>).</summary>
    Subtract,

    /// <summary>Multiplication (<c>*</c>).</summary>
    Multiply,

    /// <summary>Division (<c>/</c>): Spark promotes non-decimal operands to a floating result and returns SQL <c>NULL</c> (or throws under ANSI) on a zero divisor.</summary>
    Divide,

    /// <summary>Modulo (<c>%</c>): a zero divisor yields SQL <c>NULL</c> (or throws under ANSI).</summary>
    Remainder,
}

/// <summary>
/// How a resolved <see cref="ArithmeticExpression"/> is evaluated lane-by-lane: the CLR accumulation
/// type the kernel reads operands into. Computed once from the operand types and the operator so the
/// evaluator never re-derives it.
/// </summary>
internal enum ArithmeticEvalKind
{
    /// <summary>64-bit integral accumulation with ANSI overflow checking; result is the wider integral type.</summary>
    Integral,

    /// <summary>IEEE single-precision accumulation; result is <see cref="FloatType"/>.</summary>
    Single,

    /// <summary>IEEE double-precision accumulation; result is <see cref="DoubleType"/> (also Spark's <c>/</c> on non-decimals).</summary>
    Double,

    /// <summary>Exact fixed-point accumulation via <see cref="DecimalValue"/>; result is a <see cref="DecimalType"/>.</summary>
    Decimal,
}

/// <summary>
/// A binary arithmetic expression (<c>left op right</c>) over two numeric operands (STORY-03.4.1).
/// Its resolved <see cref="PhysicalExpression.Type"/> follows Spark's numeric promotion and decimal
/// result-type rules (EPIC-02 <see cref="TypeCoercion"/>/<see cref="DecimalArithmetic"/>): integral
/// <c>+ - * %</c> widen to the wider integral type; any <see cref="DoubleType"/> operand (or a
/// decimal mixed with floating) yields <see cref="DoubleType"/>; <c>/</c> on non-decimals yields
/// <see cref="DoubleType"/>; decimals follow <see cref="DecimalArithmetic.ResultType"/>. ANSI/Legacy
/// overflow and zero-divisor behavior is carried by <see cref="Mode"/> and applied during evaluation,
/// not here (building the node does no row work).
/// </summary>
public sealed class ArithmeticExpression : PhysicalExpression
{
    private readonly PhysicalExpression[] _children;

    /// <summary>Creates <paramref name="left"/> <paramref name="op"/> <paramref name="right"/>.</summary>
    /// <param name="left">The left operand (numeric).</param>
    /// <param name="right">The right operand (numeric).</param>
    /// <param name="op">The arithmetic operator.</param>
    /// <param name="mode">The ANSI strictness lens for overflow and zero division (default <see cref="AnsiMode.Ansi"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="left"/> or <paramref name="right"/> is null.</exception>
    /// <exception cref="ArgumentException">An operand is not a numeric type.</exception>
    public ArithmeticExpression(PhysicalExpression left, PhysicalExpression right, ArithmeticOperator op, AnsiMode mode = AnsiMode.Ansi)
        : base(Resolve(left, right, op, out ArithmeticEvalKind kind), Nullability(left, right, mode))
    {
        _children = [left, right];
        Operator = op;
        Mode = mode;
        EvalKind = kind;
    }

    /// <summary>The arithmetic operator.</summary>
    public ArithmeticOperator Operator { get; }

    /// <summary>The ANSI strictness lens applied to overflow and zero division.</summary>
    public AnsiMode Mode { get; }

    /// <summary>The left operand.</summary>
    public PhysicalExpression Left => _children[0];

    /// <summary>The right operand.</summary>
    public PhysicalExpression Right => _children[1];

    /// <summary>The accumulation kind the evaluator uses (resolved once at construction).</summary>
    internal ArithmeticEvalKind EvalKind { get; }

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalExpression> Children => _children;

    private static bool Nullability(PhysicalExpression left, PhysicalExpression right, AnsiMode mode)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        // Operand nulls always propagate. Under Legacy, overflow and zero division produce SQL NULL
        // from otherwise-non-null operands, so the result is nullable there too.
        return left.Nullable || right.Nullable || mode == AnsiMode.Legacy;
    }

    private static DataType Resolve(PhysicalExpression left, PhysicalExpression right, ArithmeticOperator op, out ArithmeticEvalKind kind)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        DataType l = left.Type;
        DataType r = right.Type;
        if (!TypeCoercion.IsNumeric(l) || !TypeCoercion.IsNumeric(r))
        {
            throw new ArgumentException(
                $"Arithmetic operator '{op}' requires numeric operands but got '{l.SimpleString}' and '{r.SimpleString}'.");
        }

        bool anyDouble = l is DoubleType || r is DoubleType;
        bool anyFloat = l is FloatType || r is FloatType;
        bool anyDecimal = l is DecimalType || r is DecimalType;

        // Floating dominates: any double, or a decimal mixed with a float, evaluates in double.
        if (anyDouble || (anyFloat && anyDecimal))
        {
            kind = ArithmeticEvalKind.Double;
            return DoubleType.Instance;
        }

        // Spark's `/` always produces a floating (double) result for non-decimal operands.
        if (op == ArithmeticOperator.Divide && !anyDecimal)
        {
            kind = ArithmeticEvalKind.Double;
            return DoubleType.Instance;
        }

        if (anyFloat)
        {
            kind = ArithmeticEvalKind.Single;
            return FloatType.Instance;
        }

        if (anyDecimal)
        {
            kind = ArithmeticEvalKind.Decimal;
            DecimalType ld = DecimalArithmetic.ForType(l);
            DecimalType rd = DecimalArithmetic.ForType(r);
            return DecimalArithmetic.ResultType(ToDecimalOp(op), ld, rd);
        }

        // Both integral: +, -, *, % keep the wider integral type.
        kind = ArithmeticEvalKind.Integral;
        return TypeCoercion.FindWiderTypeForTwo(l, r)
            ?? throw new ArgumentException(
                $"No common integral type for '{l.SimpleString}' and '{r.SimpleString}'.");
    }

    private static DecimalOp ToDecimalOp(ArithmeticOperator op) => op switch
    {
        ArithmeticOperator.Add => DecimalOp.Add,
        ArithmeticOperator.Subtract => DecimalOp.Subtract,
        ArithmeticOperator.Multiply => DecimalOp.Multiply,
        ArithmeticOperator.Divide => DecimalOp.Divide,
        ArithmeticOperator.Remainder => DecimalOp.Remainder,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown arithmetic operator."),
    };
}
