using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// The per-batch reservation ledger the interpreted evaluator threads through one
/// <see cref="ExpressionEvaluator.Evaluate"/> pass (STORY-03.4.1). Every materialized intermediate or
/// output column reserves its bounded footprint against the run's <see cref="IExecutionMemory"/>
/// budget <b>before</b> it is allocated, so a deeply nested expression over a large batch cannot
/// exhaust shared executor memory; the whole pass is released at once when the operator finishes with
/// the batch.
/// </summary>
/// <remarks>
/// The interpreter materializes a vector per computed node, so its reservation is proportional to the
/// expression-tree size times the batch length — bounded, but larger than the inputs. The STORY-03.4.2
/// compiled tier fuses a tree into one pass and eliminates these intermediates; this ledger keeps the
/// interpreter honest about the footprint in the meantime.
/// </remarks>
internal sealed class BatchEvaluationMemory
{
    private readonly IExecutionMemory _memory;
    private long _reservedBytes;

    /// <summary>Creates a ledger over the run's bounded memory context.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="memory"/> is null.</exception>
    public BatchEvaluationMemory(IExecutionMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        _memory = memory;
    }

    /// <summary>The bytes currently reserved by this pass.</summary>
    public long ReservedBytes => _reservedBytes;

    /// <summary>
    /// Reserves <paramref name="bytes"/> from the budget, throwing <see cref="ExecutionMemoryException"/>
    /// if it would exceed it (the interpreter has no spillable intermediate in v1).
    /// </summary>
    /// <exception cref="ExecutionMemoryException">The reservation would exceed the budget.</exception>
    public void Reserve(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        if (!_memory.TryReserve(bytes))
        {
            throw new ExecutionMemoryException(
                bytes, _memory.AvailableBytes, _memory.BudgetBytes,
                "the interpreted expression evaluator has no spillable intermediate in v1; "
                + "raise the query/tenant memory budget");
        }

        _reservedBytes += bytes;
    }

    /// <summary>Reserves the footprint of a fixed-width output vector of <paramref name="rows"/> rows for <paramref name="type"/>.</summary>
    public void ReserveVector(DataType type, int rows) => Reserve(EstimateFixedWidthBytes(type, rows));

    /// <summary>Releases every byte reserved by this pass (idempotent).</summary>
    public void Release()
    {
        if (_reservedBytes > 0)
        {
            _memory.Release(_reservedBytes);
            _reservedBytes = 0;
        }
    }

    /// <summary>
    /// The value-buffer plus validity-bitmap footprint of a fixed-width vector. Variable-width types
    /// contribute only their validity here; the value bytes are reserved by the producing evaluator,
    /// which alone knows the encoded length.
    /// </summary>
    internal static long EstimateFixedWidthBytes(DataType type, int rows)
    {
        ArgumentNullException.ThrowIfNull(type);
        long validity = (rows + 7L) / 8L;
        if (PhysicalLayoutResolver.TryResolve(type, out PhysicalLayout layout) && layout.IsFixedWidth)
        {
            return ((long)rows * layout.FixedWidthBytes) + validity;
        }

        return validity;
    }
}
