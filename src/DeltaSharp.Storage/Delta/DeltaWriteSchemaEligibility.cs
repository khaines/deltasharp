using DeltaSharp.Types;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The <b>declared-write-schema type-eligibility door</b> (#702): refuses to build a <c>metaData</c> action
/// for a schema that declares a column DeltaSharp cannot physically persist.
/// </summary>
/// <remarks>
/// <para>
/// Today the sole ineligible type is <see cref="NullType"/> (the <c>void</c>/<c>null</c> type). It has no
/// physical layout (<c>PhysicalLayoutResolver.TryResolve</c> returns <see langword="false"/>) and no Parquet
/// mapping, so a table whose <c>metaData.schemaString</c> declares it is unreadable BY DELTASHARP ITSELF —
/// every read fails <c>StorageErrorKind.UnsupportedFeature</c>. Committing such a schema therefore bricks the
/// table at version 0, and the only "fix" is deleting the table.
/// </para>
/// <para>
/// <b>Why this door and not a per-file guard.</b> The pre-#702 reasoning held that
/// <c>ParquetTypeMapping.CreateField</c> already rejects a <c>void</c> column before any <c>metaData</c> is
/// written. That is true only for a write that STAGES AT LEAST ONE FILE. A <b>zero-file create</b> — an empty
/// write to a fresh path, e.g. <c>DeltaWriteTarget.AppendAsync(schema, [], batches: [])</c> — legitimately
/// creates the (empty) table at version 0 and reaches NO per-file guard: <c>CreateField</c> never runs, and
/// <c>DeltaTableWriter.ValidateStagedWriteSchema</c> iterates an empty file list. The declared schema went
/// straight to <c>SchemaJson.ToJson</c> and committed <c>"type":"void"</c>. This check is therefore
/// deliberately INDEPENDENT of the staged-file count and runs on every path that builds a
/// <c>MetadataAction</c> — create, mapped create, schema evolution, and <c>overwriteSchema</c> replacement —
/// BEFORE the schemaString is serialized and before any object-store or <c>_delta_log</c> write, so a
/// rejected write leaves the table exactly as it was (no partial commit).
/// </para>
/// <para>
/// <b>Read tolerance is unchanged.</b> <c>SchemaJson.FromJson</c> still ACCEPTS <c>"void"</c>/<c>"null"</c>
/// and <c>ToJson</c> still emits it, so a schemaString ANOTHER engine wrote (delta-rs 1.6.2 maps <c>void</c>
/// to Arrow Null) still parses. The rejection is on the DECLARED WRITE SCHEMA, not on the serializer: a
/// serializer-level reject would make a foreign table un-re-serializable (checkpoint / footer / evolution
/// paths all round-trip a foreign schema) instead of merely un-creatable.
/// </para>
/// </remarks>
internal static class DeltaWriteSchemaEligibility
{
    /// <summary>
    /// Fails closed when <paramref name="schema"/> declares an ineligible type ANYWHERE in its type tree —
    /// as a top-level field, an array element, a map key, a map value, or a nested struct field.
    /// </summary>
    /// <param name="schema">The declared write schema about to be serialized into a <c>metaData</c> action.</param>
    /// <exception cref="DeltaStorageException">A column (or a nested leaf) is
    /// <see cref="NullType"/> (<see cref="StorageErrorKind.UnsupportedFeature"/>).</exception>
    public static void EnsureCommittable(StructType schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        foreach (StructField field in schema)
        {
            EnsureTypeCommittable(field.DataType, field.Name);
        }
    }

    // Walks one field's type tree. The path names the offending LEAF ("v", "s.inner", "a.element", "m.key",
    // "m.value") using the same dotted convention ColumnMapping's duplicate-name walk uses, so an operator can
    // locate the column. The segments are the writer's own declared schema identifiers, but they are echoed
    // through DiagnosticText.Sanitize anyway (uniform posture: bounded + control-char-neutralized), matching
    // ParquetTypeMapping's identical column-name echo.
    private static void EnsureTypeCommittable(DataType type, string path)
    {
        switch (type)
        {
            case NullType:
                throw DeltaStorageException.UnsupportedFeature(
                    $"Cannot create a Delta table with a NullType column '{DiagnosticText.Sanitize(path)}'; "
                    + "the void/null type has no physical layout and cannot be persisted.");
            case StructType nested:
                foreach (StructField field in nested)
                {
                    EnsureTypeCommittable(field.DataType, path + "." + field.Name);
                }

                break;
            case ArrayType array:
                EnsureTypeCommittable(array.ElementType, path + ".element");
                break;
            case MapType map:
                EnsureTypeCommittable(map.KeyType, path + ".key");
                EnsureTypeCommittable(map.ValueType, path + ".value");
                break;
            default:
                break;
        }
    }
}
