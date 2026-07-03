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

    private static QueryExecutionException NotRegistered() =>
        new("No execution backend is registered, so this DataFrame action cannot run. "
            + "Reference the DeltaSharp.Executor package to enable query execution "
            + "(collect/count/show); it ships in STORY-04.6.2 (#174).");
}
