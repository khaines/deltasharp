using DeltaSharp.Types;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Compares a write's schema against a Delta table's current schema and either <b>accepts</b> it,
/// <b>evolves</b> the table schema by an additive, non-destructive change, or <b>rejects</b> it with a
/// deterministic <see cref="DeltaSchemaMismatchException"/> — the storage-side schema
/// enforcement/evolution rule set (STORY-05.4.2; design §2.12.2). It is a pure, side-effect-free function
/// consumed by <see cref="DeltaTableWriter"/> <b>before</b> any action is built, so a rejected write never
/// publishes state (AC1 reject-before-commit) and an accepted evolution produces the merged schema the
/// writer folds into a single atomic <c>metaData</c>+adds commit (AC2).
///
/// <para><b>The only evolution is additive.</b> <c>Reconcile</c> <b>never</b> returns a schema whose column
/// types differ from the table's — the only merged schema it ever produces adds a <b>new nullable column</b>
/// (top-level or nested) under <see cref="SchemaEvolutionMode.AddNewColumns"/>. Every other difference is
/// either a no-op (returns <see langword="null"/>) or a rejection. This is fail-closed: no accepted write
/// can leave the table in a state its own reader cannot read back.</para>
///
/// <para><b>Type widening is fail-closed (deferred, #495).</b> Widening the <i>logical</i> schema
/// (<c>int→long</c>, <c>float→double</c>, <c>date→timestamp</c>, growing a <c>decimal</c>) without also
/// registering Delta's <c>typeWidening</c> table feature and its per-field widening metadata makes the
/// table's existing Parquet files <b>unreadable even by DeltaSharp</b>:
/// <c>ParquetFileReader.ValidateFileField</c> does an exact physical-type match, so an <c>Int32</c> file
/// read under a widened <c>long</c> schema throws <c>SchemaMismatch</c>. So <b>no</b> type change is ever
/// applied. The <see cref="IsPermittedWidening"/> classifier is retained only to produce a better error: a
/// differing type pair that <i>would</i> be a lossless widening is rejected as
/// <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/> (naming the deferred feature), and every
/// other differing type as <see cref="DeltaSchemaMismatchKind.IncompatibleType"/>. (<c>date→timestamp</c> is
/// likewise rejected: Delta only sanctions <c>date→timestamp_ntz</c>, which needs a not-yet-existing NTZ
/// type — #495.)</para>
///
/// <para><b>Compatibility rules (deterministic, total, case-sensitive; AC3).</b> Fields are matched
/// <b>by name</b> (case-sensitive / ordinal, matching <see cref="StructType"/>), so column reordering is
/// not a change. For each matched column and, recursively, each nested <c>struct</c> field,
/// <c>array</c> element, and <c>map</c> key/value:</para>
/// <list type="bullet">
/// <item><b>Type.</b> Equal types are accepted unchanged. Any differing type is rejected —
/// <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/> for a would-be lossless widening,
/// <see cref="DeltaSchemaMismatchKind.IncompatibleType"/> for anything else. A differing <b>partition</b>
/// column's type is rejected earlier and more clearly as
/// <see cref="DeltaSchemaMismatchKind.PartitionColumnEvolutionUnsupported"/>.</item>
/// <item><b>Nullability.</b> The table's nullability is authoritative and never tightened: writing a
/// non-null value into a nullable column is fine. Writing a nullable value into a required column — or
/// relaxing a required column to nullable — is <b>always</b> rejected
/// (<see cref="DeltaSchemaMismatchKind.NullabilityViolation"/>). Array <c>containsNull</c> and map
/// <c>valueContainsNull</c> follow the same rule.</item>
/// <item><b>Presence.</b> A table column the write omits is accepted only if it is nullable (written as
/// <c>null</c>); omitting a required column is rejected
/// (<see cref="DeltaSchemaMismatchKind.MissingRequiredColumn"/>). A write column absent from the table is a
/// new column: rejected unless <see cref="SchemaEvolutionMode.AddNewColumns"/> is enabled, and then only if
/// it is nullable. New columns are <b>appended</b> after the existing (table-ordered) columns.</item>
/// <item><b>Case-fold uniqueness.</b> Although matching is case-sensitive, the <i>merged</i> schema must not
/// contain two columns whose names are equal ignoring case (e.g. table <c>id</c> + new write column
/// <c>ID</c>) — Delta/Spark enforce case-insensitive column-name uniqueness as a storage/protocol
/// invariant. Such a merge is rejected
/// (<see cref="DeltaSchemaMismatchKind.CaseInsensitiveDuplicateColumn"/>), recursively through nested
/// structs.</item>
/// </list>
///
/// <para><b>Case sensitivity (deliberate).</b> Matching is case-sensitive/ordinal, so <c>Id</c> and
/// <c>id</c> are distinct <i>match</i> targets. Spark's default case-<i>insensitive</i> name resolution is a
/// name-resolution concern to layer above this enforcer later (design §2.12.2), not a storage-layer
/// type-compatibility rule. The case-fold <i>uniqueness</i> check above is a separate, storage-level
/// protocol invariant on the merged result.</para>
///
/// <para><b>Scope boundary.</b> Column mapping / typed field metadata (§2.12.3) is out of scope; field
/// metadata is carried through from the table schema unchanged. Read-side null-fill of an evolved column and
/// physical write-schema validation are the read path's / production write-door's responsibility
/// (#497).</para>
/// </summary>
internal static class DeltaSchemaEnforcer
{
    /// <summary>
    /// Validates <paramref name="writeSchema"/> against <paramref name="tableSchema"/> under
    /// <paramref name="mode"/>. Returns <see langword="null"/> when the write is compatible and requires no
    /// schema change (the writer commits adds only), or the <b>merged</b> table schema when
    /// <paramref name="mode"/> permitted an additive change (a new nullable column — the writer commits a
    /// <c>metaData</c> carrying it in the same version as the adds). The merged schema never changes any
    /// existing column's type. Throws before returning if the write is incompatible or needs a change
    /// <paramref name="mode"/> does not allow.
    /// </summary>
    /// <param name="tableSchema">The table's current schema.</param>
    /// <param name="writeSchema">The incoming write's schema.</param>
    /// <param name="mode">Which additive evolution (if any) is permitted.</param>
    /// <param name="partitionColumns">The table's partition columns (top-level names). A differing partition
    /// column type is rejected with a clear <see cref="DeltaSchemaMismatchKind.PartitionColumnEvolutionUnsupported"/>
    /// reason. May be <see langword="null"/>/empty for an unpartitioned table.</param>
    /// <exception cref="DeltaSchemaMismatchException">The write is incompatible with the table schema, or
    /// requires a schema change not permitted by <paramref name="mode"/>.</exception>
    public static StructType? Reconcile(
        StructType tableSchema,
        StructType writeSchema,
        SchemaEvolutionMode mode,
        IReadOnlyCollection<string>? partitionColumns = null)
    {
        ArgumentNullException.ThrowIfNull(tableSchema);
        ArgumentNullException.ThrowIfNull(writeSchema);

        HashSet<string>? partitions = partitionColumns is { Count: > 0 }
            ? new HashSet<string>(partitionColumns, StringComparer.Ordinal)
            : null;

        StructType merged = MergeStruct(tableSchema, writeSchema, mode, parentPath: null, partitions);

        // Value-equality: reordering columns or omitting nullable ones yields a schema equal to the table's,
        // so no metadata change is emitted. Only a genuine additive change returns a non-null merged schema.
        return merged.Equals(tableSchema) ? null : merged;
    }

