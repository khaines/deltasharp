using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Apache.Arrow;
using DeltaSharp.Engine.Types;
using ArrowTypes = Apache.Arrow.Types;

namespace DeltaSharp.Engine.Columnar.Arrow;

/// <summary>
/// Builds an Apache Arrow array from a single DeltaSharp <see cref="ColumnVector"/> for
/// <see cref="ArrowBatchConverter.ToArrow"/> (STORY-02.2.2, #136) — the export half of the "Arrow at
/// the edges" boundary. The vector is read through the storage-agnostic <see cref="ColumnVector"/>
/// contract (logical, offset/selection-aware, per-row accessors), so the same path exports a
/// freshly built managed vector, an Arrow-backed wrapper, a slice, and a selected view. Validity is
/// emitted in Arrow's LSB-first bit order (the order <see cref="Bitmap"/> already uses), values in
/// little-endian, and the resulting array owns fresh managed buffers so the exported
/// <c>RecordBatch</c> is independent of any imported source. Nested columns are exported by
/// <see cref="ArrowBatchConverter"/> via a retained pass-through and never reach this writer.
/// </summary>
internal static class ArrowColumnWriter
{
    // DeltaSharp timestamps are UTC-normalized microsecond instants; the timezone is not modeled by
    // the v1 type, so export normalizes to a stable "UTC" zone (documented in the capability matrix).
    private static readonly ArrowTypes.TimestampType TimestampMicrosUtc =
        new(ArrowTypes.TimeUnit.Microsecond, "UTC");

    /// <summary>Builds the Arrow array for the logical rows of <paramref name="column"/>.</summary>
    /// <exception cref="UnsupportedTypeException">
    /// The column's logical type has no v1 Arrow export path (a nested column, which the converter
    /// handles as a pass-through and must not route here).
    /// </exception>
    internal static IArrowArray BuildArray(ColumnVector column) =>
        column.Type switch
        {
            BooleanType => BuildBoolean(column),
            ByteType => BuildFixed<byte>(column, ArrowTypes.Int8Type.Default),
            ShortType => BuildFixed<short>(column, ArrowTypes.Int16Type.Default),
            IntegerType => BuildFixed<int>(column, ArrowTypes.Int32Type.Default),
            DateType => BuildFixed<int>(column, ArrowTypes.Date32Type.Default),
            LongType => BuildFixed<long>(column, ArrowTypes.Int64Type.Default),
            TimestampType => BuildFixed<long>(column, TimestampMicrosUtc),
            FloatType => BuildFixed<float>(column, ArrowTypes.FloatType.Default),
            DoubleType => BuildFixed<double>(column, ArrowTypes.DoubleType.Default),
            DecimalType d => BuildDecimal(column, d),
            StringType => BuildVariable(column, ArrowTypes.StringType.Default),
            BinaryType => BuildVariable(column, ArrowTypes.BinaryType.Default),
            _ => throw new UnsupportedTypeException(
                $"No Arrow export is defined for column type '{column.Type.SimpleString}'."),
        };

    private static IArrowArray BuildFixed<T>(ColumnVector column, ArrowTypes.IArrowType arrowType)
        where T : unmanaged
    {
        int length = column.Length;
        var values = new T[length];
        for (int i = 0; i < length; i++)
        {
            // Null slots still occupy a value slot (zero); validity carries nullness.
            values[i] = column.IsNull(i) ? default : column.GetValue<T>(i);
        }

        var valuesBuffer = new ArrowBuffer(MemoryMarshal.AsBytes<T>(values).ToArray());
        (ArrowBuffer validity, int nullCount) = BuildValidity(column);
        var data = new ArrayData(arrowType, length, nullCount, 0, new[] { validity, valuesBuffer });
        return ArrowArrayFactory.BuildArray(data);
    }

    private static IArrowArray BuildBoolean(ColumnVector column)
    {
        int length = column.Length;

        // Arrow booleans are bit-packed LSB-first in their own values buffer — the same packing as
        // the validity bitmap — so DeltaSharp's 1-byte bools are re-packed here.
        var valueBits = new byte[Bitmap.ByteCount(length)];
        for (int i = 0; i < length; i++)
        {
            if (!column.IsNull(i) && column.GetValue<bool>(i))
            {
                Bitmap.Set(valueBits, i, value: true);
            }
        }

        var valuesBuffer = new ArrowBuffer(valueBits);
        (ArrowBuffer validity, int nullCount) = BuildValidity(column);
        var data = new ArrayData(
            ArrowTypes.BooleanType.Default, length, nullCount, 0, new[] { validity, valuesBuffer });
        return ArrowArrayFactory.BuildArray(data);
    }

    private static IArrowArray BuildDecimal(ColumnVector column, DecimalType type)
    {
        const int byteWidth = 16;
        int length = column.Length;
        var valueBytes = new byte[length * byteWidth];
        Span<byte> span = valueBytes;
        for (int i = 0; i < length; i++)
        {
            if (column.IsNull(i))
            {
                continue;
            }

            // DeltaSharp stores the unscaled integer (compacted to long when precision allows);
            // Arrow decimal128 is the same unscaled value as 16 little-endian two's-complement bytes.
            Int128 unscaled = type.IsCompact ? column.GetValue<long>(i) : column.GetValue<Int128>(i);
            BinaryPrimitives.WriteInt128LittleEndian(span.Slice(i * byteWidth, byteWidth), unscaled);
        }

        var valuesBuffer = new ArrowBuffer(valueBytes);
        (ArrowBuffer validity, int nullCount) = BuildValidity(column);
        var arrowType = new ArrowTypes.Decimal128Type(type.Precision, type.Scale);
        var data = new ArrayData(arrowType, length, nullCount, 0, new[] { validity, valuesBuffer });
        return ArrowArrayFactory.BuildArray(data);
    }

    private static IArrowArray BuildVariable(ColumnVector column, ArrowTypes.IArrowType arrowType)
    {
        int length = column.Length;
        var offsets = new int[length + 1];
        int running = 0;
        for (int i = 0; i < length; i++)
        {
            offsets[i] = running;
            if (!column.IsNull(i))
            {
                running += column.GetBytes(i).Length;
            }
        }

        offsets[length] = running;

        var dataBytes = new byte[running];
        for (int i = 0; i < length; i++)
        {
            if (column.IsNull(i))
            {
                continue;
            }

            column.GetBytes(i).CopyTo(dataBytes.AsSpan(offsets[i]));
        }

        var offsetsBuffer = new ArrowBuffer(MemoryMarshal.AsBytes<int>(offsets).ToArray());
        var dataBuffer = new ArrowBuffer(dataBytes);
        (ArrowBuffer validity, int nullCount) = BuildValidity(column);
        var data = new ArrayData(arrowType, length, nullCount, 0, new[] { validity, offsetsBuffer, dataBuffer });
        return ArrowArrayFactory.BuildArray(data);
    }

    private static (ArrowBuffer Validity, int NullCount) BuildValidity(ColumnVector column)
    {
        int nullCount = column.NullCount;
        if (nullCount == 0)
        {
            // Arrow treats an empty validity buffer as "all valid"; no allocation needed.
            return (ArrowBuffer.Empty, 0);
        }

        int length = column.Length;
        var bits = new byte[Bitmap.ByteCount(length)];
        for (int i = 0; i < length; i++)
        {
            // Set = valid/non-null (Arrow and Bitmap share LSB-first ordering).
            Bitmap.Set(bits, i, !column.IsNull(i));
        }

        return (new ArrowBuffer(bits), nullCount);
    }
}
