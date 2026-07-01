namespace DeltaSharp.Types;

/// <summary>
/// Base for the parameterless, singleton atomic types (the v1 primitives plus
/// <see cref="DateType"/>, <see cref="TimestampType"/>, and <see cref="NullType"/>). Each
/// concrete atomic type is a singleton reached through its <c>Instance</c> property, so two
/// references to the same atomic type are equal by type identity.
/// </summary>
/// <remarks>
/// <see cref="DecimalType"/> is a leaf scalar type too, but it is parameterized
/// (precision/scale) and therefore derives from <see cref="DataType"/> directly rather than
/// from <see cref="AtomicType"/>.
/// </remarks>
public abstract class AtomicType : DataType
{
    private protected AtomicType()
    {
    }

    /// <inheritdoc/>
    public sealed override bool Equals(DataType? other) =>
        other is not null && other.GetType() == GetType();

    /// <inheritdoc/>
    public sealed override int GetHashCode() => StableHash.OfString(TypeName);
}

/// <summary>The Spark <c>boolean</c> type. Physical layout: 1 byte per value.</summary>
public sealed class BooleanType : AtomicType
{
    private BooleanType()
    {
    }

    /// <summary>The singleton instance.</summary>
    public static BooleanType Instance { get; } = new();

    /// <inheritdoc/>
    public override string TypeName => "boolean";

    /// <inheritdoc/>
    public override string SimpleString => "boolean";
}

/// <summary>The Spark <c>byte</c> (<c>tinyint</c>) type: signed 8-bit integer.</summary>
public sealed class ByteType : AtomicType
{
    private ByteType()
    {
    }

    /// <summary>The singleton instance.</summary>
    public static ByteType Instance { get; } = new();

    /// <inheritdoc/>
    public override string TypeName => "byte";

    /// <inheritdoc/>
    public override string SimpleString => "tinyint";
}

/// <summary>The Spark <c>short</c> (<c>smallint</c>) type: signed 16-bit integer.</summary>
public sealed class ShortType : AtomicType
{
    private ShortType()
    {
    }

    /// <summary>The singleton instance.</summary>
    public static ShortType Instance { get; } = new();

    /// <inheritdoc/>
    public override string TypeName => "short";

    /// <inheritdoc/>
    public override string SimpleString => "smallint";
}

/// <summary>The Spark <c>integer</c> (<c>int</c>) type: signed 32-bit integer.</summary>
public sealed class IntegerType : AtomicType
{
    private IntegerType()
    {
    }

    /// <summary>The singleton instance.</summary>
    public static IntegerType Instance { get; } = new();

    /// <inheritdoc/>
    public override string TypeName => "integer";

    /// <inheritdoc/>
    public override string SimpleString => "int";
}

/// <summary>The Spark <c>long</c> (<c>bigint</c>) type: signed 64-bit integer.</summary>
public sealed class LongType : AtomicType
{
    private LongType()
    {
    }

    /// <summary>The singleton instance.</summary>
    public static LongType Instance { get; } = new();

    /// <inheritdoc/>
    public override string TypeName => "long";

    /// <inheritdoc/>
    public override string SimpleString => "bigint";
}

/// <summary>The Spark <c>float</c> type: IEEE-754 single-precision.</summary>
public sealed class FloatType : AtomicType
{
    private FloatType()
    {
    }

    /// <summary>The singleton instance.</summary>
    public static FloatType Instance { get; } = new();

    /// <inheritdoc/>
    public override string TypeName => "float";

    /// <inheritdoc/>
    public override string SimpleString => "float";
}

/// <summary>The Spark <c>double</c> type: IEEE-754 double-precision.</summary>
public sealed class DoubleType : AtomicType
{
    private DoubleType()
    {
    }

    /// <summary>The singleton instance.</summary>
    public static DoubleType Instance { get; } = new();

    /// <inheritdoc/>
    public override string TypeName => "double";

    /// <inheritdoc/>
    public override string SimpleString => "double";
}

/// <summary>The Spark <c>string</c> type: UTF-8 text. Physical layout: variable length.</summary>
public sealed class StringType : AtomicType
{
    private StringType()
    {
    }

    /// <summary>The singleton instance.</summary>
    public static StringType Instance { get; } = new();

    /// <inheritdoc/>
    public override string TypeName => "string";

    /// <inheritdoc/>
    public override string SimpleString => "string";
}

/// <summary>The Spark <c>binary</c> type: opaque bytes. Physical layout: variable length.</summary>
public sealed class BinaryType : AtomicType
{
    private BinaryType()
    {
    }

    /// <summary>The singleton instance.</summary>
    public static BinaryType Instance { get; } = new();

    /// <inheritdoc/>
    public override string TypeName => "binary";

    /// <inheritdoc/>
    public override string SimpleString => "binary";
}

/// <summary>
/// The Spark <c>date</c> type: a calendar date stored as the number of days from the Unix
/// epoch. Physical layout: 4 bytes (a 32-bit day count).
/// </summary>
public sealed class DateType : AtomicType
{
    private DateType()
    {
    }

    /// <summary>The singleton instance.</summary>
    public static DateType Instance { get; } = new();

    /// <inheritdoc/>
    public override string TypeName => "date";

    /// <inheritdoc/>
    public override string SimpleString => "date";
}

/// <summary>
/// The Spark <c>timestamp</c> type: a UTC-normalized instant stored as microseconds from the
/// Unix epoch. Physical layout: 8 bytes (a 64-bit microsecond count). A session-local
/// (no-time-zone) variant is deferred — see the EPIC-02 open questions.
/// </summary>
public sealed class TimestampType : AtomicType
{
    private TimestampType()
    {
    }

    /// <summary>The singleton instance.</summary>
    public static TimestampType Instance { get; } = new();

    /// <inheritdoc/>
    public override string TypeName => "timestamp";

    /// <inheritdoc/>
    public override string SimpleString => "timestamp";
}

/// <summary>
/// The Spark <c>void</c> (null) type — the type of a bare <c>NULL</c> literal. It is a valid
/// member of the type system (it participates in equality, validation, and serialization)
/// but has <b>no physical representation</b> — the concrete case behind STORY-02.5.1 AC4's
/// explicit unsupported-layout path. A column of this type must be widened to a concrete type
/// before it can be materialized.
/// </summary>
public sealed class NullType : AtomicType
{
    private NullType()
    {
    }

    /// <summary>The singleton instance.</summary>
    public static NullType Instance { get; } = new();

    /// <inheritdoc/>
    public override string TypeName => "void";

    /// <inheritdoc/>
    public override string SimpleString => "void";
}