    private static StructType MergeStruct(
        StructType tableStruct,
        StructType writeStruct,
        SchemaEvolutionMode mode,
        string? parentPath,
        IReadOnlySet<string>? partitionColumns)
    {
        var mergedFields = new List<StructField>(tableStruct.Count);

        // Existing columns, in table order (order-insensitive matching by name; reordering is not a change).
        foreach (StructField tableField in tableStruct)
        {
            string path = Combine(parentPath, tableField.Name);
            if (writeStruct.TryGetField(tableField.Name, out StructField writeField))
            {
                // A partition column's type cannot be evolved (it is encoded in the table layout). Reject a
                // differing type here, before the generic type classification, for a clearer reason. Partition
                // columns are top-level, so this only applies at parentPath == null.
                if (partitionColumns is not null
                    && partitionColumns.Contains(tableField.Name)
                    && !tableField.DataType.Equals(writeField.DataType))
                {
                    throw DeltaSchemaMismatchException.PartitionColumnEvolution(
                        path, tableField.DataType.SimpleString, writeField.DataType.SimpleString);
                }

                mergedFields.Add(MergeField(tableField, writeField, mode, path));
            }
            else
            {
                // The write omits this column. Allowed only if it is nullable (written as null for new rows).
                if (!tableField.Nullable)
                {
                    throw DeltaSchemaMismatchException.MissingRequiredColumn(path);
                }

                mergedFields.Add(tableField);
            }
        }

        // New columns (present in the write, absent from the table), appended in write order.
        foreach (StructField writeField in writeStruct)
        {
            if (tableStruct.TryGetField(writeField.Name, out _))
            {
                continue;
            }

            string path = Combine(parentPath, writeField.Name);
            if ((mode & SchemaEvolutionMode.AddNewColumns) == 0)
            {
                throw DeltaSchemaMismatchException.NewColumnNotAllowed(path);
            }

            if (!writeField.Nullable)
            {
                throw DeltaSchemaMismatchException.NewColumnMustBeNullable(path);
            }

            mergedFields.Add(writeField);
        }

        // Storage/protocol invariant: column names must be unique ignoring case. Evolution must not mint a
        // merged schema with a case-fold collision (e.g. table `id` + new `ID`) that Delta/Spark would reject.
        RejectCaseInsensitiveDuplicates(mergedFields, parentPath);

        return new StructType(mergedFields);
    }

