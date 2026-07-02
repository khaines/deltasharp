using DeltaSharp.Plans.Expressions;

namespace DeltaSharp;

/// <summary>
/// A named expression over the columns of a <see cref="DataFrame"/>, equivalent to Apache Spark's
/// <c>Column</c>. A column is a <b>lazy</b> description of intent: constructing one — with
/// <see cref="Functions.Col(string)"/>, <see cref="Functions.Lit(object?)"/>, or an alias — performs
/// <b>no</b> schema lookup and <b>no</b> evaluation. It merely wraps an immutable node of the
/// internal logical expression IR that the analyzer (FEAT-04.5) later resolves and the engine later
/// evaluates when an action runs (ADR-0001).
/// </summary>
/// <remarks>
/// <para>
/// The wrapped expression is deliberately <b>not</b> part of the public surface: users compose
/// columns through this type and the <see cref="Functions"/> entry points and never see the
/// internal IR. DeltaSharp mirrors Spark's <c>col("x").as("y")</c> ergonomics with .NET-idiomatic
/// PascalCase names (<see cref="As(string)"/>/<see cref="Alias(string)"/>/<see cref="Name(string)"/>).
/// </para>
/// <para>
/// STORY-04.3.1 delivered column references, aliases, and literals. This type also carries the
/// <c>CASE</c>-expression builders <see cref="When(Column, object?)"/>/<see cref="Otherwise(object?)"/>
/// (STORY-04.3.3, the companion to <see cref="Functions.When(Column, object?)"/>). Comparison,
/// arithmetic, and boolean operators arrive in STORY-04.3.2; the type is shaped so those additions
/// layer on without changing the wrapping contract.
/// </para>
/// </remarks>
public sealed class Column
{
    /// <summary>Wraps an immutable logical expression node.</summary>
    internal Column(Expression expression)
    {
        Expr = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    /// <summary>
    /// The wrapped, immutable logical expression node. Internal so the DataFrame API and the
    /// analyzer can unwrap a column without exposing the IR on the public surface.
    /// </summary>
    internal Expression Expr { get; }

    /// <summary>
    /// Returns a new column that gives this expression the output <paramref name="alias"/>, mirroring
    /// Spark's <c>Column.as(alias)</c>. The alias is recorded (as an <c>Alias</c> node) for the
    /// analyzer to preserve during name resolution; it triggers no work.
    /// </summary>
    /// <param name="alias">The output name to assign.</param>
    /// <returns>A new aliased <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is null or empty.</exception>
    public Column As(string alias) => new(new Alias(Expr, alias));

    /// <summary>
    /// Returns a new column that gives this expression the output <paramref name="alias"/>, mirroring
    /// Spark's <c>Column.alias(alias)</c>. Equivalent to <see cref="As(string)"/>.
    /// </summary>
    /// <param name="alias">The output name to assign.</param>
    /// <returns>A new aliased <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is null or empty.</exception>
    public Column Alias(string alias) => As(alias);

    /// <summary>
    /// Returns a new column that gives this expression the output <paramref name="alias"/>, mirroring
    /// Spark's <c>Column.name(alias)</c>. Equivalent to <see cref="As(string)"/>.
    /// </summary>
    /// <param name="alias">The output name to assign.</param>
    /// <returns>A new aliased <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is null or empty.</exception>
    public Column Name(string alias) => As(alias);

    /// <summary>
    /// Adds a <c>WHEN <paramref name="condition"/> THEN <paramref name="value"/></c> branch to a
    /// <c>CASE</c> expression, mirroring Spark's <c>Column.when(condition, value)</c>. It is valid
    /// <b>only</b> on a column previously produced by <see cref="Functions.When(Column, object?)"/>
    /// (or a prior chained <see cref="When(Column, object?)"/>) and before any
    /// <see cref="Otherwise(object?)"/> — matching Spark, which rejects <c>when</c> applied to any
    /// other column or after <c>otherwise</c>. <b>Lazy:</b> returns a new column wrapping an extended
    /// unresolved <c>CaseWhen</c>; <paramref name="value"/> is wrapped via
    /// <see cref="Functions.Lit(object?)"/>. This instance is unchanged.
    /// </summary>
    /// <param name="condition">The boolean predicate column for the new branch.</param>
    /// <param name="value">The result when <paramref name="condition"/> is true; a literal or a
    /// <see cref="Column"/>.</param>
    /// <returns>A new <c>CaseWhen</c> column with the branch appended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is null.</exception>
    /// <exception cref="InvalidOperationException">This column was not produced by
    /// <see cref="Functions.When(Column, object?)"/>, or an <c>otherwise</c> is already set.</exception>
    public Column When(Column condition, object? value)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (Expr is not CaseWhen caseWhen)
        {
            throw new InvalidOperationException(
                "when() can only be applied on a Column previously generated by the When() function.");
        }

        return new Column(caseWhen.AddBranch(condition.Expr, Functions.Lit(value).Expr));
    }

    /// <summary>
    /// Sets the default <c>ELSE <paramref name="value"/></c> of a <c>CASE</c> expression, mirroring
    /// Spark's <c>Column.otherwise(value)</c>. It is valid <b>only</b> on a column produced by
    /// <see cref="Functions.When(Column, object?)"/> (or a chained <see cref="When(Column, object?)"/>)
    /// and only once — matching Spark, which rejects <c>otherwise</c> elsewhere or applied twice.
    /// With no <c>otherwise</c>, an unmatched row is SQL <c>NULL</c>. <b>Lazy:</b> returns a new
    /// column wrapping the closed unresolved <c>CaseWhen</c>; <paramref name="value"/> is wrapped via
    /// <see cref="Functions.Lit(object?)"/>. This instance is unchanged.
    /// </summary>
    /// <param name="value">The default result; a literal or a <see cref="Column"/>.</param>
    /// <returns>A new <c>CaseWhen</c> column with the <c>ELSE</c> set.</returns>
    /// <exception cref="InvalidOperationException">This column was not produced by
    /// <see cref="Functions.When(Column, object?)"/>, or an <c>otherwise</c> is already set.</exception>
    public Column Otherwise(object? value)
    {
        if (Expr is not CaseWhen caseWhen)
        {
            throw new InvalidOperationException(
                "otherwise() can only be applied on a Column previously generated by the When() "
                + "function.");
        }

        return new Column(caseWhen.WithElse(Functions.Lit(value).Expr));
    }

    /// <summary>
    /// Returns the Catalyst-style inline rendering of the wrapped expression (for example
    /// <c>'name</c> for an unresolved reference or <c>'name AS x</c> for an alias), matching Spark's
    /// <c>Column.toString</c>. Intended for diagnostics; it performs no analysis.
    /// </summary>
    public override string ToString() => Expr.SimpleString;
}
