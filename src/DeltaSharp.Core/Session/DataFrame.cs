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

    /// <summary>
    /// Joins this frame with <paramref name="right"/> with <b>no</b> join condition, mirroring Spark's
    /// <c>Dataset.join(right)</c> — an inner join whose absent condition yields the Cartesian product
    /// (add a condition, or use <see cref="Join(DataFrame, Column)"/>, to avoid it). This is a lazy
    /// <b>transformation</b>: it records a <c>Join</c> over both frames' plans and reads
    /// <b>neither</b> side (ADR-0001). Both frames are unchanged; the result shares both plan subtrees
    /// by reference (structural sharing, #167).
    /// </summary>
    /// <param name="right">The frame to join with.</param>
    /// <returns>A new <see cref="DataFrame"/> joining the two frames.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="right"/> is null.</exception>
    public DataFrame Join(DataFrame right)
    {
        ArgumentNullException.ThrowIfNull(right);
        return new DataFrame(new Join(Plan, right.Plan, JoinType.Inner));
    }

    /// <summary>
    /// Joins this frame with <paramref name="right"/> on <paramref name="condition"/> as an
    /// <b>inner</b> join, mirroring Spark's <c>Dataset.join(right, joinExprs)</c>. The condition is
    /// only <i>recorded</i> in the plan — never evaluated — and neither side is read (ADR-0001, a lazy
    /// transformation leaving both frames unchanged).
    /// </summary>
    /// <param name="right">The frame to join with.</param>
    /// <param name="condition">The boolean join condition.</param>
    /// <returns>A new <see cref="DataFrame"/> inner-joining on <paramref name="condition"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="right"/> or <paramref name="condition"/>
    /// is null.</exception>
    public DataFrame Join(DataFrame right, Column condition) => Join(right, condition, "inner");

    /// <summary>
    /// Joins this frame with <paramref name="right"/> on <paramref name="condition"/> using the
    /// join kind named by <paramref name="joinType"/>, mirroring Spark's
    /// <c>Dataset.join(right, joinExprs, joinType)</c>. <paramref name="joinType"/> is a Spark
    /// join-type alias (case- and separator-insensitive): <c>"inner"</c>, <c>"cross"</c>,
    /// <c>"outer"</c>/<c>"full"</c>, <c>"left"</c>, <c>"right"</c>, <c>"left_semi"</c>,
    /// <c>"left_anti"</c> (and their <c>leftouter</c>/<c>semi</c>/… variants). This is a lazy
    /// transformation: it records the <c>Join</c> and reads neither side (ADR-0001).
    /// </summary>
    /// <param name="right">The frame to join with.</param>
    /// <param name="condition">The boolean join condition.</param>
    /// <param name="joinType">The Spark join-type alias.</param>
    /// <returns>A new <see cref="DataFrame"/> joining on <paramref name="condition"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="right"/>, <paramref name="condition"/>,
    /// or <paramref name="joinType"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="joinType"/> is not a supported Spark
    /// join-type alias; the message names the valid aliases (AC3).</exception>
    public DataFrame Join(DataFrame right, Column condition, string joinType)
    {
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(condition);
        JoinType type = JoinTypes.FromSparkString(joinType);
        return new DataFrame(new Join(Plan, right.Plan, type, condition.Expr));
    }

    /// <summary>
    /// Inner-joins this frame with <paramref name="right"/> on a single shared column, mirroring
    /// Spark's <c>Dataset.join(right, usingColumn)</c>. Building the node is supported now — it
    /// records the shared column pre-resolution and reads neither side (a lazy transformation,
    /// ADR-0001).
    /// <para>
    /// <b>Resolution is deferred.</b> The analyzer rule that desugars a using-column join into an
    /// equi-condition is not yet implemented, so <b>analyzing</b> a plan that contains this join
    /// currently fails with a targeted <c>AnalysisException</c> until the follow-up
    /// (<see href="https://github.com/khaines/deltasharp/issues/405">#405</see>) lands.
    /// </para>
    /// </summary>
    /// <param name="right">The frame to join with.</param>
    /// <param name="usingColumn">The name of the column shared by both frames.</param>
    /// <returns>A new <see cref="DataFrame"/> inner-joining on the shared column.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="right"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="usingColumn"/> is null or empty.</exception>
    public DataFrame Join(DataFrame right, string usingColumn)
    {
        ArgumentNullException.ThrowIfNull(right);
        ArgumentException.ThrowIfNullOrEmpty(usingColumn);
        return Join(right, new[] { usingColumn }, "inner");
    }

    /// <summary>
    /// Inner-joins this frame with <paramref name="right"/> on a set of shared columns, mirroring
    /// Spark's <c>Dataset.join(right, usingColumns)</c>. Like the single-column overload the shared
    /// columns are recorded pre-resolution and this reads neither side.
    /// <para>
    /// <b>Resolution is deferred</b> (the desugar-to-equi-condition rule is not yet implemented);
    /// analyzing a plan with this join fails with a targeted <c>AnalysisException</c> until
    /// <see href="https://github.com/khaines/deltasharp/issues/405">#405</see> lands.
    /// </para>
    /// </summary>
    /// <param name="right">The frame to join with.</param>
    /// <param name="usingColumns">The names of the columns shared by both frames.</param>
    /// <returns>A new <see cref="DataFrame"/> inner-joining on the shared columns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="right"/> or
    /// <paramref name="usingColumns"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="usingColumns"/> is empty or contains a null
    /// or empty name.</exception>
    public DataFrame Join(DataFrame right, IEnumerable<string> usingColumns) =>
        Join(right, usingColumns, "inner");

    /// <summary>
    /// Joins this frame with <paramref name="right"/> on a set of shared columns using the join kind
    /// named by <paramref name="joinType"/>, mirroring Spark's
    /// <c>Dataset.join(right, usingColumns, joinType)</c>. A lazy transformation reading neither side.
    /// <para>
    /// <b>Resolution is deferred</b> (the desugar-to-equi-condition rule is not yet implemented);
    /// analyzing a plan with this join fails with a targeted <c>AnalysisException</c> until
    /// <see href="https://github.com/khaines/deltasharp/issues/405">#405</see> lands.
    /// </para>
    /// </summary>
    /// <param name="right">The frame to join with.</param>
    /// <param name="usingColumns">The names of the columns shared by both frames.</param>
    /// <param name="joinType">The Spark join-type alias.</param>
    /// <returns>A new <see cref="DataFrame"/> joining on the shared columns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="right"/>,
    /// <paramref name="usingColumns"/>, or <paramref name="joinType"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="joinType"/> is not a supported Spark
    /// join-type alias, or <paramref name="usingColumns"/> is empty or contains a null/empty
    /// name.</exception>
    public DataFrame Join(DataFrame right, IEnumerable<string> usingColumns, string joinType)
    {
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(usingColumns);
        JoinType type = JoinTypes.FromSparkString(joinType);
        var columns = new List<string>();
        foreach (string column in usingColumns)
        {
            ArgumentException.ThrowIfNullOrEmpty(column, nameof(usingColumns));
            columns.Add(column);
        }

        return new DataFrame(new Join(Plan, right.Plan, type, usingColumns: columns));
    }

    /// <summary>
    /// Explicitly cross-joins (Cartesian product) this frame with <paramref name="right"/>, mirroring
    /// Spark's <c>Dataset.crossJoin(right)</c> — the safe, intent-revealing way to ask for a product
    /// (unlike a conditionless <see cref="Join(DataFrame)"/>, which records an <c>Inner</c> join).
    /// This is a lazy <b>transformation</b>: it records a <c>Cross</c> join with <b>no</b> condition
    /// and reads <b>neither</b> side (ADR-0001); both frames are unchanged and the result shares both
    /// plan subtrees by reference (structural sharing, #167).
    /// </summary>
    /// <param name="right">The frame to cross-join with.</param>
    /// <returns>A new <see cref="DataFrame"/> that is the Cartesian product of the two frames.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="right"/> is null.</exception>
    public DataFrame CrossJoin(DataFrame right)
    {
        ArgumentNullException.ThrowIfNull(right);
        return new DataFrame(new Join(Plan, right.Plan, JoinType.Cross, condition: null));
    }

    /// <summary>
    /// Orders rows by the given columns, returning a new <see cref="DataFrame"/> whose plan is a
    /// global <c>Sort</c> over this frame's plan, mirroring Spark's <c>Dataset.orderBy(Column*)</c>.
    /// A plain <see cref="Column"/> sorts <b>ascending</b> (SQL <c>NULL</c>s first); wrap it with
    /// <see cref="Column.Desc"/>/<see cref="Column.Asc"/> to choose direction and null placement.
    /// This is a lazy <b>transformation</b>: the ordering is only recorded — nothing is sorted and no
    /// side is read (ADR-0001) — and this instance is unchanged.
    /// </summary>
    /// <param name="columns">The ordering columns, in precedence order.</param>
    /// <returns>A new <see cref="DataFrame"/> ordered by <paramref name="columns"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> or any element is null.</exception>
    public DataFrame OrderBy(params Column[] columns) => BuildSort(columns);

    /// <summary>
    /// Orders rows by the named columns (all ascending), returning a new <see cref="DataFrame"/>,
    /// mirroring Spark's <c>Dataset.orderBy(String, String*)</c>. Each name is turned into an
    /// ascending ordering term. Like the <see cref="Column"/> overload this is lazy and leaves this
    /// instance unchanged.
    /// </summary>
    /// <param name="column">The first column name to order by.</param>
    /// <param name="columns">Any further column names, in precedence order.</param>
    /// <returns>A new <see cref="DataFrame"/> ordered by the named columns.</returns>
    /// <exception cref="ArgumentException"><paramref name="column"/> or any element of
    /// <paramref name="columns"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> is null.</exception>
    public DataFrame OrderBy(string column, params string[] columns) => BuildSort(column, columns);

    /// <summary>
    /// An alias for <see cref="OrderBy(Column[])"/>, mirroring Spark's <c>Dataset.sort(Column*)</c>.
    /// Spark's <c>sort</c> and <c>orderBy</c> are exact synonyms (both a global <c>Sort</c>); this
    /// method is identical to <see cref="OrderBy(Column[])"/>.
    /// </summary>
    /// <param name="columns">The ordering columns, in precedence order.</param>
    /// <returns>A new <see cref="DataFrame"/> ordered by <paramref name="columns"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> or any element is null.</exception>
    public DataFrame Sort(params Column[] columns) => BuildSort(columns);

    /// <summary>
    /// An alias for <see cref="OrderBy(string, string[])"/>, mirroring Spark's
    /// <c>Dataset.sort(String, String*)</c>.
    /// </summary>
    /// <param name="column">The first column name to order by.</param>
    /// <param name="columns">Any further column names, in precedence order.</param>
    /// <returns>A new <see cref="DataFrame"/> ordered by the named columns.</returns>
    /// <exception cref="ArgumentException"><paramref name="column"/> or any element of
    /// <paramref name="columns"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> is null.</exception>
    public DataFrame Sort(string column, params string[] columns) => BuildSort(column, columns);

    /// <summary>
    /// Returns a new <see cref="DataFrame"/> that keeps at most <paramref name="n"/> rows, mirroring
    /// Spark's <c>Dataset.limit(int)</c>. This is a lazy <b>transformation</b>: the bound is recorded
    /// in a <c>Limit</c> plan node and no rows are materialized (ADR-0001).
    /// </summary>
    /// <param name="n">The maximum number of rows to keep (non-negative).</param>
    /// <returns>A new <see cref="DataFrame"/> limited to <paramref name="n"/> rows.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="n"/> is negative.</exception>
    public DataFrame Limit(int n) => new(new Limit(n, Plan));

    /// <summary>
    /// Returns a new <see cref="DataFrame"/> with duplicate rows removed, mirroring Spark's
    /// <c>Dataset.distinct()</c>. This is a lazy <b>transformation</b>: it records a <c>Distinct</c>
    /// node and deduplicates nothing here (ADR-0001). This instance is unchanged.
    /// </summary>
    /// <returns>A new <see cref="DataFrame"/> over distinct rows.</returns>
    public DataFrame Distinct() => new(new Distinct(Plan));

    /// <summary>
    /// Returns a new <see cref="DataFrame"/> containing the rows of this frame followed by those of
    /// <paramref name="other"/>, mirroring Spark's <c>Dataset.union(other)</c>. Union is
    /// <b>by position</b> (the <i>n</i>th column of each side is unioned, regardless of name) and
    /// <b>row-preserving</b> (no deduplication — call <see cref="Distinct"/> to dedupe). This is a
    /// lazy <b>transformation</b>: it records a <c>Union</c> over both plans and reads neither side
    /// (ADR-0001).
    /// </summary>
    /// <remarks>
    /// By-<b>name</b> alignment is Spark's separate <c>unionByName</c> (deferred, tracked by
    /// <see href="https://github.com/khaines/deltasharp/issues/405">#405</see>).
    /// </remarks>
    /// <param name="other">The frame whose rows are appended.</param>
    /// <returns>A new <see cref="DataFrame"/> unioning the two frames by position.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public DataFrame Union(DataFrame other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new DataFrame(new Union(new[] { Plan, other.Plan }));
    }

    /// <summary>
    /// An alias for <see cref="Union(DataFrame)"/>, mirroring Spark's <c>Dataset.unionAll(other)</c>.
    /// In Spark <c>unionAll</c> is a deprecated synonym of <c>union</c> — both are the same
    /// row-preserving, by-position bag union — so this is exactly equivalent to
    /// <see cref="Union(DataFrame)"/>. DeltaSharp intentionally keeps this member
    /// <b>non-obsolete</b> for migration ergonomics: Spark code that calls <c>unionAll</c> ports
    /// across without warning churn (see the design doc §6).
    /// </summary>
    /// <param name="other">The frame whose rows are appended.</param>
    /// <returns>A new <see cref="DataFrame"/> unioning the two frames by position.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public DataFrame UnionAll(DataFrame other) => Union(other);

    private DataFrame BuildSort(Column[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var order = new Expression[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            Column column = columns[i]
                ?? throw new ArgumentNullException(
                    nameof(columns), $"Ordering column at index {i} is null.");
            order[i] = ToSortOrder(column.Expr);
        }

        return new DataFrame(new Sort(order, global: true, Plan));
    }

    private DataFrame BuildSort(string column, string[] columns)
    {
        ArgumentException.ThrowIfNullOrEmpty(column);
        ArgumentNullException.ThrowIfNull(columns);
        var order = new Expression[columns.Length + 1];
        order[0] = ToSortOrder(Functions.Col(column).Expr);
        for (int i = 0; i < columns.Length; i++)
        {
            order[i + 1] = ToSortOrder(Functions.Col(columns[i]).Expr);
        }

        return new DataFrame(new Sort(order, global: true, Plan));
    }

    /// <summary>Wraps a bare expression as an ascending (nulls-first) ordering term, but passes an
    /// expression that is already a <see cref="SortOrder"/> (from <see cref="Column.Asc"/>/
    /// <see cref="Column.Desc"/>) through unchanged, matching Spark's <c>orderBy</c> defaulting.</summary>
    private static Expression ToSortOrder(Expression expr) =>
        expr is SortOrder
            ? expr
            : new SortOrder(expr, SortDirection.Ascending, NullOrdering.NullsFirst);
}
