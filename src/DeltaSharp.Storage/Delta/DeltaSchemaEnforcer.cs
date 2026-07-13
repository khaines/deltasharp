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
/// <para><b>The only additive evolution is a new column; type widening is <i>applied</i> only when the
/// table enables it.</b> Under <see cref="SchemaEvolutionMode.AddNewColumns"/> a new <b>nullable column</b>
/// (top-level or nested) may be added. Separately, when the table has the Delta <c>typeWidening</c> table
/// feature <b>and</b> the <c>delta.enableTypeWidening</c> property set (threaded as
/// <c>typeWideningEnabled</c>), a <b>Delta-sanctioned widening</b> of an existing column's scalar type is
/// <b>applied</b> — the merged schema carries the wider type and a <c>delta.typeChanges</c> metadata entry so
/// readers promote pre-widening files. Every other difference is either a no-op (returns <see langword="null"/>)
/// or a rejection. This is fail-closed: no accepted write can leave the table in a state its own reader
/// cannot read back.</para>
///
/// <para><b>Type widening (Delta PROTOCOL.md "Type Widening").</b> The applied allowlist
/// (<see cref="TypeWidening.IsSanctionedWidening"/>) is: integral <c>byte→short→int→long</c> (any wider
/// rank), <c>float→double</c>, and <c>decimal(p,s)→decimal(p',s')</c> grow-only (integer-digit range and
/// scale both non-decreasing). When the table does <b>not</b> enable type widening, such a change is rejected
/// distinctly as <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/> (naming the enablement
/// requirement); every other differing type is <see cref="DeltaSchemaMismatchKind.IncompatibleType"/>.
/// The <b>cross-family</b> sanctioned widenings (integral→<c>double</c>, integral→<c>decimal</c>) are DEFERRED
/// (#535) — rejected distinctly as <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/> (naming #535),
/// never the generic <see cref="DeltaSchemaMismatchKind.IncompatibleType"/>, because they ARE Delta-sanctioned.
/// <c>date→timestamp</c> is always rejected (as <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/>):
/// Delta only sanctions <c>date→timestamp_ntz</c>, which needs a not-yet-existing NTZ type. Widening inside an
/// array element / map key/value is not applied (it would need a <c>fieldPath</c> and the Parquet read path
/// does not read nested types) — it stays fail-closed.</para>
///
/// <para><b>Compatibility rules (deterministic, total, case-sensitive; AC3).</b> Fields are matched
/// <b>by name</b> (case-sensitive / ordinal, matching <see cref="StructType"/>), so column reordering is
/// not a change. For each matched column and, recursively, each nested <c>struct</c> field,
/// <c>array</c> element, and <c>map</c> key/value:</para>
/// <list type="bullet">
/// <item><b>Type.</b> Equal types are accepted unchanged. A Delta-sanctioned widening of a scalar type is
/// <b>applied</b> when the table enables type widening (else rejected as
/// <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/>); any other differing type is rejected —
/// <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/> for a would-be/deferred widening,
/// <see cref="DeltaSchemaMismatchKind.IncompatibleType"/> for anything else. A differing <b>partition</b>
/// column's type is rejected earlier and more clearly: a non-widening change as
/// <see cref="DeltaSchemaMismatchKind.PartitionColumnEvolutionUnsupported"/>; a would-be Delta-sanctioned
/// widening (which Delta allows without a rewrite — partition values are strings — but this build defers,
/// #537) distinctly as <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/>.</item>
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
    /// column type is rejected: a non-widening change with a clear
    /// <see cref="DeltaSchemaMismatchKind.PartitionColumnEvolutionUnsupported"/> reason, a would-be
    /// Delta-sanctioned widening (deferred #537) distinctly as
    /// <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/>. May be <see langword="null"/>/empty for
    /// an unpartitioned table.</param>
    /// <param name="typeWideningEnabled">Whether the table enables applying Delta-sanctioned type widenings
    /// (the <c>typeWidening</c> table feature is present AND <c>delta.enableTypeWidening</c> is set — see
    /// <see cref="TypeWideningFeature.IsWriteEnabled"/>). When <see langword="false"/> a would-be widening is
    /// rejected as <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/>; when <see langword="true"/>
    /// a sanctioned scalar widening is applied and recorded in the field's <c>delta.typeChanges</c> metadata.</param>
    /// <exception cref="DeltaSchemaMismatchException">The write is incompatible with the table schema, or
    /// requires a schema change not permitted by <paramref name="mode"/>.</exception>
    public static StructType? Reconcile(
        StructType tableSchema,
        StructType writeSchema,
        SchemaEvolutionMode mode,
        IReadOnlyCollection<string>? partitionColumns = null,
        bool typeWideningEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(tableSchema);
        ArgumentNullException.ThrowIfNull(writeSchema);

        HashSet<string>? partitions = partitionColumns is { Count: > 0 }
            ? new HashSet<string>(partitionColumns, StringComparer.Ordinal)
            : null;

        StructType merged = MergeStruct(tableSchema, writeSchema, mode, parentPath: null, partitions, typeWideningEnabled);

        // Value-equality: reordering columns or omitting nullable ones yields a schema equal to the table's,
        // so no metadata change is emitted. Only a genuine additive change returns a non-null merged schema.
        return merged.Equals(tableSchema) ? null : merged;
    }

    private static StructType MergeStruct(
        StructType tableStruct,
        StructType writeStruct,
        SchemaEvolutionMode mode,
        string? parentPath,
        IReadOnlySet<string>? partitionColumns,
        bool typeWideningEnabled)
    {
        var mergedFields = new List<StructField>(tableStruct.Count);

        // Existing columns, in table order (order-insensitive matching by name; reordering is not a change).
        foreach (StructField tableField in tableStruct)
        {
            string path = Combine(parentPath, tableField.Name);
            if (writeStruct.TryGetField(tableField.Name, out StructField writeField))
            {
                // A partition column's type change is rejected here, before the generic type classification,
                // for a clearer reason. Two distinct honest cases:
                //   (a) The change WOULD be a Delta-sanctioned widening AND type widening is enabled: Delta
                //       DOES sanction partition-column widening WITHOUT a rewrite (partition values are stored
                //       as strings in the log / directory names — verified vs Delta's golden
                //       TypeWideningAlterTableSuite), but this build DEFERS it (#537). It stays fail-closed,
                //       classified distinctly (TypeWideningUnsupported) with an HONEST message — not the
                //       factually-wrong "requires a full table rewrite" reason.
                //   (b) Any other partition-column type change (a non-widening, or widening not enabled) keeps
                //       the existing PartitionColumnEvolutionUnsupported classification.
                // Partition columns are top-level, so this only applies at parentPath == null.
                if (partitionColumns is not null
                    && partitionColumns.Contains(tableField.Name)
                    && !tableField.DataType.Equals(writeField.DataType))
                {
                    if (typeWideningEnabled
                        && TypeWidening.IsSanctionedWidening(tableField.DataType, writeField.DataType))
                    {
                        throw DeltaSchemaMismatchException.PartitionColumnWideningDeferred(
                            path, tableField.DataType.SimpleString, writeField.DataType.SimpleString);
                    }

                    throw DeltaSchemaMismatchException.PartitionColumnEvolution(
                        path, tableField.DataType.SimpleString, writeField.DataType.SimpleString);
                }

                mergedFields.Add(MergeField(tableField, writeField, mode, path, typeWideningEnabled));
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
        StructField tableField, StructField writeField, SchemaEvolutionMode mode, string path, bool typeWideningEnabled)
    {
        // Nullability: the table's constraint is authoritative and never tightened or relaxed by a write.
        // A nullable write into a required table column would carry null into a column that forbids it.
        if (!tableField.Nullable && writeField.Nullable)
        {
            throw DeltaSchemaMismatchException.NullabilityViolation(path);
        }

        DataType mergedType = MergeType(
            tableField.DataType, writeField.DataType, mode, path, typeWideningEnabled, allowWidenApply: true);

        if (tableField.DataType.Equals(mergedType))
        {
            // No type change: preserve the table field (declared nullability + metadata) exactly.
            return tableField;
        }

        // The type changed. A change at THIS field's own scalar type is an applied type widening (§2.12.2):
        // record it in the field's `delta.typeChanges` metadata (Delta PROTOCOL.md "Type Change Metadata") so
        // readers promote pre-widening files. A non-scalar change (a nested struct field widening) attaches
        // its `delta.typeChanges` to the inner StructField via recursion, not here.
        FieldMetadata metadata = tableField.Metadata;
        if (IsScalar(tableField.DataType) && IsScalar(mergedType))
        {
            metadata = AppendTypeChange(metadata, tableField.DataType, mergedType);
        }

        return new StructField(tableField.Name, mergedType, tableField.Nullable, metadata);
    }

    private static DataType MergeType(
        DataType tableType,
        DataType writeType,
        SchemaEvolutionMode mode,
        string path,
        bool typeWideningEnabled,
        bool allowWidenApply)
    {
        if (tableType.Equals(writeType))
        {
            return tableType;
        }

        switch (tableType, writeType)
        {
            case (StructType tableStruct, StructType writeStruct):
                // Nested structs recurse; partition columns are only top-level, so pass none here. Each inner
                // field re-enters MergeField, which re-enables applied widening for its own scalar type.
                return MergeStruct(tableStruct, writeStruct, mode, path, partitionColumns: null, typeWideningEnabled);

            case (ArrayType tableArray, ArrayType writeArray):
                if (!tableArray.ContainsNull && writeArray.ContainsNull)
                {
                    throw DeltaSchemaMismatchException.NullabilityViolation(path + ".element");
                }

                // A widening of an array element / map key/value is NOT applied here: its `delta.typeChanges`
                // would need a `fieldPath` on the enclosing StructField, and the Parquet read path does not
                // read nested types at all — so nested-collection widening stays fail-closed (allowWidenApply
                // is false, so a differing element scalar is classified/rejected, never silently applied).
                DataType mergedElement = MergeType(
                    tableArray.ElementType, writeArray.ElementType, mode, path + ".element",
                    typeWideningEnabled, allowWidenApply: false);
                return new ArrayType(mergedElement, tableArray.ContainsNull);

            case (MapType tableMap, MapType writeMap):
                DataType mergedKey = MergeType(
                    tableMap.KeyType, writeMap.KeyType, mode, path + ".key", typeWideningEnabled, allowWidenApply: false);
                if (!tableMap.ValueContainsNull && writeMap.ValueContainsNull)
                {
                    throw DeltaSchemaMismatchException.NullabilityViolation(path + ".value");
                }

                DataType mergedValue = MergeType(
                    tableMap.ValueType, writeMap.ValueType, mode, path + ".value", typeWideningEnabled, allowWidenApply: false);
                return new MapType(mergedKey, mergedValue, tableMap.ValueContainsNull);

            default:
                // A differing scalar type. APPLY the change only when it is a Delta-sanctioned widening AND
                // the table has type widening enabled (feature + `delta.enableTypeWidening`) AND we are at a
                // promotable position (a StructField's own scalar, not an array/map element). Otherwise
                // classify the rejection: a would-be sanctioned/deferred widening surfaces distinctly as
                // TypeWideningUnsupported; anything else (a narrowing or unrelated type) as IncompatibleType.
                if (allowWidenApply && typeWideningEnabled && TypeWidening.IsSanctionedWidening(tableType, writeType))
                {
                    return writeType;
                }

                // Cross-family sanctioned widenings (integral→double, integral→decimal) are DEFERRED (#535)
                // even when enabled — surfaced distinctly (TypeWideningUnsupported, naming #535) rather than
                // as the generic IncompatibleType a truly-unrelated change (string→int) gets, because they
                // ARE Delta-sanctioned, just not applied yet. Independent of enablement.
                if (TypeWidening.IsDeferredCrossFamilyWidening(tableType, writeType))
                {
                    throw DeltaSchemaMismatchException.TypeWideningCrossFamilyDeferred(
                        path, tableType.SimpleString, writeType.SimpleString);
                }

                if (TypeWidening.IsSanctionedWidening(tableType, writeType)
                    || TypeWidening.IsDeferredWidening(tableType, writeType))
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

    /// <summary>The metadata key recording a field's applied type-change history (Delta PROTOCOL.md "Type
    /// Change Metadata"). The value is a JSON list of <c>{ "fromType", "toType" }</c> objects, oldest first.</summary>
    private const string TypeChangesKey = "delta.typeChanges";

    private static bool IsScalar(DataType type) => type is AtomicType or DecimalType;

    // Appends a `{ "fromType": <old>, "toType": <new> }` entry to the field's `delta.typeChanges` metadata,
    // preserving any earlier changes (Delta requires the full history, oldest first — see the two-change
    // example in PROTOCOL.md). The type names are the Delta-log type strings (DataType.TypeName), matching
    // what SchemaJson serializes for the field's own type. `fieldPath` is omitted: this build only applies a
    // widening to a StructField's own scalar type (never a map key/value or array element), which per the
    // protocol carries no `fieldPath`.
    private static FieldMetadata AppendTypeChange(FieldMetadata existing, DataType fromType, DataType toType)
    {
        MetadataValue change = MetadataValue.Nested(FieldMetadata.FromEntries(new[]
        {
            new KeyValuePair<string, string>("fromType", fromType.TypeName),
            new KeyValuePair<string, string>("toType", toType.TypeName),
        }));

        var changes = new List<MetadataValue>();
        if (existing.TryGetValue(TypeChangesKey, out MetadataValue? current) && current.TryGetArray(out var prior))
        {
            changes.AddRange(prior);
        }

        changes.Add(change);

        var entries = new List<KeyValuePair<string, MetadataValue>>(existing.Count + 1);
        foreach (KeyValuePair<string, MetadataValue> entry in existing)
        {
            if (!string.Equals(entry.Key, TypeChangesKey, StringComparison.Ordinal))
            {
                entries.Add(entry);
            }
        }

        entries.Add(new KeyValuePair<string, MetadataValue>(TypeChangesKey, MetadataValue.Array(changes)));
        return FieldMetadata.FromValues(entries);
    }

    private static string Combine(string? parentPath, string name) =>
        parentPath is null ? name : parentPath + "." + name;
}
