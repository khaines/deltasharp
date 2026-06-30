using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.Execution.Spill;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The pull-based <see cref="IBatchStream"/> for an <see cref="ExchangeLocalOperator"/>
/// (STORY-03.2.2): the in-process repartition seam a future network shuffle (STORY-03.5.x) builds on.
/// For each input batch it routes the batch's rows into
/// <see cref="ExchangeLocalOperator.PartitionCount"/> buckets, then emits exactly that many output
/// batches in partition-id order (0..N-1). When the per-batch assignment buffers cannot be reserved it
/// <b>spills</b> (STORY-03.6.2 AC4): each row is serialized to a per-partition spill segment, the input
/// batch reference is dropped, and the partitions are recovered (materialized) on emit so per-partition
/// row counts and contents match the no-spill path exactly.
/// </summary>
/// <remarks>
/// <para><b>Assignment.</b> With partition keys, a row's partition is
/// <c>FNV-1a(canonical key bytes) mod N</c> over the byte-sortable encoding (<see cref="RowKeyProjection"/>);
/// null key fields hash normally. With no keys, rows are assigned round-robin from partition 0. The
/// assignment is identical in the in-memory and spilled paths, so spill preserves the partition mapping.</para>
/// <para><b>Spill.</b> The in-memory path emits zero-copy selection views; the spilled path materializes
/// each partition by decoding its segment, so a recovered partition is value-identical to its no-spill
/// view. A spill write failure (AC5) releases every reservation, disposes the partial segments (deleting
/// temp files), and raises <see cref="SpillIOException"/> with no partition of the failing batch emitted.</para>
/// </remarks>
internal sealed class InterpretedExchangeLocalStream : IBatchStream
{
    private readonly IBatchStream _input;
    private readonly OperatorMetrics _metrics;
    private readonly IExecutionMemory _memory;
    private readonly ISpillStore _spillStore;
    private readonly CancellationToken _cancellationToken;
    private readonly RowKeyProjection? _partitionKeys;
    private readonly int _partitionCount;
    private readonly long _rowBytes;
    private readonly RowSpillCodec _codec;

    private ColumnBatch? _currentBatch;
    private int[][] _selections = [];
    private int _partitionCursor;
    private int _roundRobin;
    private long _reservedBytes;
    private long _chunkReserved;

