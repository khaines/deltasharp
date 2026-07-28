using System.Globalization;
using System.Text;

namespace DeltaSharp.Diagnostics;

/// <summary>
/// The repository-wide <b>diagnostic-text hygiene primitive</b>: bounds and neutralizes an untrusted token
/// before it is interpolated into an exception message.
/// </summary>
/// <remarks>
/// <para>
/// A DeltaSharp fault message is routinely handed to a structured-log sink. Any token in it that an attacker
/// can author — a value from a hostile <c>_delta_log</c> (a <c>metaData.configuration</c> entry, a
/// <c>delta.constraints.&lt;name&gt;</c> CHECK predicate, a protocol feature name), or the offending lexeme a
/// SQL parser echoes back out of such a predicate — is therefore a <b>log-injection</b> vector (raw CR/LF or
/// other control characters forge log lines) and an <b>unbounded-render</b> vector (a 100&#160;000-character
/// token becomes a 100&#160;000-character log line). <see cref="Sanitize"/> closes both: it replaces every
/// control character (and the Unicode line/paragraph separators several renderers treat as newlines) with
/// U+FFFD and caps the retained length, eliding the remainder with an ellipsis.
/// </para>
/// <para>
/// This lives in <c>DeltaSharp.Abstractions</c> — the one assembly <c>DeltaSharp.Core</c>,
/// <c>DeltaSharp.Engine</c>, and <c>DeltaSharp.Storage</c> all reference — deliberately: the pattern was
/// introduced for the Storage config-value surfaces (#666/#684) and is now also needed by the
/// <c>DeltaSharp.Core</c> SQL parser's diagnostics (#687), which a hostile CHECK predicate reaches. Core must
/// not depend on Storage (wrong layering direction), so the primitive is hoisted here and
/// <c>DeltaSharp.Storage.Delta.DiagnosticText</c> forwards to it — one implementation, so the sanitizing
/// semantics cannot drift between layers.
/// </para>
/// </remarks>
internal static class DiagnosticText
{
    /// <summary>The default cap for an echoed <b>identifier-shaped token</b> — generous enough for any
    /// realistic identifier, dotted path, or protocol key, yet bounded so a crafted token cannot blow up a log
    /// line. This is the primitive's neutral default; each calling layer documents its own reason where it
    /// aliases or overrides it (for example <c>SqlParser.EchoedTokenMaxLength</c> for an echoed SQL lexeme, or
    /// <c>DeltaSharp.Storage.Delta.DiagnosticText.ConfigTokenMaxLength</c> for a table-property value).</summary>
    internal const int DefaultMaxLength = 128;

    /// <summary>
    /// Bounds and neutralizes an untrusted token before it is interpolated into a diagnostic message: caps
    /// the length (appending an ellipsis when truncated) and replaces every control character with U+FFFD, so
    /// a poisoned value cannot inject newlines/control sequences into a log line or render an unbounded string.
    /// </summary>
    /// <param name="raw">The token to sanitize. A <see langword="null"/> token renders as the literal
    /// <c>(null)</c> so the message stays well-formed.</param>
    /// <param name="maxLength">The maximum retained length before truncation.</param>
    internal static string Sanitize(string? raw, int maxLength = DefaultMaxLength)
    {
        if (raw is null)
        {
            return "(null)";
        }

        // Fast path: the overwhelmingly common case is a short, clean token — a real column name, a protocol
        // feature key, an ordinary SQL identifier. #696 also calls this per-row-group x per-nested-column, so
        // an unconditional StringBuilder allocation (measured 160 bytes/call) shows up as real Gen0 pressure on
        // a hot path that previously allocated nothing. Verify the input is already within budget and free of
        // anything the slow path would rewrite, then hand back the SAME instance.
        //
        // LOAD-BEARING, do not "optimize" this into a specialized inline scan: the fast path is safe precisely
        // because it calls THE SAME IsInjectionUnsafe predicate as the slow path rather than restating the
        // rule. That sharing IS the control. A hand-inlined character test here would be a second statement of
        // the rule that the compiler cannot keep in sync — exactly the drift this file exists to prevent.
        //
        // Surrogates bail to the slow path even when the pair is well-formed (where the slow path may keep it
        // verbatim): pair validation and astral classification are the slow path's job, and keeping this a
        // single flat scan over code UNITS keeps its equivalence inspectable rather than argued. The
        // short-circuit order also matters — char.IsSurrogate runs first, so the Rune constructor below never
        // sees a surrogate (it would throw).
        if (raw.Length <= maxLength)
        {
            bool clean = true;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (char.IsSurrogate(c) || IsInjectionUnsafe(new Rune(c)))
                {
                    clean = false;
                    break;
                }
            }

            if (clean)
            {
                return raw;
            }
        }