    private static StructField MergeField(
        StructField tableField, StructField writeField, SchemaEvolutionMode mode, string path)
    {
        // Nullability: the table's constraint is authoritative and never tightened or relaxed by a write.
        // A nullable write into a required table column would carry null into a column that forbids it.
        if (!tableField.Nullable && writeField.Nullable)
        {
            throw DeltaSchemaMismatchException.NullabilityViolation(path);
        }

        DataType mergedType = MergeType(tableField.DataType, writeField.DataType, mode, path);

        // Preserve the table field's declared nullability and metadata (field metadata is not evolved here).
        return tableField.DataType.Equals(mergedType)
            ? tableField
            : new StructField(tableField.Name, mergedType, tableField.Nullable, tableField.Metadata);
    }

    private static DataType MergeType(DataType tableType, DataType writeType, SchemaEvolutionMode mode, string path)
    {
        if (tableType.Equals(writeType))
        {
            return tableType;
        }

        switch (tableType, writeType)
        {
            case (StructType tableStruct, StructType writeStruct):
                // Nested structs recurse; partition columns are only top-level, so pass none here.
                return MergeStruct(tableStruct, writeStruct, mode, path, partitionColumns: null);

            case (ArrayType tableArray, ArrayType writeArray):
                if (!tableArray.ContainsNull && writeArray.ContainsNull)
                {
                    throw DeltaSchemaMismatchException.NullabilityViolation(path + ".element");
                }

                DataType mergedElement = MergeType(tableArray.ElementType, writeArray.ElementType, mode, path + ".element");
                return new ArrayType(mergedElement, tableArray.ContainsNull);

            case (MapType tableMap, MapType writeMap):
                DataType mergedKey = MergeType(tableMap.KeyType, writeMap.KeyType, mode, path + ".key");
                if (!tableMap.ValueContainsNull && writeMap.ValueContainsNull)
                {
                    throw DeltaSchemaMismatchException.NullabilityViolation(path + ".value");
                }

                DataType mergedValue = MergeType(tableMap.ValueType, writeMap.ValueType, mode, path + ".value");
                return new MapType(mergedKey, mergedValue, tableMap.ValueContainsNull);

            default:
                // No type change is ever applied (fail-closed). Classify the rejection so the caller learns
                // whether the change would have been a lossless widening (deferred, #495) or is outright
                // incompatible. Delta never silently downcasts, and DeltaSharp never widens the logical schema
                // out from under its own reader.
                if (IsPermittedWidening(tableType, writeType))
                {
                    throw DeltaSchemaMismatchException.TypeWideningUnsupported(
                        path, tableType.SimpleString, writeType.SimpleString);
                }

                throw DeltaSchemaMismatchException.IncompatibleType(
                    path, tableType.SimpleString, writeType.SimpleString);
        }
    }

