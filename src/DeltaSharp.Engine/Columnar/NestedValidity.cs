namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// Shared buffer helpers for the nested reference vectors (<see cref="StructColumnVector"/>,
/// <see cref="ListColumnVector"/>, <see cref="MapColumnVector"/>). Kept <c>internal</c>: nested
/// vectors own their own public contract, and these merely centralize the top-level validity and
/// offsets construction so the three implementations agree bit-for-bit.
/// </summary>
internal static class NestedValidity
{
    /// <summary>
    /// Builds a packed, Arrow LSB-first validity bitmap (a <b>set</b> bit means the row is valid /
    /// non-null) covering <paramref name="length"/> rows from the per-row null flags in
    /// <paramref name="nulls"/> (<c>nulls[i] == true</c> marks row <c>i</c> null). An empty
    /// <paramref name="nulls"/> span yields an all-valid bitmap. Returns the buffer and the null count.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="nulls"/> is non-empty but not exactly <paramref name="length"/> long.</exception>
    public static (byte[] Validity, int NullCount) Build(ReadOnlySpan<bool> nulls, int length)
    {
        if (!nulls.IsEmpty && nulls.Length != length)
        {
            throw new ArgumentException(
                $"Null flags describe {nulls.Length} row(s) but the vector has {length} row(s).", nameof(nulls));
        }

        var validity = new byte[Bitmap.ByteCount(length)];

        // Set every row valid first, then clear the null rows: cheaper to reason about than seeding
        // from zero, and the trailing (unused) bits stay clear.
        for (int i = 0; i < length; i++)
        {
            Bitmap.Set(validity, i, true);
        }

        int nullCount = 0;
        for (int i = 0; i < nulls.Length; i++)
        {
            if (nulls[i])
            {
                Bitmap.Set(validity, i, false);
                nullCount++;
            }
        }

        return (validity, nullCount);
    }

    /// <summary>
    /// Validates and copies an <paramref name="offsets"/> buffer of length <c>rows + 1</c> for a
    /// list/map child of <paramref name="childLength"/> elements: it must be non-negative,
    /// monotonically non-decreasing, and stay within the child. Returns a private copy so the vector
    /// owns its offsets.
    /// </summary>
    /// <param name="offsets">The offsets buffer (length <c>rows + 1</c>).</param>
    /// <param name="childLength">The number of elements in the flattened child.</param>
    /// <param name="paramName">The caller's parameter name, for exception reporting.</param>
    /// <exception cref="ArgumentException"><paramref name="offsets"/> is empty, non-monotonic, negative, or exceeds the child.</exception>
    public static int[] CopyValidatedOffsets(ReadOnlySpan<int> offsets, int childLength, string paramName)
    {
        if (offsets.IsEmpty)
        {
            throw new ArgumentException("Offsets must contain at least one element (rows + 1).", paramName);
        }

        if (offsets[0] < 0)
        {
            throw new ArgumentException($"Offsets[0] is {offsets[0]}; offsets must be non-negative.", paramName);
        }

        for (int i = 1; i < offsets.Length; i++)
        {
            if (offsets[i] < offsets[i - 1])
            {
                throw new ArgumentException(
                    $"Offsets must be monotonically non-decreasing but offsets[{i}] ({offsets[i]}) < "
                    + $"offsets[{i - 1}] ({offsets[i - 1]}).", paramName);
            }
        }

        int last = offsets[^1];
        if (last > childLength)
        {
            throw new ArgumentException(
                $"Offsets end at {last} but the child has only {childLength} element(s).", paramName);
        }

        return offsets.ToArray();
    }
}
