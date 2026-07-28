using System.Globalization;
using DeltaSharp.Diagnostics;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Analysis;

/// <summary>
/// The error class of an <see cref="AnalysisException"/>, mirroring the Spark analyzer failures the
/// M1 resolver can raise. Exposed so callers (and tests) can branch on the failure kind without
/// parsing the message text (AC3).
/// </summary>
internal enum AnalysisErrorKind
{
    /// <summary>A relation identifier did not resolve to a registered catalog source.</summary>
    TableOrViewNotFound,

    /// <summary>A column reference did not match any attribute in its input.</summary>
    UnresolvedColumn,

    /// <summary>A column reference matched more than one input attribute.</summary>
    AmbiguousReference,

    /// <summary>The analyzer's post-condition (CheckAnalysis) found the plan still not fully
    /// resolved — an unresolved attribute, star, function, or operator survived the rule pass.</summary>
    UnresolvedPlan,

    /// <summary>A projection element cannot be turned into a named output attribute yet — for
    /// example an alias over an expression whose type is undetermined before type coercion
    /// (STORY-04.5.2 / #171), or an unnamed projection element.</summary>
    UnsupportedProjection,

    /// <summary>A set operation (currently <c>Union</c>) was given inputs whose <b>column counts</b>
    /// differ — a structural (arity) incompatibility. Deep column-type compatibility/coercion is a
    /// separate concern (STORY-04.5.2 / #171).</summary>
    NumberOfColumnsMismatch,

    /// <summary>A using-column or natural <c>Join</c> reached analysis, but the analyzer rule that
    /// desugars its shared columns into an equi-condition is not yet implemented (tracked by #405).
    /// The join node builds fine; only its <i>resolution</i> is deferred.</summary>
    UsingOrNaturalJoinNotImplemented,

    /// <summary>A function call names a function the analyzer's registry does not know.</summary>
    UnresolvedFunction,

    /// <summary>A function call supplied the wrong number of arguments, or an argument whose type
    /// cannot be coerced to the function's expected input type.</summary>
    InvalidFunctionArgument,

    /// <summary>An operator, conditional, or predicate was given operands whose types are not valid
    /// under ADR-0008 — e.g. a boolean in an arithmetic context, a non-boolean branch condition, or
    /// incompatible <c>CASE</c> branch value types.</summary>
    DataTypeMismatch,

    /// <summary>A nested field reference (a struct-field access such as <c>s.f</c>) could not be resolved
    /// because the base is not a struct or the struct has no such field — a <b>structural</b> absence (the
    /// field was dropped/renamed, or the base column was retyped away from a struct). This coalesces Spark's
    /// two <i>distinct</i> field-extraction error classes — <c>INVALID_EXTRACT_BASE_FIELD_TYPE</c> ("need a
    /// complex type") for a non-struct base, and <c>FIELD_NOT_FOUND</c> for a missing field — <b>both</b> of
    /// which Spark keeps SEPARATE from the operand-level <c>DATATYPE_MISMATCH</c>. DeltaSharp's flat taxonomy
    /// coarsens the two into one kind while preserving that separation: it stays distinct from
    /// <see cref="DataTypeMismatch"/> (a predicate-operand type error, e.g. <c>id &gt; 0</c> after a top-level
    /// int→string retype) and from the AMBIGUOUS struct-field case (which stays <see cref="DataTypeMismatch"/> —
    /// there the field exists, the path is merely under-specified). Carries the full nested reference (e.g.
    /// <c>s.f</c>) in <see cref="AnalysisException.Reference"/> so a caller can attribute the failure to the
    /// top-level column (#600).</summary>
    UnresolvedStructField,

    /// <summary>An aggregate function appears outside a valid aggregate context (e.g. in a
    /// <c>Select</c>/<c>Filter</c> with no <c>groupBy</c>/<c>agg</c>).</summary>
    MisplacedAggregate,

    /// <summary>A resolved expression reached the post-condition without a concrete result type — the
    /// coercion pass left it null-typed (a guard against leaking an untyped node downstream).</summary>
    UntypedResolvedExpression,

    /// <summary>A file-format data source (for example a <c>Read.Parquet(path)</c> scan) reached
    /// analysis, but the file-format reader is delivered by EPIC-05 (Delta/Parquet storage) and is not
    /// available in M1. The scan node builds fine; only its <i>resolution</i> is deferred.</summary>
    UnsupportedDataSource,

