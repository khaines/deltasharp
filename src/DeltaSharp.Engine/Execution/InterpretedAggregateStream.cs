using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.Execution.Spill;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The pull-based <see cref="IBatchStream"/> for an <see cref="AggregateOperator"/> (STORY-03.2.2):
/// a hash group-by (or a single global group when there are no keys) that buffers every group's
/// accumulator state, then streams the result rows out in bounded chunks. It is a <b>pipeline
/// breaker</b> — the first <see cref="TryGetNext"/> drains the whole child before any row is emitted,
/// so its reservation is held until <see cref="Dispose"/>, not released per emitted batch.
/// </summary>
/// <remarks>
/// <para><b>Null / empty semantics (Spark parity).</b> <c>COUNT(*)</c> counts every row including
/// all-null rows; <c>COUNT(x)</c>/<c>SUM</c>/<c>AVG</c>/<c>MIN</c>/<c>MAX</c> skip null inputs. A group
/// whose inputs are all null (or empty) yields <c>COUNT = 0</c> and SQL <c>NULL</c> for the others. A
/// <b>global</b> aggregate over empty input still emits exactly one row (count 0, others null); a
/// <b>grouped</b> aggregate over empty input emits zero rows. <c>SUM</c>/<c>AVG</c> overflow follows
/// <see cref="AnsiMode"/> — ANSI throws <see cref="ArithmeticOverflowException"/>, Legacy nulls the
/// group.</para>
/// <para><b>Grouping.</b> Group identity is the row's canonical byte-sortable key encoding
/// (<see cref="RowKeyProjection"/>) wrapped in a <see cref="RowKey"/>; the group ordinal is discovery
/// order, and key values are gathered into the output key columns the first time a group is seen.
/// All-null keys form a single group (Spark <c>GROUP BY</c> keeps nulls).</para>
/// </remarks>
internal sealed class InterpretedAggregateStream : IBatchStream
{
    private const int OutputBatchRows = 1024;

    // The hash-partition fan-out used when the build table spills (STORY-03.6.2). A group's spill
    // partition is FNV-1a(encodedKey) mod PartitionCount, so the SAME group always lands in the SAME
    // partition across every spill round and across the final-tail flush; the emit phase then merges one
    // partition at a time, bounding recovery memory to a single partition's worth of groups. Recursive
    // re-partitioning of a partition that still overflows is deferred to the shuffle epic.
    private const int PartitionCount = 16;

    private readonly IBatchStream _input;
    private readonly OperatorMetrics _metrics;
    private readonly IExecutionMemory _memory;
    private readonly CancellationToken _cancellationToken;
    private readonly RowKeyProjection? _keyEncoder;
    private readonly ExpressionEvaluator?[] _aggInputs;
    private readonly Aggregator[] _aggregators;
    private readonly int _keyCount;
    private readonly long _stateBytesPerGroup;
    private readonly long _outputBytesPerGroup;

    private readonly ISpillStore _spillStore;
    private readonly RowSpillCodec? _keyCodec;
    private readonly SpillStateWriter _spillWriter = new();

    private readonly Dictionary<RowKey, int> _groups = new();
    private MutableColumnVector[] _keyColumns = [];
    private MutableColumnVector[] _aggColumns = [];
    private int _groupCount;
    private long _reservedBytes;

    private ISpillSegment[]? _partitions;
    private bool _spilled;
    private int _emitPartition;
    private long _mergeReserved;

    private ManagedColumnBatch? _result;
    private int _emitCursor;
    private bool _built;
    private bool _disposed;

    internal InterpretedAggregateStream(
        AggregateOperator op,
        RowKeyProjection? keyEncoder,
        ExpressionEvaluator?[] aggInputs,
        Aggregator[] aggregators,
        IBatchStream input,
        ExecutionContext context)
    {
        Schema = op.OutputSchema;
        _keyCount = op.GroupingKeys.Count;
        _keyEncoder = keyEncoder;
        _aggInputs = aggInputs;
        _aggregators = aggregators;
        _input = input;
        _metrics = op.Metrics;
        _memory = context.Memory;
        _cancellationToken = context.CancellationToken;
        _spillStore = context.SpillStore;

        long state = 0;
        foreach (Aggregator aggregator in aggregators)
        {
            state += aggregator.BytesPerGroup;
        }

        _stateBytesPerGroup = state;
        _outputBytesPerGroup = RowSizeEstimate.Bytes(op.OutputSchema);

        // The key codec serializes/recovers a group's grouping-key VALUES across a spill round so the
        // recovered group re-materializes its output key columns identically. Built only for the grouped
        // path; a global aggregate has exactly one group and never spills.
        if (_keyCount > 0)
        {
            var keyFields = new StructField[_keyCount];
            for (int k = 0; k < _keyCount; k++)
            {
                keyFields[k] = op.OutputSchema[k];
            }

            _keyCodec = new RowSpillCodec(new StructType(keyFields));
        }
    }

