using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp;

/// <summary>
/// The intermediate handle produced by <see cref="DataFrame.GroupBy(Column[])"/> — a set of
/// grouping key expressions plus the source plan they group — mirroring Apache Spark's
/// <c>RelationalGroupedDataset</c>. It is <b>not</b> a <see cref="DataFrame"/>: a grouping on its own
/// is not a scannable relation (there is nothing to <c>Collect</c>/<c>Show</c> until an aggregation
/// is chosen), so it exposes no action surface. Instead it records the grouping expressions and
/// exposes aggregation doors — <see cref="Agg(Column, Column[])"/> and <see cref="Count"/> — each of
/// which produces a new <see cref="DataFrame"/> wrapping an <c>Aggregate</c> logical plan.
/// </summary>
/// <remarks>
/// <para>
/// STORY-04.2.2 (#161) introduces this type as the middle of Spark's <c>groupBy(...).agg(...)</c>
/// chain. Like every transformation on <see cref="DataFrame"/>, constructing it and calling its
/// aggregation doors only <b>extends</b> the immutable logical plan — no schema is consulted, no
/// aggregate is computed, and no scan or backend call happens (the lazy invariant, ADR-0001). The
/// grouping expressions are recorded by reference; the source <see cref="DataFrame"/> is unchanged
/// and its plan subtree is shared structurally (#167).
/// </para>
/// <para>
/// Aggregate <b>input</b> validation (for example rejecting a non-aggregate expression in aggregate
/// position, or an aggregate outside an aggregate context) is the analyzer's job and lands with
/// aggregate type coercion in STORY-04.5.2 (<see href="https://github.com/khaines/deltasharp/issues/171">#171</see>).
/// These doors only build the well-formed unresolved <c>Aggregate</c>; they never coerce or execute.
/// Instances are created by <see cref="DataFrame.GroupBy(Column[])"/>, not by user code, so the
/// constructor is non-public. See <c>docs/engineering/design/aggregation-api.md</c>.
/// </para>
/// <para>
/// <b>Deferred Spark surface.</b> Apache Spark's <c>RelationalGroupedDataset</c> also exposes typed
/// aggregate shortcuts (<c>sum</c>/<c>avg</c>/<c>mean</c>/<c>min</c>/<c>max</c> over named columns),
/// <c>pivot</c>, and the string-map/tuple forms (<c>agg(Map&lt;string,string&gt;)</c> /
/// <c>agg((string, string)*)</c>). DeltaSharp ships only <see cref="Agg(Column, Column[])"/> and
/// <see cref="Count"/> in M1; the general <c>Agg(Functions.Sum(...).As("total"))</c> door is the
/// workaround for every typed shortcut. Those members — and the matching global
/// <c>DataFrame.Agg(Map/tuple)</c> forms — are tracked as a parity backlog in
/// <see href="https://github.com/khaines/deltasharp/issues/406">#406</see>. Note that a <b>real</b>
/// aggregate needs aggregate-function resolution + Spark auto-naming
/// (<see href="https://github.com/khaines/deltasharp/issues/171">#171</see>) <i>and</i> the action
/// surface (<c>Collect</c>/<c>Show</c>/<c>count</c>) to analyze and execute end-to-end — not just
/// naming.
/// </para>
/// </remarks>
public sealed class RelationalGroupedDataset
{
    private readonly LogicalPlan _plan;
    private readonly IReadOnlyList<Expression> _groupingExpressions;

