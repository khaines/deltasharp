using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The pull-based <see cref="IBatchStream"/> for a <see cref="SortOperator"/> (STORY-03.2.2): an
/// in-memory total sort over EPIC-02 byte-sortable keys. It is a <b>pipeline breaker</b> — the first
/// <see cref="TryGetNext"/> buffers every input row, computes one order-preserving key per row, sorts
/// a permutation of row ordinals by a <c>memcmp</c> of those keys, then streams the rows out in that
/// order as bounded, zero-copy reordered views over the buffered columns.
/// </summary>
/// <remarks>
/// <para><b>Ordering (Spark parity).</b> Each <see cref="SortOrder"/> maps to a
/// <see cref="DeltaSharp.Engine.RowFormat.SortKeyOrdering"/> (direction + null placement) baked into the
/// key bytes, so an ascending <c>memcmp</c> realizes the requested asc/desc × nulls-first/last order
/// across all keys. The encoding is the one STORY-02.4.2 proved equal to
/// <see cref="DeltaSharp.Engine.RowFormat.RowOrderingComparer"/>: nulls sort by their marker, <c>NaN</c>
/// is the largest float, <c>-0.0 == +0.0</c>, decimals compare exactly, and dates/timestamps compare as
/// their integral instants. Spark's sort is not guaranteed stable; this sort breaks key ties on input
/// order so the result is deterministic.</para>
/// <para><b>Memory.</b> The whole input is buffered (in-memory; spill is STORY-03.5.x). The buffer and
/// per-row keys reserve before each row is stored and are held until <see cref="Dispose"/>; each
/// emitted chunk additionally reserves its small selection array, released on the next pull. A refusal
/// raises <see cref="ExecutionMemoryException"/>.</para>
/// </remarks>
internal sealed class InterpretedSortStream : IBatchStream
{
    private const int OutputBatchRows = 1024;

    private readonly IBatchStream _input;
    private readonly OperatorMetrics _metrics;
    private readonly IExecutionMemory _memory;
    private readonly CancellationToken _cancellationToken;
    private readonly RowKeyProjection _sortKeys;
    private readonly long _rowBytes;

    private readonly List<byte[]> _keys = new();
    private MutableColumnVector[] _buffer = [];
    private ManagedColumnBatch? _bufferBatch;
    private int[] _order = [];
    private int _rowCount;
    private int _cursor;
    private long _bufferReserved;
    private long _chunkReserved;
    private bool _built;
    private bool _disposed;

    internal InterpretedSortStream(
        SortOperator op, RowKeyProjection sortKeys, IBatchStream input, ExecutionContext context)
    {
        Schema = op.OutputSchema;
        _sortKeys = sortKeys;
        _input = input;
        _metrics = op.Metrics;
        _memory = context.Memory;
        _cancellationToken = context.CancellationToken;
        _rowBytes = RowSizeEstimate.Bytes(op.OutputSchema);
    }

    /// <inheritdoc />
    public StructType Schema { get; }

    /// <inheritdoc />
    public bool TryGetNext([NotNullWhen(true)] out ColumnBatch? batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellationToken.ThrowIfCancellationRequested();

        // Release the previous emitted chunk's selection reservation (one in-flight chunk).
        ReleaseChunkReservation();

        // Lazy: the first pull buffers and sorts; later pulls only build the next selection view.
        EnsureBuilt();

        if (_bufferBatch is null || _cursor >= _rowCount)
        {
            batch = null;
            return false;
        }

        long start = Stopwatch.GetTimestamp();
        int length = Math.Min(OutputBatchRows, _rowCount - _cursor);
        ReserveChunk((long)length * sizeof(int));
        var selection = new SelectionVector(_order.AsSpan(_cursor, length));
        ColumnBatch view = _bufferBatch.WithSelection(selection);
        _cursor += length;
        _metrics.AddOutput(length);
        _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(start));

        batch = view;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseChunkReservation();
        if (_bufferReserved > 0)
        {
            _memory.Release(_bufferReserved);
            _bufferReserved = 0;
        }

        _input.Dispose();
    }

    private void EnsureBuilt()
    {
        if (_built)
        {
            return;
        }

        _built = true;
        _buffer = ColumnVectors.CreateForSchema(Schema, OutputBatchRows);

        while (_input.TryGetNext(out ColumnBatch? input))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            long start = Stopwatch.GetTimestamp();
            _metrics.AddInputRows(input.LogicalRowCount);
            BufferBatch(input);
            _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(start));
        }

        long sortStart = Stopwatch.GetTimestamp();
        _bufferBatch = new ManagedColumnBatch(Schema, _buffer, _rowCount);
        _order = new int[_rowCount];
        for (int i = 0; i < _rowCount; i++)
        {
            _order[i] = i;
        }

        // Total order: key bytes first, input ordinal as a deterministic tie-break.
        Array.Sort(_order, CompareOrdinals);
        _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(sortStart));
    }

    private void BufferBatch(ColumnBatch batch)
    {
        int rows = batch.LogicalRowCount;
        var scratch = new BatchEvaluationMemory(_memory);
        try
        {
            ColumnVector[] keyVectors = _sortKeys.Evaluate(batch, scratch, _cancellationToken);
            int columnCount = Schema.Count;
            var columns = new ColumnVector[columnCount];
            for (int c = 0; c < columnCount; c++)
            {
                columns[c] = batch.SelectedColumn(c);
            }

            for (int r = 0; r < rows; r++)
            {
                byte[] key = _sortKeys.Encode(keyVectors, r, out _);

                // Reserve before storing the row so a refusal leaves the buffer consistent. The
                // var-width term charges the TRUE byte length of every buffered string/binary column
                // (not the flat 16-byte estimate), so a wide payload cannot bypass the budget.
                ReserveBuffer(_rowBytes + key.Length + RowSizeEstimate.VariableWidthBytes(columns, r));
                _keys.Add(key);
                for (int c = 0; c < columnCount; c++)
                {
                    if (columns[c].IsNull(r))
                    {
                        _buffer[c].AppendNull();
                    }
                    else
                    {
                        VectorMaterializer.CopyValue(_buffer[c], columns[c], r);
                    }
                }

                _rowCount++;
            }
        }
        finally
        {
            scratch.Release();
        }
    }

    private int CompareOrdinals(int a, int b)
    {
        int comparison = _keys[a].AsSpan().SequenceCompareTo(_keys[b]);
        return comparison != 0 ? comparison : a.CompareTo(b);
    }

    private void ReserveBuffer(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        if (!_memory.TryReserve(bytes))
        {
            throw new ExecutionMemoryException(
                bytes, _memory.AvailableBytes, _memory.BudgetBytes,
                "the in-memory sort buffer cannot spill in v1 (external sort is STORY-03.5.x); "
                + "raise the query/tenant memory budget");
        }

        _bufferReserved += bytes;
        _metrics.ObservePeakMemory(_bufferReserved + _chunkReserved);
    }

    private void ReserveChunk(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        if (!_memory.TryReserve(bytes))
        {
            throw new ExecutionMemoryException(
                bytes, _memory.AvailableBytes, _memory.BudgetBytes,
                "the sort output selection has no spillable representation in v1; raise the memory budget");
        }

        _chunkReserved += bytes;
        _metrics.ObservePeakMemory(_bufferReserved + _chunkReserved);
    }

    private void ReleaseChunkReservation()
    {
        if (_chunkReserved > 0)
        {
            _memory.Release(_chunkReserved);
            _chunkReserved = 0;
        }
    }
}
