using Apache.Arrow;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Columnar.Arrow;

/// <summary>
/// An immutable variable-width <see cref="ColumnVector"/> over an Apache Arrow
/// <see cref="BinaryArray"/> (which also backs <see cref="StringArray"/>) for the v1
/// <see cref="StringType"/> (UTF-8) and <see cref="BinaryType"/> (STORY-02.2.1). The Arrow array's
/// value offsets and validity are already offset-adjusted, so each <see cref="GetBytes"/> returns
/// the logical row's bytes without copying, and a null row reports empty (use <see cref="IsNull"/>
/// to distinguish empty from null).
/// </summary>
internal sealed class ArrowVariableWidthColumnVector : ArrowColumnVector
{
    private readonly BinaryArray _array;

    internal ArrowVariableWidthColumnVector(DataType type, BinaryArray array)
        : base(type)
    {
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
    public override ReadOnlySpan<T> GetValues<T>() =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is variable-width; use GetBytes() instead of GetValues<T>().");

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> GetBytes(int index)
    {
        CheckIndex(index);

        // A null row carries no value; the contract returns empty (use IsNull to distinguish).
        return _array.IsNull(index) ? default : _array.GetBytes(index);
    }

    /// <inheritdoc/>
    public override ColumnVector Slice(int offset, int length)
    {
        CheckRange(offset, length);

        // Zero-copy Arrow slice (adjusts offset over the shared buffers); re-wrap into a ColumnVector.
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