    /// <summary>A write intent (<c>DataFrame.Write…Save</c>) named a sink <b>format</b> the M1 write door
    /// cannot execute: either an EPIC-05-deferred writer (Delta/Parquet storage — STORY-04.6.3 AC4) or a
    /// format with no M1 write mapping at all (AC3). The <see cref="WriteToSource"/> node builds fine;
    /// only its <i>resolution</i> is rejected, before any output is committed.</summary>
    UnsupportedDataSink,

    /// <summary>A Delta read's time-travel intent is invalid (#499): a version and a timestamp were both
    /// specified (or the same dimension was specified twice via an option and the path suffix), or a
    /// <c>versionAsOf</c>/<c>timestampAsOf</c> value could not be parsed. Spark disallows specifying both
    /// a version and a timestamp; DeltaSharp additionally rejects a redundant/conflicting spec fail-closed
    /// rather than silently ignoring one.</summary>
    InvalidTimeTravelSpec,

    /// <summary>A path-based file-format read (#499, currently <c>delta</c>) was recognized but could not
    /// be resolved: it is not a Delta table, the requested version is out of range or has been vacuumed,
    /// the timestamp is out of range, the log is malformed, or no storage backend is registered. The
    /// <see cref="UnresolvedFileRelation"/> builds fine; only its <i>resolution</i> failed, during analysis,
    /// before any execution backend is reached.</summary>
    FileSourceResolutionFailed,
}

/// <summary>
/// The single analyzer failure type (Spark parity: <c>AnalysisException</c>). It carries a
/// Spark-compatible message and a structured <see cref="Kind"/>, the failing
/// <see cref="Reference"/>, and the <see cref="Candidates"/> that were in scope, so the diagnostic
/// names the offending reference and its candidate columns (AC3).
/// </summary>
/// <remarks>
/// The analyzer raises this — and only this — on any catalog or name-resolution failure. Because it
/// is thrown from the analyze pass, before any physical planning exists, a resolution failure can
/// never reach an execution backend (AC4).
/// </remarks>
internal sealed class AnalysisException : Exception
{
    /// <summary>
    /// The cap applied to the <b>whole composed message</b> of every <see cref="AnalysisException"/> — the
    /// backstop half of the #687 diagnostic-hygiene posture, and the analyzer's analogue of
    /// <c>SqlParseException.MaxMessageLength</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Almost every factory below interpolates text that is attacker-authored in the hostile-<c>_delta_log</c>
    /// threat model: a rendered expression carrying a CHECK predicate's string literals, a column or function
    /// name taken from the log's schema, a storage-side failure reason. Sanitizing the composed message at the
    /// one private constructor closes the class for <b>all</b> of them at once — including any factory added
    /// later — instead of relying on ~20 call sites each remembering to sanitize.
    /// </para>
    /// <para>
    /// Deliberately generous: control-character neutralization is lossless and always applied, while the cap
    /// exists only to make the render independent of attacker input length.
    /// </para>
    /// <para>
    /// <b>It is a backstop, and a factory that can routinely reach it has a bug.</b> An earlier revision of
    /// this comment claimed the cap "comfortably fits a wide-schema listing"; that was measurably false — a
    /// 50-column table already rendered 1107 characters and was silently cut to 1025. Truncating the whole
    /// message destroys the very signal that truncation happened, which on the TRUSTED path (a user's own
    /// schema) turns a self-service fix into a support ticket. Any factory composing a LIST must therefore
    /// bound its own items and report an explicit overflow count, so this cap never elides a list. Every one
    /// of them does: <see cref="UnresolvedColumn"/>, <see cref="AmbiguousReference"/>,
    /// <see cref="TableOrViewNotFound"/>, <see cref="UnsupportedDataSink"/>,
    /// <see cref="UnsupportedWriteFormat"/>, and both users of <c>RenderTypes</c>
    /// (<see cref="UnknownFunction"/>, <see cref="InvalidFunctionArgument"/>). That enumeration is not
    /// maintained by hand — <c>EveryListComposingFactory_ReportsAnOverflowCount</c> ranges over all of them,
    /// because a comment asserting a global property is worth nothing without a test that ranges over the
    /// whole class. A single oversized TOKEN can still reach this cap; that is the cap doing its job, and is
    /// a different thing from eliding a list without saying so.
    /// </para>
    /// <para>
    /// <b>The factories are not the whole class.</b> A second family composes its text in the analyzer
    /// (<c>Analyzer.ExtractStructField</c>, <c>ExpressionCoercion</c>) and hands it to
    /// <see cref="DataTypeMismatch"/> / <see cref="UnresolvedStructField"/> as an opaque <c>detail</c>
    /// string, so no factory-ranging test can see it. Those sites interpolated a <see cref="DataType"/>,
    /// and a type render is itself a <em>recursive collection</em> — <c>struct&lt;f1:int,…&gt;</c> over
    /// user-authored field names — so an ordinary 60-field nested payload struct was cut by this cap with a
    /// bare ellipsis and no count, identically to a 400-field one. They now render through
    /// <see cref="CoercionHelpers.DiagnosticType"/>, and <c>AnalysisExceptionTypeRenderTests</c> ranges over
    /// the diagnostic <em>sites</em> rather than the factories. The general rule, stated once: <b>anything
    /// interpolated into a diagnostic that is a collection — a list, or a type that contains one — must
    /// bound its own elements and report what it dropped.</b>
    /// </para>
    /// <para>
    /// The same correction applies to the mitigation this comment used to cite: <see cref="Reference"/>,
    /// <see cref="RootColumn"/> and <see cref="Candidates"/> do remain <b>unmodified</b> as the structured,
    /// machine-readable channel (they are matched on by callers such as the Delta dependent-column
    /// reclassifier, so they must never be rewritten) — but this type is <c>internal</c>, so they are NOT
    /// reachable by an external consumer and are not a user-facing mitigation for a truncated message. They
    /// justify keeping the raw channel raw; they do not justify a lossy message.
    /// </para>
    /// <para>
    /// <b>Invariant this creates:</b> an <see cref="AnalysisException"/> message is single-line by
    /// construction — a structural <c>\n</c> a factory might want for a multi-line listing would itself be
    /// neutralized to U+FFFD. That is deliberate for an analyzer diagnostic (every one is a single sentence)
    /// and is the opposite of the Storage posture, where per-token sanitization exists precisely so
    /// <c>DeltaConstraintDependentColumnException</c>'s own <c>"\n  "</c> listing survives. A future
    /// multi-line analyzer diagnostic must therefore sanitize its untrusted TOKENS and opt out of this
    /// whole-message backstop, not fight it.
    /// </para>
    /// </remarks>
    internal const int MaxMessageLength = 1024;

