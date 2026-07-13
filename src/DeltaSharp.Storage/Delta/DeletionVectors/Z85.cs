namespace DeltaSharp.Storage.Delta.DeletionVectors;

/// <summary>
/// The <a href="https://rfc.zeromq.org/spec/32/">Z85</a> Base85 variant the Delta protocol uses to embed
/// a serialized <see cref="RoaringBitmapArray"/> inline in the log (<c>storageType='i'</c>) and to encode
/// the UUID of a relative-path DV file (<c>storageType='u'</c>). Z85 is JSON-friendly (its alphabet
/// contains no <c>"</c> or <c>\</c>), so it round-trips through the NDJSON commit without escaping.
///
/// <para><b>Byte-exact interop.</b> The alphabet and 4-byte→5-char block algorithm match Delta's
/// <c>Base85Codec</c> (kernel/spark) exactly: each big-endian <c>uint32</c> block is expanded into five
/// base-85 digits, unaligned input is zero-padded to a 4-byte boundary before encoding, and
/// <see cref="DecodeBytes"/> trims to the caller's declared length (dropping any encode-time padding). This
/// is a pure, reflection-free, allocation-bounded codec (NativeAOT-safe, ADR-0014).</para>
///
/// <para><b>Fail-closed decode surface (design §2.14).</b> The encoded string comes from a poisoned table's
/// log. A length that is not 5-char aligned, a non-ASCII character, a character outside the Z85 alphabet, or
/// a declared output length longer than the decoded block all throw a typed
/// <see cref="DeltaStorageException"/> (kind <see cref="StorageErrorKind.CorruptData"/>) — never a wrong,
/// silently-truncated byte set (which would translate to the wrong deleted-row set, the cardinal DV safety
/// violation).</para>
/// </summary>
internal static class Z85
{
    private const long Base = 85L;
    private const long Base2 = 85L * 85L;
    private const long Base3 = 85L * 85L * 85L;
    private const long Base4 = 85L * 85L * 85L * 85L;

    /// <summary>The fixed Z85 encoded length of a 16-byte UUID (four blocks × five chars).</summary>
    public const int EncodedUuidLength = 20;

    // The Z85 alphabet in code-point order: 0-9, a-z, A-Z, then the 23 symbol characters. Exactly 85 entries.
    private static readonly char[] EncodeMap = BuildEncodeMap();

    // ASCII → base-85 digit (0..84), or -1 for any character not in the alphabet.
    private static readonly sbyte[] DecodeMap = BuildDecodeMap();

    /// <summary>Encodes <paramref name="input"/> to Z85, zero-padding to a 4-byte boundary first (Delta's
    /// <c>encodeBytes</c>). The output length is always a multiple of five.</summary>
    public static string Encode(ReadOnlySpan<byte> input)
    {
        int aligned = (input.Length + 3) & ~3; // round up to a multiple of 4
        int blocks = aligned / 4;
        var output = new char[blocks * 5];
        Span<byte> block = stackalloc byte[4];
        int outIndex = 0;
        for (int b = 0; b < blocks; b++)
        {
            block.Clear();
            int start = b * 4;
            int copy = Math.Min(4, input.Length - start);
            if (copy > 0)
            {
                input.Slice(start, copy).CopyTo(block);
            }

            // Big-endian uint32 for this block. Delta's deletion-vector Base85 is the ZeroMQ Z85 codec
            // (RFC 32): a plain (big-endian) uint32 per 4-byte block. The mixed-endianness people notice in
            // an inline DV comes from the SERIALIZED RoaringBitmapArray, not the Base85 layer — its native
            // framing (magic/numberOfBitmaps/bitmapSize) is written big-endian (Java ByteBuffer default)
            // while the roaring container bytes are little-endian; both survive this big-endian block codec.
            // Verified against the protocol's inline Example 3 → {3,4,7,11,18,29}.
            long sum = ((long)block[0] << 24) | ((long)block[1] << 16) | ((long)block[2] << 8) | block[3];
            output[outIndex++] = EncodeMap[(int)(sum / Base4)];
            sum %= Base4;
            output[outIndex++] = EncodeMap[(int)(sum / Base3)];
            sum %= Base3;
            output[outIndex++] = EncodeMap[(int)(sum / Base2)];
            sum %= Base2;
            output[outIndex++] = EncodeMap[(int)(sum / Base)];
            output[outIndex++] = EncodeMap[(int)(sum % Base)];
        }

        return new string(output);
    }

