using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.Types;

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

    private readonly Dictionary<RowKey, int> _groups = new();
    private MutableColumnVector[] _keyColumns = [];
    private MutableColumnVector[] _aggColumns = [];
    private int _groupCount;
    private long _reservedBytes;

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

        long state = 0;
        foreach (Aggregator aggregator in aggregators)
        {
            state += aggregator.BytesPerGroup;
        }

        _stateBytesPerGroup = state;
        _outputBytesPerGroup = RowSizeEstimate.Bytes(op.OutputSchema);
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

        if (_result is null || _emitCursor >= _groupCount)
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

                _metrics.ObserveRelease(0);
            }
            finally
            {
                _input.Dispose();
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
        Reserve(_stateBytesPerGroup + _outputBytesPerGroup + encoded.Length + RowSizeEstimate.HashTableEntryBytes);
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

    private void BuildResult()
    {
        for (int a = 0; a < _aggregators.Length; a++)
        {
            MutableColumnVector destination = _aggColumns[a];
            bool variableWidth = destination.Type is StringType or BinaryType;
            for (int group = 0; group < _groupCount; group++)
            {
                _aggregators[a].Emit(group, destination);

                // Output var-width accounting (deferral (c)): the emitted MIN/MAX value is copied into
                // the output column, a real allocation the flat per-group estimate does not cover. Charge
                // its true byte length for symmetry with the input-side retention charge, so the bound
                // holds in bytes. The value's length is only known post-fold; a refusal disposes the
                // stream, releasing everything.
                if (variableWidth)
                {
                    Reserve(RowSizeEstimate.VariableWidthBytes(destination, group));
                }
            }
        }

        var columns = new ColumnVector[_keyColumns.Length + _aggColumns.Length];
        Array.Copy(_keyColumns, columns, _keyColumns.Length);
        Array.Copy(_aggColumns, 0, columns, _keyColumns.Length, _aggColumns.Length);
        _result = new ManagedColumnBatch(Schema, columns, _groupCount);
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
                "the aggregate hash table has no spillable representation in v1 (spill is STORY-03.5.x); "
                + "raise the query/tenant memory budget or add a pre-aggregation exchange");
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
