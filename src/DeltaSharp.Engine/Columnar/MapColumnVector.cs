using DeltaSharp.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// A managed reference <see cref="ColumnVector"/> for a <see cref="MapType"/> (increment 1a of the
/// nested-type line, #570). Arrow models a map as <c>list&lt;struct&lt;key, value&gt;&gt;</c>; this
/// implements it directly with an <c>int</c> offsets buffer of length <c>Length + 1</c> over two
/// parallel flattened children — a key child and a value child — plus a top-level validity bitmap.
/// Logical row <c>i</c>'s entries are the child rows <c>[offsets[i], offsets[i + 1])</c> of both
/// children. Like its flat siblings it is a concrete <b>non-Arrow</b> implementation of the columnar
/// contract (ADR-0002); no member names an <c>Apache.Arrow</c> type.
/// </summary>
/// <remarks>
/// <para>
/// A null logical row is a <b>null map</b>, distinct from an <b>empty map</b>: both may report
/// <see cref="EntryLength"/> <c>0</c>, but a null map has <see cref="IsNull"/> <c>true</c> while an
/// empty map has <see cref="IsNull"/> <c>false</c>. Per <see cref="MapType"/>, keys are non-null; this
/// vector stores them in a key child but does not itself enforce key non-nullness.
/// </para>
/// <para>
/// <b>Building.</b> The mutable builder (<see cref="ColumnVectors.Create(DataType,int)"/> or the
/// <see cref="MapColumnVector(MapType,int)"/> ctor) owns a mutable key child and value child. To
/// append a row, append each entry's key into <see cref="Keys"/> and value into <see cref="Values"/>
/// (equal counts), then close the row with <see cref="EndMap"/> (a non-null map) or
/// <see cref="AppendNull"/> (a null map). The per-row readers (<see cref="KeysAt"/>/<see cref="ValuesAt"/>)
/// slice the shared children and therefore seal them; finish appending entries before reading per-row.
/// </para>
/// <para><see cref="Select"/> (row gather) is not implemented in this increment; see its remarks.</para>
/// </remarks>
public sealed class MapColumnVector : MutableColumnVector
{
    private readonly MapType _mapType;
    private readonly ColumnVector _keys;
    private readonly ColumnVector _values;
    private int[] _offsets;
    private byte[] _validity;
    private readonly int _offset;
    private int _length;
    private int _nullCount;
    private readonly bool _mutable;
    private bool _sealed;

    /// <summary>
    /// Creates an empty, mutable map builder for <paramref name="type"/> with a mutable key child and
    /// value child (built through <see cref="ColumnVectors.Create(DataType,int)"/> at the given
    /// <paramref name="capacity"/>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public MapColumnVector(MapType type, int capacity)
        : base(type)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _mapType = type;