        // Cap the length without splitting a UTF-16 surrogate pair (a lone surrogate is malformed text).
        int cap = raw.Length;
        if (maxLength >= 0 && cap > maxLength)
        {
            cap = maxLength;
            if (cap > 0 && char.IsHighSurrogate(raw[cap - 1]))
            {
                cap--;
            }
        }

        bool truncated = cap < raw.Length;
        var builder = new StringBuilder(cap + (truncated ? 1 : 0));
        for (int i = 0; i < cap; i++)
        {
            char c = raw[i];
            if (char.IsHighSurrogate(c))
            {
                // A high surrogate followed by a low surrogate (both within the cap — the back-off above
                // guarantees a high surrogate is never the last retained char) is a well-formed astral CODE
                // POINT. Decode it and run the SAME classification every other code point gets: an astral
                // format character is neutralized to a single U+FFFD (one replacement for the code point, not
                // one per code unit), and legitimate astral text — emoji, CJK extensions — is kept verbatim.
                // A high surrogate NOT followed by a low surrogate is a LONE (malformed) surrogate.
                if (i + 1 < cap && char.IsLowSurrogate(raw[i + 1]))
                {
                    var pair = new Rune(c, raw[i + 1]);
                    if (IsInjectionUnsafe(pair))
                    {
                        builder.Append('\uFFFD');
                        i++;
                        continue;
                    }

                    builder.Append(c);
                    builder.Append(raw[i + 1]);
                    i++;
                    continue;
                }

                builder.Append('\uFFFD');
                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                // Reached only when this low surrogate has no preceding high surrogate (a valid pair is consumed
                // above) — a lone (malformed) surrogate.
                builder.Append('\uFFFD');
                continue;
            }

            builder.Append(IsInjectionUnsafe(new Rune(c)) ? '\uFFFD' : c);
        }

