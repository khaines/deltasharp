using System.Collections.Generic;
using DeltaSharp.Diagnostics;
using DeltaSharp.Plans;

namespace DeltaSharp;

/// <summary>
/// The deterministic public error <see cref="SparkSession.Sql(string)"/> throws when it cannot lower
/// a statement into the shared logical plan — either because the text is malformed
/// (<see cref="SqlParseErrorKind.SyntaxError"/>) or because it uses a construct the M1 SQL door does
/// not implement yet (<see cref="SqlParseErrorKind.UnsupportedFeature"/>, AC2).
/// </summary>
/// <remarks>
/// <para>
/// It is raised at <b>parse time</b>, before any analysis or execution, so an unsupported or
/// malformed query can never reach a backend (AC2 — no execution is invoked). Mirrors Apache Spark's
/// <c>ParseException</c>; the message is built only from deterministic inputs (the offending token /
/// construct and its 1-based source position) so it is stable and catchable. The structured
/// <see cref="ErrorKind"/> and <see cref="Construct"/> let callers branch without matching text. See
/// <c>docs/engineering/design/sql-door.md</c>.
/// </para>
/// </remarks>
public sealed class SqlParseException : Exception
{
    /// <summary>Initializes a new instance (kind defaults to <see cref="SqlParseErrorKind.SyntaxError"/>).</summary>
    public SqlParseException()
    {
    }

    /// <summary>Initializes a new instance with a precise <paramref name="message"/>.</summary>
    /// <param name="message">The deterministic error message. <b>It is rewritten:</b> injection-unsafe
    /// characters (Unicode categories <c>Cc</c>, <c>Cf</c>, <c>Zl</c>, <c>Zp</c>, plus lone surrogates) are
    /// replaced with U+FFFD and the result is capped — see <see cref="MaxMessageLength"/> for why this applies
    /// to every message-taking constructor and not just to the internal factories. A single-line message of
    /// ordinary length is returned unchanged.</param>
    public SqlParseException(string message)
        : base(Bounded(message))
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    /// <param name="message">The deterministic error message. <b>It is rewritten:</b> injection-unsafe
    /// characters (Unicode categories <c>Cc</c>, <c>Cf</c>, <c>Zl</c>, <c>Zp</c>, plus lone surrogates) are
    /// replaced with U+FFFD and the result is capped — see <see cref="MaxMessageLength"/>. A single-line
    /// message of ordinary length is returned unchanged. The <paramref name="innerException"/>'s message is
    /// NOT touched.</param>
    /// <param name="innerException">The exception that caused this error.</param>
    public SqlParseException(string message, Exception? innerException)
        : base(Bounded(message), innerException)
    {
    }

    private SqlParseException(
        string message, SqlParseErrorKind kind, string? construct, Exception? innerException = null)
        : base(Bounded(message), innerException)
    {
        ErrorKind = kind;
        Construct = construct is null ? null : DiagnosticText.Sanitize(construct, MaxConstructLength);
    }

    /// <summary>The structured reason the statement was rejected.</summary>
    public SqlParseErrorKind ErrorKind { get; }

    /// <summary>
    /// The named construct that is not supported, for an
    /// <see cref="SqlParseErrorKind.UnsupportedFeature"/> failure. This is a short, <b>stable</b>
    /// identifier token (for example <c>JOIN</c>, <c>GROUP_BY</c>, <c>FUNCTION_CALL</c>,
    /// <c>SELECT_DISTINCT</c>) — the programmatic branch key callers switch on. The human-readable
    /// prose lives only in <see cref="Exception.Message"/>. It is <see langword="null"/> for a
    /// <see cref="SqlParseErrorKind.SyntaxError"/>. The value is length-bounded and control-char
    /// sanitized (<see cref="MaxConstructLength"/>) on assignment — a no-op for every registered
    /// construct, but it keeps this property from becoming an unbounded raw-token sink.
    /// </summary>
    public string? Construct { get; }

