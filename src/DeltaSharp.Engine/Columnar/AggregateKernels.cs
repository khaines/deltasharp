using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// Scalar and SIMD kernels for the v1 aggregates — <c>SUM</c>, <c>MIN</c>, <c>MAX</c>, <c>COUNT</c>, and
/// <c>AVG</c> — over a single <see cref="ColumnVector"/> (STORY-03.3.1, #149), so the hash-aggregate and
/// global-aggregate operators (#148) reuse one verified hot-loop primitive instead of materializing rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scalar reference is the oracle (ADR-0001).</b> Every aggregate has a per-row scalar reference computed
/// through the logical-row <see cref="ColumnVector.GetValue{T}"/>/<see cref="ColumnVector.IsNull"/> API, so it
/// is correct over contiguous vectors, slices, and zero-copy <see cref="SelectedColumnVector"/> selections
/// alike. The SIMD fast paths are an <i>additive</i> optimization that must produce a result identical to that
/// reference; they are taken only for a <b>contiguous, no-null</b> integer/temporal vector (the dominant
/// analytic shape) and fall back to the reference otherwise.
/// </para>
/// <para>
/// <b>Spark null semantics.</b> <c>SUM</c>/<c>MIN</c>/<c>MAX</c>/<c>AVG</c>/<c>COUNT(x)</c> skip nulls; an empty
/// or all-null input yields SQL <c>NULL</c> (modeled as a <see langword="null"/> return) — except
/// <c>COUNT</c>, which returns <c>0</c>. <c>COUNT(*)</c> counts logical rows regardless of validity.
/// </para>
/// <para>
/// <b>ANSI overflow (EPIC-02 type contract).</b> Integral <c>SUM</c> accumulates into <see cref="long"/> and
/// decimal <c>SUM</c> into an exact <see cref="DecimalValue"/>; a result outside range follows the
/// <see cref="AnsiMode"/> contract — <see cref="AnsiMode.Ansi"/> throws
/// <see cref="ArithmeticOverflowException"/>, <see cref="AnsiMode.Legacy"/> yields <c>NULL</c>. DeltaSharp never
/// silently wraps (see <c>AnsiMode</c>). Integer <c>MIN</c>/<c>MAX</c>/<c>COUNT</c> and floating <c>SUM</c>
/// cannot overflow.
/// </para>
/// <para>
/// <b>Zero allocation.</b> No method here allocates, boxes, uses LINQ, or dispatches virtually on its per-row
/// path; reductions return value types and the SIMD accumulators stay in registers. The
/// <c>Group*</c> bulk-update entry points write into caller-owned spans.
/// </para>
/// </remarks>
internal static class AggregateKernels
{
    // =====================================================================================================
    // COUNT
    // =====================================================================================================

    /// <summary>The <c>COUNT(*)</c> of <paramref name="vector"/>: its logical row count, independent of validity.</summary>
    public static long CountAll(ColumnVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        return vector.Length;
    }

    /// <summary>
    /// The <c>COUNT(x)</c> of <paramref name="vector"/>: the number of non-null logical rows. Uses the vector's
    /// cached <see cref="ColumnVector.NullCount"/> (an O(1) exact count); the SIMD count primitive over a raw
    /// validity bitmap is the reused #144 popcount — see the <see cref="Validity"/> overload.
    /// </summary>
    public static long CountNonNull(ColumnVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        return vector.Length - vector.NullCount;
    }

    /// <summary>
    /// The number of valid (non-null) rows described by <paramref name="validity"/>, counted with the
    /// hardware popcount of #144 (<see cref="BitmapOps.PopCount"/>) when the bitmap is present and byte-aligned,
    /// else the scalar per-bit count. The absent (all-valid) bitmap returns <see cref="Validity.Length"/>
    /// without touching memory.
    /// </summary>
    public static long CountNonNull(Validity validity)
    {
        if (!validity.HasBitmap)
        {
            return validity.Length;
        }

        return (validity.Offset & 7) == 0
            ? BitmapOps.PopCount(validity.Bits[(validity.Offset >> 3)..], validity.Length)
            : validity.CountValid();
    }

    // =====================================================================================================
    // SUM
    // =====================================================================================================

