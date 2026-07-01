namespace DeltaSharp.Engine.Types;

/// <summary>
/// The Spark <c>map</c> type: an association from <see cref="KeyType"/> values to
/// <see cref="ValueType"/> values. Keys are always non-null (Spark's <c>MapType</c> has no
/// key-null flag); only values carry <see cref="ValueContainsNull"/>.
/// </summary>
/// <remarks>
/// The key-type check (AC2) is intentionally stricter than Spark and <b>non-recursive</b>: a
/// directly <see cref="NullType"/> or <see cref="MapType"/> key is rejected, but a key that
/// merely contains one (e.g. <c>array&lt;void&gt;</c>) is permitted in v1.
/// </remarks>
public sealed class MapType : DataType
{
    /// <summary>Creates a map type, validating the key type (STORY-02.5.1 AC2).</summary>
    /// <param name="keyType">The key type. Must not be the null type or a map type.</param>
    /// <param name="valueType">The value type.</param>
    /// <param name="valueContainsNull">Whether values may be null (default <see langword="true"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="keyType"/> or <paramref name="valueType"/> is null.</exception>
    /// <exception cref="SchemaValidationException">The key type is unsupported (null type or map type).</exception>
    public MapType(DataType keyType, DataType valueType, bool valueContainsNull = true)
    {
        ArgumentNullException.ThrowIfNull(keyType);
        ArgumentNullException.ThrowIfNull(valueType);

        if (keyType is NullType or MapType)
        {
            throw new SchemaValidationException(
                $"Map key type '{keyType.SimpleString}' is not supported; "
                + "map keys must not be the null type or a map type.");
        }

        KeyType = keyType;
        ValueType = valueType;
        ValueContainsNull = valueContainsNull;
    }

    /// <summary>The key type (never null-valued at runtime).</summary>
    public DataType KeyType { get; }

    /// <summary>The value type.</summary>
    public DataType ValueType { get; }

    /// <summary>Whether values may be null.</summary>
    public bool ValueContainsNull { get; }

    /// <inheritdoc/>
    public override string TypeName => "map";

    /// <inheritdoc/>
    public override string SimpleString => $"map<{KeyType.SimpleString},{ValueType.SimpleString}>";

    /// <inheritdoc/>
    public override bool Equals(DataType? other) =>
        other is MapType map
        && map.ValueContainsNull == ValueContainsNull
        && map.KeyType.Equals(KeyType)
        && map.ValueType.Equals(ValueType);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        StableHash.Combine(
            StableHash.OfString("map"),
            StableHash.Combine(
                KeyType.GetHashCode(),
                StableHash.Combine(ValueType.GetHashCode(), ValueContainsNull ? 1 : 0)));
}
