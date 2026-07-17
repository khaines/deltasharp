using DeltaSharp.Types;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// Extracts the field <see cref="FieldName"/> (at <see cref="Ordinal"/>) from a struct-typed
/// <see cref="Child"/> — Catalyst's <c>GetStructField</c>, the resolved form of a nested reference
/// <c>s.f</c> (#580). The analyzer builds it from an <see cref="UnresolvedAttribute"/> whose leading
/// part resolves to a struct column (chaining one node per path segment, so <c>s.a.b</c> lowers to
/// <c>GetStructField(GetStructField(s, …a), …b)</c>). Its <see cref="Type"/> is the field's declared
/// type, and it is nullable when either the struct value is null (extracting a field of a null struct
/// yields null) OR the field itself is nullable.
/// </summary>
internal sealed class GetStructField : Expression
{
    /// <summary>Creates <c>child.<paramref name="fieldName"/></c>, extracting the struct field at
    /// <paramref name="ordinal"/>.</summary>
    /// <param name="child">The struct-typed operand.</param>
    /// <param name="ordinal">The zero-based field index within the child's struct type.</param>
    /// <param name="fieldName">The field's name (for rendering and equality).</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> or <paramref name="fieldName"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is negative.</exception>
    public GetStructField(Expression child, int ordinal, string fieldName)
        : base(Unary(child))
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        Ordinal = ordinal;
        FieldName = fieldName;
    }

    /// <summary>The struct-typed operand the field is extracted from.</summary>
    public Expression Child => Children[0];

    /// <summary>The zero-based field index within the child's struct type.</summary>
    public int Ordinal { get; }

    /// <summary>The extracted field's name.</summary>
    public string FieldName { get; }

    private StructField? Field =>
        Child.Type is StructType structType && Ordinal < structType.Count ? structType[Ordinal] : null;

    /// <inheritdoc/>
    public override DataType? Type => Field?.DataType;

    /// <inheritdoc/>
    public override bool Nullable => Child.Nullable || (Field?.Nullable ?? true);

    /// <inheritdoc/>
    public override string NodeName => "GetStructField";

    /// <inheritdoc/>
    public override string SimpleString => $"{Child.SimpleString}.{FieldName}";

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 1, NodeName);
        return ReferenceEquals(newChildren[0], Child)
            ? this
            : new GetStructField(newChildren[0], Ordinal, FieldName);
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other)
    {
        var field = (GetStructField)other;
        return Ordinal == field.Ordinal
            && string.Equals(FieldName, field.FieldName, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    protected override int NodeHashCode() =>
        PlanHash.Combine(
            PlanHash.Combine(PlanHash.Seed, Ordinal),
            FieldName.GetHashCode(StringComparison.Ordinal));
}
