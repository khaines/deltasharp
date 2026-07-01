namespace DeltaSharp.Types;

/// <summary>
/// The Spark <c>array</c> type: an ordered collection of <see cref="ElementType"/> values.
/// </summary>
public sealed class ArrayType : DataType
{
    /// <summary>Creates an array type.</summary>
    /// <param name="elementType">The element type.</param>
    /// <param name="containsNull">Whether elements may be null (default <see langword="true"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="elementType"/> is null.</exception>
    public ArrayType(DataType elementType, bool containsNull = true)
    {
        ArgumentNullException.ThrowIfNull(elementType);
        ElementType = elementType;
        ContainsNull = containsNull;
    }

    /// <summary>The element type.</summary>
    public DataType ElementType { get; }

    /// <summary>Whether elements may be null (the array's null-of-element flag, per Spark).</summary>
    public bool ContainsNull { get; }

    /// <inheritdoc/>
    public override string TypeName => "array";

    /// <inheritdoc/>
    public override string SimpleString => $"array<{ElementType.SimpleString}>";

    /// <inheritdoc/>
    public override bool Equals(DataType? other) =>
        other is ArrayType array
        && array.ContainsNull == ContainsNull
        && array.ElementType.Equals(ElementType);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        StableHash.Combine(
            StableHash.OfString("array"),
            StableHash.Combine(ElementType.GetHashCode(), ContainsNull ? 1 : 0));
}
