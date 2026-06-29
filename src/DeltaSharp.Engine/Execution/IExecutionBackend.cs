namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The pluggable seam through which a physical operator or expression is evaluated (ADR-0001).
/// Two implementations exist: the always-present, AOT-clean
/// <see cref="InterpretedVectorizedBackend"/> (the correctness reference) and the optional,
/// dynamic-code <see cref="CompiledBackend"/>. Callers obtain a backend from
/// <see cref="ExecutionBackends.Select()"/> and never construct one directly, so the compiled
/// tier stays elidable under NativeAOT.
/// </summary>
/// <remarks>
/// The surface has two layers: a representative scalar-fusion entry point
/// (<see cref="BuildAffineEvaluator"/>) that exercised the seam in M1, and the operator-execution
/// contract (<see cref="Supports"/> + <see cref="Open"/>) that turns a v1
/// <see cref="PhysicalOperator"/> into a pull-based <see cref="IBatchStream"/> with cancellation
/// and bounded memory. Operator <i>kernels</i> arrive in FEAT-03.2, so v1 backends declare every
/// kind unsupported; the shape is fixed now. <b>Every</b> implementation must produce results
/// identical to the interpreted backend (the ADR-0001 parity oracle).
/// </remarks>
public interface IExecutionBackend
{
    /// <summary>A short, stable identifier for diagnostics and logs (e.g. <c>interpreted-vectorized</c>).</summary>
    string Name { get; }

    /// <summary>
    /// <see langword="true"/> if this backend emits IL / uses runtime code generation (and is
    /// therefore NativeAOT-incompatible); <see langword="false"/> for the interpreted backend.
    /// </summary>
    bool UsesDynamicCode { get; }

    /// <summary>
    /// Builds a delegate that evaluates <paramref name="kernel"/>. The interpreted backend
    /// returns a closure; the compiled backend returns a JIT-compiled delegate. Both must return
    /// the same value as <see cref="AffineInt64Kernel.Evaluate"/> for every input.
    /// </summary>
    /// <param name="kernel">The affine kernel to evaluate.</param>
    /// <returns>A delegate mapping an input <see cref="long"/> to the kernel's output.</returns>
    Func<long, long> BuildAffineEvaluator(AffineInt64Kernel kernel);

    /// <summary>
    /// Whether this backend can evaluate operators of <paramref name="kind"/>. The planner asks
    /// before <see cref="Open"/> so it can pick a supported shape; a backend never silently
    /// degrades an unsupported kind to a row-at-a-time fallback (STORY-03.1.1 AC3).
    /// </summary>
    /// <param name="kind">The operator kind to test.</param>
    /// <returns><see langword="true"/> when <see cref="Open"/> can evaluate the kind.</returns>
    bool Supports(OperatorKind kind);

    /// <summary>
    /// Opens an executing <see cref="IBatchStream"/> for <paramref name="op"/>, threading
    /// cancellation and the bounded memory budget through <paramref name="context"/> and updating
    /// the operator's <see cref="PhysicalOperator.Metrics"/>. The stream emits batches conforming
    /// to <see cref="PhysicalOperator.OutputSchema"/>. Both backends must produce identical
    /// results; the compiled tier only fuses hot expressions, never changes semantics.
    /// </summary>
    /// <param name="op">The physical operator to evaluate.</param>
    /// <param name="context">Cancellation and memory context for this execution.</param>
    /// <returns>A pull-based batch stream over the operator's output.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="UnsupportedOperatorException">
    /// The operator shape is not supported; no row-at-a-time fallback is attempted.
    /// </exception>
    IBatchStream Open(PhysicalOperator op, ExecutionContext context);
}
