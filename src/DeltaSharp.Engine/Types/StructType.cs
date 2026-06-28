using System.Collections.ObjectModel;
using System.Text;

namespace DeltaSharp.Engine.Types;

/// <summary>
/// A named field of a <see cref="StructType"/>: a <see cref="Name"/>, its
/// <see cref="DataType"/>, a <see cref="Nullable"/> flag, and optional
/// <see cref="Metadata"/>. Nullability lives here (not on the type), following Spark.
/// </summary>
public sealed class StructField : IEquatable<StructField>
{
    /// <summary>Creates a struct field.</summary>
    /// <param name="name">The field name (non-empty).</param>
    /// <param name="dataType">The field's type.</param>
    /// <param name="nullable">Whether the field may be null (default <see langword="true"/>).</param>
    /// <param name="metadata">Optional field metadata; defaults to <see cref="FieldMetadata.Empty"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="dataType"/> is null.</exception>
    public StructField(string name, DataType dataType, bool nullable = true, FieldMetadata? metadata = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(dataType);
        Name = name;
        DataType = dataType;
        Nullable = nullable;
        Metadata = metadata ?? FieldMetadata.Empty;
    }

    /// <summary>The field name (case-sensitive).</summary>
    public string Name { get; }

    /// <summary>The field's type.</summary>
    public DataType DataType { get; }

    /// <summary>Whether the field may hold null.</summary>
    public bool Nullable { get; }

    /// <summary>The field's metadata (never null; <see cref="FieldMetadata.Empty"/> when absent).</summary>
    public FieldMetadata Metadata { get; }

    /// <inheritdoc/>
    public bool Equals(StructField? other) =>
        other is not null
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && Nullable == other.Nullable
        && DataType.Equals(other.DataType)
        && Metadata.Equals(other.Metadata);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as StructField);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        int hash = StableHash.OfString(Name);
        hash = StableHash.Combine(hash, DataType.GetHashCode());
        hash = StableHash.Combine(hash, Nullable ? 1 : 0);
        hash = StableHash.Combine(hash, Metadata.GetHashCode());
        return hash;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name}:{DataType.SimpleString}";
}

/// <summary>
/// The Spark <c>struct</c> type: an ordered list of named <see cref="StructField"/>s. A
/// DeltaSharp <b>schema</b> is a top-level <see cref="StructType"/> (Spark parity).
/// </summary>
/// <remarks>
/// Field name lookup is case-sensitive and ordinal. Duplicate field names are rejected at
/// construction (STORY-02.5.1 AC2). Names that differ only by case are permitted at the type
/// level — matching Spark's <c>StructType</c>, where case-insensitive ambiguity is a
/// name-resolution concern, not a type-construction error.
/// </remarks>
public sealed class StructType : DataType, IReadOnlyList<StructField>
{
    private readonly StructField[] _fields;
    private readonly ReadOnlyCollection<StructField> _fieldsView;
    private readonly Dictionary<string, int> _indexByName;

    /// <summary>Creates a struct type, validating for duplicate field names (STORY-02.5.1 AC2).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> or any element is null.</exception>
    /// <exception cref="SchemaValidationException">Two fields share the same name.</exception>
    public StructType(IEnumerable<StructField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        _fields = fields.ToArray();

        _indexByName = new Dictionary<string, int>(_fields.Length, StringComparer.Ordinal);
        for (int i = 0; i < _fields.Length; i++)
        {
            StructField field = _fields[i]
                ?? throw new ArgumentNullException(nameof(fields), "Struct field cannot be null.");

            if (!_indexByName.TryAdd(field.Name, i))
            {
                throw new SchemaValidationException(
                    $"Duplicate field name '{field.Name}' in struct (at positions "
                    + $"{_indexByName[field.Name]} and {i}).");
            }
        }

        _fieldsView = Array.AsReadOnly(_fields);
    }

    /// <summary>The empty struct (a schema with no fields).</summary>
    public static StructType Empty { get; } = new(Array.Empty<StructField>());

    /// <summary>The fields, in declared order. The returned view is read-only and cannot be
    /// cast back to a mutable array, preserving the type's immutability.</summary>
    public IReadOnlyList<StructField> Fields => _fieldsView;

    /// <inheritdoc/>
    public int Count => _fields.Length;

    /// <inheritdoc/>
    public StructField this[int index] => _fields[index];

    /// <summary>Gets a field by name (case-sensitive).</summary>
    /// <exception cref="KeyNotFoundException">No field has the given name.</exception>
    public StructField this[string name] =>
        _indexByName.TryGetValue(name, out int i)
            ? _fields[i]
            : throw new KeyNotFoundException($"No field named '{name}' in {SimpleString}.");

    /// <summary>Tries to get a field by name (case-sensitive).</summary>
    public bool TryGetField(string name, out StructField field)
    {
        if (_indexByName.TryGetValue(name, out int i))
        {
            field = _fields[i];
            return true;
        }

        field = null!;
        return false;
    }

    /// <summary>The position of the field with the given name, or <c>-1</c> if absent.</summary>
    public int IndexOf(string name) => _indexByName.TryGetValue(name, out int i) ? i : -1;

    /// <inheritdoc/>
    public override string TypeName => "struct";

    /// <inheritdoc/>
    public override string SimpleString
    {
        get
        {
            var builder = new StringBuilder("struct<");
            for (int i = 0; i < _fields.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                StructField current = _fields[i];
                builder.Append(current.Name).Append(':').Append(current.DataType.SimpleString);
            }

            return builder.Append('>').ToString();
        }
    }

    /// <inheritdoc/>
    public override bool Equals(DataType? other)
    {
        if (other is not StructType s || s._fields.Length != _fields.Length)
        {
            return false;
        }

        for (int i = 0; i < _fields.Length; i++)
        {
            if (!_fields[i].Equals(s._fields[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // Fold the field count and each field's position so that reordering fields (which
        // produces a different, non-equal type) also changes the hash.
        int hash = StableHash.Combine(StableHash.OfString("struct"), _fields.Length);
        for (int i = 0; i < _fields.Length; i++)
        {
            hash = StableHash.Combine(hash, i);
            hash = StableHash.Combine(hash, _fields[i].GetHashCode());
        }

        return hash;
    }

    /// <inheritdoc/>
    public override bool TryGetPhysicalLayout(out PhysicalLayout layout)
    {
        layout = PhysicalLayout.Nested;
        return true;
    }

    /// <inheritdoc/>
    public IEnumerator<StructField> GetEnumerator() =>
        ((IEnumerable<StructField>)_fields).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        _fields.GetEnumerator();
}
