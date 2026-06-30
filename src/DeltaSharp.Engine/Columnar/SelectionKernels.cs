using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// Reusable kernels that turn a packed predicate bitmap into a <see cref="SelectionVector"/> (and compose an
/// existing selection with a new predicate) so filters and joins push selection down without materializing rows
/// (STORY-03.3.2, #150; ADR-0002 late materialization). Every method reads only packed bits and writes only
/// <c>int</c> indices, so no value buffer is copied.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bitmap → selection (AC1).</b> <see cref="ToSelection(ReadOnlySpan{byte}, int, int, Span{int}, KernelTier)"/>
/// walks the logical window <c>[offset, offset + length)</c> of an Arrow LSB-first bitmap (a <b>set</b> bit = the
/// row passes) and writes the logical index <c>i ∈ [0, length)</c> of every set bit, in ascending order. Because
/// each logical index is visited exactly once and emitted only when its bit is set, the output is <b>ordered</b>,
/// <b>unique</b>, and <b>in <c>[0, length)</c></b> by construction. Arbitrary <paramref name="offset" /> values
/// (including non-byte-aligned) and arbitrary tail lengths are handled by a scalar lead-in to the next source byte
/// boundary plus a scalar tail over the final <c>&lt; 8</c> bits, so padding lanes beyond <c>length</c> are never
/// inspected.
/// </para>
/// <para>
/// <b>Selection ∘ predicate (AC2).</b> <see cref="Compose(ReadOnlySpan{int}, ReadOnlySpan{byte}, Span{int}, KernelTier)"/>
/// takes an existing selection (base → original-row-space indices) and a predicate bitmap whose bit <c>p</c> answers
/// "did the row at selection position <c>p</c> pass?"; it emits <c>selection[p]</c> for every set <c>p</c>, in
/// selection order. The predicate therefore applies <i>over the already-selected rows</i>, and the emitted indices
/// stay in the <b>original physical row space</b> — exactly the rows produced by applying the base selection's
/// predicate and then this predicate in sequence. Order and uniqueness of the base selection are preserved (the
/// kernel only drops positions, never reorders or duplicates them).
/// </para>
/// <para>
/// <b>Scalar reference is the oracle (ADR-0001).</b> The <see cref="KernelTier.Scalar"/> path is a pure per-bit
/// loop — the in-code reference. The <see cref="KernelTier.Vector128"/>/<see cref="KernelTier.Vector256"/> paths are
/// an <i>additive</i> accelerator: they load a 16-/32-byte chunk of the bitmap and, when the whole chunk is zero,
/// skip 128/256 logical indices with a single compare; a non-zero chunk falls back to the identical per-byte
/// bit-scan (<see cref="BitOperations.TrailingZeroCount(uint)" />). A zero byte emits nothing on either path, so the
/// emitted index sequence is <b>bit-identical across tiers</b> regardless of which zero regions a tier skips
/// wholesale (AC3). Tier dispatch reuses the <see cref="KernelTierGate" /> seam (#149/#144): under
/// <see cref="KernelTier.Auto" /> each tier is gated by its <c>IsHardwareAccelerated</c> guard (so an unsupported tier
/// is dead-code-eliminated, ADR-0014), while a forced value runs the portable-vector body on any host — including the
/// arm64 CI box where <see cref="Vector256" /> is a software fallback.
/// </para>
/// <para>
/// <b>Zero allocation (AC4).</b> The <see cref="Span{Int32}" /> overloads write into a caller-owned buffer and
/// allocate, box, or dispatch virtually nowhere on the per-row path; an operator sizes one scratch buffer of
/// <c>length</c> ints per batch and reuses it. The <see cref="SelectionVector" />-returning overloads are the
/// convenience (cold) path and do allocate the result array. The filter input is the bitwise <c>AND</c> of a
/// comparison's value and validity bitmaps (a null row is not selected — Spark <c>WHERE</c> drops rows that are not
/// <c>TRUE</c>); produce it with <see cref="BitmapOps.And" /> (#144) and hand the result here.
/// </para>
/// </remarks>
internal static class SelectionKernels
{
    /// <summary>
    /// Writes the logical index <c>i ∈ [0, length)</c> of every <b>set</b> bit in the window
    /// <c>[offset, offset + length)</c> of <paramref name="predicate" /> into <paramref name="dest" />, ascending,
    /// returning the count written. The indices are ordered, unique, and in <c>[0, length)</c> (AC1).
    /// </summary>
    /// <param name="predicate">An Arrow LSB-first bitmap covering at least <c>offset + length</c> bits.</param>
    /// <param name="offset">The first logical bit (may be non-byte-aligned); must be non-negative.</param>
    /// <param name="length">The number of logical rows to scan; must be non-negative.</param>
    /// <param name="dest">Scratch for the selected indices; must hold at least <paramref name="length" /> ints (worst case all-pass).</param>
    /// <param name="tier">Forces a SIMD tier for parity testing; production passes <see cref="KernelTier.Auto" />.</param>
    /// <returns>The number of selected indices written to <paramref name="dest" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset" /> or <paramref name="length" /> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="predicate" /> is shorter than <c>ByteCount(offset + length)</c> or <paramref name="dest" /> is shorter than <paramref name="length" />.</exception>
    public static int ToSelection(ReadOnlySpan<byte> predicate, int offset, int length, Span<int> dest, KernelTier tier = KernelTier.Auto)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
        {
            return 0;
        }

