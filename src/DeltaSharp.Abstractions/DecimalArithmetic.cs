namespace DeltaSharp.Types;

/// <summary>
/// Spark-parity decimal result-type, widening, and overflow rules (STORY-02.5.2 AC2). Mirrors
/// Apache Spark's <c>DecimalType</c>/<c>DecimalPrecision</c>: result precision/scale for the
/// binary operators, integral-to-decimal widening, and the bounded clamp that keeps results
/// inside <see cref="DecimalType.MaxPrecision"/>.
/// </summary>
public static class DecimalArithmetic
{
    /// <summary>The maximum decimal scale, matching Spark's <c>MAX_SCALE</c>.</summary>
    public const int MaxScale = DecimalType.MaxPrecision;

    /// <summary>
    /// The smallest scale a clamped result keeps before integer digits win, matching Spark's
    /// <c>MINIMUM_ADJUSTED_SCALE</c>. Below this the result loses fractional digits but never
    /// silently drops integer digits.
    /// </summary>
    public const int MinimumAdjustedScale = 6;

    /// <summary>Spark's <c>DECIMAL(10,0)</c> system default.</summary>
    public static DecimalType SystemDefault { get; } = new(10, 0);

    /// <summary>The decimal that exactly holds any value of an integral/floating <paramref name="type"/> (Spark <c>forType</c>).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    /// <exception cref="UnsupportedTypeException"><paramref name="type"/> has no decimal widening.</exception>
    public static DecimalType ForType(DataType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            ByteType => new DecimalType(3, 0),
            ShortType => new DecimalType(5, 0),
            IntegerType => new DecimalType(10, 0),
            LongType => new DecimalType(20, 0),
            FloatType => new DecimalType(14, 7),
            DoubleType => new DecimalType(30, 15),
            DecimalType d => d,
            _ => throw new UnsupportedTypeException($"Type '{type.SimpleString}' has no decimal widening."),
        };
    }

    /// <summary>
    /// Clamps a precision/scale into the legal range like Spark's <c>adjustPrecisionScale</c>:
    /// when precision exceeds 38 it caps precision at 38 and reduces scale, never below
    /// <see cref="MinimumAdjustedScale"/> while integer digits remain.
    /// </summary>
    public static DecimalType Bounded(int precision, int scale)
    {
        if (scale > MaxScale)
        {
            scale = MaxScale;
        }

        if (precision <= DecimalType.MaxPrecision)
        {
            return new DecimalType(Math.Max(precision, DecimalType.MinPrecision), Math.Max(scale, 0));
        }

        int intDigits = precision - scale;
        int minScale = Math.Min(scale, MinimumAdjustedScale);
        int adjustedScale = intDigits + minScale > DecimalType.MaxPrecision
            ? minScale
            : DecimalType.MaxPrecision - intDigits;
        return new DecimalType(DecimalType.MaxPrecision, adjustedScale);
    }

    /// <summary>The result type of a decimal <paramref name="op"/> over two decimals (Spark <c>DecimalPrecision</c>).</summary>
    public static DecimalType ResultType(DecimalOp op, DecimalType left, DecimalType right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        int p1 = left.Precision, s1 = left.Scale, p2 = right.Precision, s2 = right.Scale;
        return op switch
        {
            DecimalOp.Add or DecimalOp.Subtract =>
                Bounded(Math.Max(p1 - s1, p2 - s2) + Math.Max(s1, s2) + 1, Math.Max(s1, s2)),
            DecimalOp.Multiply => Bounded(p1 + p2 + 1, s1 + s2),
            DecimalOp.Divide => Bounded(
                p1 - s1 + s2 + Math.Max(MinimumAdjustedScale, s1 + p2 + 1),
                Math.Max(MinimumAdjustedScale, s1 + p2 + 1)),
            DecimalOp.Remainder => Bounded(Math.Min(p1 - s1, p2 - s2) + Math.Max(s1, s2), Math.Max(s1, s2)),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
    }
}

/// <summary>The decimal binary operators whose result precision/scale follow Spark's rules.</summary>
public enum DecimalOp
{
    /// <summary>Addition.</summary>
    Add,

    /// <summary>Subtraction.</summary>
    Subtract,

    /// <summary>Multiplication.</summary>
    Multiply,

    /// <summary>Division.</summary>
    Divide,

    /// <summary>Modulo.</summary>
    Remainder,
}
