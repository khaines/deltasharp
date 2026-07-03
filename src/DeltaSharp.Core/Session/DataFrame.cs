using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeltaSharp.Analysis;
using DeltaSharp.Execution;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

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
    /// <summary>Wraps an immutable, unresolved logical plan with no owning session (execution is
    /// unavailable until the frame is bound to a <see cref="SparkSession"/>).</summary>
    internal DataFrame(LogicalPlan plan)
        : this(null, plan)
    {
    }

    /// <summary>Wraps an immutable, unresolved logical plan bound to the <paramref name="session"/>
    /// whose <see cref="Analysis.LocalCatalog"/> and <see cref="IQueryExecutor"/> an action uses to
    /// analyze and execute it. A <see langword="null"/> session yields a session-free frame.</summary>
    internal DataFrame(SparkSession? session, LogicalPlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Session = session;
    }

    /// <summary>The immutable, unresolved logical plan backing this DataFrame.</summary>
    internal LogicalPlan Plan { get; }

    /// <summary>
    /// The session that owns this frame, or <see langword="null"/> for a session-free frame. Actions
    /// (<see cref="Collect"/>/<see cref="Count"/>/<see cref="Show(int, bool)"/>) analyze and execute
    /// through it; transformations propagate it unchanged so a whole chain shares one session.
    /// </summary>
    internal SparkSession? Session { get; }

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
        return new DataFrame(Session, new Project(ToExprs(columns, nameof(columns)), Plan));
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
        return new DataFrame(Session, new Project(NamesToExprs(column, columns, nameof(columns)), Plan));
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
        return new DataFrame(Session, new Filter(condition.Expr, Plan));
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
        return new DataFrame(Session, new Project(projectList, Plan));
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
        return new RelationalGroupedDataset(Session, Plan, ToExprs(columns, nameof(columns)));
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
        return new RelationalGroupedDataset(Session, Plan, NamesToExprs(column, columns, nameof(columns)));
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
        new RelationalGroupedDataset(Session, Plan, Array.Empty<Expression>()).Agg(expr, exprs);

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
        return new DataFrame(Session ?? right.Session, new Join(Plan, right.Plan, JoinType.Inner));
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
        return new DataFrame(Session ?? right.Session, new Join(Plan, right.Plan, type, condition.Expr));
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

        return new DataFrame(Session ?? right.Session, new Join(Plan, right.Plan, type, usingColumns: columns));
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
        return new DataFrame(Session ?? right.Session, new Join(Plan, right.Plan, JoinType.Cross, condition: null));
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
    public DataFrame Limit(int n) => new(Session, new Limit(n, Plan));

    /// <summary>
    /// Returns a new <see cref="DataFrame"/> with duplicate rows removed, mirroring Spark's
    /// <c>Dataset.distinct()</c>. This is a lazy <b>transformation</b>: it records a <c>Distinct</c>
    /// node and deduplicates nothing here (ADR-0001). This instance is unchanged.
    /// </summary>
    /// <returns>A new <see cref="DataFrame"/> over distinct rows.</returns>
    public DataFrame Distinct() => new(Session, new Distinct(Plan));

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
        return new DataFrame(Session ?? other.Session, new Union(new[] { Plan, other.Plan }));
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

    // ------------------------------------------------------------------------------------------
    // Actions (eager). These are the ONLY DataFrame members that execute — they analyze the plan,
    // run the optimizer seam (an intentional identity pass in M1 — see Optimize), and drive the
    // session's IQueryExecutor. Building or chaining a transformation above never reaches here (the
    // lazy/eager invariant, ADR-0001), which the #169 audit seam makes observable: an action records
    // exactly one Analyzer stage; a transformation records none. See
    // docs/engineering/design/actions-and-row.md.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Executes the query and returns every result row as an in-memory list, mirroring Spark's
    /// <c>Dataset.collect()</c>. This is an <b>action</b>: it analyzes this frame's plan, runs the
    /// optimizer seam (an identity pass in M1), and drives the session's execution backend exactly
    /// once — the crossing from lazy plan construction into eager execution.
    /// </summary>
    /// <returns>The materialized <see cref="Row"/>s, in result order.</returns>
    /// <exception cref="InvalidOperationException">This frame is not bound to a <see cref="SparkSession"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    /// <exception cref="QueryExecutionException">No execution backend is registered, or the backend
    /// reported a runtime failure.</exception>
    public IReadOnlyList<Row> Collect()
    {
        SparkSession session = RequireSession(nameof(Collect));
        LogicalPlan analyzed = AnalyzeForExecution(session, Plan);
        return session.QueryExecutor.Collect(analyzed);
    }

    /// <summary>
    /// Executes the query and returns the number of rows in the result, mirroring Spark's
    /// <c>Dataset.count()</c>. Like <see cref="Collect"/> this is an <b>action</b>: it analyzes, runs
    /// the optimizer seam (an identity pass in M1), and drives the backend exactly once, without
    /// materializing the rows.
    /// </summary>
    /// <returns>The number of result rows.</returns>
    /// <exception cref="InvalidOperationException">This frame is not bound to a <see cref="SparkSession"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    /// <exception cref="QueryExecutionException">No execution backend is registered, or the backend
    /// reported a runtime failure.</exception>
    public long Count()
    {
        SparkSession session = RequireSession(nameof(Count));
        LogicalPlan analyzed = AnalyzeForExecution(session, Plan);
        return session.QueryExecutor.Count(analyzed);
    }

    /// <summary>
    /// Executes the query and prints the first <paramref name="numRows"/> rows to the console as a
    /// Spark-style bordered table, mirroring Spark's <c>Dataset.show(numRows, truncate)</c>. This is an
    /// <b>action</b>: it drives the backend to materialize (up to <paramref name="numRows"/> + 1) rows
    /// but leaves this frame's plan unchanged — the row bound is applied to a derived plan, not this one.
    /// </summary>
    /// <param name="numRows">The maximum number of rows to display (default 20).</param>
    /// <param name="truncate">When <see langword="true"/> (default) cells longer than 20 characters are
    /// truncated with a trailing <c>...</c>; when <see langword="false"/> full values are shown.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="numRows"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">This frame is not bound to a <see cref="SparkSession"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    /// <exception cref="QueryExecutionException">No execution backend is registered, or the backend
    /// reported a runtime failure.</exception>
    public void Show(int numRows = 20, bool truncate = true) =>
        Console.Out.Write(ShowString(numRows, truncate));

    /// <summary>
    /// Prints the physical plan of this query to the console, mirroring Spark's <c>Dataset.explain()</c>
    /// (the <see cref="ExplainMode.Simple"/> mode). This is <b>not</b> an action: it renders the plan
    /// without executing it (the physical plan is <i>planned</i>, not run — ADR-0001), so it is safe to
    /// call while debugging a query.
    /// </summary>
    /// <exception cref="InvalidOperationException">This frame is not bound to a <see cref="SparkSession"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    public void Explain() => Explain(ExplainMode.Simple);

    /// <summary>
    /// Prints this query's plan to the console, mirroring Spark's <c>Dataset.explain(extended)</c>. When
    /// <paramref name="extended"/> is <see langword="true"/> the parsed (unresolved), analyzed,
    /// optimized, and physical plans are printed in separate sections (<see cref="ExplainMode.Extended"/>);
    /// otherwise only the physical plan is printed (<see cref="ExplainMode.Simple"/>). Like
    /// <see cref="Explain()"/>, this renders without executing.
    /// </summary>
    /// <param name="extended">Whether to include the logical (parsed/analyzed/optimized) sections.</param>
    /// <exception cref="InvalidOperationException">This frame is not bound to a <see cref="SparkSession"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    public void Explain(bool extended) =>
        Explain(extended ? ExplainMode.Extended : ExplainMode.Simple);

    /// <summary>
    /// Prints this query's plan to the console in the given <paramref name="mode"/>, mirroring Spark's
    /// <c>Dataset.explain(mode)</c>. Accepted mode strings (case-insensitive) are <c>"simple"</c>,
    /// <c>"extended"</c>, <c>"codegen"</c>, <c>"cost"</c>, and <c>"formatted"</c>. Renders without
    /// executing.
    /// </summary>
    /// <param name="mode">The Spark explain-mode string.</param>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is not a recognised mode string.</exception>
    /// <exception cref="InvalidOperationException">This frame is not bound to a <see cref="SparkSession"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    public void Explain(string mode) => Explain(ParseMode(mode));

    /// <summary>
    /// Prints this query's plan to the console in the given <paramref name="mode"/>, the strongly-typed
    /// counterpart of <see cref="Explain(string)"/>. Renders without executing (ADR-0001): logical
    /// sections are produced entirely in <c>DeltaSharp.Core</c>, and the physical section is planned
    /// (through the execution seam) but never run. See <c>docs/engineering/design/explain.md</c>.
    /// </summary>
    /// <param name="mode">The explain mode.</param>
    /// <exception cref="InvalidOperationException">This frame is not bound to a <see cref="SparkSession"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    public void Explain(ExplainMode mode) => Console.Out.Write(ExplainString(mode));

    /// <summary>
    /// Builds the Spark-style table string that <see cref="Show(int, bool)"/> prints, returning it
    /// instead of writing to the console so formatting is unit-testable. It executes the query
    /// (bounded to <paramref name="numRows"/> + 1 rows to detect whether more rows exist) but does not
    /// mutate this frame's plan.
    /// </summary>
    /// <param name="numRows">The maximum number of rows to render.</param>
    /// <param name="truncate">Whether cells wider than 20 characters are truncated.</param>
    /// <returns>The rendered table, including a trailing "only showing top N rows" footer when the
    /// result has more than <paramref name="numRows"/> rows.</returns>
    internal string ShowString(int numRows, bool truncate)
    {
        if (numRows < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numRows), numRows, "The number of rows to show cannot be negative.");
        }

        int truncateWidth = truncate ? DefaultTruncateWidth : 0;

        // Collect numRows + 1 so we can tell whether the result has more rows than we display, without
        // materializing the whole result. The bound is pushed onto a DERIVED plan; this frame is
        // unchanged (AC3: show respects row limits without changing the underlying plan).
        SparkSession session = RequireSession(nameof(Show));
        long boundLong = (long)numRows + 1;
        int bound = boundLong > int.MaxValue ? int.MaxValue : (int)boundLong;
        LogicalPlan analyzed = AnalyzeForExecution(
            session,
            new Limit(bound, Plan),
            out IReadOnlyList<(string Name, DataType Type, bool Nullable)> outputColumns);
        IReadOnlyList<Row> collected = session.QueryExecutor.Collect(analyzed);

        bool hasMoreData = collected.Count > numRows;
        int displayCount = Math.Min(collected.Count, numRows);

        // The header is derived from the analyzed plan's ordered output columns (not collected[0].Schema)
        // so an EMPTY result still renders real column headers (Spark parity) instead of a degenerate
        // ++/++ box. The column list is duplicate-name tolerant (unlike a StructType), so a plan whose
        // output has repeated names — df.Join(other) or Select(Col("x"), Col("x")) — renders duplicate
        // headers the way Spark's show() does, and stays reachable where Collect/Count already were. The
        // dup-rejecting StructType/Row materialization policy is tracked by #419.
        return FormatTable(outputColumns, collected, displayCount, numRows, truncateWidth, hasMoreData);
    }

    /// <summary>The Spark-default column truncation width applied when <c>truncate</c> is true.</summary>
    private const int DefaultTruncateWidth = 20;

    /// <summary>The minimum rendered width of any column (Spark parity).</summary>
    private const int MinColumnWidth = 3;

    private SparkSession RequireSession(string action)
    {
        SparkSession session = Session ?? throw new InvalidOperationException(
            "This DataFrame is not bound to a SparkSession and cannot be executed. Obtain DataFrames "
            + "from a SparkSession (for example spark.Read/spark.Sql) so actions can analyze and run "
            + "them.");

        // An action must not run on a stopped/disposed session (Spark parity): route through the
        // single lifecycle guard so Collect/Count/Show after Stop() throw SessionStoppedException
        // instead of reaching the analyzer/executor.
        session.EnsureNotStopped(action);
        return session;
    }

    /// <summary>
    /// The eager analyze (and optimize) stage shared by <see cref="Collect"/>/<see cref="Count"/>: it
    /// resolves <paramref name="plan"/> against the session catalog (emitting the #169 audit's Analyzer
    /// stage) and then runs the optimizer seam, returning the plan the executor runs. It does not
    /// materialize the output schema — the collect/count contract needs only the executor's rows.
    /// </summary>
    private static LogicalPlan AnalyzeForExecution(SparkSession session, LogicalPlan plan) =>
        Optimize(new Analyzer(session.Catalog).Resolve(plan));

    /// <summary>
    /// The analyze + optimize stage for <see cref="Show(int, bool)"/>: like
    /// <see cref="AnalyzeForExecution(SparkSession, LogicalPlan)"/> but also captures the analyzed
    /// plan's ordered <paramref name="outputColumns"/> from the <b>single</b> Resolve call (so deriving
    /// them emits no extra Analyzer audit stage), which the header is rendered from — even when the
    /// result is empty or the output has duplicate column names (#419). Optimize is an intentional
    /// identity pass in M1 (see <see cref="Optimize"/>), so the analyzed output columns are also the
    /// optimized plan's output columns.
    /// </summary>
    private static LogicalPlan AnalyzeForExecution(
        SparkSession session,
        LogicalPlan plan,
        out IReadOnlyList<(string Name, DataType Type, bool Nullable)> outputColumns) =>
        Optimize(new Analyzer(session.Catalog).Resolve(plan, out outputColumns));

    /// <summary>
    /// The optimizer seam. The standalone rule-based <c>Optimizer</c> (STORY-04.5.3 / #172) is already
    /// merged, but this action-pipeline seam is <b>intentionally an identity pass in M1</b>: wiring the
    /// optimizer in is deferred to the #174 physical-planning bridge and is gated on
    /// <see href="https://github.com/khaines/deltasharp/issues/415">#415</see> — the engine currently
    /// evaluates <c>And</c>/<c>Or</c> eagerly with no per-lane short-circuit, so an optimizer-combined
    /// filter could raise ANSI errors on guard-excluded rows. Keeping the seam as an explicit, named
    /// step is the point: #174 can wire it without reshaping the action pipeline or the
    /// <see cref="IQueryExecutor"/> contract.
    /// </summary>
    private static LogicalPlan Optimize(LogicalPlan analyzedPlan) => analyzedPlan;

    // ------------------------------------------------------------------------------------------
    // EXPLAIN (STORY-04.7.3 / #179). Renders each pipeline stage WITHOUT executing: logical/analyzed/
    // optimized entirely in Core; the physical section is PLANNED (not run) through the IQueryExecutor
    // seam. Diagnostics are rendered as text, never thrown in place of a diagnostic line (AC4). See
    // docs/engineering/design/explain.md.
    // ------------------------------------------------------------------------------------------

    /// <summary>The header prefixing every EXPLAIN section (Spark parity), e.g. <c>== Physical Plan ==</c>.</summary>
    private static string SectionHeader(string title) => $"== {title} ==";

    /// <summary>
    /// Builds the console text that <see cref="Explain(ExplainMode)"/> prints, returning it instead of
    /// writing to the console so the rendered plan is unit-testable (the same pattern as
    /// <see cref="ShowString(int, bool)"/>). It renders without executing (ADR-0001).
    /// </summary>
    /// <param name="mode">The explain mode.</param>
    /// <returns>The rendered, newline-terminated plan text.</returns>
    internal string ExplainString(ExplainMode mode)
    {
        SparkSession session = RequireSession(nameof(Explain));
        var builder = new StringBuilder();

        bool includeLogical = mode is ExplainMode.Extended or ExplainMode.Cost;
        if (includeLogical)
        {
            AppendSection(builder, "Parsed Logical Plan", Plan.TreeString());

            // Analyze + optimize on the SAME seam the actions use (AnalyzeForExecution/Optimize), so the
            // rendered plans reflect what the executor will actually plan. A resolution failure is a
            // diagnostic line, not an escaping exception (AC4) — the Parsed section above still shows the
            // offending unresolved plan, so the diagnostic is not hidden.
            if (TryAnalyze(session, out LogicalPlan? analyzed, out string? analysisDiagnostic))
            {
                AppendSection(builder, "Analyzed Logical Plan", analyzed!.TreeString());
                AppendSection(builder, "Optimized Logical Plan", Optimize(analyzed!).TreeString());
            }
            else
            {
                AppendSection(builder, "Analyzed Logical Plan", analysisDiagnostic!);
                AppendSection(builder, "Optimized Logical Plan", analysisDiagnostic!);
            }
        }

        AppendSection(builder, "Physical Plan", PhysicalPlanText(session));

        AppendModeNote(builder, mode);
        return builder.ToString();
    }

    /// <summary>
    /// Renders the physical-plan section by planning (not executing) through the session's execution
    /// seam. When the plan cannot be analyzed, the physical section carries the analysis diagnostic; the
    /// seam itself is contractually non-throwing (unsupported operators / no-backend become diagnostics).
    /// </summary>
    private string PhysicalPlanText(SparkSession session) =>
        TryAnalyze(session, out LogicalPlan? analyzed, out string? diagnostic)
            ? session.QueryExecutor.ExplainPhysical(Optimize(analyzed!))
            : diagnostic!;

    /// <summary>
    /// Resolves this frame's plan against the session catalog, capturing an <see cref="AnalysisException"/>
    /// as a diagnostic string instead of letting it escape (AC4). Other exception types are genuine
    /// faults and are not swallowed.
    /// </summary>
    private bool TryAnalyze(SparkSession session, out LogicalPlan? analyzed, out string? diagnostic)
    {
        try
        {
            analyzed = new Analyzer(session.Catalog).Resolve(Plan);
            diagnostic = null;
            return true;
        }
        catch (AnalysisException ex)
        {
            analyzed = null;
            diagnostic = $"<cannot analyze plan: {ex.Message}>";
            return false;
        }
    }

    /// <summary>Appends a titled section (header, body, trailing blank line) to <paramref name="builder"/>.</summary>
    private static void AppendSection(StringBuilder builder, string title, string body)
    {
        builder.Append(SectionHeader(title)).Append('\n');
        builder.Append(body);
        if (body.Length > 0 && body[^1] != '\n')
        {
            builder.Append('\n');
        }

        builder.Append('\n');
    }

    /// <summary>Appends the mode-specific diagnostic footer for modes M1 does not fully realize (AC4).</summary>
    private static void AppendModeNote(StringBuilder builder, ExplainMode mode)
    {
        string? note = mode switch
        {
            ExplainMode.Codegen =>
                "<codegen mode: whole-stage codegen is not part of the M1 interpreted backend (ADR-0001)>",
            ExplainMode.Cost =>
                "<cost mode: plan cost statistics are not collected in M1>",
            ExplainMode.Formatted =>
                "<formatted mode: per-node detail sections arrive with execution metrics (#176)>",
            _ => null,
        };

        if (note is not null)
        {
            builder.Append(note).Append('\n');
        }
    }

    /// <summary>Parses a Spark explain-mode string (case-insensitive) into an <see cref="ExplainMode"/>.</summary>
    /// <exception cref="ArgumentException">The string is not a recognised mode (Spark parity).</exception>
    private static ExplainMode ParseMode(string mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return mode.Trim().ToLowerInvariant() switch
        {
            "simple" => ExplainMode.Simple,
            "extended" => ExplainMode.Extended,
            "codegen" => ExplainMode.Codegen,
            "cost" => ExplainMode.Cost,
            "formatted" => ExplainMode.Formatted,
            _ => throw new ArgumentException(
                $"Unsupported EXPLAIN mode '{mode}'. Supported modes are 'simple', 'extended', "
                + "'codegen', 'cost', and 'formatted'.",
                nameof(mode)),
        };
    }

    /// <summary>
    /// Renders <paramref name="rows"/> (up to <paramref name="displayCount"/>) as a Spark-style
    /// bordered table with a header derived from <paramref name="columns"/>. The column list is
    /// duplicate-name tolerant (Spark renders duplicate headers; the dup-rejecting <c>StructType</c>/
    /// <c>Row</c> materialization policy is tracked by #419). Cells are right-justified when truncating
    /// (Spark parity), null is shown as <c>null</c>, and a footer is appended when
    /// <paramref name="hasMoreData"/> is set.
    /// </summary>
    private static string FormatTable(
        IReadOnlyList<(string Name, DataType Type, bool Nullable)> columns,
        IReadOnlyList<Row> rows,
        int displayCount,
        int numRows,
        int truncateWidth,
        bool hasMoreData)
    {
        int numCols = columns.Count;
        if (numCols == 0)
        {
            // An empty schema (no columns) still renders a stable, closed box.
            var emptyBuilder = new StringBuilder("++\n++\n");
            if (hasMoreData)
            {
                AppendFooter(emptyBuilder, numRows);
            }

            return emptyBuilder.ToString();
        }

        var cells = new List<string[]>(displayCount + 1);
        var header = new string[numCols];
        for (int c = 0; c < numCols; c++)
        {
            header[c] = columns[c].Name;
        }

        cells.Add(header);

        for (int r = 0; r < displayCount; r++)
        {
            Row row = rows[r];
            var line = new string[numCols];
            for (int c = 0; c < numCols; c++)
            {
                line[c] = TruncateCell(Row.Render(row[c]), truncateWidth);
            }

            cells.Add(line);
        }

        var widths = new int[numCols];
        for (int c = 0; c < numCols; c++)
        {
            widths[c] = MinColumnWidth;
        }

        foreach (string[] line in cells)
        {
            for (int c = 0; c < numCols; c++)
            {
                widths[c] = Math.Max(widths[c], line[c].Length);
            }
        }

        var builder = new StringBuilder();
        string separator = BuildSeparator(widths);
        builder.Append(separator);
        AppendRow(builder, cells[0], widths, truncateWidth);
        builder.Append(separator);
        for (int i = 1; i < cells.Count; i++)
        {
            AppendRow(builder, cells[i], widths, truncateWidth);
        }

        builder.Append(separator);
        if (hasMoreData)
        {
            AppendFooter(builder, numRows);
        }

        return builder.ToString();
    }

    private static string TruncateCell(string cell, int truncateWidth)
    {
        if (truncateWidth <= 0 || cell.Length <= truncateWidth)
        {
            return cell;
        }

        return truncateWidth < 4
            ? cell.Substring(0, truncateWidth)
            : cell.Substring(0, truncateWidth - 3) + "...";
    }

    private static string BuildSeparator(int[] widths)
    {
        var builder = new StringBuilder("+");
        foreach (int width in widths)
        {
            builder.Append('-', width).Append('+');
        }

        return builder.Append('\n').ToString();
    }

    private static void AppendRow(StringBuilder builder, string[] cells, int[] widths, int truncateWidth)
    {
        builder.Append('|');
        for (int c = 0; c < cells.Length; c++)
        {
            string padded = truncateWidth > 0
                ? cells[c].PadLeft(widths[c])
                : cells[c].PadRight(widths[c]);
            builder.Append(padded).Append('|');
        }

        builder.Append('\n');
    }

    private static void AppendFooter(StringBuilder builder, int numRows) =>
        builder.Append(
            CultureInfo.InvariantCulture,
            $"only showing top {numRows} {(numRows == 1 ? "row" : "rows")}\n");

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

        return new DataFrame(Session, new Sort(order, global: true, Plan));
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

        return new DataFrame(Session, new Sort(order, global: true, Plan));
    }

    /// <summary>Wraps a bare expression as an ascending (nulls-first) ordering term, but passes an
    /// expression that is already a <see cref="SortOrder"/> (from <see cref="Column.Asc"/>/
    /// <see cref="Column.Desc"/>) through unchanged, matching Spark's <c>orderBy</c> defaulting.</summary>
    private static Expression ToSortOrder(Expression expr) =>
        expr is SortOrder
            ? expr
            : new SortOrder(expr, SortDirection.Ascending, NullOrdering.NullsFirst);
}