    // Spilled-batch state (set when the current input batch was spilled instead of held in memory).
    private bool _currentSpilled;
    private ISpillSegment[]? _spillSegments;
    private int[] _spillCounts = [];

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
        _spillStore = context.SpillStore;
        _cancellationToken = context.CancellationToken;
        _rowBytes = RowSizeEstimate.Bytes(op.OutputSchema);
        _codec = new RowSpillCodec(op.OutputSchema);
    }

    /// <inheritdoc />
    public StructType Schema { get; }

    /// <inheritdoc />
    public bool TryGetNext([NotNullWhen(true)] out ColumnBatch? batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellationToken.ThrowIfCancellationRequested();

        // Release the previous emitted spilled chunk's materialization reservation (one in flight).
        ReleaseChunkReservation();

        // Advance to an input batch that still has partitions to emit.
        while ((_currentBatch is null && !_currentSpilled) || _partitionCursor >= _partitionCount)
        {
            if (!AdvanceInput())
            {
                batch = null;
                return false;
            }
        }

        long start = Stopwatch.GetTimestamp();
        int partition = _partitionCursor++;
        ColumnBatch view = _currentSpilled ? RecoverPartition(partition) : EmitInMemoryPartition(partition);
        _metrics.AddOutput(view.LogicalRowCount);
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
        try
        {
            ReleaseChunkReservation();
            ReleaseReservation();
            DisposeSpillSegments();
        }
        finally
        {
            _input.Dispose();
        }
    }

    private ColumnBatch EmitInMemoryPartition(int partition)
    {
        var selection = new SelectionVector(_selections[partition]);
        return _currentBatch!.WithSelection(selection);
    }

    private ColumnBatch RecoverPartition(int partition)
    {
        int rows = _spillCounts[partition];
        MutableColumnVector[] columns = ColumnVectors.CreateForSchema(Schema, Math.Max(rows, 1));

        // The recovered partition is held in memory while the consumer reads it; reserve it like the
        // in-memory path's selection storage and release it on the next pull.
        ReserveChunk((rows * _rowBytes) + (rows * RowSizeEstimate.PermutationEntryBytes));

        using ISpillSegmentReader reader = _spillSegments![partition].OpenRead();
        while (reader.TryRead(out byte[]? record))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _codec.DecodeInto(columns, record);
        }

        return new ManagedColumnBatch(Schema, columns, rows);
    }

    private bool AdvanceInput()
    {
        // Release the prior batch's assignment arrays / spill segments (one input batch in flight).
        ReleaseReservation();
        DisposeSpillSegments();
        _currentBatch = null;
        _currentSpilled = false;

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

            _partitionCursor = 0;
            return true;
        }

        return false;
    }

    private void Partition(ColumnBatch batch, int rows)
    {
        // Reserve before allocating the assignment storage (~2 ints/row plus the O(partitionCount)
        // counts + cursors). If the budget refuses it, spill this batch's partitions to disk instead.
        long bytes = (2L * rows * sizeof(int)) + (2L * _partitionCount * sizeof(int));
        if (!_memory.TryReserve(bytes))
        {
            SpillPartition(batch, rows);
            return;
        }

        _reservedBytes += bytes;
        _metrics.ObserveReservation(_reservedBytes + _chunkReserved);

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
            CancellationPolicy.Poll(_cancellationToken, r);
            int p = assignment[r];
            buckets[p][cursors[p]++] = r;
        }

        _selections = buckets;
        _currentBatch = batch;
        _currentSpilled = false;
    }

    // Spills the batch's rows to one segment per partition, then drops the batch reference. The whole
    // batch is written before any partition is emitted, so a write failure leaves nothing half-emitted.
    private void SpillPartition(ColumnBatch batch, int rows)
    {
        var segments = new ISpillSegment[_partitionCount];
        var counts = new int[_partitionCount];
        long spilled = 0;
        try
        {
            for (int p = 0; p < _partitionCount; p++)
            {
                segments[p] = _spillStore.CreateSegment($"exchange-p{p}");
            }

            var columns = new ColumnVector[Schema.Count];
            for (int c = 0; c < columns.Length; c++)
            {
                columns[c] = batch.SelectedColumn(c);
            }

            var scratch = new BatchEvaluationMemory(_memory);
            try
            {
                ColumnVector[]? keyVectors = _partitionKeys?.Evaluate(batch, scratch, _cancellationToken);
                for (int r = 0; r < rows; r++)
                {
                    CancellationPolicy.Poll(_cancellationToken, r);
                    int p = PartitionOf(keyVectors, r);
                    byte[] frame = _codec.Encode(columns, r);
                    segments[p].Write(frame);
                    spilled += frame.Length;
                    counts[p]++;
                }
            }
            finally
            {
                scratch.Release();
            }
        }
        catch
        {
            // Release-all + delete temp files; the batch produced no output, so there is no partial success.
            DisposeSegments(segments);
            throw;
        }

        _spillSegments = segments;
        _spillCounts = counts;
        _currentSpilled = true;
        _currentBatch = null;
        _metrics.AddSpilledBytes(spilled);
        // Fail closed if this batch's spill would breach the per-query spill cap; segments are stored in
        // _spillSegments so Dispose deletes their temp files and releases reservations exactly once.
        _memory.RecordSpill(spilled);
    }

    private int PartitionOf(ColumnVector[]? keyVectors, int row)
    {
        if (keyVectors is null)
        {
            int p = _roundRobin;
            _roundRobin = _roundRobin + 1 == _partitionCount ? 0 : _roundRobin + 1;
            return p;
        }

        byte[] key = _partitionKeys!.Encode(keyVectors, row, out _);
        return (int)(RowKey.Fnv1a(key) % (uint)_partitionCount);
    }

    private void ComputeAssignment(ColumnBatch batch, int rows, int[] assignment, int[] counts)
    {
        if (_partitionKeys is null)
        {
            for (int r = 0; r < rows; r++)
            {
                CancellationPolicy.Poll(_cancellationToken, r);
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
                CancellationPolicy.Poll(_cancellationToken, r);
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
                "the recovered exchange partition cannot itself spill; raise the memory budget");
        }

        _chunkReserved += bytes;
        _metrics.ObserveReservation(_reservedBytes + _chunkReserved);
    }

    private void ReleaseChunkReservation()
    {
        if (_chunkReserved > 0)
        {
            _memory.Release(_chunkReserved);
            _chunkReserved = 0;
            _metrics.ObserveRelease(_reservedBytes + _chunkReserved);
        }
    }

    private void ReleaseReservation()
    {
        if (_reservedBytes > 0)
        {
            _memory.Release(_reservedBytes);
            _reservedBytes = 0;
            _metrics.ObserveRelease(_reservedBytes + _chunkReserved);
        }
    }

    private void DisposeSpillSegments()
    {
        if (_spillSegments is not null)
        {
            DisposeSegments(_spillSegments);
            _spillSegments = null;
        }
    }

    private static void DisposeSegments(ISpillSegment?[] segments)
    {
        foreach (ISpillSegment? segment in segments)
        {
            segment?.Dispose();
        }
    }
}
