using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.RowFormat;

/// <summary>
/// The per-type, order-preserving byte transforms that make a binary sort key
/// <c>memcmp</c>-comparable (STORY-02.4.2 AC1). Each method writes an <b>ascending</b> value
/// encoding; <see cref="EncodeValue"/> applies the descending direction by complementing the
/// written bytes. The transforms are documented in
/// <c>docs/engineering/design/byte-sortable-ordering.md</c> and mirror Spark's ordering for
/// signed integers, IEEE-754 floats (NaN largest, −0.0 == +0.0), decimals, timestamps, and
/// lexicographic strings/binary.
/// </summary>
/// <remarks>
/// All writes are <see cref="System.Span{T}"/>-based with no intermediate heap allocation: the
/// only scratch is a stack buffer (or a pooled rental for large strings), so the encode hot path
/// is allocation-free. Kept <c>internal</c> and exercised through the friend-assembly test-access
/// policy.
/// </remarks>
internal static class ByteSortableEncoding
{
    /// <summary>UTF-8 byte counts at or below this take a stack scratch buffer instead of a pool rental.</summary>
    private const int StackScratchThreshold = 256;

    /// <summary>The canonical quiet-NaN bit pattern all <see cref="float"/> NaNs collapse to before encoding.</summary>
    private const int CanonicalSingleNaNBits = 0x7FC0_0000;

    /// <summary>The canonical quiet-NaN bit pattern all <see cref="double"/> NaNs collapse to before encoding.</summary>
    private const long CanonicalDoubleNaNBits = 0x7FF8_0000_0000_0000L;

    /// <summary>Whether <paramref name="type"/> can be a byte-sortable sort key (every atomic/decimal type except <c>void</c> and the nested types).</summary>
    public static bool IsSupportedKeyType(DataType type) => type switch
    {
        BooleanType or ByteType or ShortType or IntegerType or LongType
            or FloatType or DoubleType or DateType or TimestampType
            or DecimalType or StringType or BinaryType => true,
        _ => false,
    };

    /// <summary>
    /// An upper bound on the value-encoding length for <paramref name="value"/> of
    /// <paramref name="type"/> (the marker byte is counted separately by <see cref="SortKeyEncoder"/>).
    /// Cheap: for strings/binary it counts UTF-8 bytes without encoding, then assumes the
    /// worst-case escape expansion.
    /// </summary>
    public static int MaxValueLength(DataType type, object value) => type switch
    {
        BooleanType => 1,
        ByteType => 1,
        ShortType => 2,
        IntegerType or DateType => 4,
        LongType or TimestampType => 8,
        FloatType => 4,
        DoubleType => 8,
        DecimalType => 16,
        StringType => EscapedUpperBound(Encoding.UTF8.GetByteCount(Cast<string>(value, type))),
        BinaryType => EscapedUpperBound(Cast<byte[]>(value, type).Length),
        _ => throw Unsupported(type),
    };

    /// <summary>
    /// Writes the order-preserving encoding of <paramref name="value"/> (typed by
    /// <paramref name="type"/>) into <paramref name="destination"/>, complementing it when
    /// <paramref name="descending"/>, and returns the number of bytes written.
    /// </summary>
    /// <exception cref="RowFormatException">The CLR value does not match <paramref name="type"/>, or the type is not a supported sort key.</exception>
    public static int EncodeValue(DataType type, object value, Span<byte> destination, bool descending)
    {
        int written = type switch
        {
            BooleanType => WriteBoolean(Cast<bool>(value, type), destination),
            ByteType => WriteSByte(Cast<sbyte>(value, type), destination),
            ShortType => WriteInt16(Cast<short>(value, type), destination),
            IntegerType or DateType => WriteInt32(Cast<int>(value, type), destination),
            LongType or TimestampType => WriteInt64(Cast<long>(value, type), destination),
            FloatType => WriteSingle(Cast<float>(value, type), destination),
            DoubleType => WriteDouble(Cast<double>(value, type), destination),
            DecimalType => WriteInt128(Cast<Int128>(value, type), destination),
            StringType => WriteString(Cast<string>(value, type), destination),
            BinaryType => WriteVariableBytes(Cast<byte[]>(value, type), destination),
            _ => throw Unsupported(type),
        };

        if (descending)
        {
            Complement(destination[..written]);
        }

        return written;
    }

