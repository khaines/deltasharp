namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Thrown when a vectorized operator cannot reserve the memory it needs from the run's
/// <see cref="IExecutionMemory"/> budget and has <b>no spillable state to fall back on</b>. It is
/// the v1 enforcement of the per-query/per-tenant memory bound: the contract (memory-model) calls
/// for refused reservations to push an operator toward spilling, but the scan/filter/project
/// operators that land first have nothing to spill, so they fail fast with this precise, typed
/// signal rather than silently exceeding the budget. Spill machinery (and turning this into a
/// spill rather than a stop) arrives with the EPIC-02 memory manager.
/// </summary>
/// <remarks>
/// It derives from <see cref="InvalidOperationException"/> — a refused reservation reflects the
/// run-time state of the budget, not a bad argument — and carries the request/availability figures
/// so SRE and FinOps can attribute a query that hit its bound.
/// </remarks>
public sealed class ExecutionMemoryException : InvalidOperationException
{
    /// <summary>Creates the exception describing the refused reservation.</summary>
    /// <param name="requestedBytes">The bytes the operator tried to reserve.</param>
    /// <param name="availableBytes">The bytes still reservable when the request was refused.</param>
    /// <param name="budgetBytes">The total reservation ceiling.</param>
    /// <param name="detail">Optional operator-specific context (e.g. why there is nothing to spill).</param>
    public ExecutionMemoryException(long requestedBytes, long availableBytes, long budgetBytes, string? detail = null)
        : base(BuildMessage(requestedBytes, availableBytes, budgetBytes, detail))
    {
        RequestedBytes = requestedBytes;
        AvailableBytes = availableBytes;
        BudgetBytes = budgetBytes;
    }

    /// <summary>The bytes the operator tried to reserve.</summary>
    public long RequestedBytes { get; }

    /// <summary>The bytes still reservable when the request was refused.</summary>
    public long AvailableBytes { get; }

    /// <summary>The total reservation ceiling of the budget.</summary>
    public long BudgetBytes { get; }

    private static string BuildMessage(long requestedBytes, long availableBytes, long budgetBytes, string? detail)
    {
        string suffix = string.IsNullOrEmpty(detail) ? string.Empty : $" {detail}.";
        return $"Could not reserve {requestedBytes} byte(s) from the execution memory budget "
            + $"({availableBytes} of {budgetBytes} byte(s) available).{suffix}";
    }
}
