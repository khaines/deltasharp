using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.Execution.Spill;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The pull-based <see cref="IBatchStream"/> for a <see cref="JoinOperator"/> (STORY-03.2.2): a hash
/// join that buffers and hashes the <b>right</b> (build) input, then <b>streams</b> the left (probe)
/// input against it, emitting joined rows in bounded chunks. The build phase is the only blocking
/// part; the probe is fully resumable across pulls, so probe-side memory stays bounded by one output
/// chunk plus the (held) build table.
/// </summary>
/// <remarks>
/// <para><b>Shapes (Spark parity).</b> All six <see cref="JoinType"/>s ship in v1:
/// <c>INNER</c> emits matched pairs; <c>LEFT/RIGHT/FULL OUTER</c> add null-padded unmatched rows from
/// the respective side; <c>LEFT SEMI</c>/<c>LEFT ANTI</c> emit a left row once when it has, respectively
/// has no, right match (left columns only). Row multiplicity is exact — a left row joins every matching
/// build row.</para>
/// <para><b>Null keys.</b> A row whose join key has any null is never indexed and never matches (SQL
/// equi-join <c>null ≠ null</c>): it is dropped by <c>INNER</c>/<c>SEMI</c>, kept null-padded by
/// <c>LEFT</c>/<c>FULL OUTER</c> (left) and <c>RIGHT</c>/<c>FULL OUTER</c> (right, via the unmatched
/// pass), and kept by <c>ANTI</c>. Non-null keys match through the canonical byte-sortable encoding
/// (<see cref="RowKeyProjection"/>), so <c>NaN</c>/<c>-0.0</c> normalize exactly as Spark joins them.</para>
/// <para><b>Memory.</b> The build relation, its hash table, and the matched-flags are buffered
/// in-memory and held until <see cref="Dispose"/>; each emitted chunk reserves its own output columns,
/// released on the next pull. When a build reservation is refused the join switches to a grace-hash
/// spill (STORY-03.6.2): build and probe are hash-partitioned to spill segments and joined one
/// partition at a time, so cardinality and null-key semantics are preserved while peak memory stays
/// bounded to a single partition.</para>
/// </remarks>
internal sealed class InterpretedJoinStream : IBatchStream
{
    private const int OutputBatchRows = 1024;

    // Grace-hash fan-out when the build side spills (STORY-03.6.2). A row's partition is
    // FNV-1a(encodedKey) mod PartitionCount; matching keys hash identically so a probe row and its build
    // matches always co-locate, preserving join cardinality. Null-key rows (which never match) bypass the
    // partitions into dedicated null segments for the OUTER unmatched passes.
    private const int PartitionCount = 16;

    private readonly IBatchStream _probe;
    private readonly IBatchStream _build;
    private readonly OperatorMetrics _metrics;
    private readonly IExecutionMemory _memory;
    private readonly CancellationToken _cancellationToken;
    private readonly RowKeyProjection _leftKeys;
    private readonly RowKeyProjection _rightKeys;
    private readonly JoinType _joinType;
    private readonly int _leftCount;
    private readonly int _rightCount;
    private readonly bool _emitRightColumns;
    private readonly bool _emitUnmatchedBuild;
    private readonly bool _semiAnti;
    private readonly long _buildRowBytes;
    private readonly long _outputRowBytes;

    private readonly ISpillStore _spillStore;
    private readonly RowSpillCodec _buildCodec;
    private readonly RowSpillCodec _probeCodec;
    private readonly SpillStateWriter _recordWriter = new();

    private readonly Dictionary<RowKey, List<int>> _buildTable = new();
    private MutableColumnVector[] _buildColumns = [];
    private bool[] _matched = [];
    private int _buildRowCount;

    private MutableColumnVector[] _outColumns = [];
    private int _outRows;
    private long _outReserved;
    private long _buildReserved;

    // Grace-mode spill state.
    private bool _grace;
    private ISpillSegment[]? _buildPartitions;
    private ISpillSegment[]? _probePartitions;
    private ISpillSegment? _buildNull;
    private ISpillSegment? _probeNull;
    private bool _probePartitioned;
    private int _gracePartition;
    private ISpillSegmentReader? _probeReader;

