using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;

namespace DeltaSharp;

/// <summary>
/// The entry points for building <see cref="Column"/> expressions, equivalent to Apache Spark's
/// <c>org.apache.spark.sql.functions</c>. DeltaSharp mirrors Spark's semantics while using the
/// .NET-idiomatic PascalCase names <see cref="Col(string)"/>, <see cref="Column(string)"/>, and
/// <see cref="Lit(object?)"/> (Spark's lowercase <c>col</c>/<c>lit</c> are not valid .NET member
/// names, and the rest of the public surface — <see cref="SparkSession"/>, <see cref="DataFrame"/> —
/// is already PascalCase).
/// </summary>
/// <remarks>
/// Every method here is <b>lazy</b>: it records intent by wrapping an immutable node of the internal
/// logical expression IR and performs no schema lookup and no evaluation (ADR-0001). A column
/// reference stays <b>unresolved</b> until the analyzer (FEAT-04.5) binds it.
/// </remarks>
public static class Functions
{
    private static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    /// <summary>
    /// Returns a <see cref="DeltaSharp.Column"/> that references the column named
    /// <paramref name="columnName"/>, mirroring Spark's <c>functions.col(colName)</c>. The reference
    /// is <b>unresolved</b> — no schema is consulted — until the analyzer binds it. The wildcard
    /// <c>"*"</c> and a qualified <c>"t.*"</c> produce a star that expands to all (qualified) columns
    /// at analysis, matching Spark.
    /// </summary>
    /// <param name="columnName">The column name, or <c>"*"</c>/<c>"t.*"</c> for a star.</param>
    /// <returns>An unresolved column reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is null or empty.</exception>
    public static Column Col(string columnName)
    {
        ArgumentException.ThrowIfNullOrEmpty(columnName);

        if (columnName == "*")
        {
            return new Column(new UnresolvedStar());
        }

        if (columnName.EndsWith(".*", StringComparison.Ordinal))
        {
            string[] target = columnName[..^2].Split('.');
            return new Column(new UnresolvedStar(target));
        }

        return new Column(new UnresolvedAttribute(columnName));
    }

    /// <summary>
    /// An alias for <see cref="Col(string)"/>, mirroring Spark's <c>functions.column(colName)</c>.
    /// </summary>
    /// <param name="columnName">The column name, or <c>"*"</c>/<c>"t.*"</c> for a star.</param>
    /// <returns>An unresolved column reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is null or empty.</exception>
    public static Column Column(string columnName) => Col(columnName);

