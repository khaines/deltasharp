using DeltaSharp.Storage.Parquet;
using Xunit;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// The §2.3c <b>cross-leaf</b> clauses: <see cref="NestedColumnShredder.ValidateMapParallelLevels"/> and
/// <see cref="NestedColumnShredder.ValidateStructNullParity"/>.
/// </summary>
/// <remarks>
/// <para>These two guards are the only controls that see MORE THAN ONE leaf at a time, and neither defect
/// they catch is visible to the per-leaf <see cref="NestedLevelGuard"/>: every stream below is individually
/// well-formed. A map whose key and value repetition streams re-partition the same slots into different rows
/// persists silently and mis-pairs entries on read; a struct whose children disagree about whether the struct
/// is null reads back as an availability error in DeltaSharp and as WRONG ROWS in Spark.</para>
/// <para>Each defect is injected DIRECTLY, because a correct shredder can never produce it — a test that
/// could only reach these guards through the shredder would be vacuous — and each guard also carries a
/// non-vacuity positive over the normative literal streams, so a guard that rejected everything would fail
/// here too.</para>
/// </remarks>
public sealed class NestedCrossLeafGuardTests
{
    // ----- map: one repeated key_value group, two leaves -----

    [Fact]
    public void MapGuard_AcceptsTheNormativeEncoding()
    {
        // Rows {a:1, b:null} / null / {} / {c:3} at mapMaxDef 2 — the exact streams
        // NestedParquetLevelStreamTests reads back off the wire.
        NestedColumnShredder.ValidateMapParallelLevels(
            keyDef: new[] { 2, 2, 0, 1, 2 },
            valueDef: new[] { 3, 2, 0, 1, 3 },
            keyRep: new[] { 0, 1, 0, 0, 0 },
            valueRep: new[] { 0, 1, 0, 0, 0 },
            mapMaxDef: 2,
            label: "m");
    }

    [Fact]
    public void MapGuard_RejectsARepetitionRePartition()
    {
        // Both streams are individually legal (each opens a row at slot 0 and continues legally), but they
        // partition the SAME three slots into different rows: keys read as one 2-entry row then a 1-entry
        // row, values as a 1-entry row then a 2-entry row. Every entry after the first is mis-paired.
        AssertRejected(
            () => NestedColumnShredder.ValidateMapParallelLevels(
                keyDef: new[] { 2, 2, 2 },
                valueDef: new[] { 3, 3, 3 },
                keyRep: new[] { 0, 1, 0 },
                valueRep: new[] { 0, 0, 1 },
                mapMaxDef: 2,
                label: "m"),
            "repetition levels diverge at slot 1");
    }

    [Fact]
    public void MapGuard_RejectsAKeyValueDefinitionDisagreementBelowTheMapLevel()
    {
        // Slot 0: the key says def 0 (a NULL map) and the value says def 1 (an EMPTY map). Both are below
        // mapMaxDef, so both agree "no entry here" — but they disagree about the CONTAINER's state, and the
        // reader takes the container's nullity from one leaf. The row would read as null for one and as an
        // empty map for the other.
        AssertRejected(
            () => NestedColumnShredder.ValidateMapParallelLevels(
                keyDef: new[] { 0 },
                valueDef: new[] { 1 },
                keyRep: new[] { 0 },
                valueRep: new[] { 0 },
                mapMaxDef: 2,
                label: "m"),
            "null map vs empty map");
    }

    [Fact]
    public void MapGuard_RejectsAKeyValueDisagreementOnEntryPresence()
    {
        AssertRejected(
            () => NestedColumnShredder.ValidateMapParallelLevels(
                keyDef: new[] { 2 },
                valueDef: new[] { 1 },
                keyRep: new[] { 0 },
                valueRep: new[] { 0 },
                mapMaxDef: 2,
                label: "m"),
            "disagree on entry presence at slot 0");
    }

