namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Options that influence <see cref="ExecutionBackends.Select(ExecutionBackendOptions)"/>.
/// </summary>
public sealed class ExecutionBackendOptions
{
    /// <summary>The default options: prefer the compiled tier when dynamic code is supported.</summary>
    public static ExecutionBackendOptions Default { get; } = new();

    /// <summary>
    /// When <see langword="true"/>, selection always returns the
    /// <see cref="InterpretedVectorizedBackend"/> even on a JIT runtime. Useful for determinism,
    /// debugging, and differential parity testing against the compiled tier.
    /// </summary>
    public bool ForceInterpreted { get; init; }
}
