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
    /// <param name="message">The deterministic error message.</param>
    public SqlParseException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    /// <param name="message">The deterministic error message.</param>
    /// <param name="innerException">The exception that caused this error.</param>
    public SqlParseException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    private SqlParseException(string message, SqlParseErrorKind kind, string? construct)
        : base(message)
    {
        ErrorKind = kind;
        Construct = construct;
    }

    /// <summary>The structured reason the statement was rejected.</summary>
    public SqlParseErrorKind ErrorKind { get; }

    /// <summary>
    /// The named construct that is not supported, for an
    /// <see cref="SqlParseErrorKind.UnsupportedFeature"/> failure (for example <c>JOIN</c>,
    /// <c>GROUP BY</c>, <c>function call</c>); <see langword="null"/> for a
    /// <see cref="SqlParseErrorKind.SyntaxError"/>.
    /// </summary>
    public string? Construct { get; }

    /// <summary>Builds a deterministic <see cref="SqlParseErrorKind.SyntaxError"/> tagged with the
    /// 1-based source <paramref name="position"/>.</summary>
    /// <param name="detail">A precise description of what was expected/found.</param>
    /// <param name="position">The 1-based position of the offending token in the source SQL.</param>
    internal static SqlParseException Syntax(string detail, int position) =>
        new($"Syntax error at position {position}: {detail}", SqlParseErrorKind.SyntaxError, null);

    /// <summary>Builds a deterministic <see cref="SqlParseErrorKind.UnsupportedFeature"/> naming the
    /// <paramref name="construct"/> and its 1-based source <paramref name="position"/>.</summary>
    /// <param name="construct">The unsupported construct's name (for example <c>JOIN</c>).</param>
    /// <param name="position">The 1-based position of the construct in the source SQL.</param>
    internal static SqlParseException Unsupported(string construct, int position) =>
        new(
            $"Unsupported SQL feature at position {position}: {construct} is not supported by the M1 "
            + "SQL door (STORY-04.1.3 / #159). The supported subset is 'SELECT <cols|*> FROM <relation> "
            + "[WHERE <predicate>]'; the full SQL frontend arrives in EPIC-07 (ADR-0007).",
            SqlParseErrorKind.UnsupportedFeature,
            construct);
}
