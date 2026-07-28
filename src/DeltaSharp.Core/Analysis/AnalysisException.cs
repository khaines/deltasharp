using System;
using System.Globalization;
using System.Linq;
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
    /// bound its own items and report an explicit overflow count, so this cap never elides a list.
    /// </para>
    /// <para>
    /// <b>That is a claim about every list-composing factory, so it is asserted rather than listed.</b>
    /// <c>EveryListComposingFactory_ReportsAnOverflowCount</c> and
    /// <c>NoFreeProseToken_CanCrowdOutAListingsOverflowCount</c> both discover the factories by reflection.
    /// A hand-written enumeration stood here for one revision and was already wrong: bounding each list is
    /// necessary but <em>not sufficient</em>, because a listing's budget is <c>MaxMessageLength − prose</c>
    /// and is therefore only as honest as the prose is bounded. <see cref="UnsupportedDataSink"/> and
    /// <see cref="UnsupportedWriteFormat"/> interpolated two unbounded user tokens — a target path and a
    /// requested format — and an 816-character path pushed both messages past this cap, so the backstop cut
    /// from the tail and took the listing and its count with it. <see cref="BoundTokens"/> closes that, and
    /// the reflective test is what keeps the claim true for a factory added tomorrow. Note which of the two
    /// tokens the review repro used: <c>path</c>. <c>format</c> was equally unbounded and equally capable of
    /// it, so a fix or a test naming <c>path</c> would have closed half the defect.
    /// </para>
    /// <para>
    /// A single oversized TOKEN can still reach this cap in a factory that composes <b>no</b> listing, and
    /// there that is the cap doing its job. The distinction is not "token versus list" — it is whether
    /// anything with a count is downstream of the cut. An earlier revision of this paragraph drew the line
    /// the first way and was falsified by exactly that case: an oversized token in a list-composing factory
    /// produced the thing the paragraph called impossible.
    /// </para>
    /// <para>
    /// <b>The factories are not the whole class.</b> A second family composes its text in the analyzer
    /// (<c>Analyzer.ExtractStructField</c>, <c>ExpressionCoercion</c>) and hands it to
    /// <see cref="DataTypeMismatch(string, string)"/> / <see cref="UnresolvedStructField(string, string, string?)"/> as an opaque <c>detail</c>
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
    /// The <b>ceiling</b> on a single listed item. A per-item budget is derived from how many items are
    /// actually being shown (<c>DiagnosticText.SanitizeToBudget</c>); this caps that derivation so one name
    /// cannot consume the whole listing's allowance.
    /// </summary>
    internal const int MaxEchoedCandidateLength = 224;

    /// <summary>
    /// The <b>floor</b> on a single listed item, so a very wide listing still shows enough of each name to
    /// be recognizable. 48 clears the longest real-world column names observed in review
    /// (<c>customer_lifetime_value_rolling_90d</c> is 35).
    /// </summary>
    internal const int MinEchoedCandidateLength = 48;

    /// <summary>
    /// Composes a message whose listing is bounded by <b>the space the message actually has left</b>, rather
    /// than by a chosen constant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="compose"/> is called twice: once with an empty listing to measure the fixed prose, and
    /// once with the listing rendered into whatever remains under <see cref="MaxMessageLength"/>. The result
    /// therefore fits by construction, and the listing is elided <em>only</em> when it genuinely will not fit.
    /// </para>
    /// <para>
    /// <b>Why not a constant.</b> Four review seats each objected to a different fixed number here — a per-item
    /// cap of 32, an item cap of 20, an item cap of 14 — and every objection had the same shape: a number
    /// chosen without reference to the space available. Both kinds discarded information while the budget went
    /// unused, which is <em>worse than the unbounded original</em> at every width that used to fit. Measured
    /// against the corpus in <c>AnalysisExceptionCandidateListingTests</c> (35-character real-world column
    /// names), the previous revision elided a <b>four</b>-column listing at 197 characters, and pinned every
    /// width from 14 upward at 771 — leaving a quarter of the budget permanently unspent while dropping names.
    /// Deriving the budget removes the constant, and with it the argument about the constant. These figures
    /// are not maintained by hand either: <c>ListingBudget_IsSpentBeforeAnythingIsElided</c> asserts the
    /// property they illustrate, over the whole <b>product</b> of listing width (1–400) and item length
    /// (1–90) — 36,000 cells. It swept width alone at a single 35-character name until round 11, when a
    /// defect living on the interaction of the two axes went undetected by exactly that shape of corpus: a
    /// line through a plane cannot find it.
    /// </para>
    /// <para>
    /// The floor and ceiling on a single item survive, because they are not tuning: the ceiling stops one
    /// pathological name consuming the listing, and the floor keeps every name recognizable. Both are
    /// exercised by <c>ListingBudget_IsSpentBeforeAnythingIsElided</c>, which sweeps the boundary band
    /// continuously rather than sampling either side of it, and by the product sweep above — a ceiling is
    /// only observable at item lengths that exceed it, so a corpus of uniformly short names cannot see one.
    /// </para>
    /// </remarks>
    /// <param name="compose">Builds the full message from a rendered listing.</param>
    /// <param name="items">The untrusted items to list.</param>
    /// <param name="render">Renders one item within a supplied allowance.</param>
    /// <param name="reserved">Characters an outer wrapper will add that <paramref name="compose"/> cannot see.</param>
    private static string ComposeWithListing<T>(
        Func<string, string> compose, IReadOnlyList<T> items, Func<T, int, string> render, int reserved = 0) =>
        RenderListing(items, render, RemainingBudget(compose(string.Empty).Length + reserved));

    /// <summary>
    /// Variant for a caller that composes a <c>detail</c> string rather than a whole message
    /// (<see cref="ExpressionCoercion"/>). The wrapping factory's own prose is not visible there, so its
    /// worst case — a <see cref="DataTypeMismatch(string, string)"/> reference at its full budget plus fixed prose — is
    /// reserved up front.
    /// </summary>
    /// <summary>
    /// Composes a <c>detail</c> string whose TYPE slots are bounded by the space the finished message will
    /// actually have left, rather than by a constant.
    /// </summary>
    /// <remarks>
    /// The listing sibling of this helper measures the prose by composing it once with an empty listing; this
    /// does the same with empty type slots, so the free tokens a detail interpolates — a field name, a
    /// rendered reference, an operator context — are <b>measured</b> rather than estimated. Only the wrapping
    /// factory's own prose has to be reserved, because that is the one part not visible from here.
    /// </remarks>
    /// <param name="compose">Builds the detail from one rendered string per type slot.</param>
    /// <param name="types">The types to render, in the order <paramref name="compose"/> interpolates them.</param>
    /// <param name="wrap">Wraps a finished detail in the calling factory's own prose.</param>
    private static string ComposeDetailWithTypes(
        Func<string[], string> compose, DataType[] types, Func<string, string> wrap)
    {
        ArgumentNullException.ThrowIfNull(compose);
        ArgumentNullException.ThrowIfNull(types);

        // Two passes, and BOTH layers are measured: the detail with empty type slots, then the factory's own
        // prose around it. Nothing here is estimated, so the types get every character the message can spare.
        string[] empty = [.. types.Select(_ => string.Empty)];
        return compose(CoercionHelpers.BoundTypes(MaxMessageLength - wrap(compose(empty)).Length, types));
    }

    internal static string ComposeDetailWithListing<T>(
        Func<string, string> compose, IReadOnlyList<T> items, Func<T, int, string> render) =>
        ComposeWithListing(
            compose, items, render, reserved: CoercionHelpers.DiagnosticReferenceMaxLength + 64);

    /// <summary>The characters left for listings once <paramref name="proseLength"/> is spent, shared evenly
    /// when a message composes more than one listing.</summary>
    /// <remarks>
    /// The floor is <b>zero</b>, deliberately. An earlier revision floored this at
    /// <see cref="MinEchoedCandidateLength"/>, on the reasoning that a listing should always get enough room
    /// to show something. That is a claim about the world, and the world falsified it: prose containing an
    /// unbounded token could consume the entire message, the listing was granted 48 characters anyway, and
    /// the composed result ran past <see cref="MaxMessageLength"/> — so the whole-message backstop cut the
    /// listing <em>and its overflow count</em> off the tail. A floor asserts "there is always room for a
    /// little"; returning the truth lets <c>SanitizeToBudget</c> collapse the listing to its count, which is
    /// the one thing worth keeping when there is no room. <see cref="BoundTokens"/> is what makes that
    /// remainder large enough to be useful.
    /// </remarks>
    private static int RemainingBudget(int proseLength, int lists = 1) =>
        Math.Max(0, (MaxMessageLength - proseLength) / lists);

    /// <summary>
    /// The space free prose TOKENS may occupy: everything the fixed literal text does not need, less the room
    /// each listing needs to state its own overflow count.
    /// </summary>
    /// <param name="literalProseLength">Length of the message with every token and listing empty.</param>
    /// <param name="itemCounts">The cardinality of each listing the message composes.</param>
    private static int TokenBudget(int literalProseLength, params int[] itemCounts) =>
        MaxMessageLength - literalProseLength - itemCounts.Sum(DiagnosticText.OverflowMarkerLength);

    /// <summary>
    /// Bounds the free prose tokens of a message — a path, a format name — so that no single one of them can
    /// crowd out the listing that follows it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> The listing budget is derived from the composed prose, so it is only as honest
    /// as the prose is bounded. <see cref="UnsupportedDataSink"/> and <see cref="UnsupportedWriteFormat"/>
    /// interpolate two user-supplied tokens — the redacted target path and the requested format — neither of
    /// which had any bound at all. An 816-character path drove both messages to the backstop, taking the
    /// listing and its <c>(+N more)</c> count with it, which is exactly the failure the listing budget was
    /// introduced to prevent, arriving through the prose instead of the list.
    /// </para>
    /// <para>
    /// <b>Allocation is max-min fair, shortest first, so the common case is bounded by nothing at all.</b> A
    /// token shorter than its even share consumes only what it needs and hands the remainder to the others;
    /// only when the tokens genuinely cannot all fit does any of them get cut. An ordinary
    /// <c>'delta'</c>-plus-a-real-path message is therefore byte-identical to what it was before this bound
    /// existed — the property <c>FreeProseTokens_AreBoundedOnlyWhenTheyDoNotFit</c> asserts, because
    /// "the common case must not pay for the pathological one" has been claimed here before by a comment
    /// whose corpus could not exercise it.
    /// </para>
    /// </remarks>
    /// <param name="available">The token budget from <see cref="TokenBudget"/>.</param>
    /// <param name="tokens">The free prose tokens, in the order the message interpolates them.</param>
    private static string[] BoundTokens(int available, params string[] tokens)
    {
        var bounded = new string[tokens.Length];
        int remaining = Math.Max(0, available);
        int[] shortestFirst = [.. Enumerable.Range(0, tokens.Length).OrderBy(i => tokens[i].Length)];

        for (int n = 0; n < shortestFirst.Length; n++)
        {
            int i = shortestFirst[n];
            int share = remaining / (shortestFirst.Length - n);

            // Sanitize returns maxLength + 1 characters when it truncates, because it appends the elision
            // mark. Asking for one less when a cut is certain makes the allocation exact rather than
            // one-over — and one-over is the whole defect, since the backstop then eats the overflow count.
            bounded[i] = DiagnosticText.Sanitize(
                tokens[i], tokens[i].Length <= share ? share : Math.Max(0, share - 1));
            remaining = Math.Max(0, remaining - bounded[i].Length);
        }

        return bounded;
    }

    private static string RenderListing<T>(
        IReadOnlyList<T> items, Func<T, int, string> render, int budget, string separator = ", ") =>
        DiagnosticText.SanitizeToBudget(
            items, render, budget, MinEchoedCandidateLength, MaxEchoedCandidateLength, separator);

    /// <summary>The cap applied to the unresolved/ambiguous name echoed in the MESSAGE. The structured
    /// <see cref="Reference"/> property keeps the full value; this bounds only the prose, so that a long name
    /// cannot push a listing past <see cref="MaxMessageLength"/> and destroy the overflow count with it.
    /// </summary>
    internal const int MaxEchoedReferenceLength = 64;

    /// <summary>The floor on the NAME half of a composite <c>name#exprId</c> candidate, so that clamping can
    /// never invert and leave the identifier as the only surviving part.</summary>
    private const int MinEchoedNameLength = 8;

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
        string Compose(string listing) => $"Table or view not found: {listing}";
        return new AnalysisException(
            Compose(RenderListing(
                identifier, DiagnosticText.SanitizeTo, RemainingBudget(Compose(string.Empty).Length), ".")),
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
        string Render(string fmt, string target, string listing) =>
            $"Writing a '{fmt}' data source is not supported in this milestone: the writer for target "
            + $"'{target}' is delivered by EPIC-05 (Delta transaction-log storage). Until then, write to a "
            + $"supported M1 local sink (format: [{listing}]).";

        string[] tokens = BoundTokens(
            TokenBudget(Render(string.Empty, string.Empty, string.Empty).Length, localFormats.Count),
            format,
            safePath);
        string Compose(string listing) => Render(tokens[0], tokens[1], listing);
        return new AnalysisException(
            Compose(ComposeWithListing(Compose, localFormats, DiagnosticText.SanitizeTo)),
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
        string Render(string fmt, string target, string local, string deferred) =>
            $"Unsupported write format '{fmt}' for target '{target}'. DeltaSharp M1 writes these "
            + $"local sink formats: [{local}]; these formats are recognized but deferred to EPIC-05 "
            + $"(Delta/Parquet storage): [{deferred}].";

        string[] tokens = BoundTokens(
            TokenBudget(
                Render(string.Empty, string.Empty, string.Empty, string.Empty).Length,
                localFormats.Count,
                deferredFormats.Count),
            format,
            safePath);
        string Compose(string local, string deferred) => Render(tokens[0], tokens[1], local, deferred);

        // Two listings in one message, so they share what is left rather than each taking it.
        int shared = RemainingBudget(Compose(string.Empty, string.Empty).Length, lists: 2);
        return new AnalysisException(
            Compose(
                RenderListing(localFormats, DiagnosticText.SanitizeTo, shared),
                RenderListing(deferredFormats, DiagnosticText.SanitizeTo, shared)),
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
        string safeName = DiagnosticText.Sanitize(name, MaxEchoedReferenceLength);
        string Compose(string listing) =>
            $"Cannot resolve column name '{safeName}' given input columns: [{listing}]";
        return new AnalysisException(
            Compose(ComposeWithListing(Compose, candidates, DiagnosticText.SanitizeTo)),
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

        // RenderCandidate, not Sanitize: the per-item allowance must eat NAME characters, never the
        // #exprId that is the only thing distinguishing two same-named candidates.
        string safeName = DiagnosticText.Sanitize(name, MaxEchoedReferenceLength);
        string Compose(string listing) =>
            $"Reference '{safeName}' is ambiguous, could be: {listing}.";
        return new AnalysisException(
            Compose(ComposeWithListing(Compose, matches, RenderCandidate)),
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
        string safeName = DiagnosticText.Sanitize(name, MaxEchoedReferenceLength);
        string Compose(string listing) =>
            $"Undefined function: '{safeName}'. The function is neither a registered scalar nor an "
            + $"aggregate function in the M1 registry (supplied argument types: [{listing}]).";
        return new AnalysisException(
            Compose(ComposeWithListing(Compose, argumentTypes, CoercionHelpers.DiagnosticType)),
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
        // Both free prose tokens go through the allocator, not just the name. `expected` was echoed raw: it
        // is supplied by a function's own argument contract today, but it is a plain string parameter on a
        // public factory, and the round-9 guard oversized each parameter ONE AT A TIME, so a caller passing
        // two long tokens at once slipped through. 1024-char name + 1024-char expected rendered 1025 chars,
        // i.e. straight into the whole-message backstop, which is the one cut that takes a listing's
        // (+N more) count with it.
        string Render(string fn, string want, string listing) =>
            $"Cannot resolve function '{fn}({listing})': {want}";

        string[] tokens = BoundTokens(
            TokenBudget(Render(string.Empty, string.Empty, string.Empty).Length, argumentTypes.Count),
            name,
            expected);
        string Compose(string listing) => Render(tokens[0], tokens[1], listing);
        return new AnalysisException(
            Compose(ComposeWithListing(Compose, argumentTypes, CoercionHelpers.DiagnosticType)),
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
            DataTypeMismatchMessage(reference, detail),
            AnalysisErrorKind.DataTypeMismatch,
            reference,
            Array.Empty<string>());
    }

    private static string DataTypeMismatchMessage(string reference, string detail) =>
        $"cannot resolve '{DiagnosticText.Sanitize(reference, CoercionHelpers.DiagnosticReferenceMaxLength)}' "
        + $"due to data type mismatch: {detail}";

    /// <summary>
    /// <see cref="DataTypeMismatch(string, string)"/> for a detail containing TYPE slots, sized against this
    /// factory's own composed prose rather than against a reserve.
    /// </summary>
    /// <remarks>
    /// The detail cannot see the wrapping prose, so an earlier revision had the detail-side helper reserve a
    /// worst case for it — the full 256-character reference cap plus slack. That is an estimate, and it was
    /// wrong in the ordinary direction: for a short reference like <c>payload.typo</c> it over-reserved by
    /// roughly 300 characters, and <c>TypeBudget_IsSpentBeforeAnyFieldIsElided</c> caught the render eliding
    /// at 723 of 1024. Composing here instead <b>measures</b> the prose, which is the same correction this
    /// PR already applied to listings; a reserve is a constant wearing a different hat.
    /// </remarks>
    internal static AnalysisException DataTypeMismatch(
        string reference, Func<string[], string> detail, params DataType[] types) =>
        DataTypeMismatch(reference, ComposeDetailWithTypes(detail, types, d => DataTypeMismatchMessage(reference, d)));

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
            UnresolvedStructFieldMessage(reference, detail),
            AnalysisErrorKind.UnresolvedStructField,
            reference,
            Array.Empty<string>(),
            rootColumn);
    }

    private static string UnresolvedStructFieldMessage(string reference, string detail) =>
        $"cannot resolve '{DiagnosticText.Sanitize(reference, CoercionHelpers.DiagnosticReferenceMaxLength)}': {detail}";

    /// <summary>
    /// <see cref="UnresolvedStructField(string, string, string?)"/> for a detail containing TYPE slots, sized
    /// against this factory's own composed prose. See <see cref="DataTypeMismatch(string, Func{string[], string}, DataType[])"/>
    /// for why this is measured here rather than reserved by the caller.
    /// </summary>
    internal static AnalysisException UnresolvedStructField(
        string reference,
        Func<string[], string> detail,
        DataType[] types,
        string? rootColumn = null) =>
        UnresolvedStructField(
            reference,
            ComposeDetailWithTypes(detail, types, d => UnresolvedStructFieldMessage(reference, d)),
            rootColumn);

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
    private static string RenderCandidate(AttributeReference attribute, int budget)
    {
        string id = string.Create(CultureInfo.InvariantCulture, $"#{attribute.ExprId}");

        // -1 leaves room for Sanitize's own elision glyph, so the composite still fits the per-item cap and
        // SanitizeAndJoin never gets the chance to re-truncate (and re-delete the id).
        int nameBudget = Math.Max(MinEchoedNameLength, budget - id.Length - 1);
        return DiagnosticText.Sanitize(attribute.Name, nameBudget) + id;
    }

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
