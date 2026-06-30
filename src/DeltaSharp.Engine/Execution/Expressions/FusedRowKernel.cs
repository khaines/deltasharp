using DeltaSharp.Engine.Columnar;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// The signature of a JIT-compiled, fused per-row kernel produced by
/// <see cref="CompiledExpressionLowering"/> (STORY-03.4.2). One delegate evaluates an entire
/// <see cref="PhysicalExpression"/> tree for a single logical row and appends exactly one lane
/// (value or null) to <paramref name="output"/>, with <b>no</b> per-node intermediate vectors —
/// that intermediate elimination is the fusion win over the vector-at-a-time interpreted tier.
/// </summary>
/// <param name="inputs">
/// The referenced input columns, one slot per distinct <see cref="ColumnReference.Ordinal"/> in
/// first-encounter order (see <see cref="CompiledExpressionEvaluator"/>). Each slot is the batch's
/// logical (selection-applied) view, so <paramref name="row"/> indexes it directly.
/// </param>
/// <param name="row">The logical row index in <c>[0, ColumnBatch.LogicalRowCount)</c>.</param>
/// <param name="output">The result vector the kernel appends one lane to, in logical row order.</param>
internal delegate void FusedRowKernel(ColumnVector[] inputs, int row, MutableColumnVector output);
