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
/// <para><b>Compatibility rules (deterministic, total, case-sensitive; AC3).</b> Fields are matched
/// <b>by name</b> (case-sensitive / ordinal, matching <see cref="StructType"/>), so column reordering is
/// not a change. For each matched column and, recursively, each nested <c>struct</c> field,
/// <c>array</c> element, and <c>map</c> key/value:</para>
/// <list type="bullet">
/// <item><b>Type.</b> Equal types are accepted unchanged. A strictly wider, lossless type from the
/// permitted set is accepted <i>only</i> when <see cref="SchemaEvolutionMode.WidenTypes"/> is enabled and
/// evolves the column: integral widening <c>byte→short→int→long</c>, <c>float→double</c>,
/// <c>date→timestamp</c>, and <c>decimal(p,s)→decimal(p',s')</c> when
/// <c>p'-s' ≥ p-s ∧ s' ≥ s</c> (the integer-digit range and scale both grow). Every other type change —
/// narrowing, a shrinking decimal, or an unrelated type — is <b>always</b> rejected
/// (<see cref="DeltaSchemaMismatchKind.IncompatibleType"/>); Delta never silently downcasts.</item>
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
/// </list>
///
/// <para><b>Case sensitivity (deliberate).</b> Matching is case-sensitive/ordinal, so <c>Id</c> and
/// <c>id</c> are distinct columns. This is the deterministic, total posture of <see cref="StructType"/>'s
/// ordinal lookup; Spark's default case-<i>insensitive</i> name resolution is a name-resolution concern to
/// layer above this enforcer later (design §2.12.2), not a storage-layer type-compatibility rule.</para>
///
/// <para><b>Scope boundary.</b> Type-widening evolution here changes the <i>logical</i> table schema
/// atomically; registering Delta's <c>typeWidening</c> writer/reader table feature and the per-field
/// widening metadata that lets other engines read pre-widening files back at the widened type (§2.14,
/// HP-09) is deferred. Column mapping / typed field metadata (§2.12.3) is likewise out of scope; field
/// metadata is carried through from the table schema unchanged.</para>
/// </summary>
internal static class DeltaSchemaEnforcer
{
    /// <summary>
    /// Validates <paramref name="writeSchema"/> against <paramref name="tableSchema"/> under
    /// <paramref name="mode"/>. Returns <see langword="null"/> when the write is compatible and requires no
    /// schema change (the writer commits adds only), or the <b>merged</b> table schema when
    /// <paramref name="mode"/> permitted an additive change (the writer commits a <c>metaData</c> carrying
    /// it in the same version as the adds). Throws before returning if the write is incompatible or needs a
    /// change <paramref name="mode"/> does not allow.
    /// </summary>
    /// <exception cref="DeltaSchemaMismatchException">The write is incompatible with the table schema, or
    /// requires a schema change not permitted by <paramref name="mode"/>.</exception>
    public static StructType? Reconcile(StructType tableSchema, StructType writeSchema, SchemaEvolutionMode mode)
    {
        ArgumentNullException.ThrowIfNull(tableSchema);
        ArgumentNullException.ThrowIfNull(writeSchema);

        StructType merged = MergeStruct(tableSchema, writeSchema, mode, parentPath: null);

        // Value-equality: reordering columns or omitting nullable ones yields a schema equal to the table's,
        // so no metadata change is emitted. Only a genuine additive change returns a non-null merged schema.
        return merged.Equals(tableSchema) ? null : merged;
    }

    private static StructType MergeStruct(
        StructType tableStruct, StructType writeStruct, SchemaEvolutionMode mode, string? parentPath)
    {
        var mergedFields = new List<StructField>(tableStruct.Count);

        // Existing columns, in table order (order-insensitive matching by name; reordering is not a change).
        foreach (StructField tableField in tableStruct)
        {
            string path = Combine(parentPath, tableField.Name);
            if (writeStruct.TryGetField(tableField.Name, out StructField writeField))
            {
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
                return MergeStruct(tableStruct, writeStruct, mode, path);

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
                if (IsPermittedWidening(tableType, writeType))
                {
                    if ((mode & SchemaEvolutionMode.WidenTypes) == 0)
                    {
                        throw DeltaSchemaMismatchException.TypeWideningNotEnabled(
                            path, tableType.SimpleString, writeType.SimpleString);
                    }

                    return writeType;
                }

                throw DeltaSchemaMismatchException.IncompatibleType(
                    path, tableType.SimpleString, writeType.SimpleString);
        }
    }

    /// <summary>
    /// Whether <paramref name="writeType"/> is a strictly wider, lossless widening of <paramref name="tableType"/>
    /// from the permitted atomic/decimal set. Deliberately stricter than <see cref="TypeCoercion"/>'s generic
    /// common-type search: only these total, unambiguous cases are permitted, so a lossy promotion (e.g.
    /// <c>long→double</c>) is never accepted as evolution.
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
