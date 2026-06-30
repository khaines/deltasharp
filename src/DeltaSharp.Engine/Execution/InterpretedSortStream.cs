using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.Execution.Spill;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The pull-based <see cref="IBatchStream"/> for a <see cref="SortOperator"/> (STORY-03.2.2): a total
/// sort over EPIC-02 byte-sortable keys that spills to sorted runs and k-way merges them when the input
/// exceeds the budget (STORY-03.6.2 AC2). While the buffer fits memory it is an in-memory total sort
/// (sort a permutation, emit zero-copy reordered views); when <see cref="ReserveBuffer"/> is refused the
/// current buffer is sorted and written as a <b>run</b>, the memory is released, and buffering resumes.
/// At emit, the runs are k-way merged in the same global order the in-memory comparator would produce.
/// </summary>
/// <remarks>
/// <para><b>Ordering (Spark parity).</b> Each <see cref="SortOrder"/> maps to a
/// <see cref="DeltaSharp.Engine.RowFormat.SortKeyOrdering"/> baked into the key bytes, so an ascending
/// <c>memcmp</c> realizes asc/desc × nulls-first/last across all keys (the STORY-02.4.2 encoding equal to
/// <see cref="DeltaSharp.Engine.RowFormat.RowOrderingComparer"/>: nulls by marker, <c>NaN</c> largest,
/// <c>-0.0 == +0.0</c>, exact decimals). Ties break by a global insertion ordinal so the result is
/// deterministic.</para>
/// <para><b>Spill identity.</b> The merge orders runs by <c>(keyBytes, globalSeq)</c> — the same total
/// order the in-memory <see cref="Array.Sort{T}(T[], System.Comparison{T})"/> uses (key bytes, then input
/// ordinal). Each run is internally sorted by that order and the merge picks the global minimum across run
/// heads, so the merged output is byte-identical to the no-spill output. A run write/read failure (AC5)
/// releases every reservation, disposes the runs (deleting temp files), and raises
/// <see cref="SpillIOException"/> with no rows emitted.</para>
/// </remarks>
internal sealed class InterpretedSortStream : IBatchStream
{
    private const int OutputBatchRows = 1024;

    private readonly IBatchStream _input;
    private readonly OperatorMetrics _metrics;
    private readonly IExecutionMemory _memory;
    private readonly ISpillStore _spillStore;
    private readonly CancellationToken _cancellationToken;
    private readonly RowKeyProjection _sortKeys;
    private readonly long _rowBytes;
    private readonly RowSpillCodec _codec;

    private readonly List<byte[]> _keys = new();
    private readonly List<long> _seq = new();
    private MutableColumnVector[] _buffer = [];
    private ManagedColumnBatch? _bufferBatch;
    private int[] _order = [];
    private int _rowCount;
    private long _globalSeq;
    private int _cursor;
    private int _compareCounter;
    private long _bufferReserved;
    private long _chunkReserved;
    private bool _built;
    private bool _disposed;

    // Spill (external merge sort) state.
    private readonly List<ISpillSegment> _runs = new();
    private bool _spilled;
    private SortMergeCursor[] _mergeCursors = [];
    private int[] _heap = [];
    private int _heapSize;
    private int _mergeChunkRows;

    internal InterpretedSortStream(
        SortOperator op, RowKeyProjection sortKeys, IBatchStream input, ExecutionContext context)
    {
        Schema = op.OutputSchema;
        _sortKeys = sortKeys;
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

        // Release the previous emitted chunk's reservation (one in-flight chunk).
        ReleaseChunkReservation();

        // Lazy: the first pull buffers and sorts (or builds the run cursors); later pulls slice/merge.
        EnsureBuilt();

        return _spilled ? TryGetNextMerged(out batch) : TryGetNextInMemory(out batch);
    }

    private bool TryGetNextInMemory([NotNullWhen(true)] out ColumnBatch? batch)
    {
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

    private bool TryGetNextMerged([NotNullWhen(true)] out ColumnBatch? batch)
    {
        if (_heapSize == 0)
        {
            batch = null;
            return false;
        }

        long start = Stopwatch.GetTimestamp();
        var columns = ColumnVectors.CreateForSchema(Schema, OutputBatchRows);
        _mergeChunkRows = 0;
        ReserveChunk((long)OutputBatchRows * _rowBytes);

        while (_mergeChunkRows < OutputBatchRows && _heapSize > 0)
        {
            CancellationPolicy.Poll(_cancellationToken, _mergeChunkRows);
            int run = _heap[0];
            SortMergeCursor cursor = _mergeCursors[run];
            _codec.DecodeInto(columns, cursor.RowFrame());
            _mergeChunkRows++;
            AdvanceMergeCursor(run);
        }

        var result = new ManagedColumnBatch(Schema, columns, _mergeChunkRows);
        _metrics.AddOutput(_mergeChunkRows);
        _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(start));
        batch = result;
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
            if (_bufferReserved > 0)
            {
                _memory.Release(_bufferReserved);
                _bufferReserved = 0;
            }

            DisposeMergeCursors();
            DisposeRuns();
            _metrics.ObserveRelease(0);
        }
        finally
        {
            _input.Dispose();
        }
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

