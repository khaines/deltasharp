using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using DeltaSharp.Engine.Memory;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.RowFormat;

/// <summary>
/// Encodes <see cref="RowData"/> into the 8-byte-aligned binary row layout (an <c>UnsafeRow</c>
/// analog, ADR-0008) backed by an off-heap <see cref="OwnedBuffer"/> from a
/// <see cref="NativeMemoryAllocator"/>. Each block (row, nested struct, array, map) is
/// self-contained: a word-aligned null bitset, a fixed region of 8-byte slots, then a variable
/// region whose payloads are 8-byte padded so the block total stays 8-byte aligned
/// (STORY-02.4.1 AC1). Variable, large-decimal, and nested fields store a packed
/// <c>(offset, length)</c> reference (offsets relative to the enclosing block) into the variable
/// region; nested structs/arrays/maps recurse, preserving element order, key/value pairing, and
/// nested nulls (AC3).
/// </summary>
public sealed class BinaryRowEncoder
{
    private readonly NativeMemoryAllocator _allocator;

    /// <summary>Creates an encoder that allocates row buffers through <paramref name="allocator"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is null.</exception>
    public BinaryRowEncoder(NativeMemoryAllocator allocator)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        _allocator = allocator;
    }

    /// <summary>
    /// Encodes <paramref name="row"/> into a new off-heap <see cref="BinaryRow"/> the caller owns
    /// and must dispose exactly once. The bytes are also 8-byte aligned in total.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
    /// <exception cref="RowFormatException">A value's CLR type does not match its field type, or a type is unsupported.</exception>
    public BinaryRow Encode(RowData row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var layout = new RowLayout(row.Schema);
        byte[] bytes = EncodeStruct(row);

        OwnedBuffer buffer = _allocator.AllocateUninitialized(bytes.Length);
        try
        {
            bytes.CopyTo(buffer.AsSpan());
            return new BinaryRow(buffer, layout);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    internal static byte[] EncodeStruct(RowData row)
    {
        StructType schema = row.Schema;
        int n = schema.Count;
        int bitsetBytes = RowNullBitSet.ByteSize(n);
        int headerBytes = bitsetBytes + (n * RowLayout.SlotBytes);

        byte[] header = new byte[headerBytes];
        var variable = new List<byte>();
        for (int i = 0; i < n; i++)
        {
            object? value = row[i];
            if (value is null)
            {
                RowNullBitSet.SetNull(header.AsSpan(0, bitsetBytes), i);
                continue;
            }

            WriteField(header.AsSpan(bitsetBytes + (i * RowLayout.SlotBytes), RowLayout.SlotBytes),
                schema[i].DataType, value, headerBytes, variable, schema[i].Name);
        }

        return Concat(header, variable);
    }

    private static byte[] EncodeArray(DataType elementType, int count, Func<int, object?> elementAt)
    {
        int bitsetBytes = RowNullBitSet.ByteSize(count);
        int headerBytes = 8 + bitsetBytes + (count * RowLayout.SlotBytes); // 8-byte element-count header.
        byte[] header = new byte[headerBytes];
        BinaryPrimitives.WriteInt64LittleEndian(header, count);
        Span<byte> bitset = header.AsSpan(8, bitsetBytes);
        var variable = new List<byte>();
        for (int i = 0; i < count; i++)
        {
            object? value = elementAt(i);
            if (value is null)
            {
                RowNullBitSet.SetNull(bitset, i);
                continue;
            }

            WriteField(header.AsSpan(8 + bitsetBytes + (i * RowLayout.SlotBytes), RowLayout.SlotBytes),
                elementType, value, headerBytes, variable, "element");
        }

        return Concat(header, variable);
    }

    private static byte[] EncodeMap(MapData map)
    {
        byte[] keys = EncodeArray(map.KeyType, map.Count, map.Key);
        byte[] values = EncodeArray(map.ValueType, map.Count, map.Value);
        byte[] block = new byte[8 + keys.Length + values.Length]; // 8-byte key-array size, then keys, then values.
        BinaryPrimitives.WriteInt64LittleEndian(block, keys.Length);
        keys.CopyTo(block, 8);
        values.CopyTo(block, 8 + keys.Length);
        return block;
    }

    private static void WriteField(Span<byte> slot, DataType type, object value, int headerBytes, List<byte> variable, string field)
    {
        if (RowLayout.IsInlineFixedWidth(type))
        {
            WriteInline(slot, type, value, field);
            return;
        }

        byte[] payload = EncodeVariable(type, value, field);
        int offset = headerBytes + variable.Count;
        variable.AddRange(payload);
        Pad8(variable);
        BinaryPrimitives.WriteInt64LittleEndian(slot, ((long)offset << 32) | (uint)payload.Length);
    }

    private static void WriteInline(Span<byte> slot, DataType type, object value, string field)
    {
        switch (type)
        {
            case BooleanType: slot[0] = Cast<bool>(value, type, field) ? (byte)1 : (byte)0; break;
            case ByteType: slot[0] = (byte)Cast<sbyte>(value, type, field); break;
            case ShortType: BinaryPrimitives.WriteInt16LittleEndian(slot, Cast<short>(value, type, field)); break;
            case IntegerType or DateType: BinaryPrimitives.WriteInt32LittleEndian(slot, Cast<int>(value, type, field)); break;
            case LongType or TimestampType: BinaryPrimitives.WriteInt64LittleEndian(slot, Cast<long>(value, type, field)); break;
            case FloatType: BinaryPrimitives.WriteSingleLittleEndian(slot, Cast<float>(value, type, field)); break;
            case DoubleType: BinaryPrimitives.WriteDoubleLittleEndian(slot, Cast<double>(value, type, field)); break;
            case DecimalType: BinaryPrimitives.WriteInt64LittleEndian(slot, (long)Cast<Int128>(value, type, field)); break;
            default: throw Unsupported(type, field);
        }
    }

    private static byte[] EncodeVariable(DataType type, object value, string field) =>
        type switch
        {
            StringType => Encoding.UTF8.GetBytes(Cast<string>(value, type, field)),
            BinaryType => Cast<byte[]>(value, type, field),
            DecimalType => WriteInt128(Cast<Int128>(value, type, field)),
            StructType => EncodeStruct(CastRow(value, type, field)),
            ArrayType a => EncodeArrayValue(a.ElementType, Cast<ArrayData>(value, type, field)),
            MapType => EncodeMap(Cast<MapData>(value, type, field)),
            _ => throw Unsupported(type, field),
        };

    private static byte[] EncodeArrayValue(DataType elementType, ArrayData array) =>
        EncodeArray(elementType, array.Count, i => array[i]);

    private static byte[] WriteInt128(Int128 value)
    {
        byte[] bytes = new byte[16];
        BinaryPrimitives.WriteInt128LittleEndian(bytes, value);
        return bytes;
    }

    private static void Pad8(List<byte> bytes)
    {
        int rem = bytes.Count & 7;
        if (rem != 0)
        {
            for (int i = rem; i < 8; i++)
            {
                bytes.Add(0);
            }
        }
    }

    private static byte[] Concat(byte[] header, List<byte> variable)
    {
        byte[] result = new byte[header.Length + variable.Count];
        header.CopyTo(result, 0);
        variable.CopyTo(result, header.Length);
        return result;
    }

    private static T Cast<T>(object value, DataType type, string field) =>
        value is T t ? t : throw new RowFormatException(
            $"Field '{field}' of type {type.SimpleString} expected {typeof(T).Name} but got {value.GetType().Name}.");

    private static RowData CastRow(object value, DataType type, string field) => Cast<RowData>(value, type, field);

    private static RowFormatException Unsupported(DataType type, string field) =>
        new($"Field '{field}' has unsupported binary-row type {type.SimpleString}.");
}