    /// <summary>
    /// The backstop cap applied to every caller-supplied or parser-composed <see cref="SqlParseException"/>
    /// message. Sized well above the longest diagnostic the parser composes today — its longest fixed prose
    /// plus a per-token-bounded echo is a small fraction of it — so it never truncates a well-behaved message;
    /// it exists only so a call site that forgets to bound its own echo still cannot render an unbounded
    /// message.
    /// </summary>
    /// <remarks>
    /// <para><b>Applied in every message-taking constructor, including both message-taking public ones.</b>
    /// Placing it in the <see cref="Syntax"/> factory alone left it bypassable: this type is public and exposes
    /// public <c>(string)</c> / <c>(string, Exception)</c> constructors that never reach that factory. The
    /// parameterless public constructor carries only a runtime-supplied default message and accepts no input.
    /// Nothing in the repo exploits the message-taking bypass today (all in-repo uses carry fixed prose), but
    /// "the backstop no caller-supplied message can bypass" has to be structurally true, not true by
    /// inspection — so the sanitize moved down to <see cref="Bounded"/>, which every message-taking
    /// construction path runs through.</para>
    /// <para><b>Invariant this creates:</b> every caller-supplied or parser-composed
    /// <see cref="SqlParseException"/> message is single-line by construction — a structural <c>\n</c> a
    /// factory might want for a multi-line listing would itself be neutralized to U+FFFD. That is deliberate
    /// for a parser diagnostic (every one is a single position-tagged line) and is the opposite of the Storage
    /// posture, where per-token sanitization exists precisely so
    /// <c>DeltaConstraintDependentColumnException</c>'s own <c>"\n  "</c> listing survives. A future
    /// multi-line SQL diagnostic must therefore sanitize its untrusted TOKENS and opt out of this whole-message
    /// backstop, not fight it.</para>
    /// <para><b>No nested elision.</b> Two-layer bounding can in principle produce a doubly-elided render
    /// (a per-token <c>…</c> that is then itself truncated by the whole-message cap, yielding <c>… …</c>).
    /// The SQL path is provably immune: the token echo is capped at 128, the fixed prose and the
    /// <c>"Syntax error at position N: "</c> prefix are all inside this 512-character budget, and the longest
    /// message the parser can compose is well under it — so the outer cap never fires on a message the inner
    /// cap already elided. Noted here so a future reader does not re-derive it as a defect.</para>
    /// </remarks>
    internal const int MaxMessageLength = 512;

    /// <summary>The backstop length for the structured <see cref="Construct"/> token. A registered
    /// construct is a short compile-time constant far under this, so the cap is a no-op in practice;
    /// it exists only so <see cref="Construct"/> cannot become an unbounded raw-token sink if a future
    /// or mis-wired producer ever hands it an oversized value (#687).</summary>
    private const int MaxConstructLength = 128;

    /// <summary>Applies the <see cref="MaxMessageLength"/> backstop — the single chokepoint every
    /// message-taking constructor routes through.</summary>
    private static string? Bounded(string? message) =>
        message is null ? null : DiagnosticText.Sanitize(message, MaxMessageLength);

    /// <summary>Builds a deterministic <see cref="SqlParseErrorKind.SyntaxError"/> tagged with the
    /// 1-based source <paramref name="position"/>.</summary>
    /// <remarks>
    /// <para><b>Diagnostic hygiene (#687).</b> A syntax <paramref name="detail"/> normally echoes the
    /// offending lexeme, and SQL text is not always caller-authored: a Delta <c>delta.constraints.&lt;name&gt;</c>
    /// CHECK predicate comes from the table's <c>_delta_log</c> and is parsed on the write path
    /// (<c>ConstraintExpressionFrontend</c>), so a hostile table can choose the token this message renders.
    /// Every detail is therefore passed through <see cref="DiagnosticText.Sanitize"/> before it becomes the
    /// message: control characters (raw CR/LF and friends), Unicode line/paragraph separators, and format
    /// characters (U+202E RTL-override and the zero-width family) are neutralized to U+FFFD so the message
    /// cannot forge or visually rewrite lines in a structured-log sink, and the whole message is capped at
    /// <see cref="MaxMessageLength"/> so a 100&#160;000-character token cannot render a
    /// 100&#160;000-character message. Individual sites additionally bound the token they echo
    /// (<c>SqlParser.Describe</c>), so the cap here is a BACKSTOP no construction path can bypass — it is
    /// deliberately generous enough never to truncate a diagnostic built from a per-token-bounded echo.
    /// Sanitizing is idempotent, so the two layers compose without double-eliding a well-formed message.</para>
    /// </remarks>
    /// <param name="detail">A precise description of what was expected/found.</param>
    /// <param name="position">The 1-based position of the offending token in the source SQL.</param>
    internal static SqlParseException Syntax(string detail, int position) =>
        new($"Syntax error at position {position}: {detail}", SqlParseErrorKind.SyntaxError, null);