        if (truncated)
        {
            builder.Append('…');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Sanitizes each token in <paramref name="tokens"/> (per-item bounded via <see cref="Sanitize"/> with
    /// <paramref name="maxItemLength"/>) and joins them with <paramref name="separator"/>, rendering at most
    /// <paramref name="maxItems"/> and appending <c>… (+N more)</c> when the list is longer — so an
    /// attacker-supplied LIST (e.g. a foreign table's thousands of forged reader/writer features) cannot flood
    /// a log line even though every element is individually bounded.
    /// </summary>
    /// <remarks>
    /// <b>Both bounds are caller-supplied on purpose.</b> This primitive owns the ALGORITHM; the calling layer
    /// owns its POSTURE — the per-item length cap and the item count cap are the layer's policy, and only the
    /// layer knows what a legitimate list looks like there (a Delta reader/writer feature set is not a SQL
    /// select list). Neither has a default here, so a caller cannot silently inherit a bound it did not choose:
    /// a layer that already declares its own cap (e.g. <c>DeltaSharp.Storage.Delta.DiagnosticText</c>'s
    /// <c>MaxEchoedListItems</c>, which that layer ALSO reads directly for its hand-rolled elisions) must pass
    /// it, so its declared constant is provably the one in force on every path. Adding a default here would
    /// re-create exactly the silent-drift hazard the shared primitive exists to eliminate: two independent
    /// constants, one name, and no signal when they diverge.
    /// </remarks>
    /// <param name="tokens">The untrusted tokens to render.</param>
    /// <param name="maxItemLength">The per-item length cap handed to <see cref="Sanitize"/>.</param>
    /// <param name="maxItems">The maximum number of items rendered before the remainder is elided.</param>
    /// <param name="separator">The separator placed between rendered items.</param>
    internal static string SanitizeAndJoin(
        IEnumerable<string> tokens, int maxItemLength, int maxItems, string separator = ", ")
    {
        IReadOnlyList<string> list = tokens as IReadOnlyList<string> ?? tokens.ToList();
        int shown = Math.Min(list.Count, maxItems);
        var builder = new StringBuilder();
        for (int i = 0; i < shown; i++)
        {
            if (i > 0)
            {
                builder.Append(separator);
            }

            builder.Append(Sanitize(list[i], maxItemLength));
        }

        if (list.Count > shown)
        {
            builder.Append(separator).Append(CultureInfo.InvariantCulture, $"… (+{list.Count - shown} more)");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders <paramref name="items"/> into <b>at most <paramref name="budget"/> characters</b>, showing as
    /// many as fit in full and appending <c>… (+N more)</c> for the remainder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists alongside <see cref="SanitizeAndJoin"/>.</b> That primitive takes a fixed item cap
    /// and a fixed per-item cap, which means a listing can elide while most of the message budget goes
    /// unused: a 30-item listing capped at 20 dropped 10 items to render 491 characters against a 1024-char
    /// ceiling. Eliding while more than half the budget is spare discards information for no benefit — and it
    /// is <em>worse</em> than the unbounded original for every width that used to fit. This overload derives
    /// both bounds from the space actually available, so a listing is elided only when it genuinely will not
    /// fit, and the common case is untouched by construction rather than by a well-chosen constant.
    /// </para>
    /// <para>
    /// The per-item allowance is <c>budget / count</c> clamped to
    /// [<paramref name="minItemLength"/>, <paramref name="maxItemLength"/>]; items shorter than their
    /// allowance simply leave room for more items, so a listing of ordinary names fills the budget rather
    /// than stopping at an arbitrary count. The result never exceeds <paramref name="budget"/>: room for the
    /// overflow suffix is reserved before each item is committed, so the count can never be the thing cut.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The item type, rendered by <paramref name="render"/>.</typeparam>
    /// <param name="items">The untrusted items to render.</param>
    /// <param name="render">Renders one item within a supplied character allowance. Taking the allowance as a
    /// parameter is what lets a caller decide which PART of a composite item to sacrifice — an
    /// <c>name#exprId</c> candidate must lose name characters, never the discriminating identifier.</param>
    /// <param name="budget">The hard ceiling on the composed result.</param>
    /// <param name="minItemLength">Floor on the per-item allowance, so a wide listing still names each item
    /// recognizably.</param>
    /// <param name="maxItemLength">Ceiling on the per-item allowance, so one pathological item cannot consume
    /// the whole listing.</param>
    /// <param name="separator">The separator placed between rendered items.</param>
    internal static string SanitizeToBudget<T>(
        IReadOnlyList<T> items,
        Func<T, int, string> render,
        int budget,
        int minItemLength,
        int maxItemLength,
        string separator = ", ")
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(separator);

        if (items.Count == 0)
        {
            return string.Empty;
        }

        // Upper bound on the overflow suffix, reserved so the count is never what gets cut.
        int reserve = separator.Length + OverflowMarkerLength(items.Count);
        int allowance = Math.Clamp(budget / items.Count, minItemLength, maxItemLength);
        string[] rendered = new string[items.Count];
        int whole = 0;
        for (int i = 0; i < items.Count; i++)
        {
            rendered[i] = render(items[i], allowance);
            whole += rendered[i].Length + (i > 0 ? separator.Length : 0);
        }

        // FITS-ENTIRELY PRE-CHECK, and it has to come before the greedy walk rather than fall out of it.
        // The walk charges every non-final item for an overflow suffix that will not exist if the listing
        // turns out to fit, so it can stop short while the complete listing would have been inside the
        // budget all along: at 107 seven-character names it elided two of them at 1015 characters when the
        // full listing renders in 1020. Discarding a user's own column names with budget to spare is worse
        // than the unbounded original this bound replaced — the exact disqualifier this design is built on.
        // The reserve itself stays: it is what keeps the count from being cut once the listing genuinely does
        // not fit, and it is only wrong to charge it when nothing will overflow. Setting `reserve` to 0
        // breaks EveryListComposingFactory_StaysUnderTheBackstop, EveryListComposingFactory_ReportsAnOverflow-
        // Count, NoFreeProseToken_CanCrowdOutAListingsOverflowCount, AmbiguousReference_BoundsItsCandidateList_
        // WithAnAccurateCount and ListingBudget_IsSpentBeforeAnythingIsElided_AcrossTheProductOfWidthAndName-
        // Length.
        //
        // Those are named rather than counted deliberately. A RED count is a claim about the SUITE, so it has
        // to be re-tuned whenever coverage legitimately grows, and every drift then looks exactly like a
        // regression — this comment said "10" and was 14 within one round. Names are a claim about the
        // PROPERTY: they do not move when the suite grows, they say what actually dies, and a stale one is a
        // filter that matches nothing rather than a sentence that is quietly wrong. It is the same reason the
        // listing tests assert a constant-free count oracle instead of a literal (+N more).
        //
        // The 0 RED figures elsewhere in this file and in CoercionHelpers are a different kind of claim and
        // are left as numbers on purpose: they assert a mutant is EQUIVALENT, so 0 is the whole content of the
        // claim, it cannot drift upward as coverage grows without the equivalence itself having become false,
        // and that falsification is precisely the signal wanted. Do not "correct" them into test names.
        if (whole <= budget)
        {
            return string.Join(separator, rendered);
        }

        var builder = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < items.Count; i++)
        {
            int addition = (shown > 0 ? separator.Length : 0) + rendered[i].Length;

            // Every item is charged for the overflow suffix, with no exemption for the last. There used to be
            // one, on the reasoning that taking the final item leaves no remainder to report — true, but now
            // unreachable: this loop only runs when the complete listing does NOT fit, and admitting the last
            // item would mean exactly that it does. Deleting the exemption is 0 RED, which is the correct
            // result for dead code rather than a gap in the corpus. The pre-check above is what actually
            // delivers the "spend the whole budget" property the exemption was reaching for, and it delivers
            // it for every item rather than only the final one.
            if (builder.Length + addition + reserve > budget)
            {
                break;
            }

            if (shown > 0)
            {
                builder.Append(separator);
            }

            builder.Append(rendered[i]);
            shown++;
        }

        if (shown == items.Count)
        {
            return builder.ToString();
        }

        return (shown == 0 ? new StringBuilder() : builder.Append(separator))
            .Append(CultureInfo.InvariantCulture, $"… (+{items.Count - shown} more)")
            .ToString();
    }

    /// <summary>
    /// The exact width of the <c>… (+N more)</c> marker for <paramref name="hidden"/> dropped items — the
    /// space a listing needs in order to be able to say <em>anything at all</em>.
    /// </summary>
    /// <remarks>
    /// Stated here, next to the marker it measures, because a caller that reserves room for the count must
    /// reserve the <b>same</b> amount the renderer will later spend. Two independent expressions of one
    /// width is the drift hazard that a shared constant exists to remove.
    /// </remarks>
    internal static int OverflowMarkerLength(int hidden) =>
        string.Create(CultureInfo.InvariantCulture, $"… (+{hidden} more)").Length;

    /// <summary>Adapter exposing <see cref="Sanitize"/> in the <c>(item, allowance)</c> shape
    /// <see cref="SanitizeToBudget{T}(IReadOnlyList{T}, Func{T, int, string}, int, int, int, string)"/>
    /// expects, so a plain string listing needs no lambda at every call site.</summary>
    internal static string SanitizeTo(string raw, int maxLength) => Sanitize(raw, maxLength);

    /// <summary>String overload of
    /// <see cref="SanitizeToBudget{T}(IReadOnlyList{T}, Func{T, int, string}, int, int, int, string)"/>,
    /// rendering each item with <see cref="Sanitize"/>.</summary>
    internal static string SanitizeToBudget(
        IReadOnlyList<string> items,
        int budget,
        int minItemLength,
        int maxItemLength,
        string separator = ", ") =>
        SanitizeToBudget(items, Sanitize, budget, minItemLength, maxItemLength, separator);

    // THE RULE, stated exactly once. A code point is neutralized if it is a C0/C1 control (category Cc —
    // CR/LF/NUL/tab/NEL), a Unicode LINE or PARAGRAPH SEPARATOR (U+2028/U+2029, categories Zl/Zp) which
    // several renderers and log viewers treat as a newline, or a FORMAT character (category Cf).
    //
    // The parameter is a Rune, not a char, ON PURPOSE. A char is a UTF-16 code UNIT, and a code-unit predicate
    // structurally CANNOT classify an astral code point — a surrogate is category Cs, never Cf, so a char-wise
    // test silently answers "safe" for every astral character no matter what the rule says. Cc, Zl and Zp are
    // entirely BMP, so a code-unit predicate happened to be complete for them; Cf is not (the TAG block
    // U+E0020–U+E007F, U+110BD, U+110CD, U+13430–U+1343F, U+1BCA0–U+1BCA3, U+1D173–U+1D17A), and adding Cf
    // therefore turned a complete rule into a half-implemented one. Taking a Rune makes the model match the
    // domain, so BOTH loops below and the fast path share one definition and there is nothing to keep in sync.
    // A comment saying "remember to mirror this" is not a control; a single call site is.
    //
    // Cf matters even though it cannot forge a new log RECORD, because it serves the same objective this
    // primitive exists to defeat — making the log lie. U+202E (RTL override) visually reverses the remainder of
    // a rendered line, so an attacker can make a hostile token read as something else entirely during incident
    // triage; U+200B/U+200E/U+FEFF/U+00AD hide or silently reorder text in exactly the surfaces (log viewers,
    // terminals, issue trackers) a fault message is read in. The TAG block is worse still: it renders as
    // NOTHING AT ALL, so an operator pasting a message into a ticket carries an invisible arbitrary-ASCII
    // payload along with it — strictly more deceptive than U+202E, which at least looks odd.
    //
    // DELIBERATE TRADE, do not "fix" this back: U+200D (ZWJ) and U+200C (ZWNJ) are also Cf and ARE semantically
    // required for correct Indic/Arabic shaping and emoji sequences, so a blanket Cf ban slightly degrades how
    // an exotic-but-legitimate identifier RENDERS. That trade is right here for three reasons. (1) A diagnostic
    // message is not a text-rendering surface — it is prose read by a human triaging a failure, and a
    // zero-width joiner carries no information a reader can act on, while an RTL override actively misleads
    // them. (2) The raw value is never lost: it stays verbatim on the typed, machine-readable channel
    // (AnalysisException.Reference/RootColumn/Candidates, SqlToken.Text), which is what any caller that needs
    // the exact bytes should read. (3) THE CATEGORY IS THE STABLE CONTRACT. Carving out an allow-list for
    // ZWJ/ZWNJ would have to be re-audited against every Unicode revision as new format characters are
    // assigned; "the category" does not. Security's round-3 sweep is the evidence — U+061C, U+0600, U+2060,
    // U+2069 and U+FFF9–U+FFFB were all neutralized without anyone having thought of them, precisely because
    // this is a category test and not a list.
    //
    // OUT OF SCOPE, deliberately: combining marks (category Mn — "Zalgo" stacking). A stacked payload does
    // survive, but it is hard-bounded by the length caps every caller applies (128 per token, 512 for a parse
    // diagnostic, 1024 for an analysis one), the harm is a transient visual smear rather than forgery or
    // smuggling, and unlike Cf the category is genuinely required for ordinary text in many scripts. Cf earns a
    // blanket ban because its legitimate uses are invisible-by-design; Mn does not.
    private static bool IsInjectionUnsafe(Rune rune) =>
        Rune.GetUnicodeCategory(rune)
            is UnicodeCategory.Control
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Format;
}
