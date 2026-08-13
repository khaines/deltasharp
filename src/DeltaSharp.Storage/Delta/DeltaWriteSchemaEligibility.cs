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
/// mapping. An EMPTY such table still loads and reads fine (a schema-only snapshot has no data file to map),
/// so the damage is deferred, not absent: the moment the table has to touch a data file — any write that
/// stages a file (<c>ParquetTypeMapping.CreateField</c>) and any read that maps a file's columns — the
/// operation fails <c>StorageErrorKind.UnsupportedFeature</c>. A committed <c>void</c> column therefore
/// produces a table that can be created and described but never populated or scanned, and the only "fix" is
/// dropping the column (or deleting the table).
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
    /// The maximum declared TYPE-tree depth this door walks before failing closed. Sits at the schema
    /// serializer's own JSON-container bound (<c>SchemaJson.MaxDepth</c> = 64): every type level costs AT
    /// LEAST one JSON container, and a struct schema pays three containers (struct object, <c>fields</c>
    /// array, field object) before its first field's type is even opened, so anything <c>SchemaJson.ToJson</c>
    /// would agree to serialize nests strictly shallower than this — the bound can only reject a schema the
    /// serializer would reject moments later, never one it would accept.
    /// </summary>
    /// <remarks>
    /// The walk below runs BEFORE <c>SchemaJson.ToJson</c> (that is the whole point of the door: reject
    /// before a schemaString exists), so it cannot borrow the serializer's depth guard — it needs its own.
    /// An UNBOUNDED recursive walk of an attacker- or generator-supplied declared schema overflows the stack
    /// with an <b>uncatchable</b> <see cref="StackOverflowException"/> (process abort, not a planned error) at
    /// roughly 4,000 levels on a thread-pool stack. Same rule, same remedy, as
    /// <c>DeltaSharp.Executor.Physical.NestedTypeDepth</c>: walk ITERATIVELY with an explicit stack and a
    /// depth bound, so a pathological tree is a deterministic refusal.
    /// </remarks>
    private const int MaxDepth = 64;

    /// <summary>
    /// Fails closed when <paramref name="schema"/> declares an ineligible type ANYWHERE in its type tree —
    /// as a top-level field, an array element, a map key, a map value, or a nested struct field — or when
    /// that type tree nests deeper than <see cref="MaxDepth"/>.
    /// </summary>
    /// <param name="schema">The declared write schema about to be serialized into a <c>metaData</c> action.</param>
    /// <exception cref="DeltaStorageException">A column (or a nested leaf) is
    /// <see cref="NullType"/>, or the type tree nests deeper than <see cref="MaxDepth"/>
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/> in both cases).</exception>
    public static void EnsureCommittable(StructType schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        foreach (StructField field in schema)
        {
            EnsureTypeCommittable(field.DataType, field.Name);
        }
    }

    // Walks one field's type tree ITERATIVELY (an explicit stack), so validating a pathologically deep
    // declared schema cannot itself overflow the runtime stack — the depth check is the FIRST thing done to
    // every popped node, before the eligibility arm and before any child is pushed, so it cannot be skipped
    // or out-run by a deeper subtree.
    //
    // The path names the offending LEAF ("v", "s.inner", "a.element", "m.key", "m.value") using the same
    // dotted convention ColumnMapping's duplicate-name walk uses, so an operator can locate the column. The
    // segments are the writer's own declared schema identifiers, but they are echoed through
    // DiagnosticText.Sanitize anyway (uniform posture: bounded + control-char-neutralized), matching
    // ParquetTypeMapping's identical column-name echo.
    private static void EnsureTypeCommittable(DataType type, string path)
    {
        var pending = new Stack<(DataType Type, string Path, int Depth)>();
        pending.Push((type, path, 1));
        while (pending.Count > 0)
        {
            (DataType current, string currentPath, int depth) = pending.Pop();
            if (depth > MaxDepth)
            {
                throw DeltaStorageException.UnsupportedFeature(
                    $"Cannot commit a Delta table schema whose column '{DiagnosticText.Sanitize(path)}' nests "
                    + $"deeper than the supported limit of {MaxDepth} type levels; a deeper declared schema is "
                    + "refused fail-closed (it could not be serialized to a readable schemaString, and walking "
                    + "it unbounded would overflow the stack).");
            }

            switch (current)
            {
                case NullType:
                    throw DeltaStorageException.UnsupportedFeature(
                        "Cannot commit a Delta table schema declaring a NullType column "
                        + $"'{DiagnosticText.Sanitize(currentPath)}'; the void/null type has no physical layout "
                        + "and cannot be persisted. Drop the column to proceed.");
                case StructType nested:
                    foreach (StructField field in nested)
                    {
                        pending.Push((field.DataType, currentPath + "." + field.Name, depth + 1));
                    }

                    break;
                case ArrayType array:
                    pending.Push((array.ElementType, currentPath + ".element", depth + 1));
                    break;
                case MapType map:
                    pending.Push((map.KeyType, currentPath + ".key", depth + 1));
                    pending.Push((map.ValueType, currentPath + ".value", depth + 1));
                    break;
                default:
                    break;
            }
        }
    }
}
