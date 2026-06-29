using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The pull-based <see cref="IBatchStream"/> for an <see cref="InMemoryScanOperator"/> (STORY-03.2.1).
/// It yields the source batches <b>verbatim</b> — preserving schema, row count, column ordering, null
/// metadata, and any pre-existing selection — so the emitted output matches the supplied physical-plan
/// batches exactly. Construction does no work; the first <see cref="TryGetNext"/> advances the cursor
/// (lazy). Cancellation is observed at each batch boundary; metrics record rows read, batches/rows
/// emitted, an estimated scanned-byte volume, and the operator's own elapsed time.
/// </summary>
internal sealed class InterpretedScanStream : IBatchStream
{
    private readonly IReadOnlyList<ColumnBatch> _batches;
    private readonly OperatorMetrics _metrics;
    private readonly CancellationToken _cancellationToken;
    private int _index;
    private bool _disposed;

    internal InterpretedScanStream(InMemoryScanOperator op, ExecutionContext context)
    {
        // Store references only — no batch is touched until TryGetNext (lazy).
        Schema = op.OutputSchema;
        _batches = op.Batches;
        _metrics = op.Metrics;
        _cancellationToken = context.CancellationToken;
    }

    /// <inheritdoc />
    public StructType Schema { get; }

    /// <inheritdoc />
    public bool TryGetNext([NotNullWhen(true)] out ColumnBatch? batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellationToken.ThrowIfCancellationRequested();

        if (_index >= _batches.Count)
        {
            batch = null;
            return false;
        }

        long start = Stopwatch.GetTimestamp();
        ColumnBatch next = _batches[_index++];
        int logicalRows = next.LogicalRowCount;

        _metrics.AddInputRows(logicalRows);
        _metrics.AddBytesScanned(EstimateBatchBytes(next));
        _metrics.AddOutput(logicalRows);
        _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(start));

        batch = next;
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _disposed = true;

    /// <summary>
    /// A best-effort estimate of the data bytes a real scan would read for this batch: per fixed-width
    /// column, <c>rows × width</c>; per variable-width column, the sum of non-null value lengths. It is
    /// a v1 proxy for tenant scan-byte accounting — the byte-accurate figure comes from the Parquet
    /// reader in the storage layer. Allocation-free (it reads spans, never copies).
    /// </summary>
    private static long EstimateBatchBytes(ColumnBatch batch)
    {
        long bytes = 0;
        for (int c = 0; c < batch.ColumnCount; c++)
        {
            ColumnVector column = batch.Column(c);
            if (!column.Type.TryGetPhysicalLayout(out PhysicalLayout layout))
            {
                continue;
            }

            if (layout.Kind == PhysicalLayoutKind.FixedWidth)
            {
                bytes += (long)column.Length * layout.FixedWidthBytes;
            }
            else if (layout.Kind == PhysicalLayoutKind.Variable)
            {
                for (int i = 0; i < column.Length; i++)
                {
                    if (!column.IsNull(i))
                    {
                        bytes += column.GetBytes(i).Length;
                    }
                }
            }
        }

        return bytes;
    }
}
