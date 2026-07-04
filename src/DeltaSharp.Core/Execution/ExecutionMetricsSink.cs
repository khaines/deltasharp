namespace DeltaSharp.Execution;

/// <summary>
/// A per-action mutable holder the executor writes the run's <see cref="ExecutionMetrics"/> into before
/// it returns or throws (STORY-04.6.4 / #176, criterion 4). A <b>fresh</b> sink is allocated per action
/// by the <c>out</c>-metrics overloads and threaded across the <see cref="IQueryExecutor"/> seam, so it
/// carries no cross-action state and never mutates the process-wide <see cref="ExecutionOptions.Default"/>
/// singleton — two concurrent actions cannot race on each other's metrics.
/// </summary>
/// <remarks>
/// The executor fills <see cref="Metrics"/> in a <c>finally</c>, so it holds the partial counters on the
/// cancel/timeout/failure paths as well as on success; the <c>out</c>-metrics overloads read it in their
/// own <c>finally</c> so they surface metrics even when the action throws. A <see langword="null"/>
/// <see cref="Metrics"/> means the executor never published any (for example an action that fails before
/// the driver runs); callers substitute <see cref="ExecutionMetrics.Empty"/>.
/// </remarks>
internal sealed class ExecutionMetricsSink
{
    /// <summary>The metrics the executor published for this action, or <see langword="null"/> if none.</summary>
    public ExecutionMetrics? Metrics { get; set; }
}
