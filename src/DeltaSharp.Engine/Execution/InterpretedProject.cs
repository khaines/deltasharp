using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The pull-based <see cref="IBatchStream"/> for a <see cref="ProjectOperator"/> whose projections are
/// <see cref="ColumnReference"/>s (STORY-03.2.1). It produces the output batch by selecting and
/// reordering the referenced input columns into the output schema — a <b>zero-copy</b> column
/// reorder/rename: output column <c>i</c> <i>is</i> the input column the i-th projection references
/// (shared, never copied), and an output field may rename it (an alias). Any selection on the input is
/// preserved, so the projected batch exposes the same logical rows with the same validity. Casts and
/// computed expressions need the interpreted expression evaluator (STORY-03.4.1) and are rejected here
/// rather than emulated.
/// </summary>
internal sealed class InterpretedProjectStream : IBatchStream
{
    private readonly IBatchStream _input;
    private readonly OperatorMetrics _metrics;
    private readonly IExecutionMemory _memory;
    private readonly CancellationToken _cancellationToken;
    private readonly int[] _ordinals;
    private long _reservedBytes;
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

    /// <inheritdoc />
    public StructType Schema { get; }

    /// <inheritdoc />
    public bool TryGetNext([NotNullWhen(true)] out ColumnBatch? batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellationToken.ThrowIfCancellationRequested();
        ReleaseReservation();

        if (!_input.TryGetNext(out ColumnBatch? input))
        {
            batch = null;
            return false;
        }

        long start = Stopwatch.GetTimestamp();
        int logicalRows = input.LogicalRowCount;
        _metrics.AddInputRows(logicalRows);

        // Reserve the output column-reference array. Projection is a zero-copy reorder, so this is a
        // small bookkeeping footprint (references, not value buffers) — but it keeps every operator
        // honest about reserving before allocating.
        Reserve((long)_ordinals.Length * sizeof(long));

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
