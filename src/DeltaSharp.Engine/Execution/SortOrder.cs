namespace DeltaSharp.Engine.Execution;

/// <summary>Sort ordering direction for a <see cref="SortOrder"/>.</summary>
public enum SortDirection
{
    /// <summary>Ascending order.</summary>
    Ascending,

    /// <summary>Descending order.</summary>
    Descending,
}

/// <summary>Where nulls sort relative to non-null values, mirroring Spark's NULLS FIRST/LAST.</summary>
public enum NullOrdering
{
    /// <summary>Nulls sort before non-null values.</summary>
    NullsFirst,

    /// <summary>Nulls sort after non-null values.</summary>
    NullsLast,
}

/// <summary>
/// One key of a <see cref="SortOperator"/> specification: an <see cref="PhysicalExpression"/> plus
/// its <see cref="Direction"/> and <see cref="NullOrdering"/>. The comparator semantics for nulls,
/// NaN, decimals, and timestamps are defined by the sort operator contract (checklist 21).
/// </summary>
public sealed class SortOrder
{
    /// <summary>Creates a sort key.</summary>
    /// <param name="expression">The key expression.</param>
    /// <param name="direction">Ascending or descending; defaults to ascending.</param>
    /// <param name="nullOrdering">Where nulls sort; defaults to nulls-first (Spark's ascending default).</param>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is null.</exception>
    public SortOrder(
        PhysicalExpression expression,
        SortDirection direction = SortDirection.Ascending,
        NullOrdering nullOrdering = NullOrdering.NullsFirst)
    {
        ArgumentNullException.ThrowIfNull(expression);
        Expression = expression;
        Direction = direction;
        NullOrdering = nullOrdering;
    }

    /// <summary>The key expression to sort on.</summary>
    public PhysicalExpression Expression { get; }

    /// <summary>The sort direction.</summary>
    public SortDirection Direction { get; }

    /// <summary>Null placement relative to non-null values.</summary>
    public NullOrdering NullOrdering { get; }
}
