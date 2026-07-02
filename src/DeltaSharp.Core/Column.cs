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
/// This story (STORY-04.3.1) delivers only column references, aliases, and literals. Operators
/// (<c>+</c>, <c>==</c>, comparisons, and boolean combinators) arrive in STORY-04.3.2; the type is
/// shaped so those additions layer on without changing the wrapping contract.
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
    /// Returns the Catalyst-style inline rendering of the wrapped expression (for example
    /// <c>'name</c> for an unresolved reference or <c>'name AS x</c> for an alias), matching Spark's
    /// <c>Column.toString</c>. Intended for diagnostics; it performs no analysis.
    /// </summary>
    public override string ToString() => Expr.SimpleString;
}
