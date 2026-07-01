using System.Globalization;
using DeltaSharp.Types;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// A typed constant leaf (Catalyst <c>Literal</c>) carrying a single value — or a typed SQL
/// <c>NULL</c> — of a fixed ADR-0008 <see cref="DataType"/> (AC3). It is always resolved; building
/// it does no work and it is immutable. The value is held in its natural CLR storage shape for the
/// logical type (for example <see cref="int"/> for a date epoch-day, <see cref="long"/> for a
/// timestamp epoch-microsecond instant, an unscaled <see cref="Int128"/> for a decimal, a
/// <see cref="string"/> for a string, and <see cref="byte"/><c>[]</c> for binary).
/// </summary>
internal sealed class Literal : Expression
{
    private Literal(DataType type, object? value, bool isNull)
        : base(PlanCollections.Empty<Expression>())
    {
        Type = type;
        Value = value;
        IsNull = isNull;
    }

    /// <inheritdoc/>
    public override DataType Type { get; }

    /// <summary>Whether this literal is the SQL <c>NULL</c> of its <see cref="Type"/>.</summary>
    public bool IsNull { get; }

    /// <summary>The constant value in its CLR storage shape, or <see langword="null"/> when
    /// <see cref="IsNull"/>.</summary>
    public object? Value { get; }

    /// <summary>A SQL <c>NULL</c> literal is nullable; a value literal is not.</summary>
    public override bool Nullable => IsNull;

    /// <inheritdoc/>
    public override string NodeName => "Literal";

    /// <inheritdoc/>
    public override string SimpleString => Render();

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

    /// <summary>A <see cref="StringType"/> literal.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static Literal OfString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Literal(StringType.Instance, value, false);
    }

    /// <summary>A <see cref="BinaryType"/> literal (the bytes are copied defensively).</summary>
    public static Literal OfBinary(ReadOnlySpan<byte> value) =>
        new(BinaryType.Instance, value.ToArray(), false);

    /// <summary>A <see cref="DateType"/> literal as an epoch-day count.</summary>
    public static Literal OfDate(int epochDay) => new(DateType.Instance, epochDay, false);

    /// <summary>A <see cref="TimestampType"/> literal as a UTC microsecond instant.</summary>
    public static Literal OfTimestamp(long epochMicros) =>
        new(TimestampType.Instance, epochMicros, false);

    /// <summary>A <see cref="DecimalType"/> literal from an unscaled mantissa at
    /// <paramref name="type"/>'s scale.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    public static Literal OfDecimal(Int128 unscaled, DecimalType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new Literal(type, unscaled, false);
    }

    /// <inheritdoc/>
    public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren)
    {
        RequireArity(newChildren, 0, NodeName);
        return this;
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(Expression other)
    {
        var literal = (Literal)other;
        if (IsNull != literal.IsNull || !Type.Equals(literal.Type))
        {
            return false;
        }

        if (IsNull)
        {
            return true;
        }

        if (Value is byte[] left && literal.Value is byte[] right)
        {
            return left.AsSpan().SequenceEqual(right);
        }

        return Equals(Value, literal.Value);
    }

    /// <inheritdoc/>
    protected override int NodeHashCode()
    {
        int hash = Type.GetHashCode();
        if (IsNull)
        {
            return PlanHash.Combine(hash, 0);
        }

        int valueHash = Value switch
        {
            byte[] bytes => HashBytes(bytes),
            string s => PlanHash.OfString(s),
            null => 0,
            _ => Value.GetHashCode(),
        };

        return PlanHash.Combine(hash, valueHash);
    }

    private static int HashBytes(byte[] bytes)
    {
        int hash = PlanHash.Seed;
        foreach (byte b in bytes)
        {
            hash = PlanHash.Combine(hash, b);
        }

        return hash;
    }

    private string Render()
    {
        if (IsNull)
        {
            return "null";
        }

        return Value switch
        {
            string s => $"\"{s}\"",
            byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
            bool b => b ? "true" : "false",
            null => "null",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => Value.ToString() ?? "null",
        };
    }
}
