namespace DeltaSharp;

/// <summary>
/// The structured class of a <see cref="SqlParseException"/>, letting callers (and tests) branch on
/// <i>why</i> <see cref="SparkSession.Sql(string)"/> rejected a statement without parsing the message
/// text. Mirrors the analyzer's structured-kind convention (Spark parity: <c>ParseException</c>
/// carries an error class).
/// </summary>
public enum SqlParseErrorKind
{
    /// <summary>
    /// The text is not well-formed against the M1 grammar — an unexpected token, a missing
    /// <c>FROM</c>, an unterminated string, and so on.
    /// </summary>
    SyntaxError,

    /// <summary>
    /// The text is recognizable SQL but names a construct the M1 SQL door does not implement yet
    /// (a join, aggregate/<c>GROUP BY</c>, subquery, function call, DDL/DML, set operation, …). The
    /// offending construct is named in <see cref="SqlParseException.Construct"/> and the message.
    /// </summary>
    UnsupportedFeature,
}