    /// <summary>
    /// The number of candidate columns echoed in a "given input columns"/"could be" listing before the
    /// remainder is replaced by an explicit <c>(+N more)</c> count.
    /// </summary>
    /// <remarks>
    /// This is the analyzer's instance of the posture stated at <c>SqlParser.cs</c>: bound the TOKEN so the
    /// PROSE survives. Bounding per item keeps the message bounded <i>and</i> honest — the reader is told
    /// exactly how many candidates they are not seeing — whereas letting
    /// <see cref="MaxMessageLength"/> cut the composed string yields a listing that is truncated with no
    /// indication that it is truncated, or by how much.
    /// </remarks>
    internal const int MaxEchoedCandidates = 14;

    /// <summary>The per-candidate length cap. A single pathological name is elided with <c>…</c> — which is
    /// visible — instead of being allowed to consume the whole listing's budget.</summary>
    /// <remarks>
    /// <para>
    /// <b>Derived, not round.</b> The first version of this was 32, which is BELOW real column-name length —
    /// <c>customer_lifetime_value_rolling_90d</c> is 35 — so a schema of three entirely legitimate names
    /// already elided, and the common case paid for the wide case. Three legitimate names now render in full.
    /// </para>
    /// <para>
    /// The budget must fit the LONGEST-prose list factory, which is <see cref="UnknownFunction"/> at roughly
    /// 140 fixed characters — not <see cref="UnresolvedColumn"/> at 53. Sizing against the short one is how
    /// the first attempt at these numbers ended up 1 character over the cap on the long one:
    /// <c>prose(140) + reference(64+1) + items(14 × 49) + separators(13 × 2) + overflow(~18)</c> = 935,
    /// leaving 89 characters of headroom under <see cref="MaxMessageLength"/>. 48 covers the 35-character
    /// real-world name with room to spare; going to 64 would have bought length at the cost of dropping to
    /// ~10 candidates, and an unrecognizable name is useless at any count, so the trade favours name length
    /// first and count second.
    /// </para>
    /// <para>
    /// <b>Do not trust the arithmetic above — it is a sketch.</b>
    /// <c>EveryListComposingFactory_StaysUnderTheBackstop</c> measures the true worst case for every list
    /// factory and reports the headroom, so changing any of these constants tells you immediately whether
    /// the budget still holds.
    /// </para>
    /// </remarks>
    internal const int MaxEchoedCandidateLength = 48;

    /// <summary>The cap applied to the unresolved/ambiguous name echoed in the MESSAGE. The structured
    /// <see cref="Reference"/> property keeps the full value; this bounds only the prose, so that a long name
    /// cannot push a listing past <see cref="MaxMessageLength"/> and destroy the overflow count with it.
    /// </summary>
    internal const int MaxEchoedReferenceLength = 64;

