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
    /// <param name="valueCount">The number of packed present values accompanying the levels.</param>
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

        // The container's own max definition level: the leaf's, minus the one level the leaf spends on its own
        // OPTIONAL-ness. One below that is the level an EMPTY container occupies, and everything at or below it
        // means "no element exists here".
        int containerMaxDef = maxDef - (leaf.IsNullable ? 1 : 0);
        int emptyContainerDef = containerMaxDef - 1;

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
        // wrong row.
        if (present != valueCount)
        {
            throw Corrupt(
                label,
                $"{present} slot(s) encode a present value but {valueCount} value(s) were packed");
        }
    }

    private static DeltaStorageException Corrupt(string label, string detail) =>
        DeltaStorageException.CorruptData(
            $"Nested column '{label}': the encoded Parquet level streams are invalid ({detail}).");
}