    /// <summary>Builds the fixed-prose "expression nesting too deep" diagnostic, chaining the internal
    /// depth exception so a caller catching <see cref="SqlParseException"/> never sees the raw
    /// <c>PlanDepthExceededException</c>/<c>InsufficientExecutionStackException</c>. Overloaded on the
    /// two internal depth types (rather than <see cref="Exception"/>) so the chained inner is
    /// constrained by the COMPILER: an arbitrary <see cref="Exception"/> carrying a lexeme cannot be
    /// passed, closing the <c>ToString()</c>-leak vector by construction. The message is a compile-time
    /// constant with no lexeme (#687).</summary>
    /// <param name="innerException">The internal plan-depth exception to chain.</param>
    internal static SqlParseException NestingTooDeep(PlanDepthExceededException innerException) =>
        new(
            "Syntax error: expression nesting too deep to parse.",
            SqlParseErrorKind.SyntaxError,
            null,
            innerException);

    /// <summary>Depth diagnostic for the physical-stack guard; see the plan-depth overload.</summary>
    /// <param name="innerException">The internal insufficient-stack exception to chain.</param>
    internal static SqlParseException NestingTooDeep(InsufficientExecutionStackException innerException) =>
        new(
            "Syntax error: expression nesting too deep to parse.",
            SqlParseErrorKind.SyntaxError,
            null,
            innerException);

    /// <summary>Builds the fixed-prose CHECK-constraint depth diagnostic. The only interpolation is the
    /// caller-supplied compile-time constant <paramref name="maxDepth"/>; this factory takes no source
    /// text, so it keeps the constraint depth diagnostic off the banned public constructors (#687).</summary>
    /// <param name="maxDepth">The compile-time maximum constraint nesting depth.</param>
    internal static SqlParseException ConstraintNestingTooDeep(int maxDepth) =>
        new(
            $"Constraint expression nests deeper than {maxDepth} levels; a CHECK constraint must be a "
                + "shallow predicate over the table's columns.",
            SqlParseErrorKind.SyntaxError,
            null);

    /// <summary>Builds a deterministic <see cref="SqlParseErrorKind.UnsupportedFeature"/> whose
    /// <see cref="Construct"/> is the stable <paramref name="construct"/> token and whose message
    /// carries the human-readable prose (plus a DataFrame-API onboarding hint when one exists).</summary>
    /// <remarks>The backstop applies here too (every message-taking constructor routes through it), but it is
    /// a <b>no-op</b> in practice and always has been: every <paramref name="construct"/> the parser passes is
    /// a compile-time constant from its own keyword maps (<c>JOIN</c>, <c>GROUP_BY</c>,
    /// <c>FUNCTION_CALL</c>, …) — never
    /// source text — so no attacker-chosen token can reach this message, and the longest message this factory
    /// can compose is comfortably inside <see cref="MaxMessageLength"/> (#687).
    /// <para>The lookup also <b>fails closed</b>: an <paramref name="construct"/> that is not a registered
    /// <see cref="ConstructInfo"/> key renders fixed generic prose rather than echoing the raw token into the
    /// message. The stable token is still preserved verbatim on <see cref="Construct"/> (a programmatic key,
    /// not human-facing text), so this is behaviour-neutral for every construct the parser emits today while
    /// structurally guaranteeing that no future or mis-wired producer can leak an unregistered token verbatim
    /// via this path (#687).</para></remarks>
    /// <param name="construct">The unsupported construct's stable token (for example <c>JOIN</c>).</param>
    /// <param name="position">The 1-based position of the construct in the source SQL.</param>
    internal static SqlParseException Unsupported(string construct, int position)
    {
        (string description, string? hint) = ConstructInfo.TryGetValue(construct, out (string, string?) info)
            ? info
            : ("an unsupported SQL construct", null);

        string message =
            $"Unsupported SQL feature at position {position}: {description} is not supported by the M1 "
            + "SQL door (STORY-04.1.3 / #159). The supported subset is 'SELECT <cols|*> FROM <relation> "
            + "[WHERE <predicate>]'; the full SQL frontend arrives in EPIC-07 (ADR-0007).";

        if (hint is not null)
        {
            message += $" Use {hint} in the DataFrame API instead.";
        }

        return new SqlParseException(message, SqlParseErrorKind.UnsupportedFeature, construct);
    }

