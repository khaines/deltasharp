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
}
