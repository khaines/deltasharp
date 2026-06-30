using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// The compiled counterpart of the interpreted <see cref="ExpressionEvaluator"/> tree (STORY-03.4.2,
/// ADR-0001 optional codegen tier). It evaluates an entire fixed-width <see cref="PhysicalExpression"/>
/// tree with a single fused, JIT-compiled <see cref="FusedRowKernel"/> instead of a vector-at-a-time
/// kernel per node — eliminating the per-node intermediate vectors. Because it derives from the same
/// <see cref="ExpressionEvaluator"/> base, it is a drop-in replacement that produces a vector of exactly
/// <see cref="ColumnBatch.LogicalRowCount"/> rows in logical order, carrying identical values and
/// validity to the interpreter (the parity oracle).
/// </summary>
/// <remarks>
/// Annotated <see cref="RequiresDynamicCodeAttribute"/> (the kernel is JIT-compiled); built only behind
/// the dynamic-code feature guard and elided from NativeAOT. The driver gathers each referenced column
/// once per batch via <see cref="ColumnBatch.SelectedColumn"/> (selection-aware, zero-copy) and reserves
/// only the <i>output</i> footprint — the fused kernel allocates no intermediates, so unlike the
/// interpreter it reserves no per-node temporaries.
/// </remarks>
[RequiresDynamicCode(
    "Evaluates a kernel produced by Expression.Compile (ADR-0001 optional codegen tier); reachable only " +
    "behind the IsCompiledBackendAvailable feature guard and elided from NativeAOT.")]
internal sealed class CompiledExpressionEvaluator : ExpressionEvaluator
{
    private readonly FusedRowKernel _kernel;
    private readonly int[] _slotOrdinals;

    public CompiledExpressionEvaluator(DataType type, bool nullable, CompiledFusion fusion)
        : base(type, nullable)
    {
        _kernel = fusion.Kernel;
        _slotOrdinals = fusion.SlotOrdinals;
    }

    /// <inheritdoc />
    public override ColumnVector Evaluate(ColumnBatch batch, BatchEvaluationMemory memory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(memory);
        int rows = batch.LogicalRowCount;

        ColumnVector[] inputs = _slotOrdinals.Length == 0
            ? Array.Empty<ColumnVector>()
            : new ColumnVector[_slotOrdinals.Length];
        for (int slot = 0; slot < _slotOrdinals.Length; slot++)
        {
            inputs[slot] = batch.SelectedColumn(_slotOrdinals[slot]);
        }

        memory.ReserveVector(Type, rows);
        MutableColumnVector result = ColumnVectors.Create(Type, rows);
        for (int row = 0; row < rows; row++)
        {
            _kernel(inputs, row, result);
        }

        return result;
    }
}
