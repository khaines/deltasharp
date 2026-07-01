using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Shared builders and independent oracles for the STORY-03.3.1 (#149) aggregate/comparison kernel parity
/// tests. The oracles here are written in plain C# from the source arrays — deliberately <b>not</b> reusing
/// <see cref="KernelScalars"/> — so a kernel that drifts (SIMD <i>or</i> scalar reference) fails a parity
/// assertion rather than agreeing with a co-mutated helper.
/// </summary>
internal static class KernelTestSupport
{
    /// <summary>Length sweep: powers of two, ±1 neighbours, sub-byte tails (257), and vector-width tails (1000).</summary>
    public static readonly int[] Lengths = { 0, 1, 7, 8, 9, 63, 64, 65, 127, 128, 255, 256, 257, 1000, 1024, 4096 };

    /// <summary>The explicitly-forced SIMD tiers (every tier except <see cref="KernelTier.Auto"/>).</summary>
    public static readonly KernelTier[] ForcedTiers = { KernelTier.Scalar, KernelTier.Vector128, KernelTier.Vector256 };

    /// <summary>Null densities spanning no-null, sparse, half, dense, and all-null.</summary>
    public static readonly double[] NullDensities = { 0.0, 0.1, 0.5, 0.9, 1.0 };

    /// <summary>All six comparison predicates.</summary>
    public static readonly ComparisonOp[] AllOps =
    {
        ComparisonOp.Equal, ComparisonOp.NotEqual, ComparisonOp.LessThan,
        ComparisonOp.LessThanOrEqual, ComparisonOp.GreaterThan, ComparisonOp.GreaterThanOrEqual,
    };

    // --- typed column builders (no-null when validity omitted) -------------------------------------------

    public static ColumnVector Long(long[] values, bool[]? valid = null) => Build(LongType.Instance, values.Length, valid, (v, i) => v.AppendValue(values[i]));

    public static ColumnVector Int(int[] values, bool[]? valid = null) => Build(IntegerType.Instance, values.Length, valid, (v, i) => v.AppendValue(values[i]));

    public static ColumnVector Short(short[] values, bool[]? valid = null) => Build(ShortType.Instance, values.Length, valid, (v, i) => v.AppendValue(values[i]));

    public static ColumnVector Double(double[] values, bool[]? valid = null) => Build(DoubleType.Instance, values.Length, valid, (v, i) => v.AppendValue(values[i]));

    public static ColumnVector Float(float[] values, bool[]? valid = null) => Build(FloatType.Instance, values.Length, valid, (v, i) => v.AppendValue(values[i]));

    public static ColumnVector Boolean(bool[] values, bool[]? valid = null) => Build(BooleanType.Instance, values.Length, valid, (v, i) => v.AppendValue(values[i]));

    public static ColumnVector Date(int[] days, bool[]? valid = null) => Build(DateType.Instance, days.Length, valid, (v, i) => v.AppendValue(days[i]));

    public static ColumnVector Timestamp(long[] micros, bool[]? valid = null) => Build(TimestampType.Instance, micros.Length, valid, (v, i) => v.AppendValue(micros[i]));

    /// <summary>A compact (precision ≤ 18) decimal column whose mantissas are <paramref name="unscaled"/> at <paramref name="scale"/>.</summary>
    public static ColumnVector Decimal(long[] unscaled, int scale, bool[]? valid = null) =>
        Build(new DecimalType(18, scale), unscaled.Length, valid, (v, i) => v.AppendValue(unscaled[i]));

    /// <summary>A zero-copy selected (non-contiguous) view of <paramref name="parent"/> over <paramref name="indices"/>.</summary>
    public static ColumnVector Selected(ColumnVector parent, int[] indices) => parent.Select(new SelectionVector(indices));

    private static ColumnVector Build(DataType type, int length, bool[]? valid, Action<MutableColumnVector, int> append)
    {
        MutableColumnVector vector = ColumnVectors.Create(type, length);
        for (int i = 0; i < length; i++)
        {
            if (valid is null || valid[i])
            {
                append(vector, i);
            }
            else
            {
                vector.AppendNull();
            }
        }

        return vector;
    }

    // --- random generators (System.Random is allowed in tests; production code uses seeded xorshift) ------

    public static long[] RandomLongs(Random rng, int length, long lo, long hi)
    {
        var values = new long[length];
        for (int i = 0; i < length; i++)
        {
            values[i] = rng.NextInt64(lo, hi);
        }

        return values;
    }

    public static int[] RandomInts(Random rng, int length, int lo, int hi)
    {
        var values = new int[length];
        for (int i = 0; i < length; i++)
        {
            values[i] = rng.Next(lo, hi);
        }

        return values;
    }

    public static bool[] RandomValidity(Random rng, int length, double nullDensity)
    {
        var valid = new bool[length];
        for (int i = 0; i < length; i++)
        {
            valid[i] = rng.NextDouble() >= nullDensity;
        }

        return valid;
    }

    // --- oracles ----------------------------------------------------------------------------------------

    /// <summary>Spark's total order over doubles (NaN greatest, −0 == +0), written independently of the kernel.</summary>
    public static int OracleCompareDouble(double a, double b)
    {
        if (a < b)
        {
            return -1;
        }

        if (a > b)
        {
            return 1;
        }

        if (a == b)
        {
            return 0;
        }

        // At least one NaN.
        bool aNaN = double.IsNaN(a);
        bool bNaN = double.IsNaN(b);
        if (aNaN && bNaN)
        {
            return 0;
        }

        return aNaN ? 1 : -1;
    }

    public static bool ApplyOp(ComparisonOp op, int sign) => op switch
    {
        ComparisonOp.Equal => sign == 0,
        ComparisonOp.NotEqual => sign != 0,
        ComparisonOp.LessThan => sign < 0,
        ComparisonOp.LessThanOrEqual => sign <= 0,
        ComparisonOp.GreaterThan => sign > 0,
        ComparisonOp.GreaterThanOrEqual => sign >= 0,
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    /// <summary>
    /// Runs every comparison op through <see cref="ComparisonKernels.Compare(ComparisonOp, ColumnVector, ColumnVector, Span{byte}, Span{byte})"/>
    /// and asserts byte-identical value + validity bitmaps, an equal null count, and canonical padding against an
    /// oracle built from <paramref name="oracleSign"/> (the test's own per-row sign) and the validity masks.
    /// </summary>
    public static void AssertCompareParity(ColumnVector left, ColumnVector right, bool[] validL, bool[] validR, Func<int, int> oracleSign)
    {
        int n = left.Length;
        int byteCount = Math.Max(1, Bitmap.ByteCount(n));
        foreach (ComparisonOp op in AllOps)
        {
            var values = new byte[byteCount];
            var validity = new byte[byteCount];
            int nulls = ComparisonKernels.Compare(op, left, right, values, validity);

            var expectedValues = new byte[byteCount];
            var expectedValidity = new byte[byteCount];
            int expectedNulls = 0;
            for (int i = 0; i < n; i++)
            {
                if (!validL[i] || !validR[i])
                {
                    expectedNulls++;
                    continue;
                }

                Bitmap.Set(expectedValidity, i, true);
                if (ApplyOp(op, oracleSign(i)))
                {
                    Bitmap.Set(expectedValues, i, true);
                }
            }

            Assert.Equal(expectedNulls, nulls);
            AssertBitmapEqual(expectedValidity, validity, n, $"{op} validity");
            AssertBitmapEqual(expectedValues, values, n, $"{op} values");
            AssertCanonicalPadding(values, n, $"{op} values padding");
            AssertCanonicalPadding(validity, n, $"{op} validity padding");
        }
    }

    /// <summary>Asserts the two bitmaps agree on every bit in <c>[0, length)</c>.</summary>
    public static void AssertBitmapEqual(byte[] expected, byte[] actual, int length, string because)
    {
        for (int i = 0; i < length; i++)
        {
            Assert.True(Bitmap.Get(expected, i) == Bitmap.Get(actual, i), $"bit {i} differs ({because})");
        }
    }

    /// <summary>Asserts every padding bit (index ≥ <paramref name="length"/>) of the final byte is <c>0</c>.</summary>
    public static void AssertCanonicalPadding(byte[] bitmap, int length, string because)
    {
        int byteCount = Bitmap.ByteCount(length);
        for (int i = length; i < byteCount * 8; i++)
        {
            Assert.False(Bitmap.Get(bitmap, i), $"padding bit {i} is set ({because})");
        }
    }
}
