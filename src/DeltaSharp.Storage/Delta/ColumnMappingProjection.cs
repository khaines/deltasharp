using System.Collections.Immutable;
using System.Globalization;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The single, shared <b>column-mapping projection</b> seam: it translates a logical Delta table schema into
/// the physical shape a Parquet data file actually stores under column mapping, and relabels a physically-read
/// batch back into a full-schema LOGICAL batch. Every read-time consumer — the scan path
/// (<see cref="DeltaReadSource"/>) and merge-on-read <see cref="DeltaDelete"/> — MUST project through this one
/// type so they resolve/relabel columns <b>identically</b> (a divergence would let one path serve a column's
/// value under another column's logical name — a silent misread — or delete the wrong rows). In
/// <see cref="ColumnMappingMode.None"/> physical name == logical name, so every method degrades to the exact
/// prior (pre-column-mapping) behavior.
/// </summary>
/// <remarks>
/// Extracted (#529) from the previously-duplicated per-consumer copies to remove drift risk. Nested column
/// mapping is unsupported in this build: <see cref="ResolvePhysicalNames"/> rejects a nested top-level column
/// fail-closed under <see cref="ColumnMappingMode.Name"/> rather than risk mis-associating columns.
/// </remarks>
internal static class ColumnMappingProjection
{
    /// <summary>
    /// The PHYSICAL name of each table-schema field, in field order: the declared
    /// <c>delta.columnMapping.physicalName</c> in <see cref="ColumnMappingMode.Name"/>, the field's own name
    /// in <see cref="ColumnMappingMode.None"/>. A nested (struct/array/map) top-level column under name
    /// mapping is rejected fail-closed here — nested column mapping is unsupported in this build (design
    /// §2.9/§2.12.3), and only top-level (leaf) columns are mapped — rather than fail later in the Parquet
    /// reader or risk a wrong relabel.
    /// </summary>
    /// <exception cref="DeltaProtocolException">A nested top-level column is present under name mapping.</exception>
    public static string[] ResolvePhysicalNames(StructType tableSchema, ColumnMappingMode mode)
    {
        var names = new string[tableSchema.Count];
        for (int i = 0; i < tableSchema.Count; i++)
        {
            StructField field = tableSchema[i];
            if (mode != ColumnMappingMode.None && field.DataType is StructType or ArrayType or MapType)
            {
                throw DeltaProtocolException.Unsupported(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column '{field.Name}' is a nested ({field.DataType.TypeName}) type; nested column "
                        + $"mapping is unsupported in this build (design §2.9/§2.12.3). Only top-level (leaf) "
                        + $"columns are supported under column mapping (name or id mode)."));
            }

            names[i] = ColumnMapping.PhysicalName(field, mode);
        }

