using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// Branchless, SIMD-accelerated null-mask kernels: the vectorized fast path under the scalar
/// <see cref="NullPropagation"/> three-valued-logic (3VL) contracts (STORY-02.6.2, #144). Each kernel
/// is a bit-for-bit equal, faster alternative to its <see cref="NullPropagation"/> counterpart — the
/// scalar <c>bool?</c> reference there is the <b>parity oracle</b> these kernels are validated against
/// (ADR-0001: the interpreter is the correctness ground truth and the vectorized tier must match it).
/// </summary>
/// <remarks>
/// <para>
/// <b>Bit-packed boolean representation.</b> Where <see cref="NullPropagation"/> models a boolean column
/// as <c>ReadOnlySpan&lt;bool&gt;</c> values plus a <see cref="Validity"/>, the Kleene kernels here take
/// the SIMD-native form: a <b>value</b> bitmap and a <b>validity</b> bitmap, both Arrow LSB-first packed
/// (bit <c>i</c> in byte <c>i/8</c>, position <c>i%8</c>; value bit meaningful only where the validity
/// bit is set). This turns 3VL into pure bitwise word formulas:
/// </para>
/// <list type="bullet">
/// <item><description>Let <c>tX = vX &amp; bX</c> ("valid TRUE") and <c>fX = vX &amp; ~bX</c> ("valid FALSE").</description></item>
/// <item><description><c>AND</c>: <c>value = tL &amp; tR</c>; <c>valid = fL | fR | (tL &amp; tR)</c> — a valid FALSE rescues the null.</description></item>
/// <item><description><c>OR</c>: <c>value = tL | tR</c>; <c>valid = (tL | tR) | (fL &amp; fR)</c> — a valid TRUE rescues the null.</description></item>
/// <item><description><c>NOT</c>: <c>valid = vX</c>; <c>value = vX &amp; ~bX</c> — nulls propagate unchanged.</description></item>
/// </list>
/// <para>
/// In every case a null output lane (validity bit <c>0</c>) carries value bit <c>0</c>, matching the
/// deterministic <c>false</c> placeholder the scalar kernels write. The bitwise formulas are applied a
/// whole word at a time by <see cref="BitmapOps"/>'s vector/<c>ulong</c>/<c>byte</c> tiers, so there are
/// no per-lane branches, no scratch buffers, and no allocation on the hot path.
/// </para>
/// </remarks>
internal static class NullMasks
{
    // ----------------------------------------------------------------------------------------
    // Propagate-on-any-null (arithmetic / comparison): out_valid = AND of input validity bitmaps.
    // Validity-based entry points mirror NullPropagation so parity is apples-to-apples.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Vectorized unary propagate (<c>out_valid = in_valid</c>): writes the output validity bitmap for
    /// <paramref name="input"/> and returns the null count. Byte-identical to
    /// <see cref="NullPropagation.PropagateUnary"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="output"/> is smaller than <c>ByteCount(input.Length)</c>.</exception>
    public static int PropagateUnary(Validity input, Span<byte> output)
    {
        int length = input.Length;
        RequireOutput(output.Length, Bitmap.ByteCount(length));
        if (length == 0)
        {
            return 0;
        }

        if (!input.HasBitmap)
        {
            BitmapOps.FillValid(output, length);
            return 0;
        }

        if (IsByteAligned(input.Offset))
        {
            BitmapOps.CopyValidity(input.Bits.Slice(input.Offset >> 3), output, length);
            return BitmapOps.CountNulls(output, length);
        }

        // Bit-unaligned slice: defer to the scalar reference (still the canonical result).
        return NullPropagation.PropagateUnary(input, output);
    }

