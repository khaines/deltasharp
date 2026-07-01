using System.Buffers.Binary;
using DeltaSharp.Engine.Memory;
using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.RowFormat;

/// <summary>
/// STORY-02.4.2 AC4: malformed or truncated row bytes fail deserialization with a bounded, typed
/// <see cref="RowValidationException"/> and never read outside the buffer. Every check is exercised
/// both end-to-end through <see cref="RowSpillSerializer.ReadFrame"/> and directly against
/// <see cref="BinaryRowValidator"/>, including exhaustive truncation and pseudo-random fuzzing that
/// must only ever throw <see cref="RowValidationException"/>.
/// </summary>
public class RowFrameValidationTests
{
    private static readonly StructType IntString = new(
        [new StructField("a", IntegerType.Instance), new StructField("s", StringType.Instance)]);

    private static readonly StructType IntIntMap = new(
        [new StructField("m", new MapType(IntegerType.Instance, IntegerType.Instance))]);

    // Inner struct reused by the nested fuzz schema below.
    private static readonly StructType NestedInner = new(
        [new StructField("x", IntegerType.Instance), new StructField("s", StringType.Instance)]);

    // A schema that exercises every nested validator branch — map (key/value pairing + non-null
    // keys), array (with nested nulls), and a nested struct — so the fuzz reaches the map invariants
    // a flat int/string schema never touches.
    private static readonly StructType NestedMapArrayStruct = new(
    [
        new StructField("m", new MapType(StringType.Instance, IntegerType.Instance)),
        new StructField("a", new ArrayType(LongType.Instance)),
        new StructField("nested", NestedInner),
    ]);

    private static byte[] ValidFrame(out int payloadStart)
    {
        var encoder = new BinaryRowEncoder(new NativeMemoryAllocator(scratchThreshold: 0));
        using BinaryRow row = encoder.Encode(new RowData(IntString, 42, "payload"));
        payloadStart = RowSpillSerializer.HeaderSize;
        return RowSpillSerializer.WriteFrame(row);
    }