        int initial = Math.Max(capacity, 1);
        _keys = ColumnVectors.Create(type.KeyType, capacity);
        _values = ColumnVectors.Create(type.ValueType, capacity);
        _offsets = new int[initial + 1];
        _validity = new byte[Bitmap.ByteCount(initial)];
        _mutable = true;
    }

    /// <summary>
    /// Wraps already-built, parallel key and value children and their <paramref name="offsets"/> as an
    /// immutable map vector — the natural path for a columnar decoder. <see cref="Length"/> is
    /// <c>offsets.Length - 1</c>.
    /// </summary>
    /// <param name="type">The map type.</param>
    /// <param name="keys">The flattened key child; its type must equal the map's key type.</param>
    /// <param name="values">The flattened value child; its type must equal the map's value type, and its length must equal the key child's.</param>
    /// <param name="offsets">
    /// The offsets buffer (length <c>Length + 1</c>): non-negative, monotonically non-decreasing, and
    /// ending at or before the entry count.
    /// </param>
    /// <param name="nulls">
    /// Optional per-row null flags where <c>nulls[i] == true</c> marks logical row <c>i</c> a null map.
    /// An empty span means no null rows; when non-empty its length must equal <c>Length</c>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="type"/>, <paramref name="keys"/>, or <paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentException">The key/value types, child lengths, offsets, or <paramref name="nulls"/> length are inconsistent.</exception>
    public MapColumnVector(
        MapType type, ColumnVector keys, ColumnVector values, ReadOnlySpan<int> offsets, ReadOnlySpan<bool> nulls = default)
        : base(type)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(values);
        _mapType = type;

        if (!keys.Type.Equals(type.KeyType))
        {
            throw new ArgumentException(
                $"Key child has type '{keys.Type.SimpleString}' but the map declares key type "
                + $"'{type.KeyType.SimpleString}'.", nameof(keys));
        }

        if (!values.Type.Equals(type.ValueType))
        {
            throw new ArgumentException(
                $"Value child has type '{values.Type.SimpleString}' but the map declares value type "
                + $"'{type.ValueType.SimpleString}'.", nameof(values));
        }

        if (keys.Length != values.Length)
        {
            throw new ArgumentException(
                $"Key child has {keys.Length} entry(ies) but value child has {values.Length}; a map's key and "
                + "value children must be parallel.", nameof(values));
        }

        _offsets = NestedValidity.CopyValidatedOffsets(offsets, keys.Length, nameof(offsets));
        int length = _offsets.Length - 1;
        (_validity, _nullCount) = NestedValidity.Build(nulls, length);
        _keys = keys;
        _values = values;
        _length = length;
        _mutable = false;
    }

    private MapColumnVector(
        MapType type, ColumnVector keys, ColumnVector values, int[] offsets, byte[] validity,
        int offset, int length, int nullCount)
        : base(type)
    {
        _mapType = type;
        _keys = keys;
        _values = values;
        _offsets = offsets;
        _validity = validity;
        _offset = offset;
        _length = length;
        _nullCount = nullCount;
        _mutable = false;
    }

    /// <summary>
    /// The flattened key child, aligned to this vector's logical rows (the whole child for a
    /// just-built vector; the reachable key sub-range for a <see cref="Slice"/>). Index a row's keys
    /// through <see cref="KeysAt"/>; while building, cast this to <see cref="MutableColumnVector"/> to
    /// append an entry's key.
    /// </summary>
    public ColumnVector Keys => AlignedChild(_keys);

    /// <summary>
    /// The flattened value child, aligned to this vector's logical rows (see <see cref="Keys"/>).
    /// Index a row's values through <see cref="ValuesAt"/>; while building, cast this to
    /// <see cref="MutableColumnVector"/> to append an entry's value.
    /// </summary>
    public ColumnVector Values => AlignedChild(_values);

    /// <inheritdoc/>
    public override int Length => _length;

    /// <inheritdoc/>
    public override int Offset => _offset;

    /// <inheritdoc/>
    public override bool HasNulls => _nullCount > 0;

    /// <inheritdoc/>
    public override int NullCount => _nullCount;

    /// <summary>
    /// The number of entries in logical row <paramref name="index"/> (<c>offsets[i + 1] -
    /// offsets[i]</c>). A null map typically reports <c>0</c>; use <see cref="IsNull"/> to tell a null
    /// map from an empty one.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
    public int EntryLength(int index)
    {
        CheckIndex(index);
        int physical = _offset + index;
        return _offsets[physical + 1] - _offsets[physical];
    }

    /// <summary>
    /// A zero-copy view of logical row <paramref name="index"/>'s keys (the key child sliced to
    /// <c>[offsets[i], offsets[i + 1])</c>). Pairs positionally with <see cref="ValuesAt"/>. Slicing
    /// the shared key child seals it, so call this only after all entry appends are done.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
    public ColumnVector KeysAt(int index) => EntrySlice(_keys, index);

    /// <summary>
    /// A zero-copy view of logical row <paramref name="index"/>'s values, pairing positionally with
    /// <see cref="KeysAt"/>. Slicing the shared value child seals it, so call this only after all entry
    /// appends are done.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
    public ColumnVector ValuesAt(int index) => EntrySlice(_values, index);

    /// <inheritdoc/>
    public override bool IsNull(int index)
    {
        CheckIndex(index);
        return !Bitmap.Get(_validity, _offset + index);
    }

    /// <summary>Not supported: a map has no flat scalar span. Read entries through <see cref="KeysAt"/>/<see cref="ValuesAt"/>.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override ReadOnlySpan<T> GetValues<T>() =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a map and has no flat scalar span; read its entries via KeysAt/ValuesAt(index).");

    /// <summary>Not supported: a map carries no variable-width bytes at the top level.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override ReadOnlySpan<byte> GetBytes(int index) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a map; read its entries via KeysAt/ValuesAt(index), not GetBytes.");

    /// <inheritdoc/>
    public override ColumnVector Slice(int offset, int length)
    {
        CheckRange(offset, length);

        // Zero-copy: share both children, offsets, and validity; the window re-bases logical row 0 via
        // _offset (offsets stay absolute into the shared children). Sealing keeps the shared buffers
        // safe against post-slice appends on this builder.
        _sealed = true;

        int absoluteOffset = _offset + offset;
        int nulls = Bitmap.CountNulls(_validity, absoluteOffset, length);
        return new MapColumnVector(_mapType, _keys, _values, _offsets, _validity, absoluteOffset, length, nulls);
    }

    /// <summary>
    /// Not supported in this increment. A correct row gather over a map must materialize the gathered
    /// variable-length entry ranges into new compacted key/value children and rebuild offsets, which
    /// the nested-representation increment (#570) defers rather than returning a partial/wrong view.
    /// Slice for a contiguous sub-range, or materialize. Late-materialized nested <see cref="Select"/>
    /// is tracked on the #546 line.
    /// </summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override ColumnVector Select(SelectionVector selection) =>
        throw new NotSupportedException(
            $"Select (row gather) over a map column ('{Type.SimpleString}') is not supported in the nested "
            + "representation increment (#570); gathered/late-materialized nested Select is a follow-up (#546). "
            + "Use Slice for a contiguous sub-range or materialize the column.");

    /// <summary>Seals the owner so a selection/slice view shares the buffers safely.</summary>
    protected override void SealForView() => _sealed = true;

    /// <summary>Not supported: a map has no flat scalar value. Append into <see cref="Keys"/>/<see cref="Values"/>, then <see cref="EndMap"/>.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override void AppendValue<T>(T value) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a map; append into Keys/Values then call EndMap/AppendNull.");

    /// <summary>Not supported: a map has no flat scalar value. Append into <see cref="Keys"/>/<see cref="Values"/>, then <see cref="EndMap"/>.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override void AppendBytes(ReadOnlySpan<byte> value) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a map; append into Keys/Values then call EndMap/AppendNull.");

    /// <summary>
    /// Closes a non-null map row, recording that its entries are every key/value appended into
    /// <see cref="Keys"/>/<see cref="Values"/> since the previous row.
    /// </summary>
    /// <exception cref="InvalidOperationException">The vector is read-only or sealed, or the key and value children are not parallel.</exception>
    public void EndMap() => CommitRow(isNull: false);

    /// <summary>
    /// Closes a null map row. Recorded as a zero-length map unless entries were appended since the
    /// previous row (which are then present but masked).
    /// </summary>
    /// <exception cref="InvalidOperationException">The vector is read-only or sealed, or the key and value children are not parallel.</exception>
    public override void AppendNull() => CommitRow(isNull: true);

    /// <summary>Not supported: a map has no flat scalar value.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override void SetValue<T>(int index, T value) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a map and does not support in-place SetValue.");

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
        ((MutableColumnVector)_keys).Clear();
        ((MutableColumnVector)_values).Clear();
        _length = 0;
        _nullCount = 0;
        _offsets[0] = 0;
        Array.Clear(_validity);
    }

    private ColumnVector AlignedChild(ColumnVector child)
    {
        if (_mutable)
        {
            return child;
        }

        int start = _offsets[_offset];
        int end = _offsets[_offset + _length];
        return start == 0 && end == child.Length ? child : child.Slice(start, end - start);
    }

    private ColumnVector EntrySlice(ColumnVector child, int index)
    {
        CheckIndex(index);
        int physical = _offset + index;
        int start = _offsets[physical];
        return child.Slice(start, _offsets[physical + 1] - start);
    }

    private void CommitRow(bool isNull)
    {
        RequireMutable();
        if (_keys.Length != _values.Length)
        {
            throw new InvalidOperationException(
                $"Key child has {_keys.Length} entry(ies) but value child has {_values.Length}; append one value "
                + "per key before committing a map row.");
        }

        EnsureRowCapacity(_length + 1);
        _offsets[_length + 1] = _keys.Length;
        Bitmap.Set(_validity, _length, !isNull);
        _length++;
        if (isNull)
        {
            _nullCount++;
        }
    }

    private void EnsureRowCapacity(int requiredRows)
    {
        if (requiredRows + 1 > _offsets.Length)
        {
            Array.Resize(ref _offsets, Math.Max(requiredRows + 1, _offsets.Length * 2));
        }

        int requiredBytes = Bitmap.ByteCount(requiredRows);
        if (requiredBytes > _validity.Length)
        {
            Array.Resize(ref _validity, Math.Max(requiredBytes, _validity.Length * 2));
        }
    }

    private void RequireMutable()
    {
        if (!_mutable)
        {
            throw new InvalidOperationException("This vector is a read-only view and cannot be modified.");
        }

        if (_sealed)
        {
            throw new InvalidOperationException(
                "This vector has been sliced and is now sealed; build a vector fully before slicing it.");
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