    [Fact]
    public void MapGuard_RejectsStreamsOfDifferentLengths()
    {
        AssertRejected(
            () => NestedColumnShredder.ValidateMapParallelLevels(
                keyDef: new[] { 2, 2 },
                valueDef: new[] { 2 },
                keyRep: new[] { 0, 1 },
                valueRep: new[] { 0, 1 },
                mapMaxDef: 2,
                label: "m"),
            "different lengths");
    }

    // ----- struct: every child vs the SOURCE null mask -----

    // Packs a per-row struct-null mask into the bitmap shape the shredder captures from the source vectors.
    private static ulong[] NullMask(params bool[] rows)
    {
        var words = new ulong[NestedColumnShredder.NullBitmapWords(rows.Length)];
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i])
            {
                words[i >> 6] |= 1UL << (i & 63);
            }
        }

        return words;
    }

    [Fact]
    public void StructGuard_AcceptsTheNormativeEncoding()
    {
        // Rows {1,"x"} / null / {null,"y"} / {3,null} at structMaxDef 1 — the children disagree about their
        // OWN nullity at rows 2 and 3 (which is legal and expected) but both agree with the STRUCT's own null
        // mask at all four rows.
        ulong[] mask = NullMask(false, true, false, false);
        NestedColumnShredder.ValidateStructNullParity(
            new[] { 2, 0, 1, 2 }, mask, ordinal: 0, structMaxDef: 1, label: "s");
        NestedColumnShredder.ValidateStructNullParity(
            new[] { 2, 0, 2, 1 }, mask, ordinal: 1, structMaxDef: 1, label: "s");
    }

    [Fact]
    public void StructGuard_RejectsAChildThatDisagreesWithTheSourceNullMask()
    {
        // Row 1: the source says the struct is PRESENT, but this child emits def 0 (below structMaxDef 1 —
        // "the struct is null"). The stream is individually well-formed, and a sibling encoding def 2 at the
        // same row would keep the file structurally plausible while Spark read the wrong rows.
        AssertRejected(
            () => NestedColumnShredder.ValidateStructNullParity(
                new[] { 2, 0 }, NullMask(false, false), ordinal: 0, structMaxDef: 1, label: "s"),
            "at row 1 encodes the struct as null but the source column vector reports it present");
    }

    [Fact]
    public void StructGuard_RejectsTheDisagreementInEitherDirection()
    {
        // The mirror: the source says NULL and the child encodes a present struct. Binding to the source (D2)
        // rather than to a sibling is what makes both directions detectable on a SINGLE child — the old
        // sibling-to-sibling comparison was satisfied by construction whenever every child walked the same mask.
        AssertRejected(
            () => NestedColumnShredder.ValidateStructNullParity(
                new[] { 2, 2 }, NullMask(false, true), ordinal: 1, structMaxDef: 1, label: "s"),
            "at row 1 encodes the struct as present but the source column vector reports it null");
    }

    [Fact]
    public void StructGuard_RejectsADisagreementOnAThirdChild()
    {
        // Parity is checked for EVERY child, not just the first pair — the ordinal is reported so the failing
        // child is attributable.
        AssertRejected(
            () => NestedColumnShredder.ValidateStructNullParity(
                new[] { 1 }, NullMask(true), ordinal: 2, structMaxDef: 1, label: "s"),
            "field 2: the shredded definition level at row 0");
    }

    [Fact]
    public void StructGuard_IsInertForARequiredStruct()
    {
        // structMaxDef 0 means the struct itself cannot be null, so there is no parity to check and the guard
        // must not read the mask at all.
        NestedColumnShredder.ValidateStructNullParity(
            new[] { 0, 0 }, Array.Empty<ulong>(), ordinal: 0, structMaxDef: 0, label: "s");
    }

    private static void AssertRejected(Action act, string expectedFragment)
    {
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(act);
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains(expectedFragment, error.Message, StringComparison.Ordinal);
    }
}
