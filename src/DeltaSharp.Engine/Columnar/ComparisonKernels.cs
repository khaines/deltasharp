using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>The six SQL comparison predicates the kernels evaluate (Columnar-local, so the layer stays self-contained).</summary>
internal enum ComparisonOp
{
    /// <summary><c>=</c></summary>
    Equal,

    /// <summary><c>&lt;&gt;</c></summary>
    NotEqual,

    /// <summary><c>&lt;</c></summary>
    LessThan,

    /// <summary><c>&lt;=</c></summary>
    LessThanOrEqual,

    /// <summary><c>&gt;</c></summary>
    GreaterThan,

    /// <summary><c>&gt;=</c></summary>
    GreaterThanOrEqual,
}

/// <summary>
/// Scalar and SIMD comparison kernels (STORY-03.3.1, #149): for two <see cref="ColumnVector"/>s (or a vector and
/// a scalar literal) they write a packed boolean <b>result bitmap</b> plus a packed <b>validity bitmap</b> and
/// return the null count, so the filter/predicate operators (#148) reuse one verified hot-loop primitive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Output shape.</b> Results are two Arrow-compatible LSB-first bitmaps: <c>resultValues</c> (bit set ⇒ the
/// predicate is <see langword="true"/>) and <c>resultValidity</c> (bit set ⇒ the row is non-null). Both are
/// canonical — bits at index <c>≥ length</c> in the final byte are <c>0</c> — and the invariant
/// <c>value ⊆ valid</c> holds (a null row has both bits clear). This is exactly the bit layout #144 produces, so
/// a result feeds straight back into <see cref="BitmapOps"/>/<see cref="NullMasks"/>.
/// </para>
/// <para>
/// <b>Propagate-on-any-null (#143).</b> A comparison whose left or right operand is SQL <c>NULL</c> is <c>NULL</c>:
/// <c>out_valid = left_valid &amp; right_valid</c>. That AND-combine is delegated to the reused #144 seam —
/// <see cref="NullMasks.PropagateBinary(Validity, Validity, Span{byte}, NullMaskTier)"/> whenever both operands
/// expose a packed validity bitmap (always for the no-null fast path), else a per-row reference fallback. The
/// value bits are then computed only for valid rows, so null rows stay <c>0</c>.
/// </para>
/// <para>
/// <b>Scalar reference is the oracle (ADR-0001).</b> Per kind, the sign is
/// <c>Int64</c>: <see cref="KernelScalars.ReadInt64"/> (integral/temporal/boolean, boolean as 0/1);
/// <c>Double</c>: <see cref="KernelScalars.CompareDouble"/> (Spark total order — <c>NaN</c> greatest, <c>-0 == +0</c>);
/// <c>Decimal</c>: <see cref="KernelScalars.CompareDecimal"/> (exact cross-scale);
/// <c>TemporalPromote</c>: a date and a timestamp compare at the date's UTC-midnight instant
/// (<c>days × TemporalValues.MicrosPerDay</c>). These are bit-for-bit the interpreter's semantics
/// (<c>Execution.Expressions.ComparisonEvaluator</c>), so both tiers agree row-for-row.
/// </para>
/// <para>
/// <b>SIMD fast path.</b> Taken only for two <b>contiguous, no-null, same-typed</b> int32 (<c>int</c>/<c>date</c>)
/// or int64 (<c>long</c>/<c>timestamp</c>) vectors: a width-portable <see cref="Vector256"/>/<see cref="Vector128"/>
/// compare whose mask lanes are packed to result bits with <c>ExtractMostSignificantBits</c>. Every other shape —
/// nulls, a selection, floating/decimal, or a date↔timestamp mix — uses the scalar reference. String/binary
/// comparison stays on the interpreter path (AC2 scope is primitive/decimal/date/timestamp); turning a result
/// bitmap into a compacted selection vector (left-pack) is deferred to STORY-03.3.2 (#150/#153).
/// </para>
/// <para><b>Zero allocation.</b> Every entry point writes into caller-owned spans; nothing here allocates, boxes, or uses LINQ.</para>
/// </remarks>
internal static class ComparisonKernels
{
    private enum CmpKind
    {
        Int64,
        Double,
        Decimal,
        TemporalPromote,
    }

    // =========================================================================================================
    // High-level: vector vs vector
    // =========================================================================================================

