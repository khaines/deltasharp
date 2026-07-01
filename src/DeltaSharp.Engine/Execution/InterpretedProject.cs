using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The pull-based <see cref="IBatchStream"/> for a <see cref="ProjectOperator"/> (STORY-03.2.1 /
/// STORY-03.4.1). When every projection is a <see cref="ColumnReference"/> it produces the output by
/// selecting and reordering the referenced input columns — a <b>zero-copy</b> reorder/rename where
/// output column <c>i</c> <i>is</i> the input column the i-th projection references (shared, never
/// copied) and any input selection is preserved. When any projection is computed, it emits a
/// <b>selection-free</b> batch of <see cref="ColumnBatch.LogicalRowCount"/> rows whose columns are all
/// contiguous and logical-row aligned: computed columns are materialized by an
/// <see cref="ExpressionEvaluator"/>, and column-reference columns are shared when the input has no
/// selection or gathered into a fresh contiguous vector when it does — so downstream operators keep
/// their <see cref="ColumnVector.GetValues{T}"/> fast path.
/// </summary>
internal sealed class InterpretedProjectStream : IBatchStream
{
    private readonly IBatchStream _input;
    private readonly OperatorMetrics _metrics;
    private readonly IExecutionMemory _memory;
    private readonly CancellationToken _cancellationToken;
    private readonly int[]? _ordinals;
    private readonly ProjectionPlan[]? _plans;
    private long _reservedBytes;
    private BatchEvaluationMemory? _evalMemory;
    private bool _disposed;

    internal InterpretedProjectStream(ProjectOperator op, int[] ordinals, IBatchStream input, ExecutionContext context)
    {
        Schema = op.OutputSchema;
        _ordinals = ordinals;
        _input = input;
        _metrics = op.Metrics;
        _memory = context.Memory;
        _cancellationToken = context.CancellationToken;
    }

    internal InterpretedProjectStream(ProjectOperator op, ProjectionPlan[] plans, IBatchStream input, ExecutionContext context)
    {
        Schema = op.OutputSchema;
        _plans = plans;
        _input = input;
        _metrics = op.Metrics;
        _memory = context.Memory;
        _cancellationToken = context.CancellationToken;
    }

    /// <inheritdoc />
    public StructType Schema { get; }

    /// <inheritdoc />
    public bool TryGetNext([NotNullWhen(true)] out ColumnBatch? batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellationToken.ThrowIfCancellationRequested();

        // Release the previous emitted batch's reservations: a streaming operator accounts for at most
        // one in-flight batch, so the bound is per-batch, not per-stream.
        ReleaseReservation();

        if (!_input.TryGetNext(out ColumnBatch? input))
        {
            batch = null;
            return false;
        }

        long start = Stopwatch.GetTimestamp();
        int logicalRows = input.LogicalRowCount;
        _metrics.AddInputRows(logicalRows);

        ColumnBatch projected = _plans is null ? ProjectZeroCopy(input) : ProjectComputed(input);

        _metrics.AddOutput(logicalRows);
        _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(start));

        batch = projected;
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

    private ColumnBatch ProjectZeroCopy(ColumnBatch input)
    {
        // Reserve the output column-reference array. Projection is a zero-copy reorder, so this is a
        // small bookkeeping footprint (references, not value buffers) — but it keeps every operator
        // honest about reserving before allocating.
        Reserve((long)_ordinals!.Length * sizeof(long));

        var columns = new ColumnVector[_ordinals.Length];
        for (int i = 0; i < _ordinals.Length; i++)
        {
            // Physical column (length == input.RowCount); the input's selection is re-applied below so
            // the projected batch presents the same logical rows.
            columns[i] = input.Column(_ordinals[i]);
        }

        ColumnBatch projected = new ManagedColumnBatch(Schema, columns, input.RowCount);
        if (input.Selection is { } selection)
        {
            projected = projected.WithSelection(selection);
        }

        return projected;
    }

    private ColumnBatch ProjectComputed(ColumnBatch input)
    {
        int rows = input.LogicalRowCount;

        // Hold the pass's reservation in a field up front so a refused reservation mid-build (or a
        // throw from an evaluator) is still released by Dispose / the next pull.
        var memory = new BatchEvaluationMemory(_memory);
        _evalMemory = memory;

        bool hasSelection = input.Selection is not null;
        var columns = new ColumnVector[_plans!.Length];
        for (int i = 0; i < _plans.Length; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            ProjectionPlan plan = _plans[i];
            if (plan.Evaluator is { } evaluator)
            {
                columns[i] = evaluator.Evaluate(input, memory, _cancellationToken);
            }
            else if (!hasSelection)
            {
                // No selection: the physical column already presents the logical rows in order (length
                // == RowCount == logical rows), so share it zero-copy.
                columns[i] = input.Column(plan.Ordinal);
            }
            else
            {
                // Selection present: gather the selected logical rows into a fresh contiguous vector so
                // the emitted batch carries no selection and stays GetValues-capable downstream.
                ColumnVector view = input.SelectedColumn(plan.Ordinal);
                ReserveGather(memory, view, rows);
                columns[i] = VectorMaterializer.Materialize(view, rows);
            }
        }

        // The computed batch is selection-free: every column is contiguous and length == logical rows.
        return new ManagedColumnBatch(Schema, columns, rows);
    }

    /// <summary>
    /// Reserves the footprint a gathered column-reference materialization will allocate. The fixed-width
    /// footprint (value buffer + validity) is the whole reservation for primitives; a variable-width
    /// column (<see cref="StringType"/>/<see cref="BinaryType"/>) additionally allocates an offset per
    /// row plus the gathered value bytes, which <see cref="BatchEvaluationMemory.ReserveVector"/> does
    /// NOT cover — for a var-width type it counts only the validity footprint. The selected rows' encoded
    /// length is known here, so meter it too (exactly as <c>LiteralEvaluator</c> reserves a var-width
    /// broadcast) and keep the reservation honest with what <see cref="VectorMaterializer.Materialize"/>
    /// allocates; otherwise a wide string/binary projection under a selection drains shared executor
    /// memory unmetered past the tenant budget.
    /// </summary>
    private static void ReserveGather(BatchEvaluationMemory memory, ColumnVector view, int rows)
    {
        memory.ReserveVector(view.Type, rows);
        if (view.Type is StringType or BinaryType)
        {
            long valueBytes = 0;
            for (int i = 0; i < rows; i++)
            {
                if (!view.IsNull(i))
                {
                    valueBytes += view.GetBytes(i).Length;
                }
            }

            memory.Reserve(((long)rows * sizeof(int)) + valueBytes);
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
                "the projection output batch has no spillable representation in v1; raise the query/tenant memory budget");
        }

        _reservedBytes += bytes;
        _metrics.ObserveReservation(_reservedBytes);
    }

    private void ReleaseReservation()
    {
        if (_reservedBytes > 0)
        {
            _memory.Release(_reservedBytes);
            _reservedBytes = 0;
            _metrics.ObserveRelease(_reservedBytes);
        }

        if (_evalMemory is not null)
        {
            _evalMemory.Release();
            _evalMemory = null;
        }
    }
}
