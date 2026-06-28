namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// An ordered set of selected physical row indices into a <see cref="ColumnBatch"/>, letting
/// filters, joins, and aggregates carry "which rows survive" without copying value buffers
/// (ADR-0002 late materialization). A batch becomes selection-aware via
/// <see cref="ColumnBatch.WithSelection"/>; the zero-copy <i>selected views</i> over values are
/// STORY-02.1.2 (#134) and build on this contract.
/// </summary>
public sealed class SelectionVector
{
    private readonly int[] _indices;

    /// <summary>Creates a selection vector from physical row <paramref name="indices"/> (copied).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="indices"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Any index is negative.</exception>
    public SelectionVector(ReadOnlySpan<int> indices)
    {
        int[] copy = indices.ToArray();
        for (int i = 0; i < copy.Length; i++)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(copy[i], nameof(indices));
        }

        _indices = copy;
    }

    /// <summary>The identity selection <c>[0, length)</c> in order.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public static SelectionVector Range(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var indices = new int[length];
        for (int i = 0; i < length; i++)
        {
            indices[i] = i;
        }

        return new SelectionVector(indices);
    }

    /// <summary>
    /// Composes this selection (base → physical) with an <paramref name="outer"/> selection
    /// layered on top, so the result at position <c>p</c> is <c>this[outer[p]]</c> — exactly the
    /// physical rows produced by applying this selection then <paramref name="outer"/> in sequence.
    /// No value buffers are involved; only a fresh index array is built (STORY-02.1.2 AC2).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="outer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An outer index is outside <c>[0, Count)</c>.</exception>
    public SelectionVector Compose(SelectionVector outer)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ReadOnlySpan<int> outerIndices = outer.Indices;
        var composed = new int[outerIndices.Length];
        for (int p = 0; p < outerIndices.Length; p++)
        {
            int position = outerIndices[p];
            if ((uint)position >= (uint)_indices.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(outer), position, $"Outer index must be in [0, {_indices.Length}).");
            }

            composed[p] = _indices[position];
        }

        return new SelectionVector(composed);
    }

    /// <summary>The number of selected rows (the selected cardinality).</summary>
    public int Count => _indices.Length;

    /// <summary>The physical row index selected at selection position <paramref name="position"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is outside <c>[0, Count)</c>.</exception>
    public int this[int position] => _indices[position];

    /// <summary>The selected physical indices, in selection order.</summary>
    public ReadOnlySpan<int> Indices => _indices;
}
