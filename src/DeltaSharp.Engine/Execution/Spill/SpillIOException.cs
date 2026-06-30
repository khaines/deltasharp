namespace DeltaSharp.Engine.Execution.Spill;

/// <summary>
/// The deterministic, typed execution error a stateful operator raises when spill I/O fails
/// (STORY-03.6.2 AC5). Unlike <see cref="ExecutionMemoryException"/> (a budget refusal with spillable
/// state still recoverable), this signals that the spill <i>medium</i> failed — a write/read error or a
/// truncated/corrupt segment — after which the operator has released <b>all</b> of its reservations and
/// emitted <b>no</b> partial output. It is the clean-failure contract: a spill failure stops the query
/// with a precise signal rather than corrupting results or partially succeeding.
/// </summary>
/// <remarks>
/// It derives from <see cref="InvalidOperationException"/> (matching <see cref="ExecutionMemoryException"/>):
/// a spill-medium failure is a run-time condition, not a bad argument. The original I/O fault is carried
/// as <see cref="Exception.InnerException"/> so SRE can attribute the underlying cause.
/// </remarks>
internal sealed class SpillIOException : InvalidOperationException
{
    /// <summary>Creates the exception describing a failed spill operation.</summary>
    /// <param name="operation">The spill operation that failed (e.g. <c>"write"</c>, <c>"read"</c>, <c>"open"</c>).</param>
    /// <param name="detail">Operator-specific context (e.g. <c>"aggregate partition 3"</c>).</param>
    /// <param name="innerException">The underlying I/O fault, when one exists.</param>
    public SpillIOException(string operation, string detail, Exception? innerException = null)
        : base($"Spill {operation} failed for {detail}; the operator released its reservations and produced no partial output.", innerException)
    {
        Operation = operation;
        Detail = detail;
    }

    /// <summary>The spill operation that failed.</summary>
    public string Operation { get; }

    /// <summary>Operator-specific context for the failure.</summary>
    public string Detail { get; }
}