    /// <summary>Records <paramref name="groupingExpressions"/> over <paramref name="plan"/> without
    /// evaluating either.</summary>
    internal RelationalGroupedDataset(LogicalPlan plan, IReadOnlyList<Expression> groupingExpressions)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _groupingExpressions =
            groupingExpressions ?? throw new ArgumentNullException(nameof(groupingExpressions));
    }

    /// <summary>The source plan being grouped (for tests and internal use).</summary>
    internal LogicalPlan Plan => _plan;

    /// <summary>The recorded grouping key expressions, in order (for tests and internal use).</summary>
    internal IReadOnlyList<Expression> GroupingExpressions => _groupingExpressions;

    /// <summary>
    /// Computes one or more aggregate expressions per group, returning a new <see cref="DataFrame"/>
    /// whose plan is an <c>Aggregate</c> over the grouped frame's plan, mirroring Spark's
    /// <c>RelationalGroupedDataset.agg(expr: Column, exprs: Column*)</c>. This is a
    /// <b>transformation</b>: it only extends the immutable logical plan — nothing is evaluated and
    /// no scan or backend call happens (ADR-0001).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result plan is <c>Aggregate(groupingExprs, retainedGroupingExprs ⧺ aggExprs, plan)</c>.
    /// Following Spark's default <c>spark.sql.retainGroupColumns = true</c>, the grouping key
    /// expressions are <b>retained</b> at the front of the aggregate output, so the resolved output
    /// is <i>grouping columns ⧺ aggregate results</i> (for example <c>groupBy("k").agg(sum("v"))</c>
    /// yields <c>[k, sum(v)]</c>). This retention also feeds the analyzer's structural
    /// <c>Aggregate</c> output derivation (grouping attributes followed by aggregate aliases).
    /// </para>
    /// <para>
    /// <b>Output naming.</b> If an aggregate <see cref="Column"/> is aliased (via
    /// <see cref="Column.As(string)"/>) the alias is honored verbatim. A bare aggregate (for example
    /// <see cref="Functions.Sum(Column)"/>) is left unaliased; Spark's auto-name (<c>sum(v)</c>) is
    /// assigned by the analyzer when aggregate-function resolution and pretty-naming land with
    /// STORY-04.5.2 (<see href="https://github.com/khaines/deltasharp/issues/171">#171</see>). These
    /// doors never compute the name eagerly.
    /// </para>
    /// <para>
    /// At least one aggregate expression is required (Spark's <c>agg</c> takes a required first
    /// <see cref="Column"/>), so a zero-aggregate call is not expressible.
    /// </para>
    /// </remarks>
    /// <param name="expr">The first aggregate expression (required).</param>
    /// <param name="exprs">Any further aggregate expressions, in output order.</param>
    /// <returns>A new <see cref="DataFrame"/> wrapping the grouped aggregation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expr"/>, <paramref name="exprs"/>, or
    /// any element of <paramref name="exprs"/> is null.</exception>
    public DataFrame Agg(Column expr, params Column[] exprs)
    {
        ArgumentNullException.ThrowIfNull(expr);
        ArgumentNullException.ThrowIfNull(exprs);

        Expression[] tail = DataFrame.ToExprs(exprs, nameof(exprs));
        var aggregateExpressions =
            new Expression[_groupingExpressions.Count + 1 + tail.Length];
        int next = 0;

        // Spark's retainGroupColumns=true: grouping keys lead the aggregate output.
        foreach (Expression grouping in _groupingExpressions)
        {
            aggregateExpressions[next++] = grouping;
        }

        aggregateExpressions[next++] = expr.Expr;
        foreach (Expression tailExpr in tail)
        {
            aggregateExpressions[next++] = tailExpr;
        }

        return new DataFrame(new Aggregate(_groupingExpressions, aggregateExpressions, _plan));
    }

    /// <summary>
    /// Counts the number of rows in each group, returning a new <see cref="DataFrame"/> whose plan is
    /// an <c>Aggregate</c> exposing the grouping columns and a <c>count</c> column named
    /// <c>"count"</c>, mirroring Spark's <c>RelationalGroupedDataset.count()</c>. Like
    /// <see cref="Agg(Column, Column[])"/> this is a lazy transformation that evaluates nothing.
    /// </summary>
    /// <remarks>Realized as <c>Agg(Count(Lit(1L)).As("count"))</c> — Spark counts rows via
    /// <c>count(1)</c> and names the output <c>count</c>.</remarks>
    /// <returns>A new <see cref="DataFrame"/> with the per-group row counts.</returns>
    public DataFrame Count() => Agg(Functions.Count(Functions.Lit(1L)).As("count"));
}
