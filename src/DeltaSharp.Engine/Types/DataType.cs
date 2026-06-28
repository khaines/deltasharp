namespace DeltaSharp.Engine.Types;

/// <summary>
/// Base of the DeltaSharp logical type system — the Spark SQL-compatible descriptor that
/// vectors, binary rows, expressions, and (later) the public API agree on for value shape
/// (ADR-0008). Instances are immutable and compare by structural value.
/// </summary>
/// <remarks>
/// <para>
/// Nullability is intentionally <b>not</b> a property of a <see cref="DataType"/>. Following
/// Spark, it lives on the container that holds a value — <see cref="StructField.Nullable"/>,
/// <see cref="ArrayType.ContainsNull"/>, and <see cref="MapType.ValueContainsNull"/>.
/// </para>
/// <para>
/// The hierarchy is closed: the only subtypes are the atomic singletons
/// (<see cref="AtomicType"/>), <see cref="DecimalType"/>, and the nested
/// <see cref="ArrayType"/>/<see cref="MapType"/>/<see cref="StructType"/>. Consumers may
/// exhaustively pattern-match on it.
/// </para>
/// <para>
/// These contracts live in the unshipped <c>DeltaSharp.Engine</c> assembly; <c>public</c>
/// here is an engine-internal seam, not a shipped NuGet surface
/// (see <c>docs/engineering/design/testing-conventions.md</c>).
/// </para>
/// </remarks>
public abstract class DataType : IEquatable<DataType>
{
    private protected DataType()
    {
    }

    /// <summary>
    /// The Spark-compatible type name used in the schema JSON — for example
    /// <c>"integer"</c>, <c>"array"</c>, or <c>"decimal(10,2)"</c>.
    /// </summary>
    public abstract string TypeName { get; }

    /// <summary>
    /// A deterministic, human-readable canonical form (Spark's <c>catalogString</c>) — for
    /// example <c>"int"</c>, <c>"decimal(10,2)"</c>, <c>"array&lt;int&gt;"</c>, or
    /// <c>"struct&lt;a:int,b:string&gt;"</c>. Stable across processes; used for diagnostics
    /// and deterministic comparison.
    /// </summary>
    public abstract string SimpleString { get; }

    /// <summary>Structural value equality.</summary>
    public abstract bool Equals(DataType? other);

    /// <inheritdoc/>
    public sealed override bool Equals(object? obj) => Equals(obj as DataType);

    /// <summary>A deterministic, process-independent hash consistent with <see cref="Equals(DataType?)"/>.</summary>
    public abstract override int GetHashCode();

    /// <inheritdoc/>
    public sealed override string ToString() => SimpleString;

    /// <summary>Structural value equality.</summary>
    public static bool operator ==(DataType? left, DataType? right) => Equals(left, right);

    /// <summary>Structural value inequality.</summary>
    public static bool operator !=(DataType? left, DataType? right) => !Equals(left, right);

    /// <summary>
    /// Resolves the physical layout a vector or binary-row builder should use for this type
    /// (STORY-02.5.1 AC4).
    /// </summary>
    /// <param name="layout">The resolved layout when the type is physically representable.</param>
    /// <returns>
    /// <see langword="true"/> and a supported <paramref name="layout"/> for representable
    /// types; <see langword="false"/> for types with no physical representation
    /// (for example <see cref="NullType"/>).
    /// </returns>
    public abstract bool TryGetPhysicalLayout(out PhysicalLayout layout);

    /// <summary>
    /// Resolves the physical layout for this type, throwing when the type has no supported
    /// physical representation. Prefer <see cref="TryGetPhysicalLayout"/> to avoid exceptions
    /// on hot paths.
    /// </summary>
    /// <exception cref="UnsupportedTypeException">The type has no physical representation.</exception>
    public PhysicalLayout GetPhysicalLayout()
    {
        if (TryGetPhysicalLayout(out PhysicalLayout layout))
        {
            return layout;
        }

        throw new UnsupportedTypeException(
            $"Type '{SimpleString}' has no supported physical layout.");
    }

    /// <summary>
    /// Serializes this type tree to the Spark-compatible schema JSON (the same format Delta
    /// stores in its log), round-trippable with <see cref="FromJson(string)"/>
    /// (STORY-02.5.1 AC3).
    /// </summary>
    public string ToJson() => SchemaJson.Serialize(this);

    /// <summary>Parses a type tree from Spark-compatible schema JSON produced by <see cref="ToJson"/>.</summary>
    /// <exception cref="SchemaValidationException">The JSON is malformed or describes an invalid/unknown type.</exception>
    public static DataType FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return SchemaJson.Deserialize(json);
    }
}