        if (_spilled)
        {
            FinishExternalSort();
        }
        else
        {
            FinishInMemorySort();
        }
    }

    private void FinishInMemorySort()
    {
        long sortStart = Stopwatch.GetTimestamp();
        _bufferBatch = new ManagedColumnBatch(Schema, _buffer, _rowCount);
        _order = new int[_rowCount];
        for (int i = 0; i < _rowCount; i++)
        {
            _order[i] = i;
        }

        // Array.Sort wraps a comparer exception in InvalidOperationException; unwrap a cancellation OCE.
        try
        {
            Array.Sort(_order, CompareOrdinals);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is OperationCanceledException oce)
        {
            throw oce;
        }

        _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(sortStart));
    }

    private void FinishExternalSort()
    {
        // Sort and spill the final in-memory tail so every row lives in a run, then open the k-way merge.
        if (_rowCount > 0)
        {
            SpillCurrentRun();
        }
        else
        {
            ReleaseBufferReservation();
        }

        _mergeCursors = new SortMergeCursor[_runs.Count];
        _heap = new int[_runs.Count];
        _heapSize = 0;
        for (int i = 0; i < _runs.Count; i++)
        {
            var cursor = new SortMergeCursor(_runs[i].OpenRead());
            _mergeCursors[i] = cursor;
            if (cursor.MoveNext())
            {
                HeapPush(i);
            }
        }
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
                CancellationPolicy.Poll(_cancellationToken, r);
                byte[] key = _sortKeys.Encode(keyVectors, r, out _);

                // Reserve before storing the row. On refusal, spill the current run and retry once; a
                // single row that cannot fit even an empty budget then fails closed via ReserveBuffer.
                long need = _rowBytes + key.Length + RowSizeEstimate.VariableWidthBytes(columns, r)
                    + RowSizeEstimate.PermutationEntryBytes;
                if (!_memory.TryReserve(need))
                {
                    if (_rowCount > 0)
                    {
                        SpillCurrentRun();
                    }

                    ReserveBuffer(need);
                }
                else
                {
                    _bufferReserved += need;
                    _metrics.ObserveReservation(_bufferReserved + _chunkReserved);
                }

                _keys.Add(key);
                _seq.Add(_globalSeq++);
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

    // Sorts the buffered rows by (key, seq) and writes them as one sorted run, then releases the buffer.
    private void SpillCurrentRun()
    {
        _spilled = true;
        var permutation = new int[_rowCount];
        for (int i = 0; i < _rowCount; i++)
        {
            permutation[i] = i;
        }

        try
        {
            Array.Sort(permutation, CompareOrdinals);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is OperationCanceledException oce)
        {
            throw oce;
        }

        ISpillSegment run = _spillStore.CreateSegment($"sort-run{_runs.Count}");
        long spilled = 0;
        try
        {
            var bufferBatch = new ManagedColumnBatch(Schema, _buffer, _rowCount);
            ColumnVector[] columns = GetBufferColumns(bufferBatch);
            for (int i = 0; i < _rowCount; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int row = permutation[i];
                byte[] frame = _codec.Encode(columns, row);
                byte[] record = BuildRunRecord(_keys[row], _seq[row], frame);
                run.Write(record);
                spilled += record.Length;
            }
        }
        catch
        {
            run.Dispose();
            throw;
        }

        _runs.Add(run);
        _metrics.AddSpilledBytes(spilled);

        // Release the spilled buffer's reservation and reset for the next run.
        ReleaseBufferReservation();
        _keys.Clear();
        _seq.Clear();
        _buffer = ColumnVectors.CreateForSchema(Schema, OutputBatchRows);
        _rowCount = 0;
    }

    private ColumnVector[] GetBufferColumns(ManagedColumnBatch bufferBatch)
    {
        var columns = new ColumnVector[Schema.Count];
        for (int c = 0; c < columns.Length; c++)
        {
            columns[c] = bufferBatch.Column(c);
        }

        return columns;
    }

    private int CompareOrdinals(int a, int b)
    {
        if ((_compareCounter++ & (CancellationPolicy.RowPollInterval - 1)) == 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
        }

        int comparison = _keys[a].AsSpan().SequenceCompareTo(_keys[b]);
        return comparison != 0 ? comparison : a.CompareTo(b);
    }

    // ---- k-way merge heap (min-heap of run indices, ordered by current head's (key, seq)) ----

    private void AdvanceMergeCursor(int run)
    {
        if (_mergeCursors[run].MoveNext())
        {
            HeapSiftDown(0);
        }
        else
        {
            HeapPop();
        }
    }

    private void HeapPush(int run)
    {
        _heap[_heapSize] = run;
        int child = _heapSize++;
        while (child > 0)
        {
            int parent = (child - 1) / 2;
            if (CompareRuns(_heap[child], _heap[parent]) >= 0)
            {
                break;
            }

            (_heap[child], _heap[parent]) = (_heap[parent], _heap[child]);
            child = parent;
        }
    }

    private void HeapPop()
    {
        _heapSize--;
        if (_heapSize > 0)
        {
            _heap[0] = _heap[_heapSize];
            HeapSiftDown(0);
        }
    }

    private void HeapSiftDown(int index)
    {
        while (true)
        {
            int left = (2 * index) + 1;
            int right = left + 1;
            int smallest = index;
            if (left < _heapSize && CompareRuns(_heap[left], _heap[smallest]) < 0)
            {
                smallest = left;
            }

            if (right < _heapSize && CompareRuns(_heap[right], _heap[smallest]) < 0)
            {
                smallest = right;
            }

            if (smallest == index)
            {
                return;
            }

            (_heap[index], _heap[smallest]) = (_heap[smallest], _heap[index]);
            index = smallest;
        }
    }

    private int CompareRuns(int runA, int runB)
    {
        SortMergeCursor a = _mergeCursors[runA];
        SortMergeCursor b = _mergeCursors[runB];
        int comparison = a.Key().SequenceCompareTo(b.Key());
        return comparison != 0 ? comparison : a.Seq.CompareTo(b.Seq);
    }

    private static byte[] BuildRunRecord(byte[] key, long seq, byte[] frame)
    {
        byte[] record = new byte[sizeof(int) + key.Length + sizeof(long) + frame.Length];
        BinaryPrimitives.WriteInt32LittleEndian(record, key.Length);
        key.CopyTo(record.AsSpan(sizeof(int)));
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(sizeof(int) + key.Length), seq);
        frame.CopyTo(record.AsSpan(sizeof(int) + key.Length + sizeof(long)));
        return record;
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
                "a single sort row exceeds the whole memory budget even after spilling; raise the budget");
        }

        _bufferReserved += bytes;
        _metrics.ObserveReservation(_bufferReserved + _chunkReserved);
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
                "the sort output chunk has no spillable representation; raise the memory budget");
        }

        _chunkReserved += bytes;
        _metrics.ObserveReservation(_bufferReserved + _chunkReserved);
    }

    private void ReleaseBufferReservation()
    {
        if (_bufferReserved > 0)
        {
            _memory.Release(_bufferReserved);
            _bufferReserved = 0;
            _metrics.ObserveRelease(_bufferReserved + _chunkReserved);
        }
    }

    private void ReleaseChunkReservation()
    {
        if (_chunkReserved > 0)
        {
            _memory.Release(_chunkReserved);
            _chunkReserved = 0;
            _metrics.ObserveRelease(_bufferReserved + _chunkReserved);
        }
    }

    private void DisposeMergeCursors()
    {
        foreach (SortMergeCursor cursor in _mergeCursors)
        {
            cursor?.Dispose();
        }

        _mergeCursors = [];
    }

    private void DisposeRuns()
    {
        foreach (ISpillSegment run in _runs)
        {
            run.Dispose();
        }

        _runs.Clear();
    }

    // The per-run merge cursor over a run's records (each [keyLen][key][seq][rowFrame]).
    private sealed class SortMergeCursor : IDisposable
    {
        private readonly ISpillSegmentReader _reader;
        private byte[]? _record;
        private int _keyOffset;
        private int _keyLength;
        private int _frameOffset;

        public SortMergeCursor(ISpillSegmentReader reader) => _reader = reader;

        public long Seq { get; private set; }

        public bool MoveNext()
        {
            if (!_reader.TryRead(out byte[]? record))
            {
                _record = null;
                return false;
            }

            _record = record;
            _keyLength = BinaryPrimitives.ReadInt32LittleEndian(record);
            _keyOffset = sizeof(int);
            int seqOffset = _keyOffset + _keyLength;
            Seq = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(seqOffset));
            _frameOffset = seqOffset + sizeof(long);
            return true;
        }

        public ReadOnlySpan<byte> Key() => _record.AsSpan(_keyOffset, _keyLength);

        public ReadOnlySpan<byte> RowFrame() => _record.AsSpan(_frameOffset);

        public void Dispose() => _reader.Dispose();
    }
}
