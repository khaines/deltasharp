namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// A zero-copy, selection-aware view over a parent <see cref="ColumnVector"/> (STORY-02.1.2). Its
/// logical rows are the parent rows the <see cref="SelectionVector"/> selects, in selection order,
/// so <see cref="Length"/> equals the selected cardinality; the parent's value and validity
/// buffers are shared, never copied. Selected logical row <c>i</c> maps to physical row
/// <c>selection[i]</c>, and every read — value, bytes, and validity — resolves against the parent
/// at that physical index. Composing a view over a view fuses the two selections (no nesting cost).
/// </summary>
/// <remarks>
/// A selection is not contiguous, so the bulk <see cref="GetValues{T}"/> accessor is unavailable;
/// kernels enumerate the selected rows over <c>[0, Length)</c> via <see cref="GetValue{T}"/>,
/// <see cref="GetBytes"/>, and <see cref="IsNull"/> (a gather). The parent is sealed when the view
/// is created, so it cannot mutate the shared buffers underneath the view.
/// </remarks>
public sealed class SelectedColumnVector : ColumnVector
{
    private readonly ColumnVector _parent;
    private readonly SelectionVector _selection;
    private readonly int _nullCount;

    internal SelectedColumnVector(ColumnVector parent, SelectionVector selection)
        : base(parent.Type)
    {
        _parent = parent;
        _selection = selection;

        // The view's null count is the parent's null bits at the selected physical rows, counted
        // once at construction so HasNulls/NullCount are O(1) reads thereafter.
        int nulls = 0;
        ReadOnlySpan<int> indices = selection.Indices;
        for (int i = 0; i < indices.Length; i++)
        {
            // Load-bearing bounds gate: every selected physical index must be within the parent. This must
            // not be removed even if NullCount becomes lazy — it is the view's last guard against an
            // out-of-range physical read (security/refactor durability).
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)indices[i], (uint)parent.Length, nameof(selection));
            if (parent.IsNull(indices[i]))
            {
                nulls++;
            }
        }

        _nullCount = nulls;
    }

    /// <summary>The selection that maps this view's logical rows to parent physical rows.</summary>
    public SelectionVector Selection => _selection;

    /// <inheritdoc/>
    public override int Length => _selection.Count;

    /// <summary>Always <c>0</c>: a selected view re-bases logical row <c>0</c> to the first selected row.</summary>
    public override int Offset => 0;

    /// <inheritdoc/>
    public override bool HasNulls => _nullCount > 0;

    /// <inheritdoc/>
    public override int NullCount => _nullCount;

    /// <inheritdoc/>
    public override bool IsNull(int index)
    {
        CheckIndex(index);
        return _parent.IsNull(_selection[index]);
    }

    /// <summary>
    /// Not supported on a selected view: the selected rows are generally non-contiguous, so a
    /// single offset-adjusted span cannot describe them without copying. Enumerate the rows over
    /// <c>[0, Length)</c> with <see cref="GetValue{T}"/> (a gather), or materialize first.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override ReadOnlySpan<T> GetValues<T>() =>
        throw new InvalidOperationException(
            "A selected view is not contiguous; enumerate rows in [0, Length) via GetValue<T>() or materialize.");

    /// <inheritdoc/>
    public override T GetValue<T>(int index)
    {
        CheckIndex(index);
        return _parent.GetValue<T>(_selection[index]);
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> GetBytes(int index)
    {
        CheckIndex(index);
        return _parent.GetBytes(_selection[index]);
    }

    /// <inheritdoc/>
    public override ColumnVector Slice(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > _selection.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), length, $"Slice [{offset}, {offset + length}) exceeds length {_selection.Count}.");
        }

        // Sub-range the selection in place — still a zero-copy view over the same parent buffers.
        return new SelectedColumnVector(_parent, new SelectionVector(_selection.Indices.Slice(offset, length)));
    }

    /// <inheritdoc/>
    public override ColumnVector Select(SelectionVector selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        // Fuse: applying this view's selection then the outer selection resolves to one selection
        // over the same parent, so nesting never copies and never deepens (STORY-02.1.2 AC2).
        return new SelectedColumnVector(_parent, _selection.Compose(selection));
    }

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)_selection.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be in [0, {_selection.Count}).");
        }
    }
}
