namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The mutable per-operator metrics surface every <see cref="PhysicalOperator"/> exposes
/// (STORY-03.1.1 AC2). A running operator accumulates counters here; SRE, performance, and FinOps
/// seats read <see cref="Snapshot()"/> after execution. Counters are <b>not</b> thread-safe —
/// each operator owns its instance — and timing is supplied by the executing backend (no clock is
/// read here, keeping the surface deterministic and avoiding banned wall-clock APIs).
/// </summary>
public sealed class OperatorMetrics
{
    /// <summary>Rows read from input streams (sum across children).</summary>
    public long InputRows { get; private set; }

    /// <summary>Rows emitted downstream after this operator's logic.</summary>
    public long OutputRows { get; private set; }

    /// <summary>Logical rows kept by a selection vector (e.g. a filter's passing rows).</summary>
    public long SelectedRows { get; private set; }

    /// <summary>Batches emitted downstream.</summary>
    public long OutputBatches { get; private set; }

    /// <summary>Bytes read from the data plane (scan) — the unit of tenant scan-byte accounting.</summary>
    public long BytesScanned { get; private set; }

    /// <summary>Bytes written to a local exchange/shuffle boundary.</summary>
    public long ShuffleBytes { get; private set; }

    /// <summary>
    /// Bytes spilled to disk under memory pressure. v1 has no spill — every blocking operator that
    /// cannot reserve fails fast with <see cref="ExecutionMemoryException"/> — so this stays 0; it is
    /// the wired-but-dormant surface that STORY-03.6.2 (spill) will populate.
    /// </summary>
    public long SpilledBytes { get; private set; }

    /// <summary>Peak memory reserved through the <see cref="IExecutionMemory"/> context (high-water mark).</summary>
    public long PeakMemoryBytes { get; private set; }

    /// <summary>
    /// Bytes the operator currently holds reserved against the <see cref="IExecutionMemory"/> budget.
    /// Rises on each reservation and falls on each release, reaching 0 once the operator is disposed
    /// (every reservation released exactly once — STORY-03.6.1 AC2).
    /// </summary>
    public long CurrentReservedBytes { get; private set; }

    /// <summary>
    /// The number of successful reservation events the operator made against the budget — its
    /// allocation count for the reserved data plane (new groups, buffered rows, output chunks, …).
    /// </summary>
    public long AllocationCount { get; private set; }

    /// <summary>Monotonic elapsed execution time accumulated by the backend, in nanoseconds (from a monotonic source such as <c>Stopwatch.GetTimestamp</c>, never wall-clock <c>UtcNow</c>).</summary>
    public long ElapsedNanos { get; private set; }

    /// <summary>Records input rows.</summary>
    public void AddInputRows(long count) => InputRows += count;

    /// <summary>Records emitted rows and one batch.</summary>
    public void AddOutput(long rows)
    {
        OutputRows += rows;
        OutputBatches++;
    }

    /// <summary>Records rows retained by a selection.</summary>
    public void AddSelectedRows(long count) => SelectedRows += count;

    /// <summary>Records scanned bytes.</summary>
    public void AddBytesScanned(long bytes) => BytesScanned += bytes;

    /// <summary>Records shuffle bytes for a local exchange.</summary>
    public void AddShuffleBytes(long bytes) => ShuffleBytes += bytes;

    /// <summary>Records spilled bytes.</summary>
    public void AddSpilledBytes(long bytes) => SpilledBytes += bytes;

    /// <summary>Raises the peak memory high-water mark.</summary>
    public void ObservePeakMemory(long bytes)
    {
        if (bytes > PeakMemoryBytes)
        {
            PeakMemoryBytes = bytes;
        }
    }

    /// <summary>
    /// Records one successful reservation event: bumps <see cref="AllocationCount"/>, sets
    /// <see cref="CurrentReservedBytes"/> to the operator's new live total, and rolls the
    /// <see cref="PeakMemoryBytes"/> high-water mark. Operators call this after every successful
    /// <see cref="IExecutionMemory.TryReserve"/> so peak, current, and allocation count stay coherent.
    /// </summary>
    /// <param name="currentReservedBytes">The operator's total live reservation after this reserve.</param>
    public void ObserveReservation(long currentReservedBytes)
    {
        AllocationCount++;
        CurrentReservedBytes = currentReservedBytes;
        ObservePeakMemory(currentReservedBytes);
    }

    /// <summary>
    /// Sets the operator's live <see cref="CurrentReservedBytes"/> total. Called on every release to
    /// lower it, and to refresh it after an out-of-band reservation (e.g. the aggregate's MIN/MAX
    /// running best, which charges the budget inside the aggregator). <see cref="PeakMemoryBytes"/> is
    /// a high-water mark and is left unchanged.
    /// </summary>
    /// <param name="currentReservedBytes">The operator's total live reservation after the change.</param>
    public void ObserveRelease(long currentReservedBytes) => CurrentReservedBytes = currentReservedBytes;

    /// <summary>Adds elapsed nanoseconds measured by the backend.</summary>
    public void AddElapsedNanos(long nanos) => ElapsedNanos += nanos;

    /// <summary>An immutable point-in-time copy for downstream consumers.</summary>
    public OperatorMetricsSnapshot Snapshot() => new(
        InputRows, OutputRows, SelectedRows, OutputBatches, BytesScanned, ShuffleBytes,
        SpilledBytes, PeakMemoryBytes, ElapsedNanos, CurrentReservedBytes, AllocationCount);
}

/// <summary>An immutable snapshot of <see cref="OperatorMetrics"/> for SRE/perf/FinOps consumers.</summary>
/// <param name="InputRows">Rows read from inputs.</param>
/// <param name="OutputRows">Rows emitted.</param>
/// <param name="SelectedRows">Rows kept by selection.</param>
/// <param name="OutputBatches">Batches emitted.</param>
/// <param name="BytesScanned">Bytes read from the data plane.</param>
/// <param name="ShuffleBytes">Bytes written to a local exchange.</param>
/// <param name="SpilledBytes">Bytes spilled under pressure (0 until STORY-03.6.2 lands spill).</param>
/// <param name="PeakMemoryBytes">Peak reserved memory (high-water mark).</param>
/// <param name="ElapsedNanos">Accumulated execution time.</param>
/// <param name="CurrentReservedBytes">Bytes currently held reserved (0 once disposed).</param>
/// <param name="AllocationCount">Number of reservation events against the budget.</param>
public readonly record struct OperatorMetricsSnapshot(
    long InputRows,
    long OutputRows,
    long SelectedRows,
    long OutputBatches,
    long BytesScanned,
    long ShuffleBytes,
    long SpilledBytes,
    long PeakMemoryBytes,
    long ElapsedNanos,
    long CurrentReservedBytes,
    long AllocationCount);
