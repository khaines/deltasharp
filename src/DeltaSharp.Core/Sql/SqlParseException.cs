using System.Collections.Generic;

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
    /// <see cref="SqlParseErrorKind.UnsupportedFeature"/> failure. This is a short, <b>stable</b>
    /// identifier token (for example <c>JOIN</c>, <c>GROUP_BY</c>, <c>FUNCTION_CALL</c>,
    /// <c>SELECT_DISTINCT</c>) — the programmatic branch key callers switch on. The human-readable
    /// prose lives only in <see cref="Exception.Message"/>. It is <see langword="null"/> for a
    /// <see cref="SqlParseErrorKind.SyntaxError"/>.
    /// </summary>
    public string? Construct { get; }

    /// <summary>Builds a deterministic <see cref="SqlParseErrorKind.SyntaxError"/> tagged with the
    /// 1-based source <paramref name="position"/>.</summary>
    /// <param name="detail">A precise description of what was expected/found.</param>
    /// <param name="position">The 1-based position of the offending token in the source SQL.</param>
    internal static SqlParseException Syntax(string detail, int position) =>
        new($"Syntax error at position {position}: {detail}", SqlParseErrorKind.SyntaxError, null);

    /// <summary>Builds a deterministic <see cref="SqlParseErrorKind.UnsupportedFeature"/> whose
    /// <see cref="Construct"/> is the stable <paramref name="construct"/> token and whose message
    /// carries the human-readable prose (plus a DataFrame-API onboarding hint when one exists).</summary>
    /// <param name="construct">The unsupported construct's stable token (for example <c>JOIN</c>).</param>
    /// <param name="position">The 1-based position of the construct in the source SQL.</param>
    internal static SqlParseException Unsupported(string construct, int position)
    {
        (string description, string? hint) = ConstructInfo.TryGetValue(construct, out (string, string?) info)
            ? info
            : (construct, null);

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
