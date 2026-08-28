using DeltaSharp.Storage.Delta;
using Parquet.Schema;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// One repeated (list/map) ancestor level on a leaf's schema path, carrying the per-node Dremel markers the
/// per-level structural guard keys off (design §2.10.3). Read from the level's OWN footer node — not derived
/// from the leaf's <see cref="Field.MaxDefinitionLevel"/>/<see cref="Field.MaxRepetitionLevel"/>, which is
/// insufficient for interleaved shapes such as <c>array&lt;struct&lt;array&gt;&gt;</c>.
/// </summary>
/// <param name="RepLevel">This level's own <see cref="Field.MaxRepetitionLevel"/> (== its 1-based ordinal on a
/// pure list/map chain).</param>
/// <param name="PresentDef">This level's own <see cref="Field.MaxDefinitionLevel"/> — the "element-bearing"
/// definition level.</param>
/// <param name="ParentPresentDef">The immediate parent container node's
/// <see cref="Field.MaxDefinitionLevel"/> (<c>0</c> for the outermost level).</param>
internal readonly record struct RepeatedLevel(int RepLevel, int PresentDef, int ParentPresentDef);

/// <summary>
/// The §2.3c/§2.10.3 pre-write level-invariant guard: the last gate between a shredded leaf and bytes on disk.
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
/// <para><b>Per-level (§2.10.3).</b> #873 rewrote the guard as a FAITHFUL per-level dual of the reader's
/// <c>BuildRepeatedStructure</c>: it tracks, for EVERY repeated level, the same owner-open + <c>ownerComplete</c>
/// occurrence state the reader keys off each container's OWN footer node. The continuation-legality reject
/// (<c>ownerComplete[r] || def &lt; presentDef_r</c>) fires at every repeated level <c>r = 1…k</c>, not
/// innermost-only. There is NO <c>rep == maxRep</c> simplification (that was itself a false-accept bug). For a
/// single repeated level (<c>maxRep == 1</c>) the two conditions coincide and the guard is byte-identical to
/// the pre-#873 single-level guard.</para>
/// <para>Bounds come off the SCHEMA-ATTACHED <see cref="DataField"/> (<see cref="Field.MaxDefinitionLevel"/> /
/// <see cref="Field.MaxRepetitionLevel"/>) and, per level, off the built repeated-ancestor chain — the same
/// objects the footer is stamped from — so the guard can never disagree with the file about what the legal
/// levels are.</para>
/// </remarks>
internal static class NestedLevelGuard
{
    /// <summary>
    /// Validates one leaf's encoded level streams against <paramref name="leaf"/>'s single-level schema
    /// position. The repeated-ancestor chain is DERIVED from the leaf (correct for a single-level nested leaf,
    /// whose one repeated ancestor is fully described by the leaf). The deep (§2.10.3) path calls the
    /// chain-taking overload instead, because an interleaved shape's per-level markers cannot be recovered from
    /// the leaf alone.
    /// </summary>
    public static void Validate(
        DataField leaf,
        ReadOnlySpan<int> definitions,
        ReadOnlySpan<int> repetitions,
        bool hasRepetitions,
        int valueCount,
        int rowCount,
        string label)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        int maxRep = leaf.MaxRepetitionLevel;
        int containerMaxDef = ContainerMaxDefinitionLevel(leaf);

        // A single-level nested leaf has one repeated ancestor at most (maxRep <= 1); a pure-struct leaf has
        // none. Build the chain for a pure list/map chain: each level adds exactly two definition levels (the
        // optional outer group + the repeated group). For maxRep == 1 this yields presentDef == containerMaxDef
        // and parentPresentDef == 0 — the design's single-level markers (§2.10.3-d).
        Span<RepeatedLevel> chain = maxRep <= 8 ? stackalloc RepeatedLevel[maxRep] : new RepeatedLevel[maxRep];
        for (int level = 1; level <= maxRep; level++)
        {
            int presentDef = containerMaxDef - (2 * (maxRep - level));
            int parentPresentDef = Math.Max(presentDef - 2, 0);
            chain[level - 1] = new RepeatedLevel(level, presentDef, parentPresentDef);
        }

        ValidateCore(leaf, chain, definitions, repetitions, hasRepetitions, valueCount, rowCount, label);
    }

    /// <summary>
    /// Validates one leaf's encoded level streams against its schema position using the EXPLICIT ordered chain
    /// of repeated ancestors <c>R_1…R_k</c> (outermost→innermost), each carrying its own footer-node markers
    /// (design §2.10.3). Used by the recursive (nested-within-nested) shredder, where a level's
    /// <c>presentDef</c>/<c>parentPresentDef</c> cannot be derived from the leaf alone.
    /// </summary>
    public static void Validate(
        DataField leaf,
        ReadOnlySpan<RepeatedLevel> chain,
        ReadOnlySpan<int> definitions,
        ReadOnlySpan<int> repetitions,
        bool hasRepetitions,
        int valueCount,
        int rowCount,
        string label)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        ValidateCore(leaf, chain, definitions, repetitions, hasRepetitions, valueCount, rowCount, label);
    }

    private static void ValidateCore(
        DataField leaf,
        ReadOnlySpan<RepeatedLevel> chain,
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
        // silently collapses every clause below. Refuse rather than validate against bounds that are not the
        // file's.
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

        // ownerComplete[v] (v = 1..k) is true when level v's CURRENT occurrence opened as a null/empty
        // (non-element-bearing) container, so it admits NO continuation — the dual of the reader's
        // BuildRepeatedStructure ownerComplete. k == maxRep for a well-formed leaf.
        int k = chain.Length;
        Span<bool> ownerComplete = k < 64 ? stackalloc bool[k + 1] : new bool[k + 1];
        ownerComplete.Clear();

        int present = 0;
        int rowOpenings = 0;
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
            }
            else
            {
                if (i == 0)
                {
                    throw Corrupt(label, "the first slot continues a row that was never opened");
                }

                // Continuation legality at level `rep` (§2.10.3 clause 2): this slot continues R_rep's current
                // occurrence, so reject iff that occurrence was opened null/empty earlier in the row
                // (ownerComplete) OR this slot is not itself an element-bearing occurrence (def < presentDef).
                // Fires at EVERY repeated level r = 1..k, exactly the reader's `ownerComplete || d < thisMaxDef`.
                RepeatedLevel continued = chain[rep - 1];
                if (ownerComplete[rep] || def < continued.PresentDef)
                {
                    throw Corrupt(
                        label,
                        $"slot {i} continues repeated level {rep} at definition level {def}, which encodes an "
                        + "absent or empty container (or continues a container opened absent or empty); an "
                        + "empty or null repeated container has no continuation");
                }
            }

            // Re-open the deeper levels this slot starts (§2.10.3 clause 3): a rep = r slot opens a new owner
            // for every level v > r (and, when r == 0, for all v = 1..k); it does NOT re-open level r itself.
            int lo = rep == 0 ? 1 : rep + 1;
            for (int v = lo; v <= k; v++)
            {
                RepeatedLevel level = chain[v - 1];
                ownerComplete[v] = def < level.ParentPresentDef ? true : def < level.PresentDef;
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
        // from the source vectors' own null masks. Two independent derivations of the same quantity.
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
    /// (Architect #9) — the single-level shredder derives its level tables from it too, so the encoder and this
    /// guard can never disagree about where the container/leaf boundary sits.
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
