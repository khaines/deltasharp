using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Per-row, no-boxing scalar reads from a <see cref="ColumnVector"/> for the interpreted evaluator
/// (STORY-03.4.1), plus the Spark-parity floating comparison. Every accessor goes through the
/// logical row API (<see cref="ColumnVector.GetValue{T}"/>/<see cref="ColumnVector.GetBytes"/>), so it
/// reads contiguous vectors and zero-copy selected views identically — kernels never touch the
/// contiguous <see cref="ColumnVector.GetValues{T}"/> span (unavailable on a selection).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ByteType"/> storage is a CLR <see cref="byte"/> but Spark <c>tinyint</c> is
/// <b>signed</b>, so byte reads reinterpret through <see cref="sbyte"/>. A compact
/// <see cref="DecimalType"/> stores its mantissa as <see cref="long"/>, a wide one as
/// <see cref="Int128"/>; both surface here as a scale-tagged <see cref="DecimalValue"/>.
/// </para>
/// </remarks>
internal static class ScalarReader
{
    /// <summary>Reads an integral/boolean/temporal lane as a sign-extended <see cref="long"/>.</summary>
    public static long ReadInt64(ColumnVector vector, int index) => vector.Type switch
    {
        BooleanType => vector.GetValue<bool>(index) ? 1L : 0L,
        ByteType => (sbyte)vector.GetValue<byte>(index),
        ShortType => vector.GetValue<short>(index),
        IntegerType or DateType => vector.GetValue<int>(index),
        LongType or TimestampType => vector.GetValue<long>(index),
        _ => throw new InvalidOperationException(
            $"Column of type '{vector.Type.SimpleString}' cannot be read as a 64-bit integer."),
    };

    /// <summary>Reads any numeric lane widened to IEEE <see cref="double"/> (decimals lose precision, matching Spark).</summary>
    public static double ReadDouble(ColumnVector vector, int index) => vector.Type switch
    {
        FloatType => vector.GetValue<float>(index),
        DoubleType => vector.GetValue<double>(index),
        DecimalType => ToDouble(ReadDecimal(vector, index)),
        _ => ReadInt64(vector, index),
    };

    /// <summary>Reads any numeric lane narrowed to IEEE <see cref="float"/>.</summary>
    public static float ReadSingle(ColumnVector vector, int index) => vector.Type switch
    {
        FloatType => vector.GetValue<float>(index),
        DoubleType => (float)vector.GetValue<double>(index),
        DecimalType => (float)ToDouble(ReadDecimal(vector, index)),
        _ => ReadInt64(vector, index),
    };

    /// <summary>Reads a decimal (or integral, at scale 0) lane as an exact <see cref="DecimalValue"/>.</summary>
    public static DecimalValue ReadDecimal(ColumnVector vector, int index) => vector.Type switch
    {
        DecimalType { IsCompact: true } d => new DecimalValue(vector.GetValue<long>(index), d.Scale),
        DecimalType d => new DecimalValue(vector.GetValue<Int128>(index), d.Scale),
        ByteType => new DecimalValue((sbyte)vector.GetValue<byte>(index), 0),
        ShortType => new DecimalValue(vector.GetValue<short>(index), 0),
        IntegerType => new DecimalValue(vector.GetValue<int>(index), 0),
        LongType => new DecimalValue(vector.GetValue<long>(index), 0),
        _ => throw new InvalidOperationException(
            $"Column of type '{vector.Type.SimpleString}' cannot be read as a decimal."),
    };

    /// <summary>Reads a boolean lane.</summary>
    public static bool ReadBool(ColumnVector vector, int index) => vector.GetValue<bool>(index);

    /// <summary>Reads a variable-width (string/binary) lane as its raw UTF-8/binary bytes.</summary>
    public static ReadOnlySpan<byte> ReadBytes(ColumnVector vector, int index) => vector.GetBytes(index);

    /// <summary>
    /// Spark's total order over <see cref="double"/>: <c>NaN</c> equals <c>NaN</c> and sorts greater
    /// than every other value, and <c>-0.0</c> equals <c>+0.0</c> (Spark <c>SQLOrderingUtil</c> /
    /// <c>NormalizeFloatingNumbers</c>). Returns -1, 0, or 1.
    /// </summary>
    public static int CompareDouble(double left, double right)
    {
        if (left < right)
        {
            return -1;
        }

        if (left > right)
        {
            return 1;
        }

        if (left == right)
        {
            // Covers the -0.0 == +0.0 case (IEEE equality), which Spark treats as equal.
            return 0;
        }

        // At least one operand is NaN (the only way to reach here): NaN ties with NaN and is greatest.
        if (double.IsNaN(left))
        {
            return double.IsNaN(right) ? 0 : 1;
        }

        return -1;
    }

    /// <summary>Exact three-valued-free comparison of two <see cref="DecimalValue"/>s (callers handle nulls).</summary>
    public static int CompareDecimal(DecimalValue left, DecimalValue right)
    {
        int scale = Math.Max(left.Scale, right.Scale);
        try
        {
            Int128 l = Rescale(left, scale);
            Int128 r = Rescale(right, scale);
            return l.CompareTo(r);
        }
        catch (OverflowException)
        {
            // Defensive: values whose common-scale mantissa exceeds Int128 fall back to a double
            // comparison. Decimals bounded to precision <= 38 with sane scales never reach here.
            return CompareDouble(ToDouble(left), ToDouble(right));
        }
    }

    /// <summary>Approximates a <see cref="DecimalValue"/> as a <see cref="double"/> (lossy; for floating arithmetic/compare).</summary>
    public static double ToDouble(DecimalValue value) =>
        (double)value.Unscaled / (double)DecimalValue.Pow10(value.Scale);

    private static Int128 Rescale(DecimalValue value, int targetScale) =>
        targetScale >= value.Scale
            ? checked(value.Unscaled * DecimalValue.Pow10(targetScale - value.Scale))
            : value.Unscaled / DecimalValue.Pow10(value.Scale - targetScale);
}