    /// <summary>
    /// Returns a literal <see cref="DeltaSharp.Column"/> for <paramref name="value"/>, mirroring
    /// Spark's <c>functions.lit(value)</c>. The .NET scalar type is mapped to its ADR-0008
    /// <see cref="DataType"/>; a <see langword="null"/> value becomes a typed SQL <c>NULL</c> of
    /// <see cref="NullType"/>. Building a literal performs no work.
    /// </summary>
    /// <remarks>
    /// Supported .NET types and their DataType mapping:
    /// <list type="table">
    /// <listheader><term>.NET type</term><description>DataType</description></listheader>
    /// <item><term><see cref="bool"/></term><description><see cref="BooleanType"/></description></item>
    /// <item><term><see cref="sbyte"/></term><description><see cref="ByteType"/> (signed <c>tinyint</c>)</description></item>
    /// <item><term><see cref="byte"/></term><description><see cref="ShortType"/> — widened, see note below</description></item>
    /// <item><term><see cref="short"/></term><description><see cref="ShortType"/></description></item>
    /// <item><term><see cref="int"/></term><description><see cref="IntegerType"/></description></item>
    /// <item><term><see cref="long"/></term><description><see cref="LongType"/></description></item>
    /// <item><term><see cref="float"/></term><description><see cref="FloatType"/></description></item>
    /// <item><term><see cref="double"/></term><description><see cref="DoubleType"/></description></item>
    /// <item><term><see cref="string"/></term><description><see cref="StringType"/></description></item>
    /// <item><term><see cref="byte"/><c>[]</c></term><description><see cref="BinaryType"/></description></item>
    /// <item><term><see cref="decimal"/></term><description><see cref="DecimalType"/> (precision/scale from the value)</description></item>
    /// <item><term><see cref="DateOnly"/></term><description><see cref="DateType"/></description></item>
    /// <item><term><see cref="DateTime"/></term><description><see cref="TimestampType"/> — full instant, see note below</description></item>
    /// <item><term><see cref="DateTimeOffset"/></term><description><see cref="TimestampType"/></description></item>
    /// <item><term><see cref="DeltaSharp.Column"/></term><description>returned unchanged (Spark <c>lit(col)</c> idempotence)</description></item>
    /// <item><term><see langword="null"/></term><description><see cref="NullType"/></description></item>
    /// </list>
    /// <para>
    /// <b>Byte note.</b> Spark's <see cref="ByteType"/> is a <i>signed</i> 8-bit integer (.NET
    /// <see cref="sbyte"/>), so a .NET <see cref="byte"/> (unsigned, 0–255) does not fit for values
    /// above 127. To avoid silently truncating/wrapping, <c>Lit((byte)x)</c> is <b>widened</b> to
    /// <see cref="ShortType"/>, which losslessly holds every <see cref="byte"/>. Pass an
    /// <see cref="sbyte"/> to get a <see cref="ByteType"/> literal.
    /// </para>
    /// <para>
    /// <b>DateTime note.</b> A <see cref="DateTime"/> maps to <see cref="TimestampType"/> (an
    /// epoch-microsecond instant), preserving its time-of-day — matching Spark's <c>lit</c>, where a
    /// Python <c>datetime.datetime</c> (the analogue of .NET <see cref="DateTime"/>) becomes a
    /// timestamp. The <see cref="DateTime.Kind"/> is honored deterministically:
    /// <see cref="DateTimeKind.Utc"/> is used directly; <see cref="DateTimeKind.Local"/> is converted
    /// via <see cref="DateTime.ToUniversalTime"/> (machine-time-zone dependent, inherent to a local
    /// value); and <see cref="DateTimeKind.Unspecified"/> is treated as <b>UTC</b> (the deterministic
    /// choice — it avoids any machine-time-zone dependence for the common naive value). Pass a
    /// <see cref="DateOnly"/> for a date-only (<see cref="DateType"/>) literal.
    /// </para>
    /// <para>
    /// <b>Idempotence note.</b> Passing an existing <see cref="DeltaSharp.Column"/> returns it
    /// unchanged, mirroring Spark's <c>lit(col)</c>, so generic <c>object?</c> code paths can call
    /// <c>Lit</c> uniformly on values and columns alike.
    /// </para>
    /// </remarks>
    /// <param name="value">The literal value, or <see langword="null"/> for a typed SQL <c>NULL</c>.</param>
    /// <returns>A literal column.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is a .NET type with no supported literal mapping; the message names
    /// the offending type.
    /// </exception>
    public static Column Lit(object? value)
    {
        if (value is Column column)
        {
            return column;
        }

        Literal literal = value switch
        {
            null => Literal.Null(NullType.Instance),
            bool b => Literal.OfBoolean(b),
            sbyte sb => Literal.OfByte(sb),
            byte ub => Literal.OfShort(ub),
            short s => Literal.OfShort(s),
            int i => Literal.OfInt(i),
            long l => Literal.OfLong(l),
            float f => Literal.OfFloat(f),
            double d => Literal.OfDouble(d),
            string str => Literal.OfString(str),
            byte[] bytes => Literal.OfBinary(bytes),
            decimal dec => DecimalLiteral(dec),
            DateOnly date => DateLiteral(date),
            DateTime dt => TimestampLiteral(dt),
            DateTimeOffset dto => TimestampLiteral(dto),
            _ => throw new ArgumentException(
                $"Cannot create a literal from an unsupported .NET type '{value.GetType()}'. "
                + "Supported types are bool, sbyte, byte, short, int, long, float, double, string, "
                + "byte[], decimal, DateOnly, DateTime, DateTimeOffset, Column, and null.",
                nameof(value)),
        };

        return new Column(literal);
    }

