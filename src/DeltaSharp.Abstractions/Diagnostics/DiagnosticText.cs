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
    /// Renders <paramref name="items"/> into <b>at most <paramref name="budget"/> characters, with one
    /// stated exception</b>, showing as many as fit in full and appending <c>… (+N more)</c> for the
    /// remainder. When not even one item fits, the bare <c>… (+N more)</c> is returned <b>whatever the
    /// budget</b> — see the contract note in the remarks, which every caller must honour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists alongside <see cref="SanitizeAndJoin"/>.</b> That primitive takes a fixed item cap
    /// and a fixed per-item cap, which means a listing can elide while most of the message budget goes
    /// unused: a listing wider than the item cap dropped names while leaving over half the message budget
    /// spare. Eliding while the budget is spare discards information for no benefit — and it
    /// is <em>worse</em> than the unbounded original for every width that used to fit. This overload derives
    /// both bounds from the space actually available, so a listing is elided only when it genuinely will not
    /// fit, and the common case is untouched by construction rather than by a well-chosen constant.
    /// </para>
    /// <para>
    /// The per-item allowance is <c>budget / count</c> clamped to
    /// [<paramref name="minItemLength"/>, <paramref name="maxItemLength"/>]; items shorter than their
    /// allowance simply leave room for more items, so a listing of ordinary names fills the budget rather
    /// than stopping at an arbitrary count.
    /// </para>
    /// <para>
    /// <b>The contract, exactly.</b> The result is within <paramref name="budget"/> in every case except
    /// one: if not even a single item plus its marker fits, the bare <c>… (+N more)</c> is returned and may
    /// exceed the budget — by up to the marker's own width, which grows with the digit count of
    /// <c>N</c>. That is deliberate. The alternative is truncating the marker, which destroys the count,
    /// and the count is the one thing this whole family of bounds exists to protect: a listing that admits
    /// how much it dropped is useful, a silently empty one is not.
    /// </para>
    /// <para>
    /// <b>So the caller owes the marker its room.</b> A caller that subtracts nothing and trusts the budget
    /// as a ceiling can be handed a longer string than it asked for. The one caller today
    /// (<c>AnalysisException</c>) discharges this through its <c>TokenBudget</c>, which reserves exactly
    /// <see cref="OverflowMarkerLength(int)"/> per listing before any budget is handed out. This is written
    /// down because the primitive was hoisted into Abstractions precisely so Core, Storage and Engine could
    /// share it: having a single caller is an accident of timing, not a property of the design, and the next
    /// caller will read this summary rather than the loop. <c>TheMarkerExemption_IsTheOnlyWayThe…</c> test
    /// in the shared contract suite pins the exemption's width so it cannot silently widen.
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

        int allowance = Math.Clamp(budget / items.Count, minItemLength, maxItemLength);
        string[] rendered = new string[items.Count];
        int[] prefix = new int[items.Count + 1];
        for (int i = 0; i < items.Count; i++)
        {
            rendered[i] = render(items[i], allowance);
            prefix[i + 1] = prefix[i] + rendered[i].Length + (i > 0 ? separator.Length : 0);
        }

        // Show the LARGEST number of items whose listing, together with the marker naming exactly the ones it
        // hides, is within budget. Stated that way the answer is a search rather than a walk, and the walk is
        // what kept being subtly wrong.
        //
        // The walk charged every item a reserve of OverflowMarkerLength(items.Count) — the marker for hiding
        // ALL of them — when what it actually needed was the marker for the few that would really be left.
        // A wider count makes a wider number makes a bigger reserve, so it stopped early and hid items it had
        // room for. The earlier
        // "does the whole thing fit" pre-check patched exactly one cell of that — the case where nothing is
        // hidden — and could not help at any width where something genuinely is. Charging the true remaining
        // count removes the reserve concept altogether, and with it the fits-entirely special case, since
        // k == items.Count is simply the candidate that carries no marker.
        //
        // Scanning downward returns the maximum feasible k by construction, which matters because cost is not
        // monotonic in k: taking one more item lengthens the listing but can shorten the marker, so a "stop at
        // the first failure" walk can stop just below a k that fits.
        //
        // The counts that fit are therefore NOT a prefix, and that is the whole content of the property. The
        // step that takes the last item deletes the marker outright and refunds its separator with it, so an
        // item that costs less than that refund creates a k that fails while k+1 succeeds. Above the refund
        // no such k exists and the two walk shapes are the same function — which is how the identical scan
        // in CoercionHelpers.RenderBounded stayed 0-RED for two rounds while this one was pinned. Not a
        // missing axis: a fixture VALUE.
        //
        // The threshold is deliberately NOT written here as a number. This same sentence carried one in the
        // Core sibling and it was too large by one, which is the direction that sends a maintainer choosing
        // a corpus by it to a corpus that cannot see the defect — a comment that fails by being followed.
        // Both pins now compute the refund from the marker itself and assert it in both directions: that
        // nothing discriminates at or above it, and that something discriminates immediately below it.
        //
        // The PROPERTY pinned here is that this scan yields the LARGEST feasible k, not the k before the
        // first infeasible one. Making the scan stop at its first infeasible k — the surgical mutant for
        // exactly that property — is caught by several tests, not one, including the contract pin named
        // below, which asserts that its corpus still contains such a k rather than assuming it:
        // SharedDiagnosticTextContractTests.TheListingCountIsTheLargestFeasibleCount_NotOneShortOfTheFirst-
        // InfeasibleOne, whose Core sibling states the same property in the same shape.
        //
        // The name is a pointer, not the coverage claim; the property above is. An earlier revision of this
        // comment named one test as though a name were a unit of coverage, and elsewhere in this change that
        // habit hid a genuinely unpinned invariant: one test's name covered two properties and a reviewer
        // discharged both by observing the first. Which mutant is used decides what can be concluded — a
        // coarse one (reversing the scan direction) kills most of this area's tests and attributes nothing.
        for (int k = items.Count; k >= 1; k--)
        {
            int hidden = items.Count - k;
            int cost = prefix[k]
                + (hidden == 0 ? 0 : separator.Length + OverflowMarkerLength(hidden));
            if (cost > budget)
            {
                continue;
            }

            var builder = new StringBuilder(rendered[0]);
            for (int i = 1; i < k; i++)
            {
                builder.Append(separator).Append(rendered[i]);
            }

            return hidden == 0
                ? builder.ToString()
                : builder.Append(separator)
                    .Append(CultureInfo.InvariantCulture, $"… (+{hidden} more)")
                    .ToString();
        }

        // Not even one item and its marker fit, so the count is all that can be said. It is still said: a
        // listing that admits how much it dropped remains useful, whereas a silently empty one does not.
        return string.Create(CultureInfo.InvariantCulture, $"… (+{items.Count} more)");
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
