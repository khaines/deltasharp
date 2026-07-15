using System.Buffers.Binary;
using DeltaSharp.Engine.Memory;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.RowFormat;

/// <summary>
/// A read view over an encoded binary row that owns its off-heap <see cref="OwnedBuffer"/>
/// (ADR-0008/0013). It exposes typed accessors and a full <see cref="ToRowData"/> decode, and is
/// disposed exactly once. For shuffle/spill serialization, <see cref="TransferBuffer"/> moves
/// ownership of the bytes out so the new owner disposes them — keeping disposal responsibility
/// explicit and double-free safe (STORY-02.4.1 AC4): the underlying <see cref="OwnedBuffer"/>
/// already releases at most once, so a second <see cref="Dispose"/> is a no-op.
/// </summary>
public sealed class BinaryRow : IDisposable
{
    private OwnedBuffer? _buffer;
    private readonly int _length;

    internal BinaryRow(OwnedBuffer buffer, RowLayout layout)
    {
        _buffer = buffer;
        _length = buffer.Length;
        Layout = layout;
    }

    /// <summary>The geometry/schema this row was encoded with.</summary>
    public RowLayout Layout { get; }

    /// <summary>The schema.</summary>
    public StructType Schema => Layout.Schema;

    /// <summary>The total encoded size in bytes (8-byte aligned).</summary>
    public int Length => _length;

    /// <summary>The raw encoded bytes. Must not outlive the row; throws after disposal/transfer.</summary>
    /// <exception cref="ObjectDisposedException">The buffer was disposed or its ownership transferred.</exception>
    public ReadOnlySpan<byte> AsSpan() => Buffer.AsSpan();

    /// <summary>Whether field <paramref name="index"/> is null.</summary>
    public bool IsNullAt(int index) => RowNullBitSet.IsNull(AsSpan(), index);

    /// <summary>Decodes the whole row into a <see cref="RowData"/>.</summary>
    public RowData ToRowData() => RowDecoder.DecodeStruct(AsSpan(), Schema);

    /// <summary>
    /// Transfers ownership of the off-heap buffer to the caller (for shuffle/spill serialization).
    /// After transfer this row no longer owns the bytes; its <see cref="Dispose"/> is a no-op and
    /// the caller must dispose the returned buffer exactly once.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Ownership was already disposed or transferred.</exception>
    public OwnedBuffer TransferBuffer()
    {
        OwnedBuffer buffer = Buffer;
        _buffer = null;
        return buffer;
    }

    /// <summary>Releases the off-heap buffer exactly once (no-op after a prior dispose or transfer).</summary>
    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
    }

    private OwnedBuffer Buffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            return _buffer;
        }
    }
}

/// <summary>Decodes self-contained binary row/struct/array/map blocks back into the value model (STORY-02.4.1 AC2/AC3).</summary>
internal static class RowDecoder
{
    public static RowData DecodeStruct(ReadOnlySpan<byte> block, StructType schema)
    {
        int n = schema.Count;
        int bitsetBytes = RowNullBitSet.ByteSize(n);
        ReadOnlySpan<byte> bitset = block[..bitsetBytes];
        object?[] values = new object?[n];
        for (int i = 0; i < n; i++)
        {
            if (!RowNullBitSet.IsNull(bitset, i))
            {
                values[i] = ReadField(block, bitsetBytes + (i * RowLayout.SlotBytes), schema[i].DataType);
            }
        }

        return new RowData(schema, values);
    }

    public static ArrayData DecodeArray(ReadOnlySpan<byte> block, DataType elementType, bool containsNull)
    {
        int count = checked((int)BinaryPrimitives.ReadInt64LittleEndian(block));
        int bitsetBytes = RowNullBitSet.ByteSize(count);
        ReadOnlySpan<byte> bitset = block.Slice(8, bitsetBytes);
        object?[] elements = new object?[count];
        int slotBase = 8 + bitsetBytes;
        for (int i = 0; i < count; i++)
        {
            if (!RowNullBitSet.IsNull(bitset, i))
            {
                elements[i] = ReadField(block, slotBase + (i * RowLayout.SlotBytes), elementType);
            }
        }

        return new ArrayData(elementType, containsNull, elements);
    }

    public static MapData DecodeMap(ReadOnlySpan<byte> block, DataType keyType, DataType valueType, bool valueContainsNull)
    {
        int keyArrayBytes = checked((int)BinaryPrimitives.ReadInt64LittleEndian(block));
        ArrayData keys = DecodeArray(block.Slice(8, keyArrayBytes), keyType, false);
        ArrayData vals = DecodeArray(block[(8 + keyArrayBytes)..], valueType, valueContainsNull);
        object?[] k = new object?[keys.Count];
        object?[] v = new object?[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            k[i] = keys[i];
            v[i] = vals[i];
        }

        return new MapData(keyType, valueType, k, v);
    }

    private static object ReadField(ReadOnlySpan<byte> block, int slotOffset, DataType type)
    {
        ReadOnlySpan<byte> slot = block.Slice(slotOffset, RowLayout.SlotBytes);
        if (RowLayout.IsInlineFixedWidth(type))
        {
            return type switch
            {
                BooleanType => slot[0] != 0,
                ByteType => (sbyte)slot[0],
                ShortType => BinaryPrimitives.ReadInt16LittleEndian(slot),
                IntegerType or DateType => BinaryPrimitives.ReadInt32LittleEndian(slot),
                LongType or TimestampType or TimestampNtzType => BinaryPrimitives.ReadInt64LittleEndian(slot),
                FloatType => BinaryPrimitives.ReadSingleLittleEndian(slot),
                DoubleType => BinaryPrimitives.ReadDoubleLittleEndian(slot),
                DecimalType => (Int128)BinaryPrimitives.ReadInt64LittleEndian(slot),
                _ => throw new RowFormatException($"Unsupported inline type {type.SimpleString}."),
            };
        }

        long packed = BinaryPrimitives.ReadInt64LittleEndian(slot);
        int offset = (int)(packed >> 32);
        int length = (int)(uint)packed;
        ReadOnlySpan<byte> payload = block.Slice(offset, length);
        return type switch
        {
            StringType => System.Text.Encoding.UTF8.GetString(payload),
            BinaryType => payload.ToArray(),
            DecimalType => BinaryPrimitives.ReadInt128LittleEndian(payload),
            StructType s => DecodeStruct(payload, s),
            ArrayType a => DecodeArray(payload, a.ElementType, a.ContainsNull),
            MapType m => DecodeMap(payload, m.KeyType, m.ValueType, m.ValueContainsNull),
            _ => throw new RowFormatException($"Unsupported variable type {type.SimpleString}."),
        };
    }
}
