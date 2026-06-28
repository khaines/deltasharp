using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// The unit of vectorized work: a set of equal-length <see cref="ColumnVector"/>s described by a
/// <see cref="StructType"/> schema (ADR-0002). Operators and kernels bind to this contract;
/// like <see cref="ColumnVector"/>, no member names a <c>Apache.Arrow</c> type.
/// </summary>
public abstract class ColumnBatch
{
    /// <summary>The schema describing the batch's columns (the shared Lane-2 type contract).</summary>
    public abstract StructType Schema { get; }

    /// <summary>The number of physical rows in each column.</summary>
    public abstract int RowCount { get; }

    /// <summary>The number of columns; equals <see cref="Schema"/>'s field count.</summary>
    public abstract int ColumnCount { get; }

    /// <summary>
    /// The optional selection vector. When present, the batch's <i>logical</i> rows are the
    /// selected physical rows, in selection order; <see cref="LogicalRowCount"/> reflects it.
    /// </summary>
    public abstract SelectionVector? Selection { get; }

    /// <summary>
    /// The logical row count: <see cref="SelectionVector.Count"/> when a <see cref="Selection"/>
    /// is present, otherwise <see cref="RowCount"/>.
    /// </summary>
    public int LogicalRowCount => Selection?.Count ?? RowCount;

    /// <summary>The column at the given ordinal.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is outside <c>[0, ColumnCount)</c>.</exception>
    public abstract ColumnVector Column(int ordinal);

    /// <summary>The column with the given (case-sensitive) schema field name.</summary>
    /// <exception cref="KeyNotFoundException">No column has that name.</exception>
    public ColumnVector Column(string name)
    {
        int ordinal = Schema.IndexOf(name);
        return ordinal >= 0
            ? Column(ordinal)
            : throw new KeyNotFoundException($"No column named '{name}' in batch schema {Schema.SimpleString}.");
    }

    /// <summary>
    /// A logical sub-range of physical rows as a new batch over sliced child vectors that retain
    /// consistent row count, offset, and validity (STORY-02.1.1 AC2). Slicing drops any selection
    /// (selection composition over slices is STORY-02.1.2).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside <c>[0, RowCount]</c>.</exception>
    public abstract ColumnBatch Slice(int offset, int length);

    /// <summary>
    /// Returns a selection-aware view of this batch carrying <paramref name="selection"/>. The
    /// underlying columns are shared (no copy); only the logical row mapping changes. If the batch
    /// already carries a selection, the two compose — <paramref name="selection"/> indexes the
    /// current logical rows and resolves through to physical rows (STORY-02.1.2 AC1, AC2).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="selection"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A selected index is outside <c>[0, LogicalRowCount)</c>.</exception>
    public abstract ColumnBatch WithSelection(SelectionVector selection);

    /// <summary>
    /// The column at <paramref name="ordinal"/> as a logical view of the selected rows: when a
    /// <see cref="Selection"/> is present, the returned vector's row <c>i</c> is the i-th selected
    /// row (zero-copy via <see cref="ColumnVector.Select"/>); otherwise it is the column itself.
    /// Kernels enumerate it over <c>[0, LogicalRowCount)</c> without selection bookkeeping.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is outside <c>[0, ColumnCount)</c>.</exception>
    public ColumnVector SelectedColumn(int ordinal) =>
        Selection is { } selection ? Column(ordinal).Select(selection) : Column(ordinal);
}
