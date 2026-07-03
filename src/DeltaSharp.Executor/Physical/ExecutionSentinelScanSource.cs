using System.Collections;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Executor;

/// <summary>
/// An <see cref="IScanSource"/> whose backing batch list explodes (and counts) on <b>any</b> access —
/// enumeration, indexing, or <c>Count</c>. It is the rigorous no-execution oracle for
/// <c>DataFrame.Explain</c>'s physical section: physical PLANNING only stores the batch <i>reference</i>
/// (it renders the tree from the plan's schema, never the rows), so
/// <see cref="LocalQueryExecutor.ExplainPhysical"/> leaves <see cref="BatchAccessCount"/> at zero,
/// whereas any real EXECUTION (<c>Collect</c>/<c>Count</c>) reads the batches and trips the sentinel. A
/// test can therefore assert planner-only behaviour that would FAIL the moment <c>ExplainPhysical</c>
/// started executing.
/// </summary>
/// <remarks>
/// Lives in the (non-packable) Executor assembly — like <see cref="InMemoryRelationFixture"/> and
/// <see cref="InMemoryScanSource"/> — because <see cref="IScanSource"/> references the Core-internal
/// <see cref="ResolvedRelation"/>; it is consumed by <c>DeltaSharp.Executor.Tests</c>.
/// </remarks>
internal sealed class ExecutionSentinelScanSource : IScanSource
{
    private readonly SentinelBatchList _batches = new();

    /// <summary>How many times the backing batches were accessed (0 after a pure planning pass).</summary>
    public int BatchAccessCount => _batches.AccessCount;

    /// <inheritdoc />
    public bool TryGetBatches(ResolvedRelation relation, [NotNullWhen(true)] out IReadOnlyList<ColumnBatch>? batches)
    {
        // Resolving a relation to its batch REFERENCE is a planning-time act and must not count as a read;
        // only touching the list's contents (execution) arms the sentinel.
        batches = _batches;
        return true;
    }

    /// <summary>Thrown when the physical plan is executed (batches read) instead of merely planned.</summary>
    internal sealed class BatchExecutedException : Exception
    {
        public BatchExecutedException()
            : base("EXPLAIN must not read batches: the physical plan was executed, not merely planned.")
        {
        }
    }

    private sealed class SentinelBatchList : IReadOnlyList<ColumnBatch>
    {
        public int AccessCount { get; private set; }

        public ColumnBatch this[int index] => Trip<ColumnBatch>();

        public int Count => Trip<int>();

        public IEnumerator<ColumnBatch> GetEnumerator() => Trip<IEnumerator<ColumnBatch>>();

        IEnumerator IEnumerable.GetEnumerator() => Trip<IEnumerator>();

        private T Trip<T>()
        {
            AccessCount++;
            throw new BatchExecutedException();
        }
    }
}
