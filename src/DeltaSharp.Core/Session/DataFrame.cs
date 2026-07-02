using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp;

/// <summary>
/// A distributed collection of data organized into named columns, equivalent to Apache Spark's
/// <c>DataFrame</c> (an untyped <c>Dataset&lt;Row&gt;</c>).
/// </summary>
/// <remarks>
/// STORY-04.1.1 (#157) introduced this type as the return shape of the <see cref="SparkSession"/>
/// doors (<see cref="SparkSession.Sql(string)"/> and the reader), backed by the immutable logical
/// <see cref="Plan"/> it wraps — the structural-sharing foundation from STORY-04.4.1 (#167).
/// STORY-04.2.1 (#160) adds the first transformation surface — <see cref="Select(Column[])"/>,
/// <see cref="Filter(Column)"/>/<see cref="Where(Column)"/>, and <see cref="WithColumn(string, Column)"/>
/// — each of which only <b>extends</b> the plan and returns a <i>new</i> <see cref="DataFrame"/>,
/// preserving the lazy invariant (transformations do no work; only actions execute — ADR-0001) and
/// this instance's immutability. Actions (<c>Collect</c>/<c>Count</c>/…) arrive in later stories.
/// Instances are created by the engine from a logical plan, not by user code, so the constructor is
/// non-public. See <c>docs/engineering/design/dataframe-transformations.md</c>.
/// </remarks>
public sealed class DataFrame
{
    /// <summary>Wraps an immutable, unresolved logical plan.</summary>
    internal DataFrame(LogicalPlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    /// <summary>The immutable, unresolved logical plan backing this DataFrame.</summary>
    internal LogicalPlan Plan { get; }

    /// <summary>
    /// Selects a set of column expressions, returning a new <see cref="DataFrame"/> whose plan is a
    /// <c>Project</c> of <paramref name="columns"/> over this frame's plan, mirroring Spark's
    /// <c>Dataset.select(Column*)</c>. This is a <b>transformation</b>: it only extends the immutable
    /// logical plan — no schema is consulted, no predicate is evaluated, and no scan or backend call
    /// happens (ADR-0001). This instance is unchanged; the two frames share this frame's plan subtree
    /// by reference (structural sharing, #167).
    /// </summary>
    /// <remarks>
    /// A star column (<see cref="Functions.Col(string)"/> with <c>"*"</c>) is preserved unexpanded in
    /// the projection and expanded to the child's output by the analyzer (FEAT-04.5). Calling
    /// <c>Select()</c> with no arguments builds an empty projection (a zero-column frame), matching
    /// Spark's <c>select()</c>.
    /// </remarks>
    /// <param name="columns">The column expressions to project, in output order.</param>
    /// <returns>A new <see cref="DataFrame"/> projecting <paramref name="columns"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> or any element is null.</exception>
    public DataFrame Select(params Column[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var projectList = new Expression[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            Column column = columns[i]
                ?? throw new ArgumentNullException($"{nameof(columns)}[{i}]");
            projectList[i] = column.Expr;
        }

        return new DataFrame(new Project(projectList, Plan));
    }

    /// <summary>
    /// Selects columns by name, returning a new <see cref="DataFrame"/> whose plan is a <c>Project</c>
    /// over this frame's plan, mirroring Spark's <c>Dataset.select(String, String*)</c>. Each name is
    /// turned into an unresolved reference via <see cref="Functions.Col(string)"/> (so <c>"*"</c> is a
    /// star the analyzer expands). Like the <see cref="Select(Column[])"/> overload this is a lazy
    /// transformation that leaves this instance unchanged.
    /// </summary>
    /// <remarks>
    /// The first name is a required parameter (Spark's <c>select(col: String, cols: String*)</c>
    /// shape), so <c>Select()</c> with no arguments unambiguously resolves to the
    /// <see cref="Select(Column[])"/> overload rather than being an ambiguous call.
    /// </remarks>
    /// <param name="column">The first column name to project.</param>
    /// <param name="columns">Any further column names, in output order.</param>
    /// <returns>A new <see cref="DataFrame"/> projecting the named columns.</returns>
    /// <exception cref="ArgumentException"><paramref name="column"/> or any element of
    /// <paramref name="columns"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> is null.</exception>
    public DataFrame Select(string column, params string[] columns)
    {
        ArgumentException.ThrowIfNullOrEmpty(column);
        ArgumentNullException.ThrowIfNull(columns);
        var projectList = new Expression[columns.Length + 1];
        projectList[0] = Functions.Col(column).Expr;
        for (int i = 0; i < columns.Length; i++)
        {
            projectList[i + 1] = Functions.Col(columns[i]).Expr;
        }

        return new DataFrame(new Project(projectList, Plan));
    }

    /// <summary>
    /// Filters rows by a boolean <paramref name="condition"/>, returning a new <see cref="DataFrame"/>
    /// whose plan is a <c>Filter</c> over this frame's plan, mirroring Spark's
    /// <c>Dataset.filter(Column)</c>. This is a <b>transformation</b>: the predicate is only
    /// <i>recorded</i> in the plan — it is never evaluated here, and no scan or backend call happens
    /// (ADR-0001). This instance is unchanged.
    /// </summary>
    /// <param name="condition">The boolean predicate to retain rows by.</param>
    /// <returns>A new <see cref="DataFrame"/> filtered by <paramref name="condition"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is null.</exception>
    public DataFrame Filter(Column condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return new DataFrame(new Filter(condition.Expr, Plan));
    }

    /// <summary>
    /// An alias for <see cref="Filter(Column)"/>, mirroring Spark's <c>Dataset.where(Column)</c>. It
    /// is exactly equivalent — same lazy <c>Filter</c> plan node, same immutability.
    /// </summary>
    /// <param name="condition">The boolean predicate to retain rows by.</param>
    /// <returns>A new <see cref="DataFrame"/> filtered by <paramref name="condition"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is null.</exception>
    public DataFrame Where(Column condition) => Filter(condition);

    /// <summary>
    /// Adds a column, or replaces an existing column of the same name, returning a new
    /// <see cref="DataFrame"/>, mirroring Spark's <c>Dataset.withColumn(String, Column)</c>. This is a
    /// lazy <b>transformation</b> that leaves this instance unchanged and performs no work (ADR-0001).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result is realized as a <c>Project([*, col AS colName])</c>: an <c>UnresolvedStar</c> that
    /// the analyzer expands to this frame's full output, followed by <paramref name="col"/> aliased to
    /// <paramref name="colName"/>. When <paramref name="colName"/> does not match an existing column
    /// this is an <b>append</b> and is fully correct end-to-end today. Spark's <b>replace-in-place</b>
    /// (a name that matches an existing column overwrites it, preserving position) is a
    /// name-resolution concern that the analyzer owns; DeltaSharp's <see cref="DataFrame"/> is a
    /// lazy, session-free plan wrapper and cannot resolve the schema at this point without executing
    /// analysis, so it builds the correct unresolved shape and defers replace-on-duplicate resolution
    /// to the analyzer (tracked with FEAT-04.5 / #170). See
    /// <c>docs/engineering/design/dataframe-transformations.md</c>.
    /// </para>
    /// </remarks>
    /// <param name="colName">The output name of the new or replaced column.</param>
    /// <param name="col">The column expression to bind to <paramref name="colName"/>.</param>
    /// <returns>A new <see cref="DataFrame"/> with the added or replaced column.</returns>
    /// <exception cref="ArgumentException"><paramref name="colName"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="col"/> is null.</exception>
    public DataFrame WithColumn(string colName, Column col)
    {
        ArgumentException.ThrowIfNullOrEmpty(colName);
        ArgumentNullException.ThrowIfNull(col);
        var projectList = new Expression[] { new UnresolvedStar(), new Alias(col.Expr, colName) };
        return new DataFrame(new Project(projectList, Plan));
    }
}
