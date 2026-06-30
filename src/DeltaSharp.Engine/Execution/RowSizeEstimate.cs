using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// A coarse byte estimate for a materialized row, used only by the blocking operators' memory
/// reservation (aggregate hash table, sort buffer, join build table). It is an accounting heuristic,
/// not a layout: fixed-width types use their physical width and the flat <see cref="Width(DataType)"/>
/// charges variable-width types (string/binary) a nominal 16 bytes. Because a blocking operator
/// buffers the <i>full</i> value of every variable-width column it retains, the per-row reservation
/// adds <see cref="VariableWidthBytes(ColumnVector[], int)"/> — the true byte length of those columns
/// at the buffered row — on top of the flat estimate, so a wide string/binary payload cannot bypass
/// the budget. Every buffered row reserves before it is stored, and a refusal raises
/// <see cref="ExecutionMemoryException"/>.
/// </summary>
internal static class RowSizeEstimate
{
    /// <summary>
    /// The summed <i>true</i> byte length of the variable-width (string/binary) values at logical
    /// <paramref name="row"/> across <paramref name="columns"/>; fixed-width and null lanes add nothing.
    /// Blocking operators add this to the flat <see cref="Bytes(StructType)"/> so the reservation
    /// reflects the full var-width payload they buffer, not the nominal 16-byte placeholder.
    /// </summary>
    internal static long VariableWidthBytes(ColumnVector[] columns, int row)
    {
        long total = 0;
        for (int c = 0; c < columns.Length; c++)
        {
            ColumnVector column = columns[c];
            if (column.Type is StringType or BinaryType && !column.IsNull(row))
            {
                total += column.GetBytes(row).Length;
            }
        }

        return total;
    }

    /// <summary>The summed per-row estimate over every field of <paramref name="schema"/>.</summary>
    internal static long Bytes(StructType schema)
    {
        long total = 0;
        for (int i = 0; i < schema.Count; i++)
        {
            total += Width(schema[i].DataType);
        }

        return total;
    }

    /// <summary>The per-value estimate for <paramref name="type"/> (variable-width counts as 16).</summary>
    internal static long Width(DataType type) => type switch
    {
        BooleanType or ByteType => 1,
        ShortType => 2,
        IntegerType or DateType or FloatType => 4,
        LongType or TimestampType or DoubleType => 8,
        DecimalType { IsCompact: true } => 8,
        DecimalType => 16,
        _ => 16,
    };
}
