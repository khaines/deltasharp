using System.Globalization;
using System.Text;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Message-hygiene helpers shared across the Storage write/commit/optimize surfaces (#667, following the
/// read-path #653 hardening). The DeltaSharp storage layer <b>cannot redact</b> (Core's
/// <c>SecretRedaction</c> is internal and unreachable here), so a fault message must never carry an
/// attacker-controllable or unbounded token in a form that could inject into a structured-log sink
/// (CRLF/control chars) or disclose file layout. Two postures are used, matching the token:
/// <list type="bullet">
/// <item>attacker-controllable/foreign content (a file path from a possibly-poisoned <c>_delta_log</c>, a
/// physical data schema) is <b>dropped</b> from the <c>Message</c> and, where useful, kept on a typed
/// property a caller can read and redact at its own sink;</item>
/// <item>a bounded, caller-authored identifier (an own-schema column name) is echoed through
/// <see cref="Sanitize"/>, which strips control characters and caps length — closing the log-injection
/// vector while preserving the diagnostic name.</item>
/// </list>
/// This is the same idiom as <c>ColumnMapping.SanitizeEchoedToken</c> (#516), lifted to a single shared
/// helper so the postures cannot drift across surfaces.
/// </summary>
internal static class DiagnosticText
{
    /// <summary>The default cap for an echoed identifier — generous enough for any real dotted column path
    /// (a physical name is <c>col-&lt;uuid&gt;</c> = 40 chars; a nested logical path is typically short) yet
    /// bounded so a crafted name cannot blow up a log line.</summary>
    internal const int DefaultMaxLength = 128;

    /// <summary>The cap for an echoed <b>table-property / configuration VALUE</b> (e.g. a
    /// <c>metaData.configuration</c> entry such as <c>delta.appendOnly</c> or a retention duration) — the
    /// single source of truth shared by every config-value echo so the bound cannot drift across surfaces
    /// (#666). Tighter than <see cref="DefaultMaxLength"/> because a valid property value is a short protocol
    /// string (a boolean or a calendar-interval literal), unlike a dotted column path.</summary>
    internal const int ConfigTokenMaxLength = 64;

    /// <summary>The maximum number of items rendered from an attacker-influenceable LIST (a foreign table's
    /// unsupported reader/writer features, or the CHECK constraints dependent on a changed column) before the
    /// remainder is elided as <c>… (+N more)</c>. Bounds the AGGREGATE message length so a hostile list of
    /// thousands of (individually per-item-capped) entries cannot flood a log line (#666).</summary>
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
