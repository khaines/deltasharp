using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Builds the canonical, injective <b>partition key</b> for a file's partition values over a table's
/// partition columns, so two files that belong to the same partition map to the same key regardless of
/// dictionary identity or insertion order (design §2.11). This is the <b>exact-match</b> keying that
/// destructive, partition-scoped operations rely on:
///
/// <list type="bullet">
/// <item>a dynamic partition <b>overwrite</b> (<see cref="DeltaTableWriter"/>) removes a prior active file
/// <b>iff</b> its key exactly equals a touched partition's key — never a read-oriented over-approximation
/// (<see cref="Snapshot.PruneFiles"/> keeps any file it cannot prove non-matching, which for a destructive
/// selection would tombstone a file in an untouched partition: silent data loss, council #486 R1);</item>
/// <item><b>OPTIMIZE</b>/compaction (<see cref="DeltaOptimize"/>) groups a partition's small files by this
/// key so a compaction group only ever combines files that share a partition.</item>
/// </list>
///
/// <para>The key is stable and injective: NUL separators keep values that concatenate ambiguously
/// (e.g. <c>"a","b"</c> vs <c>"ab"</c>) distinct, a length prefix disambiguates further, and a null value
/// is a distinct sentinel (the "absent value ≡ null/default partition" semantics Hive/Delta use). A file
/// missing a partition-column key coerces that column to null, so it belongs to the null partition — a
/// bounded, deterministic behavior, never an untouched-partition match.</para>
/// </summary>
internal static class PartitionKeyBuilder
{
    /// <summary>
    /// Builds the canonical key for <paramref name="partitionValues"/> over <paramref name="partitionColumns"/>.
    /// An unpartitioned table (no partition columns) has a single partition, so its key is the empty string.
    /// This entry sorts <paramref name="partitionColumns"/> on every call; a hot loop that builds many keys
    /// over the SAME columns should <see cref="SortColumns"/> once and call <see cref="BuildSorted"/>.
    /// </summary>
    public static string Build(
        ImmutableSortedDictionary<string, string?> partitionValues, ImmutableArray<string> partitionColumns) =>
        BuildSorted(partitionValues, SortColumns(partitionColumns));

    /// <summary>
    /// Sorts <paramref name="partitionColumns"/> into the ordinal order <see cref="BuildSorted"/> expects,
    /// once, so a per-row hot path (e.g. <c>ColumnBatchPartitioner</c>) can build a key per row without
    /// re-sorting the column list on every call. An empty/default input yields an empty array.
    /// </summary>
    public static ImmutableArray<string> SortColumns(ImmutableArray<string> partitionColumns) =>
        partitionColumns.IsDefaultOrEmpty
            ? ImmutableArray<string>.Empty
            : partitionColumns.Sort(StringComparer.Ordinal);

    /// <summary>
    /// Builds the canonical key from a partition-column list ALREADY sorted by <see cref="SortColumns"/>
    /// (ordinal). Identical output to <see cref="Build"/>, but does no per-call re-sort — for the per-row
    /// staging path that keys thousands of rows over one fixed column set.
    /// </summary>
    public static string BuildSorted(
        ImmutableSortedDictionary<string, string?> partitionValues, ImmutableArray<string> sortedPartitionColumns)
    {
        if (sortedPartitionColumns.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (string column in sortedPartitionColumns)
        {
            partitionValues.TryGetValue(column, out string? value);
            sb.Append(column.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(column).Append('\u0000');
            sb.Append(value is null ? "\u0001null" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value);
            sb.Append('\u0000');
        }

        return sb.ToString();
    }
}
