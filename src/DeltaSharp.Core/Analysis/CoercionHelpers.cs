using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;

namespace DeltaSharp.Analysis;

/// <summary>
/// Small shared helpers for the analyzer's binding + type-coercion pass (STORY-04.5.2 / #171): the
/// "cast-unless-already-that-type" widening and the pretty (ExprId-free) reference renderer used both
/// to auto-name functions in output position and to name the offending reference in a
/// <see cref="AnalysisException.DataTypeMismatch"/> diagnostic. Centralizing them keeps the two
/// coercion entry points (<see cref="ExpressionCoercion"/>, <see cref="FunctionRegistry"/>) and the
/// diagnostic call sites in agreement (one DRY source for each concern).
/// </summary>
internal static class CoercionHelpers
{
    /// <summary>Wraps <paramref name="expression"/> in a <see cref="Cast"/> to
    /// <paramref name="target"/> unless it already has that type (structural sharing on a no-op
    /// coercion). This is the single implementation of the "cast unless already that type" rule shared
    /// by operand coercion (<see cref="ExpressionCoercion"/>) and function-argument coercion
    /// (<see cref="FunctionRegistry"/>).</summary>
    public static Expression CastIfNeeded(Expression expression, DataType target)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(target);
        return expression.Type is { } t && t.Equals(target) ? expression : new Cast(expression, target);
    }

    /// <summary>
    /// Renders a <b>resolved</b> expression as Spark's pretty SQL form for user-facing use — the
    /// auto-name of a function in output position and the offending reference in a data-type-mismatch
    /// diagnostic. It mirrors Spark's <c>usePrettyExpression</c>: an <see cref="AttributeReference"/>
    /// contributes its bare <c>Name</c> (never the internal <c>name#ExprId</c>), an implicit coercion
    /// <see cref="Cast"/> is transparent (its child's pretty form), binary arithmetic/comparison render
    /// as the infix <c>(left op right)</c>, a <see cref="ResolvedFunction"/> renders as
    /// <c>name(DISTINCT? args)</c>, and anything else (a literal, an unresolved marker) falls back to
    /// its <c>SimpleString</c>. Critically, it never leaks an ExprId, so diagnostics
    /// show <c>(b + i)</c> / <c>i</c> rather than <c>(b#7 + i#8)</c> / <c>i#8</c>.
    /// </summary>
    public static string PrettyReference(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return expression switch
        {
            AttributeReference attribute => attribute.Name,
            Cast cast => PrettyReference(cast.Child),
            BinaryArithmetic arithmetic =>
                $"({PrettyReference(arithmetic.Left)} {arithmetic.Symbol} {PrettyReference(arithmetic.Right)})",
            BinaryComparison comparison =>
                $"({PrettyReference(comparison.Left)} {comparison.Symbol} {PrettyReference(comparison.Right)})",
            ResolvedFunction function => PrettyFunction(function),
            _ => expression.SimpleString,
        };
    }

    private static string PrettyFunction(ResolvedFunction function)
    {
        string distinct = function.IsDistinct ? "DISTINCT " : string.Empty;
        string args = string.Join(", ", function.Arguments.Select(PrettyReference));
        return $"{function.Name}({distinct}{args})";
    }
}
