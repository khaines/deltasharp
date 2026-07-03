using DeltaSharp.Plans.Expressions;
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

    /// <summary>An aggregate function appears outside a valid aggregate context (e.g. in a
    /// <c>Select</c>/<c>Filter</c> with no <c>groupBy</c>/<c>agg</c>).</summary>
    MisplacedAggregate,

    /// <summary>A resolved expression reached the post-condition without a concrete result type — the
    /// coercion pass left it null-typed (a guard against leaking an untyped node downstream).</summary>
    UntypedResolvedExpression,
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

    /// <summary>Builds an <see cref="AnalysisErrorKind.UnresolvedColumn"/> failure naming the
    /// missing column and the candidate input columns.</summary>
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
}
