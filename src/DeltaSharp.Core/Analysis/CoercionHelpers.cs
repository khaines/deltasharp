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
    /// <c>name(DISTINCT? args)</c>, the boolean composites (<see cref="And"/>, <see cref="Or"/>,
    /// <see cref="Not"/>) and null predicates (<see cref="IsNull"/>, <see cref="IsNotNull"/>,
    /// <see cref="EqualNullSafe"/>) render as their parenthesized SQL forms, <see cref="Alias"/> /
    /// <see cref="SortOrder"/> render their wrapped child, and a <see cref="CaseWhen"/> renders as
    /// <c>CASE WHEN … THEN … [ELSE …] END</c>.
    /// <para>
    /// The ExprId-free guarantee holds <b>by construction</b>: the only leaf whose
    /// <c>SimpleString</c> carries an ExprId is an <see cref="AttributeReference"/>, and it is cased
    /// first (to its bare <c>Name</c>). Every other node is rendered from its <em>pretty</em> children,
    /// including via the generic fallback (<see cref="PrettyFallback"/>) for any node type not given a
    /// bespoke SQL form — so a resolved <see cref="AttributeReference"/> can never leak its
    /// <c>#ExprId</c> through the <c>SimpleString</c> of an un-cased parent, and the invariant survives
    /// future node types. Diagnostics therefore show <c>(b + i)</c> / <c>i</c> / <c>(b AND i)</c> /
    /// <c>(i IS NULL)</c> / <c>CASE WHEN b THEN i ELSE s END</c> rather than <c>(b#7 + i#8)</c> etc.
    /// </para>
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
            And and => $"({PrettyReference(and.Left)} AND {PrettyReference(and.Right)})",
            Or or => $"({PrettyReference(or.Left)} OR {PrettyReference(or.Right)})",
            Not not => $"(NOT {PrettyReference(not.Child)})",
            IsNull isNull => $"({PrettyReference(isNull.Child)} IS NULL)",
            IsNotNull isNotNull => $"({PrettyReference(isNotNull.Child)} IS NOT NULL)",
            EqualNullSafe equalNullSafe =>
                $"({PrettyReference(equalNullSafe.Left)} <=> {PrettyReference(equalNullSafe.Right)})",
            Alias alias => $"{PrettyReference(alias.Child)} AS {alias.Name}",
            SortOrder sortOrder => PrettySortOrder(sortOrder),
            CaseWhen caseWhen => PrettyCaseWhen(caseWhen),
            ResolvedFunction function => PrettyFunction(function),
            _ => PrettyFallback(expression),
        };
    }

    /// <summary>Total, leak-proof fallback for any node without a bespoke SQL form. A true leaf
    /// (<see cref="Literal"/>, an unresolved marker) carries no ExprId, so its <c>SimpleString</c> is
    /// safe; any composite is rendered generically from <em>pretty</em> children so no resolved
    /// <see cref="AttributeReference"/> descendant can leak its <c>#ExprId</c>.</summary>
    private static string PrettyFallback(Expression expression) =>
        expression.Children.Count == 0
            ? expression.SimpleString
            : $"{expression.NodeName}({string.Join(", ", expression.Children.Select(PrettyReference))})";

    private static string PrettySortOrder(SortOrder sortOrder)
    {
        string direction = sortOrder.Direction == SortDirection.Ascending ? "ASC" : "DESC";
        string nulls = sortOrder.NullOrdering == NullOrdering.NullsFirst ? "NULLS FIRST" : "NULLS LAST";
        return $"{PrettyReference(sortOrder.Child)} {direction} {nulls}";
    }

    private static string PrettyFunction(ResolvedFunction function)
    {
        string distinct = function.IsDistinct ? "DISTINCT " : string.Empty;
        string args = string.Join(", ", function.Arguments.Select(PrettyReference));
        return $"{function.Name}({distinct}{args})";
    }

    private static string PrettyCaseWhen(CaseWhen caseWhen)
    {
        string branches = string.Join(
            " ",
            caseWhen.Branches.Select(
                b => $"WHEN {PrettyReference(b.Condition)} THEN {PrettyReference(b.Value)}"));
        string elsePart = caseWhen.ElseValue is { } elseValue
            ? $" ELSE {PrettyReference(elseValue)}"
            : string.Empty;
        return $"CASE {branches}{elsePart} END";
    }
}
