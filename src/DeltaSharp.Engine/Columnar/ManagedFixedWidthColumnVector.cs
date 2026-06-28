using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// A managed, GC-heap reference implementation of a fixed-width <see cref="ColumnVector"/> backed
/// by a <typeparamref name="T"/><c>[]</c> value buffer and a validity bitmap. It is the
/// correctness reference and a concrete <b>non-Arrow</b> implementation of the contracts
/// (STORY-02.1.1 AC4); the Arrow-backed (STORY-02.2.1) and off-heap (STORY-02.3.1) vectors are
/// separate. <typeparamref name="T"/> is the natural CLR storage type of the logical
/// <see cref="ColumnVector.Type"/> (for example <see cref="int"/> for <see cref="IntegerType"/>
/// or <see cref="DateType"/>, <see cref="long"/> for <see cref="LongType"/>/<see cref="TimestampType"/>).
/// </summary>
/// <typeparam name="T">The fixed-width storage element type.</typeparam>
public sealed class ManagedFixedWidthColumnVector<T> : MutableColumnVector
    where T : unmanaged
{
    private T[] _data;
    private byte[] _validity;
    private readonly int _offset;
    private int _length;
    private int _nullCount;
    private readonly bool _mutable;

    /// <summary>Creates an empty, mutable vector for <paramref name="type"/> with the given initial capacity.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> does not match the type's fixed-width layout.</exception>
    public ManagedFixedWidthColumnVector(DataType type, int capacity)
        : base(type)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (!type.TryGetPhysicalLayout(out PhysicalLayout layout)
            || layout.Kind != PhysicalLayoutKind.FixedWidth
            || layout.FixedWidthBytes != Unsafe.SizeOf<T>())
        {
            throw new ArgumentException(
                $"Storage type {typeof(T).Name} does not match the fixed-width layout of '{type.SimpleString}'.",
                nameof(type));
        }

        int initial = Math.Max(capacity, 1);
        _data = new T[initial];
        _validity = new byte[Bitmap.ByteCount(initial)];
        _mutable = true;
    }

    private ManagedFixedWidthColumnVector(DataType type, T[] data, byte[] validity, int offset, int length, int nullCount)
        : base(type)
    {
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
    public override ReadOnlySpan<TRequest> GetValues<TRequest>()
    {
        RequireElementType<TRequest>();
        return MemoryMarshal.Cast<T, TRequest>(_data.AsSpan(_offset, _length));
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> GetBytes(int index) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is fixed-width; use GetValues<T>() instead of GetBytes.");

    /// <inheritdoc/>
    public override ColumnVector Slice(int offset, int length)
    {
        CheckRange(offset, length);
        int absoluteOffset = _offset + offset;
        int nulls = Bitmap.CountNulls(_validity, absoluteOffset, length);
        return new ManagedFixedWidthColumnVector<T>(Type, _data, _validity, absoluteOffset, length, nulls);
    }

    /// <inheritdoc/>
    public override void AppendValue<TRequest>(TRequest value)
    {
        RequireMutable();
        RequireElementType<TRequest>();
        EnsureCapacity(_length + 1);
        _data[_length] = Unsafe.As<TRequest, T>(ref value);
        Bitmap.Set(_validity, _length, true);
        _length++;
    }

    /// <inheritdoc/>
    public override void AppendBytes(ReadOnlySpan<byte> value) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is fixed-width; use AppendValue<T>() instead of AppendBytes.");

    /// <inheritdoc/>
    public override void AppendNull()
    {
        RequireMutable();
        EnsureCapacity(_length + 1);
        _data[_length] = default;
        Bitmap.Set(_validity, _length, false);
        _length++;
        _nullCount++;
    }

    /// <inheritdoc/>
    public override void SetValue<TRequest>(int index, TRequest value)
    {
        RequireMutable();
        RequireElementType<TRequest>();
        CheckIndex(index);
        if (IsNull(index))
        {
            _nullCount--;
        }

        _data[_offset + index] = Unsafe.As<TRequest, T>(ref value);
        Bitmap.Set(_validity, _offset + index, true);
    }

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
        _nullCount = 0;
        Array.Clear(_validity);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _data.Length)
        {
            return;
        }

        int newCapacity = Math.Max(required, _data.Length * 2);
        Array.Resize(ref _data, newCapacity);
        Array.Resize(ref _validity, Bitmap.ByteCount(newCapacity));
    }

    private static void RequireElementType<TRequest>()
        where TRequest : unmanaged
    {
        if (typeof(TRequest) != typeof(T))
        {
            throw new InvalidOperationException(
                $"Vector stores '{typeof(T).Name}'; requested element type was '{typeof(TRequest).Name}'.");
        }
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