    /// <summary>
    /// Vectorized binary propagate (<c>out_valid = left_valid &amp; right_valid</c>): writes the output
    /// validity bitmap and returns the null count. Byte-identical to
    /// <see cref="NullPropagation.PropagateBinary"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The operand lengths differ, or <paramref name="output"/> is too small.</exception>
    public static int PropagateBinary(Validity left, Validity right, Span<byte> output)
    {
        int length = left.Length;
        RequireSameLength(length, right.Length, "operand validity");
        RequireOutput(output.Length, Bitmap.ByteCount(length));
        if (length == 0)
        {
            return 0;
        }

        bool leftAligned = IsByteAligned(left.Offset);
        bool rightAligned = IsByteAligned(right.Offset);

        // Both all-valid (no bitmap): output is all-valid, zero nulls — the no-null fast path.
        if (!left.HasBitmap && !right.HasBitmap)
        {
            BitmapOps.FillValid(output, length);
            return 0;
        }

        // Both present and byte-aligned: a single bytewise AND over the sliced buffers.
        if (left.HasBitmap && right.HasBitmap && leftAligned && rightAligned)
        {
            BitmapOps.And(left.Bits.Slice(left.Offset >> 3), right.Bits.Slice(right.Offset >> 3), output, length);
            return BitmapOps.CountNulls(output, length);
        }

        // Exactly one present and aligned: the result equals that operand's validity (AND with all-ones).
        if (left.HasBitmap && !right.HasBitmap && leftAligned)
        {
            BitmapOps.CopyValidity(left.Bits.Slice(left.Offset >> 3), output, length);
            return BitmapOps.CountNulls(output, length);
        }

        if (!left.HasBitmap && right.HasBitmap && rightAligned)
        {
            BitmapOps.CopyValidity(right.Bits.Slice(right.Offset >> 3), output, length);
            return BitmapOps.CountNulls(output, length);
        }

        // Bit-unaligned slice(s): defer to the scalar reference (still the canonical result).
        return NullPropagation.PropagateBinary(left, right, output);
    }

    // ----------------------------------------------------------------------------------------
    // Kleene 3VL over the bit-packed (value, validity) representation. Value-aware, fully fused.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Kleene <c>AND</c> over bit-packed boolean operands, writing the output value and validity bitmaps
    /// and returning the null count. Per-lane equal to <see cref="NullPropagation.KleeneAnd(bool?, bool?)"/>.
    /// </summary>
    public static int KleeneAnd(
        ReadOnlySpan<byte> leftValues,
        ReadOnlySpan<byte> leftValidity,
        ReadOnlySpan<byte> rightValues,
        ReadOnlySpan<byte> rightValidity,
        Span<byte> resultValues,
        Span<byte> resultValidity,
        int length)
        => ApplyBinary<KleeneAndOperator>(
            leftValues, leftValidity, rightValues, rightValidity, resultValues, resultValidity, length);

    /// <summary>
    /// Kleene <c>OR</c> over bit-packed boolean operands, writing the output value and validity bitmaps
    /// and returning the null count. Per-lane equal to <see cref="NullPropagation.KleeneOr(bool?, bool?)"/>.
    /// </summary>
    public static int KleeneOr(
        ReadOnlySpan<byte> leftValues,
        ReadOnlySpan<byte> leftValidity,
        ReadOnlySpan<byte> rightValues,
        ReadOnlySpan<byte> rightValidity,
        Span<byte> resultValues,
        Span<byte> resultValidity,
        int length)
        => ApplyBinary<KleeneOrOperator>(
            leftValues, leftValidity, rightValues, rightValidity, resultValues, resultValidity, length);

