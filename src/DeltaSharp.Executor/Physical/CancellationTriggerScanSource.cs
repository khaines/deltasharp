using System.Collections;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Executor;

/// <summary>
/// An <see cref="IScanSource"/> whose backing batch list, on its <b>first execution-time access</b>
/// (enumeration/indexing/<c>Count</c>), cancels a caller-supplied token and then throws an
/// <see cref="OperationCanceledException"/> — modelling an action cancelled <i>in flight</i> while the
/// backend is reading source batches. Pure physical PLANNING only stores the batch reference (it never
/// reads it), so it never trips; only real EXECUTION (<c>Collect</c>/<c>Count</c>) arms it. Used by the
/// STORY-04.6.4 (#176) criterion-1 test to prove an in-flight cancellation (a) stops promptly — only the
/// first access happens, no rows are materialized — and (b) still releases the run's
/// <see cref="PhysicalRuntime"/> deterministically.
/// </summary>
/// <remarks>
/// Lives in the (non-packable) Executor assembly — like <see cref="ExecutionSentinelScanSource"/> — because
/// <see cref="IScanSource"/> references the Core-internal <see cref="ResolvedRelation"/>; it is consumed by
/// <c>DeltaSharp.Executor.Tests</c> through <see cref="InMemoryRelationFixture"/>.
/// </remarks>
internal sealed class CancellationTriggerScanSource : IScanSource
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TriggerBatchList _batches;

    public CancellationTriggerScanSource()
    {
        _batches = new TriggerBatchList(_cts);
    }

    /// <summary>The token the in-flight batch read cancels; wire it into the action's execution options.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>How many times the backing batches were accessed (0 after a pure planning pass, 1 after an
    /// in-flight-cancelled execution that stopped promptly at the first read).</summary>
    public int AccessCount => _batches.AccessCount;

    /// <inheritdoc />
    public bool TryGetBatches(
        ResolvedRelation relation,
        [NotNullWhen(true)] out Func<CancellationToken, IReadOnlyList<ColumnBatch>>? batchFactory)
    {
        // Resolving the batch factory is a planning-time act and must not trip; only reading the list's
        // contents (execution, when the ScanPlan invokes the factory and the operator enumerates) cancels
        // and throws.
        batchFactory = _ => _batches;
        return true;
    }

    private sealed class TriggerBatchList(CancellationTokenSource cts) : IReadOnlyList<ColumnBatch>
    {
        public int AccessCount { get; private set; }

        public ColumnBatch this[int index] => Trip<ColumnBatch>();

        public int Count => Trip<int>();

        public IEnumerator<ColumnBatch> GetEnumerator() => Trip<IEnumerator<ColumnBatch>>();

        IEnumerator IEnumerable.GetEnumerator() => Trip<IEnumerator>();

        private T Trip<T>()
        {
            AccessCount++;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }
    }
}
