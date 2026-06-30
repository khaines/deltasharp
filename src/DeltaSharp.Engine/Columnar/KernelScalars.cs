using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// The Columnar-layer scalar value readers and Spark-parity comparators the aggregate and comparison kernels
/// (STORY-03.3.1, #149) bind to. These are the per-row primitives the <b>scalar reference</b> path uses (the
/// ADR-0001 correctness oracle the SIMD fast paths are validated against), so they go through the logical-row
/// <see cref="ColumnVector.GetValue{T}"/>/<see cref="ColumnVector.GetBytes"/> API and therefore read contiguous
/// vectors, slices, and zero-copy selected views identically.
/// </summary>
/// <remarks>
/// <para>
/// They intentionally live in <c>DeltaSharp.Engine.Columnar</c> rather than reusing
/// <c>Execution.Expressions.ScalarReader</c>: the kernels are a lower layer that operators and the interpreter
/// build <i>on</i>, so a Columnar→Execution reference would invert the dependency. The comparison semantics here
/// (Spark NaN/−0 ordering; decimal cross-scale) are deliberately identical to the interpreter's, so both tiers
/// agree row-for-row.
/// </para>
/// <para>
/// Spark <c>tinyint</c> is <b>signed</b> but its physical storage is a CLR <see cref="byte"/>, so byte reads
/// reinterpret through <see cref="sbyte"/>. A compact <see cref="DecimalType"/> stores its mantissa as
/// <see cref="long"/>, a wide one as <see cref="Int128"/>; both surface here as a scale-tagged
/// <see cref="DecimalValue"/>.
/// </para>
/// </remarks>
internal static class KernelScalars
{
    /// <summary>
    /// Reads an integral/temporal/boolean lane at logical <paramref name="index"/> as a sign-extended
    /// <see cref="long"/> (boolean as <c>0</c>/<c>1</c>, so comparison orders <c>false &lt; true</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The vector's type is not integral/temporal/boolean.</exception>
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
    /// <exception cref="InvalidOperationException">The vector's type is not numeric.</exception>
    public static double ReadDouble(ColumnVector vector, int index) => vector.Type switch
    {
        FloatType => vector.GetValue<float>(index),
        DoubleType => vector.GetValue<double>(index),
        DecimalType => DecimalToDouble(ReadDecimal(vector, index)),
        _ => ReadInt64(vector, index),
    };

    /// <summary>Reads a decimal (or integral, at scale 0) lane as an exact <see cref="DecimalValue"/>.</summary>
    /// <exception cref="InvalidOperationException">The vector's type cannot be read as a decimal.</exception>
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

    /// <summary>
    /// Spark's total order over <see cref="double"/>: <c>NaN</c> equals <c>NaN</c> and sorts greater than every
    /// other value, and <c>-0.0</c> equals <c>+0.0</c> (Spark <c>SQLOrderingUtil</c> /
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

    /// <summary>Spark's total order over <see cref="float"/> (NaN greatest, −0 == +0); returns -1, 0, or 1.</summary>
    public static int CompareSingle(float left, float right) => CompareDouble(left, right);

    /// <summary>
    /// Exact comparison of two <see cref="DecimalValue"/>s across differing scales (callers handle nulls).
    /// Rescales both to the wider scale in <see cref="Int128"/>; a value whose common-scale mantissa would
    /// exceed <see cref="Int128"/> falls back to a <see cref="double"/> comparison (unreachable for
    /// precision ≤ 38 with sane scales).
    /// </summary>
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
            return CompareDouble(DecimalToDouble(left), DecimalToDouble(right));
        }
    }

    /// <summary>
    /// Branchless signed-overflow check for <c>sum = a + b</c>: returns <see langword="true"/> when the true
    /// mathematical sum is outside <see cref="long"/>. The two operands share a sign that differs from the
    /// result's sign exactly on overflow, so <c>((a ^ sum) &amp; (b ^ sum)) &lt; 0</c> detects it without a
    /// <c>checked</c> trap (which the scalar reference must avoid on its per-element path).
    /// </summary>
    public static bool AddOverflows(long a, long b, long sum) => ((a ^ sum) & (b ^ sum)) < 0;

    /// <summary>Approximates a <see cref="DecimalValue"/> as a <see cref="double"/> (lossy; for floating compare fallback).</summary>
    private static double DecimalToDouble(DecimalValue value) =>
        (double)value.Unscaled / (double)DecimalValue.Pow10(value.Scale);

    private static Int128 Rescale(DecimalValue value, int targetScale) =>
        targetScale >= value.Scale
            ? checked(value.Unscaled * DecimalValue.Pow10(targetScale - value.Scale))
            : value.Unscaled / DecimalValue.Pow10(value.Scale - targetScale);
}
