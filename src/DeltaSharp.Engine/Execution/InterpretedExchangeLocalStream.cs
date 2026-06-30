using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The pull-based <see cref="IBatchStream"/> for an <see cref="ExchangeLocalOperator"/>
/// (STORY-03.2.2): the in-process repartition seam a future network shuffle (STORY-03.5.x) builds on.
/// It is <b>streaming and zero-copy</b> — for each input batch it routes the batch's rows into
/// <see cref="ExchangeLocalOperator.PartitionCount"/> buckets, then emits exactly that many output
/// batches in partition-id order (0..N-1), each a selection view over the input batch holding that
/// partition's rows. Output batch <c>i</c> therefore carries partition <c>i mod N</c>; the emission is
/// positional because the batch contract has no partition tag yet (the remote-shuffle story adds one).
/// </summary>
/// <remarks>
/// <para><b>Assignment.</b> With partition keys, a row's partition is
/// <c>FNV-1a(canonical key bytes) mod N</c> over the same byte-sortable encoding the other operators
/// use (<see cref="RowKeyProjection"/>); null key fields hash normally (unlike join, which drops null
/// keys). This is deliberately not Spark's Murmur3 hash partitioning — the seam only needs a
/// deterministic, well-spread, total assignment, and the network-shuffle story can adopt Murmur3 there.
/// With no keys, rows are assigned round-robin starting at partition 0 (Spark randomizes the start;
/// this is deterministic for reproducibility).</para>
/// <para><b>Preservation.</b> Each row lands in exactly one partition, so concatenating an input
/// batch's N output views reproduces its rows with none lost or duplicated. Routed bytes are recorded
/// in <see cref="OperatorMetrics.ShuffleBytes"/>.</para>
/// <para><b>Memory.</b> Bounded to one input batch's partition-assignment arrays, reserved before they
/// are built and released when the next input batch is pulled; the emitted views copy no values. A
/// refusal raises <see cref="ExecutionMemoryException"/>.</para>
/// </remarks>
internal sealed class InterpretedExchangeLocalStream : IBatchStream
{
    private readonly IBatchStream _input;
    private readonly OperatorMetrics _metrics;
    private readonly IExecutionMemory _memory;
    private readonly CancellationToken _cancellationToken;
    private readonly RowKeyProjection? _partitionKeys;
    private readonly int _partitionCount;
    private readonly long _rowBytes;

    private ColumnBatch? _currentBatch;
    private int[][] _selections = [];
    private int _partitionCursor;
    private int _roundRobin;
    private long _reservedBytes;
    private bool _disposed;

    internal InterpretedExchangeLocalStream(
        ExchangeLocalOperator op, RowKeyProjection? partitionKeys, IBatchStream input, ExecutionContext context)
    {
        Schema = op.OutputSchema;
        _partitionKeys = partitionKeys;
        _partitionCount = op.PartitionCount;
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

        // Advance to an input batch that still has partitions to emit.
        while (_currentBatch is null || _partitionCursor >= _partitionCount)
        {
            if (!AdvanceInput())
            {
                batch = null;
                return false;
            }
        }

        long start = Stopwatch.GetTimestamp();
        int partition = _partitionCursor++;
        var selection = new SelectionVector(_selections[partition]);
        ColumnBatch view = _currentBatch.WithSelection(selection);
        _metrics.AddOutput(selection.Count);
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
        ReleaseReservation();
        _input.Dispose();
    }

    private bool AdvanceInput()
    {
        // Release the prior batch's assignment arrays (one input batch in flight at a time).
        ReleaseReservation();

        while (_input.TryGetNext(out ColumnBatch? input))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int rows = input.LogicalRowCount;
            if (rows == 0)
            {
                continue; // an empty input batch routes nothing; skip rather than emit N empties
            }

            long start = Stopwatch.GetTimestamp();
            _metrics.AddInputRows(rows);
            Partition(input, rows);
            _metrics.AddShuffleBytes(rows * _rowBytes);
            _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(start));

            _currentBatch = input;
            _partitionCursor = 0;
            return true;
        }

        _currentBatch = null;
        return false;
    }

    private void Partition(ColumnBatch batch, int rows)
    {
        // Reserve before allocating the assignment storage: the per-row partition id plus the bucket
        // index arrays are ~2 ints per row.
        Reserve(2L * rows * sizeof(int));

        var assignment = new int[rows];
        var counts = new int[_partitionCount];
        ComputeAssignment(batch, rows, assignment, counts);

        var buckets = new int[_partitionCount][];
        for (int p = 0; p < _partitionCount; p++)
        {
            buckets[p] = new int[counts[p]];
        }

        var cursors = new int[_partitionCount];
        for (int r = 0; r < rows; r++)
        {
            int p = assignment[r];
            buckets[p][cursors[p]++] = r;
        }

        _selections = buckets;
    }

    private void ComputeAssignment(ColumnBatch batch, int rows, int[] assignment, int[] counts)
    {
        if (_partitionKeys is null)
        {
            for (int r = 0; r < rows; r++)
            {
                int p = _roundRobin;
                _roundRobin = _roundRobin + 1 == _partitionCount ? 0 : _roundRobin + 1;
                assignment[r] = p;
                counts[p]++;
            }

            return;
        }

        var scratch = new BatchEvaluationMemory(_memory);
        try
        {
            ColumnVector[] keyVectors = _partitionKeys.Evaluate(batch, scratch, _cancellationToken);
            for (int r = 0; r < rows; r++)
            {
                byte[] key = _partitionKeys.Encode(keyVectors, r, out _);
                int p = (int)(RowKey.Fnv1a(key) % (uint)_partitionCount);
                assignment[r] = p;
                counts[p]++;
            }
        }
        finally
        {
            scratch.Release();
        }
    }

    private void Reserve(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        if (!_memory.TryReserve(bytes))
        {
            throw new ExecutionMemoryException(
                bytes, _memory.AvailableBytes, _memory.BudgetBytes,
                "the local exchange partition buffers have no spillable representation in v1; "
                + "raise the query/tenant memory budget");
        }

        _reservedBytes += bytes;
        _metrics.ObservePeakMemory(_reservedBytes);
    }

    private void ReleaseReservation()
    {
        if (_reservedBytes > 0)
        {
            _memory.Release(_reservedBytes);
            _reservedBytes = 0;
        }
    }
}
