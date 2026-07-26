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

        string capped = raw.Length <= maxLength
            ? raw
            : string.Concat(raw.AsSpan(0, maxLength), "…");
        var builder = new StringBuilder(capped.Length);
        foreach (char c in capped)
        {
            builder.Append(char.IsControl(c) ? '\uFFFD' : c);
        }

        return builder.ToString();
    }
}