    /// <summary>
    /// Maps each stable <see cref="Construct"/> token to its human-readable description (used only in
    /// the <see cref="Exception.Message"/>) and, where a live DataFrame equivalent exists, an
    /// onboarding hint. Keeping the prose here — not in <see cref="Construct"/> — lets the stable
    /// token stay a frozen programmatic key while the message text can evolve freely.
    /// </summary>
    private static readonly Dictionary<string, (string Description, string? DataFrameHint)> ConstructInfo =
        new(System.StringComparer.Ordinal)
        {
            ["JOIN"] = ("a JOIN", "DataFrame.Join(...)"),
            ["IMPLICIT_JOIN"] = ("a comma-separated table list (implicit join)", "DataFrame.Join(...)"),
            ["UNION"] = ("a set operation (UNION/INTERSECT/EXCEPT)", "DataFrame.Union(...)"),
            ["GROUP_BY"] = ("GROUP BY", "DataFrame.GroupBy(...)"),
            ["ORDER_BY"] = ("ORDER BY", "DataFrame.OrderBy(...)"),
            ["SORT_BY"] = ("CLUSTER/DISTRIBUTE/SORT BY", "DataFrame.Sort(...)"),
            ["HAVING"] = ("HAVING", null),
            ["LIMIT"] = ("LIMIT", "DataFrame.Limit(...)"),
            ["OFFSET"] = ("OFFSET", null),
            ["WINDOW"] = ("a WINDOW clause", null),
            ["SELECT_DISTINCT"] = ("SELECT DISTINCT", "DataFrame.Distinct()"),
            ["FUNCTION_CALL"] = ("a function call", null),
            ["SUBQUERY"] = ("a subquery", null),
            ["CTE"] = ("a common table expression (WITH)", null),
            ["VALUES"] = ("a VALUES clause", null),
            ["SHOW"] = ("SHOW", null),
            ["DESCRIBE"] = ("DESCRIBE", null),
            ["EXPLAIN"] = ("EXPLAIN", null),
            ["USE"] = ("USE", null),
            ["SET"] = ("SET", null),
            ["INSERT"] = ("INSERT", null),
            ["UPDATE"] = ("UPDATE", null),
            ["DELETE"] = ("DELETE", null),
            ["MERGE"] = ("MERGE", null),
            ["CREATE"] = ("CREATE", null),
            ["DROP"] = ("DROP", null),
            ["ALTER"] = ("ALTER", null),
            ["TRUNCATE"] = ("TRUNCATE", null),
            ["UNARY_MINUS"] = ("a general unary minus (negation of a non-literal)", null),
            ["DECIMAL_LITERAL"] = ("a decimal-promoted large integer literal", null),
            ["IS_NULL"] = ("an IS [NOT] NULL predicate", "Column.IsNull()/Column.IsNotNull()"),
            ["IN"] = ("an IN predicate", null),
            ["LIKE"] = ("a LIKE predicate", null),
            ["BETWEEN"] = ("a BETWEEN predicate", null),
            ["NOT_IN"] = ("a NOT IN predicate", null),
            ["NOT_LIKE"] = ("a NOT LIKE predicate", null),
            ["NOT_BETWEEN"] = ("a NOT BETWEEN predicate", null),
        };
}
