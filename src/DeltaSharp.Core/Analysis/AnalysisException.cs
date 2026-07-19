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
    /// field was dropped/renamed, or the base column was retyped away from a struct), distinct from a general
    /// <see cref="DataTypeMismatch"/> in a predicate's operands. Carries the full nested reference (e.g.
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
    private AnalysisException(
        string message,
        AnalysisErrorKind kind,
        string? reference,
        IReadOnlyList<string> candidates)
        : base(message)
    {
        Kind = kind;
        Reference = reference;
        Candidates = candidates;
    }

    /// <summary>The structured error class.</summary>
    public AnalysisErrorKind Kind { get; }

    /// <summary>The failing reference name (a table identifier or column name), when applicable.</summary>
    public string? Reference { get; }

    /// <summary>The candidate names that were in scope at the failure (empty when not applicable).</summary>
    public IReadOnlyList<string> Candidates { get; }

    /// <summary>Builds a <see cref="AnalysisErrorKind.TableOrViewNotFound"/> failure naming the
    /// unresolved identifier.</summary>
    public static AnalysisException TableOrViewNotFound(IReadOnlyList<string> identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        string name = string.Join('.', identifier);
        return new AnalysisException(
            $"Table or view not found: {name}",
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
        string alternatives = string.Join(", ", localFormats);
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
            + $"local sink formats: [{string.Join(", ", localFormats)}]; these formats are recognized but "
            + $"deferred to EPIC-05 (Delta/Parquet storage): [{string.Join(", ", deferredFormats)}].",
            AnalysisErrorKind.UnsupportedDataSink,
            safePath,
            Array.Empty<string>());
    }

    public static AnalysisException UnresolvedColumn(
        string name, IReadOnlyList<AttributeReference> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string[] candidates = input.Select(a => a.Name).ToArray();
        return new AnalysisException(
            $"Cannot resolve column name '{name}' given input columns: [{string.Join(", ", candidates)}]",
            AnalysisErrorKind.UnresolvedColumn,
            name,
            candidates);
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.AmbiguousReference"/> failure naming the
    /// reference and the attributes it could bind to.</summary>
    public static AnalysisException AmbiguousReference(
        string name, IReadOnlyList<AttributeReference> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        string[] candidates = matches.Select(a => a.SimpleString).ToArray();
        return new AnalysisException(
            $"Reference '{name}' is ambiguous, could be: {string.Join(", ", candidates)}.",
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
            $"Plan is not fully resolved: unresolved reference '{reference}' remains in operator "
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
            $"Undefined function: '{name}'. The function is neither a registered scalar nor an "
            + $"aggregate function in the M1 registry (supplied argument types: [{RenderTypes(argumentTypes)}]).",
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
            $"Cannot resolve function '{name}({RenderTypes(argumentTypes)})': {expected}",
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
            $"cannot resolve '{reference}' due to data type mismatch: {detail}",
            AnalysisErrorKind.DataTypeMismatch,
            reference,
            Array.Empty<string>());
    }

    /// <summary>Builds an <see cref="AnalysisErrorKind.UnresolvedStructField"/> failure: a nested field
    /// reference (<paramref name="reference"/>, e.g. <c>s.f</c>) could not be resolved because its base is
    /// not a struct or the struct has no such field — a <b>structural</b> absence, not a predicate operand
    /// type mismatch. <paramref name="reference"/> is the full nested path so a caller can normalise it to
    /// the top-level column (#600).</summary>
    public static AnalysisException UnresolvedStructField(string reference, string detail)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(detail);
        return new AnalysisException(
            $"cannot resolve '{reference}': {detail}",
            AnalysisErrorKind.UnresolvedStructField,
            reference,
            Array.Empty<string>());
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

    private static string RenderTypes(IReadOnlyList<DataType> types) =>
        string.Join(", ", types.Select(t => t.SimpleString));

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
