using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Types;
using ExecutionContext = DeltaSharp.Engine.Execution.ExecutionContext;

namespace DeltaSharp.Executor;

/// <summary>
/// The materialized output of executing a <see cref="PhysicalPlan"/> subtree: the output
/// <see cref="StructType"/> plus the ordered <see cref="ColumnBatch"/>es it produced. STORY-04.6.2's
/// M1 execution model materializes at every operator boundary (each node fully drains its child into
/// a batch list before the next operator opens over it); intra-subtree streaming/fusion is a future
/// optimization documented in <c>docs/engineering/design/physical-planning.md</c>.
/// </summary>
/// <param name="Schema">The output schema (field names/types) the batches conform to.</param>
/// <param name="Batches">The output batches in order; each conforms to <paramref name="Schema"/>.</param>
internal readonly record struct BatchResult(StructType Schema, IReadOnlyList<ColumnBatch> Batches);

/// <summary>
/// The per-execution context a <see cref="PhysicalPlan"/> tree is driven with: the selected EPIC-03
/// <see cref="IExecutionBackend"/> (ADR-0001 — interpreted vectorized by default), the backend
/// options, and the run's cancellation token. It owns a <b>single</b> Engine
/// <see cref="ExecutionContext"/> shared across the whole operator tree (matching that type's
/// "immutable and shared across an operator tree" contract) and is <see cref="IDisposable"/> so the
/// executor releases the run's spill store / memory context deterministically on every path —
/// success, cancellation, timeout, and failure alike (STORY-04.6.4 / #176, discharging
/// <see href="https://github.com/khaines/deltasharp/issues/420">#420</see>).
/// </summary>
internal sealed class PhysicalRuntime : IDisposable
{
    private readonly IExecutionBackend _backend;
    private readonly ExecutionContext _context;
    private readonly CancellationToken _cancellationToken;
    private long _bytesScanned;
    private long _peakMemoryBytes;
    private long _spilledBytes;

    /// <summary>Creates a runtime bound to a backend, its options, a token, and an optional memory budget.</summary>
    /// <param name="backend">The EPIC-03 execution backend chosen for this run.</param>
    /// <param name="options">Backend options (e.g. force-interpreter) threaded into the shared context.</param>
    /// <param name="cancellationToken">The <b>effective</b> token (user cancellation linked with any timeout).</param>
    /// <param name="memoryBudgetBytes">The operator memory budget in bytes, or <see langword="null"/> for unbounded.</param>
    public PhysicalRuntime(
        IExecutionBackend backend,
        ExecutionBackendOptions options,
        CancellationToken cancellationToken,
        long? memoryBudgetBytes = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        ArgumentNullException.ThrowIfNull(options);
        _cancellationToken = cancellationToken;

        // One shared context for the whole tree. A configured memory budget swaps the unbounded memory
        // for a BoundedExecutionMemory, so an operator that cannot reserve within it fails fast with a
        // deterministic ExecutionMemoryException (criterion 3) instead of materializing unbounded state.
        IExecutionMemory memory = memoryBudgetBytes is { } budget
            ? new BoundedExecutionMemory(budget)
            : BoundedExecutionMemory.Unbounded;
        _context = new ExecutionContext(memory, cancellationToken, options);
    }

    /// <summary>The aggregate estimated data-plane bytes scanned across every operator run (diagnostics).</summary>
    public long BytesScanned => _bytesScanned;

    /// <summary>The high-water reserved execution memory across every operator run (diagnostics).</summary>
    public long PeakMemoryBytes => _peakMemoryBytes;

    /// <summary>The aggregate bytes spilled under memory pressure across every operator run (diagnostics).</summary>
    public long SpilledBytes => _spilledBytes;

    /// <summary>
    /// Opens <paramref name="op"/> on the backend over the shared context and drains its
    /// <see cref="IBatchStream"/> to a fully materialized batch list, polling cancellation at each batch
    /// boundary and folding the operator's <see cref="OperatorMetrics"/> into the run totals. Engine
    /// construction/validation failures are surfaced by the callers' <c>BuildOperator</c> guard as a
    /// deterministic <see cref="UnsupportedPlanException"/>.
    /// </summary>
    /// <remarks>
    /// <b>Batch-ownership invariant (#420).</b> Accumulating every emitted <see cref="ColumnBatch"/> into
    /// a list relies on each batch being <b>independently owned</b> — a batch stays valid across
    /// subsequent <see cref="IBatchStream.TryGetNext"/> calls and the stream's <see cref="IDisposable.Dispose"/>.
    /// Every M1 operator produces fresh, independently-owned output (fresh columns, or a view over
    /// immutable GC-owned buffers), so this holds today. A future pooled/off-heap operator that reuses
    /// (or frees on <c>Dispose</c>) its output buffers would violate it and MUST copy-out here before
    /// adding to the list; that streaming/pooling seam is tracked by #420. The stream is disposed in a
    /// <c>finally</c> on every path (drain, cancellation, fault), and the run's shared context — which
    /// owns the spill store / memory ledger — is disposed by <see cref="Dispose"/>.
    /// </remarks>
    /// <param name="op">The shallow Engine operator (built over an in-memory scan of child batches).</param>
    /// <returns>Every batch the operator emitted, in order.</returns>
    /// <exception cref="OperationCanceledException">The effective token was cancelled (user cancel or timeout).</exception>
    public IReadOnlyList<ColumnBatch> Run(PhysicalOperator op)
    {
        ArgumentNullException.ThrowIfNull(op);

        var batches = new List<ColumnBatch>();
        IBatchStream stream = _backend.Open(op, _context);
        try
        {
            // Belt-and-suspenders cancellation: Engine streams already poll the context token internally
            // (InterpretedScanStream/CancellationPolicy), but the driver also checks between batches so a
            // cancel/timeout stops promptly even for an operator that emits everything in one batch.
            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (!stream.TryGetNext(out ColumnBatch? batch))
                {
                    break;
                }

                batches.Add(batch);
            }
        }
        finally
        {
            stream.Dispose();
            AccumulateMetrics(op);
        }

