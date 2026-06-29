namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The default, always-present execution backend (ADR-0001): batch-at-a-time vectorized
/// interpretation built from pre-existing kernels. It emits no IL, so it is fully NativeAOT-
/// and trim-clean, and it is the <b>correctness ground truth</b> that the optional
/// <see cref="CompiledBackend"/> must match.
/// </summary>
/// <remarks>
/// The representative <see cref="AffineInt64Kernel"/> stands in for the SIMD expression-kernel
/// library (later EPIC-03 stories); operator evaluation routes through the AOT-clean
/// <see cref="InterpretedOperators"/> dispatch, which evaluates the v1 scan/filter/project shapes
/// (STORY-03.2.1) and fails fast for kinds whose kernels have not shipped. A single shared
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
    /// <remarks>v1 evaluates the scan/filter/project operator shapes (STORY-03.2.1); remaining kinds
    /// (aggregate, sort, joins, exchange) arrive in later FEAT-03.2 stories and are fail-fast until then.</remarks>
    public bool Supports(OperatorKind kind) => InterpretedOperators.Supports(kind);

    /// <inheritdoc />
    /// <remarks>Operator execution is delegated to the shared, AOT-clean <see cref="InterpretedOperators"/>
    /// dispatch; building the returned stream performs no row work (lazy), and unsupported shapes throw
    /// <see cref="UnsupportedOperatorException"/> attributed to this backend with no row-at-a-time fallback.</remarks>
    public IBatchStream Open(PhysicalOperator op, ExecutionContext context)
        => InterpretedOperators.Open(Name, op, context);
}
