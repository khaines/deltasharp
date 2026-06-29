using System.Buffers.Binary;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.RowFormat;

/// <summary>
/// Validates that an encoded binary-row block is structurally well-formed for a given schema before
/// it is decoded (STORY-02.4.2 AC4). Every offset, length, slot, and nested block is range-checked
/// with explicit in-bounds tests <b>before</b> any byte is read, so a malformed or truncated block
/// fails fast with a <see cref="RowValidationException"/> and <b>never</b> reads outside the buffer.
/// </summary>
/// <remarks>
/// <para>
/// The recursion is driven by the <b>trusted schema</b>, not by the untrusted bytes: nesting depth
/// equals the schema's type-tree depth, so an adversarial buffer cannot force unbounded recursion.
/// Element counts come from the bytes but are bounded against the block length before any size is
/// computed, capping the work at <c>O(block length)</c> and preventing integer overflow.
/// </para>
/// <para>
/// Kept <c>internal</c> — <see cref="RowSpillSerializer"/> runs it on the deserialize path, and the
/// friend test assembly exercises it directly.
/// </para>
/// </remarks>
internal static class BinaryRowValidator
{
    /// <summary>Validates a top-level row block against <paramref name="schema"/>.</summary>
    /// <exception cref="RowValidationException">The block is malformed or truncated for the schema.</exception>
    public static void ValidateStruct(ReadOnlySpan<byte> block, StructType schema)
    {
        long fieldCount = schema.Count;
        long bitsetBytes = RowNullBitSet.ByteSize(schema.Count);
        long headerBytes = bitsetBytes + (fieldCount * RowLayout.SlotBytes);
        if (block.Length < headerBytes)
        {
            throw Malformed($"struct header needs {headerBytes} bytes but block has {block.Length}.");
        }

        ReadOnlySpan<byte> bitset = block[..(int)bitsetBytes];
        for (int i = 0; i < schema.Count; i++)
        {
            if (!RowNullBitSet.IsNull(bitset, i))
            {
                ValidateField(block, (int)bitsetBytes + (i * RowLayout.SlotBytes), schema[i].DataType);
            }
        }
    }

    private static void ValidateField(ReadOnlySpan<byte> block, int slotOffset, DataType type)
    {
        // The slot itself is inside the already-validated header, so reading 8 bytes is in bounds.
        if (RowLayout.IsInlineFixedWidth(type))
        {
            return;
        }

        long packed = BinaryPrimitives.ReadInt64LittleEndian(block.Slice(slotOffset, RowLayout.SlotBytes));
        int offset = (int)(packed >> 32);
        int length = (int)(uint)packed;
        if (offset < 0 || length < 0 || (long)offset + length > block.Length)
        {
            throw Malformed(
                $"variable reference (offset {offset}, length {length}) is outside the {block.Length}-byte block.");
        }

        ReadOnlySpan<byte> payload = block.Slice(offset, length);
        switch (type)
        {
            case DecimalType:
                if (length != 16)
                {
                    throw Malformed($"decimal payload must be 16 bytes but was {length}.");
                }

                break;
            case StringType:
            case BinaryType:
                break; // any byte content is valid
            case StructType s:
                ValidateStruct(payload, s);
                break;
            case ArrayType a:
                ValidateArray(payload, a.ElementType);
                break;
            case MapType m:
                ValidateMap(payload, m.KeyType, m.ValueType);
                break;
            default:
                throw Malformed($"unsupported variable type {type.SimpleString}.");
        }
    }

    private static void ValidateArray(ReadOnlySpan<byte> block, DataType elementType)
    {
        if (block.Length < 8)
        {
            throw Malformed($"array block needs an 8-byte count but has {block.Length} bytes.");
        }

        long count = BinaryPrimitives.ReadInt64LittleEndian(block);

        // Bound the count against the block before computing any size, so the bitset/slot math can
        // neither overflow nor allocate work beyond the buffer. Each element occupies an 8-byte slot.
        if (count < 0 || count > (block.Length - 8) / RowLayout.SlotBytes)
        {
            throw Malformed($"array count {count} is impossible for a {block.Length}-byte block.");
        }

        long bitsetBytes = RowNullBitSet.ByteSize((int)count);
        long headerBytes = 8 + bitsetBytes + (count * RowLayout.SlotBytes);
        if (block.Length < headerBytes)
        {
            throw Malformed($"array header needs {headerBytes} bytes but block has {block.Length}.");
        }

        ReadOnlySpan<byte> bitset = block.Slice(8, (int)bitsetBytes);
        int slotBase = 8 + (int)bitsetBytes;
        for (int i = 0; i < count; i++)
        {
            if (!RowNullBitSet.IsNull(bitset, i))
            {
                ValidateField(block, slotBase + (i * RowLayout.SlotBytes), elementType);
            }
        }
    }

    private static void ValidateMap(ReadOnlySpan<byte> block, DataType keyType, DataType valueType)
    {
        if (block.Length < 8)
        {
            throw Malformed($"map block needs an 8-byte key-array size but has {block.Length} bytes.");
        }

        long keyArrayBytes = BinaryPrimitives.ReadInt64LittleEndian(block);
        if (keyArrayBytes < 0 || (long)8 + keyArrayBytes > block.Length)
        {
            throw Malformed($"map key-array size {keyArrayBytes} is outside the {block.Length}-byte block.");
        }

        ValidateArray(block.Slice(8, (int)keyArrayBytes), keyType);
        ValidateArray(block[(8 + (int)keyArrayBytes)..], valueType);
    }

    private static RowValidationException Malformed(string detail) =>
        new($"Malformed binary row: {detail}");
}