    // Probe state, preserved across pulls so a left row's matches resume mid-chunk.
    private ColumnBatch? _leftBatch;
    private ColumnVector[]? _leftColumns;
    private ColumnVector[]? _leftKeyVectors;
    private BatchEvaluationMemory? _leftScratch;
    private int _leftRow;
    private int _leftRows;
    private bool _rowInitialized;
    private List<int>? _currentMatches;
    private int _matchPos;
    private bool _pendingNullRight;
    private bool _pendingLeftOnly;
    private bool _probeDone;
    private int _unmatchedCursor;

    private bool _built;
    private bool _disposed;

    // Nanos spent inside the probe child's TryGetNext during the current FillChunk, excluded from the
    // join's own ElapsedNanos so child-pull time is not double-counted up the tree (mirrors the build
    // loop, which samples its clock AFTER the build child pull returns).
    private long _probePullNanos;

    internal InterpretedJoinStream(
        JoinOperator op,
        RowKeyProjection leftKeys,
        RowKeyProjection rightKeys,
        IBatchStream probe,
        IBatchStream build,
        ExecutionContext context)
    {
        Schema = op.OutputSchema;
        _joinType = op.JoinType;
        _leftKeys = leftKeys;
        _rightKeys = rightKeys;
        _probe = probe;
        _build = build;
        _metrics = op.Metrics;
        _memory = context.Memory;
        _cancellationToken = context.CancellationToken;
        _leftCount = op.Children[0].OutputSchema.Count;
        _rightCount = op.Children[1].OutputSchema.Count;

        _semiAnti = _joinType is JoinType.LeftSemi or JoinType.LeftAnti;
        _emitRightColumns = !_semiAnti;
        _emitUnmatchedBuild = _joinType is JoinType.RightOuter or JoinType.FullOuter;
        _buildRowBytes = RowSizeEstimate.Bytes(op.Children[1].OutputSchema);
        _outputRowBytes = RowSizeEstimate.Bytes(op.OutputSchema);

        _spillStore = context.SpillStore;
        _buildCodec = new RowSpillCodec(op.Children[1].OutputSchema);
        _probeCodec = new RowSpillCodec(op.Children[0].OutputSchema);
    }

    /// <inheritdoc />
    public StructType Schema { get; }

    /// <inheritdoc />
    public bool TryGetNext([NotNullWhen(true)] out ColumnBatch? batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellationToken.ThrowIfCancellationRequested();

        // Release the previous emitted chunk's reservation (one in-flight output batch).
        ReleaseOutputReservation();

        EnsureBuilt();

        long start = Stopwatch.GetTimestamp();
        _probePullNanos = 0;
        ResetOutputColumns();
        FillChunk();

        // Exclude the probe subtree's pull time so the join times only its own work (§2.1).
        long elapsed = InterpretedOperators.ElapsedNanos(start) - _probePullNanos;
        if (_outRows == 0)
        {
            _metrics.AddElapsedNanos(elapsed);
            batch = null;
            return false;
        }

        var result = new ManagedColumnBatch(Schema, _outColumns, _outRows);
        _metrics.AddOutput(_outRows);
        _metrics.AddElapsedNanos(elapsed);
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

        // Release this operator's own reservations first (so its exactly-once accounting holds), then
        // dispose the children in a nested try/finally so a throw from the probe Dispose (or the own-byte
        // release) cannot skip the build Dispose and strand its (grandchild) reservations.
        try
        {
            _leftScratch?.Release();
            _leftScratch = null;
            ReleaseOutputReservation();
            if (_buildReserved > 0)
            {
                _memory.Release(_buildReserved);
                _buildReserved = 0;
            }

            _metrics.ObserveRelease(0);
        }
        finally
        {
            try
            {
                // Dispose all spill resources (deletes temp files for a TempFileSpillStore) on the normal,
                // cancellation, AND failure paths, so a grace join leaves no leaked temp files.
                DisposeGraceResources();
            }
            finally
            {
                try
                {
                    _probe.Dispose();
                }
                finally
                {
                    _build.Dispose();
                }
            }
        }
    }

