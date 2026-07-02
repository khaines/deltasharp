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
        return new DataFrame(new Project(ToExprs(columns, nameof(columns)), Plan));
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
        return new DataFrame(new Project(NamesToExprs(column, columns, nameof(columns)), Plan));
    }

    /// <summary>
    /// Filters rows by a boolean <paramref name="condition"/>, returning a new <see cref="DataFrame"/>
    /// whose plan is a <c>Filter</c> over this frame's plan, mirroring Spark's
    /// <c>Dataset.filter(Column)</c>. This is a <b>transformation</b>: the predicate is only
    /// <i>recorded</i> in the plan — it is never evaluated here, and no scan or backend call happens
    /// (ADR-0001). This instance is unchanged.
    /// </summary>
    /// <remarks>
    /// Only the <see cref="Column"/>-typed overload ships in M1. Spark's string/SQL-expression
    /// overload (<c>filter(conditionExpr: String)</c>) needs the SQL expression parser and lands with
    /// the SQL frontend (ADR-0007, STORY-07.2.1 / #217); until then, build predicates with
    /// <see cref="Filter(Column)"/> and the <see cref="Column"/> operators.
    /// </remarks>
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
    /// <remarks>
    /// As with <see cref="Filter(Column)"/>, only the <see cref="Column"/>-typed overload ships in M1;
    /// Spark's string/SQL-expression <c>where(conditionExpr: String)</c> lands with the SQL frontend
    /// (ADR-0007, STORY-07.2.1 / #217).
    /// </remarks>
    /// <param name="condition">The boolean predicate to retain rows by.</param>
    /// <returns>A new <see cref="DataFrame"/> filtered by <paramref name="condition"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is null.</exception>
    public DataFrame Where(Column condition) => Filter(condition);

    /// <summary>
    /// Adds a column by appending it to this frame's output, returning a new <see cref="DataFrame"/>,
    /// mirroring Spark's <c>Dataset.withColumn(String, Column)</c>. This is a lazy
    /// <b>transformation</b> that leaves this instance unchanged and performs no work (ADR-0001).
    /// <b>Append</b> (a <paramref name="colName"/> that does not match an existing column) is fully
    /// correct end-to-end today. Spark's <b>in-place replacement</b> of a same-named column is the
    /// intended parity target but is <b>not yet implemented</b> in M1 (see the remarks and issue
    /// #398); a name that matches an existing column currently yields a duplicate rather than an
    /// in-place replace.
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
    /// to the analyzer (tracked as FEAT-04.5 follow-up work in
    /// <see href="https://github.com/khaines/deltasharp/issues/398">#398</see>). See
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

    /// <summary>
    /// Groups rows by a set of column expressions, returning a <see cref="RelationalGroupedDataset"/>
    /// on which an aggregation can be chosen, mirroring Spark's <c>Dataset.groupBy(cols: Column*)</c>.
    /// This is a <b>transformation</b>: it only <i>records</i> the grouping expressions over this
    /// frame's plan — no grouping is computed, no schema is consulted, and no scan or backend call
    /// happens (ADR-0001). This instance is unchanged; the grouped handle shares this frame's plan by
    /// reference (structural sharing, #167).
    /// </summary>
    /// <remarks>
    /// The returned handle is <b>not</b> a <see cref="DataFrame"/> — a grouping alone is not a
    /// scannable relation — so it exposes only aggregation doors
    /// (<see cref="RelationalGroupedDataset.Agg(Column, Column[])"/>,
    /// <see cref="RelationalGroupedDataset.Count"/>), each of which produces a
    /// <see cref="DataFrame"/> wrapping an <c>Aggregate</c> plan. Grouping by no columns
    /// (<c>GroupBy()</c>) records an empty grouping — a global aggregation over all rows — matching
    /// the semantics of <see cref="Agg(Column, Column[])"/>.
    /// </remarks>
    /// <param name="columns">The grouping key expressions, in order.</param>
    /// <returns>A <see cref="RelationalGroupedDataset"/> recording the grouping.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> or any element is null.</exception>
    public RelationalGroupedDataset GroupBy(params Column[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return new RelationalGroupedDataset(Plan, ToExprs(columns, nameof(columns)));
    }

    /// <summary>
    /// Groups rows by column name, returning a <see cref="RelationalGroupedDataset"/>, mirroring
    /// Spark's <c>Dataset.groupBy(col1: String, cols: String*)</c>. Each name is turned into an
    /// unresolved reference via <see cref="Functions.Col(string)"/>. Like the
    /// <see cref="GroupBy(Column[])"/> overload this is a lazy transformation that leaves this
    /// instance unchanged.
    /// </summary>
    /// <remarks>
    /// The first name is a required parameter (Spark's <c>groupBy(col1: String, cols: String*)</c>
    /// shape), so <c>GroupBy()</c> with no arguments unambiguously resolves to the
    /// <see cref="GroupBy(Column[])"/> overload (a global, no-key grouping).
    /// </remarks>
    /// <param name="column">The first grouping column name.</param>
    /// <param name="columns">Any further grouping column names, in order.</param>
    /// <returns>A <see cref="RelationalGroupedDataset"/> recording the grouping.</returns>
    /// <exception cref="ArgumentException"><paramref name="column"/> or any element of
    /// <paramref name="columns"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> is null.</exception>
    public RelationalGroupedDataset GroupBy(string column, params string[] columns)
    {
        ArgumentException.ThrowIfNullOrEmpty(column);
        ArgumentNullException.ThrowIfNull(columns);
        return new RelationalGroupedDataset(Plan, NamesToExprs(column, columns, nameof(columns)));
    }

    /// <summary>
    /// Computes one or more aggregate expressions over the whole frame (no grouping), returning a new
    /// <see cref="DataFrame"/>, mirroring Spark's <c>Dataset.agg(expr: Column, exprs: Column*)</c> —
    /// which is exactly <c>groupBy().agg(...)</c>. This is a lazy <b>transformation</b> that builds an
    /// <c>Aggregate</c> plan with an <b>empty grouping</b> and evaluates nothing (ADR-0001). This
    /// instance is unchanged.
    /// </summary>
    /// <param name="expr">The first aggregate expression (required).</param>
    /// <param name="exprs">Any further aggregate expressions, in output order.</param>
    /// <returns>A new <see cref="DataFrame"/> wrapping the global aggregation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expr"/>, <paramref name="exprs"/>, or
    /// any element of <paramref name="exprs"/> is null.</exception>
    public DataFrame Agg(Column expr, params Column[] exprs) =>
        new RelationalGroupedDataset(Plan, Array.Empty<Expression>()).Agg(expr, exprs);

    /// <summary>
    /// Materializes a <see cref="Column"/> array into the internal <see cref="Expression"/> array the
    /// plan nodes consume, unwrapping each <see cref="Column.Expr"/> and rejecting a null element with
    /// an <b>indexed</b> parameter name (for example <c>columns[2]</c>). Shared by
    /// <see cref="Select(Column[])"/>, <see cref="GroupBy(Column[])"/>, and
    /// <see cref="RelationalGroupedDataset.Agg(Column, Column[])"/> so the per-element null guard is
    /// expressed once.
    /// </summary>
    /// <param name="columns">The (non-null) column array to materialize.</param>
    /// <param name="paramName">The caller's parameter name, used to build the indexed guard message.</param>
    /// <returns>The unwrapped expressions, in order.</returns>
    /// <exception cref="ArgumentNullException">Any element of <paramref name="columns"/> is null.</exception>
    internal static Expression[] ToExprs(Column[] columns, string paramName)
    {
        var exprs = new Expression[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            Column column = columns[i]
                ?? throw new ArgumentNullException($"{paramName}[{i}]");
            exprs[i] = column.Expr;
        }

        return exprs;
    }

    /// <summary>
    /// Materializes a required first name plus a rest array of names into an
    /// <see cref="Expression"/> array of <see cref="UnresolvedAttribute"/>s (via
    /// <see cref="Functions.Col(string)"/>), rejecting a null-or-empty rest element with an
    /// <b>indexed</b> parameter name (for example <c>columns[1]</c>) so its guard is symmetric with the
    /// <see cref="Column"/>-array path. Shared by <see cref="Select(string, string[])"/> and
    /// <see cref="GroupBy(string, string[])"/>. The first name is assumed already validated by the
    /// caller.
    /// </summary>
    /// <param name="first">The (already-validated) first column name.</param>
    /// <param name="rest">The (non-null) rest array of column names.</param>
    /// <param name="restParamName">The caller's rest parameter name for the indexed guard message.</param>
    /// <returns>The name expressions, first then rest, in order.</returns>
    /// <exception cref="ArgumentException">Any element of <paramref name="rest"/> is null or empty.</exception>
    private static Expression[] NamesToExprs(string first, string[] rest, string restParamName)
    {
        var exprs = new Expression[rest.Length + 1];
        exprs[0] = Functions.Col(first).Expr;
        for (int i = 0; i < rest.Length; i++)
        {
            string name = rest[i];
            ArgumentException.ThrowIfNullOrEmpty(name, $"{restParamName}[{i}]");
            exprs[i + 1] = Functions.Col(name).Expr;
        }

        return exprs;
    }
}
