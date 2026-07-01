using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// A constant scalar broadcast across every row of a batch — the simplest computed
/// <see cref="PhysicalExpression"/> leaf beyond <see cref="ColumnReference"/> (STORY-03.4.1). It
/// carries a single resolved value (or SQL <c>NULL</c>) of a fixed <see cref="PhysicalExpression.Type"/>;
/// evaluating it materializes that value into a column of the batch's logical length, so a literal
/// participates in arithmetic (<c>price * 2</c>), comparison (<c>status = 'ok'</c>), and projection
/// (<c>SELECT 1 AS one</c>) exactly like any other operand.
/// </summary>
/// <remarks>
/// The value is stored once in its natural CLR storage shape for the logical type (for example
/// <see cref="int"/> for <see cref="IntegerType"/>/<see cref="DateType"/>, <see cref="long"/> for
/// <see cref="LongType"/>/<see cref="TimestampType"/>, an unscaled <see cref="Int128"/> for
/// <see cref="DecimalType"/>, a UTF-8 <see cref="string"/> for <see cref="StringType"/>). It is read
/// exactly once per batch and broadcast without per-row boxing, so building the node and holding the
/// value performs no row work (the lazy/eager invariant). The node is immutable.
/// </remarks>
public sealed class Literal : PhysicalExpression
{
    private Literal(DataType type, object? value, bool isNull)
        : base(type, isNull)
    {
        Value = value;
        IsNull = isNull;
    }

    /// <summary>Whether this literal is the SQL <c>NULL</c> of its <see cref="PhysicalExpression.Type"/>.</summary>
    public bool IsNull { get; }

    /// <summary>
    /// The constant value in its CLR storage shape (for example <see cref="int"/>, <see cref="long"/>,
    /// <see cref="double"/>, <see cref="Int128"/>, <see cref="string"/>, or <see cref="byte"/><c>[]</c>),
    /// or <see langword="null"/> when <see cref="IsNull"/>.
    /// </summary>
    public object? Value { get; }

    /// <summary>The typed SQL <c>NULL</c> literal of <paramref name="type"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    public static Literal Null(DataType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new Literal(type, null, true);
    }

    /// <summary>A <see cref="BooleanType"/> literal.</summary>
    public static Literal OfBoolean(bool value) => new(BooleanType.Instance, value, false);

    /// <summary>A <see cref="ByteType"/> (signed <c>tinyint</c>) literal.</summary>
    public static Literal OfByte(sbyte value) => new(ByteType.Instance, value, false);

    /// <summary>A <see cref="ShortType"/> (<c>smallint</c>) literal.</summary>
    public static Literal OfShort(short value) => new(ShortType.Instance, value, false);

    /// <summary>An <see cref="IntegerType"/> literal.</summary>
    public static Literal OfInt(int value) => new(IntegerType.Instance, value, false);

    /// <summary>A <see cref="LongType"/> (<c>bigint</c>) literal.</summary>
    public static Literal OfLong(long value) => new(LongType.Instance, value, false);

    /// <summary>A <see cref="FloatType"/> literal.</summary>
    public static Literal OfFloat(float value) => new(FloatType.Instance, value, false);

    /// <summary>A <see cref="DoubleType"/> literal.</summary>
    public static Literal OfDouble(double value) => new(DoubleType.Instance, value, false);

    /// <summary>A <see cref="StringType"/> literal (stored as the source string; encoded to UTF-8 on evaluation).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static Literal OfString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Literal(StringType.Instance, value, false);
    }

    /// <summary>A <see cref="BinaryType"/> literal (the bytes are copied defensively).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static Literal OfBinary(ReadOnlySpan<byte> value) => new(BinaryType.Instance, value.ToArray(), false);

    /// <summary>A <see cref="DateType"/> literal as an epoch-day count.</summary>
    public static Literal OfDate(int epochDay) => new(DateType.Instance, epochDay, false);

    /// <summary>A <see cref="TimestampType"/> literal as a UTC microsecond instant.</summary>
    public static Literal OfTimestamp(long epochMicros) => new(TimestampType.Instance, epochMicros, false);

    /// <summary>
    /// A <see cref="DecimalType"/> literal from an unscaled mantissa interpreted at
    /// <paramref name="type"/>'s scale.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    public static Literal OfDecimal(Int128 unscaled, DecimalType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new Literal(type, unscaled, false);
    }
}
