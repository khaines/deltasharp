namespace DeltaSharp.Engine.Execution;

/// <summary>
/// A hash-table key wrapping a row's canonical byte-sortable key encoding (see
/// <see cref="RowKeyProjection"/>). Equality is a bytewise compare of the encodings and the hash is
/// FNV-1a over those bytes, so two rows collide in a group/build bucket <b>iff</b> Spark would treat
/// their keys as equal — including the float normalization the encoding bakes in (<c>NaN</c> shares
/// one bit pattern, <c>-0.0</c> folds to <c>+0.0</c>; Spark SPARK-26021). NULLs are encoded with a
/// distinct present/absent marker, so a null key never equals a present key; join callers additionally
/// drop null keys entirely (SQL equi-join <c>null ≠ null</c>), grouping/exchange callers keep them.
/// </summary>
internal readonly struct RowKey : IEquatable<RowKey>
{
    private readonly byte[] _bytes;

    /// <summary>Wraps an already-encoded canonical key. The array is retained, not copied.</summary>
    internal RowKey(byte[] bytes) => _bytes = bytes;

    /// <summary>The canonical key bytes.</summary>
    internal ReadOnlySpan<byte> Bytes => _bytes;

    /// <inheritdoc />
    public bool Equals(RowKey other) => _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RowKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => unchecked((int)Fnv1a(_bytes));

    /// <summary>
    /// FNV-1a (32-bit) over <paramref name="bytes"/>. Used both for the dictionary hash and for the
    /// local-exchange partition assignment. This is intentionally <i>not</i> Spark's Murmur3 hash —
    /// the local exchange only needs a deterministic, well-spread assignment, and the seam that becomes
    /// a network shuffle (STORY-03.5.x) is free to adopt Murmur3 there without changing these operators.
    /// </summary>
    internal static uint Fnv1a(ReadOnlySpan<byte> bytes)
    {
        uint hash = 2166136261u;
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash = unchecked(hash * 16777619u);
        }

        return hash;
    }
}
