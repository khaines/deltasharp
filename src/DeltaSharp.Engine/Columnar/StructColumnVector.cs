using DeltaSharp.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// A managed reference <see cref="ColumnVector"/> for a <see cref="StructType"/> (increment 1a of
/// the nested-type line, #570): one child <see cref="ColumnVector"/> per field, each aligned 1:1
/// with this vector's logical rows, plus a top-level validity bitmap. Like its flat siblings it is
/// the correctness reference and a concrete <b>non-Arrow</b> implementation of the columnar
/// contract (ADR-0002); no member names an <c>Apache.Arrow</c> type.
/// </summary>
/// <remarks>
/// <para>
/// A null logical row is a <b>null struct</b>: the whole row is absent. The child slots at that row
/// still exist (so every child keeps <c>Length == this.Length</c>), but they are masked by the
/// struct's validity — distinguish a null struct from a struct of null fields with <see cref="IsNull"/>.
/// </para>
/// <para>
/// <b>Building.</b> The mutable builder (<see cref="ColumnVectors.Create(DataType,int)"/> or the
/// <see cref="StructColumnVector(StructType,int)"/> ctor) owns one mutable child per field. To append
/// a row, advance <b>every</b> field child by exactly one element (through <see cref="Child(int)"/>
/// cast to <see cref="MutableColumnVector"/>) and then commit the row with <see cref="EndStruct"/>
/// (a non-null struct) or <see cref="AppendNull"/> (a null struct). Both commits require the children
/// to already be advanced, keeping the parent and children length-aligned. The top-level scalar
/// mutators (<see cref="AppendValue{T}"/>, <see cref="AppendBytes"/>, <see cref="SetValue{T}"/>) are
/// unavailable — a struct has no flat scalar value.
/// </para>
/// <para>
/// <see cref="Select"/> (row gather) is not implemented in this increment; see its remarks.
/// </para>
/// </remarks>
public sealed class StructColumnVector : MutableColumnVector
{
    private readonly StructType _structType;
    private readonly ColumnVector[] _children;
    private byte[] _validity;
    private readonly int _offset;
    private int _length;
    private int _nullCount;
    private readonly bool _mutable;
    private bool _sealed;

    /// <summary>
    /// Creates an empty, mutable struct builder for <paramref name="type"/> with one mutable child
    /// per field (each built through <see cref="ColumnVectors.Create(DataType,int)"/> at the given
    /// <paramref name="capacity"/>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public StructColumnVector(StructType type, int capacity)
        : base(type)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _structType = type;

        int initial = Math.Max(capacity, 1);
        _children = new ColumnVector[type.Count];
        for (int i = 0; i < type.Count; i++)
        {
            _children[i] = ColumnVectors.Create(type[i].DataType, capacity);
        }