    // false (0) sorts before true (1), matching Spark.
    private static int WriteBoolean(bool value, Span<byte> destination)
    {
        destination[0] = value ? (byte)1 : (byte)0;
        return 1;
    }

    // Signed integers: flip the sign bit so the unsigned big-endian bytes order like the signed value.
    private static int WriteSByte(sbyte value, Span<byte> destination)
    {
        destination[0] = (byte)((value & 0xFF) ^ 0x80);
        return 1;
    }

    private static int WriteInt16(short value, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)((value & 0xFFFF) ^ 0x8000));
        return 2;
    }

    private static int WriteInt32(int value, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)value ^ 0x8000_0000u);
        return 4;
    }

    private static int WriteInt64(long value, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)value ^ 0x8000_0000_0000_0000UL);
        return 8;
    }

    // Compact and large decimals are both carried as an unscaled Int128 in the value model; a single
    // DecimalType per key column means unscaled values are directly comparable.
    private static int WriteInt128(Int128 value, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt128BigEndian(destination, (UInt128)value ^ ((UInt128)1 << 127));
        return 16;
    }

    private static int WriteSingle(float value, Span<byte> destination)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        if (float.IsNaN(value))
        {
            bits = CanonicalSingleNaNBits; // every NaN sorts equal and as the largest value (Spark parity)
        }
        else if (value == 0f)
        {
            bits = 0; // canonicalize −0.0 to +0.0 so they encode (and compare) equal
        }

        uint u = (uint)bits;
        uint mask = (uint)((int)u >> 31) | 0x8000_0000u; // negative ⇒ flip all bits, positive ⇒ flip sign bit
        BinaryPrimitives.WriteUInt32BigEndian(destination, u ^ mask);
        return 4;
    }

    private static int WriteDouble(double value, Span<byte> destination)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        if (double.IsNaN(value))
        {
            bits = CanonicalDoubleNaNBits;
        }
        else if (value == 0d)
        {
            bits = 0;
        }

        ulong u = (ulong)bits;
        ulong mask = (ulong)((long)u >> 63) | 0x8000_0000_0000_0000UL;
        BinaryPrimitives.WriteUInt64BigEndian(destination, u ^ mask);
        return 8;
    }

    private static int WriteString(string value, Span<byte> destination)
    {
        int utf8Length = Encoding.UTF8.GetByteCount(value);
        byte[]? rented = null;
        Span<byte> scratch = utf8Length <= StackScratchThreshold
            ? stackalloc byte[StackScratchThreshold]
            : (rented = ArrayPool<byte>.Shared.Rent(utf8Length));
        try
        {
            int produced = Encoding.UTF8.GetBytes(value, scratch);
            return WriteVariableBytes(scratch[..produced], destination);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    // Order-preserving, self-delimiting byte string: escape 0x00 as 0x00 0xFF, then terminate with
    // 0x00 0x00. The terminator is the minimum two-byte sequence and cannot appear inside the body,
    // so no encoding is a prefix of another — which keeps lexicographic order under memcmp AND makes
    // the descending complement an exact order reversal.
    private static int WriteVariableBytes(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int pos = 0;
        foreach (byte b in source)
        {
            if (b == 0x00)
            {
                destination[pos++] = 0x00;
                destination[pos++] = 0xFF;
            }
            else
            {
                destination[pos++] = b;
            }
        }

        destination[pos++] = 0x00;
        destination[pos++] = 0x00;
        return pos;
    }

    private static void Complement(Span<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)~bytes[i];
        }
    }

    private static int EscapedUpperBound(int rawByteCount) => (2 * rawByteCount) + 2;

    private static T Cast<T>(object value, DataType type) =>
        value is T t
            ? t
            : throw new RowFormatException(
                $"Sort key of type {type.SimpleString} expected {typeof(T).Name} but got {value.GetType().Name}.");

    private static RowFormatException Unsupported(DataType type) =>
        new($"Type {type.SimpleString} is not a supported byte-sortable sort key.");
}