    /// <inheritdoc />
    public StructType Schema { get; }

    /// <inheritdoc />
    public bool TryGetNext([NotNullWhen(true)] out ColumnBatch? batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellationToken.ThrowIfCancellationRequested();

        // Lazy: the first pull drains the child and materializes the result; later pulls only slice it.
        EnsureBuilt();

        if (_spilled)
        {
            // Spilled path: emit one recovered partition's groups at a time. When the current partition's
            // result is exhausted, load and merge the next non-empty partition; false means all done.
            while (_result is null || _emitCursor >= _groupCount)
            {
                if (!LoadNextPartition())
                {
                    batch = null;
                    return false;
                }
            }
        }
        else if (_result is null || _emitCursor >= _groupCount)
        {
            batch = null;
            return false;
        }

        long start = Stopwatch.GetTimestamp();
        int length = Math.Min(OutputBatchRows, _groupCount - _emitCursor);
        ColumnBatch slice = _result.Slice(_emitCursor, length);
        _emitCursor += length;
        _metrics.AddOutput(length);
        _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(start));

        batch = slice;
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

        // A blocking operator holds its accumulator/result reservation for its whole lifetime (the
        // result rows are live until the consumer finishes draining), so the bulk release happens here.
        // MIN/MAX over string/binary also reserves its retained best value's true length; release that
        // too.
        //
        // Exactly-once release is guaranteed LOCALLY by the _disposed guard plus field-zeroing, NOT by
        // the budget ledger: under a shared memory context the ledger only catches a NET over-release
        // across the whole operator tree, so a per-operator double-release masked by a compensating leak
        // elsewhere could slip past it. The local guarantee is concrete — _disposed makes this body run
        // once, _reservedBytes is zeroed after its release, and each aggregator zeroes its own
        // retained-bytes field — so a repeated or re-entrant Dispose releases nothing a second time.
        //
        // The releases are nested in try/finally so a throw from one aggregator's Release cannot strand a
        // later aggregator's bytes, the flat _reservedBytes, or the child Dispose.
        try
        {
            ReleaseAggregators(0);
        }
        finally
        {
            try
            {
                if (_reservedBytes > 0)
                {
                    _memory.Release(_reservedBytes);
                    _reservedBytes = 0;
                }

                if (_mergeReserved > 0)
                {
                    _memory.Release(_mergeReserved);
                    _mergeReserved = 0;
                }

                _metrics.ObserveRelease(0);
            }
            finally
            {
                try
                {
                    // Dispose every spill segment — deletes the temp file for a TempFileSpillStore — on
                    // the normal, cancellation, AND failure paths (Dispose runs from the consumer's
                    // finally/catch), so a spill leaves no leaked temp files.
                    DisposePartitions();
                }
                finally
                {
                    _input.Dispose();
                }
            }
        }
    }

    // Releases every aggregator's retained variable-width reservation. Structured as nested try/finally
    // (via recursion) so a throw from one aggregator's Release cannot skip the rest.
    private void ReleaseAggregators(int index)
    {
        if (index >= _aggregators.Length)
        {
            return;
        }

        try
        {
            _aggregators[index].Release();
        }
        finally
        {
            ReleaseAggregators(index + 1);
        }
    }

    private void EnsureBuilt()
    {
        if (_built)
        {
            return;
        }

        _built = true;

        _keyColumns = new MutableColumnVector[_keyCount];
        for (int k = 0; k < _keyCount; k++)
        {
            _keyColumns[k] = ColumnVectors.Create(Schema[k].DataType, OutputBatchRows);
        }

        _aggColumns = new MutableColumnVector[_aggregators.Length];
        for (int a = 0; a < _aggregators.Length; a++)
        {
            _aggColumns[a] = ColumnVectors.Create(Schema[_keyCount + a].DataType, OutputBatchRows);
        }

        // A global (no-key) aggregate has exactly one group even over empty input. It uses no hash
        // table, so it is charged state + output only (no per-entry collection overhead).
        if (_keyEncoder is null)
        {
            Reserve(_stateBytesPerGroup + _outputBytesPerGroup);
            foreach (Aggregator aggregator in _aggregators)
            {
                aggregator.EnsureCapacity(1);
            }

            _groupCount = 1;
        }

        while (_input.TryGetNext(out ColumnBatch? input))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            long start = Stopwatch.GetTimestamp();
            _metrics.AddInputRows(input.LogicalRowCount);
            ConsumeBatch(input);

            // MIN/MAX over string/binary grows its reservation inside Accumulate (not via Reserve),
            // so observe peak after each batch to capture that growth and refresh current-reserved.
            long total = _reservedBytes + AggregatorReservedBytes();
            _metrics.ObservePeakMemory(total);
            _metrics.ObserveRelease(total);
            _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(start));
        }

        BuildResult();
    }

    private void BuildResult()
    {
        if (_spilled)
        {
            // At least one spill happened; flush the final in-memory tail so EVERY group lives in a
            // partition segment, then enter the partition-at-a-time emit phase (driven by TryGetNext).
            if (_groupCount > 0)
            {
                SpillInMemoryGroups();
            }

            _emitPartition = 0;
            return;
        }

        BuildResultCore(merge: false);
    }

    private void ConsumeBatch(ColumnBatch batch)
    {
        int rows = batch.LogicalRowCount;
        var scratch = new BatchEvaluationMemory(_memory);
        try
        {
            var inputs = new ColumnVector?[_aggregators.Length];
            for (int a = 0; a < _aggInputs.Length; a++)
            {
                inputs[a] = _aggInputs[a]?.Evaluate(batch, scratch, _cancellationToken);
            }

            ColumnVector[]? keyVectors = _keyEncoder?.Evaluate(batch, scratch, _cancellationToken);

            for (int r = 0; r < rows; r++)
            {
                CancellationPolicy.Poll(_cancellationToken, r);
                int group = keyVectors is null ? 0 : ResolveGroup(keyVectors, r);
                for (int a = 0; a < _aggregators.Length; a++)
                {
                    _aggregators[a].Accumulate(group, inputs[a], r);
                }
            }
        }
        finally
        {
            scratch.Release();
        }
    }

    private int ResolveGroup(ColumnVector[] keyVectors, int row)
    {
        // Grouping keeps null keys (they form their own group), so anyNull is ignored here.
        byte[] encoded = _keyEncoder!.Encode(keyVectors, row, out _);
        var key = new RowKey(encoded);
        if (_groups.TryGetValue(key, out int existing))
        {
            return existing;
        }

        // Reserve before mutating any state so a refusal leaves the build consistent. The hash-table
        // entry overhead (deferral (a)) is charged once per newly discovered group on top of the state,
        // output, and key bytes, so the reserved figure bounds the real peak in bytes.
        //
        // The var-width term (deferral (c), symmetric with BuildResult's agg-value charge) charges the
        // TRUE byte length of the grouping-KEY value about to be copied into the OUTPUT key columns
        // (_keyColumns) by the CopyValue loop below. This is a DISTINCT physical allocation from the
        // byte-sortable DICTIONARY key (`encoded`, charged above): `encoded` keys the _groups dictionary,
        // _keyColumns is the materialized output copy. Without this term a wide (e.g. 4 KB) string key
        // appends its full payload to _keyColumns while reserving only the flat 16-byte per-group output
        // estimate, so N distinct wide keys accumulate N×payload unreserved (the #359 data-scaled bypass).
        ReserveOrSpill(
            _stateBytesPerGroup + _outputBytesPerGroup + encoded.Length
            + RowSizeEstimate.HashTableEntryBytes + RowSizeEstimate.VariableWidthBytes(keyVectors, row));
        int group = _groupCount++;
        foreach (Aggregator aggregator in _aggregators)
        {
            aggregator.EnsureCapacity(_groupCount);
        }

        _groups[key] = group;
        for (int k = 0; k < keyVectors.Length; k++)
        {
            ColumnVector keyVector = keyVectors[k];
            if (keyVector.IsNull(row))
            {
                _keyColumns[k].AppendNull();
            }
            else
            {
                VectorMaterializer.CopyValue(_keyColumns[k], keyVector, row);
            }
        }

        return group;
    }

    private void BuildResultCore(bool merge)
    {
        for (int a = 0; a < _aggregators.Length; a++)
        {
            MutableColumnVector destination = _aggColumns[a];
            bool variableWidth = destination.Type is StringType or BinaryType;
            for (int group = 0; group < _groupCount; group++)
            {
                CancellationPolicy.Poll(_cancellationToken, group);
                _aggregators[a].Emit(group, destination);

                if (variableWidth)
                {
                    long bytes = RowSizeEstimate.VariableWidthBytes(destination, group);
                    if (merge)
                    {
                        ReserveMerge(bytes);
                    }
                    else
                    {
                        Reserve(bytes);
                    }
                }
            }
        }

        var columns = new ColumnVector[_keyColumns.Length + _aggColumns.Length];
        Array.Copy(_keyColumns, columns, _keyColumns.Length);
        Array.Copy(_aggColumns, 0, columns, _keyColumns.Length, _aggColumns.Length);
        _result = new ManagedColumnBatch(Schema, columns, _groupCount);
    }

    // The reserve seam from #155: a refused reservation now SPILLS the buffered groups (freeing budget)
    // and retries, rather than failing closed. Used only on the grouped build path.
    private void ReserveOrSpill(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        if (_memory.TryReserve(bytes))
        {
            _reservedBytes += bytes;
            _metrics.ObserveReservation(_reservedBytes + AggregatorReservedBytes());
            return;
        }

        // Refused: serialize every buffered group to its hash partition, release their memory, and retry
        // once. A lone group that still does not fit is a genuine over-budget unit — fail deterministically.
        if (_keyCodec is not null && _groupCount > 0)
        {
            SpillInMemoryGroups();
        }

        if (_memory.TryReserve(bytes))
        {
            _reservedBytes += bytes;
            _metrics.ObserveReservation(_reservedBytes + AggregatorReservedBytes());
            return;
        }

        throw new ExecutionMemoryException(
            bytes, _memory.AvailableBytes, _memory.BudgetBytes,
            "a single aggregate group exceeds the operator memory budget even after spilling all buffered "
            + "groups; raise the query/tenant memory budget");
    }

    // Serializes every in-memory group to its FNV-1a partition segment, then releases all in-memory state.
    // On a spill-store WRITE failure this throws (SpillIOException) WITHOUT having released _reservedBytes;
    // Dispose (run from the consumer's catch/finally) then releases memory exactly once and deletes temp
    // files, so the failure path is release-all + deterministic error + no partial output (AC5).
    private void SpillInMemoryGroups()
    {
        EnsureSpillPartitions();
        long spilled = 0;
        foreach (KeyValuePair<RowKey, int> pair in _groups)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            RowKey key = pair.Key;
            int group = pair.Value;

            _spillWriter.Reset();
            _spillWriter.WriteBytes(key.Bytes);
            byte[] keyFrame = _keyCodec!.Encode(_keyColumns, group);
            _spillWriter.WriteBytes(keyFrame);
            for (int a = 0; a < _aggregators.Length; a++)
            {
                _aggregators[a].WriteState(group, _spillWriter);
            }

            int partition = (int)(RowKey.Fnv1a(key.Bytes) % PartitionCount);
            _partitions![partition].Write(_spillWriter.WrittenSpan);
            spilled += _spillWriter.WrittenSpan.Length;
        }

        _metrics.AddSpilledBytes(spilled);
        // Fail closed before releasing/continuing if this spill would breach the per-query spill cap; the
        // reservations stay held so the consumer's Dispose releases them exactly once (no partial output).
        _memory.RecordSpill(spilled);
        _spilled = true;
        ReleaseInMemoryGroups();
    }

    private void EnsureSpillPartitions()
    {
        if (_partitions is not null)
        {
            return;
        }

        var partitions = new ISpillSegment[PartitionCount];
        int created = 0;
        try
        {
            for (; created < PartitionCount; created++)
            {
                partitions[created] = _spillStore.CreateSegment($"agg-p{created}");
            }
        }
        catch
        {
            for (int i = 0; i < created; i++)
            {
                partitions[i].Dispose();
            }

            throw;
        }

        _partitions = partitions;
    }

    // Frees the buffered groups after they have been written to spill segments: releases the flat
    // reservation, resets each aggregator (releasing its var-width retention), and clears the table.
    private void ReleaseInMemoryGroups()
    {
        if (_reservedBytes > 0)
        {
            _memory.Release(_reservedBytes);
            _reservedBytes = 0;
        }

        for (int a = 0; a < _aggregators.Length; a++)
        {
            _aggregators[a].Reset();
        }

        _groups.Clear();
        _groupCount = 0;
        _keyColumns = new MutableColumnVector[_keyCount];
        for (int k = 0; k < _keyCount; k++)
        {
            _keyColumns[k] = ColumnVectors.Create(Schema[k].DataType, OutputBatchRows);
        }

        _metrics.ObserveRelease(0);
    }

    // Emit-phase driver: recovers the next non-empty partition, merging its spilled partial states into a
    // fresh group table, then materializes that partition's result rows. Returns false when every
    // partition has been emitted. Memory is bounded to one partition because the prior partition's merge
    // reservation is released before the next is loaded.
    private bool LoadNextPartition()
    {
        if (_mergeReserved > 0)
        {
            _memory.Release(_mergeReserved);
            _mergeReserved = 0;
        }

        while (_emitPartition < PartitionCount)
        {
            int partition = _emitPartition++;
            ResetMergeState();
            MergePartition(partition);
            if (_groupCount > 0)
            {
                BuildResultCore(merge: true);
                _emitCursor = 0;
                return true;
            }
        }

        _result = null;
        return false;
    }

    private void ResetMergeState()
    {
        for (int a = 0; a < _aggregators.Length; a++)
        {
            _aggregators[a].Reset();
        }

        _groups.Clear();
        _groupCount = 0;

        _keyColumns = new MutableColumnVector[_keyCount];
        for (int k = 0; k < _keyCount; k++)
        {
            _keyColumns[k] = ColumnVectors.Create(Schema[k].DataType, OutputBatchRows);
        }

        _aggColumns = new MutableColumnVector[_aggregators.Length];
        for (int a = 0; a < _aggregators.Length; a++)
        {
            _aggColumns[a] = ColumnVectors.Create(Schema[_keyCount + a].DataType, OutputBatchRows);
        }
    }

    // Reads one partition's records and folds each into the merge table. A group new to this partition
    // gets a fresh default slot (then MergeState merges the spilled state into it); a group seen earlier
    // in this partition (a record from an earlier spill round) merges into the existing slot. Because the
    // same key always hashes to the same partition, ALL of a group's partials are here, so the merged
    // per-group result EQUALS the no-spill single-pass fold.
    private void MergePartition(int partition)
    {
        using ISpillSegmentReader reader = _partitions![partition].OpenRead();
        while (reader.TryRead(out byte[]? record))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stateReader = new SpillStateReader(record);
                ReadOnlySpan<byte> encodedKey = stateReader.ReadBytes();
                ReadOnlySpan<byte> keyFrame = stateReader.ReadBytes();

                var key = new RowKey(encodedKey.ToArray());
                if (!_groups.TryGetValue(key, out int group))
                {
                    ReserveMerge(
                        _stateBytesPerGroup + _outputBytesPerGroup + encodedKey.Length
                        + RowSizeEstimate.HashTableEntryBytes);
                    group = _groupCount++;
                    for (int a = 0; a < _aggregators.Length; a++)
                    {
                        _aggregators[a].EnsureCapacity(_groupCount);
                    }

                    _groups[key] = group;
                    _keyCodec!.DecodeInto(_keyColumns, keyFrame);
                }

                for (int a = 0; a < _aggregators.Length; a++)
                {
                    _aggregators[a].MergeState(group, ref stateReader);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
            {
                // The outer record framing was intact but its inner [key|keyFrame|aggstate...] layout is
                // corrupt (a structural parse ran past the record). Surface AC5's uniform typed error rather
                // than a raw index/argument exception. (ExecutionMemoryException from ReserveMerge is an
                // InvalidOperationException, so a legitimate budget refusal is NOT caught here.)
                throw new SpillIOException("read", $"aggregate spill partition {partition} (corrupt record)", ex);
            }
        }
    }

    private void ReserveMerge(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        if (!_memory.TryReserve(bytes))
        {
            throw new ExecutionMemoryException(
                bytes, _memory.AvailableBytes, _memory.BudgetBytes,
                "a single spilled aggregate partition exceeds the operator memory budget on recovery; "
                + "raise the budget (recursive re-partitioning of an over-large partition is deferred)");
        }

        _mergeReserved += bytes;
        _metrics.ObserveReservation(_mergeReserved + AggregatorReservedBytes());
    }

    private void DisposePartitions()
    {
        if (_partitions is null)
        {
            return;
        }

        ISpillSegment[] partitions = _partitions;
        _partitions = null;
        DisposeSegments(partitions, 0);
    }

    // Disposes each segment under nested try/finally (via recursion) so a throw from one Dispose cannot
    // strand a later segment's temp file.
    private static void DisposeSegments(ISpillSegment[] segments, int index)
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
            DisposeSegments(segments, index + 1);
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
                "a global (no-key) aggregate has a single unspillable group; raise the query/tenant "
                + "memory budget");
        }

        _reservedBytes += bytes;
        _metrics.ObserveReservation(_reservedBytes + AggregatorReservedBytes());
    }

    private long AggregatorReservedBytes()
    {
        long total = 0;
        foreach (Aggregator aggregator in _aggregators)
        {
            total += aggregator.ReservedBytes;
        }

        return total;
    }
}
