using Apache.Arrow;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Columnar.Arrow;

/// <summary>
/// An immutable, opaque <see cref="ColumnVector"/> over an Apache Arrow nested array
/// (<see cref="StructArray"/>, <see cref="ListArray"/>, or <see cref="MapArray"/>) for the v1 Arrow
/// boundary round-trip (STORY-02.2.2, #136). The v1 <see cref="ColumnVector"/> contract has no
/// child-vector accessor (nested kernels are deferred to FEAT-02.3 / the off-heap vector), and the
/// managed factory does not build nested vectors, so a nested column is carried as a
/// <b>pass-through</b>: its logical length, physical offset, and per-row validity are exposed from
/// the wrapped Arrow array (zero-copy), while the scalar accessors are unavailable. This is the
/// representation that lets projection/shuffle/Flight carry nested columns untouched and round-trip
/// them back to Arrow with values unchanged.
/// </summary>
/// <remarks>
/// Only <see cref="ArrowBatchConverter"/> and the reader construct this; it is never produced by the
/// managed <see cref="ColumnVectors"/> factory. <see cref="ArrowBatchConverter.ToArrow"/> recognizes
/// it and returns the underlying Arrow array (retained), so a nested column survives a
/// batch &#8594; Arrow &#8594; batch round-trip with its schema and values intact.
/// </remarks>
internal sealed class ArrowNestedColumnVector : ColumnVector
{
    private readonly IArrowArray _array;

    /// <summary>Wraps an Arrow nested <paramref name="array"/> as the DeltaSharp nested <paramref name="type"/>.</summary>
    internal ArrowNestedColumnVector(DataType type, IArrowArray array)
        : base(type)
    {
        _array = array;
    }

    /// <summary>The wrapped Arrow nested array (the logical view; <see cref="IArrowArray.Offset"/> may be non-zero).</summary>
    internal IArrowArray Array => _array;

    /// <inheritdoc/>
    public override int Length => _array.Length;

    /// <inheritdoc/>
    public override int Offset => _array.Offset;

    /// <inheritdoc/>
    public override bool HasNulls => _array.NullCount > 0;

    /// <inheritdoc/>
    public override int NullCount => _array.NullCount;

    /// <inheritdoc/>
    public override bool IsNull(int index)
    {
        CheckIndex(index);
        return _array.IsNull(index);
    }

    /// <summary>
    /// Not supported: a nested column has no flat scalar span. Nested element access is a later
    /// concern (FEAT-02.3); v1 carries nested columns as an opaque Arrow-backed pass-through.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override ReadOnlySpan<T> GetValues<T>() =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is nested and has no flat scalar span; nested element "
            + "access is not part of the v1 columnar contract (the column is an Arrow pass-through).");

    /// <summary>
    /// Not supported: a nested column carries no variable-width bytes at the top level. See
    /// <see cref="GetValues{T}"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override ReadOnlySpan<byte> GetBytes(int index) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is nested; nested element access is not part of the v1 "
            + "columnar contract (the column is an Arrow pass-through).");

    /// <inheritdoc/>
    public override ColumnVector Slice(int offset, int length)
    {
        CheckRange(offset, length);

        // Zero-copy Arrow slice over the shared buffers (offset-adjusted); re-wrap into a ColumnVector
        // so the seam never surfaces an Apache.Arrow type.
        return new ArrowNestedColumnVector(Type, ArrowArrayFactory.Slice(_array, offset, length));
    }

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)_array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be in [0, {_array.Length}).");
        }
    }

    private void CheckRange(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > _array.Length - offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), length, $"Slice [{offset}, {offset + length}) exceeds length {_array.Length}.");
        }
    }
}
