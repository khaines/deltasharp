using DeltaSharp.Storage.Delta.DeletionVectors;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta.DeletionVectors;

/// <summary>
/// Byte-exact round-trip and fail-closed tests for the 64-bit portable <see cref="RoaringBitmapArray"/>
/// serialization (Delta protocol "Deletion Vector Format" — the same format Spark/Databricks read and
/// write). Correctness here is interop-critical: a bit-off container header or endianness bug would either
/// exclude the WRONG rows or fail to read a real Spark <c>.bin</c>. The deserializer is a hardened,
/// bounded, fail-closed decode surface (design §2.14): a bad magic, an oversized bucket/cardinality, or an
/// out-of-range row position must throw a typed <see cref="DeltaStorageException"/>, never materialize an
/// attacker-sized bitmap or return an out-of-file position.
/// </summary>
public sealed class RoaringBitmapArrayTests
{
    public static IEnumerable<object[]> PositionSets()
    {
        yield return new object[] { Array.Empty<long>() };
        yield return new object[] { new long[] { 0 } };
        yield return new object[] { new long[] { 3, 4, 7, 11, 18, 29 } };
        yield return new object[] { new long[] { 0, 1, 2, 65535, 65536, 65537 } };
        // Dense low container (> 4096 entries ⇒ a bitset container, not an array container).
        yield return new object[] { LongRange(0, 5000) };
        // Two high-key buckets (positions above 2^32 exercise the multi-bucket path).
        yield return new object[] { new long[] { 5, 100, 1L << 32, (1L << 32) + 7, 2L << 32 } };
    }

    [Theory]
    [MemberData(nameof(PositionSets))]
    public void RoundTrip_ReconstructsExactPositions(long[] positions)
    {
        byte[] serialized = RoaringBitmapArray.Serialize(positions);
        long numRecords = positions.Length == 0 ? 1 : positions[^1] + 1;
        long[] decoded = RoaringBitmapArray.Deserialize(serialized, numRecords, positions.LongLength);

        Assert.Equal(positions, decoded);
    }

    [Fact]
    public void Serialize_MatchesSparkNativeBytes_ForProtocolExample3()
    {
        // Writer interop oracle: serializing the protocol inline Example 3 row set MUST reproduce the exact
        // 40-byte native RoaringBitmapArray payload that Delta/Spark embeds in that example (the Base85-decoded
        // bytes of "wi5b=000010000siXQKl0rr91000f55c8Xg0@@D72lkbi5=-{L"). This proves our writer is
        // byte-identical to Spark's, not merely round-trippable through our own reader. Layout: big-endian
        // framing (magic 6439D3D0, numberOfBitmaps 00000001, bitmapSize 0000001C) + little-endian 32-bit
        // roaring (cookie 303A NO_RUN, size 1, key/card 0000 0005, offset 00000010, values 3,4,7,11,18,29).
        byte[] serialized = RoaringBitmapArray.Serialize(new long[] { 3, 4, 7, 11, 18, 29 });
        string hex = Convert.ToHexString(serialized).ToLowerInvariant();
        Assert.Equal(
            "6439d3d0000000010000001c3a3000000100000000000500100000000300040007000b0012001d00",
            hex);
    }

    [Fact]
    public void Deserialize_BadMagic_FailsClosed()
    {
        byte[] serialized = RoaringBitmapArray.Serialize(new long[] { 1, 2, 3 });
        serialized[0] ^= 0xFF; // corrupt the big-endian magic number

        Assert.Throws<DeltaStorageException>(
            () => RoaringBitmapArray.Deserialize(serialized, numRecords: 100, expectedCardinality: 3));
    }

    [Fact]
    public void Deserialize_CardinalityMismatch_FailsClosed()
    {
        byte[] serialized = RoaringBitmapArray.Serialize(new long[] { 1, 2, 3 });

        // Declares 5 removed rows but the bitmap holds 3 — a corrupt descriptor; fail closed.
        Assert.Throws<DeltaStorageException>(
            () => RoaringBitmapArray.Deserialize(serialized, numRecords: 100, expectedCardinality: 5));
    }

    [Fact]
    public void Deserialize_PositionAtOrAboveNumRecords_FailsClosed()
    {
        // A DV that claims to delete row 50 in a file with only 10 records is an integrity violation —
        // returning it would exclude a non-existent position or (worse) mask a mapping bug. Fail closed.
        byte[] serialized = RoaringBitmapArray.Serialize(new long[] { 3, 50 });

        Assert.Throws<DeltaStorageException>(
            () => RoaringBitmapArray.Deserialize(serialized, numRecords: 10, expectedCardinality: 2));
    }

    [Fact]
    public void Deserialize_Truncated_FailsClosed()
    {
        byte[] serialized = RoaringBitmapArray.Serialize(new long[] { 1, 2, 3, 4, 5 });
        byte[] truncated = serialized.AsSpan(0, serialized.Length - 3).ToArray();

        Assert.Throws<DeltaStorageException>(
            () => RoaringBitmapArray.Deserialize(truncated, numRecords: 100, expectedCardinality: 5));
    }

    private static long[] LongRange(long start, int count)
    {
        long[] values = new long[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = start + i;
        }

        return values;
    }
}
