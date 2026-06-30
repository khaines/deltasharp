namespace DeltaSharp.Engine.RowFormat;

/// <summary>
/// The direction a byte-sortable sort key orders non-null values, mirroring Spark's
/// <c>ASC</c>/<c>DESC</c>.
/// </summary>
/// <remarks>
/// This is the row-format primitive the execution layer's <c>SortDirection</c> maps onto: the
/// binary-row layer (<c>src/DeltaSharp.Engine/RowFormat/</c>) owns the order-preserving byte
/// encoding, and the query-execution sort/join/exchange operators translate their logical
/// <c>SortOrder</c> into a <see cref="SortKeyOrdering"/> at the boundary. Keeping the direction
/// here keeps the row format self-contained and free of an execution-layer dependency.
/// </remarks>
public enum SortKeyDirection
{
    /// <summary>Smaller values sort first (Spark <c>ASC</c>).</summary>
    Ascending,

    /// <summary>Larger values sort first (Spark <c>DESC</c>).</summary>
    Descending,
}

/// <summary>
/// Where SQL <c>NULL</c> sorts relative to non-null values, mirroring Spark's
/// <c>NULLS FIRST</c>/<c>NULLS LAST</c>. Independent of <see cref="SortKeyDirection"/> — Spark lets
/// a key be, for example, <c>DESC NULLS FIRST</c>.
/// </summary>
public enum NullSortOrder
{
    /// <summary>Nulls sort before every non-null value (Spark's <c>ASC</c> default).</summary>
    NullsFirst,

    /// <summary>Nulls sort after every non-null value (Spark's <c>DESC</c> default).</summary>
    NullsLast,
}

/// <summary>
/// One sort key's ordering: its <see cref="Direction"/> and where nulls go
/// (<see cref="NullOrder"/>). Combined, these drive the order-preserving byte encoding
/// (<see cref="SortKeyEncoder"/>) and the scalar comparator oracle (<see cref="RowOrderingComparer"/>)
/// so a <c>memcmp</c> of the encoding always matches the comparator.
/// </summary>
/// <remarks>
/// The Spark defaults are encoded in <see cref="ForDirection"/>: <c>ASC</c> defaults to
/// <see cref="NullSortOrder.NullsFirst"/> and <c>DESC</c> to <see cref="NullSortOrder.NullsLast"/>.
/// Either default can be overridden by constructing the struct explicitly.
/// </remarks>
public readonly struct SortKeyOrdering : IEquatable<SortKeyOrdering>
{
    /// <summary>Creates an ordering from an explicit direction and null placement.</summary>
    /// <param name="direction">Ascending or descending.</param>
    /// <param name="nullOrder">Where nulls sort.</param>
    public SortKeyOrdering(SortKeyDirection direction, NullSortOrder nullOrder)
    {
        Direction = direction;
        NullOrder = nullOrder;
    }

    /// <summary>The non-null value direction.</summary>
    public SortKeyDirection Direction { get; }

    /// <summary>Where nulls sort relative to non-null values.</summary>
    public NullSortOrder NullOrder { get; }

    /// <summary>Whether non-null values sort descending.</summary>
    public bool IsDescending => Direction == SortKeyDirection.Descending;

    /// <summary>Whether nulls sort before non-null values.</summary>
    public bool NullsFirst => NullOrder == NullSortOrder.NullsFirst;

    /// <summary>Ascending with nulls first — the Spark <c>ASC</c> default.</summary>
    public static SortKeyOrdering Ascending { get; } =
        new(SortKeyDirection.Ascending, NullSortOrder.NullsFirst);

    /// <summary>Descending with nulls last — the Spark <c>DESC</c> default.</summary>
    public static SortKeyOrdering Descending { get; } =
        new(SortKeyDirection.Descending, NullSortOrder.NullsLast);

    /// <summary>
    /// The Spark default ordering for <paramref name="direction"/>: <c>ASC</c> ⇒ nulls first,
    /// <c>DESC</c> ⇒ nulls last.
    /// </summary>
    public static SortKeyOrdering ForDirection(SortKeyDirection direction) =>
        direction == SortKeyDirection.Ascending ? Ascending : Descending;

    /// <inheritdoc/>
    public bool Equals(SortKeyOrdering other) =>
        Direction == other.Direction && NullOrder == other.NullOrder;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SortKeyOrdering other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => ((int)Direction << 1) | (int)NullOrder;

    /// <inheritdoc/>
    public override string ToString() =>
        $"{(IsDescending ? "DESC" : "ASC")} NULLS {(NullsFirst ? "FIRST" : "LAST")}";

    /// <summary>Value equality.</summary>
    public static bool operator ==(SortKeyOrdering left, SortKeyOrdering right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(SortKeyOrdering left, SortKeyOrdering right) => !left.Equals(right);
}