    private void DisposeGraceResources()
    {
        try
        {
            _probeReader?.Dispose();
            _probeReader = null;
        }
        finally
        {
            try
            {
                DisposeSegmentArray(_buildPartitions);
                _buildPartitions = null;
            }
            finally
            {
                try
                {
                    DisposeSegmentArray(_probePartitions);
                    _probePartitions = null;
                }
                finally
                {
                    try
                    {
                        _buildNull?.Dispose();
                        _buildNull = null;
                    }
                    finally
                    {
                        _probeNull?.Dispose();
                        _probeNull = null;
                    }
                }
            }
        }
    }

    private static void DisposeSegmentArray(ISpillSegment[]? segments)
    {
        if (segments is null)
        {
            return;
        }

        DisposeSegmentsFrom(segments, 0);
    }

    private static void DisposeSegmentsFrom(ISpillSegment[] segments, int index)
    {
        if (index >= segments.Length)
        {
            return;
        }

        try
        {
            segments[index].Dispose();
        }
        finally
        {
            DisposeSegmentsFrom(segments, index + 1);
        }
    }

    private void EnsureBuilt()
    {
        if (_built)
        {
            return;
        }

        _built = true;
        _buildColumns = ColumnVectors.CreateForSchema(_build.Schema, OutputBatchRows);

        while (_build.TryGetNext(out ColumnBatch? input))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            long start = Stopwatch.GetTimestamp();
            _metrics.AddInputRows(input.LogicalRowCount);
            BufferBuildBatch(input);
            _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(start));
        }

        if (_grace)
        {
            // Build spilled: every build row now lives in a partition (or null) segment. The spill cap was
            // charged INCREMENTALLY, once per partitioned range during the drain (see PartitionBuildRange),
            // so a breach already failed closed mid-drain after at most one range of overshoot — there is no
            // end-of-drain RecordSpill here that could let the whole build side reach disk before the cap is
            // checked (#156 B1b). The probe is partitioned lazily on first emit, then partitions are joined
            // one at a time.
            _gracePartition = -1;
            return;
        }

        _matched = new bool[_buildRowCount];
    }

    private void BufferBuildBatch(ColumnBatch batch)
    {
        int rows = batch.LogicalRowCount;
        var scratch = new BatchEvaluationMemory(_memory);
        try
        {
            ColumnVector[] keyVectors = _rightKeys.Evaluate(batch, scratch, _cancellationToken);
            var columns = new ColumnVector[_rightCount];
            for (int c = 0; c < _rightCount; c++)
            {
                columns[c] = batch.SelectedColumn(c);
            }

            // Already in grace mode: route the whole batch to spill partitions.
            if (_grace)
            {
                PartitionBuildRange(keyVectors, columns, 0, rows);
                return;
            }

            for (int r = 0; r < rows; r++)
            {
                CancellationPolicy.Poll(_cancellationToken, r);
                byte[] key = _rightKeys.Encode(keyVectors, r, out bool anyNull);

                // Decide the collection overhead (deferral (a)) BEFORE reserving, so the reservation
                // precedes every mutation: the _matched flag is charged for every build row; a non-null
                // key additionally charges a hash entry + new List<int> when first seen, or an amortized
                // list-append when the key already exists. Look the bucket up once and reuse it below.
                long overhead = RowSizeEstimate.MatchFlagBytes;
                RowKey rowKey = default;
                List<int>? existing = null;
                if (!anyNull)
                {
                    rowKey = new RowKey(key);
                    overhead += _buildTable.TryGetValue(rowKey, out existing)
                        ? RowSizeEstimate.ListAppendBytes
                        : RowSizeEstimate.HashTableEntryBytes + RowSizeEstimate.ListHeaderBytes;
                }

                // The var-width term charges the TRUE byte length of every buffered right column,
                // so a wide string/binary build payload cannot bypass the budget.
                if (!TryReserveBuild(
                    _buildRowBytes + key.Length + RowSizeEstimate.VariableWidthBytes(columns, r) + overhead))
                {
                    // Refused: spill all buffered build rows, then partition the rest of this batch
                    // (including the current row r) straight to disk. The build side is now grace mode.
                    SwitchToGraceBuild();
                    PartitionBuildRange(keyVectors, columns, r, rows);
                    return;
                }

                int ordinal = _buildRowCount++;
                for (int c = 0; c < _rightCount; c++)
                {
                    if (columns[c].IsNull(r))
                    {
                        _buildColumns[c].AppendNull();
                    }
                    else
                    {
                        VectorMaterializer.CopyValue(_buildColumns[c], columns[c], r);
                    }
                }

                // Null keys are buffered (so RIGHT/FULL OUTER can emit them unmatched) but never indexed.
                if (anyNull)
                {
                    continue;
                }

                if (existing is not null)
                {
                    existing.Add(ordinal);
                }
                else
                {
                    _buildTable[rowKey] = [ordinal];
                }
            }
        }
        finally
        {
            scratch.Release();
        }
    }

    /// <summary>Advances the probe / unmatched-build state until the output chunk is full or work is exhausted.</summary>
    private void FillChunk()
    {
        while (_outRows < OutputBatchRows)
        {
            if (!_probeDone)
            {
                if (_leftBatch is null || _leftRow >= _leftRows)
                {
                    if (!AdvanceLeftBatch())
                    {
                        _probeDone = true;
                    }

                    continue;
                }

                EmitLeftRow();
            }
            else
            {
                // Probe exhausted for the current scope: emit the unmatched build rows (OUTER), then in
                // grace mode advance to the next partition; otherwise the join is complete.
                if (_emitUnmatchedBuild && EmitUnmatchedBuild())
                {
                    return;
                }

                if (_grace && AdvanceGracePartition())
                {
                    continue;
                }

                return;
            }
        }
    }

    private bool AdvanceLeftBatch()
    {
        _leftScratch?.Release();
        _leftScratch = null;

        ColumnBatch? next;
        bool hasNext;
        if (_grace)
        {
            hasNext = TryReadProbePartitionBatch(out next);
        }
        else
        {
            // Time the probe child pull separately so it is excluded from the join's self-time.
            long probeStart = Stopwatch.GetTimestamp();
            hasNext = _probe.TryGetNext(out next);
            _probePullNanos += InterpretedOperators.ElapsedNanos(probeStart);
        }

        if (!hasNext)
        {
            _leftBatch = null;
            return false;
        }

        _cancellationToken.ThrowIfCancellationRequested();
        _leftBatch = next!;
        _leftRows = _leftBatch.LogicalRowCount;
        _leftRow = 0;
        _rowInitialized = false;

        // In grace mode probe rows were already counted as input during partitioning; only the streaming
        // (no-spill) path counts them here.
        if (!_grace)
        {
            _metrics.AddInputRows(_leftRows);
        }

        _leftScratch = new BatchEvaluationMemory(_memory);
        _leftKeyVectors = _leftKeys.Evaluate(_leftBatch, _leftScratch, _cancellationToken);
        _leftColumns = new ColumnVector[_leftCount];
        for (int c = 0; c < _leftCount; c++)
        {
            _leftColumns[c] = _leftBatch.SelectedColumn(c);
        }

        return true;
    }

    private void EmitLeftRow()
    {
        CancellationPolicy.Poll(_cancellationToken, _leftRow);
        if (!_rowInitialized)
        {
            InitializeLeftRow();
        }

        // Emit every build match for this left row (resumes from _matchPos after a chunk boundary).
        if (_currentMatches is not null)
        {
            while (_matchPos < _currentMatches.Count)
            {
                if (_outRows == OutputBatchRows)
                {
                    return;
                }

                int buildRow = _currentMatches[_matchPos++];
                AppendJoinedRow(_leftRow, buildRow);
                if (_emitUnmatchedBuild)
                {
                    _matched[buildRow] = true;
                }
            }
        }

        if (_pendingNullRight)
        {
            if (_outRows == OutputBatchRows)
            {
                return;
            }

            AppendLeftWithNullRight(_leftRow);
            _pendingNullRight = false;
        }

        if (_pendingLeftOnly)
        {
            if (_outRows == OutputBatchRows)
            {
                return;
            }

            AppendLeftOnly(_leftRow);
            _pendingLeftOnly = false;
        }

        // The left row is fully emitted; advance.
        _rowInitialized = false;
        _leftRow++;
    }

    private void InitializeLeftRow()
    {
        byte[] key = _leftKeys.Encode(_leftKeyVectors!, _leftRow, out bool anyNull);
        List<int>? list = anyNull ? null : _buildTable.GetValueOrDefault(new RowKey(key));
        bool hasMatch = list is { Count: > 0 };

        _currentMatches = null;
        _matchPos = 0;
        _pendingNullRight = false;
        _pendingLeftOnly = false;

        switch (_joinType)
        {
            case JoinType.Inner:
                _currentMatches = list;
                break;
            case JoinType.LeftOuter:
                _currentMatches = list;
                _pendingNullRight = !hasMatch;
                break;
            case JoinType.RightOuter:
                // Only matched pairs come from the probe; unmatched build rows come from the final pass.
                _currentMatches = list;
                break;
            case JoinType.FullOuter:
                _currentMatches = list;
                _pendingNullRight = !hasMatch;
                break;
            case JoinType.LeftSemi:
                _pendingLeftOnly = hasMatch;
                break;
            case JoinType.LeftAnti:
                _pendingLeftOnly = !hasMatch;
                break;
            default:
                throw new UnsupportedOperatorException(
                    OperatorKind.Join, "interpreted", $"join type '{_joinType}' is not implemented");
        }

        _rowInitialized = true;
    }

    private bool EmitUnmatchedBuild()
    {
        while (_unmatchedCursor < _buildRowCount)
        {
            if (_outRows == OutputBatchRows)
            {
                return true;
            }

            int buildRow = _unmatchedCursor++;
            if (!_matched[buildRow])
            {
                AppendNullLeftWithBuild(buildRow);
            }
        }

        return false;
    }

    private void AppendJoinedRow(int leftRow, int buildRow)
    {
        // Output var-width accounting (deferral (c)): charge the TRUE byte length of the copied left and
        // build values on top of the flat row estimate, so the bounded output chunk holds in bytes.
        ReserveOutput(
            _outputRowBytes
            + RowSizeEstimate.VariableWidthBytes(_leftColumns!, leftRow)
            + RowSizeEstimate.VariableWidthBytes(_buildColumns, buildRow));
        for (int c = 0; c < _leftCount; c++)
        {
            AppendValueOrNull(_outColumns[c], _leftColumns![c], leftRow);
        }

        for (int c = 0; c < _rightCount; c++)
        {
            AppendValueOrNull(_outColumns[_leftCount + c], _buildColumns[c], buildRow);
        }

        _outRows++;
    }

    private void AppendLeftWithNullRight(int leftRow)
    {
        ReserveOutput(_outputRowBytes + RowSizeEstimate.VariableWidthBytes(_leftColumns!, leftRow));
        for (int c = 0; c < _leftCount; c++)
        {
            AppendValueOrNull(_outColumns[c], _leftColumns![c], leftRow);
        }

        for (int c = 0; c < _rightCount; c++)
        {
            _outColumns[_leftCount + c].AppendNull();
        }

        _outRows++;
    }

    private void AppendNullLeftWithBuild(int buildRow)
    {
        ReserveOutput(_outputRowBytes + RowSizeEstimate.VariableWidthBytes(_buildColumns, buildRow));
        for (int c = 0; c < _leftCount; c++)
        {
            _outColumns[c].AppendNull();
        }

        for (int c = 0; c < _rightCount; c++)
        {
            AppendValueOrNull(_outColumns[_leftCount + c], _buildColumns[c], buildRow);
        }

        _outRows++;
    }

    private void AppendLeftOnly(int leftRow)
    {
        ReserveOutput(_outputRowBytes + RowSizeEstimate.VariableWidthBytes(_leftColumns!, leftRow));
        for (int c = 0; c < _leftCount; c++)
        {
            AppendValueOrNull(_outColumns[c], _leftColumns![c], leftRow);
        }

        _outRows++;
    }

    private static void AppendValueOrNull(MutableColumnVector destination, ColumnVector source, int row)
    {
        if (source.IsNull(row))
        {
            destination.AppendNull();
        }
        else
        {
            VectorMaterializer.CopyValue(destination, source, row);
        }
    }

    private void ResetOutputColumns()
    {
        int width = _emitRightColumns ? _leftCount + _rightCount : _leftCount;
        _outColumns = new MutableColumnVector[width];
        for (int c = 0; c < width; c++)
        {
            _outColumns[c] = ColumnVectors.Create(Schema[c].DataType, OutputBatchRows);
        }

        _outRows = 0;
    }

    private bool TryReserveBuild(long bytes)
    {
        if (!_memory.TryReserve(bytes))
        {
            return false;
        }

        _buildReserved += bytes;
        _metrics.ObserveReservation(_buildReserved + _outReserved);
        return true;
    }

    // ---- Grace-hash spill (STORY-03.6.2) --------------------------------------------------------------

    // Switches the build side to grace mode: creates the spill partitions and flushes every already-
    // buffered build row to disk, then releases all in-memory build state. A spill-store write failure
    // here propagates out of EnsureBuilt before any row is emitted; Dispose then releases memory and
    // deletes temp files (AC5: release-all + deterministic error + no partial output).
    private void SwitchToGraceBuild()
    {
        EnsureGracePartitions();

        // Flush the rows already materialized in _buildColumns (ordinals 0.._buildRowCount). Re-evaluate
        // their keys over the buffered columns so each lands in the same partition a probe match will.
        if (_buildRowCount > 0)
        {
            var buffered = new ManagedColumnBatch(_build.Schema, _buildColumns, _buildRowCount);
            var scratch = new BatchEvaluationMemory(_memory);
            try
            {
                ColumnVector[] keyVectors = _rightKeys.Evaluate(buffered, scratch, _cancellationToken);
                PartitionBuildRange(keyVectors, _buildColumns, 0, _buildRowCount);
            }
            finally
            {
                scratch.Release();
            }
        }

        _grace = true;

        // Release the in-memory build reservation and drop the buffered rows / index.
        if (_buildReserved > 0)
        {
            _memory.Release(_buildReserved);
            _buildReserved = 0;
        }

        _buildTable.Clear();
        _buildRowCount = 0;
        _buildColumns = ColumnVectors.CreateForSchema(_build.Schema, OutputBatchRows);
        _metrics.ObserveRelease(_outReserved);
    }

    private void PartitionBuildRange(ColumnVector[] keyVectors, ColumnVector[] columns, int start, int end)
    {
        long batchSpilled = 0;
        for (int r = start; r < end; r++)
        {
            CancellationPolicy.Poll(_cancellationToken, r);
            byte[] key = _rightKeys.Encode(keyVectors, r, out bool anyNull);
            byte[] frame = _buildCodec.Encode(columns, r);

            _recordWriter.Reset();
            _recordWriter.WriteBool(!anyNull);
            if (!anyNull)
            {
                _recordWriter.WriteBytes(key);
            }

            _recordWriter.WriteBytes(frame);

            ISpillSegment target;
            if (anyNull)
            {
                EnsureBuildNull();
                target = _buildNull!;
            }
            else
            {
                target = _buildPartitions![(int)(RowKey.Fnv1a(key) % PartitionCount)];
            }

            target.Write(_recordWriter.WrittenSpan);
            batchSpilled += _recordWriter.WrittenSpan.Length;
        }

        // Charge the spill cap INCREMENTALLY, once per partitioned build range (one build batch, or the
        // one-time flush of the in-memory buffer when grace begins), so a breach fails closed mid-drain
        // after at most one range of overshoot instead of after the whole build side is on disk (#156 B1b
        // bounded blast radius). The throw propagates out of EnsureBuilt before any output is emitted, and
        // the reservations/segments unwind through Dispose exactly once. Report the metric first so
        // SpilledBytes reflects bytes actually written even when RecordSpill then fails closed.
        _metrics.AddSpilledBytes(batchSpilled);
        _memory.RecordSpill(batchSpilled);
    }

    // Drains the entire probe and partitions it to disk by the same hash, so each partition's probe rows
    // join only against the co-located build partition. Null-key probe rows go to the null segment.
    private void PartitionProbe()
    {
        EnsureProbePartitions();
        while (true)
        {
            long probeStart = Stopwatch.GetTimestamp();
            bool hasNext = _probe.TryGetNext(out ColumnBatch? batch);
            _probePullNanos += InterpretedOperators.ElapsedNanos(probeStart);
            if (!hasNext)
            {
                break;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            _metrics.AddInputRows(batch!.LogicalRowCount);
            long batchSpilled = 0;
            var scratch = new BatchEvaluationMemory(_memory);
            try
            {
                ColumnVector[] keyVectors = _leftKeys.Evaluate(batch, scratch, _cancellationToken);
                var columns = new ColumnVector[_leftCount];
                for (int c = 0; c < _leftCount; c++)
                {
                    columns[c] = batch.SelectedColumn(c);
                }

                int rows = batch.LogicalRowCount;
                for (int r = 0; r < rows; r++)
                {
                    CancellationPolicy.Poll(_cancellationToken, r);
                    byte[] key = _leftKeys.Encode(keyVectors, r, out bool anyNull);
                    byte[] frame = _probeCodec.Encode(columns, r);

                    ISpillSegment target;
                    if (anyNull)
                    {
                        EnsureProbeNull();
                        target = _probeNull!;
                    }
                    else
                    {
                        target = _probePartitions![(int)(RowKey.Fnv1a(key) % PartitionCount)];
                    }

                    target.Write(frame);
                    batchSpilled += frame.Length;
                }
            }
            finally
            {
                scratch.Release();
            }

            // Charge the spill cap INCREMENTALLY, once per probe batch, BEFORE pulling the next batch, so a
            // breach fails closed mid-drain after at most one batch of overshoot instead of after the entire
            // probe stream is on disk (#156 B1b bounded blast radius). On the throwing path _probePartitioned
            // stays false, so no partition is ever emitted and the partially-written segments unwind through
            // Dispose exactly once with zero join output. Report the metric first so SpilledBytes reflects
            // bytes actually written even when RecordSpill then fails closed.
            _metrics.AddSpilledBytes(batchSpilled);
            _memory.RecordSpill(batchSpilled);
        }

        _probePartitioned = true;
    }

    // Advances to the next logical partition in the grace emit sequence: real partitions 0..P-1, then the
    // probe-null pseudo-partition (empty build → unmatched probe rows for LEFT/FULL/ANTI), then the
    // build-null pseudo-partition (empty probe → unmatched build rows for RIGHT/FULL). Returns false when
    // every partition is drained. Reuses the in-memory per-row emit machinery unchanged.
    private bool AdvanceGracePartition()
    {
        if (!_probePartitioned)
        {
            PartitionProbe();
        }

        // Release the previous partition's build reservation and probe reader before loading the next.
        if (_buildReserved > 0)
        {
            _memory.Release(_buildReserved);
            _buildReserved = 0;
        }

        _probeReader?.Dispose();
        _probeReader = null;

        _gracePartition++;
        if (_gracePartition >= PartitionCount + 2)
        {
            return false;
        }

        ResetBuildTable();

        if (_gracePartition < PartitionCount)
        {
            LoadBuildPartition(_buildPartitions?[_gracePartition]);
            _probeReader = _probePartitions?[_gracePartition].OpenRead();
        }
        else if (_gracePartition == PartitionCount)
        {
            // Probe-null partition: empty build, probe = null-key probe rows (never match).
            _probeReader = _probeNull?.OpenRead();
        }
        else
        {
            // Build-null partition: build = null-key build rows (never indexed), empty probe.
            LoadBuildPartition(_buildNull);
        }

        _probeDone = false;
        _unmatchedCursor = 0;
        _leftBatch = null;
        _leftRow = 0;
        _leftRows = 0;
        _rowInitialized = false;
        return true;
    }

    private void ResetBuildTable()
    {
        _buildTable.Clear();
        _buildRowCount = 0;
        _buildColumns = ColumnVectors.CreateForSchema(_build.Schema, OutputBatchRows);
        _matched = [];
    }

    private void LoadBuildPartition(ISpillSegment? segment)
    {
        if (segment is null)
        {
            _matched = new bool[0];
            return;
        }

        using ISpillSegmentReader reader = segment.OpenRead();
        while (reader.TryRead(out byte[]? record))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var stateReader = new SpillStateReader(record);
            bool hasKey = stateReader.ReadBool();
            byte[]? key = hasKey ? stateReader.ReadBytes().ToArray() : null;
            ReadOnlySpan<byte> frame = stateReader.ReadBytes();

            long overhead = RowSizeEstimate.MatchFlagBytes;
            RowKey rowKey = default;
            List<int>? existing = null;
            if (hasKey)
            {
                rowKey = new RowKey(key!);
                overhead += _buildTable.TryGetValue(rowKey, out existing)
                    ? RowSizeEstimate.ListAppendBytes
                    : RowSizeEstimate.HashTableEntryBytes + RowSizeEstimate.ListHeaderBytes;
            }

            ReservePartitionBuild(_buildRowBytes + (key?.Length ?? 0) + overhead);
            int ordinal = _buildRowCount++;
            _buildCodec.DecodeInto(_buildColumns, frame);

            if (!hasKey)
            {
                continue;
            }

            if (existing is not null)
            {
                existing.Add(ordinal);
            }
            else
            {
                _buildTable[rowKey] = [ordinal];
            }
        }

        _matched = new bool[_buildRowCount];
    }

    private bool TryReadProbePartitionBatch([NotNullWhen(true)] out ColumnBatch? batch)
    {
        if (_probeReader is null)
        {
            batch = null;
            return false;
        }

        MutableColumnVector[]? columns = null;
        int rows = 0;
        while (rows < OutputBatchRows && _probeReader.TryRead(out byte[]? record))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            columns ??= ColumnVectors.CreateForSchema(_probe.Schema, OutputBatchRows);
            _probeCodec.DecodeInto(columns, record);
            rows++;
        }

        if (columns is null)
        {
            batch = null;
            return false;
        }

        batch = new ManagedColumnBatch(_probe.Schema, columns, rows);
        return true;
    }

    private void ReservePartitionBuild(long bytes)
    {
        if (!_memory.TryReserve(bytes))
        {
            throw new ExecutionMemoryException(
                bytes, _memory.AvailableBytes, _memory.BudgetBytes,
                "a single spilled join partition's build side exceeds the operator memory budget on "
                + "recovery; raise the budget (recursive re-partitioning of an over-large partition is "
                + "deferred to the shuffle epic)");
        }

        _buildReserved += bytes;
        _metrics.ObserveReservation(_buildReserved + _outReserved);
    }

    private void EnsureGracePartitions()
    {
        if (_buildPartitions is not null)
        {
            return;
        }

        _buildPartitions = CreatePartitionSegments("join-build");
        _probePartitions = CreatePartitionSegments("join-probe");
    }

    private void EnsureProbePartitions()
    {
        // The probe partitions are created alongside the build partitions when grace mode begins.
        EnsureGracePartitions();
    }

    private ISpillSegment[] CreatePartitionSegments(string label)
    {
        var segments = new ISpillSegment[PartitionCount];
        int created = 0;
        try
        {
            for (; created < PartitionCount; created++)
            {
                segments[created] = _spillStore.CreateSegment($"{label}-p{created}");
            }
        }
        catch
        {
            for (int i = 0; i < created; i++)
            {
                segments[i].Dispose();
            }

            throw;
        }

        return segments;
    }

    private void EnsureBuildNull() => _buildNull ??= _spillStore.CreateSegment("join-build-null");

    private void EnsureProbeNull() => _probeNull ??= _spillStore.CreateSegment("join-probe-null");

    private void ReserveOutput(long bytes)
    {
        if (!_memory.TryReserve(bytes))
        {
            throw new ExecutionMemoryException(
                bytes, _memory.AvailableBytes, _memory.BudgetBytes,
                "the join output chunk has no spillable representation in v1; raise the memory budget");
        }

        _outReserved += bytes;
        _metrics.ObserveReservation(_buildReserved + _outReserved);
    }

    private void ReleaseOutputReservation()
    {
        if (_outReserved > 0)
        {
            _memory.Release(_outReserved);
            _outReserved = 0;
            _metrics.ObserveRelease(_buildReserved + _outReserved);
        }
    }
}