    [Fact]
    public void TruncatedHeader_Throws()
    {
        byte[] frame = ValidFrame(out _);
        var ex = Assert.Throws<RowValidationException>(
            () => RowSpillSerializer.ReadFrame(frame.AsSpan(0, RowSpillSerializer.HeaderSize - 1), IntString, out _));
        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BadMagic_Throws()
    {
        byte[] frame = ValidFrame(out _);
        frame[0] ^= 0xFF;
        Assert.Throws<RowValidationException>(() => RowSpillSerializer.ReadFrame(frame, IntString, out _));
    }

    [Fact]
    public void UnsupportedFormatVersion_Throws()
    {
        byte[] frame = ValidFrame(out _);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), 999);
        Assert.Throws<RowValidationException>(() => RowSpillSerializer.ReadFrame(frame, IntString, out _));
    }

    [Fact]
    public void NegativePayloadLength_Throws()
    {
        byte[] frame = ValidFrame(out _);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), -1);
        Assert.Throws<RowValidationException>(() => RowSpillSerializer.ReadFrame(frame, IntString, out _));
    }

    [Fact]
    public void PayloadLengthBeyondBuffer_Throws_WithoutReadingOutOfBounds()
    {
        byte[] frame = ValidFrame(out _);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), int.MaxValue);
        var ex = Assert.Throws<RowValidationException>(() => RowSpillSerializer.ReadFrame(frame, IntString, out _));
        Assert.Contains("past the buffer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SchemaFingerprintMismatch_Throws()
    {
        byte[] frame = ValidFrame(out _);
        var otherSchema = new StructType([new StructField("x", LongType.Instance)]);
        var ex = Assert.Throws<RowValidationException>(() => RowSpillSerializer.ReadFrame(frame, otherSchema, out _));
        Assert.Contains("fingerprint", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptedVariableReference_Throws()
    {
        byte[] frame = ValidFrame(out int payloadStart);
        // The string field's 8-byte slot sits at payload offset 16 (bitset 8 + slot0 8).
        int slotOffset = payloadStart + 16;
        Assert.True(frame.Length > slotOffset + 8);

        // Point the (offset, length) reference far outside the block.
        long badPacked = ((long)0x7FFF_FFFF << 32) | 0x7FFF_FFFFL;
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(slotOffset), badPacked);

        Assert.Throws<RowValidationException>(() => RowSpillSerializer.ReadFrame(frame, IntString, out _));
    }

    [Fact]
    public void Map_KeyCountNotEqualValueCount_ThrowsValidation_NotIndexOutOfRange()
    {
        byte[] frame = ValidMapFrame(out int valueCountOffset, out _);

        // Shrink the value-array element count 2 -> 1, so the map has 2 keys but 1 value. The keys
        // and values arrays still validate *independently*, so without the map-pairing check this
        // reaches RowDecoder.DecodeMap, which indexes values[1] while iterating the 2 keys and throws
        // System.IndexOutOfRangeException — escaping the AC4 "only RowValidationException" contract.
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(valueCountOffset), 1);

        var ex = Assert.Throws<RowValidationException>(() => RowSpillSerializer.ReadFrame(frame, IntIntMap, out _));
        Assert.Contains("keys", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("values", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_NullKeyBit_ThrowsValidation_NotArgumentException()
    {
        byte[] frame = ValidMapFrame(out _, out int keyBitsetOffset);

        // Mark key 0 null in the key-array null bitset. Arrays allow null elements, so the key array
        // still validates; but map keys are never null, so without the null-key check this reaches
        // MapData's ctor, which throws System.ArgumentException — again escaping the AC4 contract.
        frame[keyBitsetOffset] |= 0x01;

        var ex = Assert.Throws<RowValidationException>(() => RowSpillSerializer.ReadFrame(frame, IntIntMap, out _));
        Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Truncation_AtEveryLength_ThrowsValidation_FullLengthDecodes()
    {
        byte[] frame = ValidFrame(out _);

        for (int len = 0; len < frame.Length; len++)
        {
            Assert.Throws<RowValidationException>(
                () => RowSpillSerializer.ReadFrame(frame.AsSpan(0, len), IntString, out _));
        }

        // The untruncated frame decodes cleanly.
        RowData decoded = RowSpillSerializer.ReadFrame(frame, IntString, out int consumed);
        Assert.Equal(new RowData(IntString, 42, "payload"), decoded);
        Assert.Equal(frame.Length, consumed);
    }

    [Fact]
    public void Fuzz_RandomBytes_OnlyThrowRowValidationException()
    {
        var rng = new Lcg(0xBADC0DE);

        // (1) Purely random bytes against a flat (int,string) schema and a nested map/array/struct
        //     schema. Half of each batch is seeded with a valid magic/version/fingerprint so the deep
        //     structural checks are reached (a purely random buffer almost never clears the magic).
        FuzzRandomBytes(ref rng, IntString);
        FuzzRandomBytes(ref rng, NestedMapArrayStruct);

        // (2) Bit/byte corruptions of a *valid* nested frame. These stay close enough to valid to
        //     drive the deep map/array/struct validators — map key/value count pairing, non-null
        //     keys, array counts, nested recursion — that random noise reaches only by accident.
        FuzzCorruptValidFrame(ref rng, NestedMapArrayStruct, ValidNestedFrame());
    }

    private static void FuzzRandomBytes(ref Lcg rng, StructType schema)
    {
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            int length = rng.Next(96);
            byte[] buffer = new byte[length];
            for (int i = 0; i < length; i++)
            {
                buffer[i] = (byte)rng.Next(256);
            }

            // Half the time, plant a valid magic + version + fingerprint so the deeper structural
            // checks are fuzzed too (a purely random buffer almost never gets past the magic).
            if ((iteration & 1) == 0 && length >= RowSpillSerializer.HeaderSize)
            {
                "DSR1"u8.CopyTo(buffer);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), RowSpillSerializer.CurrentFormatVersion);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), RowSpillSerializer.SchemaFingerprint(schema));
            }

            AssertOnlyValidationFailure(buffer, schema);
        }
    }

    private static void FuzzCorruptValidFrame(ref Lcg rng, StructType schema, byte[] valid)
    {
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            byte[] buffer = (byte[])valid.Clone();
            int mutations = 1 + rng.Next(4); // 1..4 byte flips keep the frame near-valid.
            for (int m = 0; m < mutations; m++)
            {
                buffer[rng.Next(buffer.Length)] = (byte)rng.Next(256);
            }

            AssertOnlyValidationFailure(buffer, schema);
        }
    }

    private static byte[] ValidNestedFrame()
    {
        var encoder = new BinaryRowEncoder(new NativeMemoryAllocator(scratchThreshold: 0));
        var map = new MapData(StringType.Instance, IntegerType.Instance, ["alpha", "beta"], [1, 2]);
        var array = new ArrayData(LongType.Instance, containsNull: true, 10L, null, 30L);
        var nested = new RowData(NestedInner, 7, "inner");
        using BinaryRow row = encoder.Encode(new RowData(NestedMapArrayStruct, map, array, nested));
        return RowSpillSerializer.WriteFrame(row);
    }

    private static void AssertOnlyValidationFailure(byte[] buffer, StructType schema)
    {
        try
        {
            // Either a clean decode or a bounded RowValidationException is acceptable; anything else
            // (OOB, overflow, NRE, ArgumentException, ...) is a failure of the bounded-validation
            // guarantee.
            RowSpillSerializer.ReadFrame(buffer, schema, out _);
        }
        catch (RowValidationException)
        {
            // expected, bounded failure
        }
    }

    /// <summary>
    /// Encodes a valid 2-entry <c>map&lt;int,int&gt;</c> row to a frame and reports the byte offsets a
    /// test corrupts to break the map's key/value structure. The offsets are parsed from the encoded
    /// frame (not hard-coded), so they track the real layout.
    /// </summary>
    private static byte[] ValidMapFrame(out int valueCountOffset, out int keyBitsetOffset)
    {
        var encoder = new BinaryRowEncoder(new NativeMemoryAllocator(scratchThreshold: 0));
        var map = new MapData(IntegerType.Instance, IntegerType.Instance, [1, 2], [10, 20]);
        using BinaryRow row = encoder.Encode(new RowData(IntIntMap, map));
        byte[] frame = RowSpillSerializer.WriteFrame(row);

        // payload = the struct block; field 0 (the map) is a variable reference in slot 0 (which sits
        // right after the struct's 8-byte null bitset).
        int payloadStart = RowSpillSerializer.HeaderSize;
        long mapRef = BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(payloadStart + 8));
        int mapStart = payloadStart + (int)(mapRef >> 32);

        // map block = [8-byte key-array size][key array][value array]; each array =
        // [8-byte element count][null bitset][slots].
        int keyArrayBytes = (int)BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(mapStart));
        int keyArrayStart = mapStart + 8;
        keyBitsetOffset = keyArrayStart + 8;              // null bitset follows the 8-byte element count
        valueCountOffset = keyArrayStart + keyArrayBytes; // value array begins right after the key array
        return frame;
    }

    // ----- Direct validator tests: precise hand-crafted blocks. -----

    [Fact]
    public void Validator_AcceptsWellFormedStringBlock()
    {
        var schema = new StructType([new StructField("s", StringType.Instance)]);
        byte[] block = BuildStringBlock(offset: 16, length: 3, payload: [(byte)'a', (byte)'b', (byte)'c'], blockSize: 24);
        BinaryRowValidator.ValidateStruct(block, schema); // does not throw
    }

    [Fact]
    public void Validator_RejectsHeaderLargerThanBlock()
    {
        var schema = new StructType(
            [new StructField("a", IntegerType.Instance), new StructField("b", IntegerType.Instance)]);
        // Header for 2 fields needs bitset(8) + 2*8 = 24 bytes; give it only 16.
        byte[] tooSmall = new byte[16];
        Assert.Throws<RowValidationException>(() => BinaryRowValidator.ValidateStruct(tooSmall, schema));
    }

    [Fact]
    public void Validator_RejectsOutOfRangeVariableReference()
    {
        var schema = new StructType([new StructField("s", StringType.Instance)]);
        byte[] block = BuildStringBlock(offset: 16, length: 1000, payload: [(byte)'a'], blockSize: 24);
        Assert.Throws<RowValidationException>(() => BinaryRowValidator.ValidateStruct(block, schema));
    }

    [Fact]
    public void Validator_RejectsImpossibleArrayCount()
    {
        var schema = new StructType([new StructField("xs", new ArrayType(IntegerType.Instance))]);

        // Struct: bitset(8) + slot0(8) referencing a 16-byte array block at offset 16.
        byte[] block = new byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(8), ((long)16 << 32) | 16); // (offset 16, length 16)
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(16), long.MaxValue); // array count = absurd

        var ex = Assert.Throws<RowValidationException>(() => BinaryRowValidator.ValidateStruct(block, schema));
        Assert.Contains("count", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_RejectsDecimalPayloadOfWrongLength()
    {
        var schema = new StructType([new StructField("d", new DecimalType(30, 4))]);
        // 16-byte decimal stored as a variable reference; declare an 8-byte payload instead.
        byte[] block = BuildStringBlock(offset: 16, length: 8, payload: new byte[8], blockSize: 24);
        Assert.Throws<RowValidationException>(() => BinaryRowValidator.ValidateStruct(block, schema));
    }

    private static byte[] BuildStringBlock(int offset, int length, byte[] payload, int blockSize)
    {
        // Single-field struct: bitset(8) not-null, slot0 = packed(offset,length), then payload bytes.
        byte[] block = new byte[blockSize];
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(8), ((long)offset << 32) | (uint)length);
        payload.CopyTo(block.AsSpan(offset, payload.Length));
        return block;
    }

    /// <summary>A tiny deterministic LCG so fuzz input is reproducible without <c>System.Random</c>.</summary>
    private struct Lcg
    {
        private ulong _state;

        public Lcg(ulong seed) => _state = seed;

        public int Next(int bound)
        {
            _state = (_state * 6364136223846793005UL) + 1442695040888963407UL;
            return (int)((_state >> 33) % (ulong)bound);
        }
    }
}