    /// <summary>
    /// <c>SUM</c> over an integral (<see cref="ByteType"/>/<see cref="ShortType"/>/<see cref="IntegerType"/>/
    /// <see cref="LongType"/>) vector, accumulated into <see cref="long"/> (Spark <c>sum(int)→bigint</c>).
    /// Returns <see langword="null"/> for an empty/all-null vector; integral overflow follows
    /// <paramref name="mode"/> (ANSI throws, Legacy yields <see langword="null"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="vector"/>'s type is not integral.</exception>
    /// <exception cref="ArithmeticOverflowException">The sum overflows <see cref="long"/> under <see cref="AnsiMode.Ansi"/>.</exception>
    public static long? SumInt64(ColumnVector vector, AnsiMode mode)
    {
        ArgumentNullException.ThrowIfNull(vector);
        RequireIntegral(vector.Type, "SUM");
        int n = vector.Length;
        if (n == 0)
        {
            return null;
        }

        // Fast path: a contiguous int32 column with no nulls sums exactly via SIMD widening (associative, so it
        // is identical across tiers and cannot overflow long for any realistic batch). Every other shape — long
        // (needs checked overflow), byte/short, nulls, or a selection — uses the scalar reference.
        if (vector.Type is IntegerType && !vector.HasNulls && vector is not SelectedColumnVector)
        {
            return SumInt32(vector.GetValues<int>());
        }

        long acc = 0;
        bool saw = false;
        bool hasNulls = vector.HasNulls;
        for (int i = 0; i < n; i++)
        {
            if (hasNulls && vector.IsNull(i))
            {
                continue;
            }

            long x = KernelScalars.ReadInt64(vector, i);
            long sum = unchecked(acc + x);
            if (KernelScalars.AddOverflows(acc, x, sum))
            {
                return OnIntegralOverflow(mode, vector.Type);
            }

            acc = sum;
            saw = true;
        }

        return saw ? acc : null;
    }

    /// <summary>
    /// <c>SUM</c> over a floating (<see cref="FloatType"/>/<see cref="DoubleType"/>) vector, accumulated into
    /// <see cref="double"/> (Spark <c>sum(float)→double</c>). Returns <see langword="null"/> for an empty/all-null
    /// vector. Accumulation is deterministic left-to-right (see the design doc on why floating <c>SUM</c> is
    /// scalar-only); ±∞/NaN propagate per IEEE, matching Spark.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="vector"/>'s type is not floating.</exception>
    public static double? SumDouble(ColumnVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Type is not (FloatType or DoubleType))
        {
            throw new InvalidOperationException(
                $"SUM(double) requires a float/double column but got '{vector.Type.SimpleString}'.");
        }

        int n = vector.Length;
        bool hasNulls = vector.HasNulls;
        double acc = 0;
        bool saw = false;
        for (int i = 0; i < n; i++)
        {
            if (hasNulls && vector.IsNull(i))
            {
                continue;
            }

            acc += KernelScalars.ReadDouble(vector, i);
            saw = true;
        }

