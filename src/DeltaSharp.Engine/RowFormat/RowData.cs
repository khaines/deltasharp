using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.RowFormat;

/// <summary>
/// Thrown when binary-row encoding or decoding fails — an unsupported type, a value whose CLR
/// type does not match its field's <see cref="DataType"/>, or malformed/truncated row bytes. The
/// untrusted-input case (malformed/truncated spill or shuffle bytes) is refined by
/// <see cref="RowValidationException"/>, so callers can catch either the general or the specific
/// failure (STORY-02.4.2 AC4).
/// </summary>
public class RowFormatException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public RowFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    public RowFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// An in-memory row value the binary-row encoder/decoder round-trips. Each entry pairs with a
/// field of <see cref="Schema"/>; a <see langword="null"/> entry is SQL NULL. The CLR value for a
/// field follows the type → value mapping: <c>boolean→bool</c>, <c>byte→sbyte</c>,
/// <c>short→short</c>, <c>int/date→int</c>, <c>long/timestamp→long</c>, <c>float→float</c>,
/// <c>double→double</c>, <c>decimal→Int128</c> (unscaled), <c>string→string</c>,
/// <c>binary→byte[]</c>, <c>array→ArrayData</c>, <c>map→MapData</c>, <c>struct→RowData</c>.
/// </summary>
public sealed class RowData : IEquatable<RowData>
{
    private readonly object?[] _values;

    /// <summary>Creates a row from <paramref name="values"/> (copied), one per field of <paramref name="schema"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> or <paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="values"/> length does not equal the field count.</exception>
    public RowData(StructType schema, params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != schema.Count)
        {
            throw new ArgumentException(
                $"Row has {values.Length} values but schema {schema.SimpleString} has {schema.Count} fields.",
                nameof(values));
        }

        Schema = schema;
        _values = (object?[])values.Clone();
    }

    /// <summary>The row's schema.</summary>
    public StructType Schema { get; }

    /// <summary>The number of fields.</summary>
    public int Count => _values.Length;

    /// <summary>The value at <paramref name="index"/> (<see langword="null"/> = SQL NULL).</summary>
    public object? this[int index] => _values[index];

    /// <inheritdoc/>
    public bool Equals(RowData? other) =>
        other is not null && Schema.Equals(other.Schema) && RowValues.SequenceEquals(_values, other._values);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as RowData);

    /// <inheritdoc/>
    public override int GetHashCode() => RowValues.SequenceHash(Schema.GetHashCode(), _values);
}

/// <summary>An ordered array value: its element <see cref="ElementType"/>, null-of-element flag, and elements (order preserved).</summary>
public sealed class ArrayData : IEquatable<ArrayData>
{
    private readonly object?[] _elements;

    /// <summary>Creates an array of <paramref name="elementType"/> from <paramref name="elements"/> (copied).</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ArrayData(DataType elementType, bool containsNull, params object?[] elements)
    {
        ArgumentNullException.ThrowIfNull(elementType);
        ArgumentNullException.ThrowIfNull(elements);
        ElementType = elementType;
        ContainsNull = containsNull;
        _elements = (object?[])elements.Clone();
    }

    /// <summary>The element type.</summary>
    public DataType ElementType { get; }

    /// <summary>Whether elements may be null.</summary>
    public bool ContainsNull { get; }

    /// <summary>The element count.</summary>
    public int Count => _elements.Length;

    /// <summary>The element at <paramref name="index"/>.</summary>
    public object? this[int index] => _elements[index];

    /// <inheritdoc/>
    public bool Equals(ArrayData? other) =>
        other is not null && ElementType.Equals(other.ElementType) && RowValues.SequenceEquals(_elements, other._elements);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ArrayData);

    /// <inheritdoc/>
    public override int GetHashCode() => RowValues.SequenceHash(ElementType.GetHashCode(), _elements);
}

/// <summary>
/// A map value as parallel <c>keys[i]→values[i]</c> arrays (order and pairing preserved). Keys are
/// never null; values may be null.
/// </summary>
public sealed class MapData : IEquatable<MapData>
{
    private readonly object?[] _keys;
    private readonly object?[] _values;

    /// <summary>Creates a map; <paramref name="keys"/> and <paramref name="values"/> must be the same length.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Lengths differ, or a key is null.</exception>
    public MapData(DataType keyType, DataType valueType, object?[] keys, object?[] values)
    {
        ArgumentNullException.ThrowIfNull(keyType);
        ArgumentNullException.ThrowIfNull(valueType);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(values);
        if (keys.Length != values.Length)
        {
            throw new ArgumentException($"Map has {keys.Length} keys but {values.Length} values.", nameof(values));
        }

        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] is null)
            {
                throw new ArgumentException($"Map key at index {i} is null; map keys must be non-null.", nameof(keys));
            }
        }

        KeyType = keyType;
        ValueType = valueType;
        _keys = (object?[])keys.Clone();
        _values = (object?[])values.Clone();
    }

    /// <summary>The key type.</summary>
    public DataType KeyType { get; }

    /// <summary>The value type.</summary>
    public DataType ValueType { get; }

    /// <summary>The entry count.</summary>
    public int Count => _keys.Length;

    /// <summary>The key at <paramref name="index"/>.</summary>
    public object? Key(int index) => _keys[index];

    /// <summary>The value at <paramref name="index"/>.</summary>
    public object? Value(int index) => _values[index];

    /// <inheritdoc/>
    public bool Equals(MapData? other) =>
        other is not null
        && KeyType.Equals(other.KeyType)
        && ValueType.Equals(other.ValueType)
        && RowValues.SequenceEquals(_keys, other._keys)
        && RowValues.SequenceEquals(_values, other._values);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as MapData);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        RowValues.SequenceHash(RowValues.SequenceHash(KeyType.GetHashCode(), _keys), _values);
}

/// <summary>Structural value equality/hashing for the boxed CLR values rows carry (byte[] by sequence; nested by Equals).</summary>
internal static class RowValues
{
    public static bool SequenceEquals(object?[] a, object?[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (!ValueEquals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static int SequenceHash(int seed, object?[] values)
    {
        var hash = new HashCode();
        hash.Add(seed);
        foreach (object? v in values)
        {
            hash.Add(v is byte[] bytes ? bytes.Length : v);
        }

        return hash.ToHashCode();
    }

    private static bool ValueEquals(object? x, object? y) =>
        (x, y) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            (byte[] bx, byte[] by) => bx.AsSpan().SequenceEqual(by),
            _ => x.Equals(y),
        };
}
