namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The per-execution context an <see cref="IExecutionBackend"/> threads through every operator
/// (STORY-03.1.1 AC1): cancellation and a bounded memory budget. EPIC-02 schemas, batches, and
/// expressions arrive on the <see cref="PhysicalOperator"/> itself; the context carries the
/// run-scoped controls that bound and stop work. It is immutable and shared across an operator
/// tree so cancellation and accounting are consistent end-to-end.
/// </summary>
public sealed class ExecutionContext
{
    /// <summary>Creates an execution context.</summary>
    /// <param name="memory">The bounded memory context operators reserve against.</param>
    /// <param name="cancellationToken">Cancellation observed at batch boundaries.</param>
    /// <param name="options">Backend options (e.g. force-interpreter); defaults to <see cref="ExecutionBackendOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="memory"/> is null.</exception>
    public ExecutionContext(
        IExecutionMemory memory,
        CancellationToken cancellationToken = default,
        ExecutionBackendOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        Memory = memory;
        CancellationToken = cancellationToken;
        Options = options ?? ExecutionBackendOptions.Default;
    }

    /// <summary>The bounded memory context; operators reserve before allocating and spill on refusal.</summary>
    public IExecutionMemory Memory { get; }

    /// <summary>Cancellation token; operators must observe it at bounded checkpoints and stop cleanly.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Backend selection options for this execution.</summary>
    public ExecutionBackendOptions Options { get; }
}
