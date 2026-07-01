using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The pull-based <see cref="IBatchStream"/> for a <see cref="FilterOperator"/> (STORY-03.2.1 /
/// STORY-03.4.1). It evaluates the predicate over each input batch into a <see cref="SelectionVector"/>
/// of the passing logical rows and exposes them through <see cref="ColumnBatch.WithSelection"/> —
/// <b>no value column is ever copied</b>; only "which rows survive" is materialized (ADR-0002 late
/// materialization). A boolean <see cref="ColumnReference"/> predicate reads the column directly; a
/// richer predicate is evaluated by an <see cref="ExpressionEvaluator"/> into a transient boolean
/// vector that is released before the surviving selection is emitted. A row passes iff the predicate
/// is <see langword="true"/>; null predicate values do not pass (Spark <c>WHERE</c> semantics). Output
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
    private readonly ExpressionEvaluator? _predicate;
    private readonly ArrayPool<int> _selectionPool;
    private long _reservedBytes;
    private bool _disposed;

    internal InterpretedFilterStream(
        FilterOperator op, int predicateOrdinal, IBatchStream input, ExecutionContext context, ArrayPool<int>? selectionPool = null)
    {
        Schema = op.OutputSchema;
        _predicateOrdinal = predicateOrdinal;
        _input = input;
        _metrics = op.Metrics;
        _memory = context.Memory;
        _cancellationToken = context.CancellationToken;
        _selectionPool = selectionPool ?? ArrayPool<int>.Shared;
    }

    internal InterpretedFilterStream(
        FilterOperator op, ExpressionEvaluator predicate, IBatchStream input, ExecutionContext context, ArrayPool<int>? selectionPool = null)
    {
        Schema = op.OutputSchema;
        _predicateOrdinal = -1;
        _predicate = predicate;
        _input = input;
        _metrics = op.Metrics;
        _memory = context.Memory;
        _cancellationToken = context.CancellationToken;
        _selectionPool = selectionPool ?? ArrayPool<int>.Shared;
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

            int[] rented = _selectionPool.Rent(Math.Max(logicalRows, 1));
            try
            {
                int passing = SelectPassingRows(input, rented);

                if (passing == 0)
                {
                    _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(start));
                    continue; // fully filtered — pull the next input rather than emit an empty batch
                }

                // Reserve the retained selection vector (one int per surviving row) before materializing
                // it. The SelectionVector copies the indices out of the rented span, so the scratch
                // buffer is safe to return immediately afterwards.
                Reserve((long)passing * sizeof(int));
                var selection = new SelectionVector(rented.AsSpan(0, passing));

                // Zero-copy: WithSelection shares the input's columns and composes over any prior selection.
                ColumnBatch selected = input.WithSelection(selection);
                _metrics.AddSelectedRows(passing);
                _metrics.AddOutput(passing);
                _metrics.AddElapsedNanos(InterpretedOperators.ElapsedNanos(start));

                batch = selected;
                return true;
            }
            finally
            {
                // Single return site for every path (surviving rows, fully-filtered continue, or a throw
                // from predicate evaluation / a refused reservation): the rented buffer is returned
                // exactly once, never leaked, never double-returned.
                _selectionPool.Return(rented);
            }
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
    /// count. A row passes iff its predicate value is <see langword="true"/> and not null. Indices are
    /// always logical (over <c>[0, LogicalRowCount)</c>) so <see cref="ColumnBatch.WithSelection"/>
    /// composes them over any prior selection.
    /// </summary>
    private int SelectPassingRows(ColumnBatch input, int[] buffer)
    {
        if (_predicate is not null)
        {
            return SelectByEvaluator(input, buffer);
        }

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
                    CancellationPolicy.Poll(_cancellationToken, i);
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
                    CancellationPolicy.Poll(_cancellationToken, i);
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
                CancellationPolicy.Poll(_cancellationToken, i);
                if ((!hasNulls || !view.IsNull(i)) && view.GetValue<bool>(i))
                {
                    buffer[passing++] = i;
                }
            }
        }

        return passing;
    }

    /// <summary>
    /// Evaluates a general boolean predicate into a transient vector and collects the passing logical
    /// rows. The predicate vector and its intermediates are scratch — their reservation is released
    /// before the surviving selection is built, so peak memory is bounded by the larger of the two.
    /// </summary>
    private int SelectByEvaluator(ColumnBatch input, int[] buffer)
    {
        var scratch = new BatchEvaluationMemory(_memory);
        try
        {
            ColumnVector predicate = _predicate!.Evaluate(input, scratch, _cancellationToken);
            bool hasNulls = predicate.HasNulls;
            int passing = 0;
            for (int i = 0; i < predicate.Length; i++)
            {
                CancellationPolicy.Poll(_cancellationToken, i);
                if ((!hasNulls || !predicate.IsNull(i)) && predicate.GetValue<bool>(i))
                {
                    buffer[passing++] = i;
                }
            }

            return passing;
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
                "the filter selection vector has no spillable representation in v1; raise the query/tenant memory budget");
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
    }
}
