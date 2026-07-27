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
    /// <summary>The default cap for an echoed identifier-shaped token — generous enough for any real dotted
    /// column path, SQL identifier, or physical <c>col-&lt;uuid&gt;</c> name (40 chars), yet bounded so a
    /// crafted token cannot blow up a log line.</summary>
    internal const int DefaultMaxLength = 128;

    /// <summary>The maximum number of items rendered from an attacker-influenceable LIST before the remainder
    /// is elided as <c>… (+N more)</c>. Bounds the AGGREGATE message length so a hostile list of thousands of
    /// (individually per-item-capped) entries cannot flood a log line.</summary>
    internal const int MaxEchoedListItems = 16;

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
    /// <see cref="MaxEchoedListItems"/> and appending <c>… (+N more)</c> when the list is longer — so an
    /// attacker-supplied LIST (e.g. a foreign table's thousands of forged reader/writer features) cannot flood
    /// a log line even though every element is individually bounded.
    /// </summary>
    internal static string SanitizeAndJoin(IEnumerable<string> tokens, int maxItemLength, string separator = ", ")
    {
        IReadOnlyList<string> list = tokens as IReadOnlyList<string> ?? tokens.ToList();
        int shown = Math.Min(list.Count, MaxEchoedListItems);
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

    // A character is neutralized if it is a C0/C1 control (category Cc — CR/LF/NUL/tab/NEL) OR a Unicode
    // LINE/PARAGRAPH SEPARATOR (U+2028/U+2029, categories Zl/Zp), which several renderers and log viewers treat
    // as a newline — so the full log-injection line-break surface, not just Cc, is closed.
    private static bool IsInjectionUnsafe(char c) =>
        char.IsControl(c)
        || char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}
