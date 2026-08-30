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
/// mapping (#676) is supported for the single-level surface: a <c>struct</c> is relabelled recursively
/// (<see cref="BuildDataSchema"/> physical relabel + <see cref="BuildFullBatch"/> typed inverse relabel);
/// <c>array</c>/<c>map</c> columns are relabelled at the top-level container only, their interior resolving
/// structurally.
/// </remarks>
internal static class ColumnMappingProjection
{
    /// <summary>
    /// The PHYSICAL name of each table-schema field, in field order: the declared
    /// <c>delta.columnMapping.physicalName</c> in <see cref="ColumnMappingMode.Name"/>, the field's own name
    /// in <see cref="ColumnMappingMode.None"/>. For a nested (<c>struct</c>) column this is the <b>top-level</b>
    /// container physical name; each interior struct child is relabelled structurally by
    /// <see cref="BuildDataSchema"/> (#676). <c>array</c>/<c>map</c> columns are relabelled only at the
    /// top-level container; their interior resolves structurally.
    /// </summary>
    /// <exception cref="DeltaProtocolException">A column-mapped field carries no physical name.</exception>
    public static string[] ResolvePhysicalNames(StructType tableSchema, ColumnMappingMode mode)
    {
        var names = new string[tableSchema.Count];
        for (int i = 0; i < tableSchema.Count; i++)
        {
            names[i] = ColumnMapping.PhysicalName(tableSchema[i], mode);
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

            // #676: relabel the top-level container to its physical name AND, for a struct, recursively
            // relabel each interior struct child to its own physical name (an array/map interior carries no
            // mapping and rides verbatim). In none mode / OPTIMIZE (physical == logical) the recursion is an
            // identity because no child carries a physicalName.
            dataFields.Add(new StructField(
                physicalNames[i], BuildPhysicalDataType(field.DataType, depth: 1), field.Nullable, field.Metadata));
        }

        return new StructType(dataFields);
    }

    // The maximum number of nested type levels the read-schema relabel / congruence recursions descend before
    // failing closed with a typed storage error, matching the reader's NestedParquetColumnReader.MaxNestedReadDepth
    // (= 64) and ColumnMapping.MaxNestedMappingDepth so a schema readable by the reader is also relabelable.
    // Checked BEFORE any descent (StackOverflow DoS guard, #866 866a).
    private const int MaxNestedProjectionDepth = 64;

