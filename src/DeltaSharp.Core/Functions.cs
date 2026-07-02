using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;

namespace DeltaSharp;

/// <summary>
/// The entry points for building <see cref="Column"/> expressions, equivalent to Apache Spark's
/// <c>org.apache.spark.sql.functions</c>. DeltaSharp mirrors Spark's semantics while using the
/// .NET-idiomatic PascalCase names <see cref="Col(string)"/>, <see cref="Column(string)"/>, and
/// <see cref="Lit(object?)"/> (Spark's lowercase <c>col</c>/<c>lit</c> are not valid .NET member
/// names, and the rest of the public surface — <see cref="SparkSession"/>, <see cref="DataFrame"/> —
/// is already PascalCase).
/// </summary>
/// <remarks>
/// Every method here is <b>lazy</b>: it records intent by wrapping an immutable node of the internal
/// logical expression IR and performs no schema lookup and no evaluation (ADR-0001). A column
/// reference stays <b>unresolved</b> until the analyzer (FEAT-04.5) binds it.
/// </remarks>
public static class Functions
{
    private static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    /// <summary>
    /// Returns a <see cref="DeltaSharp.Column"/> that references the column named
    /// <paramref name="columnName"/>, mirroring Spark's <c>functions.col(colName)</c>. The reference
    /// is <b>unresolved</b> — no schema is consulted — until the analyzer binds it. The wildcard
    /// <c>"*"</c> and a qualified <c>"t.*"</c> produce a star that expands to all (qualified) columns
    /// at analysis, matching Spark.
    /// </summary>
    /// <param name="columnName">The column name, or <c>"*"</c>/<c>"t.*"</c> for a star.</param>
    /// <returns>An unresolved column reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is null or empty.</exception>
    public static Column Col(string columnName)
    {
        ArgumentException.ThrowIfNullOrEmpty(columnName);

        if (columnName == "*")
        {
            return new Column(new UnresolvedStar());
        }

        if (columnName.EndsWith(".*", StringComparison.Ordinal))
        {
            string[] target = columnName[..^2].Split('.');
            return new Column(new UnresolvedStar(target));
        }

        return new Column(new UnresolvedAttribute(columnName));
    }

    /// <summary>
    /// An alias for <see cref="Col(string)"/>, mirroring Spark's <c>functions.column(colName)</c>.
    /// </summary>
    /// <param name="columnName">The column name, or <c>"*"</c>/<c>"t.*"</c> for a star.</param>
    /// <returns>An unresolved column reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is null or empty.</exception>
    public static Column Column(string columnName) => Col(columnName);

