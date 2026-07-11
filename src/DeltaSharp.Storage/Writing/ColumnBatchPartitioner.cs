using System.Collections.Immutable;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Writing;

/// <summary>
/// Splits a sequence of full-schema <see cref="ColumnBatch"/>es into one group per distinct partition
/// (keyed by the values of the table's partition columns), projecting each group's data down to the
/// <b>non-partition</b> columns — the exact shape a Delta Parquet data file stores (Delta keeps partition
/// columns only in <c>add.partitionValues</c> + the directory path, never in the file). An unpartitioned
/// table yields a single group with an empty partition key. This is the columnar half of the Delta write
/// facade's "stage a batch, partitioned by the partition columns" step (#487).
/// </summary>
internal static class ColumnBatchPartitioner
{
    // Poll cancellation every 1024 rows in the sync partition loop (mask 1023), matching
    // LocalRelationBatches.Build / RowMaterializer so the write-staging path is bounded the same way.
    private const int CancellationPollMask = 1023;

    /// <summary>A partitioned slice of the input: the (possibly-null) value of every partition column, and
    /// the data-column-only batches that belong to that partition.</summary>
    internal sealed record PartitionGroup(
        ImmutableSortedDictionary<string, string?> PartitionValues,
        StructType DataSchema,
        IReadOnlyList<ColumnBatch> Batches);

    /// <summary>Partitions <paramref name="batches"/> over <paramref name="partitionColumns"/>.</summary>
    /// <param name="fullSchema">The full write schema (partition + data columns).</param>
    /// <param name="partitionColumns">The partition column names, in declaration order (a subset of
    /// <paramref name="fullSchema"/>).</param>
    /// <param name="batches">The full-schema batches to split.</param>
    /// <param name="cancellationToken">Cancels this sync CPU loop; polled every ~1024 rows so a large
    /// staging batch honors cancel/timeout (matching <c>LocalRelationBatches.Build</c>).</param>
    /// <returns>One group per non-empty partition (an empty result when the input has no rows).</returns>
    public static IReadOnlyList<PartitionGroup> Partition(
        StructType fullSchema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<ColumnBatch> batches,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fullSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        ArgumentNullException.ThrowIfNull(batches);

        var partitionOrdinals = new int[partitionColumns.Count];
        var partitionSet = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < partitionColumns.Count; i++)
        {
            int ordinal = fullSchema.IndexOf(partitionColumns[i]);
            if (ordinal < 0)
            {
                throw new ArgumentException(
                    $"Partition column '{partitionColumns[i]}' is not present in the write schema '{fullSchema.SimpleString}'.",
                    nameof(partitionColumns));
            }

            partitionOrdinals[i] = ordinal;
            partitionSet.Add(partitionColumns[i]);
        }

        // The data schema is the full schema minus the partition columns, preserving field order.
        var dataFields = new List<StructField>(fullSchema.Count);
        var dataOrdinals = new List<int>(fullSchema.Count);
        for (int c = 0; c < fullSchema.Count; c++)
        {
            if (!partitionSet.Contains(fullSchema[c].Name))
            {
                dataFields.Add(fullSchema[c]);
                dataOrdinals.Add(c);
            }
        }

        var dataSchema = new StructType(dataFields);

        // Hoist the per-batch/per-row invariants OUT of the hot loops: the partition-column array (was
        // re-allocated via ToImmutableArray() inside the per-row loop) and its ordinal-sorted order (so
        // PartitionKeyBuilder does not re-OrderBy per row). Both are fixed for the whole call.
        ImmutableArray<string> partitionColumnArray = partitionColumns.ToImmutableArray();
        ImmutableArray<string> sortedPartitionColumns = PartitionKeyBuilder.SortColumns(partitionColumnArray);

        // Preserve first-seen partition order for deterministic output.
        var groups = new Dictionary<string, GroupBuilder>(StringComparer.Ordinal);
        var order = new List<string>();

        long processedRows = 0;
        foreach (ColumnBatch batch in batches)
        {
            int rows = batch.LogicalRowCount;
            if (rows == 0)
            {
                continue;
            }

            ColumnVector[] partitionColumnViews = new ColumnVector[partitionOrdinals.Length];
            for (int i = 0; i < partitionOrdinals.Length; i++)
            {
                partitionColumnViews[i] = batch.SelectedColumn(partitionOrdinals[i]);
            }

            ColumnVector[] dataColumnViews = new ColumnVector[dataOrdinals.Count];
            for (int i = 0; i < dataOrdinals.Count; i++)
            {
                dataColumnViews[i] = batch.SelectedColumn(dataOrdinals[i]);
            }

            for (int r = 0; r < rows; r++)
            {
                if ((processedRows++ & CancellationPollMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                ImmutableSortedDictionary<string, string?> partitionValues =
                    BuildPartitionValues(partitionColumns, partitionColumnViews, r);
                string key = PartitionKeyBuilder.BuildSorted(partitionValues, sortedPartitionColumns);

                if (!groups.TryGetValue(key, out GroupBuilder? builder))
                {
                    builder = new GroupBuilder(partitionValues, dataSchema, rows);
                    groups[key] = builder;
                    order.Add(key);
                }

                for (int c = 0; c < dataColumnViews.Length; c++)
                {
                    DeltaWriteEncoding.AppendValue(builder.Columns[c], dataColumnViews[c], r);
                }

                builder.RowCount++;
            }
        }

        var result = new List<PartitionGroup>(order.Count);
        foreach (string key in order)
        {
            result.Add(groups[key].Build());
        }

        return result;
    }

    private static ImmutableSortedDictionary<string, string?> BuildPartitionValues(
        IReadOnlyList<string> partitionColumns, ColumnVector[] views, int row)
    {
        if (partitionColumns.Count == 0)
        {
            return ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);
        }

        ImmutableSortedDictionary<string, string?>.Builder builder =
            ImmutableSortedDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        for (int i = 0; i < partitionColumns.Count; i++)
        {
            builder[partitionColumns[i]] = DeltaWriteEncoding.FormatPartitionValue(views[i], row);
        }

        return builder.ToImmutable();
    }

    private sealed class GroupBuilder
    {
        public GroupBuilder(
            ImmutableSortedDictionary<string, string?> partitionValues, StructType dataSchema, int capacity)
        {
            PartitionValues = partitionValues;
            DataSchema = dataSchema;
            Columns = ColumnVectors.CreateForSchema(dataSchema, Math.Max(capacity, 1));
        }

        public ImmutableSortedDictionary<string, string?> PartitionValues { get; }

        public StructType DataSchema { get; }

        public MutableColumnVector[] Columns { get; }

        public int RowCount { get; set; }

        public PartitionGroup Build() =>
            new(PartitionValues, DataSchema, new ColumnBatch[]
            {
                new ManagedColumnBatch(DataSchema, Columns, RowCount),
            });
    }
}
