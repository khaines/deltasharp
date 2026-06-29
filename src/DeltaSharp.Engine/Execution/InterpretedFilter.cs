using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The pull-based <see cref="IBatchStream"/> for a <see cref="FilterOperator"/> whose predicate is a
/// boolean <see cref="ColumnReference"/> (STORY-03.2.1). It evaluates the predicate over each input
/// batch into a <see cref="SelectionVector"/> of the passing logical rows and exposes them through
/// <see cref="ColumnBatch.WithSelection"/> — <b>no value column is ever copied</b>; only "which rows
/// survive" is materialized (ADR-0002 late materialization). A row passes iff the predicate is
/// <see langword="true"/>; null predicate values do not pass (Spark <c>WHERE</c> semantics). Output
/// schema equals input schema. Fully-filtered batches are dropped (the stream pulls the next input
/// rather than emitting an empty batch).
/// </summary>
internal sealed class InterpretedFilterStream : IBatchStream
{
    private readonly IBatchStream _input;
    private readonly OperatorMetrics _metrics;
    private readonly IExecutionMemory _memory;
    private readonly CancellationToken _cancellationToken;
    private readonly int _predicateOrdinal;
    private long _reservedBytes;
    private bool _disposed;

    internal InterpretedFilterStream(FilterOperator op, int predicateOrdinal, IBatchStream input, ExecutionContext context)
    {
        Schema = op.OutputSchema;
        _predicateOrdinal = predicateOrdinal;
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

        // Release the previous emitted batch's selection-vector reservation: a streaming operator
        // accounts for at most one in-flight batch, so the bound is per-batch, not per-stream.
        ReleaseReservation();

        while (_input.TryGetNext(out ColumnBatch? input))
        {
            _cancellationToken.ThrowIfCancellationRequested();

            // Start timing after the child pull so each operator's ElapsedNanos reflects only its own
            // work, never its children's.
            long start = Stopwatch.GetTimestamp();
            int logicalRows = input.LogicalRowCount;
            _metrics.AddInputRows(logicalRows);

            int[] rented = ArrayPool<int>.Shared.Rent(Math.Max(logicalRows, 1));
            int passing = SelectPassingRows(input, rented);

            if (passing == 0)
            {
                ArrayPool<int>.Shared.Return(rented);
                _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(start));
                continue; // fully filtered — pull the next input rather than emit an empty batch
            }

            // Reserve the retained selection vector (one int per surviving row) before materializing
            // it; the rented scratch buffer is always returned, even if the reservation is refused.
            SelectionVector selection;
            try
            {
                Reserve((long)passing * sizeof(int));
                selection = new SelectionVector(rented.AsSpan(0, passing));
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rented);
            }

            // Zero-copy: WithSelection shares the input's columns and composes over any prior selection.
            ColumnBatch selected = input.WithSelection(selection);
            _metrics.AddSelectedRows(passing);
            _metrics.AddOutput(passing);
            _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(start));

            batch = selected;
            return true;
        }

        batch = null;
        return false;
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

    /// <summary>
    /// Fills <paramref name="buffer"/> with the indices of the passing logical rows and returns the
    /// count. The no-selection path reads the boolean value span directly (no gather); the
    /// selection-aware path gathers through a logical view. A row passes iff its predicate value is
    /// <see langword="true"/> and not null.
    /// </summary>
    private int SelectPassingRows(ColumnBatch input, int[] buffer)
    {
        int passing = 0;

        if (input.Selection is null)
        {
            // Contiguous fast path: the predicate column is the boolean vector directly (v1 booleans
            // are managed 1-byte vectors — Arrow bit-packed booleans are unsupported), so its value
            // span is byte-addressable.
            ColumnVector column = input.Column(_predicateOrdinal);
            ReadOnlySpan<bool> values = column.GetValues<bool>();
            if (column.HasNulls)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] && !column.IsNull(i))
                    {
                        buffer[passing++] = i;
                    }
                }
            }
            else
            {
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i])
                    {
                        buffer[passing++] = i;
                    }
                }
            }
        }
        else
        {
            // Selection-aware gather: enumerate the current logical rows through the selected view.
            ColumnVector view = input.SelectedColumn(_predicateOrdinal);
            bool hasNulls = view.HasNulls;
            for (int i = 0; i < view.Length; i++)
            {
                if ((!hasNulls || !view.IsNull(i)) && view.GetValue<bool>(i))
                {
                    buffer[passing++] = i;
                }
            }
        }

        return passing;
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
                "the filter selection vector has no spillable representation in v1; raise the query/tenant memory budget");
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