    /// <summary>
    /// Compares <paramref name="left"/> <paramref name="op"/> <paramref name="right"/> element-wise into the
    /// <paramref name="resultValues"/> predicate bitmap and <paramref name="resultValidity"/> bitmap, returning the
    /// number of SQL <c>NULL</c> result rows (propagate-on-any-null). Takes the SIMD path for a contiguous, no-null,
    /// same-typed int32/int64 pair and the scalar reference otherwise.
    /// </summary>
    /// <exception cref="ArgumentException">The operand lengths differ, or a result span is smaller than <c>ByteCount(Length)</c>.</exception>
    /// <exception cref="NotSupportedException">An operand type is outside the AC2 scope (e.g. string/binary).</exception>
    public static int Compare(
        ComparisonOp op,
        ColumnVector left,
        ColumnVector right,
        Span<byte> resultValues,
        Span<byte> resultValidity)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        int n = left.Length;
        if (right.Length != n)
        {
            throw new ArgumentException($"left length {n} must equal right length {right.Length}.", nameof(right));
        }

        int byteCount = Bitmap.ByteCount(n);
        RequireResultSpans(resultValues.Length, resultValidity.Length, byteCount);
        CmpKind kind = Classify(left.Type, right.Type);

        // Validity first, via the reused #144 AND-combine when both operands expose a packed bitmap.
        int nullCount = CombineValidity(left, right, resultValidity, n);

        // SIMD value bits for the dominant analytic shape: contiguous, no-null, same fixed width.
        if (nullCount == 0
            && kind == CmpKind.Int64
            && left is not SelectedColumnVector
            && right is not SelectedColumnVector)
        {
            if (left.Type is IntegerType && right.Type is IntegerType)
            {
                CompareInt32(op, left.GetValues<int>(), right.GetValues<int>(), resultValues);
                return 0;
            }

            if (left.Type is DateType && right.Type is DateType)
            {
                CompareInt32(op, left.GetValues<int>(), right.GetValues<int>(), resultValues);
                return 0;
            }

            if (left.Type is LongType && right.Type is LongType)
            {
                CompareInt64(op, left.GetValues<long>(), right.GetValues<long>(), resultValues);
                return 0;
            }

            if (left.Type is TimestampType && right.Type is TimestampType)
            {
                CompareInt64(op, left.GetValues<long>(), right.GetValues<long>(), resultValues);
                return 0;
            }
        }

        // Scalar reference: value bit only where the row is valid, so null rows stay 0 (value ⊆ valid).
        resultValues[..byteCount].Clear();
        for (int i = 0; i < n; i++)
        {
            if (!Bitmap.Get(resultValidity, i))
            {
                continue;
            }

            if (ApplyOp(op, CompareSign(kind, left, right, i)))
            {
                Bitmap.Set(resultValues, i, true);
            }
        }

