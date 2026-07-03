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
    private readonly HashSet<PhysicalOperator> _sourceScans = new(ReferenceEqualityComparer.Instance);
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

    /// <summary>The aggregate estimated data-plane bytes scanned across every genuine source read (diagnostics).</summary>
    public long BytesScanned => _bytesScanned;

    /// <summary>
    /// The run's <b>effective</b> cancellation token (user cancellation linked with any timeout). Exposed so
    /// a deferred source read that materializes outside <see cref="Run"/>'s per-batch poll — the #158
    /// <c>LocalRelation</c> row→batch encode in <see cref="ScanPlan.Execute"/> — can poll it while draining
    /// its source, keeping cancellation/timeout deterministic for that plan shape too (STORY-04.6.4 AC2).
    /// </summary>
    public CancellationToken CancellationToken => _cancellationToken;

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
    // is taken only from InMemoryScanOperators marked as genuine SOURCE reads (see ScanOf): every other
    // InMemoryScanOperator here is a boundary re-scan wrapper re-streaming an already-materialized
    // intermediate, and counting those would inflate BytesScanned ∝ plan depth (2× for
    // project(filter(scan))) and mis-count bridge/union shapes (#176 review). Mapped operators
    // (Filter/Project/…) report BytesScanned 0 and are always folded. SpilledBytes sums across operators;
    // PeakMemoryBytes is a high-water max (operators run sequentially over the shared budget, releasing on
    // dispose, so the max per-operator peak is the whole-run peak).
    private void AccumulateMetrics(PhysicalOperator op)
    {
        OperatorMetricsSnapshot snapshot = op.Metrics.Snapshot();
        if (op is not InMemoryScanOperator || _sourceScans.Contains(op))
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

    /// <summary>
    /// Wraps a child result's batches in an EPIC-03 in-memory scan so an operator can open over them.
    /// When <paramref name="readsSourceDirectly"/> is <see langword="true"/> the wrapped scan is the
    /// genuine source read for this branch (its child subtree is a <c>ScanPlan</c> or a Limit/Union bridge
    /// over sources — see <see cref="PhysicalPlan.ReadsSourceDirectly"/>), so its estimated bytes are
    /// counted toward <see cref="BytesScanned"/> exactly once; otherwise it is a boundary re-scan of an
    /// already-materialized intermediate and its bytes are excluded (avoiding depth inflation / double
    /// counting). The scan itself streams the batches verbatim and its byte estimate is accumulated lazily
    /// as batches are pulled, so a cancelled/limited run only counts the bytes it actually read.
    /// </summary>
    /// <param name="child">The already-materialized child result.</param>
    /// <param name="readsSourceDirectly">Whether this scan is the branch's genuine source read.</param>
    /// <returns>A leaf <see cref="PhysicalOperator"/> streaming the child batches verbatim.</returns>
    public PhysicalOperator ScanOf(BatchResult child, bool readsSourceDirectly)
    {
        var op = new InMemoryScanOperator(child.Schema, child.Batches);
        if (readsSourceDirectly)
        {
            _sourceScans.Add(op);
        }

        return op;
    }
}
