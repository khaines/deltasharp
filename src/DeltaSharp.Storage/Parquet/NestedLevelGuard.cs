using DeltaSharp.Storage.Delta;
using Parquet.Schema;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// The §2.3c pre-write level-invariant guard: the last gate between a shredded leaf and bytes on disk.
/// </summary>
/// <remarks>
/// <para>Parquet.Net 6.1.0's <c>WriteAllPartsAsync</c> is a raw level-stream primitive — it validates buffer
/// LENGTHS but not level SEMANTICS, so a shredder defect that emits a structurally impossible level stream
/// produces a file that writes and closes cleanly and only surfaces as an availability error (DeltaSharp's own
/// #571 reader rejects it) or as SILENTLY WRONG rows in Spark/Delta-rs. This guard turns that class of defect
/// into a deterministic pre-write <see cref="StorageErrorKind.CorruptData"/>.</para>
/// <para>It is deliberately LEVEL-ONLY and UNCONDITIONAL: it inspects the two level spans and the value count,
/// never the source vectors, so it is an INDEPENDENT check of the shredder rather than a restatement of it,
/// and it runs on every write (not under <c>Debug.Assert</c>) because the failure mode is silent data
/// corruption on a release build. It is span-based and LINQ-free: one pass, no allocation.</para>
/// <para>Bounds come off the SCHEMA-ATTACHED <see cref="DataField"/> (<see cref="Field.MaxDefinitionLevel"/> /
/// <see cref="Field.MaxRepetitionLevel"/>), which is the same object the footer is stamped from — so the guard
/// can never disagree with the file about what the legal levels are.</para>
/// </remarks>
internal static class NestedLevelGuard
{
    /// <summary>Validates one leaf's encoded level streams against <paramref name="leaf"/>'s schema position.</summary>
    /// <param name="leaf">The schema-attached leaf being written; supplies the level bounds.</param>
    /// <param name="definitions">The leaf's definition levels, one per emitted slot.</param>
    /// <param name="repetitions">The leaf's repetition levels; ignored when <paramref name="hasRepetitions"/>
    /// is <see langword="false"/>.</param>
    /// <param name="hasRepetitions">Whether a repetition stream is being passed to the writer. A non-repeated
    /// leaf must pass a genuinely ABSENT stream (<see langword="null"/>), not an empty one — Parquet.Net
    /// rejects an empty <c>Memory&lt;int&gt;?</c> as an undersized buffer.</param>
    /// <param name="valueCount">The number of packed present values accompanying the levels, derived by the
    /// caller from the SOURCE VECTORS (not from these levels) — the two are compared below, so passing a
    /// level-derived count would make that clause circular and inert.</param>
    /// <param name="rowCount">The number of logical rows the row group covers.</param>
    /// <param name="label">The sanitized column label for diagnostics.</param>
    public static void Validate(
        DataField leaf,
        ReadOnlySpan<int> definitions,
        ReadOnlySpan<int> repetitions,
        bool hasRepetitions,
        int valueCount,
        int rowCount,
        string label)
    {
        int maxDef = leaf.MaxDefinitionLevel;
        int maxRep = leaf.MaxRepetitionLevel;
        bool repeated = maxRep > 0;

        // §2.3c N4-c level PROVENANCE. Every in-scope nested leaf sits under an OPTIONAL container, so its
        // max definition level is at least 1. A leaf that reports 0 is DETACHED from a ParquetSchema (levels
        // are assigned when the schema is constructed, not by the Field constructor) — and a detached leaf
        // silently collapses every clause below: containerMaxDef becomes 0 or -1, so `def <= emptyContainerDef`
        // is never true and the run-legality check degenerates into a no-op. Refuse rather than validate
        // against bounds that are not the file's.
        if (maxDef < 1)
        {
            throw Corrupt(
                label,
                "the leaf reports max definition level 0, so it is not attached to a Parquet schema and its "
                + "level bounds are not the ones the footer will declare");
        }

        if (repeated != hasRepetitions)
        {
            throw Corrupt(
                label,
                repeated
                    ? "a repeated leaf was encoded without a repetition stream"
                    : "a non-repeated leaf was encoded with a repetition stream");
        }

        if (hasRepetitions && repetitions.Length != definitions.Length)
        {
            throw Corrupt(label, "the definition and repetition streams have different lengths");
        }

        int containerMaxDef = ContainerMaxDefinitionLevel(leaf);
        int emptyContainerDef = EmptyContainerDefinitionLevel(leaf);

        int present = 0;
        int rowOpenings = 0;
        int currentRowOpenDef = 0;
        for (int i = 0; i < definitions.Length; i++)
        {
            int def = definitions[i];
            if (def < 0 || def > maxDef)
            {
                throw Corrupt(label, $"definition level {def} at slot {i} is outside [0, {maxDef}]");
            }

            if (def == maxDef)
            {
                present++;
            }

            if (!repeated)
            {
                continue;
            }

            int rep = repetitions[i];
            if (rep < 0 || rep > maxRep)
            {
                throw Corrupt(label, $"repetition level {rep} at slot {i} is outside [0, {maxRep}]");
            }

            if (rep == 0)
            {
                rowOpenings++;
                currentRowOpenDef = def;
                continue;
            }

            if (i == 0)
            {
                throw Corrupt(label, "the first slot continues a row that was never opened");
            }

            // Joint legality: a continuation slot describes a SUBSEQUENT element of a repeated container, so
            // the container must exist and be non-empty at this slot.
            if (def <= emptyContainerDef)
            {
                throw Corrupt(
                    label,
                    $"slot {i} continues a repeated container at definition level {def}, which encodes an "
                    + "absent or empty container");
            }

            // Run legality: the row this slot continues must itself have opened with a present, non-empty
            // container. A null or empty container occupies exactly ONE slot and can never be continued.
            if (currentRowOpenDef <= emptyContainerDef)
            {
                throw Corrupt(
                    label,
                    $"slot {i} continues a row whose container was encoded as absent or empty (opening "
                    + $"definition level {currentRowOpenDef})");
            }
        }

        if (repeated)
        {
            if (rowOpenings != rowCount)
            {
                throw Corrupt(
                    label,
                    $"the level streams open {rowOpenings} row(s) but the row group covers {rowCount}");
            }
        }
        else if (definitions.Length != rowCount)
        {
            throw Corrupt(
                label,
                $"the level stream has {definitions.Length} slot(s) but the row group covers {rowCount} row(s)");
        }

        // The packed-values invariant: WriteAllPartsAsync consumes values POSITIONALLY against the slots at
        // the leaf's max definition level, so a mismatch here silently shifts every subsequent value into the
        // wrong row. `present` is derived HERE from the level stream; `valueCount` is derived by the caller
        // from the source vectors' own null masks. Two independent derivations of the same quantity — an
        // encoder that packs fewer values than its levels claim (which would otherwise publish uninitialized
        // pooled memory) or more (a shifted lane) is caught exactly here.
        if (present != valueCount)
        {
            throw Corrupt(
                label,
                $"{present} slot(s) encode a present value but {valueCount} value(s) were packed");
        }
    }

    /// <summary>
    /// The container's own max definition level for <paramref name="leaf"/>: the leaf's own level minus the
    /// one level it spends on its OPTIONAL-ness. This is the SINGLE source of truth for the container levels
    /// (Architect #9) — the shredder derives its level tables from it too, so the encoder and this guard can
    /// never disagree about where the container/leaf boundary sits.
    /// </summary>
    internal static int ContainerMaxDefinitionLevel(DataField leaf)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        return leaf.MaxDefinitionLevel - (leaf.IsNullable ? 1 : 0);
    }

    /// <summary>The definition level an EMPTY repeated container occupies — one below
    /// <see cref="ContainerMaxDefinitionLevel"/>. Everything at or below it means "no element exists here".</summary>
    internal static int EmptyContainerDefinitionLevel(DataField leaf) => ContainerMaxDefinitionLevel(leaf) - 1;

    private static DeltaStorageException Corrupt(string label, string detail) =>
        DeltaStorageException.CorruptData(
            $"Nested column '{label}': the encoded Parquet level streams are invalid ({detail}).");
}