    // ---------------------------------------------------------------------------------------------
    // Common functions registry (STORY-04.3.3 / #166). Each entry point below builds a single
    // *unresolved* IR node (an UnresolvedFunction, or a CaseWhen for `when`) and performs no
    // evaluation, no schema lookup, and no binding — the analyzer (FEAT-04.5 / #171) later resolves
    // and classifies the call. Aggregate vs. scalar classification is therefore purely by canonical
    // Spark name here (AC2); these builders only guarantee faithful naming and argument order.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the <b>aggregate</b> row count of <paramref name="column"/>, mirroring Spark's
    /// <c>functions.count(col)</c> — the number of rows for which the argument is non-null. Pass
    /// <see cref="Col(string)"/> with <c>"*"</c> (or <see cref="Count(string)"/> with <c>"*"</c>)
    /// to count every row. <b>Lazy:</b> builds an unresolved <c>count</c> function node; the
    /// analyzer classifies it as an aggregate. No Spark deviation.
    /// </summary>
    /// <param name="column">The column (or star) to count.</param>
    /// <returns>An unresolved aggregate <c>count</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static Column Count(Column column) => ScalarFunction("count", column, nameof(column));

    /// <summary>
    /// Convenience overload of <see cref="Count(Column)"/> that counts the column named
    /// <paramref name="columnName"/>; pass <c>"*"</c> to count every row (Spark's
    /// <c>count("*")</c>). <b>Lazy:</b> resolves the name to an unresolved reference/star, then a
    /// <c>count</c> node. No Spark deviation.
    /// </summary>
    /// <param name="columnName">The column name, or <c>"*"</c> to count all rows.</param>
    /// <returns>An unresolved aggregate <c>count</c> column.</returns>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is null or empty.</exception>
    public static Column Count(string columnName) => Count(Col(columnName));