    /// <summary>The floor on the NAME half of a composite <c>name#exprId</c> candidate, so that clamping can
    /// never invert and leave the identifier as the only surviving part.</summary>
    private const int MinEchoedNameLength = 8;

    /// <summary>
    /// The per-list item budget for a factory that composes <b>two</b> lists in one message, which must
    /// therefore share the single-list allowance rather than each taking it.
    /// </summary>
    /// <remarks>
    /// Found by <c>EveryListComposingFactory_StaysUnderTheBackstop</c>, not by inspection:
    /// <see cref="UnsupportedWriteFormat"/> was the one factory whose budget is doubled, it rendered 1025
    /// characters, and hand-enumerating the factories had missed it. That is the argument for ranging a test
    /// over the class instead of listing its members.
    /// </remarks>
    private const int SharedListBudget = MaxEchoedCandidates / 2;

    private AnalysisException(
        string message,
        AnalysisErrorKind kind,
        string? reference,
        IReadOnlyList<string> candidates,
        string? rootColumn = null)
        : base(DiagnosticText.Sanitize(message, MaxMessageLength))
    {
        Kind = kind;
        Reference = reference;
        Candidates = candidates;
        RootColumn = rootColumn;
    }

    /// <summary>The structured error class.</summary>
    public AnalysisErrorKind Kind { get; }

    /// <summary>The failing reference name (a table identifier or column name), when applicable.</summary>
    public string? Reference { get; }

    /// <summary>The TOP-LEVEL column this failing reference is rooted at, as the analyzer resolved/intended it
    /// — the bound base struct column for a nested access (<c>s.f</c>/<c>t.s.f</c> → <c>s</c>) or
    /// <see cref="Plans.Expressions.UnresolvedAttribute.NameParts"/>[0] for a plain column (a quoted-dot name
    /// <c>`a.b`</c> stays <c>a.b</c>, a qualified <c>t.x</c> → <c>t</c>). Set for name-resolution failures where
    /// a caller needs to attribute the failure to a single column (e.g. the Delta dependent-column reclassifier,
    /// #600/#618) WITHOUT re-parsing <see cref="Reference"/> — which cannot recover a quoted dot or a bound base
    /// from the flattened dotted string. <see langword="null"/> when not applicable.</summary>
    public string? RootColumn { get; }

    /// <summary>The candidate names that were in scope at the failure (empty when not applicable).</summary>
    public IReadOnlyList<string> Candidates { get; }

