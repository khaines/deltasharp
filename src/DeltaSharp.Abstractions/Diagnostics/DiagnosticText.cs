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
        // anything the slow path would rewrite, then hand back the SAME instance. Surrogates bail to the slow
        // path even when the pair is well-formed (where it would copy them verbatim): pair validation is the
        // slow path's job, and keeping the fast path a single flat scan keeps it obviously equivalent.
        if (raw.Length <= maxLength)
        {
            bool clean = true;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (IsInjectionUnsafe(c) || char.IsSurrogate(c))
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
                // A valid high+low surrogate pair (both within the cap — the cap back-off above guarantees a
                // high surrogate is never the last retained char) is a legal astral code point: keep it
                // verbatim (neither half is a control/separator). A high surrogate NOT followed by a low
                // surrogate is a LONE (malformed) surrogate — neutralize it.
                if (i + 1 < cap && char.IsLowSurrogate(raw[i + 1]))
                {
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

            builder.Append(IsInjectionUnsafe(c) ? '\uFFFD' : c);
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

    // A character is neutralized if it is a C0/C1 control (category Cc — CR/LF/NUL/tab/NEL), a Unicode
    // LINE/PARAGRAPH SEPARATOR (U+2028/U+2029, categories Zl/Zp) which several renderers and log viewers treat
    // as a newline, or a FORMAT character (category Cf).
    //
    // Cf matters even though it cannot forge a new log RECORD, because it serves the same objective this
    // primitive exists to defeat — making the log lie. U+202E (RTL override) visually reverses the remainder of
    // a rendered line, so an attacker can make a hostile token read as something else entirely during incident
    // triage; U+200B/U+200E/U+FEFF/U+00AD hide or silently reorder text in exactly the surfaces (log viewers,
    // terminals, issue trackers) a fault message is read in.
    //
    // DELIBERATE TRADE, do not "fix" this back: U+200D (ZWJ) and U+200C (ZWNJ) are also Cf and ARE semantically
    // required for correct Indic/Arabic shaping and emoji sequences, so a blanket Cf ban slightly degrades how
    // an exotic-but-legitimate identifier RENDERS. That trade is right here for two reasons. (1) A diagnostic
    // message is not a text-rendering surface — it is prose read by a human triaging a failure, and a
    // zero-width joiner carries no information a reader can act on, while an RTL override actively misleads
    // them. (2) The raw value is never lost: it stays verbatim on the typed, machine-readable channel
    // (AnalysisException.Reference/RootColumn/Candidates, SqlToken.Text), which is what any caller that needs
    // the exact bytes should read. Neutralizing the whole category keeps the rule stated in one place and
    // trivially auditable, rather than an allow-list that must be revisited each Unicode revision.
    private static bool IsInjectionUnsafe(char c) =>
        char.IsControl(c)
        || char.GetUnicodeCategory(c)
            is UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Format;
}
