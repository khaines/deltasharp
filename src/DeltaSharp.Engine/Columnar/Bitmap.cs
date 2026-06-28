namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// Validity-bitmap bit operations using Arrow-compatible LSB-first ordering (bit <c>i</c> lives
/// in byte <c>i/8</c> at position <c>i%8</c>; a <b>set</b> bit means the row is valid/non-null).
/// Kept <c>internal</c> and unit-tested through the friend-assembly test-access policy
/// (<c>docs/engineering/design/testing-conventions.md</c>). Branchless/SIMD null helpers are a
/// later concern (FEAT-02.6).
/// </summary>
internal static class Bitmap
{
    /// <summary>The number of bytes needed to hold <paramref name="bitCount"/> bits.</summary>
    public static int ByteCount(int bitCount) => (bitCount + 7) >> 3;

    /// <summary>Reads the bit at <paramref name="index"/> (true = valid/non-null).</summary>
    public static bool Get(ReadOnlySpan<byte> bitmap, int index) =>
        (bitmap[index >> 3] & (1 << (index & 7))) != 0;

    /// <summary>Sets the bit at <paramref name="index"/> to <paramref name="value"/>.</summary>
    public static void Set(Span<byte> bitmap, int index, bool value)
    {
        int byteIndex = index >> 3;
        int mask = 1 << (index & 7);
        if (value)
        {
            bitmap[byteIndex] |= (byte)mask;
        }
        else
        {
            bitmap[byteIndex] &= (byte)~mask;
        }
    }

    /// <summary>Counts cleared (null) bits in the window <c>[offset, offset + length)</c>.</summary>
    public static int CountNulls(ReadOnlySpan<byte> bitmap, int offset, int length)
    {
        int nulls = 0;
        for (int i = 0; i < length; i++)
        {
            if (!Get(bitmap, offset + i))
            {
                nulls++;
            }
        }

        return nulls;
    }
}
