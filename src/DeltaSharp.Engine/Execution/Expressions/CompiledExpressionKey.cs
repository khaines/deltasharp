using System.Globalization;
using System.Text;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Builds a deterministic structural signature for a <see cref="PhysicalExpression"/> tree, used as the
/// <see cref="CompiledExpressionCache"/> key (STORY-03.4.2). Two trees share a key — and therefore a
/// compiled kernel — exactly when they lower to the same IL: the signature captures every input to the
/// lowering decision (node kind, resolved <see cref="DataType"/>, operator, eval kind, ANSI mode,
/// column ordinal, and the <b>baked-in literal value</b>). Floating literals are keyed by their raw bit
/// pattern so <c>-0.0</c>, <c>+0.0</c>, and distinct <c>NaN</c> payloads never collide. The node's
/// <see cref="PhysicalExpression.Nullable"/> flag is intentionally excluded: it is carried by the
/// wrapping <see cref="CompiledExpressionEvaluator"/>, not by the shared kernel.
/// </summary>
internal static class CompiledExpressionKey
{
    /// <summary>Computes the structural key for <paramref name="expression"/>.</summary>
    public static string Of(PhysicalExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var builder = new StringBuilder();
        Append(builder, expression);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, PhysicalExpression expression)
    {
        switch (expression)
        {
            case ColumnReference column:
                builder.Append("C(").Append(column.Ordinal).Append(':').Append(column.Type.SimpleString).Append(')');
                break;

            case Literal literal:
                builder.Append("L(").Append(literal.Type.SimpleString).Append(':').Append(LiteralKey(literal)).Append(')');
                break;

            case ArithmeticExpression arithmetic:
                builder.Append("A(").Append(arithmetic.Operator).Append(',').Append(arithmetic.EvalKind)
                    .Append(',').Append(arithmetic.Mode).Append(',').Append(arithmetic.Type.SimpleString).Append('[');
                Append(builder, arithmetic.Left);
                Append(builder, arithmetic.Right);
                builder.Append("])");
                break;

            case ComparisonExpression comparison:
                builder.Append("M(").Append(comparison.Operator).Append(',').Append(comparison.EvalKind).Append('[');
                Append(builder, comparison.Left);
                Append(builder, comparison.Right);
                builder.Append("])");
                break;

            case LogicalExpression logical:
                builder.Append("G(").Append(logical.Operator).Append('[');
                Append(builder, logical.Left);
                if (logical.Operator != LogicalOperator.Not)
                {
                    Append(builder, logical.Right);
                }

                builder.Append("])");
                break;

            case CastExpression cast:
                builder.Append("X(").Append(cast.Child.Type.SimpleString).Append("->").Append(cast.Type.SimpleString)
                    .Append(',').Append(cast.Mode).Append('[');
                Append(builder, cast.Child);
                builder.Append("])");
                break;

            case IsNullExpression isNull:
                builder.Append("N(").Append(isNull.Negated).Append('[');
                Append(builder, isNull.Child);
                builder.Append("])");
                break;

            default:
                throw new InvalidOperationException(
                    $"CanFuse should have rejected '{expression.GetType().Name}' before keying.");
        }
    }

    private static string LiteralKey(Literal literal)
    {
        if (literal.IsNull)
        {
            return "n";
        }

        return literal.Type switch
        {
            BooleanType => ((bool)literal.Value!).ToString(),
            ByteType => ((sbyte)literal.Value!).ToString(CultureInfo.InvariantCulture),
            ShortType => ((short)literal.Value!).ToString(CultureInfo.InvariantCulture),
            IntegerType or DateType => ((int)literal.Value!).ToString(CultureInfo.InvariantCulture),
            LongType or TimestampType => ((long)literal.Value!).ToString(CultureInfo.InvariantCulture),

            // Bit patterns distinguish -0.0/+0.0 and NaN payloads (which lower to distinct constants).
            FloatType => BitConverter.SingleToInt32Bits((float)literal.Value!).ToString(CultureInfo.InvariantCulture),
            DoubleType => BitConverter.DoubleToInt64Bits((double)literal.Value!).ToString(CultureInfo.InvariantCulture),
            DecimalType decimalType =>
                ((Int128)literal.Value!).ToString(CultureInfo.InvariantCulture) + "/" + decimalType.Scale.ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"No literal key for type '{literal.Type.SimpleString}'."),
        };
    }
}
