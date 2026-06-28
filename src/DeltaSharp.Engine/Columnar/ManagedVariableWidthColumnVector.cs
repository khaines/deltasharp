using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// A managed reference implementation of a variable-width <see cref="ColumnVector"/> for
/// <see cref="StringType"/> (UTF-8) and <see cref="BinaryType"/>, backed by an offsets buffer and
/// a shared byte buffer plus a validity bitmap. Like its fixed-width sibling it is a concrete
/// non-Arrow implementation of the contracts (STORY-02.1.1 AC4).
/// </summary>
public sealed class ManagedVariableWidthColumnVector : MutableColumnVector
{
    private int[] _offsets;
    private byte[] _data;
    private byte[] _validity;
    private readonly int _offset;
    private int _length;
    private int _dataLength;
    private int _nullCount;
    private readonly bool _mutable;

    /// <summary>Creates an empty, mutable variable-width vector for <paramref name="type"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not a variable-width type.</exception>
    public ManagedVariableWidthColumnVector(DataType type, int capacity)
        : base(type)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (!type.TryGetPhysicalLayout(out PhysicalLayout layout) || layout.Kind != PhysicalLayoutKind.Variable)
        {
            throw new ArgumentException(
                $"Type '{type.SimpleString}' does not have a variable-width layout.", nameof(type));
        }

        int initial = Math.Max(capacity, 1);
        _offsets = new int[initial + 1];
        _data = new byte[initial * 8];
        _validity = new byte[Bitmap.ByteCount(initial)];
        _mutable = true;
    }

    private ManagedVariableWidthColumnVector(
        DataType type, int[] offsets, byte[] data, byte[] validity, int offset, int length, int nullCount)
        : base(type)
    {
        _offsets = offsets;
        _data = data;
        _validity = validity;
        _offset = offset;
        _length = length;
        _nullCount = nullCount;
        _mutable = false;
    }

    /// <inheritdoc/>
    public override int Length => _length;

    /// <inheritdoc/>
    public override int Offset => _offset;

    /// <inheritdoc/>
    public override bool HasNulls => _nullCount > 0;

    /// <inheritdoc/>
    public override int NullCount => _nullCount;

    /// <inheritdoc/>
    public override bool IsNull(int index)
    {
        CheckIndex(index);
        return !Bitmap.Get(_validity, _offset + index);
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<TRequest> GetValues<TRequest>() =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is variable-width; use GetBytes() instead of GetValues<T>().");

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> GetBytes(int index)
    {
        CheckIndex(index);
        int physical = _offset + index;
        int start = _offsets[physical];
        int end = _offsets[physical + 1];
        return _data.AsSpan(start, end - start);
    }

    /// <inheritdoc/>
    public override ColumnVector Slice(int offset, int length)
    {
        CheckRange(offset, length);
        int absoluteOffset = _offset + offset;
        int nulls = Bitmap.CountNulls(_validity, absoluteOffset, length);
        return new ManagedVariableWidthColumnVector(Type, _offsets, _data, _validity, absoluteOffset, length, nulls);
    }

    /// <inheritdoc/>
    public override void AppendValue<TRequest>(TRequest value) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is variable-width; use AppendBytes() instead of AppendValue<T>().");

    /// <inheritdoc/>
    public override void AppendBytes(ReadOnlySpan<byte> value)
    {
        RequireMutable();
        EnsureRowCapacity(_length + 1);
        EnsureDataCapacity(_dataLength + value.Length);
        value.CopyTo(_data.AsSpan(_dataLength));
        _dataLength += value.Length;
        _offsets[_length + 1] = _dataLength;
        Bitmap.Set(_validity, _length, true);
        _length++;
    }

    /// <inheritdoc/>
    public override void AppendNull()
    {
        RequireMutable();
        EnsureRowCapacity(_length + 1);
        _offsets[_length + 1] = _dataLength;
        Bitmap.Set(_validity, _length, false);
        _length++;
        _nullCount++;
    }

    /// <inheritdoc/>
    public override void SetValue<TRequest>(int index, TRequest value) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is variable-width and does not support in-place SetValue.");

    /// <inheritdoc/>
    public override void SetNull(int index)
    {
        RequireMutable();
        CheckIndex(index);
        if (!IsNull(index))
        {
            Bitmap.Set(_validity, _offset + index, false);
            _nullCount++;
        }
    }

    /// <inheritdoc/>
    public override void Clear()
    {
        RequireMutable();
        _length = 0;
        _dataLength = 0;
        _nullCount = 0;
        _offsets[0] = 0;
        Array.Clear(_validity);
    }

    private void EnsureRowCapacity(int requiredRows)
    {
        if (requiredRows + 1 <= _offsets.Length)
        {
            return;
        }

        int newCapacity = Math.Max(requiredRows + 1, _offsets.Length * 2);
        Array.Resize(ref _offsets, newCapacity);
        Array.Resize(ref _validity, Bitmap.ByteCount(newCapacity));
    }

    private void EnsureDataCapacity(int requiredBytes)
    {
        if (requiredBytes <= _data.Length)
        {
            return;
        }

        int newCapacity = Math.Max(requiredBytes, _data.Length * 2);
        Array.Resize(ref _data, newCapacity);
    }

    private void RequireMutable()
    {
        if (!_mutable)
        {
            throw new InvalidOperationException("This vector is a read-only slice and cannot be modified.");
        }
    }

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be in [0, {_length}).");
        }
    }

    private void CheckRange(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > _length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), length, $"Slice [{offset}, {offset + length}) exceeds length {_length}.");
        }
    }
}
