namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The memory context an operator runs against (STORY-03.1.1 AC1): a bounded reservation surface
/// for the per-query/per-tenant budget, sitting in front of the EPIC-02 unified memory manager.
/// Operators reserve before allocating and release on completion, so a single unbounded query
/// cannot exhaust shared executor memory; refused reservations push an operator toward spilling.
/// </summary>
/// <remarks>
/// This is the seam contract only; the allocator and spill machinery are owned by EPIC-02 / the
/// memory model. It names no concrete allocator, so the interpreter stays AOT-clean. A backend
/// may roll <see cref="ReservedBytes"/> high-water marks into <see cref="OperatorMetrics.PeakMemoryBytes"/>.
/// </remarks>
public interface IExecutionMemory
{
    /// <summary>The reservation ceiling in bytes; <see cref="long.MaxValue"/> means effectively unbounded.</summary>
    long BudgetBytes { get; }

    /// <summary>Bytes currently reserved by this context.</summary>
    long ReservedBytes { get; }

    /// <summary>Bytes still reservable (<see cref="BudgetBytes"/> − <see cref="ReservedBytes"/>).</summary>
    long AvailableBytes { get; }

    /// <summary>
    /// Attempts to reserve <paramref name="bytes"/>. Returns <see langword="false"/> when it would
    /// exceed the budget so the caller can spill instead of failing the query.
    /// </summary>
    /// <param name="bytes">Bytes to reserve (non-negative).</param>
    /// <returns>Whether the reservation succeeded.</returns>
    bool TryReserve(long bytes);

    /// <summary>Releases <paramref name="bytes"/> previously reserved.</summary>
    /// <param name="bytes">Bytes to release (must not exceed <see cref="ReservedBytes"/>).</param>
    void Release(long bytes);

    /// <summary>
    /// The cumulative spill-bytes ceiling for the whole run (STORY-03.6.2 #156 B1): the maximum total
    /// bytes every operator combined may write to the spill store before the run fails closed.
    /// <see cref="long.MaxValue"/> means effectively unbounded. This is the disk-side bound that pairs with
    /// the in-process <see cref="BudgetBytes"/>: a query may overflow memory and spill, but only up to this
    /// cap, so a single tenant cannot fill a shared spill volume and take out co-tenants.
    /// </summary>
    long MaxSpillBytes { get; }

    /// <summary>Cumulative bytes spilled across every operator in this run so far.</summary>
    long SpilledBytes { get; }

    /// <summary>
    /// Records <paramref name="bytes"/> just written to the spill store against the cumulative spill budget.
    /// When the cumulative total would exceed <see cref="MaxSpillBytes"/> the call fails closed by throwing
    /// <see cref="Spill.SpillBudgetExceededException"/>; the operator must then release all reservations and
    /// emit no partial output (the same discipline as a spill I/O failure).
    /// </summary>
    /// <param name="bytes">The bytes just written to spill (non-negative).</param>
    /// <exception cref="Spill.SpillBudgetExceededException">The cumulative spill total would exceed the cap.</exception>
    void RecordSpill(long bytes);
}
