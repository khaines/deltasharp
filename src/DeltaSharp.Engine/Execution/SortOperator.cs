namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Reorders rows of its single input by a non-empty list of <see cref="SortOrders"/>. Output
/// schema equals input schema. The comparator's null/NaN/decimal/timestamp behaviour and whether
/// ordering is global or partition-local are fixed by the operator contract (checklist 21) and
/// implemented over EPIC-02 binary sort keys; this node only declares the spec.
/// </summary>
public sealed class SortOperator : PhysicalOperator
{
    private readonly PhysicalOperator[] _children;
    private readonly SortOrder[] _sortOrders;

    /// <summary>Creates a sort over <paramref name="input"/> using <paramref name="sortOrders"/>.</summary>
    /// <param name="input">The input operator.</param>
    /// <param name="sortOrders">At least one sort key.</param>
    /// <param name="global">Whether ordering is global (default) or partition-local.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sortOrders"/> is empty.</exception>
    public SortOperator(PhysicalOperator input, IReadOnlyList<SortOrder> sortOrders, bool global = true)
        : base((input ?? throw new ArgumentNullException(nameof(input))).OutputSchema)
    {
        ArgumentNullException.ThrowIfNull(sortOrders);
        if (sortOrders.Count == 0)
        {
            throw new ArgumentException("Sort requires at least one sort order.", nameof(sortOrders));
        }

        _children = [input];
        _sortOrders = [.. sortOrders];
        Global = global;
    }

    /// <inheritdoc />
    public override OperatorKind Kind => OperatorKind.Sort;

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalOperator> Children => _children;

    /// <summary>The ordered sort keys.</summary>
    public IReadOnlyList<SortOrder> SortOrders => _sortOrders;

    /// <summary>Whether the ordering is global across partitions; otherwise partition-local.</summary>
    public bool Global { get; }
}
