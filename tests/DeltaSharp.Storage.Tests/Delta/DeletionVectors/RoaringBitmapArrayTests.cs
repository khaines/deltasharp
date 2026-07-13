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
    public void Serialize_MatchesRealSparkGoldenPayload_ForPositionZero()
    {
        // Byte-identical interop oracle (verified against the REAL Spark golden
        // deletion_vector_10ffbe3a-…​.bin, whose 34-byte RoaringBitmapArray payload deletes exactly {0}).
        // Reproducing these exact bytes proves a Spark/Databricks reader can consume this build's output.
        // Layout: portable magic 0x6439D3D1 (LE d1 d3 39 64), 8-byte LE numBuckets=1, 4-byte LE key=0, then a
        // little-endian 32-bit roaring (cookie 303A NO_RUN, size 1, key/card 0000/0000, offset 00000010,
        // value 0000).
        byte[] serialized = RoaringBitmapArray.Serialize(new long[] { 0 });
        Assert.Equal(
            "d1d339640100000000000000000000003a3000000100000000000000100000000000",
            Convert.ToHexString(serialized).ToLowerInvariant());
    }

    [Fact]
    public void Deserialize_RealSparkGoldenPayload_DecodesPositionZero()
    {
        // The same 34-byte real Spark payload, decoded through the production path → deletes exactly {0}
        // (cardinality 1) in a 3-row physical file (the golden's data file has numRecords=3).
        byte[] payload = Convert.FromHexString(
            "d1d339640100000000000000000000003a3000000100000000000000100000000000");
        long[] decoded = RoaringBitmapArray.Deserialize(payload, numRecords: 3, expectedCardinality: 1);
        Assert.Equal(new long[] { 0 }, decoded);
    }

    [Fact]
    public void Serialize_MatchesPortableBytes_ForProtocolExample3RowSet()
    {
        // The protocol inline "Example 3" row set {3,4,7,11,18,29}, serialized in this build's ONE format —
        // the portable little-endian RoaringBitmapArray. (The protocol's inline example illustrates the
        // DEPRECATED native/big-endian serializer, which real Spark does not persist and which this build
        // neither reads nor writes; this pins our portable output byte-for-byte.)
        byte[] serialized = RoaringBitmapArray.Serialize(new long[] { 3, 4, 7, 11, 18, 29 });
        Assert.Equal(
            "d1d339640100000000000000000000003a3000000100000000000500100000000300040007000b0012001d00",
            Convert.ToHexString(serialized).ToLowerInvariant());
    }

    [Fact]
    public void RoundTrip_MultiBucketMultiContainer_IsByteStableAndDecodes()
    {
        // A real-ish multi-container / multi-bucket case: {0,5} and 999999 live in the SAME high-32 bucket
        // (key 0) but DIFFERENT 16-bit containers (0 and 15), while 2^32+10 opens a SECOND bucket (key 1).
        // Assert both byte-stability (the exact portable-LE layout: numBuckets=2, two per-bucket keys) and an
        // exact round-trip decode.
        long[] positions = { 0, 5, 999999, (1L << 32) + 10 };
        byte[] serialized = RoaringBitmapArray.Serialize(positions);
        Assert.Equal(
            "d1d339640200000000000000000000003a30000002000000000001000f000000180000001c00000000000"
            + "5003f42010000003a3000000100000000000000100000000a00",
            Convert.ToHexString(serialized).ToLowerInvariant());

        long[] decoded = RoaringBitmapArray.Deserialize(
            serialized, numRecords: (1L << 32) + 11, expectedCardinality: positions.Length);
        Assert.Equal(positions, decoded);
    }

    [Fact]
    public void Deserialize_BadMagic_FailsClosed()
    {
        byte[] serialized = RoaringBitmapArray.Serialize(new long[] { 1, 2, 3 });
        serialized[0] ^= 0xFF; // corrupt the low byte of the little-endian portable magic number

        var ex = Assert.Throws<DeltaStorageException>(
            () => RoaringBitmapArray.Deserialize(serialized, numRecords: 100, expectedCardinality: 3));
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
    }

    [Fact]
    public void Deserialize_NegativeCardinality_FailsClosedTyped()
    {
        // A descriptor-supplied negative cardinality is untrusted input and must fail closed as a TYPED
        // storage fault (never an untyped ArgumentOutOfRangeException leaking from the decode edge).
        byte[] serialized = RoaringBitmapArray.Serialize(new long[] { 1 });
        var ex = Assert.Throws<DeltaStorageException>(
            () => RoaringBitmapArray.Deserialize(serialized, numRecords: 100, expectedCardinality: -1));
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
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
