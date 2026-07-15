using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Boxes one scalar lane into the binary-row CLR shape <see cref="DeltaSharp.Engine.RowFormat.RowData"/>
/// and <see cref="DeltaSharp.Engine.RowFormat.ByteSortableEncoding"/> expect: <c>byte→sbyte</c>,
/// <c>decimal→Int128</c> (unscaled), <c>string→string</c>, <c>binary→byte[]</c>, the rest 1:1. Shared
/// by every key path that feeds the byte-sortable encoder (grouping, join, exchange, sort), so all
/// four operators encode keys identically. Returns <see langword="null"/> for SQL <c>NULL</c>.
/// </summary>
internal static class KeyBoxing
{
    /// <summary>Boxes logical row <paramref name="row"/> of <paramref name="vector"/>, or null when the lane is null.</summary>
    /// <exception cref="UnsupportedTypeException">The lane type is not byte-sortable.</exception>
    internal static object? ToRowDataValue(ColumnVector vector, int row)
    {
        if (vector.IsNull(row))
        {
            return null;
        }

        return vector.Type switch
        {
            BooleanType => vector.GetValue<bool>(row),
            ByteType => (sbyte)vector.GetValue<byte>(row),
            ShortType => vector.GetValue<short>(row),
            IntegerType or DateType => vector.GetValue<int>(row),
            LongType or TimestampType or TimestampNtzType => vector.GetValue<long>(row),
            FloatType => vector.GetValue<float>(row),
            DoubleType => vector.GetValue<double>(row),
            DecimalType { IsCompact: true } => (Int128)vector.GetValue<long>(row),
            DecimalType => vector.GetValue<Int128>(row),
            StringType => Encoding.UTF8.GetString(vector.GetBytes(row)),
            BinaryType => vector.GetBytes(row).ToArray(),
            _ => throw new UnsupportedTypeException(
                $"Key type '{vector.Type.SimpleString}' is not byte-sortable."),
        };
    }
}