        RequireSpan(predicate.Length, Bitmap.ByteCount(offset + length), "predicate");
        RequireSpan(dest.Length, length, "destination");

        int produced = 0;
        int i = 0;

        if (KernelTierGate.UseVector256(tier) || KernelTierGate.UseVector128(tier))
        {
            // Scalar lead-in to the next source byte boundary so the whole-byte SIMD skip below sees aligned bytes.
            while (i < length && ((offset + i) & 7) != 0)
            {
                if (Bitmap.Get(predicate, offset + i))
                {
                    dest[produced++] = i;
                }

                i++;
            }

            if (i < length)
            {
                int srcByte = (offset + i) >> 3;       // (offset + i) is now a multiple of 8
                int fullBytes = (length - i) >> 3;     // whole bytes wholly inside [0, length)
                ref byte head = ref Unsafe.Add(ref MemoryMarshal.GetReference(predicate), srcByte);
                int b = 0;

                if (KernelTierGate.UseVector256(tier))
                {
                    for (; b <= fullBytes - Vector256<byte>.Count; b += Vector256<byte>.Count)
                    {
                        if (Vector256.LoadUnsafe(ref head, (nuint)b) == Vector256<byte>.Zero)
                        {
                            continue; // all 256 bits clear — emit nothing, skip the chunk
                        }

                        for (int k = 0; k < Vector256<byte>.Count; k++)
                        {
                            EmitRange(Unsafe.Add(ref head, b + k), i + ((b + k) << 3), dest, ref produced);
                        }
                    }
                }

                if (KernelTierGate.UseVector128(tier))
                {
                    for (; b <= fullBytes - Vector128<byte>.Count; b += Vector128<byte>.Count)
                    {
                        if (Vector128.LoadUnsafe(ref head, (nuint)b) == Vector128<byte>.Zero)
                        {
                            continue; // all 128 bits clear — emit nothing, skip the chunk
                        }

                        for (int k = 0; k < Vector128<byte>.Count; k++)
                        {
                            EmitRange(Unsafe.Add(ref head, b + k), i + ((b + k) << 3), dest, ref produced);
                        }
                    }
                }

                for (; b < fullBytes; b++)
                {
                    EmitRange(Unsafe.Add(ref head, b), i + (b << 3), dest, ref produced);
                }

                i += fullBytes << 3;
            }
        }

        // Scalar tail (and, under KernelTier.Scalar, the entire per-bit reference loop over [0, length)).
        for (; i < length; i++)
        {
            if (Bitmap.Get(predicate, offset + i))
            {
                dest[produced++] = i;
            }
        }

