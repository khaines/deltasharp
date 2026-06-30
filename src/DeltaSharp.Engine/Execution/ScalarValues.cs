using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Boxes, compares, and re-emits a single scalar lane in its <b>storage</b> CLR shape — the helper
/// the running <c>MIN</c>/<c>MAX</c> accumulator uses to keep, order, and write back its best value.
/// Storage shape is what <see cref="ColumnVector.GetValue{T}"/> returns (signed tinyint is stored as
/// <see cref="byte"/>, a compact decimal as <see cref="long"/>, string/binary as raw bytes), so a box
/// round-trips through <see cref="ColumnVector"/>/<see cref="MutableColumnVector"/> without a
/// representation change. <see cref="Compare"/> implements Spark's total order: signed integral,
/// <c>NaN</c>-largest float/double (via <see cref="ScalarReader.CompareDouble"/>), exact decimal, and
/// unsigned-byte-lexicographic string/binary — the same order the byte-sortable encoder realizes.
/// </summary>
internal static class ScalarValues
{
    /// <summary>Boxes the non-null lane at <paramref name="row"/> in its storage shape.</summary>
    internal static object ReadStorage(ColumnVector vector, int row) => vector.Type switch
    {
        BooleanType => vector.GetValue<bool>(row),
        ByteType => vector.GetValue<byte>(row),
        ShortType => vector.GetValue<short>(row),
        IntegerType or DateType => vector.GetValue<int>(row),
        LongType or TimestampType => vector.GetValue<long>(row),
        FloatType => vector.GetValue<float>(row),
        DoubleType => vector.GetValue<double>(row),
        DecimalType { IsCompact: true } => vector.GetValue<long>(row),
        DecimalType => vector.GetValue<Int128>(row),
        StringType or BinaryType => vector.GetBytes(row).ToArray(),
        _ => throw new UnsupportedTypeException(
            $"MIN/MAX has no storage box for type '{vector.Type.SimpleString}'."),
    };

    /// <summary>Appends a previously <see cref="ReadStorage"/>-boxed value back into <paramref name="dest"/>.</summary>
    internal static void AppendStorage(MutableColumnVector dest, object boxed)
    {
        switch (dest.Type)
        {
            case BooleanType:
                dest.AppendValue((bool)boxed);
                break;
            case ByteType:
                dest.AppendValue((byte)boxed);
                break;
            case ShortType:
                dest.AppendValue((short)boxed);
                break;
            case IntegerType or DateType:
                dest.AppendValue((int)boxed);
                break;
            case LongType or TimestampType:
                dest.AppendValue((long)boxed);
                break;
            case FloatType:
                dest.AppendValue((float)boxed);
                break;
            case DoubleType:
                dest.AppendValue((double)boxed);
                break;
            case DecimalType { IsCompact: true }:
                dest.AppendValue((long)boxed);
                break;
            case DecimalType:
                dest.AppendValue((Int128)boxed);
                break;
            case StringType or BinaryType:
                dest.AppendBytes((byte[])boxed);
                break;
            default:
                throw new UnsupportedTypeException(
                    $"MIN/MAX cannot emit type '{dest.Type.SimpleString}'.");
        }
    }

    /// <summary>
    /// Spark's total order over two storage-shape boxes of <paramref name="type"/>: negative when
    /// <paramref name="a"/> sorts first, positive when last, zero when equal. Tinyint compares signed,
    /// float/double treat <c>NaN</c> as greatest (and <c>-0.0 == +0.0</c>), and string/binary compare
    /// as unsigned bytes.
    /// </summary>
    internal static int Compare(DataType type, object a, object b) => type switch
    {
        BooleanType => ((bool)a).CompareTo((bool)b),
        ByteType => ((sbyte)(byte)a).CompareTo((sbyte)(byte)b),
        ShortType => ((short)a).CompareTo((short)b),
        IntegerType or DateType => ((int)a).CompareTo((int)b),
        LongType or TimestampType => ((long)a).CompareTo((long)b),
        FloatType => ScalarReader.CompareDouble((float)a, (float)b),
        DoubleType => ScalarReader.CompareDouble((double)a, (double)b),
        DecimalType { IsCompact: true } d => ScalarReader.CompareDecimal(
            new DecimalValue((long)a, d.Scale), new DecimalValue((long)b, d.Scale)),
        DecimalType d => ScalarReader.CompareDecimal(
            new DecimalValue((Int128)a, d.Scale), new DecimalValue((Int128)b, d.Scale)),
        StringType or BinaryType => ((byte[])a).AsSpan().SequenceCompareTo((byte[])b),
        _ => throw new UnsupportedTypeException(
            $"MIN/MAX cannot order type '{type.SimpleString}'."),
    };
}
