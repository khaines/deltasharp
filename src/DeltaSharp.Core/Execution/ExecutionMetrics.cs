using System;
using System.Globalization;

namespace DeltaSharp;

/// <summary>
/// Planning and execution counters for a single local <see cref="DataFrame"/> action
/// (STORY-04.6.4 / #176, criterion 4). It is an immutable diagnostics snapshot retrievable after an
/// action <b>completes</b> (the <c>out</c>-parameter <see cref="DataFrame.Collect(out ExecutionMetrics, System.Threading.CancellationToken)"/>
/// overloads) or <b>fails</b> (<see cref="QueryExecutionException.Metrics"/>).
/// </summary>
/// <remarks>
/// <para>
/// Durations come from a monotonic timer (never the banned wall clock), so they are comparable but not
/// absolute timestamps. <see cref="OutputRows"/>/<see cref="OutputBatches"/> are computed at the driver
/// from the final result, so they are populated for every plan shape; <see cref="BytesScanned"/> and
/// <see cref="PeakMemoryBytes"/> aggregate the per-operator engine metrics and are 0 when no engine
/// operator ran (for example a bare in-memory scan).
/// </para>
/// <para>
/// This is the retrievable seam sibling lane #179 (EXPLAIN) consumes to display physical-execution
/// metadata. See <c>docs/engineering/design/execution-boundaries.md</c> §5.
/// </para>
/// </remarks>
public sealed class ExecutionMetrics
{
    /// <summary>Metrics with all counters zero — the value reported when no work was measured.</summary>
    public static ExecutionMetrics Empty { get; } =
        new(TimeSpan.Zero, TimeSpan.Zero, outputRows: 0, outputBatches: 0, bytesScanned: 0, peakMemoryBytes: 0);

    /// <summary>Creates an immutable metrics snapshot.</summary>
    /// <param name="planningDuration">Time spent in physical planning.</param>
    /// <param name="executionDuration">Time spent executing and materializing the plan.</param>
    /// <param name="outputRows">Rows the action produced.</param>
    /// <param name="outputBatches">Column batches the action produced.</param>
    /// <param name="bytesScanned">Estimated data-plane bytes read by scans.</param>
    /// <param name="peakMemoryBytes">High-water reserved execution memory across operators.</param>
    public ExecutionMetrics(
        TimeSpan planningDuration,
        TimeSpan executionDuration,
        long outputRows,
        long outputBatches,
        long bytesScanned,
        long peakMemoryBytes)
    {
        PlanningDuration = planningDuration;
        ExecutionDuration = executionDuration;
        OutputRows = outputRows;
        OutputBatches = outputBatches;
        BytesScanned = bytesScanned;
        PeakMemoryBytes = peakMemoryBytes;
    }

    /// <summary>Wall-to-wall time spent in physical planning (<c>PhysicalPlanner.Plan</c>).</summary>
    public TimeSpan PlanningDuration { get; }

    /// <summary>Wall-to-wall time spent executing and materializing the plan.</summary>
    public TimeSpan ExecutionDuration { get; }

    /// <summary>Total time, <see cref="PlanningDuration"/> + <see cref="ExecutionDuration"/>.</summary>
    public TimeSpan TotalDuration => PlanningDuration + ExecutionDuration;

    /// <summary>The number of rows the action produced.</summary>
    public long OutputRows { get; }

    /// <summary>The number of column batches the action produced.</summary>
    public long OutputBatches { get; }

    /// <summary>Estimated data-plane bytes read by scans (summed across operators).</summary>
    public long BytesScanned { get; }

    /// <summary>High-water reserved execution memory in bytes (max across operators).</summary>
    public long PeakMemoryBytes { get; }

    /// <summary>A compact single-line rendering for logs and diagnostics.</summary>
    /// <returns>A diagnostic string naming each counter.</returns>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "ExecutionMetrics(planning={0:g}, execution={1:g}, rows={2}, batches={3}, bytesScanned={4}, peakMemoryBytes={5})",
        PlanningDuration,
        ExecutionDuration,
        OutputRows,
        OutputBatches,
        BytesScanned,
        PeakMemoryBytes);
}
