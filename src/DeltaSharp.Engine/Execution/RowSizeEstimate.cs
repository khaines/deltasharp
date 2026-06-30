using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// A coarse byte estimate for a materialized row, used only by the blocking operators' memory
/// reservation (aggregate hash table, sort buffer, join build table). It is an accounting heuristic,
/// not a layout: fixed-width types use their physical width and variable-width types (string/binary)
/// count as a flat 16 bytes. The reservation it feeds is honest about <i>growth</i> — every buffered
/// row reserves before it is stored, and a refusal raises <see cref="ExecutionMemoryException"/> — so
/// the estimate only needs to scale with row count, which it does.
/// </summary>
internal static class RowSizeEstimate
{
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