    private static void EnsureProjectionDepth(int depth, string path)
    {
        if (depth > MaxNestedProjectionDepth)
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Column '{DiagnosticText.Sanitize(path)}': the schema nests deeper than the supported limit of "
                + $"{MaxNestedProjectionDepth} type levels; the read is rejected fail-closed.");
        }
    }

    // Recursively relabels a logical DataType to its physical shape for the READ data schema (#676 single-level,
    // extended to depth>1 by #866 866a): a struct relabels each child to its own delta.columnMapping.physicalName
    // (falling back to the logical name when absent — none mode / OPTIMIZE), carrying each child's metadata
    // through (so the id rides for id-mode leaves); an array/map interior recurses so an interior struct's
    // children are likewise relabelled to their physicalName (this is what puts the stable physicalName on a
    // nested container's group node, design §2.5). In none mode / OPTIMIZE (physical == logical) the recursion
    // is an identity because no child carries a physicalName. Depth is checked BEFORE descent (DoS guard).
    private static DataType BuildPhysicalDataType(DataType type, int depth)
    {
        EnsureProjectionDepth(depth, "<nested>");
        switch (type)
        {
            case StructType structType:
                var children = new List<StructField>(structType.Count);
                foreach (StructField child in structType)
                {
                    string childPhysical =
                        child.Metadata.TryGetString(ColumnMapping.PhysicalNameKey, out string? p) && p.Length > 0
                            ? p
                            : child.Name;
                    children.Add(new StructField(
                        childPhysical, BuildPhysicalDataType(child.DataType, depth + 1), child.Nullable, child.Metadata));
                }

                return new StructType(children);
            case ArrayType array:
                return new ArrayType(BuildPhysicalDataType(array.ElementType, depth + 1), array.ContainsNull);
            case MapType map:
                return new MapType(
                    BuildPhysicalDataType(map.KeyType, depth + 1),
                    BuildPhysicalDataType(map.ValueType, depth + 1),
                    map.ValueContainsNull);
            default:
                return type;
        }
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
                // #676: a data column is taken (no copy) from its physical ordinal, then — for a nested struct
                // read under a physical schema — re-typed back to its LOGICAL struct type (physical→logical
                // child-name substitution), zero-copy. Array/map/scalar columns carry no relabellable field
                // names and pass through unchanged.
                columns[i] = RelabelDataColumn(
                    dataBatch.Column(dataOrdinal), tableSchema[i].DataType, tableSchema[i].Name);
                continue;
            }

            StructField field = tableSchema[i];
            partitionValues.TryGetValue(physicalNames[i], out string? value);
            columns[i] = DeltaReadEncoding.BuildConstantColumn(field.DataType, value, rowCount);
        }

        return new ManagedColumnBatch(tableSchema, columns, rowCount);
    }

    // Re-types a physically-read column back to its LOGICAL type (#676 single-level, extended to depth>1 by
    // #866 866a — the name-mode read-exit inverse relabel). A nested STRUCT/ARRAY/MAP carrying physical field
    // names has them substituted for the logical names, zero-copy, recursively (an array<struct>/map<*,struct>
    // interior struct is relabelled too). A scalar column, or a nested type already carrying the logical type
    // (none mode), passes through unchanged. The physical type is validated for ORDERED congruence against the
    // logical type (equal count, same order, per-child DataType congruent recursively, equal nullability) and
    // fails closed as a typed DeltaStorageException.SchemaMismatch (sanitized path) BEFORE the batch is
    // constructed — never a bare ArgumentException from the zero-copy re-type, and never echoing a raw physical
    // field name.
    private static ColumnVector RelabelDataColumn(ColumnVector column, DataType logicalType, string logicalName)
    {
        // Only a nested column can carry physical field names needing substitution; a scalar passes through.
        // When the read type already equals logical (none mode / no rename), nothing to relabel.
        if (logicalType is not (StructType or ArrayType or MapType) || column.Type.Equals(logicalType))
        {
            return column;
        }

        AssertDataTypeCongruent(column.Type, logicalType, logicalName, depth: 1);
        return RelabelColumn(column, logicalType);
    }

    // Zero-copy re-type dispatch onto the logical nested type. Each vector's RelabelTo shares its child
    // buffers / validity / window and recurses into nested interiors, changing only the logical TYPE (field
    // names + per-field metadata). Congruence (incl. the depth bound) is validated by AssertDataTypeCongruent
    // BEFORE this is reached, so the subsequent vector RelabelTo recursion is likewise depth-bounded.
    private static ColumnVector RelabelColumn(ColumnVector column, DataType logicalType) => column switch
    {
        StructColumnVector s when logicalType is StructType st => s.RelabelTo(st),
        ListColumnVector l when logicalType is ArrayType at => l.RelabelTo(at),
        MapColumnVector m when logicalType is MapType mt => m.RelabelTo(mt),
        _ => column,
    };

    // Validates that a physically-read struct type is congruent with its logical struct type — the same field
    // count, in the same order, each child's DataType congruent (recursively, name-independently for a nested
    // struct/array/map; DataType.Equals for a scalar), and each child's nullability equal — so a positional
    // zero-copy re-type is sound. A COUNT-ONLY check is deliberately avoided: it would silently relabel a
    // reordered or type-mismatched physical struct. Fail closed with a sanitized-path SchemaMismatch.
    private static void AssertStructCongruent(DataType physical, StructType logical, string path, int depth)
    {
        if (physical is not StructType physicalStruct || physicalStruct.Count != logical.Count)
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Column '{DiagnosticText.Sanitize(path)}': the physically-read struct shape does not match the "
                + "logical schema (differing field count or kind); the read is rejected fail-closed.");
        }

        for (int i = 0; i < logical.Count; i++)
        {
            StructField physicalChild = physicalStruct[i];
            StructField logicalChild = logical[i];
            if (physicalChild.Nullable != logicalChild.Nullable)
            {
                throw DeltaStorageException.SchemaMismatch(
                    $"Column '{DiagnosticText.Sanitize(path + "." + logicalChild.Name)}': the physically-read struct "
                    + "child nullability does not match the logical schema; the read is rejected fail-closed.");
            }

            AssertDataTypeCongruent(physicalChild.DataType, logicalChild.DataType, path + "." + logicalChild.Name, depth + 1);
        }
    }

    // Validates a physically-read DataType is congruent with its logical DataType at depth>1 (#866 866a): a
    // struct recurses per child (AssertStructCongruent); an array/map recurses into its element/key/value
    // (preserving containsNull); a scalar requires DataType.Equals. Depth is checked BEFORE descent (DoS
    // guard). Fail closed with a sanitized-path SchemaMismatch — never echoing a raw physical field name.
    private static void AssertDataTypeCongruent(DataType physical, DataType logical, string path, int depth)
    {
        EnsureProjectionDepth(depth, path);
        switch (logical)
        {
            case StructType logicalStruct:
                AssertStructCongruent(physical, logicalStruct, path, depth);
                break;
            case ArrayType logicalArray:
                if (physical is not ArrayType physicalArray || physicalArray.ContainsNull != logicalArray.ContainsNull)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{DiagnosticText.Sanitize(path)}': the physically-read array shape does not match the "
                        + "logical schema (kind or element-nullability); the read is rejected fail-closed.");
                }

                AssertDataTypeCongruent(physicalArray.ElementType, logicalArray.ElementType, path + ".element", depth + 1);
                break;
            case MapType logicalMap:
                if (physical is not MapType physicalMap || physicalMap.ValueContainsNull != logicalMap.ValueContainsNull)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{DiagnosticText.Sanitize(path)}': the physically-read map shape does not match the "
                        + "logical schema (kind or value-nullability); the read is rejected fail-closed.");
                }

                AssertDataTypeCongruent(physicalMap.KeyType, logicalMap.KeyType, path + ".key", depth + 1);
                AssertDataTypeCongruent(physicalMap.ValueType, logicalMap.ValueType, path + ".value", depth + 1);
                break;
            default:
                if (!physical.Equals(logical))
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{DiagnosticText.Sanitize(path)}': the physically-read type does not match the "
                        + "logical schema; the read is rejected fail-closed.");
                }

                break;
        }
    }
}
