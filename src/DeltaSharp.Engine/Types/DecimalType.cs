using System.Globalization;

namespace DeltaSharp.Engine.Types;

/// <summary>
/// The Spark <c>decimal(precision, scale)</c> type: a fixed-point number with
/// <see cref="Precision"/> total significant digits and <see cref="Scale"/> digits to the
/// right of the decimal point.
/// </summary>
/// <remarks>
/// Physical layout is fixed width: the unscaled value fits in an <see cref="long"/> (8 bytes)
/// when <see cref="Precision"/> is at most <see cref="MaxCompactPrecision"/>; otherwise it
/// requires a 128-bit value (16 bytes). The columnar and binary-row layers branch on
/// <see cref="IsCompact"/>.
/// </remarks>
public sealed class DecimalType : DataType
{
    /// <summary>The smallest legal precision.</summary>
    public const int MinPrecision = 1;

    /// <summary>The largest legal precision (Spark parity).</summary>
    public const int MaxPrecision = 38;

    /// <summary>
    /// The largest precision whose unscaled value still fits in a 64-bit integer. Above this,
    /// the physical layout widens to 16 bytes.
    /// </summary>
    public const int MaxCompactPrecision = 18;

    /// <summary>Creates a decimal type, validating precision and scale (STORY-02.5.1 AC2).</summary>
    /// <exception cref="SchemaValidationException">
    /// <paramref name="precision"/> is outside <c>[1, 38]</c>, or <paramref name="scale"/> is
    /// outside <c>[0, precision]</c>.
    /// </exception>
    public DecimalType(int precision, int scale)
    {
        if (precision is < MinPrecision or > MaxPrecision)
        {
            throw new SchemaValidationException(
                $"Decimal precision {precision} is out of range [{MinPrecision}, {MaxPrecision}].");
        }

        if (scale < 0 || scale > precision)
        {
            throw new SchemaValidationException(
                $"Decimal scale {scale} is out of range [0, {precision}] for precision {precision}.");
        }

        Precision = precision;
        Scale = scale;
    }

    /// <summary>Total number of significant digits.</summary>
    public int Precision { get; }

    /// <summary>Number of digits to the right of the decimal point.</summary>
    public int Scale { get; }

    /// <summary>Whether the unscaled value fits in a 64-bit integer (8-byte physical layout).</summary>
    public bool IsCompact => Precision <= MaxCompactPrecision;

    /// <inheritdoc/>
    public override string TypeName =>
        string.Create(CultureInfo.InvariantCulture, $"decimal({Precision},{Scale})");

    /// <inheritdoc/>
    public override string SimpleString => TypeName;

    /// <inheritdoc/>
    public override bool Equals(DataType? other) =>
        other is DecimalType d && d.Precision == Precision && d.Scale == Scale;

    /// <inheritdoc/>
    public override int GetHashCode() =>
        StableHash.Combine(StableHash.OfString("decimal"), StableHash.Combine(Precision, Scale));
}