    /// <summary>
    /// Returns a literal <see cref="DeltaSharp.Column"/> for <paramref name="value"/>, mirroring
    /// Spark's <c>functions.lit(value)</c>. The .NET scalar type is mapped to its ADR-0008
    /// <see cref="DataType"/>; a <see langword="null"/> value becomes a typed SQL <c>NULL</c> of
    /// <see cref="NullType"/>. Building a literal performs no work.
    /// </summary>
    /// <remarks>
    /// Supported .NET types and their DataType mapping:
    /// <list type="table">
    /// <listheader><term>.NET type</term><description>DataType</description></listheader>
    /// <item><term><see cref="bool"/></term><description><see cref="BooleanType"/></description></item>
    /// <item><term><see cref="sbyte"/></term><description><see cref="ByteType"/> (signed <c>tinyint</c>)</description></item>
    /// <item><term><see cref="byte"/></term><description><see cref="ShortType"/> — widened, see note below</description></item>
    /// <item><term><see cref="short"/></term><description><see cref="ShortType"/></description></item>
    /// <item><term><see cref="int"/></term><description><see cref="IntegerType"/></description></item>
    /// <item><term><see cref="long"/></term><description><see cref="LongType"/></description></item>
    /// <item><term><see cref="float"/></term><description><see cref="FloatType"/></description></item>
    /// <item><term><see cref="double"/></term><description><see cref="DoubleType"/></description></item>
    /// <item><term><see cref="string"/></term><description><see cref="StringType"/></description></item>
    /// <item><term><see cref="byte"/><c>[]</c></term><description><see cref="BinaryType"/></description></item>
    /// <item><term><see cref="decimal"/></term><description><see cref="DecimalType"/> (precision/scale from the value)</description></item>
    /// <item><term><see cref="DateOnly"/></term><description><see cref="DateType"/></description></item>
    /// <item><term><see cref="DateTime"/></term><description><see cref="DateType"/> — date component (see note below)</description></item>
    /// <item><term><see cref="DateTimeOffset"/></term><description><see cref="TimestampType"/></description></item>
    /// <item><term><see langword="null"/></term><description><see cref="NullType"/></description></item>
    /// </list>
    /// <para>
    /// <b>Byte note.</b> Spark's <see cref="ByteType"/> is a <i>signed</i> 8-bit integer (.NET
    /// <see cref="sbyte"/>), so a .NET <see cref="byte"/> (unsigned, 0–255) does not fit for values
    /// above 127. To avoid silently truncating/wrapping, <c>Lit((byte)x)</c> is <b>widened</b> to
    /// <see cref="ShortType"/>, which losslessly holds every <see cref="byte"/>. Pass an
    /// <see cref="sbyte"/> to get a <see cref="ByteType"/> literal.
    /// </para>
    /// <para>
    /// <b>DateTime note.</b> A <see cref="DateTime"/> maps to <see cref="DateType"/> using its date
    /// component; the time-of-day is not represented by <see cref="DateType"/>. Pass a
    /// <see cref="DateTimeOffset"/> for a <see cref="TimestampType"/> literal.
    /// </para>
    /// </remarks>
    /// <param name="value">The literal value, or <see langword="null"/> for a typed SQL <c>NULL</c>.</param>
    /// <returns>A literal column.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is a .NET type with no supported literal mapping; the message names
    /// the offending type.
    /// </exception>
    public static Column Lit(object? value)
    {
        Literal literal = value switch
        {
            null => Literal.Null(NullType.Instance),
            bool b => Literal.OfBoolean(b),
            sbyte sb => Literal.OfByte(sb),
            byte ub => Literal.OfShort(ub),
            short s => Literal.OfShort(s),
            int i => Literal.OfInt(i),
            long l => Literal.OfLong(l),
            float f => Literal.OfFloat(f),
            double d => Literal.OfDouble(d),
            string str => Literal.OfString(str),
            byte[] bytes => Literal.OfBinary(bytes),
            decimal dec => DecimalLiteral(dec),
            DateOnly date => DateLiteral(date),
            DateTime dt => DateLiteral(DateOnly.FromDateTime(dt)),
            DateTimeOffset dto => TimestampLiteral(dto),
            _ => throw new ArgumentException(
                $"Cannot create a literal from an unsupported .NET type '{value.GetType()}'. "
                + "Supported types are bool, sbyte, byte, short, int, long, float, double, string, "
                + "byte[], decimal, DateOnly, DateTime, DateTimeOffset, and null.",
                nameof(value)),
        };

        return new Column(literal);
    }

    private static Literal DateLiteral(DateOnly date) =>
        Literal.OfDate(date.DayNumber - UnixEpochDate.DayNumber);

    private static Literal TimestampLiteral(DateTimeOffset value) =>
        Literal.OfTimestamp((value - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerMicrosecond);

    private static Literal DecimalLiteral(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(value, bits);

        int scale = (bits[3] >> 16) & 0x7F;
        bool negative = (bits[3] & unchecked((int)0x80000000)) != 0;
        UInt128 magnitude =
            ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0];
        Int128 unscaled = negative ? -(Int128)magnitude : (Int128)magnitude;

        int precision = Math.Max(Math.Max(CountDigits(magnitude), scale), DecimalType.MinPrecision);
        return Literal.OfDecimal(unscaled, new DecimalType(precision, scale));
    }

    private static int CountDigits(UInt128 value)
    {
        int digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }

        return digits;
    }
}
