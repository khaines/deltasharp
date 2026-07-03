using System.Collections.Generic;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Execution;

/// <summary>
/// The default <see cref="IQueryExecutor"/> a <see cref="SparkSession"/> holds until the executor lane
/// (STORY-04.6.2 / #174) registers a real backend. Every method throws a deterministic
/// <see cref="QueryExecutionException"/> pointing at the missing <c>DeltaSharp.Executor</c> reference,
/// so <c>DeltaSharp.Core</c> is self-contained and testable — an action fails fast with a clear
/// diagnostic rather than silently returning empty results.
/// </summary>
internal sealed class UnsupportedQueryExecutor : IQueryExecutor
{
    /// <summary>The shared stateless instance.</summary>
    internal static readonly UnsupportedQueryExecutor Instance = new();

    private UnsupportedQueryExecutor()
    {
    }

    /// <inheritdoc/>
    public IReadOnlyList<Row> Collect(LogicalPlan analyzedPlan) => throw NotRegistered();

    /// <inheritdoc/>
    public long Count(LogicalPlan analyzedPlan) => throw NotRegistered();

    /// <inheritdoc/>
    /// <remarks>
    /// Unlike <see cref="Collect"/>/<see cref="Count"/> this does <b>not</b> throw: physical-mode EXPLAIN
    /// degrades gracefully to a diagnostic line when no backend is registered (STORY-04.7.3 AC4), so a
    /// caller can still see the logical plans even without <c>DeltaSharp.Executor</c>.
    /// </remarks>
    public string ExplainPhysical(LogicalPlan analyzedPlan) =>
        "<no execution backend is registered; reference the DeltaSharp.Executor package to render the "
        + "physical plan (STORY-04.6.2 / #174)>";

    private static QueryExecutionException NotRegistered() =>
        new("No execution backend is registered, so this DataFrame action cannot run. Reference the "
            + "DeltaSharp.Executor assembly and enable execution by calling "
            + "DeltaSharp.Executor.DeltaSharpExecutor.Enable() once at startup (a program that reaches an "
            + "action through only DeltaSharp.Core types does not auto-initialize the backend). The "
            + "backend also self-registers the first time any DeltaSharp.Executor type is used.");
}
