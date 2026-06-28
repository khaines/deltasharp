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
/// The surface is intentionally minimal for M1: a single representative scalar-fusion entry
/// point (<see cref="BuildAffineEvaluator"/>) that lets the seam — and the ADR-0001 parity
/// oracle — be exercised before the general expression / operator model lands in later EPIC-02
/// stories. New evaluation entry points are added here as the engine grows; <b>every</b>
/// implementation must produce results identical to the interpreted backend.
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
}
