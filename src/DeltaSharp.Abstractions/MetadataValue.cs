using System.Diagnostics.CodeAnalysis;

namespace DeltaSharp.Types;

/// <summary>The discriminator for a <see cref="MetadataValue"/> case.</summary>
public enum MetadataValueKind
{
    /// <summary>A JSON <c>null</c> metadata value.</summary>
    Null,

    /// <summary>A string metadata value (the common column-comment case).</summary>
    String,

    /// <summary>An integral metadata value (a JSON integer), for example <c>delta.columnMapping.id</c>.</summary>
    Long,

    /// <summary>A non-integral metadata value (a JSON floating-point number).</summary>
    Double,

    /// <summary>A boolean metadata value, for example <c>delta.identity.allowExplicitInsert</c>.</summary>
    Boolean,

    /// <summary>An ordered array of metadata values.</summary>
    Array,

    /// <summary>A nested metadata object (a <see cref="FieldMetadata"/>).</summary>
    Nested,
}

/// <summary>
/// An immutable, typed metadata value mirroring Spark's
/// <c>org.apache.spark.sql.types.Metadata</c> value model: a value is one of string, long,
/// double, boolean, null, an ordered array of values, or a nested metadata object. Modelling
/// the type losslessly is required for Delta-log schema interop — column-mapping ids
/// (<c>delta.columnMapping.id</c>) and identity metadata (<c>delta.identity.start</c>/<c>.step</c>,
/// <c>delta.identity.allowExplicitInsert</c>) are stored as JSON numbers/booleans, not strings
/// (issue #330).
/// </summary>
/// <remarks>
/// JSON number discrimination mirrors Spark/Jackson: an <b>integral</b> JSON number that fits in
/// <see cref="long"/> parses to <see cref="MetadataValueKind.Long"/>; anything else (a fractional
/// number, an exponent form, or an out-of-range integer) parses to
/// <see cref="MetadataValueKind.Double"/>. So <c>5</c> round-trips as an integer, never <c>5.0</c>
/// or <c>"5"</c>. Equality is structural and by <see cref="Kind"/>, so <c>Long(5)</c>,
/// <c>Double(5.0)</c>, and <c>String("5")</c> are all distinct. Hashing is deterministic and
/// process-stable via <see cref="StableHash"/>. Arrays compare element-wise and order-sensitively
/// (arrays are ordered in Spark).
/// </remarks>
public sealed class MetadataValue : IEquatable<MetadataValue>
{
    private readonly string? _string;
    private readonly long _long;
    private readonly double _double;
    private readonly bool _boolean;
    private readonly IReadOnlyList<MetadataValue>? _array;
    private readonly FieldMetadata? _nested;

    private MetadataValue(
        MetadataValueKind kind,
        string? stringValue = null,
        long longValue = 0L,
        double doubleValue = 0d,
        bool booleanValue = false,
        IReadOnlyList<MetadataValue>? arrayValue = null,
        FieldMetadata? nestedValue = null)
    {
        Kind = kind;
        _string = stringValue;
        _long = longValue;
        _double = doubleValue;
        _boolean = booleanValue;
        _array = arrayValue;
        _nested = nestedValue;
    }

    /// <summary>The case discriminator.</summary>
    public MetadataValueKind Kind { get; }

    /// <summary>The shared <c>null</c> metadata value.</summary>
    public static MetadataValue Null { get; } = new(MetadataValueKind.Null);

    private static MetadataValue True { get; } = new(MetadataValueKind.Boolean, booleanValue: true);

    private static MetadataValue False { get; } = new(MetadataValueKind.Boolean, booleanValue: false);

