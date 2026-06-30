namespace DeltaSharp.Engine.Execution.Spill;

/// <summary>
/// The deterministic, typed execution error a stateful operator raises when a spill write would push the
/// run's <b>cumulative</b> spilled bytes past the per-query spill-bytes cap
/// (<see cref="IExecutionMemory.MaxSpillBytes"/>). It restores the bounded blast-radius the memory ceiling
/// gives in process memory: a query may overflow its memory budget and spill to disk, but only up to the
/// configured spill cap — beyond that it fails closed rather than filling a shared spill volume and taking
/// out co-tenants with <c>ENOSPC</c> (Security F2).
/// </summary>
/// <remarks>
/// It derives from <see cref="InvalidOperationException"/> — matching
/// <see cref="ExecutionMemoryException"/> and <see cref="SpillIOException"/>: a refused spill reflects the
/// run-time state of the spill budget, not a bad argument. Like a spill I/O failure, the operator that
/// raises it has released <b>all</b> of its reservations (exactly once, via the over-release ledger) and
/// emitted <b>no</b> partial output, so the failure stops the query with a precise signal rather than a
/// corrupt or partial result. The request/cumulative/cap figures are carried so SRE and FinOps can
/// attribute a query that hit its spill bound.
/// </remarks>
internal sealed class SpillBudgetExceededException : InvalidOperationException
{
    /// <summary>Creates the exception describing the refused spill write.</summary>
    /// <param name="requestedBytes">The bytes the operator was about to add to the cumulative spill total.</param>
    /// <param name="cumulativeBytes">The cumulative spilled bytes after the refused write would have applied.</param>
    /// <param name="maxSpillBytes">The per-query cumulative spill-bytes ceiling.</param>
    public SpillBudgetExceededException(long requestedBytes, long cumulativeBytes, long maxSpillBytes)
        : base(BuildMessage(requestedBytes, cumulativeBytes, maxSpillBytes))
    {
        RequestedBytes = requestedBytes;
        CumulativeBytes = cumulativeBytes;
        MaxSpillBytes = maxSpillBytes;
    }

    /// <summary>The bytes the operator was about to spill when the cap was hit.</summary>
    public long RequestedBytes { get; }

    /// <summary>The cumulative spilled bytes the refused write would have reached.</summary>
    public long CumulativeBytes { get; }

    /// <summary>The per-query cumulative spill-bytes ceiling.</summary>
    public long MaxSpillBytes { get; }

    private static string BuildMessage(long requestedBytes, long cumulativeBytes, long maxSpillBytes) =>
        $"Spilling {requestedBytes} byte(s) would raise this query's cumulative spill to {cumulativeBytes} "
        + $"byte(s), exceeding the spill-bytes cap of {maxSpillBytes}; the operator released its reservations "
        + "and produced no partial output.";
}