    /// <summary>
    /// Kleene <c>NOT</c> over a bit-packed boolean operand: <c>out_valid = valid</c>,
    /// <c>out_value = valid &amp; ~value</c>. Per-lane equal to
    /// <see cref="NullPropagation.KleeneNot(bool?)"/>; returns the null count.
    /// </summary>
    public static int KleeneNot(
        ReadOnlySpan<byte> values,
        ReadOnlySpan<byte> validity,
        Span<byte> resultValues,
        Span<byte> resultValidity,
        int length)
    {
        int byteCount = Bitmap.ByteCount(length);
        RequireInput(values.Length, byteCount, "values");
        RequireInput(validity.Length, byteCount, "validity");
        RequireOutput(resultValues.Length, byteCount);
        RequireOutput(resultValidity.Length, byteCount);
        if (length == 0)
        {
            return 0;
        }

        ref byte rb = ref MemoryMarshal.GetReference(values);
        ref byte rv = ref MemoryMarshal.GetReference(validity);
        ref byte rOutValue = ref MemoryMarshal.GetReference(resultValues);
        ref byte rOutValid = ref MemoryMarshal.GetReference(resultValidity);

        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= byteCount - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                Vector256<byte> b = Vector256.LoadUnsafe(ref rb, (nuint)i);
                Vector256<byte> v = Vector256.LoadUnsafe(ref rv, (nuint)i);
                Vector256.AndNot(v, b).StoreUnsafe(ref rOutValue, (nuint)i); // valid & ~value
                v.StoreUnsafe(ref rOutValid, (nuint)i);                      // out_valid = valid
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; i <= byteCount - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> b = Vector128.LoadUnsafe(ref rb, (nuint)i);
                Vector128<byte> v = Vector128.LoadUnsafe(ref rv, (nuint)i);
                Vector128.AndNot(v, b).StoreUnsafe(ref rOutValue, (nuint)i);
                v.StoreUnsafe(ref rOutValid, (nuint)i);
            }
        }

        for (; i <= byteCount - sizeof(ulong); i += sizeof(ulong))
        {
            ulong b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rb, i));
            ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rv, i));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref rOutValue, i), v & ~b);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref rOutValid, i), v);
        }

        for (; i < byteCount; i++)
        {
            byte v = validity[i];
            resultValues[i] = (byte)(v & ~values[i]);
            resultValidity[i] = v;
        }

        Canonicalize(resultValues, resultValidity, length);
        return BitmapOps.CountNulls(resultValidity, length);
    }

    // ----------------------------------------------------------------------------------------
    // Internals: the shared fused binary Kleene loop + the per-op branchless formulas.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// One bit-packed boolean Kleene connective expressed as branchless word formulas at every width.
    /// Implemented by <see langword="readonly"/> <see langword="struct"/>s so the generic loop is
    /// monomorphized and devirtualized (no allocation, no virtual dispatch) by the JIT/AOT compiler.
    /// </summary>
    private interface IKleeneBinaryOperator
    {
        static abstract void Apply(ulong bL, ulong vL, ulong bR, ulong vR, out ulong value, out ulong valid);

        static abstract void Apply(
            Vector128<byte> bL, Vector128<byte> vL, Vector128<byte> bR, Vector128<byte> vR,
            out Vector128<byte> value, out Vector128<byte> valid);

        static abstract void Apply(
            Vector256<byte> bL, Vector256<byte> vL, Vector256<byte> bR, Vector256<byte> vR,
            out Vector256<byte> value, out Vector256<byte> valid);
    }

    private readonly struct KleeneAndOperator : IKleeneBinaryOperator
    {
        // value = tL & tR ; valid = fL | fR | (tL & tR)  where tX = vX & bX, fX = vX & ~bX.
        public static void Apply(ulong bL, ulong vL, ulong bR, ulong vR, out ulong value, out ulong valid)
        {
            ulong bothTrue = (vL & bL) & (vR & bR);
            value = bothTrue;
            valid = (vL & ~bL) | (vR & ~bR) | bothTrue;
        }

        public static void Apply(
            Vector128<byte> bL, Vector128<byte> vL, Vector128<byte> bR, Vector128<byte> vR,
            out Vector128<byte> value, out Vector128<byte> valid)
        {
            Vector128<byte> bothTrue = (vL & bL) & (vR & bR);
            value = bothTrue;
            valid = (Vector128.AndNot(vL, bL) | Vector128.AndNot(vR, bR)) | bothTrue;
        }

        public static void Apply(
            Vector256<byte> bL, Vector256<byte> vL, Vector256<byte> bR, Vector256<byte> vR,
            out Vector256<byte> value, out Vector256<byte> valid)
        {
            Vector256<byte> bothTrue = (vL & bL) & (vR & bR);
            value = bothTrue;
            valid = (Vector256.AndNot(vL, bL) | Vector256.AndNot(vR, bR)) | bothTrue;
        }
    }

    private readonly struct KleeneOrOperator : IKleeneBinaryOperator
    {
        // value = tL | tR ; valid = (tL | tR) | (fL & fR)  where tX = vX & bX, fX = vX & ~bX.
        public static void Apply(ulong bL, ulong vL, ulong bR, ulong vR, out ulong value, out ulong valid)
        {
            ulong anyTrue = (vL & bL) | (vR & bR);
            value = anyTrue;
            valid = anyTrue | ((vL & ~bL) & (vR & ~bR));
        }

        public static void Apply(
            Vector128<byte> bL, Vector128<byte> vL, Vector128<byte> bR, Vector128<byte> vR,
            out Vector128<byte> value, out Vector128<byte> valid)
        {
            Vector128<byte> anyTrue = (vL & bL) | (vR & bR);
            value = anyTrue;
            valid = anyTrue | (Vector128.AndNot(vL, bL) & Vector128.AndNot(vR, bR));
        }

        public static void Apply(
            Vector256<byte> bL, Vector256<byte> vL, Vector256<byte> bR, Vector256<byte> vR,
            out Vector256<byte> value, out Vector256<byte> valid)
        {
            Vector256<byte> anyTrue = (vL & bL) | (vR & bR);
            value = anyTrue;
            valid = anyTrue | (Vector256.AndNot(vL, bL) & Vector256.AndNot(vR, bR));
        }
    }

    private static int ApplyBinary<TOp>(
        ReadOnlySpan<byte> leftValues,
        ReadOnlySpan<byte> leftValidity,
        ReadOnlySpan<byte> rightValues,
        ReadOnlySpan<byte> rightValidity,
        Span<byte> resultValues,
        Span<byte> resultValidity,
        int length)
        where TOp : struct, IKleeneBinaryOperator
    {
        int byteCount = Bitmap.ByteCount(length);
        RequireInput(leftValues.Length, byteCount, "left values");
        RequireInput(leftValidity.Length, byteCount, "left validity");
        RequireInput(rightValues.Length, byteCount, "right values");
        RequireInput(rightValidity.Length, byteCount, "right validity");
        RequireOutput(resultValues.Length, byteCount);
        RequireOutput(resultValidity.Length, byteCount);
        if (length == 0)
        {
            return 0;
        }

        ref byte rbL = ref MemoryMarshal.GetReference(leftValues);
        ref byte rvL = ref MemoryMarshal.GetReference(leftValidity);
        ref byte rbR = ref MemoryMarshal.GetReference(rightValues);
        ref byte rvR = ref MemoryMarshal.GetReference(rightValidity);
        ref byte rOutValue = ref MemoryMarshal.GetReference(resultValues);
        ref byte rOutValid = ref MemoryMarshal.GetReference(resultValidity);

        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= byteCount - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                TOp.Apply(
                    Vector256.LoadUnsafe(ref rbL, (nuint)i), Vector256.LoadUnsafe(ref rvL, (nuint)i),
                    Vector256.LoadUnsafe(ref rbR, (nuint)i), Vector256.LoadUnsafe(ref rvR, (nuint)i),
                    out Vector256<byte> value, out Vector256<byte> valid);
                value.StoreUnsafe(ref rOutValue, (nuint)i);
                valid.StoreUnsafe(ref rOutValid, (nuint)i);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; i <= byteCount - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                TOp.Apply(
                    Vector128.LoadUnsafe(ref rbL, (nuint)i), Vector128.LoadUnsafe(ref rvL, (nuint)i),
                    Vector128.LoadUnsafe(ref rbR, (nuint)i), Vector128.LoadUnsafe(ref rvR, (nuint)i),
                    out Vector128<byte> value, out Vector128<byte> valid);
                value.StoreUnsafe(ref rOutValue, (nuint)i);
                valid.StoreUnsafe(ref rOutValid, (nuint)i);
            }
        }

        for (; i <= byteCount - sizeof(ulong); i += sizeof(ulong))
        {
            TOp.Apply(
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rbL, i)), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rvL, i)),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rbR, i)), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rvR, i)),
                out ulong value, out ulong valid);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref rOutValue, i), value);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref rOutValid, i), valid);
        }

        for (; i < byteCount; i++)
        {
            TOp.Apply(leftValues[i], leftValidity[i], rightValues[i], rightValidity[i], out ulong value, out ulong valid);
            resultValues[i] = (byte)value;
            resultValidity[i] = (byte)valid;
        }

        Canonicalize(resultValues, resultValidity, length);
        return BitmapOps.CountNulls(resultValidity, length);
    }

    /// <summary>Clears the trailing padding lanes of both output bitmaps to the canonical <c>0</c>.</summary>
    private static void Canonicalize(Span<byte> values, Span<byte> validity, int length)
    {
        int byteCount = Bitmap.ByteCount(length);
        if (byteCount > 0 && (length & 7) != 0)
        {
            byte mask = BitmapOps.TailMask(length);
            values[byteCount - 1] &= mask;
            validity[byteCount - 1] &= mask;
        }
    }

    private static bool IsByteAligned(int offset) => (offset & 7) == 0;

    private static void RequireSameLength(int expected, int actual, string what)
    {
        if (expected != actual)
        {
            throw new ArgumentException($"Length mismatch for {what}: expected {expected} but got {actual}.");
        }
    }

    private static void RequireInput(int spanLength, int requiredBytes, string what)
    {
        if (spanLength < requiredBytes)
        {
            throw new ArgumentException($"{what} bitmap needs at least {requiredBytes} byte(s) but has {spanLength}.");
        }
    }

    private static void RequireOutput(int outputLength, int requiredBytes)
    {
        if (outputLength < requiredBytes)
        {
            throw new ArgumentException($"Output bitmap needs at least {requiredBytes} byte(s) but has {outputLength}.");
        }
    }
}