        return batches;
    }

    /// <summary>Disposes the run's shared <see cref="ExecutionContext"/> (spill store / memory ledger); idempotent.</summary>
    public void Dispose()
    {
        IsDisposed = true;
        _context.Dispose();
    }

    /// <summary>
    /// Whether <see cref="Dispose"/> has run. A disposal-observability seam for STORY-04.6.4 tests that a
    /// cancelled/failed action still releases the runtime (the test fails if the executor's disposal
    /// <c>finally</c> is removed); the context's own <c>_disposed</c> guard keeps disposal idempotent.
    /// </summary>
    public bool IsDisposed { get; private set; }

    // Folds this operator's (and its shallow children's) engine metrics into the run totals. BytesScanned
    // is attributed only ONCE, at the true leaf source scan: every materialization boundary re-wraps its
    // already-materialized child in an InMemoryScanOperator (ScanOf) whose InterpretedScanStream re-reports
    // AddBytesScanned, so summing every scan would inflate BytesScanned ∝ plan depth (measured 2× for
    // project(filter(scan)) — #176 review #3). Boundary re-scans are marked (BoundaryRescanSourceId) and
    // excluded here; only the source-scan wrapper over the original leaf contributes. SpilledBytes sums
    // across operators; PeakMemoryBytes is a high-water max (operators run sequentially over the shared
    // budget, releasing on dispose, so the max per-operator peak is the whole-run peak).
    private void AccumulateMetrics(PhysicalOperator op)
    {
        OperatorMetricsSnapshot snapshot = op.Metrics.Snapshot();
        if (!IsBoundaryRescan(op))
        {
            _bytesScanned += snapshot.BytesScanned;
        }

        _spilledBytes += snapshot.SpilledBytes;
        if (snapshot.PeakMemoryBytes > _peakMemoryBytes)
        {
            _peakMemoryBytes = snapshot.PeakMemoryBytes;
        }

        foreach (PhysicalOperator child in op.Children)
        {
            AccumulateMetrics(child);
        }
    }

    // True when the operator is a boundary re-scan wrapper (an InMemoryScanOperator ScanOf built over an
    // already-materialized intermediate result) rather than the true leaf source scan, so its estimated
    // bytes must NOT be counted toward BytesScanned (see AccumulateMetrics).
    private static bool IsBoundaryRescan(PhysicalOperator op) =>
        op is InMemoryScanOperator scan && scan.SourceId == BoundaryRescanSourceId;

    /// <summary>
    /// The <see cref="InMemoryScanOperator.SourceId"/> sentinel marking a boundary re-scan wrapper — a
    /// scan over an already-materialized intermediate result rather than the true leaf source. Its
    /// estimated scanned bytes are excluded from <see cref="BytesScanned"/> so the metric is attributed
    /// once at the source and not inflated ∝ plan depth (#176 review #3).
    /// </summary>
    internal const string BoundaryRescanSourceId = "boundary-rescan";

    /// <summary>Wraps a child result's batches in an EPIC-03 in-memory scan so an operator can open over them.</summary>
    /// <param name="child">The already-materialized child result.</param>
    /// <param name="isSourceScan">
    /// <see langword="true"/> when <paramref name="child"/> is the true leaf source scan (its estimated
    /// bytes are the real data-plane read and count toward <see cref="BytesScanned"/>); <see langword="false"/>
    /// when it re-scans an already-materialized intermediate result at a materialization boundary (its
    /// bytes are excluded to avoid inflating <see cref="BytesScanned"/> ∝ plan depth — #176 review #3).
    /// </param>
    /// <returns>A leaf <see cref="PhysicalOperator"/> streaming the child batches verbatim.</returns>
    public static PhysicalOperator ScanOf(BatchResult child, bool isSourceScan = false) =>
        new InMemoryScanOperator(child.Schema, child.Batches, isSourceScan ? "memory" : BoundaryRescanSourceId);
}
