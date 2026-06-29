namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The default, always-present execution backend (ADR-0001): batch-at-a-time vectorized
/// interpretation built from pre-existing kernels. It emits no IL, so it is fully NativeAOT-
/// and trim-clean, and it is the <b>correctness ground truth</b> that the optional
/// <see cref="CompiledBackend"/> must match.
/// </summary>
/// <remarks>
/// The M1 surface evaluates only the representative <see cref="AffineInt64Kernel"/>; the SIMD
/// kernel library and real operator evaluation arrive in later EPIC-02 stories. A single shared
/// <see cref="Instance"/> is exposed because the interpreted backend is stateless.
/// </remarks>
public sealed class InterpretedVectorizedBackend : IExecutionBackend
{
    /// <summary>The shared, stateless interpreted-backend instance.</summary>
    public static InterpretedVectorizedBackend Instance { get; } = new();

    private InterpretedVectorizedBackend()
    {
    }

    /// <inheritdoc />
    public string Name => "interpreted-vectorized";

    /// <inheritdoc />
    public bool UsesDynamicCode => false;

    /// <inheritdoc />
    public Func<long, long> BuildAffineEvaluator(AffineInt64Kernel kernel)
        => value => unchecked((kernel.Multiplier * value) + kernel.Addend);

    /// <inheritdoc />
    /// <remarks>No operator kernels ship in STORY-03.1.1; they arrive in FEAT-03.2. The contract
    /// is fail-fast, so every kind is currently unsupported rather than emulated row-at-a-time.</remarks>
    public bool Supports(OperatorKind kind) => false;

    /// <inheritdoc />
    public IBatchStream Open(PhysicalOperator op, ExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(context);
        throw new UnsupportedOperatorException(op.Kind, Name, "interpreter operator kernels arrive in FEAT-03.2");
    }
}
