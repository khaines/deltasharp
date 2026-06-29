namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// A zero-copy, offset-aware view over a column's <b>validity</b> (null state), the single contract
/// scalar, SIMD, Arrow-backed, and future off-heap vectors agree on (STORY-02.6.1). Validity is an
/// Arrow-compatible LSB-first bitmap where a <b>set</b> bit means the row is valid (non-null) and a
/// <b>cleared</b> bit means null; bit <c>i</c> lives in byte <c>i / 8</c> at position <c>i % 8</c>.
/// </summary>
/// <remarks>
/// <para>
/// The validity buffer is <b>optional</b>. An <b>empty</b> <see cref="Bits"/> span means "no validity
/// buffer", which by the Arrow-compatible contract is treated as <b>all-valid</b> — and crucially
/// <b>without allocating a synthetic all-ones bitmap</b> (STORY-02.6.1 AC1). Build one with
/// <see cref="AllValid(int)"/> for that no-null fast path.
/// </para>
/// <para>
/// All indices are <b>logical</b>: row <c>0</c> is the first row of this view. A non-zero
/// <see cref="Offset"/> (created by slicing a parent's buffer) is added to the logical index before
/// the Arrow LSB-first bit lookup, so a slice resolves the same bit the parent would (STORY-02.6.1
/// AC2). It is a <see langword="ref"/> <see langword="struct"/>: a transient, stack-only view that
/// shares the owner's buffer (no copy, no per-row boxing), mirroring <see cref="ReadOnlySpan{T}"/>.
/// </para>
/// </remarks>
public readonly ref struct Validity
{
    private readonly ReadOnlySpan<byte> _bits;
    private readonly int _offset;
    private readonly int _length;

    /// <summary>
    /// Wraps a validity <paramref name="bitmap"/> (Arrow LSB-first; set bit = valid) covering
    /// <paramref name="length"/> logical rows starting at logical bit <paramref name="offset"/>. An
    /// <b>empty</b> <paramref name="bitmap"/> represents the absent (all-valid) buffer.
    /// </summary>
    /// <param name="bitmap">The packed validity buffer, or an empty span for "no buffer / all valid".</param>
    /// <param name="offset">The logical bit offset of row <c>0</c> within <paramref name="bitmap"/>.</param>
    /// <param name="length">The number of logical rows in this view.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="length"/> is negative.</exception>
    /// <exception cref="ArgumentException">A non-empty <paramref name="bitmap"/> does not cover <c>offset + length</c> bits.</exception>
    public Validity(ReadOnlySpan<byte> bitmap, int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!bitmap.IsEmpty && (long)bitmap.Length * 8 < (long)offset + length)
        {
            throw new ArgumentException(
                $"Validity bitmap of {bitmap.Length} byte(s) ({(long)bitmap.Length * 8} bits) does not cover the "
                    + $"logical window [offset {offset}, +{length}).",
                nameof(bitmap));
        }

        _bits = bitmap;
        _offset = offset;
        _length = length;
    }

    /// <summary>
    /// An all-valid view over <paramref name="length"/> rows that owns <b>no</b> bitmap — the
    /// no-null, no-allocation path (STORY-02.6.1 AC1).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public static Validity AllValid(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new Validity(default, 0, length);
    }

    /// <summary>The number of logical rows this view describes.</summary>
    public int Length => _length;

    /// <summary>The logical bit offset of row <c>0</c> within <see cref="Bits"/> (non-zero for a slice).</summary>
    public int Offset => _offset;

    /// <summary>
    /// The raw Arrow LSB-first validity buffer this view reads, or an <b>empty</b> span when there
    /// is no buffer (all-valid). Combine with <see cref="Offset"/> for direct bit access; SIMD/block
    /// kernels (STORY-02.6.2) consume this.
    /// </summary>
    public ReadOnlySpan<byte> Bits => _bits;

    /// <summary>
    /// Whether a validity buffer is present. <see langword="false"/> is the all-valid fast path:
    /// no row is null and no bitmap was materialized. (A present buffer <i>may</i> still be all-ones.)
    /// </summary>
    public bool HasBitmap => !_bits.IsEmpty;

    /// <summary>Whether the logical row at <paramref name="index"/> is valid (non-null).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
    public bool IsValid(int index)
    {
        if ((uint)index >= (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be in [0, {_length}).");
        }

        // No buffer => all valid (no synthetic bitmap). Otherwise Arrow LSB-first lookup at the
        // logical bit (offset + index).
        return _bits.IsEmpty || Bitmap.Get(_bits, _offset + index);
    }

    /// <summary>Whether the logical row at <paramref name="index"/> is null.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
    public bool IsNull(int index) => !IsValid(index);

    /// <summary>
    /// The number of null rows in <c>[0, Length)</c>. The absent-buffer (all-valid) case is
    /// <c>0</c> without touching any byte; otherwise it counts cleared bits in the offset window
    /// (STORY-02.6.1 AC4). Deterministic — a pure function of the bits, offset, and length.
    /// </summary>
    public int CountNulls() => _bits.IsEmpty ? 0 : Bitmap.CountNulls(_bits, _offset, _length);

    /// <summary>The number of valid (non-null) rows in <c>[0, Length)</c>.</summary>
    public int CountValid() => _length - CountNulls();

    /// <summary>
    /// A logical sub-range view <c>[offset, offset + length)</c> over the <b>same</b> buffer — no
    /// bits are copied. The child accumulates the logical bit <see cref="Offset"/>, so an absent
    /// buffer stays absent (all-valid) and a present buffer resolves the parent's bits (AC2).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside <c>[0, Length]</c>.</exception>
    public Validity Slice(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > _length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), length, $"Slice [{offset}, {offset + length}) exceeds length {_length}.");
        }

        return new Validity(_bits, _offset + offset, length);
    }
}