        _validity = new byte[Bitmap.ByteCount(initial)];
        _mutable = true;
    }

    /// <summary>
    /// Wraps already-built field <paramref name="children"/> as an immutable struct vector — the
    /// natural path when each field column has been produced independently (for example a columnar
    /// decoder). Every child must report the field's declared type and an equal length; that shared
    /// length is this vector's <see cref="Length"/>.
    /// </summary>
    /// <param name="type">The struct type.</param>
    /// <param name="children">One child per field, in field order; each <c>Length</c> equal.</param>
    /// <param name="nulls">
    /// Optional per-row null flags where <c>nulls[i] == true</c> marks logical row <c>i</c> a null
    /// struct. An empty span means no null rows. When non-empty its length must equal the children's
    /// length.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> or <paramref name="children"/> (or a child) is null.</exception>
    /// <exception cref="ArgumentException">
    /// The child count, a child's type, a child's length, or <paramref name="nulls"/>'s length is
    /// inconsistent with the struct type.
    /// </exception>
    public StructColumnVector(StructType type, IReadOnlyList<ColumnVector> children, ReadOnlySpan<bool> nulls = default)
        : base(type)
    {
        ArgumentNullException.ThrowIfNull(children);
        _structType = type;

        if (children.Count != type.Count)
        {
            throw new ArgumentException(
                $"Struct type '{type.SimpleString}' has {type.Count} field(s) but {children.Count} child vector(s) "
                + "were supplied.", nameof(children));
        }

        int length = children.Count > 0
            ? (children[0] ?? throw new ArgumentNullException(nameof(children), "Child vector for field 0 is null.")).Length
            : nulls.Length;
        var copy = new ColumnVector[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            ColumnVector child = children[i]
                ?? throw new ArgumentNullException(nameof(children), $"Child vector for field {i} is null.");

            if (!child.Type.Equals(type[i].DataType))
            {
                throw new ArgumentException(
                    $"Child vector for field {i} ('{type[i].Name}') has type '{child.Type.SimpleString}' but the "
                    + $"struct declares '{type[i].DataType.SimpleString}'.", nameof(children));
            }

            if (child.Length != length)
            {
                throw new ArgumentException(
                    $"Child vector for field {i} ('{type[i].Name}') has length {child.Length} but field 0 has "
                    + $"length {length}; every struct child must be equal length.", nameof(children));
            }

            copy[i] = child;
        }

        (_validity, _nullCount) = NestedValidity.Build(nulls, length);
        _children = copy;
        _length = length;
        _mutable = false;
    }

    private StructColumnVector(
        StructType type, ColumnVector[] children, byte[] validity, int offset, int length, int nullCount)
        : base(type)
    {
        _structType = type;
        _children = children;
        _validity = validity;
        _offset = offset;
        _length = length;
        _nullCount = nullCount;
        _mutable = false;
    }

    /// <summary>The number of fields (child vectors) in this struct.</summary>
    public int FieldCount => _children.Length;

    /// <inheritdoc/>
    public override int Length => _length;

    /// <inheritdoc/>
    public override int Offset => _offset;

    /// <inheritdoc/>
    public override bool HasNulls => _nullCount > 0;

    /// <inheritdoc/>
    public override int NullCount => _nullCount;

    /// <summary>
    /// The child vector for field <paramref name="ordinal"/>, aligned to this vector's logical rows
    /// (row <c>i</c> of the returned child is row <c>i</c> of this struct). For a whole/just-built
    /// vector this is the child itself; for a <see cref="Slice"/> it is a zero-copy sliced view (a
    /// small allocation — hoist it if reading repeatedly). While building, cast the returned child to
    /// <see cref="MutableColumnVector"/> to append the field's value before committing the row.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is outside <c>[0, FieldCount)</c>.</exception>
    public ColumnVector Child(int ordinal)
    {
        if ((uint)ordinal >= (uint)_children.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal), ordinal, $"Field ordinal must be in [0, {_children.Length}).");
        }

        ColumnVector child = _children[ordinal];

        // A live builder exposes the raw mutable child (offset 0) so the caller can append into it;
        // slicing it here would seal it mid-build. A read view aligns the child to its window.
        if (_mutable)
        {
            return child;
        }

        return _offset == 0 && _length == child.Length ? child : child.Slice(_offset, _length);
    }

    /// <summary>The child vector for the field named <paramref name="name"/> (case-sensitive).</summary>
    /// <exception cref="KeyNotFoundException">The struct has no field with that name.</exception>
    public ColumnVector Child(string name)
    {
        int ordinal = _structType.IndexOf(name);
        return ordinal >= 0
            ? Child(ordinal)
            : throw new KeyNotFoundException($"No field named '{name}' in {_structType.SimpleString}.");
    }

    /// <inheritdoc/>
    public override bool IsNull(int index)
    {
        CheckIndex(index);
        return !Bitmap.Get(_validity, _offset + index);
    }

    /// <summary>Not supported: a struct has no flat scalar span. Read fields through <see cref="Child(int)"/>.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override ReadOnlySpan<T> GetValues<T>() =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a struct and has no flat scalar span; read its fields via Child(ordinal).");

    /// <summary>Not supported: a struct carries no variable-width bytes at the top level.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override ReadOnlySpan<byte> GetBytes(int index) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a struct; read its fields via Child(ordinal), not GetBytes.");

    /// <inheritdoc/>
    public override ColumnVector Slice(int offset, int length)
    {
        CheckRange(offset, length);

        // Zero-copy: share the children and validity buffer; the window re-bases logical row 0 via _offset.
        // Sealing this builder blocks further row commits (EndStruct/AppendNull) so the parent's validity
        // buffer is not resized under the view. NOTE: the shared child vectors are NOT sealed — a caller that
        // retained a mutable child reference from Child(i) BEFORE slicing could still mutate it and desync
        // this view; the contract is "build fully before slicing" (child-seal propagation tracked in #575).
        _sealed = true;

        int absoluteOffset = _offset + offset;
        int nulls = Bitmap.CountNulls(_validity, absoluteOffset, length);
        return new StructColumnVector(_structType, _children, _validity, absoluteOffset, length, nulls);
    }

    /// <summary>
    /// Not supported in this increment. A correct row gather over a struct must materialize gathered
    /// children (and, for nested children, recurse), which the nested-representation increment (#570)
    /// deliberately defers rather than returning a partial/wrong view. Slice for contiguous sub-ranges,
    /// or materialize. Late-materialized nested <see cref="Select"/> is tracked on the #546 line.
    /// </summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override ColumnVector Select(SelectionVector selection) =>
        throw new NotSupportedException(
            $"Select (row gather) over a struct column ('{Type.SimpleString}') is not supported in the nested "
            + "representation increment (#570); gathered/late-materialized nested Select is a follow-up (#546). "
            + "Use Slice for a contiguous sub-range or materialize the column.");

    /// <summary>Seals the owner so a selection/slice view shares the buffers safely.</summary>
    protected override void SealForView() => _sealed = true;

    /// <summary>Not supported: a struct has no flat scalar value. Append into each field child, then <see cref="EndStruct"/>.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override void AppendValue<T>(T value) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a struct; append into each field child then call EndStruct/AppendNull.");

    /// <summary>Not supported: a struct has no flat scalar value. Append into each field child, then <see cref="EndStruct"/>.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override void AppendBytes(ReadOnlySpan<byte> value) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a struct; append into each field child then call EndStruct/AppendNull.");

    /// <summary>
    /// Commits a non-null struct row. Every field child must already have been advanced by exactly one
    /// element since the last committed row (so children stay length-aligned with the struct).
    /// </summary>
    /// <exception cref="InvalidOperationException">The vector is read-only/sealed, or a child is not advanced by exactly one row.</exception>
    public void EndStruct() => CommitRow(isNull: false);

    /// <summary>
    /// Commits a null struct row. As with <see cref="EndStruct"/>, every field child must already have
    /// been advanced by one element (typically a null) so children stay length-aligned.
    /// </summary>
    /// <exception cref="InvalidOperationException">The vector is read-only/sealed, or a child is not advanced by exactly one row.</exception>
    public override void AppendNull() => CommitRow(isNull: true);

    /// <summary>Not supported: a struct has no flat scalar value.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public override void SetValue<T>(int index, T value) =>
        throw new InvalidOperationException(
            $"Vector of type '{Type.SimpleString}' is a struct and does not support in-place SetValue.");

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
        for (int i = 0; i < _children.Length; i++)
        {
            ((MutableColumnVector)_children[i]).Clear();
        }

        _length = 0;
        _nullCount = 0;
        Array.Clear(_validity);
    }

    private void CommitRow(bool isNull)
    {
        RequireMutable();
        EnsureRowCapacity(_length + 1);
        for (int i = 0; i < _children.Length; i++)
        {
            if (_children[i].Length != _length + 1)
            {
                throw new InvalidOperationException(
                    $"Field child {i} ('{_structType[i].Name}') has length {_children[i].Length}; expected "
                    + $"{_length + 1}. Append exactly one value to every field child before committing a struct row.");
            }
        }

        Bitmap.Set(_validity, _length, !isNull);
        _length++;
        if (isNull)
        {
            _nullCount++;
        }
    }

    private void EnsureRowCapacity(int requiredRows)
    {
        int requiredBytes = Bitmap.ByteCount(requiredRows);
        if (requiredBytes <= _validity.Length)
        {
            return;
        }

        Array.Resize(ref _validity, Math.Max(requiredBytes, _validity.Length * 2));
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