    /// <summary>Decodes <paramref name="encoded"/> and returns exactly <paramref name="outputLength"/> bytes,
    /// dropping any 4-byte-alignment padding the encoder added (Delta's <c>decodeBytes</c>).</summary>
    /// <exception cref="DeltaStorageException">The input is not 5-char aligned, contains an invalid
    /// character, or is too short to yield <paramref name="outputLength"/> bytes (fail closed).</exception>
    public static byte[] DecodeBytes(string encoded, int outputLength)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentOutOfRangeException.ThrowIfNegative(outputLength);
        byte[] decoded = DecodeBlocks(encoded);
        if (outputLength > decoded.Length)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector's inline Z85 payload decodes to fewer bytes than its declared sizeInBytes; the log is corrupt.");
        }

        if (outputLength == decoded.Length)
        {
            return decoded;
        }

        var trimmed = new byte[outputLength];
        Array.Copy(decoded, trimmed, outputLength);
        return trimmed;
    }

    /// <summary>Decodes a 20-character Z85 string into the 16 canonical bytes of a UUID (big-endian, the
    /// <c>uuidToByteBuffer</c> order Delta uses for a relative-path DV file name).</summary>
    /// <exception cref="DeltaStorageException">The input is not a valid 20-character Z85 UUID (fail closed).</exception>
    public static Guid DecodeUuid(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length != EncodedUuidLength)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector descriptor's relative-path UUID is not a 20-character Z85 string; the log is corrupt.");
        }

        byte[] bytes = DecodeBlocks(encoded); // 16 bytes
        return GuidFromBigEndian(bytes);
    }

    /// <summary>Encodes the 16 big-endian bytes of <paramref name="id"/> as a 20-character Z85 string
    /// (the inverse of <see cref="DecodeUuid"/>).</summary>
    public static string EncodeUuid(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        WriteBigEndian(id, bytes);
        return Encode(bytes);
    }

    private static byte[] DecodeBlocks(string encoded)
    {
        if (encoded.Length % 5 != 0)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector's Z85 payload length is not a multiple of five; the log is corrupt.");
        }

        int blocks = encoded.Length / 5;
        var output = new byte[blocks * 4];
        int outIndex = 0;
        for (int b = 0; b < blocks; b++)
        {
            long sum =
                (Digit(encoded, b * 5) * Base4)
                + (Digit(encoded, (b * 5) + 1) * Base3)
                + (Digit(encoded, (b * 5) + 2) * Base2)
                + (Digit(encoded, (b * 5) + 3) * Base)
                + Digit(encoded, (b * 5) + 4);

            // A well-formed 5-char block never exceeds uint32.MaxValue; a value that overflows is a
            // corrupt/over-large block and must fail closed rather than wrap.
            if (sum > uint.MaxValue)
            {
                throw DeltaStorageException.CorruptData(
                    "A Delta deletion vector's Z85 block encodes a value larger than 2^32-1; the log is corrupt.");
            }

            // Big-endian uint32 for this block (ZeroMQ Z85; see Encode). The RoaringBitmapArray's own
            // mixed endianness is a property of its serialized bytes, not of this Base85 layer.
            output[outIndex++] = (byte)((sum >> 24) & 0xFF);
            output[outIndex++] = (byte)((sum >> 16) & 0xFF);
            output[outIndex++] = (byte)((sum >> 8) & 0xFF);
            output[outIndex++] = (byte)(sum & 0xFF);
        }

        return output;
    }

    private static long Digit(string encoded, int index)
    {
        char c = encoded[index];
        if (c > 127)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector's Z85 payload contains a non-ASCII character; the log is corrupt.");
        }

        sbyte digit = DecodeMap[c];
        if (digit < 0)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector's Z85 payload contains a character outside the Z85 alphabet; the log is corrupt.");
        }

        return digit;
    }

    private static Guid GuidFromBigEndian(ReadOnlySpan<byte> bytes)
    {
        // Delta encodes a UUID as msb(8) then lsb(8), each big-endian. Guid's big-endian constructor
        // consumes the 16 bytes in that exact canonical order.
        return new Guid(bytes[..16], bigEndian: true);
    }

    private static void WriteBigEndian(Guid id, Span<byte> destination)
    {
        bool ok = id.TryWriteBytes(destination, bigEndian: true, out int written);
        System.Diagnostics.Debug.Assert(ok && written == 16, "A Guid always serializes to 16 big-endian bytes.");
    }

    private static char[] BuildEncodeMap()
    {
        var map = new char[85];
        int i = 0;
        for (char c = '0'; c <= '9'; c++)
        {
            map[i++] = c;
        }

        for (char c = 'a'; c <= 'z'; c++)
        {
            map[i++] = c;
        }

        for (char c = 'A'; c <= 'Z'; c++)
        {
            map[i++] = c;
        }

        foreach (char c in ".-:+=^!/*?&<>()[]{}@%$#")
        {
            map[i++] = c;
        }

        System.Diagnostics.Debug.Assert(i == 85, "The Z85 alphabet has exactly 85 characters.");
        return map;
    }

    private static sbyte[] BuildDecodeMap()
    {
        var map = new sbyte[128];
        Array.Fill(map, (sbyte)-1);
        char[] encodeMap = BuildEncodeMap();
        for (int i = 0; i < encodeMap.Length; i++)
        {
            map[encodeMap[i]] = (sbyte)i;
        }

        return map;
    }
}
