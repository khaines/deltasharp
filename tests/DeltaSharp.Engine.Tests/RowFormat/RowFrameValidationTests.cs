using System.Buffers.Binary;
using DeltaSharp.Engine.Memory;
using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Engine.Types;
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
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            int length = rng.Next(80);
            byte[] buffer = new byte[length];
            for (int i = 0; i < length; i++)
            {
                buffer[i] = (byte)rng.Next(256);
            }

            // Half the time, plant a valid magic + version so the deeper structural checks are fuzzed
            // too (a purely random buffer almost never gets past the magic).
            if ((iteration & 1) == 0 && length >= RowSpillSerializer.HeaderSize)
            {
                "DSR1"u8.CopyTo(buffer);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), RowSpillSerializer.CurrentFormatVersion);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), RowSpillSerializer.SchemaFingerprint(IntString));
            }

            AssertOnlyValidationFailure(buffer);
        }
    }

    private static void AssertOnlyValidationFailure(byte[] buffer)
    {
        try
        {
            // Either a clean decode or a bounded RowValidationException is acceptable; anything else
            // (OOB, overflow, NRE, ...) is a failure of the bounded-validation guarantee.
            RowSpillSerializer.ReadFrame(buffer, IntString, out _);
        }
        catch (RowValidationException)
        {
            // expected, bounded failure
        }
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
