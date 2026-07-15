using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

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
    /// The per-entry overhead of a managed hash table (the <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
    /// the aggregate uses for groups and the join uses for the build table) charged once per newly
    /// discovered group / distinct build key (STORY-03.6.1, deferral (a)). It covers the bucket slot,
    /// the entry record (hash code, next index, key reference, value), and amortized headroom for the
    /// array-doubling transient the parallel state arrays incur as they grow — so the reserved figure
    /// bounds the real peak (which otherwise ran ~1.5–3× over) in bytes, not just row count. The key
    /// <i>bytes</i> themselves are charged separately by each operator's <c>+ key.Length</c> term.
    /// </summary>
    internal const long HashTableEntryBytes = 64;

    /// <summary>
    /// The overhead of a per-key <see cref="System.Collections.Generic.List{T}"/> build-index, charged
    /// once when a join build key is first seen: the list object header plus its initial backing array.
    /// Subsequent ordinals appended to an existing list are charged <see cref="ListAppendBytes"/> each.
    /// </summary>
    internal const long ListHeaderBytes = 48;

    /// <summary>The amortized per-append cost of growing a join build-index <c>List&lt;int&gt;</c> (int slot + doubling headroom).</summary>
    internal const long ListAppendBytes = sizeof(int) * 2;

    /// <summary>The per-build-row cost of the join's <c>_matched bool[]</c> flag (STORY-03.6.1, deferral (a)).</summary>
    internal const long MatchFlagBytes = 1;

    /// <summary>The per-buffered-row cost of the sort's permutation <c>int[]</c> ordinal slot (with doubling headroom).</summary>
    internal const long PermutationEntryBytes = sizeof(int) * 2;

    /// <summary>
    /// The <i>true</i> byte length of the variable-width (string/binary) value at <paramref name="row"/>
    /// of <paramref name="column"/>; fixed-width and null lanes contribute nothing. Output paths charge
    /// this on the values they copy so a wide payload cannot bypass the byte bound (STORY-03.6.1,
    /// deferral (c)), symmetric with the input-side <see cref="VariableWidthBytes(ColumnVector[], int)"/>.
    /// </summary>
    internal static long VariableWidthBytes(ColumnVector column, int row) =>
        column.Type is StringType or BinaryType && !column.IsNull(row) ? column.GetBytes(row).Length : 0;

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
            total += VariableWidthBytes(columns[c], row);
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
        LongType or TimestampType or TimestampNtzType or DoubleType => 8,
        DecimalType { IsCompact: true } => 8,
        DecimalType => 16,
        _ => 16,
    };
}
