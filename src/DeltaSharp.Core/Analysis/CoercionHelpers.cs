using System.Globalization;
using System.Text;
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

    /// <summary>
    /// The budget for a type rendered into the <em>detail</em> of a single-type diagnostic
    /// (<c>Analyzer.ExtractStructField</c>, <see cref="ExpressionCoercion"/>). Sized to show an ordinary
    /// nested payload struct with its field names intact while leaving room for the surrounding prose,
    /// the reference and the field name inside <see cref="AnalysisException.MaxMessageLength"/>.
    /// </summary>
    internal const int DiagnosticTypeMaxLength = 320;

    /// <summary>The smallest budget <see cref="DiagnosticType"/> accepts: the widest possible compact
    /// summary (<c>struct&lt;(2147483647 fields)&gt;</c>, 27 characters) must fit, or the hard
    /// length guarantee could not be honoured without cutting the count off the end.</summary>
    private const int MinDiagnosticTypeLength = 32;

    /// <summary>Per-field-name cap inside a rendered type. Field names are user-authored (a hostile
    /// <c>_delta_log</c> schema) and otherwise unbounded.</summary>
    private const int MaxEchoedFieldNameLength = 32;

    /// <summary>Nesting depth past which a composite renders as its compact summary. Bounds the render
    /// of a pathologically deep type (<c>array&lt;array&lt;…&gt;&gt;</c>) that carries no fields at all
    /// and so would never trip the character budget.</summary>
    private const int MaxEchoedTypeDepth = 4;

    /// <summary>
    /// Renders <paramref name="type"/> for use <b>inside a diagnostic message</b>, bounded to
    /// <paramref name="maxLength"/> characters and neutralized, <b>with an explicit overflow count for
    /// every field it omits</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists rather than <c>Sanitize(type.SimpleString, cap)</c>.</b> A type render is not a
    /// token, it is a <em>recursive collection</em>: <c>StructType.SimpleString</c> is
    /// <c>struct&lt;f1:int,f2:string,…&gt;</c> over user-authored field names. Capping that flat string cuts
    /// the container and destroys the signal that anything was cut — a 60-field and a 400-field payload
    /// struct rendered as the <em>same</em> 1024-character message ending in a bare <c>…</c>, so a user
    /// mistyping <c>df.Select("payload.typo")</c> on an ordinary wide struct was shown a silently
    /// truncated field list with no indication that it was truncated or by how much. Bounding each
    /// field and reporting <c>(+N more)</c> keeps the render bounded <em>and</em> honest.
    /// </para>
    /// <para>
    /// <b>The length guarantee is hard.</b> The result is never longer than <paramref name="maxLength"/>.
    /// The budget-driven walk stops emitting fields as it approaches the ceiling, but a deeply nested
    /// type can still overshoot (each level reserves room for its own <c>(+N more)</c> suffix); when it
    /// does, the whole render collapses to the compact summary <c>struct&lt;(N fields)&gt;</c>, which is
    /// short, bounded by construction, and <em>still carries the count</em>. Both branches are honest —
    /// that is the property, not the exact shape.
    /// </para>
    /// </remarks>
    internal static string DiagnosticType(DataType type, int maxLength = DiagnosticTypeMaxLength)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, MinDiagnosticTypeLength);
        return RenderBounded(type, maxLength, depth: 0);
    }

    /// <summary>Compact, count-carrying fallback used when the detailed render does not fit its budget or
    /// bottoms out on depth. Bounded by construction: the longest form is a struct summary carrying an
    /// <see cref="int"/> field count (<c>struct&lt;(2147483647 fields)&gt;</c>, 27 characters), which is why
    /// <see cref="MinDiagnosticTypeLength"/> is the floor on a caller's budget.</summary>
    private static string SummarizeType(DataType type, int maxLength) => type switch
    {
        StructType structType =>
            string.Create(CultureInfo.InvariantCulture, $"struct<({structType.Count} fields)>"),
        ArrayType => "array<\u2026>",
        MapType => "map<\u2026>",
        _ => DiagnosticText.Sanitize(type.SimpleString, maxLength),
    };

    /// <summary>
    /// Renders <paramref name="type"/> in at most <paramref name="budget"/> characters. Each candidate
    /// piece is rendered first and appended only if it <em>and</em> a worst-case overflow suffix still fit,
    /// so the count can never be the thing that gets cut; if the detailed form does not fit even so, the
    /// whole render collapses to the count-carrying summary. The fit-is-checked-before-committing shape is
    /// deliberate: an append-then-measure loop overshoots by up to one field, which made the render flip
    /// between detailed and summary form on nothing more than a change in field-name length.
    /// </summary>
    private static string RenderBounded(DataType type, int budget, int depth)
    {
        // " … (+2147483647 more)" is 21 characters and the closing '>' is one more.
        const int SuffixReserve = 22;

        string summary = SummarizeType(type, budget);
        if (depth >= MaxEchoedTypeDepth)
        {
            return summary;
        }

        string detailed;
        switch (type)
        {
            case StructType structType:
                var builder = new StringBuilder("struct<");
                int shown = 0;
                for (int i = 0; i < structType.Count; i++)
                {
                    string name = DiagnosticText.Sanitize(structType[i].Name, MaxEchoedFieldNameLength);
                    string child = RenderBounded(structType[i].DataType, budget, depth + 1);
                    string piece = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{(shown > 0 ? "," : string.Empty)}{name}:{child}");
                    if (builder.Length + piece.Length + SuffixReserve > budget)
                    {
                        break;
                    }

                    builder.Append(piece);
                    shown++;
                }

                if (shown < structType.Count)
                {
                    builder.Append(
                        CultureInfo.InvariantCulture, $" \u2026 (+{structType.Count - shown} more)");
                }

                detailed = builder.Append('>').ToString();
                break;

            case ArrayType arrayType:
                detailed = $"array<{RenderBounded(arrayType.ElementType, budget - 7, depth + 1)}>";
                break;

            case MapType mapType:
                detailed = $"map<{RenderBounded(mapType.KeyType, budget - 5, depth + 1)}," +
                    $"{RenderBounded(mapType.ValueType, budget - 5, depth + 1)}>";
                break;

            default:
                // An atomic type's SimpleString is a compile-time constant or a decimal(p,s) form: no
                // user-authored text. Sanitized anyway so the rule holds for any future DataType.
                detailed = DiagnosticText.Sanitize(type.SimpleString, MaxEchoedFieldNameLength);
                break;
        }

        return detailed.Length <= budget ? detailed : summary;
    }

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
