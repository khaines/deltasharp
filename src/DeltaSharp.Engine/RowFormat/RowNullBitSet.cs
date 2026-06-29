namespace DeltaSharp.Engine.RowFormat;

/// <summary>
/// Word-aligned null-bit operations for the binary row format (an <c>UnsafeRow</c> analog,
/// ADR-0008). A field's <i>null</i> bit lives in 8-byte words at the start of a row, array, or
/// map block: bit <c>i</c> is in byte <c>i/8</c> at position <c>i%8</c>, and a <b>set</b> bit
/// means the value is <b>null</b> (the inverse of the columnar validity convention).
/// </summary>
/// <remarks>
/// The bitset is rounded up to a whole 8-byte word so the fixed region that follows it begins on
/// an 8-byte boundary — the alignment invariant the encoder relies on. Kept <c>internal</c> and
/// exercised through the friend-assembly test-access policy.
/// </remarks>
internal static class RowNullBitSet
{
    /// <summary>The number of 8-byte words needed to hold <paramref name="fieldCount"/> null bits.</summary>
    public static int WordCount(int fieldCount) => (fieldCount + 63) >> 6;

    /// <summary>The byte size of the null bitset for <paramref name="fieldCount"/> fields (a multiple of 8).</summary>
    public static int ByteSize(int fieldCount) => WordCount(fieldCount) << 3;

    /// <summary>Reads the null bit for field <paramref name="index"/> (true = null).</summary>
    public static bool IsNull(ReadOnlySpan<byte> bitset, int index) =>
        (bitset[index >> 3] & (1 << (index & 7))) != 0;

    /// <summary>Sets the null bit for field <paramref name="index"/>.</summary>
    public static void SetNull(Span<byte> bitset, int index) =>
        bitset[index >> 3] |= (byte)(1 << (index & 7));
}