        return nullCount;
    }

    // =========================================================================================================
    // High-level: vector vs scalar literal (predicate pushdown). The literal is never null ⇒ unary validity.
    // =========================================================================================================

    /// <summary>
    /// Compares an integral/temporal vector against a <see cref="long"/> literal (read in the column's units —
    /// days for <c>date</c>, micros for <c>timestamp</c>). SIMD when the column is contiguous and no-null
    /// (int32 when the literal fits <see cref="int"/>, else int64); scalar reference otherwise. Result validity
    /// equals the column's validity (the literal cannot be null).
    /// </summary>
    /// <exception cref="ArgumentException">A result span is smaller than <c>ByteCount(Length)</c>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="left"/> is not an integral/temporal/boolean column.</exception>
    public static int Compare(ComparisonOp op, ColumnVector left, long scalar, Span<byte> resultValues, Span<byte> resultValidity)
    {
        ArgumentNullException.ThrowIfNull(left);
        if (left.Type is not (BooleanType or ByteType or ShortType or IntegerType or LongType or DateType or TimestampType))
        {
            throw new NotSupportedException(
                $"Scalar bigint comparison requires an integral/temporal column but got '{left.Type.SimpleString}'.");
        }

        int n = left.Length;
        int byteCount = Bitmap.ByteCount(n);
        RequireResultSpans(resultValues.Length, resultValidity.Length, byteCount);
        int nullCount = UnaryValidity(left, resultValidity);

        if (nullCount == 0 && left is not SelectedColumnVector)
        {
            if (left.Type is IntegerType or DateType && scalar is >= int.MinValue and <= int.MaxValue)
            {
                CompareInt32(op, left.GetValues<int>(), (int)scalar, resultValues);
                return 0;
            }

            if (left.Type is LongType or TimestampType)
            {
                CompareInt64(op, left.GetValues<long>(), scalar, resultValues);
                return 0;
            }
        }

        resultValues[..byteCount].Clear();
        for (int i = 0; i < n; i++)
        {
            if (!Bitmap.Get(resultValidity, i))
            {
                continue;
            }

            if (ApplyOp(op, KernelScalars.ReadInt64(left, i).CompareTo(scalar)))
            {
                Bitmap.Set(resultValues, i, true);
            }
        }

        return nullCount;
    }

    /// <summary>
    /// Compares a numeric vector against a <see cref="double"/> literal under Spark's float total order
    /// (<see cref="KernelScalars.CompareDouble"/>). Scalar reference only — see the design doc on why floating
    /// comparison is not vectorized (hardware compares disagree with Spark's <c>NaN</c>/<c>-0</c> ordering).
    /// Result validity equals the column's validity.
    /// </summary>
    /// <exception cref="ArgumentException">A result span is smaller than <c>ByteCount(Length)</c>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="left"/> is not a numeric column.</exception>
    public static int Compare(ComparisonOp op, ColumnVector left, double scalar, Span<byte> resultValues, Span<byte> resultValidity)
    {
        ArgumentNullException.ThrowIfNull(left);
        if (left.Type is not (ByteType or ShortType or IntegerType or LongType or FloatType or DoubleType or DecimalType))
        {
            throw new NotSupportedException(
                $"Scalar double comparison requires a numeric column but got '{left.Type.SimpleString}'.");
        }

        int n = left.Length;
        int byteCount = Bitmap.ByteCount(n);
        RequireResultSpans(resultValues.Length, resultValidity.Length, byteCount);
        int nullCount = UnaryValidity(left, resultValidity);

        resultValues[..byteCount].Clear();
        for (int i = 0; i < n; i++)
        {
            if (!Bitmap.Get(resultValidity, i))
            {
                continue;
            }

            if (ApplyOp(op, KernelScalars.CompareDouble(KernelScalars.ReadDouble(left, i), scalar)))
            {
                Bitmap.Set(resultValues, i, true);
            }
        }

        return nullCount;
    }

    // =========================================================================================================
    // Bulk SIMD over contiguous spans (the tier-forced parity surface), value bits only — validity is all-valid.
    // =========================================================================================================

    /// <summary>
    /// Packs <c>left[i] op right[i]</c> for two equal-length <see cref="int"/> spans into the
    /// <paramref name="outValues"/> result bitmap (8 rows per byte, canonical tail). The mask of one
    /// <see cref="Vector256{T}"/>/<see cref="Vector128{T}"/> compare extracts directly to result bits, so every
    /// tier — including the forced ones used by the parity tests — yields an identical bitmap.
    /// </summary>
    /// <exception cref="ArgumentException">The spans differ in length, or <paramref name="outValues"/> is too small.</exception>
    public static void CompareInt32(
        ComparisonOp op,
        ReadOnlySpan<int> left,
        ReadOnlySpan<int> right,
        Span<byte> outValues,
        KernelTier tier = KernelTier.Auto)
    {
        int n = left.Length;
        if (right.Length != n)
        {
            throw new ArgumentException($"left length {n} must equal right length {right.Length}.", nameof(right));
        }

        int byteCount = Bitmap.ByteCount(n);
        if (outValues.Length < byteCount)
        {
            throw new ArgumentException($"result span must be at least {byteCount} bytes.", nameof(outValues));
        }

        ref int pl = ref MemoryMarshal.GetReference(left);
        ref int pr = ref MemoryMarshal.GetReference(right);
        int fullBytes = n >> 3;
        int b = 0;

        if (KernelTierGate.UseVector256(tier))
        {
            for (; b < fullBytes; b++)
            {
                int row = b << 3;
                outValues[b] = (byte)Mask256(op, Vector256.LoadUnsafe(ref pl, (nuint)row), Vector256.LoadUnsafe(ref pr, (nuint)row));
            }
        }
        else if (KernelTierGate.UseVector128(tier))
        {
            for (; b < fullBytes; b++)
            {
                int row = b << 3;
                uint lo = Mask128(op, Vector128.LoadUnsafe(ref pl, (nuint)row), Vector128.LoadUnsafe(ref pr, (nuint)row));
                uint hi = Mask128(op, Vector128.LoadUnsafe(ref pl, (nuint)(row + 4)), Vector128.LoadUnsafe(ref pr, (nuint)(row + 4)));
                outValues[b] = (byte)(lo | (hi << 4));
            }
        }

        for (; b < fullBytes; b++)
        {
            outValues[b] = ScalarByte32(op, left, right, b << 3, 8);
        }

        if ((n & 7) != 0)
        {
            outValues[fullBytes] = ScalarByte32(op, left, right, fullBytes << 3, n & 7);
        }
    }

    /// <summary>Packs <c>left[i] op scalar</c> for an <see cref="int"/> span (broadcast literal) — the predicate-pushdown SIMD path.</summary>
    /// <exception cref="ArgumentException"><paramref name="outValues"/> is too small.</exception>
    public static void CompareInt32(ComparisonOp op, ReadOnlySpan<int> left, int scalar, Span<byte> outValues, KernelTier tier = KernelTier.Auto)
    {
        int n = left.Length;
        int byteCount = Bitmap.ByteCount(n);
        if (outValues.Length < byteCount)
        {
            throw new ArgumentException($"result span must be at least {byteCount} bytes.", nameof(outValues));
        }

        ref int pl = ref MemoryMarshal.GetReference(left);
        int fullBytes = n >> 3;
        int b = 0;

        if (KernelTierGate.UseVector256(tier))
        {
            Vector256<int> rv = Vector256.Create(scalar);
            for (; b < fullBytes; b++)
            {
                int row = b << 3;
                outValues[b] = (byte)Mask256(op, Vector256.LoadUnsafe(ref pl, (nuint)row), rv);
            }
        }
        else if (KernelTierGate.UseVector128(tier))
        {
            Vector128<int> rv = Vector128.Create(scalar);
            for (; b < fullBytes; b++)
            {
                int row = b << 3;
                uint lo = Mask128(op, Vector128.LoadUnsafe(ref pl, (nuint)row), rv);
                uint hi = Mask128(op, Vector128.LoadUnsafe(ref pl, (nuint)(row + 4)), rv);
                outValues[b] = (byte)(lo | (hi << 4));
            }
        }

        for (; b < fullBytes; b++)
        {
            outValues[b] = ScalarByte32(op, left, scalar, b << 3, 8);
        }

        if ((n & 7) != 0)
        {
            outValues[fullBytes] = ScalarByte32(op, left, scalar, fullBytes << 3, n & 7);
        }
    }

    /// <summary>Packs <c>left[i] op right[i]</c> for two equal-length <see cref="long"/> spans (4 lanes per 256-bit vector).</summary>
    /// <exception cref="ArgumentException">The spans differ in length, or <paramref name="outValues"/> is too small.</exception>
    public static void CompareInt64(
        ComparisonOp op,
        ReadOnlySpan<long> left,
        ReadOnlySpan<long> right,
        Span<byte> outValues,
        KernelTier tier = KernelTier.Auto)
    {
        int n = left.Length;
        if (right.Length != n)
        {
            throw new ArgumentException($"left length {n} must equal right length {right.Length}.", nameof(right));
        }

        int byteCount = Bitmap.ByteCount(n);
        if (outValues.Length < byteCount)
        {
            throw new ArgumentException($"result span must be at least {byteCount} bytes.", nameof(outValues));
        }

        ref long pl = ref MemoryMarshal.GetReference(left);
        ref long pr = ref MemoryMarshal.GetReference(right);
        int fullBytes = n >> 3;
        int b = 0;

        if (KernelTierGate.UseVector256(tier))
        {
            for (; b < fullBytes; b++)
            {
                int row = b << 3;
                uint lo = Mask256(op, Vector256.LoadUnsafe(ref pl, (nuint)row), Vector256.LoadUnsafe(ref pr, (nuint)row));
                uint hi = Mask256(op, Vector256.LoadUnsafe(ref pl, (nuint)(row + 4)), Vector256.LoadUnsafe(ref pr, (nuint)(row + 4)));
                outValues[b] = (byte)((lo & 0xF) | ((hi & 0xF) << 4));
            }
        }
        else if (KernelTierGate.UseVector128(tier))
        {
            for (; b < fullBytes; b++)
            {
                int row = b << 3;
                uint q0 = Mask128(op, Vector128.LoadUnsafe(ref pl, (nuint)row), Vector128.LoadUnsafe(ref pr, (nuint)row));
                uint q1 = Mask128(op, Vector128.LoadUnsafe(ref pl, (nuint)(row + 2)), Vector128.LoadUnsafe(ref pr, (nuint)(row + 2)));
                uint q2 = Mask128(op, Vector128.LoadUnsafe(ref pl, (nuint)(row + 4)), Vector128.LoadUnsafe(ref pr, (nuint)(row + 4)));
                uint q3 = Mask128(op, Vector128.LoadUnsafe(ref pl, (nuint)(row + 6)), Vector128.LoadUnsafe(ref pr, (nuint)(row + 6)));
                outValues[b] = (byte)((q0 & 3) | ((q1 & 3) << 2) | ((q2 & 3) << 4) | ((q3 & 3) << 6));
            }
        }

        for (; b < fullBytes; b++)
        {
            outValues[b] = ScalarByte64(op, left, right, b << 3, 8);
        }

        if ((n & 7) != 0)
        {
            outValues[fullBytes] = ScalarByte64(op, left, right, fullBytes << 3, n & 7);
        }
    }

    /// <summary>Packs <c>left[i] op scalar</c> for a <see cref="long"/> span (broadcast literal).</summary>
    /// <exception cref="ArgumentException"><paramref name="outValues"/> is too small.</exception>
    public static void CompareInt64(ComparisonOp op, ReadOnlySpan<long> left, long scalar, Span<byte> outValues, KernelTier tier = KernelTier.Auto)
    {
        int n = left.Length;
        int byteCount = Bitmap.ByteCount(n);
        if (outValues.Length < byteCount)
        {
            throw new ArgumentException($"result span must be at least {byteCount} bytes.", nameof(outValues));
        }

        ref long pl = ref MemoryMarshal.GetReference(left);
        int fullBytes = n >> 3;
        int b = 0;

        if (KernelTierGate.UseVector256(tier))
        {
            Vector256<long> rv = Vector256.Create(scalar);
            for (; b < fullBytes; b++)
            {
                int row = b << 3;
                uint lo = Mask256(op, Vector256.LoadUnsafe(ref pl, (nuint)row), rv);
                uint hi = Mask256(op, Vector256.LoadUnsafe(ref pl, (nuint)(row + 4)), rv);
                outValues[b] = (byte)((lo & 0xF) | ((hi & 0xF) << 4));
            }
        }
        else if (KernelTierGate.UseVector128(tier))
        {
            Vector128<long> rv = Vector128.Create(scalar);
            for (; b < fullBytes; b++)
            {
                int row = b << 3;
                uint q0 = Mask128(op, Vector128.LoadUnsafe(ref pl, (nuint)row), rv);
                uint q1 = Mask128(op, Vector128.LoadUnsafe(ref pl, (nuint)(row + 2)), rv);
                uint q2 = Mask128(op, Vector128.LoadUnsafe(ref pl, (nuint)(row + 4)), rv);
                uint q3 = Mask128(op, Vector128.LoadUnsafe(ref pl, (nuint)(row + 6)), rv);
                outValues[b] = (byte)((q0 & 3) | ((q1 & 3) << 2) | ((q2 & 3) << 4) | ((q3 & 3) << 6));
            }
        }

        for (; b < fullBytes; b++)
        {
            outValues[b] = ScalarByte64(op, left, scalar, b << 3, 8);
        }

        if ((n & 7) != 0)
        {
            outValues[fullBytes] = ScalarByte64(op, left, scalar, fullBytes << 3, n & 7);
        }
    }

    // =========================================================================================================
    // Internals
    // =========================================================================================================

    /// <summary>Extracts the per-lane truth of <c>l op r</c> as the low <c>Count</c> bits of a <see cref="uint"/>.</summary>
    /// <remarks>
    /// Built from <c>Equals</c>/<c>LessThan</c>/<c>GreaterThan</c> only: integer order is total, so <c>&lt;=</c> is
    /// <c>~GreaterThan</c>, <c>&gt;=</c> is <c>~LessThan</c>, and <c>&lt;&gt;</c> is <c>~Equals</c> — no dependence on
    /// the optional <c>LessThanOrEqual</c>/<c>GreaterThanOrEqual</c> vector helpers. Every block is a full lane group,
    /// so complementing all lanes is correct (tail rows never reach here).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Mask256<T>(ComparisonOp op, Vector256<T> l, Vector256<T> r)
        where T : unmanaged
    {
        Vector256<T> m = op switch
        {
            ComparisonOp.Equal => Vector256.Equals(l, r),
            ComparisonOp.NotEqual => ~Vector256.Equals(l, r),
            ComparisonOp.LessThan => Vector256.LessThan(l, r),
            ComparisonOp.LessThanOrEqual => ~Vector256.GreaterThan(l, r),
            ComparisonOp.GreaterThan => Vector256.GreaterThan(l, r),
            ComparisonOp.GreaterThanOrEqual => ~Vector256.LessThan(l, r),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };

        return Vector256.ExtractMostSignificantBits(m);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Mask128<T>(ComparisonOp op, Vector128<T> l, Vector128<T> r)
        where T : unmanaged
    {
        Vector128<T> m = op switch
        {
            ComparisonOp.Equal => Vector128.Equals(l, r),
            ComparisonOp.NotEqual => ~Vector128.Equals(l, r),
            ComparisonOp.LessThan => Vector128.LessThan(l, r),
            ComparisonOp.LessThanOrEqual => ~Vector128.GreaterThan(l, r),
            ComparisonOp.GreaterThan => Vector128.GreaterThan(l, r),
            ComparisonOp.GreaterThanOrEqual => ~Vector128.LessThan(l, r),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };

        return Vector128.ExtractMostSignificantBits(m);
    }

    private static byte ScalarByte32(ComparisonOp op, ReadOnlySpan<int> left, ReadOnlySpan<int> right, int row, int count)
    {
        byte bits = 0;
        for (int k = 0; k < count; k++)
        {
            if (ApplyOp(op, left[row + k].CompareTo(right[row + k])))
            {
                bits |= (byte)(1 << k);
            }
        }

        return bits;
    }

    private static byte ScalarByte32(ComparisonOp op, ReadOnlySpan<int> left, int scalar, int row, int count)
    {
        byte bits = 0;
        for (int k = 0; k < count; k++)
        {
            if (ApplyOp(op, left[row + k].CompareTo(scalar)))
            {
                bits |= (byte)(1 << k);
            }
        }

        return bits;
    }

    private static byte ScalarByte64(ComparisonOp op, ReadOnlySpan<long> left, ReadOnlySpan<long> right, int row, int count)
    {
        byte bits = 0;
        for (int k = 0; k < count; k++)
        {
            if (ApplyOp(op, left[row + k].CompareTo(right[row + k])))
            {
                bits |= (byte)(1 << k);
            }
        }

        return bits;
    }

    private static byte ScalarByte64(ComparisonOp op, ReadOnlySpan<long> left, long scalar, int row, int count)
    {
        byte bits = 0;
        for (int k = 0; k < count; k++)
        {
            if (ApplyOp(op, left[row + k].CompareTo(scalar)))
            {
                bits |= (byte)(1 << k);
            }
        }

        return bits;
    }

    /// <summary>Maps a three-way comparison sign to the predicate's boolean result.</summary>
    private static bool ApplyOp(ComparisonOp op, int sign) => op switch
    {
        ComparisonOp.Equal => sign == 0,
        ComparisonOp.NotEqual => sign != 0,
        ComparisonOp.LessThan => sign < 0,
        ComparisonOp.LessThanOrEqual => sign <= 0,
        ComparisonOp.GreaterThan => sign > 0,
        ComparisonOp.GreaterThanOrEqual => sign >= 0,
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    /// <summary>The scalar-reference sign of <c>left[i]</c> vs <c>right[i]</c> for the resolved comparison kind.</summary>
    private static int CompareSign(CmpKind kind, ColumnVector left, ColumnVector right, int i) => kind switch
    {
        CmpKind.Int64 => KernelScalars.ReadInt64(left, i).CompareTo(KernelScalars.ReadInt64(right, i)),
        CmpKind.Double => KernelScalars.CompareDouble(KernelScalars.ReadDouble(left, i), KernelScalars.ReadDouble(right, i)),
        CmpKind.Decimal => KernelScalars.CompareDecimal(KernelScalars.ReadDecimal(left, i), KernelScalars.ReadDecimal(right, i)),
        _ => PromoteToMicros(left, i).CompareTo(PromoteToMicros(right, i)),
    };

    /// <summary>A date (days) lifts to its UTC-midnight instant in micros; a timestamp is already micros — the interpreter's rule.</summary>
    private static long PromoteToMicros(ColumnVector vector, int i) =>
        vector.Type is DateType
            ? KernelScalars.ReadInt64(vector, i) * TemporalValues.MicrosPerDay
            : KernelScalars.ReadInt64(vector, i);

    private static CmpKind Classify(DataType left, DataType right)
    {
        RequireComparable(left);
        RequireComparable(right);
        if (left is FloatType or DoubleType || right is FloatType or DoubleType)
        {
            return CmpKind.Double;
        }

        if (left is DecimalType || right is DecimalType)
        {
            return CmpKind.Decimal;
        }

        if ((left is DateType && right is TimestampType) || (left is TimestampType && right is DateType))
        {
            return CmpKind.TemporalPromote;
        }

        return CmpKind.Int64;
    }

    private static void RequireComparable(DataType type)
    {
        if (type is not (BooleanType or ByteType or ShortType or IntegerType or LongType
            or FloatType or DoubleType or DecimalType or DateType or TimestampType))
        {
            throw new NotSupportedException(
                $"Comparison kernel does not support operand type '{type.SimpleString}'. String/binary comparisons "
                + "remain on the interpreter path (AC2 scope is primitive/decimal/date/timestamp); compare→selection "
                + "left-pack is deferred to STORY-03.3.2 (#150/#153).");
        }
    }

    /// <summary>
    /// Writes <c>out_valid = left_valid &amp; right_valid</c> and returns the null count. Reuses the #144
    /// <see cref="NullMasks.PropagateBinary(Validity, Validity, Span{byte}, NullMaskTier)"/> AND-combine when both
    /// operands surface a packed validity bitmap (always true on the no-null fast path); otherwise a per-row
    /// reference fallback (today's null-bearing managed/Arrow vectors do not expose a packed bitmap).
    /// </summary>
    private static int CombineValidity(ColumnVector left, ColumnVector right, Span<byte> output, int n)
    {
        if (left.TryGetValidity(out Validity vl) && right.TryGetValidity(out Validity vr))
        {
            return NullMasks.PropagateBinary(vl, vr, output);
        }

        int byteCount = Bitmap.ByteCount(n);
        output[..byteCount].Clear();
        bool leftNulls = left.HasNulls;
        bool rightNulls = right.HasNulls;
        int nulls = 0;
        for (int i = 0; i < n; i++)
        {
            if ((leftNulls && left.IsNull(i)) || (rightNulls && right.IsNull(i)))
            {
                nulls++;
                continue;
            }

            Bitmap.Set(output, i, true);
        }

        return nulls;
    }

    /// <summary>Writes the column's validity (the scalar literal is never null) and returns the null count, reusing #144 when a bitmap is exposed.</summary>
    private static int UnaryValidity(ColumnVector vector, Span<byte> output)
    {
        if (vector.TryGetValidity(out Validity validity))
        {
            return NullMasks.PropagateUnary(validity, output);
        }

        int n = vector.Length;
        int byteCount = Bitmap.ByteCount(n);
        output[..byteCount].Clear();
        int nulls = 0;
        for (int i = 0; i < n; i++)
        {
            if (vector.IsNull(i))
            {
                nulls++;
                continue;
            }

            Bitmap.Set(output, i, true);
        }

        return nulls;
    }

    private static void RequireResultSpans(int valuesLength, int validityLength, int byteCount)
    {
        if (valuesLength < byteCount)
        {
            throw new ArgumentException($"result values span must be at least {byteCount} bytes.", "resultValues");
        }

        if (validityLength < byteCount)
        {
            throw new ArgumentException($"result validity span must be at least {byteCount} bytes.", "resultValidity");
        }
    }
}