    /// <summary>
    /// Returns the <b>aggregate</b> count of <b>distinct</b> non-null value combinations across the
    /// supplied columns, mirroring Spark's <c>functions.countDistinct(col, cols…)</c>. <b>Lazy:</b>
    /// builds a <c>count</c> function node tagged <c>DISTINCT</c> (<c>IsDistinct</c>); the analyzer
    /// classifies it as an aggregate. No Spark deviation.
    /// </summary>
    /// <param name="column">The first column (required).</param>
    /// <param name="additional">Further columns whose distinct combinations are counted.</param>
    /// <returns>An unresolved distinct aggregate <c>count</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> or
    /// <paramref name="additional"/> is null.</exception>
    /// <exception cref="ArgumentException">Any element of <paramref name="additional"/> is null.</exception>
    public static Column CountDistinct(Column column, params Column[] additional)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(additional);
        var columns = new Column[additional.Length + 1];
        columns[0] = column;
        Array.Copy(additional, 0, columns, 1, additional.Length);
        return NaryFunction("count", columns, nameof(additional), isDistinct: true);
    }

    /// <summary>
    /// Returns the <b>aggregate</b> sum of <paramref name="column"/>, mirroring Spark's
    /// <c>functions.sum(col)</c> (nulls are skipped). <b>Lazy:</b> builds an unresolved <c>sum</c>
    /// function node the analyzer classifies as an aggregate. No Spark deviation.
    /// </summary>
    /// <param name="column">The numeric column to sum.</param>
    /// <returns>An unresolved aggregate <c>sum</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static Column Sum(Column column) => ScalarFunction("sum", column, nameof(column));

    /// <summary>Convenience overload of <see cref="Sum(Column)"/> for the column named
    /// <paramref name="columnName"/>. <b>Lazy;</b> no Spark deviation.</summary>
    /// <param name="columnName">The numeric column name to sum.</param>
    /// <returns>An unresolved aggregate <c>sum</c> column.</returns>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is null or empty.</exception>
    public static Column Sum(string columnName) => Sum(Col(columnName));

    /// <summary>
    /// Returns the <b>aggregate</b> mean of <paramref name="column"/>, mirroring Spark's
    /// <c>functions.avg(col)</c> (nulls are skipped). <b>Lazy:</b> builds an unresolved <c>avg</c>
    /// function node the analyzer classifies as an aggregate. No Spark deviation (Spark's
    /// <c>mean</c> synonym is deferred).
    /// </summary>
    /// <param name="column">The numeric column to average.</param>
    /// <returns>An unresolved aggregate <c>avg</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static Column Avg(Column column) => ScalarFunction("avg", column, nameof(column));

    /// <summary>Convenience overload of <see cref="Avg(Column)"/> for the column named
    /// <paramref name="columnName"/>. <b>Lazy;</b> no Spark deviation.</summary>
    /// <param name="columnName">The numeric column name to average.</param>
    /// <returns>An unresolved aggregate <c>avg</c> column.</returns>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is null or empty.</exception>
    public static Column Avg(string columnName) => Avg(Col(columnName));

    /// <summary>
    /// Returns the <b>aggregate</b> minimum of <paramref name="column"/>, mirroring Spark's
    /// <c>functions.min(col)</c> (nulls are skipped). <b>Lazy:</b> builds an unresolved <c>min</c>
    /// function node the analyzer classifies as an aggregate. No Spark deviation.
    /// </summary>
    /// <param name="column">The column to reduce.</param>
    /// <returns>An unresolved aggregate <c>min</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static Column Min(Column column) => ScalarFunction("min", column, nameof(column));

    /// <summary>Convenience overload of <see cref="Min(Column)"/> for the column named
    /// <paramref name="columnName"/>. <b>Lazy;</b> no Spark deviation.</summary>
    /// <param name="columnName">The column name to reduce.</param>
    /// <returns>An unresolved aggregate <c>min</c> column.</returns>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is null or empty.</exception>
    public static Column Min(string columnName) => Min(Col(columnName));

    /// <summary>
    /// Returns the <b>aggregate</b> maximum of <paramref name="column"/>, mirroring Spark's
    /// <c>functions.max(col)</c> (nulls are skipped). <b>Lazy:</b> builds an unresolved <c>max</c>
    /// function node the analyzer classifies as an aggregate. No Spark deviation.
    /// </summary>
    /// <param name="column">The column to reduce.</param>
    /// <returns>An unresolved aggregate <c>max</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static Column Max(Column column) => ScalarFunction("max", column, nameof(column));

    /// <summary>Convenience overload of <see cref="Max(Column)"/> for the column named
    /// <paramref name="columnName"/>. <b>Lazy;</b> no Spark deviation.</summary>
    /// <param name="columnName">The column name to reduce.</param>
    /// <returns>An unresolved aggregate <c>max</c> column.</returns>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is null or empty.</exception>
    public static Column Max(string columnName) => Max(Col(columnName));

    /// <summary>
    /// Returns the first non-null value among <paramref name="columns"/>, mirroring Spark's
    /// <b>scalar</b> <c>functions.coalesce(cols…)</c>. <b>Lazy:</b> builds an unresolved
    /// <c>coalesce</c> function node (never an aggregate). No Spark deviation.
    /// </summary>
    /// <param name="columns">The candidate columns, tried left to right (at least one).</param>
    /// <returns>An unresolved scalar <c>coalesce</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="columns"/> is empty or contains a null
    /// element.</exception>
    public static Column Coalesce(params Column[] columns) =>
        NaryFunction("coalesce", columns, nameof(columns));

    /// <summary>
    /// Starts a <c>CASE</c> expression that yields <paramref name="value"/> when
    /// <paramref name="condition"/> holds, mirroring Spark's <c>functions.when(condition, value)</c>.
    /// Chain further branches with <see cref="DeltaSharp.Column.When(Column, object?)"/> and a default
    /// with <see cref="DeltaSharp.Column.Otherwise(object?)"/>; with no <c>otherwise</c>, an unmatched
    /// row is SQL <c>NULL</c> (Spark parity). <b>Lazy:</b> builds an unresolved <c>CaseWhen</c> node;
    /// <paramref name="value"/> is wrapped via <see cref="Lit(object?)"/> (an existing
    /// <see cref="DeltaSharp.Column"/> passes through unchanged). No Spark deviation.
    /// </summary>
    /// <param name="condition">The boolean predicate column (built via Column operators, #165).</param>
    /// <param name="value">The result when <paramref name="condition"/> is true; a literal or a
    /// <see cref="DeltaSharp.Column"/>.</param>
    /// <returns>An unresolved <c>CaseWhen</c> column open for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is null.</exception>
    public static Column When(Column condition, object? value)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return new Column(new CaseWhen(condition.Expr, Lit(value).Expr));
    }

    /// <summary>
    /// Parses the SQL expression string <paramref name="expression"/>, mirroring Spark's
    /// <c>functions.expr(sqlText)</c>. <b>Not supported in M1 Core:</b> the SQL expression parser is
    /// the SQL frontend (EPIC-07 / #159) and is not available here, so this door throws a documented
    /// unsupported-feature diagnostic (AC3) rather than half-parsing SQL. Compose the same intent
    /// with the typed <see cref="Functions"/> / <see cref="DeltaSharp.Column"/> builders in the
    /// meantime. Builds nothing.
    /// </summary>
    /// <param name="expression">The SQL expression text (validated non-empty before the diagnostic).</param>
    /// <returns>Never returns; always throws.</returns>
    /// <exception cref="ArgumentException"><paramref name="expression"/> is null or empty.</exception>
    /// <exception cref="NotSupportedException">Always — SQL expression parsing lands with the SQL
    /// frontend (EPIC-07 / #159).</exception>
    public static Column Expr(string expression)
    {
        ArgumentException.ThrowIfNullOrEmpty(expression);
        throw new NotSupportedException(
            "Functions.Expr(string) requires the SQL expression parser, which is delivered by the "
            + "SQL frontend (EPIC-07, issue #159) and is not available in the M1 Core API. Build the "
            + "expression with the typed Functions/Column builders instead.");
    }

    /// <summary>
    /// Returns the uppercase of the string <paramref name="column"/>, mirroring Spark's <b>scalar</b>
    /// <c>functions.upper(col)</c>. <b>Lazy:</b> builds an unresolved <c>upper</c> function node. No
    /// Spark deviation.
    /// </summary>
    /// <param name="column">The string column.</param>
    /// <returns>An unresolved scalar <c>upper</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static Column Upper(Column column) => ScalarFunction("upper", column, nameof(column));

    /// <summary>
    /// Returns the lowercase of the string <paramref name="column"/>, mirroring Spark's <b>scalar</b>
    /// <c>functions.lower(col)</c>. <b>Lazy:</b> builds an unresolved <c>lower</c> function node. No
    /// Spark deviation.
    /// </summary>
    /// <param name="column">The string column.</param>
    /// <returns>An unresolved scalar <c>lower</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static Column Lower(Column column) => ScalarFunction("lower", column, nameof(column));

    /// <summary>
    /// Returns the character length of the string <paramref name="column"/>, mirroring Spark's
    /// <b>scalar</b> <c>functions.length(col)</c>. <b>Lazy:</b> builds an unresolved <c>length</c>
    /// function node. No Spark deviation.
    /// </summary>
    /// <param name="column">The string column.</param>
    /// <returns>An unresolved scalar <c>length</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static Column Length(Column column) => ScalarFunction("length", column, nameof(column));

    /// <summary>
    /// Trims leading and trailing whitespace from the string <paramref name="column"/>, mirroring
    /// Spark's <b>scalar</b> <c>functions.trim(col)</c>. <b>Lazy:</b> builds an unresolved
    /// <c>trim</c> function node. No Spark deviation.
    /// </summary>
    /// <param name="column">The string column.</param>
    /// <returns>An unresolved scalar <c>trim</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static Column Trim(Column column) => ScalarFunction("trim", column, nameof(column));

    /// <summary>
    /// Concatenates <paramref name="columns"/> into one string, mirroring Spark's <b>scalar</b>
    /// <c>functions.concat(cols…)</c>. <b>Lazy:</b> builds an unresolved <c>concat</c> function
    /// node. No Spark deviation.
    /// </summary>
    /// <param name="columns">The columns to concatenate, in order (at least one).</param>
    /// <returns>An unresolved scalar <c>concat</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="columns"/> is empty or contains a null
    /// element.</exception>
    public static Column Concat(params Column[] columns) =>
        NaryFunction("concat", columns, nameof(columns));

    /// <summary>
    /// Returns the current date at the start of query evaluation, mirroring Spark's <b>scalar</b>
    /// <c>functions.current_date()</c>. <b>Lazy:</b> builds a zero-argument unresolved
    /// <c>current_date</c> function node; the value is fixed once per query at execution, not at
    /// build time. No Spark deviation.
    /// </summary>
    /// <returns>An unresolved scalar <c>current_date</c> column.</returns>
    public static Column CurrentDate() => NullaryFunction("current_date");

    /// <summary>
    /// Returns the current timestamp at the start of query evaluation, mirroring Spark's
    /// <b>scalar</b> <c>functions.current_timestamp()</c>. <b>Lazy:</b> builds a zero-argument
    /// unresolved <c>current_timestamp</c> function node; the value is fixed once per query at
    /// execution, not at build time. No Spark deviation.
    /// </summary>
    /// <returns>An unresolved scalar <c>current_timestamp</c> column.</returns>
    public static Column CurrentTimestamp() => NullaryFunction("current_timestamp");

    /// <summary>
    /// Extracts the year from the date/timestamp <paramref name="column"/>, mirroring Spark's
    /// <b>scalar</b> <c>functions.year(col)</c>. <b>Lazy:</b> builds an unresolved <c>year</c>
    /// function node. No Spark deviation.
    /// </summary>
    /// <param name="column">The date/timestamp column.</param>
    /// <returns>An unresolved scalar <c>year</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static Column Year(Column column) => ScalarFunction("year", column, nameof(column));

    /// <summary>
    /// Extracts the month (1–12) from the date/timestamp <paramref name="column"/>, mirroring
    /// Spark's <b>scalar</b> <c>functions.month(col)</c>. <b>Lazy:</b> builds an unresolved
    /// <c>month</c> function node. No Spark deviation.
    /// </summary>
    /// <param name="column">The date/timestamp column.</param>
    /// <returns>An unresolved scalar <c>month</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static Column Month(Column column) => ScalarFunction("month", column, nameof(column));

    /// <summary>
    /// Extracts the day of month (1–31) from the date/timestamp <paramref name="column"/>, mirroring
    /// Spark's <b>scalar</b> <c>functions.dayofmonth(col)</c>. <b>Lazy:</b> builds an unresolved
    /// <c>dayofmonth</c> function node. No Spark deviation.
    /// </summary>
    /// <param name="column">The date/timestamp column.</param>
    /// <returns>An unresolved scalar <c>dayofmonth</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static Column DayOfMonth(Column column) =>
        ScalarFunction("dayofmonth", column, nameof(column));

    /// <summary>
    /// Converts the <paramref name="column"/> to a date, mirroring Spark's <b>scalar</b>
    /// <c>functions.to_date(col)</c> (default format). <b>Lazy:</b> builds an unresolved
    /// <c>to_date</c> function node. No Spark deviation (the optional format-string overload is
    /// deferred).
    /// </summary>
    /// <param name="column">The column to convert to a date.</param>
    /// <returns>An unresolved scalar <c>to_date</c> column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    public static Column ToDate(Column column) => ScalarFunction("to_date", column, nameof(column));

    private static Column ScalarFunction(string name, Column column, string paramName)
    {
        ArgumentNullException.ThrowIfNull(column, paramName);
        return new Column(new UnresolvedFunction(name, new[] { column.Expr }));
    }

    private static Column NaryFunction(
        string name, Column[] columns, string paramName, bool isDistinct = false)
    {
        ArgumentNullException.ThrowIfNull(columns, paramName);
        if (columns.Length == 0)
        {
            throw new ArgumentException(
                $"{name}(...) requires at least one column argument.", paramName);
        }

        var arguments = new Expression[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            Column column = columns[i]
                ?? throw new ArgumentException(
                    $"{name}(...) column arguments cannot be null.", paramName);
            arguments[i] = column.Expr;
        }

        return new Column(new UnresolvedFunction(name, arguments, isDistinct));
    }

    private static Column NullaryFunction(string name) =>
        new(new UnresolvedFunction(name, Array.Empty<Expression>()));

    private static Literal DateLiteral(DateOnly date) =>
        Literal.OfDate(date.DayNumber - UnixEpochDate.DayNumber);

    private static Literal TimestampLiteral(DateTimeOffset value) =>
        Literal.OfTimestamp(ToEpochMicros(value));

    private static Literal TimestampLiteral(DateTime value)
    {
        // Normalize to a UTC instant, then reuse the same epoch-micros path as DateTimeOffset.
        // Kind is honored deterministically: Local converts via the machine time zone; Utc and
        // Unspecified are both taken as UTC (Unspecified is treated as UTC by deliberate choice so
        // the common naive value never depends on the machine time zone).
        DateTimeOffset instant = value.Kind == DateTimeKind.Local
            ? new DateTimeOffset(value.ToUniversalTime().Ticks, TimeSpan.Zero)
            : new DateTimeOffset(value.Ticks, TimeSpan.Zero);

        return Literal.OfTimestamp(ToEpochMicros(instant));
    }

    // Microseconds since the Unix epoch. Sub-microsecond ticks are floored toward negative infinity
    // (matching Spark's Math.floorDiv), NOT truncated toward zero: an instant one tick (100 ns)
    // before 1970 maps to -1 micros, not 0, so temporal ordering stays monotonic across the epoch
    // boundary and pre-1970 timestamps agree with Spark's instantToMicros.
    private static long ToEpochMicros(DateTimeOffset value)
    {
        long ticks = (value - DateTimeOffset.UnixEpoch).Ticks;
        long micros = ticks / TimeSpan.TicksPerMicrosecond;
        if (ticks < 0 && micros * TimeSpan.TicksPerMicrosecond != ticks)
        {
            micros--;
        }

        return micros;
    }

    private static Literal DecimalLiteral(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(value, bits);

        int scale = (bits[3] >> 16) & 0x7F;
        bool negative = (bits[3] & unchecked((int)0x80000000)) != 0;
        UInt128 magnitude =
            ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0];
        Int128 unscaled = negative ? -(Int128)magnitude : (Int128)magnitude;

        int precision = Math.Max(Math.Max(CountDigits(magnitude), scale), DecimalType.MinPrecision);
        return Literal.OfDecimal(unscaled, new DecimalType(precision, scale));
    }

    private static int CountDigits(UInt128 value)
    {
        int digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }

        return digits;
    }
}
