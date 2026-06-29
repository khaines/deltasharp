namespace DeltaSharp.Engine.Execution;

/// <summary>
/// A minimal counting <see cref="IExecutionMemory"/> that enforces a fixed byte budget — the
/// default context for local execution and tests. Reservations beyond the budget are refused
/// (returning <see langword="false"/>) so operators spill rather than fail. It tracks bytes only;
/// the actual allocation/spill machinery is the EPIC-02 memory model's. Use
/// <see cref="Unbounded"/> when no limit applies.
/// </summary>
public sealed class BoundedExecutionMemory : IExecutionMemory
{
    private long _reserved;

    /// <summary>Creates a context with the given byte budget.</summary>
    /// <param name="budgetBytes">The reservation ceiling; use <see cref="long.MaxValue"/> for unbounded.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="budgetBytes"/> is negative.</exception>
    public BoundedExecutionMemory(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        BudgetBytes = budgetBytes;
    }

    /// <summary>An effectively unbounded context.</summary>
    public static BoundedExecutionMemory Unbounded { get; } = new(long.MaxValue);

    /// <inheritdoc />
    public long BudgetBytes { get; }

    /// <inheritdoc />
    public long ReservedBytes => Interlocked.Read(ref _reserved);

    /// <inheritdoc />
    public long AvailableBytes => BudgetBytes - ReservedBytes;

    /// <inheritdoc />
    public bool TryReserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        long updated = Interlocked.Add(ref _reserved, bytes);
        // `updated < bytes` detects 64-bit overflow (wrap to negative) so a huge request can't
        // bypass the budget by wrapping past BudgetBytes; treat it as over-budget and roll back.
        if (updated > BudgetBytes || updated < bytes)
        {
            Interlocked.Add(ref _reserved, -bytes);
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public void Release(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes > ReservedBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "Cannot release more than is reserved.");
        }

        Interlocked.Add(ref _reserved, -bytes);
    }
}
