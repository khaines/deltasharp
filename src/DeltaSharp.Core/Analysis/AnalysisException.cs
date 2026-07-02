using DeltaSharp.Plans.Expressions;

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
}