    // Rejects a merged field list that contains two names equal under a case-insensitive (ordinal) compare.
    // Runs per struct level (top-level and, via MergeStruct recursion, nested), so the dotted path is exact.
    private static void RejectCaseInsensitiveDuplicates(List<StructField> fields, string? parentPath)
    {
        Dictionary<string, string>? seen = null;
        foreach (StructField field in fields)
        {
            seen ??= new Dictionary<string, string>(fields.Count, StringComparer.OrdinalIgnoreCase);
            if (seen.TryGetValue(field.Name, out string? existing)
                && !string.Equals(existing, field.Name, StringComparison.Ordinal))
            {
                throw DeltaSchemaMismatchException.CaseInsensitiveDuplicateColumn(
                    Combine(parentPath, field.Name), Combine(parentPath, existing));
            }

            seen[field.Name] = field.Name;
        }
    }

    /// <summary>
    /// Whether <paramref name="writeType"/> is a strictly wider, lossless widening of <paramref name="tableType"/>
    /// from the recognized atomic/decimal set. Retained <b>only</b> to classify a rejection as
    /// <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/> (deferred, #495) versus
    /// <see cref="DeltaSchemaMismatchKind.IncompatibleType"/> — no widening is ever applied. Deliberately
    /// stricter than <see cref="TypeCoercion"/>'s generic common-type search: only these total, unambiguous
    /// cases count, so a lossy promotion (e.g. <c>long→double</c>) is not treated as a would-be widening.
    /// </summary>
    private static bool IsPermittedWidening(DataType tableType, DataType writeType)
    {
        // Integral widening: byte → short → int → long.
        int tableRank = IntegralRank(tableType);
        int writeRank = IntegralRank(writeType);
        if (tableRank >= 0 && writeRank >= 0)
        {
            return writeRank > tableRank;
        }

        // float → double.
        if (tableType is FloatType && writeType is DoubleType)
        {
            return true;
        }

        // date → timestamp.
        if (tableType is DateType && writeType is TimestampType)
        {
            return true;
        }

        // decimal(p,s) → decimal(p',s') when both the integer-digit range and the scale grow (or one grows
        // and the other is unchanged), so every representable value is preserved with no rounding.
        if (tableType is DecimalType from && writeType is DecimalType to)
        {
            return to.Scale >= from.Scale
                && (to.Precision - to.Scale) >= (from.Precision - from.Scale);
        }

        return false;
    }

    private static int IntegralRank(DataType type) => type switch
    {
        ByteType => 0,
        ShortType => 1,
        IntegerType => 2,
        LongType => 3,
        _ => -1,
    };

    private static string Combine(string? parentPath, string name) =>
        parentPath is null ? name : parentPath + "." + name;
}