        return produced;
    }

    /// <summary>
    /// Builds a <see cref="SelectionVector" /> of the set bits in <c>[offset, offset + length)</c> — the cold-path
    /// convenience over <see cref="ToSelection(ReadOnlySpan{byte}, int, int, Span{int}, KernelTier)" /> (it allocates
    /// the result; use the <see cref="Span{Int32}" /> overload on the hot path).
    /// </summary>
    public static SelectionVector ToSelection(ReadOnlySpan<byte> predicate, int offset, int length, KernelTier tier = KernelTier.Auto)
    {
        if (length == 0)
        {
            return SelectionVector.Range(0);
        }

        var scratch = new int[length];
        int count = ToSelection(predicate, offset, length, scratch, tier);
        return new SelectionVector(scratch.AsSpan(0, count));
    }

    /// <summary>
    /// Emits <c>selection[p]</c> for every set bit <c>p</c> of <paramref name="predicate" /> into
    /// <paramref name="dest" />, in selection order, returning the count written — the selection ∘ predicate
    /// composition (AC2). Bit <c>p</c> answers "did selection position <c>p</c> pass?"; the emitted indices stay in
    /// the original physical row space and preserve the base selection's order and uniqueness.
    /// </summary>
    /// <param name="selection">The existing selection's physical indices (base → original-row-space).</param>
    /// <param name="predicate">An Arrow LSB-first bitmap over selection positions, covering at least <c>selection.Length</c> bits.</param>
    /// <param name="dest">Scratch for the composed indices; must hold at least <c>selection.Length</c> ints (worst case all-pass).</param>
    /// <param name="tier">Forces a SIMD tier for parity testing; production passes <see cref="KernelTier.Auto" />.</param>
    /// <returns>The number of composed indices written to <paramref name="dest" />.</returns>
    /// <exception cref="ArgumentException"><paramref name="predicate" /> is shorter than <c>ByteCount(selection.Length)</c> or <paramref name="dest" /> is shorter than <c>selection.Length</c>.</exception>
    public static int Compose(ReadOnlySpan<int> selection, ReadOnlySpan<byte> predicate, Span<int> dest, KernelTier tier = KernelTier.Auto)
    {
        int count = selection.Length;
        if (count == 0)
        {
            return 0;
        }

        RequireSpan(predicate.Length, Bitmap.ByteCount(count), "predicate");
        RequireSpan(dest.Length, count, "destination");

        int produced = 0;
        int p = 0;

        if (KernelTierGate.UseVector256(tier) || KernelTierGate.UseVector128(tier))
        {
            int fullBytes = count >> 3; // predicate bit p is offset 0, so no lead-in is needed
            ref byte head = ref MemoryMarshal.GetReference(predicate);
            ref int sel = ref MemoryMarshal.GetReference(selection);
            int b = 0;

            if (KernelTierGate.UseVector256(tier))
            {
                for (; b <= fullBytes - Vector256<byte>.Count; b += Vector256<byte>.Count)
                {
                    if (Vector256.LoadUnsafe(ref head, (nuint)b) == Vector256<byte>.Zero)
                    {
                        continue;
                    }

                    for (int k = 0; k < Vector256<byte>.Count; k++)
                    {
                        EmitGather(Unsafe.Add(ref head, b + k), ref sel, (b + k) << 3, dest, ref produced);
                    }
                }
            }

            if (KernelTierGate.UseVector128(tier))
            {
                for (; b <= fullBytes - Vector128<byte>.Count; b += Vector128<byte>.Count)
                {
                    if (Vector128.LoadUnsafe(ref head, (nuint)b) == Vector128<byte>.Zero)
                    {
                        continue;
                    }

                    for (int k = 0; k < Vector128<byte>.Count; k++)
                    {
                        EmitGather(Unsafe.Add(ref head, b + k), ref sel, (b + k) << 3, dest, ref produced);
                    }
                }
            }

            for (; b < fullBytes; b++)
            {
                EmitGather(Unsafe.Add(ref head, b), ref sel, b << 3, dest, ref produced);
            }

            p = fullBytes << 3;
        }

        for (; p < count; p++)
        {
            if (Bitmap.Get(predicate, p))
            {
                dest[produced++] = selection[p];
            }
        }

        return produced;
    }

    /// <summary>
    /// Composes <paramref name="selection" /> with <paramref name="predicate" /> into a new
    /// <see cref="SelectionVector" /> — the cold-path convenience over
    /// <see cref="Compose(ReadOnlySpan{int}, ReadOnlySpan{byte}, Span{int}, KernelTier)" /> (it allocates the result).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="selection" /> is null.</exception>
    public static SelectionVector Compose(SelectionVector selection, ReadOnlySpan<byte> predicate, KernelTier tier = KernelTier.Auto)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Count == 0)
        {
            return SelectionVector.Range(0);
        }

        var scratch = new int[selection.Count];
        int count = Compose(selection.Indices, predicate, scratch, tier);
        return new SelectionVector(scratch.AsSpan(0, count));
    }

    /// <summary>
    /// Appends the logical indices of the set bits of one bitmap <paramref name="bits" /> byte (LSB first ⇒ ascending),
    /// each offset by <paramref name="baseIndex" />. A zero byte appends nothing, which is why the vector zero-skip is
    /// bit-identical to the per-byte scan.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitRange(byte bits, int baseIndex, Span<int> dest, ref int produced)
    {
        uint word = bits;
        while (word != 0)
        {
            int tz = BitOperations.TrailingZeroCount(word);
            dest[produced++] = baseIndex + tz;
            word &= word - 1; // clear the lowest set bit
        }
    }

    /// <summary>
    /// Appends <c>selection[baseIndex + tz]</c> for each set bit <c>tz</c> of one predicate <paramref name="bits" />
    /// byte (LSB first ⇒ selection order) — the composition gather analogue of <see cref="EmitRange" />.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitGather(byte bits, ref int selection, int baseIndex, Span<int> dest, ref int produced)
    {
        uint word = bits;
        while (word != 0)
        {
            int tz = BitOperations.TrailingZeroCount(word);
            dest[produced++] = Unsafe.Add(ref selection, baseIndex + tz);
            word &= word - 1; // clear the lowest set bit
        }
    }

    /// <summary>
    /// Cheap fail-fast precondition for the unchecked-<see cref="Unsafe" /> loops above (mirrors
    /// <see cref="BitmapOps" />'s <c>RequireSpan</c>): a single length comparison off the per-element path, so an
    /// undersized span throws a clear <see cref="ArgumentException" /> instead of reading or writing out of bounds.
    /// </summary>
    private static void RequireSpan(int spanLength, int required, string what)
    {
        if (spanLength < required)
        {
            throw new ArgumentException($"{what} needs at least {required} element(s) but has {spanLength}.");
        }
    }
}