    /// <summary>Builds a <see cref="AnalysisErrorKind.TableOrViewNotFound"/> failure naming the
    /// unresolved identifier.</summary>
    public static AnalysisException TableOrViewNotFound(IReadOnlyList<string> identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        string name = string.Join('.', identifier);
        return new AnalysisException(
            $"Table or view not found: "
                + $"{DiagnosticText.SanitizeAndJoin(identifier, MaxEchoedCandidateLength, MaxEchoedCandidates, ".")}",
            AnalysisErrorKind.TableOrViewNotFound,
            name,
            Array.Empty<string>());
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.UnsupportedDataSource"/> failure for a
    /// file-format scan (for example <c>Read.Parquet(path)</c>) whose reader is delivered by EPIC-05
    /// (Delta/Parquet storage) and is unavailable in M1. The message names the format, the path, and
    /// EPIC-05 ownership, and points at the working alternative (in-memory <c>CreateDataFrame</c>) — the
    /// analysis-time analog of the physical planner's deterministic <c>UnsupportedPlanException</c>.</summary>
    /// <param name="format">The data-source format (for example <c>parquet</c>).</param>
    /// <param name="path">The scanned path.</param>
    public static AnalysisException UnsupportedDataSource(string format, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(format);
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Redact credential-bearing fragments (SAS ?sig=, presigned URLs, userinfo) so the diagnostic
        // (and any log that captures it) never leaks a secret embedded in the path.
        string safePath = SecretRedaction.RedactPath(path);
        return new AnalysisException(
            $"Reading a '{format}' data source is not supported in this milestone: the file-format "
            + $"reader for path '{safePath}' is delivered by EPIC-05 (Delta/Parquet storage). Until then, "
            + "create a DataFrame from in-memory data with SparkSession.CreateDataFrame(rows, schema).",
            AnalysisErrorKind.UnsupportedDataSource,
            safePath,
            Array.Empty<string>());
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.UnsupportedDataSink"/> failure for a write whose
    /// sink <b>format</b> is delivered by EPIC-05 (Delta/Parquet storage) and is unavailable in M1
    /// (STORY-04.6.3 AC4). The message names the format, the (redacted) path, and EPIC-05 ownership, and
    /// points at the working M1 local sink — the write-side analog of <see cref="UnsupportedDataSource"/>.
    /// It fires during analysis, before any output is committed.</summary>
    /// <param name="format">The sink format (for example <c>delta</c> or <c>parquet</c>).</param>
    /// <param name="path">The target path, or <see langword="null"/> when the sink is path-less.</param>
    /// <param name="localFormats">The M1-supported local sink formats, for the actionable alternative.</param>
    public static AnalysisException UnsupportedDataSink(
        string format, string? path, IReadOnlyList<string> localFormats)
    {
        ArgumentException.ThrowIfNullOrEmpty(format);
        ArgumentNullException.ThrowIfNull(localFormats);

        // Redact credential-bearing fragments so neither the diagnostic nor any log capturing it leaks a
        // secret embedded in the sink path (parity with the read-door's UnsupportedDataSource, #424/#432).
        string safePath = path is null ? "<none>" : SecretRedaction.RedactPath(path);
        string alternatives = DiagnosticText.SanitizeAndJoin(
            localFormats, MaxEchoedCandidateLength, MaxEchoedCandidates);
        return new AnalysisException(
            $"Writing a '{format}' data source is not supported in this milestone: the writer for target "
            + $"'{safePath}' is delivered by EPIC-05 (Delta transaction-log storage). Until then, write to a "
            + $"supported M1 local sink (format: [{alternatives}]).",
            AnalysisErrorKind.UnsupportedDataSink,
            safePath,
            Array.Empty<string>());
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.UnsupportedDataSink"/> failure for a write whose
    /// sink <b>format</b> has no M1 write mapping at all (STORY-04.6.3 AC3) — neither an engine-backed
    /// local sink nor an EPIC-05-deferred format. The message names the offending format and the
    /// recognized local/deferred formats, and fires during analysis before any output is committed.</summary>
    /// <param name="format">The unsupported sink format.</param>
    /// <param name="path">The target path, or <see langword="null"/> when the sink is path-less.</param>
    /// <param name="localFormats">The M1-supported local sink formats.</param>
    /// <param name="deferredFormats">The EPIC-05-deferred formats.</param>
    public static AnalysisException UnsupportedWriteFormat(
        string format,
        string? path,
        IReadOnlyList<string> localFormats,
        IReadOnlyList<string> deferredFormats)
    {
        ArgumentException.ThrowIfNullOrEmpty(format);
        ArgumentNullException.ThrowIfNull(localFormats);
        ArgumentNullException.ThrowIfNull(deferredFormats);

        string safePath = path is null ? "<none>" : SecretRedaction.RedactPath(path);
        return new AnalysisException(
            $"Unsupported write format '{format}' for target '{safePath}'. DeltaSharp M1 writes these "
            + $"local sink formats: "
            + $"[{DiagnosticText.SanitizeAndJoin(localFormats, MaxEchoedCandidateLength, SharedListBudget)}]; "
            + $"these formats are recognized but deferred to EPIC-05 (Delta/Parquet storage): "
            + $"[{DiagnosticText.SanitizeAndJoin(deferredFormats, MaxEchoedCandidateLength, SharedListBudget)}].",
            AnalysisErrorKind.UnsupportedDataSink,
            safePath,
            Array.Empty<string>());
    }

    public static AnalysisException UnresolvedColumn(
        string name, IReadOnlyList<AttributeReference> input, string? rootColumn = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        string[] candidates = input.Select(a => a.Name).ToArray();

        // Bound the LISTING, not the composed message: a wide schema must still be told how many candidates
        // it is not being shown. `candidates` (the structured channel) keeps every name, unmodified.
        string echoed = DiagnosticText.SanitizeAndJoin(
            candidates, MaxEchoedCandidateLength, MaxEchoedCandidates);
        return new AnalysisException(
            $"Cannot resolve column name "
                + $"'{DiagnosticText.Sanitize(name, MaxEchoedReferenceLength)}' "
                + $"given input columns: [{echoed}]",
            AnalysisErrorKind.UnresolvedColumn,
            name,
            candidates,
            rootColumn);
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.AmbiguousReference"/> failure naming the
    /// reference and the attributes it could bind to.</summary>
    public static AnalysisException AmbiguousReference(
        string name, IReadOnlyList<AttributeReference> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        string[] candidates = matches.Select(a => a.SimpleString).ToArray();

        // RenderCandidate, not the raw SimpleString: the per-item cap must not eat the #exprId discriminator.
        string echoed = DiagnosticText.SanitizeAndJoin(
            matches.Select(RenderCandidate), MaxEchoedCandidateLength, MaxEchoedCandidates);
        return new AnalysisException(
            $"Reference '{DiagnosticText.Sanitize(name, MaxEchoedReferenceLength)}' "
                + $"is ambiguous, could be: {echoed}.",
            AnalysisErrorKind.AmbiguousReference,
            name,
            candidates);
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.UnresolvedPlan"/> failure for an unresolved
    /// expression marker (attribute, star, or function) that survived analysis, naming the marker
    /// and the operator that still holds it.</summary>
    public static AnalysisException UnresolvedExpression(string reference, string nodeName)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(nodeName);
        return new AnalysisException(
            $"Plan is not fully resolved: unresolved reference "
            + $"'{DiagnosticText.Sanitize(reference, CoercionHelpers.DiagnosticReferenceMaxLength)}' remains in operator "
            + $"'{nodeName}' after analysis.",
            AnalysisErrorKind.UnresolvedPlan,
            reference,
            Array.Empty<string>());
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.UnresolvedPlan"/> failure for an operator
    /// that is still unresolved after analysis for a reason outside its expressions — for example a
    /// using/natural join the analyzer has not yet desugared.</summary>
    public static AnalysisException UnresolvedOperator(string nodeName)
    {
        ArgumentNullException.ThrowIfNull(nodeName);
        return new AnalysisException(
            $"Plan is not fully resolved: operator '{nodeName}' remains unresolved after analysis.",
            AnalysisErrorKind.UnresolvedPlan,
            nodeName,
            Array.Empty<string>());
    }

    /// <summary>Builds a <see cref="AnalysisErrorKind.UsingOrNaturalJoinNotImplemented"/> failure for
    /// a using-column or natural <c>Join</c> that reached analysis. Building such a join is supported
    /// today, but the analyzer rule that desugars its shared columns into an equi-condition is not
    /// yet implemented; the message points at the follow-up (#405) so the failure is actionable
    /// rather than the generic "operator remains unresolved".</summary>
    public static AnalysisException UsingOrNaturalJoinNotImplemented(bool isNatural)
    {
        string kind = isNatural ? "natural" : "using-column";
        return new AnalysisException(
            $"using/natural join resolution is not yet implemented: a {kind} join cannot be "
            + "analyzed until the desugar-to-equi-condition rule lands (see "
            + "https://github.com/khaines/deltasharp/issues/405).",
            AnalysisErrorKind.UsingOrNaturalJoinNotImplemented,
            "Join",
            Array.Empty<string>());
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.UnsupportedProjection"/> failure for a
    /// projection element that cannot yet be exposed as a named output attribute.</summary>
    public static AnalysisException UnsupportedProjection(string message, string? reference = null) =>
        new(
            message ?? throw new ArgumentNullException(nameof(message)),
            AnalysisErrorKind.UnsupportedProjection,
            reference,
            Array.Empty<string>());

    /// <summary>Builds a <see cref="AnalysisErrorKind.NumberOfColumnsMismatch"/> failure for a
    /// <c>Union</c> whose inputs have differing column counts, naming the first input's arity and the
    /// offending input's arity (Spark parity). This is a structural (arity) check only; column-type
    /// compatibility is deferred to STORY-04.5.2 / #171.</summary>
    /// <param name="nodeName">The set-operation node name (e.g. <c>Union</c>).</param>
    /// <param name="firstColumnCount">The column count of the first input.</param>
    /// <param name="inputIndex">The zero-based position of the offending input (reported to the user
    /// as the one-based ordinal <c>inputIndex + 1</c>).</param>
    /// <param name="inputColumnCount">The column count of the offending input.</param>
    public static AnalysisException NumberOfColumnsMismatch(
        string nodeName, int firstColumnCount, int inputIndex, int inputColumnCount)
    {
        ArgumentNullException.ThrowIfNull(nodeName);
        return new AnalysisException(
            $"{nodeName} can only be performed on inputs with the same number of columns, but the "
            + $"first input has {Columns(firstColumnCount)} and input {inputIndex + 1} has "
            + $"{Columns(inputColumnCount)}.",
            AnalysisErrorKind.NumberOfColumnsMismatch,
            nodeName,
            Array.Empty<string>());
    }

    private static string Columns(int count) => count == 1 ? "1 column" : $"{count} columns";

    /// <summary>Builds an <see cref="AnalysisErrorKind.UnresolvedFunction"/> failure naming the
    /// unknown function and the supplied argument types.</summary>
    public static AnalysisException UnknownFunction(string name, IReadOnlyList<DataType> argumentTypes)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(argumentTypes);
        return new AnalysisException(
            $"Undefined function: '{DiagnosticText.Sanitize(name, MaxEchoedReferenceLength)}'. "
            + $"The function is neither a registered scalar nor an "
            + $"aggregate function in the M1 registry (supplied argument types: "
            + $"[{RenderTypes(argumentTypes)}]).",
            AnalysisErrorKind.UnresolvedFunction,
            name,
            Array.Empty<string>());
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.InvalidFunctionArgument"/> failure naming the
    /// function, the supplied argument types, and the expected argument forms.</summary>
    public static AnalysisException InvalidFunctionArgument(
        string name, IReadOnlyList<DataType> argumentTypes, string expected)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(argumentTypes);
        ArgumentNullException.ThrowIfNull(expected);
        return new AnalysisException(
            $"Cannot resolve function "
                + $"'{DiagnosticText.Sanitize(name, MaxEchoedReferenceLength)}"
                + $"({RenderTypes(argumentTypes)})': {expected}",
            AnalysisErrorKind.InvalidFunctionArgument,
            name,
            Array.Empty<string>());
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.DataTypeMismatch"/> failure describing an
    /// operator/conditional/predicate whose operand types are invalid under ADR-0008.</summary>
    public static AnalysisException DataTypeMismatch(string reference, string detail)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(detail);
        return new AnalysisException(
            $"cannot resolve '{DiagnosticText.Sanitize(reference, CoercionHelpers.DiagnosticReferenceMaxLength)}' "
            + $"due to data type mismatch: {detail}",
            AnalysisErrorKind.DataTypeMismatch,
            reference,
            Array.Empty<string>());
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.UnresolvedStructField"/> failure: a nested field
    /// reference (<paramref name="reference"/>, e.g. <c>s.f</c>) could not be resolved because its base is
    /// not a struct or the struct has no such field — a <b>structural</b> absence, not a predicate operand
    /// type mismatch. <paramref name="reference"/> is the full nested path so a caller can normalise it to
    /// the top-level column (#600).</summary>
    public static AnalysisException UnresolvedStructField(
        string reference, string detail, string? rootColumn = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(detail);
        return new AnalysisException(
            $"cannot resolve '{DiagnosticText.Sanitize(reference, CoercionHelpers.DiagnosticReferenceMaxLength)}': {detail}",
            AnalysisErrorKind.UnresolvedStructField,
            reference,
            Array.Empty<string>(),
            rootColumn);
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.MisplacedAggregate"/> failure for an aggregate
    /// function used outside a valid aggregate context.</summary>
    public static AnalysisException MisplacedAggregate(string reference, string ownerNodeName)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(ownerNodeName);
        return new AnalysisException(
            $"Aggregate function '{reference}' is not allowed in operator '{ownerNodeName}': aggregate "
            + "functions are only permitted in the aggregate expressions of a grouped aggregation "
            + "(groupBy(...).agg(...)).",
            AnalysisErrorKind.MisplacedAggregate,
            reference,
            Array.Empty<string>());
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.MisplacedAggregate"/> failure for a nested
    /// aggregate — an aggregate function whose argument subtree contains another aggregate (e.g.
    /// <c>sum(sum(x))</c>). Reuses the misplaced-aggregate kind (nesting is a placement error) but
    /// names both the outer and the nested aggregate so the diagnostic is actionable (#166).</summary>
    public static AnalysisException NestedAggregate(string outerName, string nestedName)
    {
        ArgumentNullException.ThrowIfNull(outerName);
        ArgumentNullException.ThrowIfNull(nestedName);
        return new AnalysisException(
            $"Aggregate function '{outerName}' contains a nested aggregate '{nestedName}': aggregate "
            + "functions cannot be nested inside the arguments of another aggregate.",
            AnalysisErrorKind.MisplacedAggregate,
            nestedName,
            Array.Empty<string>());
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.UntypedResolvedExpression"/> failure for a
    /// resolved expression the coercion pass left without a concrete result type.</summary>
    public static AnalysisException UntypedResolvedExpression(string reference, string ownerNodeName)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(ownerNodeName);
        return new AnalysisException(
            $"Resolved expression '{reference}' in operator '{ownerNodeName}' has no result type after "
            + "type coercion (STORY-04.5.2 / #171); an untyped resolved expression must not reach "
            + "physical planning.",
            AnalysisErrorKind.UntypedResolvedExpression,
            reference,
            Array.Empty<string>());
    }

    /// <summary>
    /// Renders one <c>name#exprId</c> candidate for an ambiguity diagnostic, bounding the NAME and keeping
    /// the identifier.
    /// </summary>
    /// <remarks>
    /// <b>Why not just <c>Sanitize(SimpleString, cap)</c>.</b> Sanitize truncates from the TAIL, and the tail
    /// of a composite candidate is precisely <c>#exprId</c> — the only thing distinguishing two same-named
    /// candidates. Applying the cap to the composite therefore deleted the discriminator and rendered two
    /// byte-identical entries in the one message whose entire purpose is to tell them apart. A bound on a
    /// COMPOSITE token has to be applied to the component that is not load-bearing for the reader.
    /// </remarks>
    private static string RenderCandidate(AttributeReference attribute)
    {
        string id = string.Create(CultureInfo.InvariantCulture, $"#{attribute.ExprId}");

        // -1 leaves room for Sanitize's own elision glyph, so the composite still fits the per-item cap and
        // SanitizeAndJoin never gets the chance to re-truncate (and re-delete the id).
        int nameBudget = Math.Max(MinEchoedNameLength, MaxEchoedCandidateLength - id.Length - 1);
        return DiagnosticText.Sanitize(attribute.Name, nameBudget) + id;
    }

    /// <summary>Renders a list of user-supplied types for a diagnostic. Each element goes through
    /// <see cref="CoercionHelpers.DiagnosticType"/> <b>first</b> — a struct type is itself a collection, so
    /// capping its flat <c>SimpleString</c> would cut it with a bare ellipsis and no count — and only then
    /// through the shared list bound, which by construction can no longer bind.</summary>
    private static string RenderTypes(IReadOnlyList<DataType> types) =>
        DiagnosticText.SanitizeAndJoin(
            types.Select(t => CoercionHelpers.DiagnosticType(t, MaxEchoedCandidateLength)),
            MaxEchoedCandidateLength,
            MaxEchoedCandidates);

    /// <summary>Builds an <see cref="AnalysisErrorKind.InvalidTimeTravelSpec"/> failure for a read that
    /// pins both a version and a timestamp (#499): the <c>versionAsOf</c> and <c>timestampAsOf</c> options
    /// together, or (defensively) a path suffix that resolves to both. An explicit option makes the load
    /// path literal, so an option and a path suffix never conflict. The (redacted) path is named; the value
    /// is never rendered.</summary>
    /// <param name="path">The load path (redacted in the message).</param>
    /// <param name="detail">A short description of the conflict.</param>
    public static AnalysisException ConflictingTimeTravel(string path, string detail)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(detail);
        string safePath = SecretRedaction.RedactPath(path);
        return new AnalysisException(
            $"Cannot time travel Delta table '{safePath}' using both a version and a timestamp: {detail}. "
            + "Pin at most one of versionAsOf / timestampAsOf — as an option (which takes precedence and "
            + "makes the path literal) or a '@v<n>' / '@yyyyMMddHHmmssSSS' path suffix — never both a "
            + "version and a timestamp.",
            AnalysisErrorKind.InvalidTimeTravelSpec,
            safePath,
            Array.Empty<string>());
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.InvalidTimeTravelSpec"/> failure for an unparseable
    /// <c>versionAsOf</c>/<c>timestampAsOf</c> value (#499).</summary>
    /// <param name="dimension">The time-travel dimension (versionAsOf / timestampAsOf).</param>
    /// <param name="value">The offending value (rendered — a time-travel value is not credential-bearing).</param>
    /// <param name="reason">A short parse-failure reason.</param>
    public static AnalysisException InvalidTimeTravelValue(string dimension, string value, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(dimension);
        ArgumentNullException.ThrowIfNull(value);
        return new AnalysisException(
            $"Invalid {dimension} value '{value}': {reason}.",
            AnalysisErrorKind.InvalidTimeTravelSpec,
            dimension,
            Array.Empty<string>());
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.FileSourceResolutionFailed"/> failure for a Delta
    /// (path-based) read whose resolution failed (#499): not a Delta table, an out-of-range/vacuumed
    /// version, a timestamp out of range, a malformed log, or no registered storage backend. The
    /// (redacted) path is named; the storage-side reason is appended.</summary>
    /// <param name="format">The data-source format (for example <c>delta</c>).</param>
    /// <param name="path">The load path (redacted in the message).</param>
    /// <param name="reason">The storage-side failure reason.</param>
    public static AnalysisException FileSourceResolutionFailed(string format, string path, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(format);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        string safePath = SecretRedaction.RedactPath(path);
        return new AnalysisException(
            $"Cannot read '{format}' source at '{safePath}': {reason}",
            AnalysisErrorKind.FileSourceResolutionFailed,
            safePath,
            Array.Empty<string>());
    }
}
