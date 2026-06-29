using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// Branchless, SIMD-accelerated word operations over Arrow LSB-first validity bitmaps — the
/// vectorized fast-path primitives under the scalar <see cref="Bitmap"/>/<see cref="NullPropagation"/>
/// contracts (STORY-02.6.2, #144). Every method writes only whole packed bytes and treats a bitmap as
/// a stream of <c>byte</c>/<c>ulong</c>/<see cref="Vector128{T}"/>/<see cref="Vector256{T}"/> words, so
/// the inner loops contain <b>no data-dependent branches</b> (pure mask arithmetic).
/// </summary>
/// <remarks>
/// <para>
/// <b>SIMD width and fallback (AOT-safe).</b> The vector tiers use
/// <see cref="System.Runtime.Intrinsics"/> <see cref="Vector256"/>/<see cref="Vector128"/> guarded by
/// their <c>IsHardwareAccelerated</c> properties, which the JIT and the NativeAOT ILCompiler resolve to
/// <b>compile-time constants</b> per target — so an unsupported tier is dead-code-eliminated rather than
/// throwing, and no dynamic codegen is involved (ADR-0001, ADR-0014). Below the vector tiers a
/// <c>ulong</c> (8-byte) loop and a final <c>byte</c> loop are the scalar fallback, so the kernels stay
/// correct even when no SIMD is available. <see cref="BitOperations.PopCount(ulong)"/> is itself the
/// AOT-safe hardware popcount (lowers to <c>POPCNT</c>/<c>CNT</c> with a software fallback).
/// </para>
/// <para>
/// <b>Canonical padding.</b> The trailing bits of the final partial byte (the lanes at index
/// <c>&gt;= length</c>) are forced to <c>0</c> on every write, matching the canonicalization the scalar
/// bulk kernels guarantee (null-validity-model.md). This makes a <c>memcmp</c> against the scalar
/// reference output unambiguous and lets <see cref="PopCount"/> ignore padding without per-call masking
/// of the inputs.
/// </para>
/// </remarks>
internal static class BitmapOps
{
    /// <summary>
    /// Whether at least one SIMD vector tier backs these kernels on the current hardware. Informational
    /// only (the scalar fallback is always correct); recorded in benchmark metadata.
    /// </summary>
    public static bool IsHardwareAccelerated => Vector128.IsHardwareAccelerated || Vector256.IsHardwareAccelerated;

    /// <summary>The widest vector lane (in bytes) used by the inner loop on the current hardware.</summary>
    public static int VectorByteWidth =>
        Vector256.IsHardwareAccelerated ? Vector256<byte>.Count :
        Vector128.IsHardwareAccelerated ? Vector128<byte>.Count : sizeof(ulong);

    /// <summary>
    /// The keep-mask for the final partial byte covering <paramref name="length"/> logical bits:
    /// <c>0xFF</c> when <paramref name="length"/> is a whole number of bytes, otherwise a low-bit mask
    /// that clears the padding lanes.
    /// </summary>
    public static byte TailMask(int length)
    {
        int remainder = length & 7;
        return remainder == 0 ? (byte)0xFF : (byte)((1 << remainder) - 1);
    }

    /// <summary>
    /// Counts <b>set</b> (valid) bits in the logical window <c>[0, length)</c> of a packed bitmap,
    /// masking off any padding lanes in the final byte. Word-parallel via
    /// <see cref="BitOperations.PopCount(ulong)"/> (hardware <c>POPCNT</c>/<c>CNT</c>); branchless.
    /// </summary>
    public static int PopCount(ReadOnlySpan<byte> bits, int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        int fullBytes = length >> 3;
        int tailBits = length & 7;
        ref byte head = ref MemoryMarshal.GetReference(bits);

        int count = 0;
        int i = 0;
        for (; i <= fullBytes - sizeof(ulong); i += sizeof(ulong))
        {
            count += BitOperations.PopCount(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref head, i)));
        }

        for (; i < fullBytes; i++)
        {
            count += BitOperations.PopCount((uint)bits[i]);
        }

        if (tailBits != 0)
        {
            count += BitOperations.PopCount((uint)(bits[fullBytes] & ((1 << tailBits) - 1)));
        }

        return count;
    }

    /// <summary>
    /// Counts <b>cleared</b> (null) bits in <c>[0, length)</c> — the vectorized analogue of
    /// <see cref="Bitmap.CountNulls"/> for an offset-0 buffer.
    /// </summary>
    public static int CountNulls(ReadOnlySpan<byte> bits, int length) => length - PopCount(bits, length);

    /// <summary>
    /// Writes <c>dest = a &amp; b</c> over the <c>ByteCount(length)</c> bytes covering <c>[0, length)</c>
    /// (propagate-on-any-null validity AND), canonicalizing the trailing padding to <c>0</c>. Inputs and
    /// <paramref name="dest"/> must each be at least <c>ByteCount(length)</c> bytes.
    /// </summary>
    public static void And(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dest, int length)
    {
        int byteCount = Bitmap.ByteCount(length);
        if (byteCount == 0)
        {
            return;
        }

        ref byte ra = ref MemoryMarshal.GetReference(a);
        ref byte rb = ref MemoryMarshal.GetReference(b);
        ref byte rd = ref MemoryMarshal.GetReference(dest);

        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= byteCount - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                (Vector256.LoadUnsafe(ref ra, (nuint)i) & Vector256.LoadUnsafe(ref rb, (nuint)i)).StoreUnsafe(ref rd, (nuint)i);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; i <= byteCount - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                (Vector128.LoadUnsafe(ref ra, (nuint)i) & Vector128.LoadUnsafe(ref rb, (nuint)i)).StoreUnsafe(ref rd, (nuint)i);
            }
        }

        for (; i <= byteCount - sizeof(ulong); i += sizeof(ulong))
        {
            ulong word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref ra, i)) & Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rb, i));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref rd, i), word);
        }

        for (; i < byteCount; i++)
        {
            dest[i] = (byte)(a[i] & b[i]);
        }

        ApplyTailMask(dest, length);
    }

    /// <summary>
    /// Fills the <c>ByteCount(length)</c> bytes covering <c>[0, length)</c> with all-valid bits
    /// (<c>0xFF</c>) and canonicalizes the trailing padding to <c>0</c> — the materialized all-valid
    /// output used when an operand carries no bitmap.
    /// </summary>
    public static void FillValid(Span<byte> dest, int length)
    {
        int byteCount = Bitmap.ByteCount(length);
        dest[..byteCount].Fill(0xFF);
        ApplyTailMask(dest, length);
    }

    /// <summary>
    /// Copies the <c>ByteCount(length)</c> validity bytes covering <c>[0, length)</c> from
    /// <paramref name="src"/> to <paramref name="dest"/> and canonicalizes the trailing padding to
    /// <c>0</c> — unary propagate (<c>out = in</c>).
    /// </summary>
    public static void CopyValidity(ReadOnlySpan<byte> src, Span<byte> dest, int length)
    {
        int byteCount = Bitmap.ByteCount(length);
        src[..byteCount].CopyTo(dest);
        ApplyTailMask(dest, length);
    }

    /// <summary>Clears the padding lanes (index <c>&gt;= length</c>) of the final byte in place.</summary>
    private static void ApplyTailMask(Span<byte> dest, int length)
    {
        int byteCount = Bitmap.ByteCount(length);
        if (byteCount > 0 && (length & 7) != 0)
        {
            dest[byteCount - 1] &= TailMask(length);
        }
    }
}
