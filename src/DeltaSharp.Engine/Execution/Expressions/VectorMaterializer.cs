using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Writes computed lanes into a freshly allocated output <see cref="MutableColumnVector"/> and
/// materializes a (possibly selected) view into a contiguous vector (STORY-03.4.1). Append-only and
/// sequential: every kernel produces its output in logical row order, so a computed column is always
/// contiguous and exposes the <see cref="ColumnVector.GetValues{T}"/> fast path downstream.
/// </summary>
internal static class VectorMaterializer
{
    /// <summary>
    /// Copies <paramref name="view"/>'s <paramref name="length"/> logical rows (values and validity)
    /// into a new contiguous vector of the same type — used to gather a selection-bearing column
    /// reference into a projection's selection-free output batch.
    /// </summary>
    public static MutableColumnVector Materialize(ColumnVector view, int length)
    {
        MutableColumnVector result = ColumnVectors.Create(view.Type, length);
        for (int i = 0; i < length; i++)
        {
            if (view.IsNull(i))
            {
                result.AppendNull();
            }
            else
            {
                CopyValue(result, view, i);
            }
        }

        return result;
    }

    /// <summary>Appends one non-null lane from <paramref name="source"/> to <paramref name="dest"/> in its storage shape.</summary>
    public static void CopyValue(MutableColumnVector dest, ColumnVector source, int index)
    {
        switch (dest.Type)
        {
            case BooleanType:
                dest.AppendValue(source.GetValue<bool>(index));
                break;
            case ByteType:
                dest.AppendValue(source.GetValue<byte>(index));
                break;
            case ShortType:
                dest.AppendValue(source.GetValue<short>(index));
                break;
            case IntegerType or DateType:
                dest.AppendValue(source.GetValue<int>(index));
                break;
            case LongType or TimestampType:
                dest.AppendValue(source.GetValue<long>(index));
                break;
            case FloatType:
                dest.AppendValue(source.GetValue<float>(index));
                break;
            case DoubleType:
                dest.AppendValue(source.GetValue<double>(index));
                break;
            case DecimalType { IsCompact: true }:
                dest.AppendValue(source.GetValue<long>(index));
                break;
            case DecimalType:
                dest.AppendValue(source.GetValue<Int128>(index));
                break;
            case StringType or BinaryType:
                dest.AppendBytes(source.GetBytes(index));
                break;
            default:
                throw new UnsupportedTypeException(
                    $"No materialization is defined for type '{dest.Type.SimpleString}'.");
        }
    }

    /// <summary>Appends an integral result to its storage shape (signed <c>tinyint</c> reinterprets through <see cref="byte"/>).</summary>
    public static void AppendIntegral(MutableColumnVector dest, long value)
    {
        switch (dest.Type)
        {
            case ByteType:
                dest.AppendValue(unchecked((byte)value));
                break;
            case ShortType:
                dest.AppendValue((short)value);
                break;
            case IntegerType:
                dest.AppendValue((int)value);
                break;
            case LongType:
                dest.AppendValue(value);
                break;
            default:
                throw new UnsupportedTypeException(
                    $"Type '{dest.Type.SimpleString}' is not an integral output.");
        }
    }

    /// <summary>Appends a decimal result mantissa to its storage shape (compact <see cref="long"/> or wide <see cref="Int128"/>).</summary>
    public static void AppendDecimal(MutableColumnVector dest, Int128 unscaled)
    {
        if (dest.Type is DecimalType { IsCompact: true })
        {
            dest.AppendValue((long)unscaled);
        }
        else
        {
            dest.AppendValue(unscaled);
        }
    }
}
