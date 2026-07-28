using System;
using System.Globalization;
using System.Linq;
using System.Text;
using DeltaSharp.Diagnostics;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;

namespace DeltaSharp.Analysis;

/// <summary>
/// Small shared helpers for the analyzer's binding + type-coercion pass (STORY-04.5.2 / #171): the
/// "cast-unless-already-that-type" widening and the pretty (ExprId-free) reference renderer used both
/// to auto-name functions in output position and to name the offending reference in a
/// <see cref="AnalysisException.DataTypeMismatch(string, string)"/> diagnostic. Centralizing them keeps the two
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
    /// Bounds a message's type slots by the space that message actually has left, spending the budget
    /// max-min fair so a narrow type hands its remainder to a wide one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The common case is bounded by nothing at all: if every type's natural render fits in the available
    /// space, all of them are returned untouched. Only when they genuinely do not fit is anything cut, and
    /// then the shortest is served first so that a <c>struct&lt;…&gt;</c> beside an <c>int</c> gets nearly
    /// the whole budget rather than half of it.
    /// </para>
    /// <para>
    /// When there is not even room for the compact summaries there is <b>no floor</b> — every slot collapses
    /// to <see cref="SummarizeType"/>, which is short, bounded by construction and still carries its field
    /// count. Granting a floor larger than the space available is what turns "no room left" into "overflow
    /// the cap", and overflowing the cap is what feeds the listing's own count to the backstop.
    /// </para>
    /// </remarks>
    /// <param name="available">Characters the message has left for all of its type slots together.</param>
    /// <param name="types">The types to render, in the order the message interpolates them.</param>
    internal static string[] BoundTypes(int available, params DataType[] types)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (types.Length == 0)
        {
            return [];
        }

        if (available < types.Length * MinDiagnosticTypeLength)
        {
            return [.. types.Select(t => SummarizeType(t, MinDiagnosticTypeLength))];
        }

        string[] natural = [.. types.Select(t => DiagnosticType(t, available))];

        // Fast path only, and PROVABLY equivalent to the loop below rather than a behavioural branch: the
        // loop serves shortest-first, so when everything fits, each type's natural render is at or under the
        // running average and is therefore kept whole anyway. Removing it is 0 RED, and that is the correct
        // result for an equivalent mutant — recorded here so it is not re-filed as an unpinned branch.
        if (natural.Sum(r => r.Length) <= available)
        {
            return natural;
        }

        var bounded = new string[types.Length];
        int[] shortestFirst = [.. Enumerable.Range(0, types.Length).OrderBy(i => natural[i].Length)];
        int remaining = available;

        for (int n = 0; n < shortestFirst.Length; n++)
        {
            int i = shortestFirst[n];
            int share = Math.Max(MinDiagnosticTypeLength, remaining / (shortestFirst.Length - n));
            bounded[i] = natural[i].Length <= share ? natural[i] : DiagnosticType(types[i], share);
            remaining = Math.Max(0, remaining - bounded[i].Length);
        }

        return bounded;
    }

    /// <summary>The smallest budget <see cref="DiagnosticType"/> accepts: the widest possible compact
    /// summary (<c>struct&lt;(2147483647 fields)&gt;</c>, 27 characters) must fit, or the hard
    /// length guarantee could not be honoured without cutting the count off the end.</summary>
    internal const int MinDiagnosticTypeLength = 32;

    /// <summary>Ceiling on one field name inside a rendered type, so a single pathological name cannot
    /// consume the whole render. Field names are user-authored (a hostile <c>_delta_log</c> schema) and
    /// otherwise unbounded.</summary>
    private const int MaxEchoedFieldNameLength = 128;

    // A field-name FLOOR used to sit here. It is gone: a floor on a name can only matter once the render is
    // nearly full, and at that point the fit check below refuses the field anyway, so the floor changed
    // nothing that was observable — lowering it 16 -> 4 was 0 RED, and correctly so. A constant that cannot
    // be observed is a constant that will be defended in review without evidence, so it is better deleted
    // than justified. The allowance now floors at 0, where Sanitize yields a bare elision mark and the fit
    // check ends the walk.

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
    internal static string DiagnosticType(DataType type, int maxLength)
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
        // What this render still owes at the point it stops: the closing '>', plus the overflow marker if and
        // only if something is actually hidden, sized for THAT many rather than for int.MaxValue.
        //
        // This used to be a fixed 22 — the width of " … (+2147483647 more)" plus the '>'. A constant reserve
        // is a reserve for the worst case charged to every case, so the walk stopped early at every width by
        // the difference between the worst case and the real one, which the independent "could one more field
        // have been shown" oracle found in quantity. Identical in shape to the listing walk's reserve, which charged the marker for hiding
        // ALL items rather than the few really left; both are the same error, so both are now the same fix.
        static int Owed(int hidden) =>
            1 + (hidden == 0 ? 0 : 1 + DiagnosticText.OverflowMarkerLength(hidden));

        string summary = SummarizeType(type, budget);
        if (depth >= MaxEchoedTypeDepth)
        {
            return summary;
        }

        string detailed;
        switch (type)
        {
            case StructType structType:
                {
                    // TWO PASSES, and no search. Round 15 began by fixing this walk's SHAPE — a forward-greedy
                    // walk stops just below a count that fits, because a child handed more room renders its own
                    // structure instead of a summary and starves the fields after it, so the render could show
                    // strictly LESS at a larger budget. The first attempt searched over candidate reserves and
                    // expansion caps sampled at powers of ten and two. That was the same mistake this whole
                    // change has been removing in other places: a SAMPLED candidate set is an incomplete one,
                    // and it missed optima that fell between the samples, eliding a field with room to spare.
                    // The quantities are computable, so they are computed.
                    //
                    // Pass 1 fixes HOW MANY. Every field has a minimum cost — its name, a colon, and the most
                    // compact form of its type — which does not depend on the budget, so a prefix-sum gives the
                    // cost of showing any k of them exactly. The largest feasible k is then a downward scan,
                    // exactly as in DiagnosticText.SanitizeToBudget and for the same reason: total cost is not
                    // monotonic in k, because the marker vanishes entirely at the last step, refunding more
                    // than a cheap field costs. So a count can be infeasible while a LARGER one fits, and a
                    // forward stop-at-first-failure walk halts below it.
                    //
                    // That sentence was, until round 17, an executable claim with nothing executing it. It is
                    // now the property asserted by
                    // AnalysisExceptionTypeRenderTests.TheFieldCountIsTheLargestFeasibleCount_NotOneShortOfThe
                    // FirstInfeasibleOne, which also asserts that its corpus still CONTAINS a count of that
                    // shape — because no fixture that predates it can reach that region, which is itself
                    // asserted, by TheFixturesThatPredateThisPin_CannotReachTheDiscriminatingRegion, rather
                    // than described here. Two revisions of this comment described it and both were wrong,
                    // the second while correcting the first, each comparing a field's NAME width where the
                    // quantity that decides the region is its MARGINAL cost. Guidance for building a
                    // discriminating corpus fails silently when followed, so it is executed now and this
                    // paragraph names the test instead of the numbers. The scan was 0-RED across the whole
                    // solution for two rounds on exactly that account: not a missing axis but a fixture
                    // VALUE, with the sibling scan in DiagnosticText pinned all along.
                    //
                    // Pass 2 fixes HOW MUCH DETAIL. The count is now settled, so each child in turn may expand
                    // into whatever is left after reserving the minimum for the fields that follow it. Nothing a
                    // child does can cost a later field its name, which is what the greedy walk got wrong.
                    //
                    // What pins the shape, stated as BOTH a measured count and the complete list of what
                    // dies, because either alone is silently wrong in a different direction: a count has to
                    // be retuned whenever coverage legitimately grows, and every drift then looks like a
                    // regression, while an under-enumerated name list fails loudly when a name is WRONG and
                    // silently when one is MISSING. Both figures below are rows-per-TFM against methods, and
                    // both were re-derived at this HEAD — the pair only reconciles if each is labelled with
                    // which kind of thing it counts.
                    //
                    // Reversing the SCAN DIRECTION — the same scan, ascending, stopping at the first failure
                    // — is 1 row on each of net8.0 and net10.0, 1 method:
                    // TheFieldCountIsTheLargestFeasibleCount_NotOneShortOfTheFirstInfeasibleOne.
                    //
                    // Deleting pass 2's reservation for the fields that follow — handing each child
                    // "budget - builder.Length - trailing" — is 2 rows on each TFM, 2 methods: that test and
                    // TheStructRender_IsMonotoneAndFullySpent_AcrossBudgetWidthAndChildShape.
                    //
                    // A third figure used to stand here, for "restoring the stop-at-first-fit walk", and it
                    // was deleted rather than re-measured. Round 15 replaced that walk, so the edit it names
                    // cannot be applied to this file and a reader has no way to re-derive the number.
                    //
                    // That is not a quibble about staleness. A reviewer who went looking measured the
                    // boundary two ways — the literal reading of the sentence, and a maximal revert of the
                    // whole struct case — and got two different answers, neither the figure quoted. An
                    // unnameable boundary cannot be mismeasured, only unmeasurable. Its count and its name
                    // list had also drifted TOGETHER, because whoever wrote them took both from the same
                    // wrong run: pairing a count with names detects a stale half, not a stale pair.
                    //
                    // So a mutation claim is worth writing down only if it names an edit to the code as it
                    // now stands, precisely enough that two people get the same number. Both surviving
                    // figures do, and both are re-derived above.
                    int fieldCount = structType.Count;
                    string[] fieldNames = new string[fieldCount];
                    string[] compactChildren = new string[fieldCount];
                    int[] minimumCost = new int[fieldCount + 1];
                    for (int i = 0; i < fieldCount; i++)
                    {
                        // Names are capped but never DIVIDED among the fields. Dividing makes a wide struct cut
                        // every name to fit them all in, which is the common case paying for the pathological
                        // one; the count reports what did not fit instead.
                        fieldNames[i] = DiagnosticText.Sanitize(
                            structType[i].Name, MaxEchoedFieldNameLength);
                        compactChildren[i] = SummarizeType(
                            structType[i].DataType, MaxEchoedFieldNameLength);
                        minimumCost[i + 1] = minimumCost[i] + (i > 0 ? 1 : 0)
                            + fieldNames[i].Length + 1 + compactChildren[i].Length;
                    }

                    const int openingLength = 7; // "struct<"
                    int shown = 0;
                    for (int k = fieldCount; k >= 1; k--)
                    {
                        if (openingLength + minimumCost[k] + Owed(fieldCount - k) <= budget)
                        {
                            shown = k;
                            break;
                        }
                    }

                    int trailing = Owed(fieldCount - shown);
                    var builder = new StringBuilder("struct<");
                    for (int i = 0; i < shown; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(',');
                        }

                        builder.Append(fieldNames[i]).Append(':');

                        // What is left once the fields after this one are guaranteed their minimum.
                        int room = Math.Max(
                            0,
                            budget - builder.Length - (minimumCost[shown] - minimumCost[i + 1]) - trailing);
                        string child = RenderBounded(structType[i].DataType, room, depth + 1);

                        // RenderBounded may return longer than it was allowed, because its summary fallback has
                        // a floor of its own. Falling back to the compact form keeps the guarantee that the
                        // fields after this one can still be shown.
                        //
                        // Defensive, and labelled as such: 0-RED and byte-identical over every cell of
                        // TheStructRender_IsMonotoneAndFullySpent_AcrossBudgetWidthAndChildShape, so no
                        // reachable shape exercises it — but, exactly as with the map's reclaim guard, there
                        // is no PROOF it is dead, because the contract that motivates it is real. Not claimed
                        // to be equivalent, only to be cheap insurance on a call that may legitimately exceed
                        // the caller's assumption. The corpus is NAMED rather than counted: a cell count in
                        // prose has to be retyped every time the sweep grows, and the sweep knows its own
                        // size and prints it on failure.
                        //
                        // PAIRED mutation says that caution was right, and says something more precise than
                        // "no proof". Appending child unconditionally is 0 rows on both TFMs. Doing that AND
                        // returning "detailed" unconditionally at the end of this method, instead of falling
                        // back to summary, is 4 rows on both TFMs across 4 methods; that second edit ALONE is
                        // 2 rows on both across 2 — DiagnosticType_NeverExceedsItsBudget_ForAnyShape and
                        // EveryCompositeRender_FitsTheBudgetItWasGiven, the further two being
                        // TheFieldCountIsTheLargestFeasibleCount_NotOneShortOfTheFirstInfeasibleOne and the
                        // monotonicity sweep named above.
                        //
                        // So this is half of a masking pair rather than an equivalent — and the half that
                        // masks it is ITSELF pinned, which is the part worth knowing. A masking pair is only
                        // dangerous when the masker can be removed silently; here it cannot, so no single
                        // edit can quietly turn this guard from dead into load-bearing. Neither mutation
                        // alone supports that statement, which is the argument for running them in pairs.
                        builder.Append(child.Length <= room ? child : compactChildren[i]);
                    }

                    if (shown < fieldCount)
                    {
                        builder.Append(
                            CultureInfo.InvariantCulture, $" \u2026 (+{fieldCount - shown} more)");
                    }

                    detailed = builder.Append('>').ToString();
                    break;
                }

            case ArrayType arrayType:
                detailed = $"array<{RenderBounded(arrayType.ElementType, budget - 7, depth + 1)}>";
                break;

            case MapType mapType:
                // A map has TWO children competing for one budget, so it splits max-min fair rather than
                // letting the key take what it likes and handing the value the scraps.
                //
                // Giving each child the full budget overran and collapsed the whole render to "map<…>".
                // Giving the value "what the key left" is better but still wrong, because the key is served
                // first and will happily eat everything: at budget 900 the key spent 890 and the value could
                // not render at all, so the composed map overran, hit the summary fallback below, and emitted
                // six characters of a 900-character budget — LESS detail than the same map at 600. That
                // fallback is why neither bug shows up as an overrun; it converts overspend into silent total
                // loss, which is exactly the failure mode this PR exists to remove.
                int available = Math.Max(0, budget - "map<,>".Length);
                int half = available / 2;
                string keyRender = RenderBounded(mapType.KeyType, half, depth + 1);
                string valueRender = RenderBounded(
                    mapType.ValueType, Math.Max(0, available - keyRender.Length), depth + 1);

                // Whatever the value did not want goes back to the key, so a small value does not cost the
                // key its detail: map<wideStruct,int> must show MORE of its key than map<wideStruct,wideStruct>
                // at the same budget, because there is more left over for it.
                //
                // The reclaim is unconditional on there being slack. It was once also gated on the key having
                // filled its half, which sounds right and meant it essentially never ran: a struct walk stops
                // at a field boundary, so the key lands just UNDER its allowance and the gate was false almost
                // always. map<wideStruct,int> then rendered under half its budget while the key's natural
                // render would have fitted whole, which is the property
                // AMapWithACheapValue_SpendsTheSlackOnItsKey now asserts instead of this sentence.
                //
                // A shortest-first variant was tried here instead, mirroring BoundTypes exactly. An earlier
                // oracle rejected it for showing zero field names at budget 82 where budget 81 showed one —
                // but that oracle asserted monotonicity of the FIELD-NAME COUNT, which is not a true property
                // of this render (a nested map legitimately trades names for structure as its budget grows),
                // and it was removed for that reason. Re-tested at this HEAD, shortest-first passes every
                // property asserted here. So the choice between it and the half-split below is NOT pinned and
                // is not claimed to be: what is pinned is the OUTCOME — the value is sized against what the
                // key actually took (EveryCompositeRender_FitsTheBudgetItWasGiven) and leftover space goes back
                // to the key (AMapWithACheapValue_SpendsTheSlackOnItsKey) — which both strategies must satisfy.
                int slack = available - keyRender.Length - valueRender.Length;
                if (slack > 0)
                {
                    // Defensive, and honestly labelled as such. RenderBounded's contract permits a return
                    // LONGER than the budget it was given, because its summary fallback has a floor of its
                    // own; this check stops such a return from overrunning the map and collapsing it. It is
                    // 0-RED, and unlike the last-item exemption there is no proof it is dead: every cell of
                    // EveryCompositeRender_FitsTheBudgetItWasGiven and AMapWithACheapValue_SpendsTheSlackOn
                    // ItsKey is byte-identical with it removed, so no reachable shape exercises it, but the
                    // contract that motivates it is real. Named rather than counted, for the reason given on
                    // the struct child's fallback above. Kept as a
                    // postcondition on a call that may legitimately violate the caller's assumption, not as
                    // a claim that it fires. (An earlier comment here justified it by a field-name-count
                    // monotonicity oracle; that oracle was unsound and has been removed.)
                    string reclaimed = RenderBounded(
                        mapType.KeyType, keyRender.Length + slack, depth + 1);
                    if (reclaimed.Length + valueRender.Length <= available)
                    {
                        keyRender = reclaimed;
                    }
                }

                detailed = $"map<{keyRender},{valueRender}>";
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
