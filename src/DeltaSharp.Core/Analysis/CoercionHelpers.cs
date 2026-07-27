using DeltaSharp.Diagnostics;
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
    /// <summary>
    /// The cap applied to an expression rendered <b>into a diagnostic</b> by
    /// <see cref="DiagnosticReference"/>. Generous enough to show any realistic predicate or projection
    /// element whole — several times the length of a typical <c>(amount &gt; 0)</c> / <c>CASE WHEN … END</c>
    /// render — while keeping the echo independent of the attacker's input length: a hostile
    /// <c>delta.constraints.&lt;name&gt;</c> predicate carrying a 100&#160;000-character string literal used
    /// to render a 100&#160;156-character analyzer message (#687).
    /// </summary>
    /// <remarks>Deliberately larger than <see cref="DiagnosticText.DefaultMaxLength"/> (which bounds a single
    /// identifier-shaped token) because this bounds a whole rendered <em>expression tree</em>, and smaller
    /// than <see cref="AnalysisException.MaxMessageLength"/> so the surrounding explanatory prose — the
    /// operand types, the operator name, the actionable advice — survives intact even when the reference
    /// itself is elided.</remarks>
    internal const int DiagnosticReferenceMaxLength = 256;

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
    /// <para>
    /// <b>Not hygienic.</b> The render embeds leaf text verbatim — notably a <see cref="Literal"/>'s decoded
    /// <em>value</em> — which is attacker-authored when the expression came from a hostile
    /// <c>delta.constraints.&lt;name&gt;</c> predicate. This raw form is correct for the <b>auto-naming</b>
    /// callers (it becomes an output-schema column name, which must not be rewritten); every call that
    /// interpolates a reference into an <b>exception message</b> must go through
    /// <see cref="DiagnosticReference"/> instead (#687).
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

    /// <summary>
    /// Renders <paramref name="expression"/> for use <b>inside a diagnostic message</b>: the
    /// <see cref="PrettyReference"/> SQL form, then bounded and neutralized by
    /// <see cref="DiagnosticText.Sanitize"/> at <see cref="DiagnosticReferenceMaxLength"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #687. A rendered expression embeds <b>attacker-authored</b> text whenever the expression came from a
    /// hostile <c>_delta_log</c> — a <c>delta.constraints.&lt;name&gt;</c> CHECK predicate reaches the analyzer
    /// through <c>ConstraintExpressionFrontend</c>, and its string-literal leaves render their decoded
    /// <em>value</em> (the SQL parser is already hygienic here: it reports the token kind
    /// <c>string literal</c>, never the value). A predicate of
    /// <c>amount &gt; 'A&lt;CR&gt;&lt;LF&gt;FORGED'</c> therefore used to drive raw CR/LF into the surfaced
    /// message (log-line forgery), and a 100&#160;000-character literal drove a 100&#160;156-character render.
    /// </para>
    /// <para>
    /// This is deliberately a <b>separate entry point</b> rather than sanitization inside
    /// <see cref="PrettyReference"/>: that renderer is also the source of Spark-parity <em>auto-names</em>
    /// for functions in output position (<c>Analyzer.SparkAutoName</c>, <c>LogicalOutput</c>), where the
    /// result is a real <b>output-schema column name</b>, not diagnostic prose. Bounding or rewriting text
    /// there would silently change query <em>results</em>. Diagnostics are bounded; names are not.
    /// </para>
    /// </remarks>
    public static string DiagnosticReference(Expression expression) =>
        DiagnosticText.Sanitize(PrettyReference(expression), DiagnosticReferenceMaxLength);

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