    /// <summary>Creates a string metadata value.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static MetadataValue String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new MetadataValue(MetadataValueKind.String, stringValue: value);
    }

    /// <summary>Creates an integral (long) metadata value.</summary>
    public static MetadataValue Long(long value) => new(MetadataValueKind.Long, longValue: value);

    /// <summary>Creates a floating-point (double) metadata value.</summary>
    public static MetadataValue Double(double value) => new(MetadataValueKind.Double, doubleValue: value);

    /// <summary>Creates a boolean metadata value.</summary>
    public static MetadataValue Boolean(bool value) => value ? True : False;

    /// <summary>Creates an ordered array metadata value. The list is snapshotted defensively.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> or any element is null.</exception>
    public static MetadataValue Array(IReadOnlyList<MetadataValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new MetadataValue[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            copy[i] = values[i]
                ?? throw new ArgumentException("Metadata array element cannot be null.", nameof(values));
        }

        return new MetadataValue(MetadataValueKind.Array, arrayValue: copy);
    }

    /// <summary>Creates a nested metadata-object value.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static MetadataValue Nested(FieldMetadata value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new MetadataValue(MetadataValueKind.Nested, nestedValue: value);
    }

    /// <summary>Gets the string value.</summary>
    /// <exception cref="InvalidOperationException">This value is not a <see cref="MetadataValueKind.String"/>.</exception>
    public string AsString() =>
        Kind == MetadataValueKind.String ? _string! : throw WrongKind(MetadataValueKind.String);

    /// <summary>Gets the integral value.</summary>
    /// <exception cref="InvalidOperationException">This value is not a <see cref="MetadataValueKind.Long"/>.</exception>
    public long AsLong() =>
        Kind == MetadataValueKind.Long ? _long : throw WrongKind(MetadataValueKind.Long);

    /// <summary>Gets the floating-point value.</summary>
    /// <exception cref="InvalidOperationException">This value is not a <see cref="MetadataValueKind.Double"/>.</exception>
    public double AsDouble() =>
        Kind == MetadataValueKind.Double ? _double : throw WrongKind(MetadataValueKind.Double);

    /// <summary>Gets the boolean value.</summary>
    /// <exception cref="InvalidOperationException">This value is not a <see cref="MetadataValueKind.Boolean"/>.</exception>
    public bool AsBoolean() =>
        Kind == MetadataValueKind.Boolean ? _boolean : throw WrongKind(MetadataValueKind.Boolean);

    /// <summary>Gets the array elements.</summary>
    /// <exception cref="InvalidOperationException">This value is not a <see cref="MetadataValueKind.Array"/>.</exception>
    public IReadOnlyList<MetadataValue> AsArray() =>
        Kind == MetadataValueKind.Array ? _array! : throw WrongKind(MetadataValueKind.Array);

    /// <summary>Gets the nested metadata object.</summary>
    /// <exception cref="InvalidOperationException">This value is not a <see cref="MetadataValueKind.Nested"/>.</exception>
    public FieldMetadata AsNested() =>
        Kind == MetadataValueKind.Nested ? _nested! : throw WrongKind(MetadataValueKind.Nested);

    /// <summary>Gets the string value when this is a <see cref="MetadataValueKind.String"/>.</summary>
    public bool TryGetString([MaybeNullWhen(false)] out string value)
    {
        if (Kind == MetadataValueKind.String)
        {
            value = _string!;
            return true;
        }

        value = null;
        return false;
    }

    /// <inheritdoc/>
    public bool Equals(MetadataValue? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            MetadataValueKind.Null => true,
            MetadataValueKind.String => string.Equals(_string, other._string, StringComparison.Ordinal),
            MetadataValueKind.Long => _long == other._long,
            // Bitwise equality is consistent with the bit-based hash (NaN == NaN, -0.0 != 0.0).
            MetadataValueKind.Double => _double.Equals(other._double),
            MetadataValueKind.Boolean => _boolean == other._boolean,
            MetadataValueKind.Array => ArraysEqual(_array!, other._array!),
            MetadataValueKind.Nested => _nested!.Equals(other._nested),
            _ => false,
        };
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as MetadataValue);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        int hash = StableHash.Combine(StableHash.OfString("metadata-value"), (int)Kind);
        switch (Kind)
        {
            case MetadataValueKind.String:
                hash = StableHash.Combine(hash, StableHash.OfString(_string!));
                break;
            case MetadataValueKind.Long:
                hash = StableHash.Combine(hash, (int)_long);
                hash = StableHash.Combine(hash, (int)(_long >> 32));
                break;
            case MetadataValueKind.Double:
                long bits = BitConverter.DoubleToInt64Bits(_double);
                hash = StableHash.Combine(hash, (int)bits);
                hash = StableHash.Combine(hash, (int)(bits >> 32));
                break;
            case MetadataValueKind.Boolean:
                hash = StableHash.Combine(hash, _boolean ? 1 : 0);
                break;
            case MetadataValueKind.Array:
                for (int i = 0; i < _array!.Count; i++)
                {
                    hash = StableHash.Combine(hash, i);
                    hash = StableHash.Combine(hash, _array[i].GetHashCode());
                }

                break;
            case MetadataValueKind.Nested:
                hash = StableHash.Combine(hash, _nested!.GetHashCode());
                break;
            case MetadataValueKind.Null:
            default:
                break;
        }

        return hash;
    }

    private static bool ArraysEqual(IReadOnlyList<MetadataValue> left, IReadOnlyList<MetadataValue> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private InvalidOperationException WrongKind(MetadataValueKind expected) =>
        new($"Metadata value is {Kind}, not {expected}.");
}
