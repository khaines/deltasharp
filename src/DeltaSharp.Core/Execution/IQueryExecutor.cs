using System.Collections.Generic;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Execution;

/// <summary>
/// The dependency-inversion seam through which a <see cref="DataFrame"/> action drives execution
/// without <c>DeltaSharp.Core</c> depending on the engine. Core builds and analyzes logical plans but
/// cannot execute them: the vectorized backend lives in <c>DeltaSharp.Engine</c>/<c>DeltaSharp.Executor</c>
/// (both <c>net10.0</c>-only), while Core is a packable <c>net8.0;net10.0</c> public library that may
/// reference only <c>DeltaSharp.Abstractions</c>. Core therefore defines this <b>internal</b> contract
/// and the executor lane (STORY-04.6.2 / #174) implements it in <c>DeltaSharp.Executor</c> — reachable
/// because Core grants <c>InternalsVisibleTo("DeltaSharp.Executor")</c>.
/// </summary>
/// <remarks>
/// <para>
/// The interface is <see langword="internal"/> because it references the internal
/// <see cref="LogicalPlan"/> IR; it can never be public. An implementation receives the
/// analyzer-resolved plan (the <c>Optimize</c> seam sits before this call but is an <b>intentional
/// identity pass in M1</b> — the standalone STORY-04.5.3 / #172 optimizer is not wired into the action
/// pipeline; that is deferred to #174 and gated on
/// <see href="https://github.com/khaines/deltasharp/issues/415">#415</see>) and is the sole owner of
/// physical planning, the EPIC-03 backend invocation, and <see cref="Row"/> materialization from the
/// engine's <c>ColumnBatch</c> results. It is the <b>only</b> point where a <see cref="DataFrame"/>
/// action crosses from lazy plan construction into eager execution.
/// </para>
/// <para>
/// Until the executor lane registers a real implementation, <see cref="SparkSession"/> holds
/// <see cref="UnsupportedQueryExecutor"/>, which throws a deterministic <see cref="QueryExecutionException"/>
/// so Core stays self-contained and testable.
/// </para>
/// <para>
/// <b>Deferred (M1):</b> a seam <c>CancellationToken</c> and result/resource bounds are tracked by
/// <see href="https://github.com/khaines/deltasharp/issues/416">#416</see>; per-<see cref="DataFrame"/>
/// analyzed-plan memoization (each action re-analyzes today) by
/// <see href="https://github.com/khaines/deltasharp/issues/417">#417</see>.
/// </para>
/// </remarks>
internal interface IQueryExecutor
{
    /// <summary>
    /// Executes <paramref name="analyzedPlan"/> and materializes every result row (Spark's
    /// <c>collect</c>). This is an <b>eager</b> operation: it is the action's crossing into execution.
    /// All returned rows share the analyzed plan's output schema.
    /// </summary>
    /// <param name="analyzedPlan">The analyzer-resolved logical plan to execute.</param>
    /// <returns>The materialized rows, in result order; every row carries the analyzed plan's output schema.</returns>
    IReadOnlyList<Row> Collect(LogicalPlan analyzedPlan);

    /// <summary>
    /// Executes <paramref name="analyzedPlan"/> and returns the number of result rows (Spark's
    /// <c>count</c>) without materializing them.
    /// </summary>
    /// <param name="analyzedPlan">The analyzer-resolved logical plan to execute.</param>
    /// <returns>The row count.</returns>
    long Count(LogicalPlan analyzedPlan);
}