        return names;
    }

    /// <summary>
    /// The PHYSICAL data schema: the table schema minus the partition columns (Delta never stores partition
    /// columns inside the Parquet data file — their values live on the <c>add</c> action), with each
    /// remaining field named by its PHYSICAL name (order-preserving) — the exact shape a Delta Parquet data
    /// file stores. Partition MEMBERSHIP is decided by the LOGICAL field name against
    /// <c>metaData.partitionColumns</c> (which holds LOGICAL names under name mode — verified against the
    /// Spark golden <c>dv-with-columnmapping</c>), decoupled from the partition VALUE KEY which stays PHYSICAL
    /// (looked up from <c>add.partitionValues</c> in <see cref="BuildFullBatch"/>). In none mode logical ==
    /// physical, so this is exactly the prior behavior. Each retained field carries its original
    /// <see cref="StructField.Metadata"/> through (only the name is relabeled to physical), so the OPTIMIZE
    /// compaction path — the one consumer that re-serializes this schema into a written data-file footer
    /// (<c>org.apache.spark.sql.parquet.row.metadata</c>) — preserves per-field metadata (column comments,
    /// generated/identity config). The read-side consumers (the <c>DeltaReadSource</c> scan and the
    /// merge-on-read DELETE predicate projection, which writes a deletion vector rather than a rewritten data
    /// file) never consult that metadata, so carrying it is inert there.
    /// </summary>
    public static StructType BuildDataSchema(
        StructType tableSchema, string[] physicalNames, ImmutableArray<string> partitionColumns)
    {
        var partitionSet = partitionColumns.IsDefaultOrEmpty
            ? null
            : partitionColumns.ToImmutableHashSet(StringComparer.Ordinal);

        var dataFields = new List<StructField>(tableSchema.Count);
        for (int i = 0; i < tableSchema.Count; i++)
        {
            StructField field = tableSchema[i];
            if (partitionSet is not null && partitionSet.Contains(field.Name))
            {
                continue;
            }

            dataFields.Add(new StructField(physicalNames[i], field.DataType, field.Nullable, field.Metadata));
        }

        return new StructType(dataFields);
    }

    /// <summary>
    /// For each table-schema field, its ordinal in the physical (Parquet) <paramref name="dataSchema"/>
    /// matched by the field's PHYSICAL name, or <c>-1</c> for a partition column (const/null-filled from the
    /// add's <c>partitionValues</c> in <see cref="BuildFullBatch"/>). Matching by physical name (not position)
    /// keeps the mapping correct under a scrambled/evolved physical schema.
    /// </summary>
    public static int[] MapDataOrdinals(string[] physicalNames, StructType dataSchema)
    {
        var map = new int[physicalNames.Length];
        for (int i = 0; i < physicalNames.Length; i++)
        {
            map[i] = dataSchema.IndexOf(physicalNames[i]);
        }

        return map;
    }

    /// <summary>
    /// Assembles one full-schema LOGICAL batch, RELABELED from the physically-read <paramref name="dataBatch"/>:
    /// a data column is taken (no copy) from its physical ordinal; a partition column (ordinal <c>-1</c>) is
    /// const/null-filled from <c>add.partitionValues</c> keyed by the column's PHYSICAL name — how Delta records
    /// partition-value keys under column mapping. The output batch carries the LOGICAL table schema, so a column
    /// whose physical name differs from its logical name (a renamed column) reads through under its new logical
    /// name from UNCHANGED Parquet data (STORY-05.4.3 AC1). In none mode physical == logical, so this is exactly
    /// the prior behavior.
    /// </summary>
    public static ColumnBatch BuildFullBatch(
        AddFileAction add,
        StructType tableSchema,
        string[] physicalNames,
        int[] dataOrdinalByField,
        ColumnBatch dataBatch)
        => BuildFullBatch(add.PartitionValues, tableSchema, physicalNames, dataOrdinalByField, dataBatch);

    /// <summary>
    /// The partition-values overload of <see cref="BuildFullBatch(AddFileAction,StructType,string[],int[],ColumnBatch)"/>:
    /// relabels the physically-read <paramref name="dataBatch"/> into a full-schema LOGICAL batch, hydrating each
    /// partition column (ordinal <c>-1</c>) from <paramref name="partitionValues"/> keyed by the column's PHYSICAL
    /// name. It takes the partition-values map directly rather than an <see cref="AddFileAction"/> so the Change
    /// Data Feed read door can reuse the identical relabel/hydrate for a <c>cdc</c> (<see cref="AddCdcFileAction"/>)
    /// or <c>remove</c> action — whose <c>partitionValues</c> are keyed by physical name in every mapping mode,
    /// exactly like <c>add.partitionValues</c>.
    /// </summary>
    public static ColumnBatch BuildFullBatch(
        ImmutableSortedDictionary<string, string?> partitionValues,
        StructType tableSchema,
        string[] physicalNames,
        int[] dataOrdinalByField,
        ColumnBatch dataBatch)
    {
        int rowCount = dataBatch.RowCount;
        var columns = new ColumnVector[tableSchema.Count];
        for (int i = 0; i < tableSchema.Count; i++)
        {
            int dataOrdinal = dataOrdinalByField[i];
            if (dataOrdinal >= 0)
            {
                columns[i] = dataBatch.Column(dataOrdinal);
                continue;
            }

            StructField field = tableSchema[i];
            partitionValues.TryGetValue(physicalNames[i], out string? value);
            columns[i] = DeltaReadEncoding.BuildConstantColumn(field.DataType, value, rowCount);
        }

        return new ManagedColumnBatch(tableSchema, columns, rowCount);
    }
}
