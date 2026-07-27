using System.Text;
using SharedDiagnosticText = DeltaSharp.Diagnostics.DiagnosticText;

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
/// helper so the postures cannot drift across surfaces. The sanitizing PRIMITIVE itself now lives in
/// <c>DeltaSharp.Abstractions</c> (<c>DeltaSharp.Diagnostics.DiagnosticText</c>) because the
/// <c>DeltaSharp.Core</c> SQL parser needs the identical semantics (#687) and Core must not reference
/// Storage; this type keeps the Storage-specific caps/postures and forwards the primitive.
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
    /// thousands of (individually per-item-capped) entries cannot flood a log line (#666).
    /// <para><b>This is the single authoritative Storage elision bound, on every path.</b> Storage elides
    /// lists two ways — through <see cref="SanitizeAndJoin"/> (e.g. <c>DeltaProtocolException</c>'s
    /// reader/writer feature list) and by reading this constant directly for a hand-rolled listing (e.g.
    /// <c>DeltaConstraintDependentColumnException</c>'s dependent-CHECK listing). The forwarder therefore
    /// passes this constant EXPLICITLY to the shared primitive rather than letting it supply a bound of its
    /// own, so both paths are provably governed by this one declaration and cannot silently desynchronize.
    /// The shared primitive deliberately has no default to inherit (#687 follow-up).</para></summary>
    internal const int MaxEchoedListItems = 16;

    /// <summary>
    /// Bounds and neutralizes an untrusted token before it is interpolated into a diagnostic message: caps
    /// the length (appending an ellipsis when truncated) and replaces every control character with U+FFFD, so
    /// a poisoned value cannot inject newlines/control sequences into a log line or render an unbounded string.
    /// <para>Forwards to the shared <c>DeltaSharp.Abstractions</c> primitive
    /// (<c>DeltaSharp.Diagnostics.DiagnosticText</c>) so the Storage config-value surfaces and the
    /// <c>DeltaSharp.Core</c> SQL-parser diagnostics (#687) sanitize <b>identically</b> — Core cannot reference
    /// Storage (wrong layering direction), so the one implementation lives in the assembly both reference.</para>
    /// </summary>
    /// <param name="raw">The token to sanitize. A <see langword="null"/> token renders as the literal
    /// <c>(null)</c> so the message stays well-formed.</param>
    /// <param name="maxLength">The maximum retained length before truncation.</param>
    internal static string Sanitize(string? raw, int maxLength = DefaultMaxLength) =>
        SharedDiagnosticText.Sanitize(raw, maxLength);

    /// <summary>
    /// Sanitizes each token in <paramref name="tokens"/> (per-item bounded via <see cref="Sanitize"/> with
    /// <paramref name="maxItemLength"/>) and joins them with <paramref name="separator"/>, rendering at most
    /// <see cref="MaxEchoedListItems"/> and appending <c>… (+N more)</c> when the list is longer — so an
    /// attacker-supplied LIST (e.g. a foreign table's thousands of forged reader/writer features) cannot flood
    /// a log line even though every element is individually bounded.
    /// </summary>
    internal static string SanitizeAndJoin(IEnumerable<string> tokens, int maxItemLength, string separator = ", ") =>
        SharedDiagnosticText.SanitizeAndJoin(tokens, maxItemLength, MaxEchoedListItems, separator);

    /// <summary>
    /// Renders <paramref name="exception"/> as <c>{TypeName}: {Message}</c> (optionally followed by
    /// <c>(Kind: {kind})</c>) plus its OWN stack trace, deliberately <b>omitting the
    /// <see cref="Exception.InnerException"/> chain</b>. The DeltaSharp storage decode/validation exceptions
    /// scrub attacker-influenceable content from their sanitized <see cref="Exception.Message"/> but retain the
    /// raw underlying cause (e.g. a Parquet.Net message or a JSON parse error over crafted bytes) as the inner
    /// for server-side diagnostics; the default <see cref="Exception.ToString"/> would re-surface that raw
    /// inner (as would the default <c>ILogger.LogError(ex, …)</c> providers, which render <c>ToString()</c>),
    /// re-leaking exactly what <see cref="Exception.Message"/> dropped. This override closes the
    /// <c>ToString()</c>/<c>ILogger</c>-<b>rendering</b> vector (#664): the inner stays attached (reachable via
    /// <see cref="Exception.InnerException"/>) for a debugger / deliberate server-side read, but is never
    /// auto-rendered — and because <see cref="Exception.ToString"/> recurses into an inner via the inner's own
    /// (overridden) <c>ToString()</c>, a covered exception nested inside an outer exception or an
    /// <see cref="AggregateException"/> is suppressed transitively.
    /// <para><b>Residual (by design).</b> This is <c>ToString()</c>-rendering parity with RF-8b, NOT the full
    /// RF-8b treatment: unlike <c>LocalFileSystemBackend.SurfaceFailure</c> (which attaches a <i>synthetic,
    /// sanitized</i> inner so even reflection is safe), these types <b>retain the raw inner object</b>. A
    /// sink that serializes the exception <i>object graph</i> by reflection (e.g. a Serilog exception
    /// destructurer, <c>JsonSerializer.Serialize(ex)</c>) — rather than calling <c>ToString()</c> — can still
    /// walk <see cref="Exception.InnerException"/> and re-surface the raw cause. That is a sink-side
    /// encode-on-write concern: a tenant-visible sink MUST render <c>.Message</c>/<c>.ToString()</c> and MUST
    /// NOT reflect over <see cref="Exception.InnerException"/>, which is server-side-diagnostic-only. No such
    /// reflecting logger exists in this repository today.</para>
    /// </summary>
    internal static string DescribeWithoutInner(Exception exception, string? kind = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var builder = new StringBuilder();
        builder.Append(exception.GetType().ToString()).Append(": ").Append(exception.Message);
        if (kind is not null)
        {
            builder.Append(" (Kind: ").Append(kind).Append(')');
        }

        if (exception.StackTrace is { } stackTrace)
        {
            builder.Append(Environment.NewLine).Append(stackTrace);
        }

        return builder.ToString();
    }
}
