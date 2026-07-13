using System.Text;
using DeltaSharp.Storage.Delta.DeletionVectors;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta.DeletionVectors;

/// <summary>
/// Byte-exact unit tests for the <see cref="Z85"/> Base85 codec (Delta protocol "Deletion Vector Format" /
/// ZeroMQ RFC 32). The codec must interop with Spark/Databricks: it encodes on-disk-format DV bytes for an
/// inline (<c>'i'</c>) descriptor and the 16-byte UUID for a relative-path (<c>'u'</c>) descriptor. The
/// decoder fails <b>closed</b> on any malformed input (invalid character, bad length) rather than returning
/// wrong bytes — a DV that fails to decode must fail the read, never silently drop the deletion.
/// </summary>
public sealed class Z85Tests
{
    [Fact]
    public void RoundTrip_ArbitraryBytes_ReconstructsExactly()
    {
        for (int length = 0; length <= 64; length++)
        {
            byte[] original = new byte[length];
            for (int i = 0; i < length; i++)
            {
                original[i] = (byte)((i * 31) + 7);
            }

            string encoded = Z85.Encode(original);
            byte[] decoded = Z85.DecodeBytes(encoded, length);
            Assert.Equal(original, decoded);
        }
    }

    [Fact]
    public void Encode_BigEndianBlockPacking_MatchesDeltaByteOrder()
    {
        // Delta's Base85 is the standard ZeroMQ Z85 codec: each 4-byte block is a BIG-endian uint32. The
        // block "wi5b=" (the leading block of the protocol's inline Example 3) decodes to the big-endian
        // bytes {0x64,0x39,0xD3,0xD0} = 0x6439D3D0 = the native RoaringBitmapArray magic 1681511376 (read
        // big-endian in the framing). This is the byte-order oracle for Delta's Base85 layer; the mixed
        // endianness inside an inline DV comes from the RoaringBitmapArray framing (big-endian) wrapping a
        // little-endian roaring container, not from this codec.
        byte[] magicBigEndian = { 0x64, 0x39, 0xD3, 0xD0 };
        Assert.Equal("wi5b=", Z85.Encode(magicBigEndian));
        Assert.Equal(magicBigEndian, Z85.DecodeBytes("wi5b=", 4));
    }

    [Fact]
    public void DecodeBytes_InvalidCharacter_FailsClosed()
    {
        // A backtick '`' is not in the Z85 alphabet.
        Assert.Throws<DeltaStorageException>(() => Z85.DecodeBytes("Hello`orld", 8));
    }

    [Fact]
    public void DecodeBytes_LengthNotMultipleOfFive_FailsClosed()
    {
        Assert.Throws<DeltaStorageException>(() => Z85.DecodeBytes("Hell", 3));
    }

    [Fact]
    public void DecodeBytes_OutputLengthLongerThanDecoded_FailsClosed()
    {
        // "HelloWorld" decodes to 8 bytes; asking for 9 must fail closed, never over-read.
        Assert.Throws<DeltaStorageException>(() => Z85.DecodeBytes("HelloWorld", 9));
    }

    [Fact]
    public void EncodeUuid_RoundTrips_And_Is20Characters()
    {
        var uuid = new Guid("01234567-89ab-cdef-0123-456789abcdef");
        string encoded = Z85.EncodeUuid(uuid);
        Assert.Equal(Z85.EncodedUuidLength, encoded.Length);
        Assert.Equal(uuid, Z85.DecodeUuid(encoded));
    }

    [Fact]
    public void DecodeUuid_WrongLength_FailsClosed()
    {
        Assert.Throws<DeltaStorageException>(() => Z85.DecodeUuid("tooShort"));
    }
}
