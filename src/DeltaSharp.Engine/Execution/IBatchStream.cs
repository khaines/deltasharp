using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// A pull-based stream of equal-schema <see cref="ColumnBatch"/>es — the typed output contract of
/// an executing <see cref="PhysicalOperator"/> (STORY-03.1.1 AC2). Consumers drive it batch-at-a-
/// time; backpressure is the consumer simply not pulling. Every returned batch matches
/// <see cref="Schema"/>; <see cref="TryGetNext"/> returns <see langword="false"/> at end of input.
/// Cancellation flows through the <see cref="ExecutionContext"/> the stream was opened with, so it
/// is observed at batch boundaries, then the stream is disposed to release owned buffers.
/// </summary>
public interface IBatchStream : IDisposable
{
    /// <summary>The schema every emitted batch conforms to (equals the operator's output schema).</summary>
    StructType Schema { get; }

    /// <summary>
    /// Pulls the next batch. The producer evaluates only enough work for one batch, keeping the
    /// pipeline streaming and never materializing the whole result.
    /// </summary>
    /// <param name="batch">The next batch when this returns <see langword="true"/>; otherwise null.</param>
    /// <returns><see langword="true"/> if a batch was produced; <see langword="false"/> at end of stream.</returns>
    /// <exception cref="OperationCanceledException">The execution token was cancelled.</exception>
    bool TryGetNext([NotNullWhen(true)] out ColumnBatch? batch);
}
