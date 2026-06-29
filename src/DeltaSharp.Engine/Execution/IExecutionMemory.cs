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
}
