using System.Runtime.InteropServices;
using Apache.Arrow;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Columnar.Arrow;

/// <summary>
/// An immutable fixed-width <see cref="ColumnVector"/> over an Apache Arrow
/// <see cref="PrimitiveArray{S}"/> (STORY-02.2.1). <typeparamref name="S"/> is the Arrow storage
/// element (e.g. <see cref="sbyte"/> for Arrow Int8); the contract element type the engine exposes
/// is recorded separately so <see cref="GetValues{T}"/> presents <c>byte</c> for the signed-8 type
/// without a copy. Arrow's <see cref="PrimitiveArray{S}.Values"/>, <see cref="NullCount"/>, and
/// <see cref="IsNull"/> are already offset-adjusted, so the wrapper does no offset arithmetic.
/// </summary>
/// <typeparam name="S">The Arrow storage element width (same byte width as the exposed type).</typeparam>
internal sealed class ArrowFixedWidthColumnVector<S> : ArrowColumnVector
    where S : unmanaged, IEquatable<S>
{
    private readonly PrimitiveArray<S> _array;
    private readonly Type _requestType;

    internal ArrowFixedWidthColumnVector(DataType type, Type requestType, PrimitiveArray<S> array)
        : base(type)
    {
        _requestType = requestType;
        _array = array;
    }

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

    /// <inheritdoc/>
    public override ReadOnlySpan<T> GetValues<T>()
    {
        if (typeof(T) != _requestType)
        {
            throw new InvalidOperationException(
                $"Vector exposes '{_requestType.Name}'; requested element type was '{typeof(T).Name}'.");
        }

        // Arrow's Values is already sliced to [0, Length) for this array's offset; reinterpret the
        // storage span as the contract type (identical byte width — e.g. sbyte → byte) with no copy.
        return MemoryMarshal.Cast<S, T>(_array.Values);
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> GetBytes(int index) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is fixed-width; use GetValues<T>() instead of GetBytes.");

    /// <inheritdoc/>
    public override ColumnVector Slice(int offset, int length)
    {
        CheckRange(offset, length);

        // PrimitiveArray.Slice is a zero-copy view that adjusts Offset; re-wrap it so the result is
        // a DeltaSharp ColumnVector, never an Apache.Arrow type at the seam.
        return Wrap((IArrowArray)_array.Slice(offset, length));
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
