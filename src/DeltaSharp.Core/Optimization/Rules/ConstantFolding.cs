using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Optimization.Rules;

/// <summary>
/// Replaces a fully-constant subexpression with the <see cref="Literal"/> it evaluates to (Catalyst
/// <c>ConstantFolding</c>, narrowed for M1). It rewrites every expression across the plan bottom-up,
/// so nested constants collapse in one pass (<c>(1 + 2) + 3 → 6</c>). It folds only when <b>every</b>
/// relevant operand is a literal, and it honors ANSI overflow semantics (ADR-0008): an integral
/// computation that would overflow is <b>not</b> folded, leaving execution to raise the ANSI error
/// rather than silently wrapping (see <c>docs/engineering/design/logical-optimizer.md</c> §3.1).
/// </summary>
internal sealed class ConstantFolding : Rule
{
    /// <inheritdoc/>
    public override string Name => "ConstantFolding";

    /// <inheritdoc/>
    public override LogicalPlan Apply(LogicalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.TransformUp(node => node.TransformExpressionsUp(Fold));
    }

    private static Expression Fold(Expression expression) => expression switch
    {
        BinaryArithmetic arithmetic => FoldArithmetic(arithmetic) ?? expression,
        Not not => FoldNot(not) ?? expression,
        And and => FoldAnd(and) ?? expression,
        Or or => FoldOr(or) ?? expression,
        _ => expression,
    };

    // ---- arithmetic ----

    private static Literal? FoldArithmetic(BinaryArithmetic arithmetic)
    {
        // Fold only Add/Subtract/Multiply over two non-null literals of the same numeric type.
        // Divide/Remainder (division-by-zero, result-type nuances) and decimal are deferred (§3.1).
        if (arithmetic.Operator is ArithmeticOperator.Divide or ArithmeticOperator.Remainder)
        {
            return null;
        }

        if (arithmetic.Left is not Literal { IsNull: false } left
            || arithmetic.Right is not Literal { IsNull: false } right
            || !left.Type.Equals(right.Type))
        {
            return null;
        }

        return left.Type switch
        {
            ByteType => FoldByte(arithmetic.Operator, (sbyte)left.Value!, (sbyte)right.Value!),
            ShortType => FoldShort(arithmetic.Operator, (short)left.Value!, (short)right.Value!),
            IntegerType => FoldInt(arithmetic.Operator, (int)left.Value!, (int)right.Value!),
            LongType => FoldLong(arithmetic.Operator, (long)left.Value!, (long)right.Value!),
            FloatType => Literal.OfFloat(FoldFloat(arithmetic.Operator, (float)left.Value!, (float)right.Value!)),
            DoubleType => Literal.OfDouble(FoldDouble(arithmetic.Operator, (double)left.Value!, (double)right.Value!)),
            _ => null,
        };
    }

    // Integral folds run in a checked context: an overflow throws OverflowException, which we catch
    // and treat as "do not fold" so ANSI semantics (never wrap) are preserved.
    private static Literal? FoldByte(ArithmeticOperator op, sbyte a, sbyte b)
    {
        try
        {
            checked
            {
                return Literal.OfByte(op switch
                {
                    ArithmeticOperator.Add => (sbyte)(a + b),
                    ArithmeticOperator.Subtract => (sbyte)(a - b),
                    _ => (sbyte)(a * b),
                });
            }
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static Literal? FoldShort(ArithmeticOperator op, short a, short b)
    {
        try
        {
            checked
            {
                return Literal.OfShort(op switch
                {
                    ArithmeticOperator.Add => (short)(a + b),
                    ArithmeticOperator.Subtract => (short)(a - b),
                    _ => (short)(a * b),
                });
            }
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static Literal? FoldInt(ArithmeticOperator op, int a, int b)
    {
        try
        {
            checked
            {
                return Literal.OfInt(op switch
                {
                    ArithmeticOperator.Add => a + b,
                    ArithmeticOperator.Subtract => a - b,
                    _ => a * b,
                });
            }
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static Literal? FoldLong(ArithmeticOperator op, long a, long b)
    {
        try
        {
            checked
            {
                return Literal.OfLong(op switch
                {
                    ArithmeticOperator.Add => a + b,
                    ArithmeticOperator.Subtract => a - b,
                    _ => a * b,
                });
            }
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static float FoldFloat(ArithmeticOperator op, float a, float b) => op switch
    {
        ArithmeticOperator.Add => a + b,
        ArithmeticOperator.Subtract => a - b,
        _ => a * b,
    };

    private static double FoldDouble(ArithmeticOperator op, double a, double b) => op switch
    {
        ArithmeticOperator.Add => a + b,
        ArithmeticOperator.Subtract => a - b,
        _ => a * b,
    };

    // ---- boolean (SQL three-valued logic) ----

    private static Literal? FoldNot(Not not)
    {
        if (!TryBool(not.Child, out bool? value))
        {
            return null;
        }

        return value is null ? Literal.Null(BooleanType.Instance) : Literal.OfBoolean(!value.Value);
    }

    private static Literal? FoldAnd(And and)
    {
        if (!TryBool(and.Left, out bool? left) || !TryBool(and.Right, out bool? right))
        {
            return null;
        }

        // 3VL AND: FALSE dominates; otherwise NULL is unknown; otherwise TRUE.
        if (left == false || right == false)
        {
            return Literal.OfBoolean(false);
        }

        if (left is null || right is null)
        {
            return Literal.Null(BooleanType.Instance);
        }

        return Literal.OfBoolean(true);
    }

    private static Literal? FoldOr(Or or)
    {
        if (!TryBool(or.Left, out bool? left) || !TryBool(or.Right, out bool? right))
        {
            return null;
        }

        // 3VL OR: TRUE dominates; otherwise NULL is unknown; otherwise FALSE.
        if (left == true || right == true)
        {
            return Literal.OfBoolean(true);
        }

        if (left is null || right is null)
        {
            return Literal.Null(BooleanType.Instance);
        }

        return Literal.OfBoolean(false);
    }

    /// <summary>Reads a boolean <see cref="Literal"/> operand: <see langword="true"/> on success with
    /// <paramref name="value"/> set (<see langword="null"/> = SQL <c>NULL</c>), else <see langword="false"/>.</summary>
    private static bool TryBool(Expression expression, out bool? value)
    {
        if (expression is Literal { Type: BooleanType } literal)
        {
            value = literal.IsNull ? null : (bool)literal.Value!;
            return true;
        }

        value = null;
        return false;
    }
}