        return saw ? acc : null;
    }

    /// <summary>
    /// <c>SUM</c> over a decimal (or integral, at scale 0) vector, accumulated exactly and fitted into
    /// <paramref name="resultType"/> (the operator's Spark result type, e.g. <c>decimal(min(38,p+10), s)</c>).
    /// Returns <see langword="null"/> for an empty/all-null vector; a result outside <paramref name="resultType"/>
    /// follows <paramref name="mode"/> (ANSI throws, Legacy yields <see langword="null"/>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="resultType"/> is null.</exception>
    /// <exception cref="ArithmeticOverflowException">The sum overflows <paramref name="resultType"/> under ANSI.</exception>
    public static DecimalValue? SumDecimal(ColumnVector vector, DecimalType resultType, AnsiMode mode)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(resultType);
        int n = vector.Length;
        bool hasNulls = vector.HasNulls;
        DecimalValue acc = default;
        bool saw = false;
        for (int i = 0; i < n; i++)
        {
            if (hasNulls && vector.IsNull(i))
            {
                continue;
            }

            DecimalValue x = KernelScalars.ReadDecimal(vector, i);
            if (!saw)
            {
                acc = x;
                saw = true;
                continue;
            }

            try
            {
                acc = DecimalValue.Add(acc, x);
            }
            catch (ArithmeticOverflowException) when (mode == AnsiMode.Legacy)
            {
                return null;
            }
        }

        return saw ? acc.ToType(resultType, mode) : null;
    }

    // =====================================================================================================
    // MIN / MAX
    // =====================================================================================================

    /// <summary>
    /// <c>MIN</c> over an integral/temporal vector read as a signed <see cref="long"/>. Returns
    /// <see langword="null"/> for an empty/all-null vector.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="vector"/>'s type is not integral/temporal.</exception>
    public static long? MinInt64(ColumnVector vector) => ExtremeInt64(vector, max: false);

    /// <summary><c>MAX</c> over an integral/temporal vector read as a signed <see cref="long"/>; null for empty/all-null.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="vector"/>'s type is not integral/temporal.</exception>
    public static long? MaxInt64(ColumnVector vector) => ExtremeInt64(vector, max: true);

    /// <summary>
    /// <c>MIN</c> over a floating vector under Spark's total order (<c>NaN</c> greatest, <c>-0 == +0</c>), so
    /// <c>NaN</c> is chosen only when every value is <c>NaN</c>. Null for empty/all-null.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="vector"/>'s type is not floating.</exception>
    public static double? MinDouble(ColumnVector vector) => ExtremeDouble(vector, max: false);

    /// <summary>
    /// <c>MAX</c> over a floating vector under Spark's total order; any <c>NaN</c> makes the result <c>NaN</c>.
    /// Null for empty/all-null.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="vector"/>'s type is not floating.</exception>
    public static double? MaxDouble(ColumnVector vector) => ExtremeDouble(vector, max: true);

    /// <summary><c>MIN</c> over a decimal vector (exact cross-scale comparison); null for empty/all-null.</summary>
    public static DecimalValue? MinDecimal(ColumnVector vector) => ExtremeDecimal(vector, max: false);

    /// <summary><c>MAX</c> over a decimal vector (exact cross-scale comparison); null for empty/all-null.</summary>
    public static DecimalValue? MaxDecimal(ColumnVector vector) => ExtremeDecimal(vector, max: true);

    // =====================================================================================================
    // AVG
    // =====================================================================================================

    /// <summary>
    /// <c>AVG</c> over a numeric vector as an IEEE <see cref="double"/> mean (Spark <c>avg(int)→double</c>):
    /// the sum of non-null values divided by their count, or <see langword="null"/> when there are none.
    /// Decimal input is averaged in double precision (lossy); for an exact decimal mean an operator finalizes
    /// <see cref="SumDecimal"/> over <see cref="CountNonNull(ColumnVector)"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="vector"/>'s type is not numeric.</exception>
    public static double? AverageDouble(ColumnVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        int n = vector.Length;
        bool hasNulls = vector.HasNulls;
        double sum = 0;
        long count = 0;
        for (int i = 0; i < n; i++)
        {
            if (hasNulls && vector.IsNull(i))
            {
                continue;
            }

            sum += KernelScalars.ReadDouble(vector, i);
            count++;
        }

        return count == 0 ? null : sum / count;
    }

    // =====================================================================================================
    // Group-aware bulk update (the per-batch contract #148's hash-aggregate consumes)
    // =====================================================================================================

    /// <summary>
    /// Per-group <c>SUM</c> bulk update for the hash-aggregate operator: for each logical row <c>i</c> of
    /// <paramref name="values"/> with group id <c>groupIds[i]</c>, adds the (integral, non-null) value into the
    /// caller-owned <paramref name="sums"/>/<paramref name="counts"/> accumulators and records Legacy overflow in
    /// <paramref name="overflowed"/>. The operator finalizes group <c>g</c> as SQL <c>NULL</c> when
    /// <c>counts[g] == 0</c> (no non-null rows) or <c>overflowed[g]</c> is set; otherwise <c>sums[g]</c>.
    /// </summary>
    /// <remarks>
    /// This is the documented consumption contract for #148: accumulator spans are zero-initialized and owned by
    /// the operator across batches, so a partial/final merge is repeated bulk updates. The update is a scatter by
    /// group id and is therefore scalar (no SIMD); it still avoids row materialization and per-row virtual
    /// dispatch. <c>MIN</c>/<c>MAX</c> extend by seeding the accumulator with the type's identity and a parallel
    /// <c>seen</c> mask; decimal sum extends with an <see cref="DecimalValue"/> accumulator array.
    /// </remarks>
    /// <param name="mode">ANSI throws <see cref="ArithmeticOverflowException"/> on overflow; Legacy sets <paramref name="overflowed"/>.</param>
    /// <exception cref="ArgumentException">The span lengths are inconsistent, or a group id is out of range.</exception>
    /// <exception cref="ArithmeticOverflowException">A group sum overflows <see cref="long"/> under ANSI.</exception>
    public static void GroupSumInt64(
        ColumnVector values,
        ReadOnlySpan<int> groupIds,
        Span<long> sums,
        Span<long> counts,
        Span<bool> overflowed,
        AnsiMode mode)
    {
        ArgumentNullException.ThrowIfNull(values);
        RequireIntegral(values.Type, "SUM");
        int n = values.Length;
        if (groupIds.Length != n)
        {
            throw new ArgumentException($"groupIds length {groupIds.Length} must equal the vector length {n}.", nameof(groupIds));
        }

        if (sums.Length != counts.Length || sums.Length != overflowed.Length)
        {
            throw new ArgumentException("sums, counts, and overflowed must have the same group count.");
        }

        int groupCount = sums.Length;
        bool hasNulls = values.HasNulls;
        for (int i = 0; i < n; i++)
        {
            int g = groupIds[i];
            if ((uint)g >= (uint)groupCount)
            {
                throw new ArgumentException($"Group id {g} at row {i} is out of range [0, {groupCount}).", nameof(groupIds));
            }

            if (hasNulls && values.IsNull(i))
            {
                continue;
            }

            counts[g]++;
            if (overflowed[g])
            {
                continue; // already null under Legacy; stop accumulating into a poisoned group
            }

            long x = KernelScalars.ReadInt64(values, i);
            long sum = unchecked(sums[g] + x);
            if (KernelScalars.AddOverflows(sums[g], x, sum))
            {
                if (mode == AnsiMode.Ansi)
                {
                    throw new ArithmeticOverflowException($"Group SUM over '{values.Type.SimpleString}' overflowed bigint.");
                }

                overflowed[g] = true;
                continue;
            }

            sums[g] = sum;
        }
    }

    /// <summary>
    /// Per-group <c>COUNT(x)</c> bulk update: increments <paramref name="counts"/><c>[groupIds[i]]</c> for every
    /// non-null logical row of <paramref name="values"/>. Companion to <see cref="GroupSumInt64"/> for a
    /// standalone <c>COUNT</c> aggregate over the same grouping.
    /// </summary>
    /// <exception cref="ArgumentException">The span lengths are inconsistent, or a group id is out of range.</exception>
    public static void GroupCountNonNull(ColumnVector values, ReadOnlySpan<int> groupIds, Span<long> counts)
    {
        ArgumentNullException.ThrowIfNull(values);
        int n = values.Length;
        if (groupIds.Length != n)
        {
            throw new ArgumentException($"groupIds length {groupIds.Length} must equal the vector length {n}.", nameof(groupIds));
        }

        int groupCount = counts.Length;
        bool hasNulls = values.HasNulls;
        for (int i = 0; i < n; i++)
        {
            int g = groupIds[i];
            if ((uint)g >= (uint)groupCount)
            {
                throw new ArgumentException($"Group id {g} at row {i} is out of range [0, {groupCount}).", nameof(groupIds));
            }

            if (!(hasNulls && values.IsNull(i)))
            {
                counts[g]++;
            }
        }
    }

    // =====================================================================================================
    // Bulk SIMD reductions over contiguous spans (the tier-forced parity surface)
    // =====================================================================================================

    /// <summary>
    /// Exact <c>SUM</c> of an <see cref="int"/> span widened into <see cref="long"/>. Integer addition is
    /// associative and commutative, so the lane-striped SIMD reduction is bit-identical to the scalar tail and
    /// to every other tier (no floating reassociation, no overflow for realistic batch sizes).
    /// </summary>
    public static long SumInt32(ReadOnlySpan<int> values, KernelTier tier = KernelTier.Auto)
    {
        int n = values.Length;
        ref int p = ref MemoryMarshal.GetReference(values);
        long total = 0;
        int i = 0;

        if (KernelTierGate.UseVector256(tier))
        {
            Vector256<long> acc = Vector256<long>.Zero;
            for (; i <= n - Vector256<int>.Count; i += Vector256<int>.Count)
            {
                (Vector256<long> lo, Vector256<long> hi) = Vector256.Widen(Vector256.LoadUnsafe(ref p, (nuint)i));
                acc += lo + hi;
            }

            total += Vector256.Sum(acc);
        }

        if (KernelTierGate.UseVector128(tier))
        {
            Vector128<long> acc = Vector128<long>.Zero;
            for (; i <= n - Vector128<int>.Count; i += Vector128<int>.Count)
            {
                (Vector128<long> lo, Vector128<long> hi) = Vector128.Widen(Vector128.LoadUnsafe(ref p, (nuint)i));
                acc += lo + hi;
            }

            total += Vector128.Sum(acc);
        }

        for (; i < n; i++)
        {
            total += values[i];
        }

        return total;
    }

    /// <summary>SIMD <c>MIN</c> of a non-empty <see cref="int"/> span (signed). Order-independent ⇒ identical across tiers.</summary>
    public static int MinInt32(ReadOnlySpan<int> values, KernelTier tier = KernelTier.Auto)
    {
        int n = values.Length;
        ref int p = ref MemoryMarshal.GetReference(values);
        int best = values[0];
        int i = 0;

        if (KernelTierGate.UseVector256(tier) && n >= Vector256<int>.Count)
        {
            Vector256<int> acc = Vector256.Create(best);
            for (; i <= n - Vector256<int>.Count; i += Vector256<int>.Count)
            {
                acc = Vector256.Min(acc, Vector256.LoadUnsafe(ref p, (nuint)i));
            }

            best = HorizontalMin(acc);
        }

        if (KernelTierGate.UseVector128(tier) && n - i >= Vector128<int>.Count)
        {
            Vector128<int> acc = Vector128.Create(best);
            for (; i <= n - Vector128<int>.Count; i += Vector128<int>.Count)
            {
                acc = Vector128.Min(acc, Vector128.LoadUnsafe(ref p, (nuint)i));
            }

            best = HorizontalMin(acc);
        }

        for (; i < n; i++)
        {
            best = Math.Min(best, values[i]);
        }

        return best;
    }

    /// <summary>SIMD <c>MAX</c> of a non-empty <see cref="int"/> span (signed).</summary>
    public static int MaxInt32(ReadOnlySpan<int> values, KernelTier tier = KernelTier.Auto)
    {
        int n = values.Length;
        ref int p = ref MemoryMarshal.GetReference(values);
        int best = values[0];
        int i = 0;

        if (KernelTierGate.UseVector256(tier) && n >= Vector256<int>.Count)
        {
            Vector256<int> acc = Vector256.Create(best);
            for (; i <= n - Vector256<int>.Count; i += Vector256<int>.Count)
            {
                acc = Vector256.Max(acc, Vector256.LoadUnsafe(ref p, (nuint)i));
            }

            best = HorizontalMax(acc);
        }

        if (KernelTierGate.UseVector128(tier) && n - i >= Vector128<int>.Count)
        {
            Vector128<int> acc = Vector128.Create(best);
            for (; i <= n - Vector128<int>.Count; i += Vector128<int>.Count)
            {
                acc = Vector128.Max(acc, Vector128.LoadUnsafe(ref p, (nuint)i));
            }

            best = HorizontalMax(acc);
        }

        for (; i < n; i++)
        {
            best = Math.Max(best, values[i]);
        }

        return best;
    }

    /// <summary>SIMD <c>MIN</c> of a non-empty <see cref="long"/> span (signed).</summary>
    public static long MinInt64(ReadOnlySpan<long> values, KernelTier tier = KernelTier.Auto)
    {
        int n = values.Length;
        ref long p = ref MemoryMarshal.GetReference(values);
        long best = values[0];
        int i = 0;

        if (KernelTierGate.UseVector256(tier) && n >= Vector256<long>.Count)
        {
            Vector256<long> acc = Vector256.Create(best);
            for (; i <= n - Vector256<long>.Count; i += Vector256<long>.Count)
            {
                acc = Vector256.Min(acc, Vector256.LoadUnsafe(ref p, (nuint)i));
            }

            best = HorizontalMin(acc);
        }

        if (KernelTierGate.UseVector128(tier) && n - i >= Vector128<long>.Count)
        {
            Vector128<long> acc = Vector128.Create(best);
            for (; i <= n - Vector128<long>.Count; i += Vector128<long>.Count)
            {
                acc = Vector128.Min(acc, Vector128.LoadUnsafe(ref p, (nuint)i));
            }

            best = HorizontalMin(acc);
        }

        for (; i < n; i++)
        {
            best = Math.Min(best, values[i]);
        }

        return best;
    }

    /// <summary>SIMD <c>MAX</c> of a non-empty <see cref="long"/> span (signed).</summary>
    public static long MaxInt64(ReadOnlySpan<long> values, KernelTier tier = KernelTier.Auto)
    {
        int n = values.Length;
        ref long p = ref MemoryMarshal.GetReference(values);
        long best = values[0];
        int i = 0;

        if (KernelTierGate.UseVector256(tier) && n >= Vector256<long>.Count)
        {
            Vector256<long> acc = Vector256.Create(best);
            for (; i <= n - Vector256<long>.Count; i += Vector256<long>.Count)
            {
                acc = Vector256.Max(acc, Vector256.LoadUnsafe(ref p, (nuint)i));
            }

            best = HorizontalMax(acc);
        }

        if (KernelTierGate.UseVector128(tier) && n - i >= Vector128<long>.Count)
        {
            Vector128<long> acc = Vector128.Create(best);
            for (; i <= n - Vector128<long>.Count; i += Vector128<long>.Count)
            {
                acc = Vector128.Max(acc, Vector128.LoadUnsafe(ref p, (nuint)i));
            }

            best = HorizontalMax(acc);
        }

        for (; i < n; i++)
        {
            best = Math.Max(best, values[i]);
        }

        return best;
    }

    // =====================================================================================================
    // Internals
    // =====================================================================================================

    private static long? ExtremeInt64(ColumnVector vector, bool max)
    {
        ArgumentNullException.ThrowIfNull(vector);
        RequireIntegralOrTemporal(vector.Type, max ? "MAX" : "MIN");
        int n = vector.Length;
        if (n == 0)
        {
            return null;
        }

        // Fast path: contiguous, no-null int32 (int/date) or int64 (long/timestamp) reduce via SIMD min/max.
        if (!vector.HasNulls && vector is not SelectedColumnVector)
        {
            switch (vector.Type)
            {
                case IntegerType or DateType:
                    ReadOnlySpan<int> i32 = vector.GetValues<int>();
                    return max ? MaxInt32(i32) : MinInt32(i32);
                case LongType or TimestampType or TimestampNtzType:
                    ReadOnlySpan<long> i64 = vector.GetValues<long>();
                    return max ? MaxInt64(i64) : MinInt64(i64);
            }
        }

        long best = 0;
        bool saw = false;
        bool hasNulls = vector.HasNulls;
        for (int i = 0; i < n; i++)
        {
            if (hasNulls && vector.IsNull(i))
            {
                continue;
            }

            long x = KernelScalars.ReadInt64(vector, i);
            best = saw ? (max ? Math.Max(best, x) : Math.Min(best, x)) : x;
            saw = true;
        }

        return saw ? best : null;
    }

    private static double? ExtremeDouble(ColumnVector vector, bool max)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Type is not (FloatType or DoubleType))
        {
            throw new InvalidOperationException(
                $"{(max ? "MAX" : "MIN")}(double) requires a float/double column but got '{vector.Type.SimpleString}'.");
        }

        int n = vector.Length;
        bool hasNulls = vector.HasNulls;
        double best = 0;
        bool saw = false;
        for (int i = 0; i < n; i++)
        {
            if (hasNulls && vector.IsNull(i))
            {
                continue;
            }

            double x = KernelScalars.ReadDouble(vector, i);
            if (!saw)
            {
                best = x;
                saw = true;
            }
            else if (max ? KernelScalars.CompareDouble(x, best) > 0 : KernelScalars.CompareDouble(x, best) < 0)
            {
                best = x;
            }
        }

        return saw ? best : null;
    }

    private static DecimalValue? ExtremeDecimal(ColumnVector vector, bool max)
    {
        ArgumentNullException.ThrowIfNull(vector);
        int n = vector.Length;
        bool hasNulls = vector.HasNulls;
        DecimalValue best = default;
        bool saw = false;
        for (int i = 0; i < n; i++)
        {
            if (hasNulls && vector.IsNull(i))
            {
                continue;
            }

            DecimalValue x = KernelScalars.ReadDecimal(vector, i);
            if (!saw)
            {
                best = x;
                saw = true;
            }
            else if (max ? KernelScalars.CompareDecimal(x, best) > 0 : KernelScalars.CompareDecimal(x, best) < 0)
            {
                best = x;
            }
        }

        return saw ? best : null;
    }

    private static long? OnIntegralOverflow(AnsiMode mode, DataType type) =>
        mode == AnsiMode.Ansi
            ? throw new ArithmeticOverflowException($"SUM over '{type.SimpleString}' overflowed bigint.")
            : null;

    private static int HorizontalMin(Vector256<int> v)
    {
        int m = v.GetElement(0);
        for (int k = 1; k < Vector256<int>.Count; k++)
        {
            m = Math.Min(m, v.GetElement(k));
        }

        return m;
    }

    private static int HorizontalMin(Vector128<int> v)
    {
        int m = v.GetElement(0);
        for (int k = 1; k < Vector128<int>.Count; k++)
        {
            m = Math.Min(m, v.GetElement(k));
        }

        return m;
    }

    private static int HorizontalMax(Vector256<int> v)
    {
        int m = v.GetElement(0);
        for (int k = 1; k < Vector256<int>.Count; k++)
        {
            m = Math.Max(m, v.GetElement(k));
        }

        return m;
    }

    private static int HorizontalMax(Vector128<int> v)
    {
        int m = v.GetElement(0);
        for (int k = 1; k < Vector128<int>.Count; k++)
        {
            m = Math.Max(m, v.GetElement(k));
        }

        return m;
    }

    private static long HorizontalMin(Vector256<long> v)
    {
        long m = v.GetElement(0);
        for (int k = 1; k < Vector256<long>.Count; k++)
        {
            m = Math.Min(m, v.GetElement(k));
        }

        return m;
    }

    private static long HorizontalMin(Vector128<long> v)
    {
        long m = v.GetElement(0);
        for (int k = 1; k < Vector128<long>.Count; k++)
        {
            m = Math.Min(m, v.GetElement(k));
        }

        return m;
    }

    private static long HorizontalMax(Vector256<long> v)
    {
        long m = v.GetElement(0);
        for (int k = 1; k < Vector256<long>.Count; k++)
        {
            m = Math.Max(m, v.GetElement(k));
        }

        return m;
    }

    private static long HorizontalMax(Vector128<long> v)
    {
        long m = v.GetElement(0);
        for (int k = 1; k < Vector128<long>.Count; k++)
        {
            m = Math.Max(m, v.GetElement(k));
        }

        return m;
    }

    private static void RequireIntegral(DataType type, string aggregate)
    {
        if (type is not (ByteType or ShortType or IntegerType or LongType))
        {
            throw new InvalidOperationException(
                $"{aggregate}(bigint) requires an integral column but got '{type.SimpleString}'.");
        }
    }

    private static void RequireIntegralOrTemporal(DataType type, string aggregate)
    {
        if (type is not (ByteType or ShortType or IntegerType or LongType or DateType or TimestampType or TimestampNtzType))
        {
            throw new InvalidOperationException(
                $"{aggregate}(bigint) requires an integral/temporal column but got '{type.SimpleString}'.");
        }
    }
}
