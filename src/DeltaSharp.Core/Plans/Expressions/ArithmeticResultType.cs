using DeltaSharp.Types;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// The result-type and operand-target of a coerced <see cref="BinaryArithmetic"/> — the Spark
/// numeric-promotion / decimal result-type rules (ADR-0008) expressed as a pure function of the two
/// operand types. <see cref="LeftTarget"/>/<see cref="RightTarget"/> are the types the operands must
/// be cast to (the analyzer inserts <see cref="Cast"/> nodes for them); <see cref="ResultType"/> is
/// the type the operation yields.
/// </summary>
/// <param name="LeftTarget">The type the left operand is coerced to.</param>
/// <param name="RightTarget">The type the right operand is coerced to.</param>
/// <param name="ResultType">The arithmetic result type.</param>
internal readonly record struct ArithmeticCoercion(
    DataType LeftTarget, DataType RightTarget, DataType ResultType);

/// <summary>
/// Computes the Spark-parity coercion of a binary arithmetic expression over two <b>numeric</b>
/// operand types (ADR-0008). This is the single source of truth shared by
/// <see cref="BinaryArithmetic.Type"/> (which derives its result type once its operands are typed)
/// and the analyzer's coercion pass (which inserts the operand casts). Keeping both on one helper
/// makes the derivation idempotent: re-running it over already-coerced operands yields the same
/// result type.
/// </summary>
internal static class ArithmeticResultType
{
    /// <summary>
    /// Returns the operand targets and result type for <paramref name="op"/> over
    /// <paramref name="left"/>/<paramref name="right"/>, or <see langword="null"/> when either
    /// operand is not a numeric type (the caller reports the operand type mismatch). A
    /// <see cref="NullType"/> operand widens to the other operand's numeric type; two null operands
    /// have no numeric result and return <see langword="null"/>.
    /// </summary>
    public static ArithmeticCoercion? TryResolve(ArithmeticOperator op, DataType left, DataType right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        // A typed NULL promotes to the other operand (SQL null propagation, ADR-0008). Two nulls
        // have no concrete numeric type, so the expression stays untyped and is rejected upstream.
        if (left is NullType && right is NullType)
        {
            return null;
        }

        DataType l = left is NullType ? right : left;
        DataType r = right is NullType ? left : right;
        if (!TypeCoercion.IsNumeric(l) || !TypeCoercion.IsNumeric(r))
        {
            return null;
        }

        // Decimal arithmetic (no float/double operand) follows Spark's DecimalPrecision result-type
        // rules; the operands are widened to the decimal that exactly holds their source type.
        bool hasDecimal = l is DecimalType || r is DecimalType;
        bool hasBinaryFloat = l is FloatType or DoubleType || r is FloatType or DoubleType;
        if (hasDecimal && !hasBinaryFloat)
        {
            DecimalType la = DecimalArithmetic.ForType(l);
            DecimalType rb = DecimalArithmetic.ForType(r);
            DecimalType result = DecimalArithmetic.ResultType(ToDecimalOp(op), la, rb);
            return new ArithmeticCoercion(la, rb, result);
        }

        // Non-decimal numeric arithmetic widens both operands to their common numeric type. Division
        // follows Spark's rule that `/` over non-decimal operands yields a DoubleType result.
        DataType common = TypeCoercion.FindWiderTypeForTwo(l, r)
            ?? throw new InvalidOperationException(
                $"No common numeric type for '{l.SimpleString}' and '{r.SimpleString}'.");
        if (op == ArithmeticOperator.Divide)
        {
            return new ArithmeticCoercion(DoubleType.Instance, DoubleType.Instance, DoubleType.Instance);
        }

        return new ArithmeticCoercion(common, common, common);
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
