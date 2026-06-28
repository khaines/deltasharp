using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Chooses the execution backend (ADR-0001). The interpreted vectorized backend is always
/// available; the optional <see cref="CompiledBackend"/> is selected only when the runtime
/// supports dynamic code, gated through a single feature-guarded property so the trimmer and
/// NativeAOT compiler can prove the compiled tier unreachable and eliminate it.
/// </summary>
public static class ExecutionBackends
{
    /// <summary>
    /// Whether the dynamic-code <see cref="CompiledBackend"/> may be activated. This is the
    /// <b>sole</b> guard for the compiled tier: annotating it
    /// <see cref="FeatureGuardAttribute"/> for <see cref="RequiresDynamicCodeAttribute"/> tells
    /// the trim/AOT analyzers that any branch it guards is unreachable when dynamic code is
    /// unsupported, so <c>CreateCompiledBackend</c>, <see cref="CompiledBackend"/>, and the
    /// <c>Expression.Compile</c> call it reaches are dead-code-eliminated from NativeAOT
    /// publishes without an IL3050 warning. It forwards
    /// <see cref="RuntimeFeature.IsDynamicCodeSupported"/>, which NativeAOT hard-wires to
    /// <see langword="false"/>.
    /// </summary>
    [FeatureGuard(typeof(RequiresDynamicCodeAttribute))]
    internal static bool IsCompiledBackendAvailable => RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>Selects a backend using <see cref="ExecutionBackendOptions.Default"/>.</summary>
    /// <returns>
    /// The <see cref="CompiledBackend"/> on a dynamic-code-capable runtime; otherwise the
    /// always-available <see cref="InterpretedVectorizedBackend"/>.
    /// </returns>
    public static IExecutionBackend Select() => Select(ExecutionBackendOptions.Default);

    /// <summary>Selects a backend honouring <paramref name="options"/>.</summary>
    /// <param name="options">Selection options; <see cref="ExecutionBackendOptions.ForceInterpreted"/> pins the interpreted backend.</param>
    /// <returns>
    /// The <see cref="InterpretedVectorizedBackend"/> when interpretation is forced or dynamic
    /// code is unsupported; otherwise the <see cref="CompiledBackend"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static IExecutionBackend Select(ExecutionBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ForceInterpreted)
        {
            return InterpretedVectorizedBackend.Instance;
        }

        // The IsCompiledBackendAvailable feature guard makes this the only place the compiled
        // tier is reached; NativeAOT removes the whole branch because the guard is false there.
        if (IsCompiledBackendAvailable)
        {
            return CreateCompiledBackend();
        }

        return InterpretedVectorizedBackend.Instance;
    }

    [RequiresDynamicCode(
        "Constructs the IL-emitting compiled backend (ADR-0001); callers MUST guard the call " +
        "with IsCompiledBackendAvailable so NativeAOT elides this path.")]
    private static IExecutionBackend CreateCompiledBackend() => new CompiledBackend();
}
