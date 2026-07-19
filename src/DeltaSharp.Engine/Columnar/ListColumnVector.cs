using DeltaSharp.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// A managed reference <see cref="ColumnVector"/> for an <see cref="ArrayType"/> (increment 1a of
/// the nested-type line, #570): an <c>int</c> offsets buffer of length <c>Length + 1</c> over a
/// single flattened element child, plus a top-level validity bitmap. Logical row <c>i</c>'s elements
/// are the child rows <c>[offsets[i], offsets[i + 1])</c>. Like its flat siblings it is a concrete
/// <b>non-Arrow</b> implementation of the columnar contract (ADR-0002); no member names an
/// <c>Apache.Arrow</c> type.
/// </summary>
/// <remarks>
/// <para>
/// A null logical row is a <b>null list</b>, which is distinct from an <b>empty list</b>: both may
/// report <see cref="ElementLength"/> <c>0</c>, but a null list has <see cref="IsNull"/> <c>true</c>
/// while an empty list has <see cref="IsNull"/> <c>false</c>. Use <see cref="IsNull"/> to tell them
/// apart.
/// </para>
/// <para>
/// <b>Building.</b> The mutable builder (<see cref="ColumnVectors.Create(DataType,int)"/> or the
/// <see cref="ListColumnVector(ArrayType,int)"/> ctor) owns a mutable element child. To append a row,
/// append that row's elements into <see cref="Elements"/> (cast to <see cref="MutableColumnVector"/>)
/// and then close the row with <see cref="EndList"/> (a non-null list) or <see cref="AppendNull"/> (a
/// null list). Each commit records <c>offsets[Length + 1] = Elements.Length</c>. The per-row readers
/// (<see cref="ElementsAt"/>) slice the shared element child and therefore seal it; finish appending
/// elements before reading per-row.
/// </para>
/// <para><see cref="Select"/> (row gather) is not implemented in this increment; see its remarks.</para>
/// </remarks>
public sealed class ListColumnVector : MutableColumnVector
{
    private readonly ArrayType _arrayType;
    private readonly ColumnVector _child;
    private int[] _offsets;
    private byte[] _validity;
    private readonly int _offset;
    private int _length;
    private int _nullCount;
    private readonly bool _mutable;
    private bool _sealed;

    /// <summary>
    /// Creates an empty, mutable list builder for <paramref name="type"/> with a mutable element
    /// child (built through <see cref="ColumnVectors.Create(DataType,int)"/> at the given
    /// <paramref name="capacity"/>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public ListColumnVector(ArrayType type, int capacity)
        : base(type)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _arrayType = type;

        int initial = Math.Max(capacity, 1);
        _child = ColumnVectors.Create(type.ElementType, capacity);
        _offsets = new int[initial + 1];
        _validity = new byte[Bitmap.ByteCount(initial)];
        _mutable = true;
    }

    /// <summary>
    /// Wraps an already-built flattened element child and its <paramref name="offsets"/> as an
    /// immutable list vector — the natural path for a columnar decoder that materializes the element
    /// column and derives offsets separately. <see cref="Length"/> is <c>offsets.Length - 1</c>.
    /// </summary>
    /// <param name="type">The array type.</param>
    /// <param name="elements">The flattened element child; its type must equal the array's element type.</param>
    /// <param name="offsets">
    /// The offsets buffer (length <c>Length + 1</c>): non-negative, monotonically non-decreasing, and
    /// ending at or before <c>elements.Length</c>.
    /// </param>
    /// <param name="nulls">
    /// Optional per-row null flags where <c>nulls[i] == true</c> marks logical row <c>i</c> a null
    /// list. An empty span means no null rows; when non-empty its length must equal <c>Length</c>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> or <paramref name="elements"/> is null.</exception>
    /// <exception cref="ArgumentException">The element type, offsets, or <paramref name="nulls"/> length is inconsistent.</exception>
    public ListColumnVector(ArrayType type, ColumnVector elements, ReadOnlySpan<int> offsets, ReadOnlySpan<bool> nulls = default)
        : base(type)
    {
        ArgumentNullException.ThrowIfNull(elements);
        _arrayType = type;

        if (!elements.Type.Equals(type.ElementType))
        {
            throw new ArgumentException(
                $"Element child has type '{elements.Type.SimpleString}' but the array declares element type "
                + $"'{type.ElementType.SimpleString}'.", nameof(elements));
        }

        _offsets = NestedValidity.CopyValidatedOffsets(offsets, elements.Length, nameof(offsets));
        int length = _offsets.Length - 1;
        (_validity, _nullCount) = NestedValidity.Build(nulls, length);
        _child = elements;
        _length = length;
        _mutable = false;
    }

    private ListColumnVector(
        ArrayType type, ColumnVector child, int[] offsets, byte[] validity, int offset, int length, int nullCount)
        : base(type)
    {
        _arrayType = type;
        _child = child;
        _offsets = offsets;
        _validity = validity;
        _offset = offset;
        _length = length;
        _nullCount = nullCount;
        _mutable = false;
    }

    /// <summary>
    /// The flattened element child, aligned to this vector's logical rows (for a whole/just-built
    /// vector this is the element child itself; for a <see cref="Slice"/> it is the zero-copy element
    /// sub-range this view reaches). Index individual rows' elements through <see cref="ElementsAt"/>;
    /// while building, cast this to <see cref="MutableColumnVector"/> to append a row's elements.
    /// </summary>
    public ColumnVector Elements
    {
        get
        {
            if (_mutable)
            {
                return _child;
            }

            int start = _offsets[_offset];
            int end = _offsets[_offset + _length];
            return start == 0 && end == _child.Length ? _child : _child.Slice(start, end - start);
        }
    }

    /// <inheritdoc/>
    public override int Length => _length;

    /// <inheritdoc/>
    public override int Offset => _offset;

    /// <inheritdoc/>
    public override bool HasNulls => _nullCount > 0;

    /// <inheritdoc/>
    public override int NullCount => _nullCount;

    /// <summary>
    /// The number of elements in logical row <paramref name="index"/> (<c>offsets[i + 1] -
    /// offsets[i]</c>). A null list reports <c>0</c> (a null list has no elements); use
    /// <see cref="IsNull"/> to tell a null list from an empty one.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
    public int ElementLength(int index)
    {
        CheckIndex(index);
        int physical = _offset + index;
        return Bitmap.Get(_validity, physical) ? _offsets[physical + 1] - _offsets[physical] : 0;
    }

    /// <summary>
    /// A zero-copy <see cref="ColumnVector"/> view of logical row <paramref name="index"/>'s elements
    /// (the element child sliced to <c>[offsets[i], offsets[i + 1])</c>). A null or empty list yields
    /// an empty view; use <see cref="IsNull"/> to distinguish them. Slicing the shared element child
    /// seals it, so call this only after all element appends are done.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
    public ColumnVector ElementsAt(int index)
    {
        CheckIndex(index);
        int physical = _offset + index;
        int start = _offsets[physical];
        // A null list row yields an empty view regardless of its physical offset span (masked elements are
        // never surfaced); IsNull distinguishes a null list from an empty one.
        int length = Bitmap.Get(_validity, physical) ? _offsets[physical + 1] - start : 0;
        return _child.Slice(start, length);
    }

    /// <inheritdoc/>
    public override bool IsNull(int index)
    {
        CheckIndex(index);
        return !Bitmap.Get(_validity, _offset + index);
    }

    /// <summary>Not supported: a list has no flat scalar span. Read elements through <see cref="Elements"/>/<see cref="ElementsAt"/>.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override ReadOnlySpan<T> GetValues<T>() =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a list and has no flat scalar span; read its elements via ElementsAt(index).");

    /// <summary>Not supported: a list carries no variable-width bytes at the top level.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override ReadOnlySpan<byte> GetBytes(int index) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a list; read its elements via ElementsAt(index), not GetBytes.");

    /// <inheritdoc/>
    public override ColumnVector Slice(int offset, int length)
    {
        CheckRange(offset, length);

        // Zero-copy: share the element child, offsets, and validity; the window re-bases logical row 0
        // via _offset (offsets stay absolute into the shared child). SealForView() blocks further row
        // commits (EndList/AppendNull) so offsets/validity are not resized under the view, AND propagates
        // the seal to the shared element child (#575) so a retained mutable child ref cannot desync it.
        SealForView();

        int absoluteOffset = _offset + offset;
        int nulls = Bitmap.CountNulls(_validity, absoluteOffset, length);
        return new ListColumnVector(_arrayType, _child, _offsets, _validity, absoluteOffset, length, nulls);
    }

    /// <summary>
    /// A view sharing this list's element child and offsets, but with the top-level validity masked so every
    /// logical row where <paramref name="parentNulls"/> is <see langword="true"/> reads as a null list (OR-ed
    /// into this list's own nulls). Used to extract a nested list field of a struct that carries null rows: a
    /// field of a null struct is null (#589 / Spark semantics), sharing the element buffers zero-copy.
    /// </summary>
    /// <param name="parentNulls">Per-logical-row flags (length <see cref="Length"/>) where <see langword="true"/>
    /// forces row <c>i</c> null.</param>
    /// <exception cref="ArgumentException"><paramref name="parentNulls"/> length does not equal <see cref="Length"/>.</exception>
    internal ListColumnVector WithParentNulls(ReadOnlySpan<bool> parentNulls)
    {
        if (parentNulls.Length != _length)
        {
            throw new ArgumentException(
                $"parentNulls length {parentNulls.Length} must equal the list Length {_length}.", nameof(parentNulls));
        }

        // Share the element child + offsets (seal so they cannot be mutated under this view, #575); copy only
        // the top-level validity so masking in the parent-null bits never mutates the source field's validity.
        SealForView();
        byte[] validity = (byte[])_validity.Clone();
        int nullCount = _nullCount;
        for (int i = 0; i < _length; i++)
        {
            int physical = _offset + i;
            if (parentNulls[i] && Bitmap.Get(validity, physical))
            {
                Bitmap.Set(validity, physical, value: false);
                nullCount++;
            }
        }

        return new ListColumnVector(_arrayType, _child, _offsets, validity, _offset, _length, nullCount);
    }

    /// <summary>
    /// Not supported in this increment. A correct row gather over a list must materialize the gathered
    /// variable-length element ranges into a new compacted child and rebuild offsets, which the nested
    /// -representation increment (#570) defers rather than returning a partial/wrong view. Slice for a
    /// contiguous sub-range, or materialize. Late-materialized nested <see cref="Select"/> is tracked
    /// on the #546 line.
    /// </summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override ColumnVector Select(SelectionVector selection) =>
        throw new NotSupportedException(
            $"Select (row gather) over a list column ('{Type.SimpleString}') is not supported in the nested "
            + "representation increment (#570); gathered/late-materialized nested Select is a follow-up (#546). "
            + "Use Slice for a contiguous sub-range or materialize the column.");

    /// <summary>Seals this owner and, recursively, its element child (#575) so a slice/selection view shares
    /// the buffers safely — a retained mutable child reference cannot be mutated after a view is taken.</summary>
    protected override void SealForView()
    {
        _sealed = true;
        _child.Seal();
    }

    /// <summary>Not supported: a list has no flat scalar value. Append elements into <see cref="Elements"/>, then <see cref="EndList"/>.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override void AppendValue<T>(T value) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a list; append elements into Elements then call EndList/AppendNull.");

    /// <summary>Not supported: a list has no flat scalar value. Append elements into <see cref="Elements"/>, then <see cref="EndList"/>.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override void AppendBytes(ReadOnlySpan<byte> value) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a list; append elements into Elements then call EndList/AppendNull.");

    /// <summary>
    /// Closes a non-null list row, recording that its elements are everything appended into
    /// <see cref="Elements"/> since the previous row.
    /// </summary>
    /// <exception cref="InvalidOperationException">The vector is read-only or sealed.</exception>
    public void EndList() => CommitRow(isNull: false);

    /// <summary>
    /// Closes a null list row. Recorded as a zero-length list unless elements were appended into
    /// <see cref="Elements"/> since the previous row (which are then present but masked).
    /// </summary>
    /// <exception cref="InvalidOperationException">The vector is read-only or sealed.</exception>
    public override void AppendNull() => CommitRow(isNull: true);

    /// <summary>Not supported: a list has no flat scalar value.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override void SetValue<T>(int index, T value) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a list and does not support in-place SetValue.");

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
        ((MutableColumnVector)_child).Clear();
        _length = 0;
        _nullCount = 0;
        _offsets[0] = 0;
        Array.Clear(_validity);
    }

    private void CommitRow(bool isNull)
    {
        RequireMutable();
        EnsureRowCapacity(_length + 1);
        _offsets[_length + 1] = _child.Length;
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
        if ((long)offset + length > _length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), length, $"Slice [{offset}, {(long)offset + length}) exceeds length {_length}.");
        }
    }
}
