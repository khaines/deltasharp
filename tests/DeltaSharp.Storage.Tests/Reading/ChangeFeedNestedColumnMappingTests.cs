using System.Globalization;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Tests.Delta;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Xunit;
using Xunit.Abstractions;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// The <b>NESTED</b> (struct / array / map) Change Data Feed column-mapping oracle — axis 2 of the #661 CDF
/// hardening, deferred by PR #674 until nested column mapping (#676) landed and now reachable end-to-end.
/// </summary>
/// <remarks>
/// <para>This file EXTENDS the flat CDF model-replay oracle (<see cref="ChangeFeedModelReplayTests"/> /
/// <see cref="ChangeFeedScenario"/>, axis 1 in <see cref="ChangeFeedRenameDropTests"/>) to nested-typed mapped
/// columns without forking a parallel oracle: it reuses the SAME production CDF read door
/// (<see cref="DeltaReadSource.LoadChangeFeedAsync"/> + <see cref="DeltaReadSource.ReadChangeBatchesAsync"/>)
/// and the SAME reconciled-output-schema + change-multiset assertions. The one piece it cannot reuse is
/// <see cref="CdfTable"/>'s <see cref="DeltaWriteTarget"/>-based write door — that facade cannot encode a
/// nested-typed batch through its scalar partitioner (see <see cref="NestedColumnMappingTests"/>) — so the
/// harness authors the nested history raw at the log the way the merged #676 tests do: the mapping is minted
/// by <see cref="ColumnMapping.AssignFreshMapping"/>, each commit's data file is a REAL physical Parquet file
/// written by the merged nested writer, and add/remove actions are assembled directly. The READ is always the
/// unmodified production <see cref="ChangeFeedReader"/>; only the log authoring is hand-rolled, which is
/// exactly the forged-<c>_delta_log</c> threat surface the tamper cells exercise.</para>
/// <para><b>Coverage (the #675 ACs):</b>
/// <list type="bullet">
/// <item>AC1 — nested-mapped histories: <c>struct&lt;a,b&gt;</c> (name + id mode), <c>array&lt;string&gt;</c>
/// and <c>map&lt;string,long&gt;</c> in BOTH name mode AND id mode (id-mode array/map now READS THROUGH the CDF
/// door once #854 lifted the #839 fail-closed gate for a container carrying a valid
/// <c>delta.columnMapping.nested.ids</c>). Append / overwrite / delete across versions (overwrite exercised for
/// array AND map under id mode); change-row value fidelity for the nested column (struct children / array
/// elements / map entries — not just counts), NULL-vs-EMPTY-vs-PRESENT container distinctness in BOTH name and
/// id mode, AND reconciled OUTPUT SCHEMA physical↔logical fidelity for the nested leaves (name-mode
/// <c>col-&lt;uuid&gt;</c> physical names; id-mode struct-child <c>field_id</c>s and array/map interior
/// <c>nested.ids</c> ids, each validated in <c>[1, maxColumnId]</c>).</item>
/// <item>AC1b — nested type WIDENING across CDF versions (#546): an <c>int→long</c> widening under a
/// typeWidening-enabled table READ-PROMOTES the pre-widening narrow file's nested leaf in NAME mode for ALL
/// THREE nested shapes — a <c>struct</c> child (<c>struct&lt;a:int&gt;→struct&lt;a:long&gt;</c>), an ARRAY
/// element (<c>array&lt;int&gt;→array&lt;long&gt;</c>), and a MAP value
/// (<c>map&lt;_,int&gt;→map&lt;_,long&gt;</c>; the map KEY is left unchanged, key widening being neither
/// sanctioned nor typical) — with a post-widen value &gt; <c>int.MaxValue</c> proving genuine 64-bit width. In
/// ID mode each of the three fails CLOSED (a DELIBERATE non-promotion — #546 §9 O1 hardcodes
/// <c>promoteLeaf: false</c> on the by-field-id resolution path — pinned by SchemaMismatch reason, see the
/// Finding on <see cref="ChangeFeedNestedColumnMappingTests.IdMode_NestedLeafWidening_AcrossCdfVersions_FailsClosed_546"/>).</item>
/// <item>AC2 — nested-leaf CDF identity immutability across retained versions: a nested-child LOGICAL rename
/// (id + physicalName preserved) reads through, and a nested-child metadata-only DROP reads through (the
/// dropped leaf projected out, the survivor promoted to the END shape); a forged nested-child identity CHANGE
/// fails closed.</item>
/// <item>AC3 — a seeded 200-iteration nested-leaf tamper fuzz that fails closed with a TYPED exception on every
/// enumerated tamper; same-typed siblings draw from DISJOINT domains so a positional mis-bind cannot pass.</item>
/// <item>AC4 — boundary: a PLAIN id-mode <c>array</c>/<c>map</c> with NO <c>nested.ids</c> (the residue of #839
/// still fail-closed after #854) and a nested-within-nested (#585) CDF table fail closed at the load/read door
/// (not a silent skip); name-mode array/map — and id-mode array/map WITH <c>nested.ids</c> — stay fully
/// exercised as read-through above.</item>
/// </list></para>
/// <para>Every same-typed sibling (the <c>struct&lt;a:long,b:long&gt;</c> children, the array elements, the
/// map values) is drawn from a DISJOINT numeric/string domain, so a physical→logical mis-bind surfaces as a
/// value mismatch rather than passing on equal values (design §3 preamble, shared with the #676 oracle).</para>
/// <para><b>Scope.</b> This oracle exercises the IMPLICIT add/remove CDF derivation path only (append /
/// overwrite / delete files); it does NOT cover explicit <c>_change_data</c> (cdc) files or deletion vectors
/// (DV), and its tables carry NO partition columns. Nested type WIDENING (#546) across CDF versions IS now
/// exercised (AC1b) — name-mode read-promotion and the id-mode deliberate fail-closed — extending the original
/// #849 scope (which deferred it before #854/#546 landed).</para>
/// </remarks>
[Collection(ColumnMappingTestCollection.Name)]
public sealed class ChangeFeedNestedColumnMappingTests
{
    private const string Scope = nameof(ChangeFeedNestedColumnMappingTests);

    private readonly ITestOutputHelper _output;

    public ChangeFeedNestedColumnMappingTests(ITestOutputHelper output) => _output = output;

    // Dispose ownership: EACH created NestedCdfTable is scoped by a `using` at its creation site (the public
    // tests, the fuzz iteration, the clean baseline, and the boundary cells), so each table's backend + temp
    // dir are released promptly at the end of its owning scope. The harness holds no table list and is not
    // IDisposable — a single, unambiguous ownership model with no double-dispose (a 200-iteration fuzz would
    // otherwise accumulate 200 undisposed temp dirs until class teardown).

    // ============================================================================================
    // AC1 · Nested-mapped histories — value fidelity + reconciled output-schema physical↔logical fidelity
    // ============================================================================================

    /// <summary>
    /// Struct name mode. A CDF history over <c>{id:long, pt:struct&lt;a:long,b:long&gt;}</c> appends, deletes a
    /// file, then static-overwrites. The whole range's change multiset must equal the model's — with the nested
    /// struct's children (a from [1000..], b from [2000..], disjoint) read through EXACTLY — and the reconciled
    /// output schema must surface the nested column under its LOGICAL child names while the end snapshot's
    /// committed schema carries the physical <c>col-&lt;uuid&gt;</c> names on those same leaves (name mode:
    /// physical ≠ logical).
    /// </summary>
    [Fact]
    public async Task NameMode_NestedStruct_CdfHistory_ValueFidelity_AndOutputSchemaReconciles()
    {
        using NestedCdfTable table = NewStructTable(ColumnMappingMode.Name);
        await table.CreateAsync();                                                          // v0
        var model = new List<ExpectedChange>();

        NestedCdfTable.FileRef f1 = await table.AppendAsync(StructBatch(table, (1, 1001, 2001), (2, 1002, 2002)));
        AddInserts(model, 1, StructSig(1, 1001, 2001), StructSig(2, 1002, 2002));           // v1
        NestedCdfTable.FileRef f2 = await table.AppendAsync(StructBatch(table, (3, 1003, 2003)));
        AddInserts(model, 2, StructSig(3, 1003, 2003));                                     // v2
        await table.RemoveAsync(f2);
        AddDeletes(model, 3, StructSig(3, 1003, 2003));                                     // v3 delete file f2
        await table.OverwriteAsync(StructBatch(table, (4, 1004, 2004)), f1);
        AddDeletes(model, 4, StructSig(1, 1001, 2001), StructSig(2, 1002, 2002));           // v4 delete f1 rows
        AddInserts(model, 4, StructSig(4, 1004, 2004));                                     // v4 insert

        (StructType schema, List<ActualChange> changes) =
            await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 4), DecodeStruct);

        AssertMultisetEqual(model, changes);

        // Reconciled output schema: nested column present under LOGICAL child names a,b (long); metadata trails.
        StructField pt = FindField(schema, "pt");
        var ptStruct = Assert.IsType<StructType>(pt.DataType);
        Assert.Equal(new[] { "a", "b" }, ptStruct.Select(c => c.Name).ToArray());
        Assert.All(ptStruct, c => Assert.Equal(DataTypes.LongType, c.DataType));

        // End snapshot committed schema: the nested leaves carry PHYSICAL col-<uuid> names that reconcile back
        // to the logical a,b (name mode: physical != logical) — the physical↔logical fidelity witness.
        StructType endSchema = await table.LoadEndSchemaAsync();
        var endPt = (StructType)endSchema["pt"].DataType;
        foreach (string child in new[] { "a", "b" })
        {
            string physical = Physical(endPt[child]);
            Assert.StartsWith("col-", physical, StringComparison.Ordinal);
            Assert.NotEqual(child, physical);
        }
    }

    /// <summary>
    /// Struct id mode. The same history under id mode, where each nested child leaf binds by <c>field_id</c>
    /// within the container. Change-row value fidelity (disjoint a/b domains) AND the end snapshot's nested
    /// leaves each carry their own <c>delta.columnMapping.id</c> reconciling to the logical child.
    /// </summary>
    [Fact]
    public async Task IdMode_NestedStruct_CdfHistory_ValueFidelity_AndNestedLeafFieldIdsReconcile()
    {
        using NestedCdfTable table = NewStructTable(ColumnMappingMode.Id);
        await table.CreateAsync();
        var model = new List<ExpectedChange>();

        NestedCdfTable.FileRef f1 = await table.AppendAsync(StructBatch(table, (1, 1001, 2001), (2, 1002, 2002)));
        AddInserts(model, 1, StructSig(1, 1001, 2001), StructSig(2, 1002, 2002));
        await table.OverwriteAsync(StructBatch(table, (3, 1003, 2003)), f1);
        AddDeletes(model, 2, StructSig(1, 1001, 2001), StructSig(2, 1002, 2002));
        AddInserts(model, 2, StructSig(3, 1003, 2003));

        (StructType schema, List<ActualChange> changes) =
            await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 2), DecodeStruct);

        AssertMultisetEqual(model, changes);

        StructField pt = FindField(schema, "pt");
        var ptStruct = Assert.IsType<StructType>(pt.DataType);
        Assert.Equal(new[] { "a", "b" }, ptStruct.Select(c => c.Name).ToArray());

        // Id mode: every nested-struct-child leaf carries its own field_id in the committed schema.
        StructType endSchema = await table.LoadEndSchemaAsync();
        var endPt = (StructType)endSchema["pt"].DataType;
        Assert.True(ColumnMapping.TryGetId(endPt["a"], out long aId) && aId > 0);
        Assert.True(ColumnMapping.TryGetId(endPt["b"], out long bId) && bId > 0);
        Assert.NotEqual(aId, bId);
    }

    /// <summary>
    /// Array name mode. A CDF history over <c>{id:long, tags:array&lt;string&gt;}</c> — value fidelity for the
    /// array ELEMENTS (order + null-vs-empty distinctions), across append/overwrite.
    /// </summary>
    [Fact]
    public async Task NameMode_NestedArray_CdfHistory_ElementValueFidelity()
    {
        using NestedCdfTable table = NewArrayTable();
        await table.CreateAsync();
        var model = new List<ExpectedChange>();

        NestedCdfTable.FileRef f1 = await table.AppendAsync(ArrayBatch(
            table, (1, new[] { "a1", "a2" }), (2, new[] { "b1" })));
        AddInserts(model, 1, ArraySig(1, "a1", "a2"), ArraySig(2, "b1"));
        NestedCdfTable.FileRef f2 = await table.AppendAsync(ArrayBatch(table, (3, Array.Empty<string>())));
        AddInserts(model, 2, ArraySig(3));                       // empty array, distinct from null
        await table.OverwriteAsync(ArrayBatch(table, (4, new[] { "d1", "d2", "d3" })), f1, f2);
        AddDeletes(model, 3, ArraySig(1, "a1", "a2"), ArraySig(2, "b1"), ArraySig(3));
        AddInserts(model, 3, ArraySig(4, "d1", "d2", "d3"));

        (StructType schema, List<ActualChange> changes) =
            await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 3), DecodeArray);

        AssertMultisetEqual(model, changes);
        StructField tags = FindField(schema, "tags");
        var arr = Assert.IsType<ArrayType>(tags.DataType);
        Assert.Equal(DataTypes.StringType, arr.ElementType);
    }

    /// <summary>
    /// Map name mode. A CDF history over <c>{id:long, props:map&lt;string,long&gt;}</c> — value fidelity for
    /// the map KEY/VALUE entries (values drawn from [5000..], disjoint from ids), across append/delete.
    /// </summary>
    [Fact]
    public async Task NameMode_NestedMap_CdfHistory_EntryValueFidelity()
    {
        using NestedCdfTable table = NewMapTable();
        await table.CreateAsync();
        var model = new List<ExpectedChange>();

        NestedCdfTable.FileRef f1 = await table.AppendAsync(MapBatch(
            table, (1, new[] { ("w", 5001L), ("h", 5002L) }), (2, new[] { ("z", 5003L) })));
        AddInserts(model, 1, MapSig(1, ("w", 5001), ("h", 5002)), MapSig(2, ("z", 5003)));
        await table.AppendAsync(MapBatch(table, (3, new[] { ("k", 5004L) })));
        AddInserts(model, 2, MapSig(3, ("k", 5004)));
        await table.RemoveAsync(f1);
        AddDeletes(model, 3, MapSig(1, ("w", 5001), ("h", 5002)), MapSig(2, ("z", 5003)));

        (StructType schema, List<ActualChange> changes) =
            await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 3), DecodeMap);

        AssertMultisetEqual(model, changes);
        StructField props = FindField(schema, "props");
        var map = Assert.IsType<MapType>(props.DataType);
        Assert.Equal(DataTypes.StringType, map.KeyType);
        Assert.Equal(DataTypes.LongType, map.ValueType);
    }

    /// <summary>
    /// Array id mode (#854/#839 read-through). A CDF history over <c>{id:long, tags:array&lt;string&gt;}</c>
    /// under id mode — the container binds by <c>physicalName</c>, its element leaf binds by the
    /// <c>delta.columnMapping.nested.ids</c> element <c>field_id</c> (#854 lifted the #839 fail-closed load-door
    /// gate for an id-mode array/map that carries a valid <c>nested.ids</c>). Asserts element value fidelity
    /// (order + empty-vs-present) across append/overwrite AND the end snapshot's container reconciles: its own
    /// <c>delta.columnMapping.id</c> plus a <c>nested.ids</c> element id recorded in the END schema, distinct
    /// from the container id and in <c>[1, maxColumnId]</c> — the id-mode physical↔logical witness for an
    /// array interior. The dual (this exact shape under NAME mode) is <see cref="NameMode_NestedArray_CdfHistory_ElementValueFidelity"/>.
    /// </summary>
    [Fact]
    public async Task IdMode_NestedArray_CdfHistory_ElementValueFidelity_AndNestedIdsReconcile()
    {
        using NestedCdfTable table = NewArrayTable(ColumnMappingMode.Id);
        await table.CreateAsync();
        var model = new List<ExpectedChange>();

        NestedCdfTable.FileRef f1 = await table.AppendAsync(ArrayBatch(
            table, (1, new[] { "a1", "a2" }), (2, new[] { "b1" })));
        AddInserts(model, 1, ArraySig(1, "a1", "a2"), ArraySig(2, "b1"));
        NestedCdfTable.FileRef f2 = await table.AppendAsync(ArrayBatch(table, (3, Array.Empty<string>())));
        AddInserts(model, 2, ArraySig(3));                       // empty array, distinct from null
        await table.OverwriteAsync(ArrayBatch(table, (4, new[] { "d1", "d2", "d3" })), f1, f2);
        AddDeletes(model, 3, ArraySig(1, "a1", "a2"), ArraySig(2, "b1"), ArraySig(3));
        AddInserts(model, 3, ArraySig(4, "d1", "d2", "d3"));

        (StructType schema, List<ActualChange> changes) =
            await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 3), DecodeArray);

        AssertMultisetEqual(model, changes);
        StructField tags = FindField(schema, "tags");
        Assert.Equal(DataTypes.StringType, Assert.IsType<ArrayType>(tags.DataType).ElementType);

        // Id-mode physical↔logical witness: the end snapshot's container CARRIES its own field_id AND a
        // nested.ids element id — the END schema records the interior element field_id, validated in
        // [1, maxColumnId] and distinct from the container id. (This proves the reconciled schema surfaces the
        // interior id; it does NOT attempt to discriminate by-id vs positional binding for a single fixed leaf.)
        StructType endSchema = await table.LoadEndSchemaAsync();
        StructField endTags = endSchema["tags"];
        Assert.True(ColumnMapping.TryGetId(endTags, out long containerId) && containerId > 0);
        Assert.True(
            ColumnMapping.TryGetArrayElementId(endTags, Physical(endTags), out long elementId) && elementId > 0);
        Assert.True(elementId <= table.MaxColumnId);
        Assert.NotEqual(containerId, elementId);
    }

    /// <summary>
    /// Map id mode (#854/#839 read-through). A CDF history over <c>{id:long, props:map&lt;string,long&gt;}</c>
    /// under id mode — the container binds by <c>physicalName</c>, its key/value leaves bind by the DISTINCT
    /// <c>delta.columnMapping.nested.ids</c> key/value <c>field_id</c>s. Asserts key/value entry value fidelity
    /// (values from [5000..], disjoint from ids) across append/delete AND the end snapshot's container
    /// reconciles: its own id plus DISTINCT <c>nested.ids</c> key+value ids, all in <c>[1, maxColumnId]</c> —
    /// the id-mode physical↔logical witness for a map interior. The dual under NAME mode is
    /// <see cref="NameMode_NestedMap_CdfHistory_EntryValueFidelity"/>.
    /// </summary>
    [Fact]
    public async Task IdMode_NestedMap_CdfHistory_EntryValueFidelity_AndNestedIdsReconcile()
    {
        using NestedCdfTable table = NewMapTable(ColumnMappingMode.Id);
        await table.CreateAsync();
        var model = new List<ExpectedChange>();

        NestedCdfTable.FileRef f1 = await table.AppendAsync(MapBatch(
            table, (1, new[] { ("w", 5001L), ("h", 5002L) }), (2, new[] { ("z", 5003L) })));
        AddInserts(model, 1, MapSig(1, ("w", 5001), ("h", 5002)), MapSig(2, ("z", 5003)));
        NestedCdfTable.FileRef f2 = await table.AppendAsync(MapBatch(table, (3, new[] { ("k", 5004L) })));
        AddInserts(model, 2, MapSig(3, ("k", 5004)));
        // Overwrite (delete + insert in one commit) — symmetric with the id-mode ARRAY history's overwrite so a
        // map's overwrite delete+insert change rows are exercised under id mode too.
        await table.OverwriteAsync(MapBatch(table, (4, new[] { ("q", 5005L), ("r", 5006L) })), f1, f2);
        AddDeletes(model, 3, MapSig(1, ("w", 5001), ("h", 5002)), MapSig(2, ("z", 5003)), MapSig(3, ("k", 5004)));
        AddInserts(model, 3, MapSig(4, ("q", 5005), ("r", 5006)));

        (StructType schema, List<ActualChange> changes) =
            await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 3), DecodeMap);

        AssertMultisetEqual(model, changes);
        StructField props = FindField(schema, "props");
        var map = Assert.IsType<MapType>(props.DataType);
        Assert.Equal(DataTypes.StringType, map.KeyType);
        Assert.Equal(DataTypes.LongType, map.ValueType);

        // Id-mode physical↔logical witness: the END schema CARRIES the container's own id + DISTINCT nested.ids
        // key/value ids, all validated in [1, maxColumnId] and mutually distinct. (This proves the reconciled
        // schema surfaces the interior ids; it does NOT discriminate by-id vs positional binding.)
        StructType endSchema = await table.LoadEndSchemaAsync();
        StructField endProps = endSchema["props"];
        Assert.True(ColumnMapping.TryGetId(endProps, out long containerId) && containerId > 0);
        Assert.True(
            ColumnMapping.TryGetMapKeyValueIds(endProps, Physical(endProps), out long keyId, out long valueId));
        Assert.True(keyId > 0 && valueId > 0);
        Assert.True(keyId <= table.MaxColumnId && valueId <= table.MaxColumnId);
        Assert.Equal(3, new[] { containerId, keyId, valueId }.Distinct().Count());
    }

    // ============================================================================================
    // AC1b · Nested type WIDENING across CDF versions (#546 read-promotion — lifts the #849 scope note)
    // ============================================================================================

    /// <summary>
    /// NAME-mode nested type WIDENING across CDF versions (#546 read-promotion). A typeWidening-enabled
    /// nested-mapped CDF history appends a NARROW file (<c>pt:struct&lt;a:int,b:int&gt;</c>), then a later
    /// version WIDENS the nested leaves <c>int→long</c> (a metadata-only commit; every leaf id/physicalName
    /// preserved — the widening keeps identity, only the leaf type changes), then appends a WIDE file carrying a
    /// value only representable as long (&gt; <see cref="int.MaxValue"/>). Reading across the widening boundary
    /// must RECONCILE to the END (wide) schema AND READ-PROMOTE the pre-widening narrow file's nested leaves to
    /// long — value fidelity across the boundary, mirroring the flat <c>int→long</c> widening CDF cell
    /// (<c>IntToLongWideningAcrossRange_EarlierNarrowValuesPromote</c>). #546 (nested type-widening promotion)
    /// is what lifts this from the #849 scope note's "nested type WIDENING across CDF versions is out of scope."
    /// The ID-mode dual is <see cref="IdMode_NestedLeafWidening_AcrossCdfVersions_FailsClosed_546"/>: id-mode
    /// nested leaves are DELIBERATELY not promoted (#546 §9 O1).
    /// </summary>
    [Fact]
    public async Task NameMode_NestedLeafWidening_AcrossCdfVersions_ReadPromotesEarlierNarrowValues()
    {
        using NestedCdfTable table = NewWideningStructTable(ColumnMappingMode.Name);
        await table.CreateAsync();                                                         // v0 (narrow int a,b)
        var model = new List<ExpectedChange>();

        await table.AppendAsync(StructIntBatch(table, (1, 1001, 2001), (2, 1002, 2002)));  // v1 narrow int file
        AddInserts(model, 1, StructSig(1, 1001, 2001), StructSig(2, 1002, 2002));

        // v2: metaData-only widen pt.a, pt.b int->long (id + physicalName preserved). Contributes ZERO change
        // rows; the v1 file keeps its NARROW int bytes on disk (the read must promote them to the END long).
        StructType wideLogical = WidenStructChildrenToLong(table.LogicalSchema, "pt");
        StructType wideMapped = WidenStructChildrenToLong(table.MappedSchema, "pt");
        await table.WidenAsync(wideLogical, wideMapped, table.MaxColumnId);

        // v3: WIDE append — a value only representable as long (> int.MaxValue) proves the column is genuinely
        // widened (not truncated to 32 bits), exactly as the flat widening cell does.
        await table.AppendAsync(StructBatch(table, (3, 3_000_000_000L, 2003)));            // v3 wide long file
        AddInserts(model, 3, StructSig(3, 3_000_000_000L, 2003));

        (StructType schema, List<ActualChange> changes) =
            await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 3), DecodeStruct);

        // Reconciled END schema: pt.a, pt.b are LONG (the widened type) — the widening witness.
        var ptStruct = (StructType)FindField(schema, "pt").DataType;
        Assert.Equal(new[] { "a", "b" }, ptStruct.Select(c => c.Name).ToArray());
        Assert.All(ptStruct, c => Assert.Equal(DataTypes.LongType, c.DataType));

        // The v1 NARROW int values read-PROMOTE to long; the v3 long-only value reads through — value fidelity
        // across the widening boundary. Disjoint a/b domains keep a positional mis-bind detectable.
        AssertMultisetEqual(model, changes);
    }

    /// <summary>
    /// ID-mode nested type WIDENING across CDF versions FAILS CLOSED (#546 §9 O1 — a DELIBERATE non-promotion,
    /// not a bug). This is the SAME history as the name-mode read-through cell
    /// (<see cref="NameMode_NestedLeafWidening_AcrossCdfVersions_ReadPromotesEarlierNarrowValues"/>) but under
    /// ID mode, where a nested struct child binds by <c>field_id</c> through
    /// <c>NestedParquetColumnReader.ResolveStructFieldById</c>, which hardcodes <c>promoteLeaf: false</c>: an
    /// id-mode nested leaf is NEVER read-promoted (design §9 O1). So the pre-widening narrow (int) file, read
    /// against the END (long) schema, is a physical-type disagreement that fails the CDF read CLOSED with a
    /// typed <see cref="DeltaReadException"/> (wrapping a <see cref="DeltaStorageException"/> SchemaMismatch)
    /// rather than silently mis-reading 32-bit bytes as a 64-bit value.
    /// <para><b>Finding (residual #675 item 2).</b> Nested type-widening READ-PROMOTION is reachable through
    /// the CDF door in NAME mode but is deliberately fail-closed in ID mode (#546 §9 O1 hardcodes
    /// <c>promoteLeaf: false</c> on the by-field-id resolution path). This cell pins that mode asymmetry as the
    /// current contract; if a future change wires id-mode nested-leaf promotion, this cell should flip to a
    /// read-through oracle mirroring the name-mode cell.</para>
    /// </summary>
    [Fact]
    public async Task IdMode_NestedLeafWidening_AcrossCdfVersions_FailsClosed_546()
    {
        using NestedCdfTable table = NewWideningStructTable(ColumnMappingMode.Id);
        await table.CreateAsync();                                                         // v0 (narrow int a,b)
        await table.AppendAsync(StructIntBatch(table, (1, 1001, 2001), (2, 1002, 2002)));  // v1 narrow int file

        StructType wideLogical = WidenStructChildrenToLong(table.LogicalSchema, "pt");
        StructType wideMapped = WidenStructChildrenToLong(table.MappedSchema, "pt");
        await table.WidenAsync(wideLogical, wideMapped, table.MaxColumnId);                // v2 widen int->long
        await table.AppendAsync(StructBatch(table, (3, 3_000_000_000L, 2003)));            // v3 wide long file

        // The read must fail closed: the v1 narrow (int) leaf resolved by field_id against the END (long) leaf
        // is a physical-type disagreement, and id-mode leaves are never promoted (§9 O1). The shared helper
        // pins the SchemaMismatch + "does not match the requested" reason so a wrong-but-thrown typed error
        // cannot masquerade as the deliberate non-promotion gate.
        await AssertIdModeWideningFailsClosedAsync(table, DeltaChangeFeedRange.FromVersion(1, 3), DecodeStruct);
    }

    /// <summary>
    /// NAME-mode ARRAY-ELEMENT type WIDENING across CDF versions (#546/#854 read-promotion). A typeWidening +
    /// column-mapping table over <c>{id:long, tags:array&lt;int&gt;}</c> appends a NARROW <c>array&lt;int&gt;</c>
    /// file, WIDENS the element leaf <c>int→long</c> (metadata-only; container id + physicalName preserved),
    /// then appends a WIDE <c>array&lt;long&gt;</c> file carrying an element only representable as long
    /// (&gt; <see cref="int.MaxValue"/>). The CDF read must reconcile to the END <c>array&lt;long&gt;</c> schema
    /// AND READ-PROMOTE the pre-widening narrow file's ELEMENTS to long (NestedParquetColumnReader promotes the
    /// element leaf at container depth ≤ 1 in name mode). The id-mode dual fails closed
    /// (<see cref="IdMode_NestedArrayElementWidening_AcrossCdfVersions_FailsClosed_546"/>).
    /// </summary>
    [Fact]
    public async Task NameMode_NestedArrayElementWidening_AcrossCdfVersions_ReadPromotesEarlierNarrowValues()
    {
        using NestedCdfTable table = NewWideningArrayTable(ColumnMappingMode.Name);
        await table.CreateAsync();                                                         // v0 (narrow array<int>)
        var model = new List<ExpectedChange>();

        await table.AppendAsync(ArrayIntBatch(table, (1, new[] { 10, 11 }), (2, new[] { 12 })));  // v1 narrow
        AddInserts(model, 1, LongArraySig(1, 10, 11), LongArraySig(2, 12));

        // v2: metaData-only widen tags element int->long (container id + physicalName preserved). Zero change
        // rows; the v1 file keeps its NARROW int element bytes on disk.
        StructType wideLogical = WidenArrayElementToLong(table.LogicalSchema, "tags");
        StructType wideMapped = WidenArrayElementToLong(table.MappedSchema, "tags");
        await table.WidenAsync(wideLogical, wideMapped, table.MaxColumnId);

        // v3: WIDE append — an element > int.MaxValue proves genuine 64-bit width.
        await table.AppendAsync(ArrayLongBatch(table, (3, new[] { 3_000_000_000L })));     // v3 wide array<long>
        AddInserts(model, 3, LongArraySig(3, 3_000_000_000L));

        (StructType schema, List<ActualChange> changes) =
            await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 3), DecodeLongArray);

        // Reconciled END schema: tags element is LONG — the widening witness.
        var arr = Assert.IsType<ArrayType>(FindField(schema, "tags").DataType);
        Assert.Equal(DataTypes.LongType, arr.ElementType);

        // The v1 NARROW int elements read-PROMOTE to long; the v3 long-only element reads through.
        AssertMultisetEqual(model, changes);
    }

    /// <summary>
    /// ID-mode ARRAY-ELEMENT widening across CDF versions FAILS CLOSED (#546 §9 O1). The SAME history as the
    /// name-mode array-element read-through cell but under id mode: the array interior binds by its
    /// <c>nested.ids</c> element <c>field_id</c> through the by-field-id path
    /// (<c>NestedParquetColumnReader.ReadListAsync</c>/<c>ExpectScalarLeaf</c> with <c>promoteLeaf: false</c>
    /// whenever <c>byFieldId</c> is non-null), so the pre-widening narrow (int) element read against the END
    /// (long) element is a physical-type disagreement that fails the read CLOSED — a deliberate non-promotion,
    /// reason-pinned exactly like the id-mode struct widening cell.
    /// </summary>
    [Fact]
    public async Task IdMode_NestedArrayElementWidening_AcrossCdfVersions_FailsClosed_546()
    {
        using NestedCdfTable table = NewWideningArrayTable(ColumnMappingMode.Id);
        await table.CreateAsync();
        await table.AppendAsync(ArrayIntBatch(table, (1, new[] { 10, 11 }), (2, new[] { 12 })));

        StructType wideLogical = WidenArrayElementToLong(table.LogicalSchema, "tags");
        StructType wideMapped = WidenArrayElementToLong(table.MappedSchema, "tags");
        await table.WidenAsync(wideLogical, wideMapped, table.MaxColumnId);
        await table.AppendAsync(ArrayLongBatch(table, (3, new[] { 3_000_000_000L })));

        await AssertIdModeWideningFailsClosedAsync(table, DeltaChangeFeedRange.FromVersion(1, 3), DecodeLongArray);
    }

    /// <summary>
    /// NAME-mode MAP-VALUE type WIDENING across CDF versions (#546/#854 read-promotion). A typeWidening +
    /// column-mapping table over <c>{id:long, props:map&lt;string,int&gt;}</c> appends a NARROW
    /// <c>map&lt;string,int&gt;</c> file, WIDENS the VALUE leaf <c>int→long</c> (metadata-only; container id +
    /// physicalName preserved — the map KEY stays <c>string</c>, since key widening is not a sanctioned/typical
    /// change), then appends a WIDE <c>map&lt;string,long&gt;</c> file with a value &gt; <see cref="int.MaxValue"/>.
    /// The CDF read must reconcile to the END <c>map&lt;string,long&gt;</c> schema AND READ-PROMOTE the
    /// pre-widening narrow file's VALUES to long. The id-mode dual fails closed
    /// (<see cref="IdMode_NestedMapValueWidening_AcrossCdfVersions_FailsClosed_546"/>).
    /// </summary>
    [Fact]
    public async Task NameMode_NestedMapValueWidening_AcrossCdfVersions_ReadPromotesEarlierNarrowValues()
    {
        using NestedCdfTable table = NewWideningMapTable(ColumnMappingMode.Name);
        await table.CreateAsync();                                                         // v0 (narrow map<_,int>)
        var model = new List<ExpectedChange>();

        await table.AppendAsync(MapIntBatch(table, (1, new[] { ("w", 100), ("h", 110) }), (2, new[] { ("z", 120) })));
        AddInserts(model, 1, MapSig(1, ("w", 100L), ("h", 110L)), MapSig(2, ("z", 120L)));

        // v2: metaData-only widen props VALUE int->long (container id + physicalName preserved; KEY unchanged).
        StructType wideLogical = WidenMapValueToLong(table.LogicalSchema, "props");
        StructType wideMapped = WidenMapValueToLong(table.MappedSchema, "props");
        await table.WidenAsync(wideLogical, wideMapped, table.MaxColumnId);

        // v3: WIDE append — a value > int.MaxValue proves genuine 64-bit width.
        await table.AppendAsync(MapLongBatch(table, (3, new[] { ("q", 3_000_000_000L) })));  // v3 wide map<_,long>
        AddInserts(model, 3, MapSig(3, ("q", 3_000_000_000L)));

        (StructType schema, List<ActualChange> changes) =
            await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 3), DecodeMap);

        // Reconciled END schema: props value is LONG (key stays string) — the widening witness.
        var map = Assert.IsType<MapType>(FindField(schema, "props").DataType);
        Assert.Equal(DataTypes.StringType, map.KeyType);
        Assert.Equal(DataTypes.LongType, map.ValueType);

        // The v1 NARROW int values read-PROMOTE to long; the v3 long-only value reads through.
        AssertMultisetEqual(model, changes);
    }

    /// <summary>
    /// ID-mode MAP-VALUE widening across CDF versions FAILS CLOSED (#546 §9 O1). The SAME history as the
    /// name-mode map-value read-through cell but under id mode: the map value binds by its <c>nested.ids</c>
    /// value <c>field_id</c> through the by-field-id path (<c>ReadMapAsync</c>/<c>ExpectScalarLeaf</c> with
    /// <c>promoteLeaf: false</c> whenever <c>byFieldId</c> is non-null), so the pre-widening narrow (int) value
    /// read against the END (long) value is a physical-type disagreement that fails the read CLOSED — a
    /// deliberate non-promotion, reason-pinned exactly like the id-mode struct/array widening cells.
    /// </summary>
    [Fact]
    public async Task IdMode_NestedMapValueWidening_AcrossCdfVersions_FailsClosed_546()
    {
        using NestedCdfTable table = NewWideningMapTable(ColumnMappingMode.Id);
        await table.CreateAsync();
        await table.AppendAsync(MapIntBatch(table, (1, new[] { ("w", 100), ("h", 110) }), (2, new[] { ("z", 120) })));

        StructType wideLogical = WidenMapValueToLong(table.LogicalSchema, "props");
        StructType wideMapped = WidenMapValueToLong(table.MappedSchema, "props");
        await table.WidenAsync(wideLogical, wideMapped, table.MaxColumnId);
        await table.AppendAsync(MapLongBatch(table, (3, new[] { ("q", 3_000_000_000L) })));

        await AssertIdModeWideningFailsClosedAsync(table, DeltaChangeFeedRange.FromVersion(1, 3), DecodeMap);
    }

    // ============================================================================================
    // AC2 · Nested-leaf CDF identity immutability across retained versions (end-to-end read door)
    // ============================================================================================

    /// <summary>
    /// A nested struct-child LOGICAL rename between retained versions (id + physicalName preserved) reads
    /// through correctly: the pre-rename file's nested child surfaces under its END (renamed) logical name with
    /// its ORIGINAL value. Extends <see cref="ColumnMappingIdentityTests.Cdf_NestedChildLogicalRename_IdAndPhysicalPreserved_IsAccepted"/>
    /// to the full CDF read door, in both name and id mode.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedChildLogicalRename_BetweenRetainedVersions_CdfReadsThrough(bool idMode)
    {
        ColumnMappingMode mode = idMode ? ColumnMappingMode.Id : ColumnMappingMode.Name;
        using NestedCdfTable table = NewStructTable(mode);
        await table.CreateAsync();                                                // v0 (child b)
        await table.AppendAsync(StructBatch(table, (1, 1001, 2001)));             // v1 (physical file, child "b")
        // v2: metaData-only LOGICAL rename pt.b -> pt.renamed (id + physicalName preserved) — a read-through.
        StructType renamed = RenameStructChild(table.MappedSchema, "pt", "b", "renamed");
        await table.CommitMetadataAsync(renamed, table.MaxColumnId);
        await table.AppendAsync(StructBatch(table, (2, 1004, 2004))); // v3 (carries "renamed")

        (StructType schema, List<ActualChange> changes) = await table.ReadRangeAsync(
            DeltaChangeFeedRange.FromVersion(1, 3), b => DecodeStructWithChild(b, "renamed"));

        // The whole range surfaces the nested child under the END logical name "renamed" (never "b").
        var ptStruct = (StructType)FindField(schema, "pt").DataType;
        Assert.Equal(new[] { "a", "renamed" }, ptStruct.Select(c => c.Name).ToArray());

        // The pre-rename insert (v1) reads its ORIGINAL b-value 2001 through under "renamed"; v3 joins it.
        AssertMultisetEqual(
            new List<ExpectedChange>
            {
                Insert(1, StructSig(1, 1001, 2001)),
                Insert(3, StructSig(2, 1004, 2004)),
            },
            changes);
    }

    /// <summary>
    /// A nested struct-child metadata-only DROP between retained versions reads through correctly across CDF
    /// versions: after <c>pt.b</c> is dropped (a metaData-only commit — the dropped id is retired, never reused;
    /// the pre-drop file keeps <c>b</c>'s bytes on disk), the whole range surfaces the nested column under the
    /// END schema <c>pt:struct&lt;a&gt;</c> — the pre-drop file's <c>b</c> is PROJECTED OUT (never surfaced as a
    /// stray column or a mis-bound value) while the surviving child <c>a</c> reads through with its ORIGINAL
    /// value, in both name and id mode. Complements the AC2 nested-child RENAME read-through
    /// (<see cref="NestedChildLogicalRename_BetweenRetainedVersions_CdfReadsThrough"/>): rename preserves a
    /// leaf's identity across versions, drop RETIRES it — both must reconcile every retained file to the END
    /// nested shape. The metaData-only drop commit contributes ZERO change rows.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedChildDrop_BetweenRetainedVersions_CdfReadsThrough(bool idMode)
    {
        ColumnMappingMode mode = idMode ? ColumnMappingMode.Id : ColumnMappingMode.Name;
        using NestedCdfTable table = NewStructTable(mode);
        await table.CreateAsync();                                                // v0 (children a,b)
        await table.AppendAsync(StructBatch(table, (1, 1001, 2001)));             // v1 (physical file, child "b")
        // v2: metaData-only DROP of pt.b (b's id is retired; maxColumnId unchanged) — a read-through.
        StructType dropped = DropStructChild(table.MappedSchema, "pt", "b");
        await table.CommitMetadataAsync(dropped, table.MaxColumnId);
        await table.AppendAsync(StructBatch(table, (2, 1004, 2004)));             // v3 (b still on disk, projected out)

        (StructType schema, List<ActualChange> changes) =
            await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 3), DecodeStructChildAOnly);

        // The whole range surfaces the nested column under the END shape struct<a> — b is dropped from output.
        var ptStruct = (StructType)FindField(schema, "pt").DataType;
        Assert.Equal(new[] { "a" }, ptStruct.Select(c => c.Name).ToArray());

        // The pre-drop insert (v1) reads its surviving a-value 1001 through (b projected out); v3 joins it.
        AssertMultisetEqual(
            new List<ExpectedChange>
            {
                Insert(1, StructASig(1, 1001)),
                Insert(3, StructASig(2, 1004)),
            },
            changes);
    }

    /// <summary>
    /// A FORGED nested struct-child identity CHANGE between retained versions (same logical path
    /// <c>pt.b</c>, but its physicalName reassigned) is an illegal/forged <c>_delta_log</c>: the CDF read
    /// interprets every retained file through the END identity, so a mid-range nested-child identity transition
    /// fails closed with a typed <see cref="DeltaReadException"/> — never a silent mis-mapped change row.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedChildForgedIdentityChange_BetweenRetainedVersions_CdfFailsClosed(bool idMode)
    {
        ColumnMappingMode mode = idMode ? ColumnMappingMode.Id : ColumnMappingMode.Name;
        using NestedCdfTable table = NewStructTable(mode);
        await table.CreateAsync();                                                // v0
        await table.AppendAsync(StructBatch(table, (1, 1001, 2001)));             // v1
        // v2: forged metaData — pt.b keeps its logical name/id but its physicalName is reassigned (col-forged).
        StructType forged = ReassignStructChildPhysicalName(table.MappedSchema, "pt", "b", "col-forged-b");
        Assert.NotEqual(DeltaSchemaJson.ToJson(table.MappedSchema), DeltaSchemaJson.ToJson(forged)); // tamper is live
        await table.CommitMetadataAsync(forged, table.MaxColumnId);
        await table.AppendAsync(StructBatch(table, (2, 1004, 2004))); // v3

        await AssertReadFailsClosedAsync(
            table, DeltaChangeFeedRange.FromVersion(1, 3), "column-mapping identity");
    }

    /// <summary>
    /// Isolates the IN-RANGE per-version nested-leaf identity check in <c>ChangeFeedReader</c> (the half that
    /// validates every version IN <c>[start, end]</c>, as distinct from the pre-range history scan that
    /// validates <c>[earliest, start-1]</c>). Every OTHER cell's range starts at v1, so the pre-range scan /
    /// end-snapshot load is always the deciding gate and this in-range check is never the load-bearing one.
    /// Here the history REVERTS at the end: a nested-child identity is forged at an INTERIOR retained version
    /// (v2), then a CLEAN metaData restatement at the END (v4) makes the end identity clean AND makes v0 (the
    /// only version the pre-range scan of range [1,4] inspects) match the end. So neither the end-snapshot load
    /// nor the pre-range scan can reject it — ONLY the in-range check, walking v2's prevailing (forged) identity
    /// against the clean end identity, fails the read closed. Verified load-bearing by neutering the in-range
    /// <c>IsImmutableFrom</c> checks: with them removed this scenario reads through (returns the model rows)
    /// rather than failing closed (documented in the accompanying non-vacuity report).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedChildForgedIdentity_AtInteriorVersion_RevertedAtEnd_FailsClosedViaInRangeCheck(bool idMode)
    {
        ColumnMappingMode mode = idMode ? ColumnMappingMode.Id : ColumnMappingMode.Name;
        using NestedCdfTable table = NewStructTable(mode);
        await table.CreateAsync();                                                // v0 clean (I0) — pre-range anchor
        await table.AppendAsync(StructBatch(table, (1, 1001, 2001)));             // v1 (clean data file)
        // v2: forged metaData — pt.b keeps its logical name/id but its physicalName is reassigned. The forged
        // identity now PREVAILS at v2 (an interior retained version), differing from the (clean) end identity.
        StructType forged = ReassignStructChildPhysicalName(table.MappedSchema, "pt", "b", "col-forged-b");
        await table.CommitMetadataAsync(forged, table.MaxColumnId);              // v2 (forged prevails)
        await table.AppendAsync(StructBatch(table, (2, 1004, 2004)));             // v3 (data file; physical bytes
                                                                                  //     follow the END identity)
                                                                                  // v4: CLEAN restatement — the END identity is I0 again, so the end-snapshot load passes and the
                                                                                  // pre-range scan of [earliest=0, start-1=0] (just v0 = I0) also passes. Only the in-range check sees v2.
        await table.CommitMetadataAsync(table.MappedSchema, table.MaxColumnId);   // v4 (I0 restated)

        // Pin the IN-RANGE ChangeFeedReader message specifically ("crosses a column-mapping identity change"):
        // the docstring proves ONLY the in-range check catches this reverted-at-end history, so the reason must
        // be the in-range one — never the pre-range/end-snapshot identity message.
        await AssertReadFailsClosedAsync(
            table, DeltaChangeFeedRange.FromVersion(1, 4), "crosses a column-mapping identity change");
    }

    /// <summary>
    /// Null-vs-empty-vs-absent nested-value fidelity through the CDF read door (name mode), for all three
    /// container kinds. Each container is authored with rows that are NULL (whole container), rows carrying a
    /// null LEAF (null struct child / null array element / null map value), and rows that are EMPTY (empty
    /// array / empty map) — and the reconciled change rows must surface each DISTINCTLY (the signature encoders
    /// render <c>&lt;null&gt;</c> apart from empty <c>[]</c>/<c>{}</c> and apart from a present zero value), so a
    /// dropped null cannot pass as an equal value. Guards the otherwise-dead <c>&lt;null&gt;</c> decode branch.
    /// </summary>
    [Fact]
    public async Task NameMode_NullContainersAndLeaves_SurviveCdfReadDistinctlyFromEmptyAndAbsent()
    {
        // --- struct: a null struct row, a null-a child, a null-b child, a fully-present row ---
        using (NestedCdfTable structTable = NewStructTable(ColumnMappingMode.Name))
        {
            await structTable.CreateAsync();
            await structTable.AppendAsync(StructBatchNullable(
                structTable,
                (1, null, null, true),        // null struct row (distinct from struct(a=<null>,b=<null>))
                (2, null, 2002L, false),      // null 'a' child
                (3, 1003L, null, false),      // null 'b' child
                (4, 1004L, 2004L, false)));   // fully present
            (_, List<ActualChange> changes) =
                await structTable.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 1), DecodeStruct);
            AssertMultisetEqual(
                new List<ExpectedChange>
                {
                    Insert(1, StructSig(1, null, null, nullStruct: true)),
                    Insert(1, StructSig(2, null, 2002L, nullStruct: false)),
                    Insert(1, StructSig(3, 1003L, null, nullStruct: false)),
                    Insert(1, StructSig(4, 1004L, 2004L, nullStruct: false)),
                },
                changes);
        }

        // --- array: null list, empty list, list with a null element, a present list ---
        using (NestedCdfTable arrayTable = NewArrayTable())
        {
            await arrayTable.CreateAsync();
            await arrayTable.AppendAsync(ArrayBatchNullable(
                arrayTable,
                (1, null),                                    // null list
                (2, Array.Empty<string?>()),                  // empty list (distinct from null)
                (3, new string?[] { "x", null, "z" }),        // list with a null element
                (4, new string?[] { "p", "q" })));            // present list
            (_, List<ActualChange> changes) =
                await arrayTable.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 1), DecodeArray);
            AssertMultisetEqual(
                new List<ExpectedChange>
                {
                    Insert(1, NullArray(1)),
                    Insert(1, ArraySig(2)),                   // empty → array[]
                    Insert(1, ArraySig(3, "x", "<null>", "z")),
                    Insert(1, ArraySig(4, "p", "q")),
                },
                changes);
        }

        // --- map: null map, empty map, map with a null value, a present map ---
        using (NestedCdfTable mapTable = NewMapTable())
        {
            await mapTable.CreateAsync();
            await mapTable.AppendAsync(MapBatchNullable(
                mapTable,
                (1, null),                                                  // null map
                (2, Array.Empty<(string, long?)>()),                        // empty map (distinct from null)
                (3, new (string, long?)[] { ("k", null), ("j", 5003L) }),   // map with a null value
                (4, new (string, long?)[] { ("w", 5004L) })));              // present map
            (_, List<ActualChange> changes) =
                await mapTable.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 1), DecodeMap);
            AssertMultisetEqual(
                new List<ExpectedChange>
                {
                    Insert(1, NullMap(1)),
                    Insert(1, MapSig(2, Array.Empty<(string, long?)>())),  // empty → map{}
                    Insert(1, MapSig(3, ("k", (long?)null), ("j", (long?)5003L))),
                    Insert(1, MapSig(4, ("w", (long?)5004L))),
                },
                changes);
        }
    }

    /// <summary>
    /// The struct null cases under ID mode (each nested child binds by <c>field_id</c>): a null struct row and a
    /// null child must still survive the CDF read distinctly, proving the null decode is not a name-mode-only
    /// artifact.
    /// </summary>
    [Fact]
    public async Task IdMode_NullStructAndNullChild_SurviveCdfReadDistinctly()
    {
        using NestedCdfTable table = NewStructTable(ColumnMappingMode.Id);
        await table.CreateAsync();
        await table.AppendAsync(StructBatchNullable(
            table,
            (1, null, null, true),       // null struct row
            (2, null, 2002L, false),     // null 'a' child
            (3, 1003L, 2003L, false)));  // present
        (_, List<ActualChange> changes) =
            await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 1), DecodeStruct);
        AssertMultisetEqual(
            new List<ExpectedChange>
            {
                Insert(1, StructSig(1, null, null, nullStruct: true)),
                Insert(1, StructSig(2, null, 2002L, nullStruct: false)),
                Insert(1, StructSig(3, 1003L, 2003L, nullStruct: false)),
            },
            changes);
    }

    /// <summary>
    /// The ARRAY and MAP null cases under ID mode (each container binds by <c>physicalName</c>, its interior
    /// element/key/value by <c>delta.columnMapping.nested.ids</c>): a NULL container, an EMPTY container, a
    /// container carrying a NULL leaf (null array element / null map value), and a PRESENT container must each
    /// survive the CDF read DISTINCTLY — the signature encoders render <c>array=&lt;null&gt;</c> apart from
    /// empty <c>array[]</c> and <c>map=&lt;null&gt;</c> apart from empty <c>map{}</c>, so a dropped null cannot
    /// pass as an equal value. This is the id-mode dual of the array/map arms of
    /// <see cref="NameMode_NullContainersAndLeaves_SurviveCdfReadDistinctlyFromEmptyAndAbsent"/> (which the
    /// #854/#839 read-through now unblocks), proving the null decode through the nested.ids-resolved interior is
    /// not a name-mode-only artifact.
    /// </summary>
    [Fact]
    public async Task IdMode_NullArrayAndMapContainers_SurviveCdfReadDistinctlyFromEmptyAndPresent()
    {
        // --- array (id mode): null list, empty list, list with a null element, a present list ---
        using (NestedCdfTable arrayTable = NewArrayTable(ColumnMappingMode.Id))
        {
            await arrayTable.CreateAsync();
            await arrayTable.AppendAsync(ArrayBatchNullable(
                arrayTable,
                (1, null),                                    // null list
                (2, Array.Empty<string?>()),                  // empty list (distinct from null)
                (3, new string?[] { "x", null, "z" }),        // list with a null element
                (4, new string?[] { "p", "q" })));            // present list
            (_, List<ActualChange> changes) =
                await arrayTable.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 1), DecodeArray);
            AssertMultisetEqual(
                new List<ExpectedChange>
                {
                    Insert(1, NullArray(1)),                  // null → array=<null>
                    Insert(1, ArraySig(2)),                   // empty → array[]
                    Insert(1, ArraySig(3, "x", "<null>", "z")),
                    Insert(1, ArraySig(4, "p", "q")),
                },
                changes);
        }

        // --- map (id mode): null map, empty map, map with a null value, a present map ---
        using (NestedCdfTable mapTable = NewMapTable(ColumnMappingMode.Id))
        {
            await mapTable.CreateAsync();
            await mapTable.AppendAsync(MapBatchNullable(
                mapTable,
                (1, null),                                                  // null map
                (2, Array.Empty<(string, long?)>()),                        // empty map (distinct from null)
                (3, new (string, long?)[] { ("k", null), ("j", 5003L) }),   // map with a null value
                (4, new (string, long?)[] { ("w", 5004L) })));              // present map
            (_, List<ActualChange> changes) =
                await mapTable.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 1), DecodeMap);
            AssertMultisetEqual(
                new List<ExpectedChange>
                {
                    Insert(1, NullMap(1)),                                 // null → map=<null>
                    Insert(1, MapSig(2, Array.Empty<(string, long?)>())),  // empty → map{}
                    Insert(1, MapSig(3, ("k", (long?)null), ("j", (long?)5003L))),
                    Insert(1, MapSig(4, ("w", (long?)5004L))),
                },
                changes);
        }
    }

    // ============================================================================================
    // AC3 · Nested-leaf tamper fuzz (fail-closed, seeded 200-iter, typed exception on every tamper)
    // ============================================================================================

    /// <summary>
    /// Seeded fuzz: generate a nested-mapped (struct&lt;a:long,b:long&gt;) CDF history, then apply ONE
    /// enumerated tamper to a nested leaf's mapping metadata in a RETAINED version's log (swap sibling
    /// physicalNames, swap sibling field ids, reassign a child physicalName, drop a child id, inject an id-mode
    /// array/map #839 shape, inject a nested-within-nested #585 shape, or inject a foreign
    /// <c>delta.columnMapping.nested.ids</c>). The CDF read of the full range must FAIL CLOSED with a typed
    /// exception — never a silent mis-mapped change row. Same-typed siblings (a,b) draw from DISJOINT domains
    /// so a positional mis-bind could not pass on equal values even if the gate were absent.
    /// </summary>
    [Fact]
    public async Task NestedLeafTamperFuzz_CdfReadFailsClosed_WithTypedException()
    {
        int baseSeed = TestSeed.Resolve();
        _output.WriteLine(
            $"[deltasharp-seed] {Scope}.NestedLeafTamperFuzz baseSeed={baseSeed} ({TestSeed.EnvironmentVariable})");

        NestedTamper[] tampers = Enum.GetValues<NestedTamper>();
        ColumnMappingMode[] modes = [ColumnMappingMode.Name, ColumnMappingMode.Id];

        // Non-vacuity baseline: the SAME history with a CLEAN mid-history metaData restatement reads exactly the
        // model multiset (proves the tamper — not the harness — is what trips the fail-closed door).
        await AssertCleanBaselineReadsCleanAsync(ColumnMappingMode.Name);
        await AssertCleanBaselineReadsCleanAsync(ColumnMappingMode.Id);

        const int iterations = 200;
        int caught = 0;
        var evidence = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < iterations; i++)
        {
            var rng = new Random(baseSeed + i);
            NestedTamper tamper = tampers[rng.Next(tampers.Length)];
            ColumnMappingMode mode = SelectMode(modes, tamper, rng);

            try
            {
                await RunTamperIterationAsync(mode, tamper, rng);
                // If we got here, the read did NOT fail closed — a real finding: surface it precisely.
                Assert.Fail(
                    $"Nested-leaf tamper was NOT caught: iter={i} seed={baseSeed + i} mode={mode} tamper={tamper}. "
                    + "The CDF read returned change rows through a tampered nested-leaf mapping identity — a "
                    + "silent mis-map. Reproduce by setting "
                    + $"{TestSeed.EnvironmentVariable}={baseSeed} and this iteration index.");
            }
            catch (Exception ex) when (ex is DeltaReadException or DeltaProtocolException or DeltaStorageException)
            {
                caught++;
                string key = string.Create(CultureInfo.InvariantCulture, $"{tamper}/{mode}");
                if (!evidence.ContainsKey(key))
                {
                    evidence[key] = ex.GetType().Name + ": " + Snippet(ex.Message);
                }
            }
        }

        Assert.Equal(iterations, caught);
        // Every enumerated tamper kind must have been exercised AND failed closed (coverage, not just a count).
        Assert.Equal(tampers.Length, evidence.Keys.Select(k => k.Split('/')[0]).Distinct().Count());
        foreach (KeyValuePair<string, string> e in evidence)
        {
            _output.WriteLine($"[nested-cdf-tamper] {e.Key} -> {e.Value}");
        }

        _output.WriteLine($"[nested-cdf-tamper] {caught}/{iterations} tampers failed closed with a typed exception.");
    }

    // Clamp to at most 100 chars WITHOUT splitting a UTF-16 surrogate pair (a naive message[..100] can cut a
    // rune in half and yield a lone surrogate). Back off one char when the boundary lands between a high and
    // low surrogate so the snippet is always well-formed.
    private static string Snippet(string message)
    {
        if (message.Length <= 100)
        {
            return message;
        }

        int end = 100;
        if (char.IsHighSurrogate(message[end - 1]) && char.IsLowSurrogate(message[end]))
        {
            end--;
        }

        return message[..end];
    }

    // ============================================================================================
    // AC4 · Boundary — a PLAIN id-mode array/map (no nested.ids) and nested-within-nested (#585) fail closed
    //       at load/read; an id-mode array/map WITH nested.ids now READS THROUGH (see the IdMode_Nested{Array,
    //       Map}_CdfHistory_*_AndNestedIdsReconcile cells above — #854 lifted the #839 fail-closed gate).
    // ============================================================================================

    /// <summary>
    /// A PLAIN id-mode CDF table declaring an <c>array</c> or <c>map</c> column with NO
    /// <c>delta.columnMapping.nested.ids</c> is fail-closed: the load/read door rejects it with a typed
    /// exception rather than silently skipping the container whose interior element/key/value has no
    /// representable id. This is the genuinely-still-fail-closed residue of #839 AFTER #854: #854 lifted the
    /// blanket id-mode-array/map gate ONLY for a container carrying a valid <c>nested.ids</c> (read-through is
    /// exercised by <see cref="IdMode_NestedArray_CdfHistory_ElementValueFidelity_AndNestedIdsReconcile"/> and
    /// <see cref="IdMode_NestedMap_CdfHistory_EntryValueFidelity_AndNestedIdsReconcile"/>); a <c>nested.ids</c>-
    /// less container stays rejected (design §2.6). The name-mode dual of the SAME shape is fully exercised
    /// above and needs no interior ids at all.
    /// </summary>
    [Theory]
    [InlineData("array")]
    [InlineData("map")]
    public async Task IdMode_NestedArrayOrMap_NoNestedIds_CdfTable_FailsClosedAtLoadDoor_839(string kind)
    {
        StructType logical = kind == "array"
            ? new StructType(new[]
            {
                new StructField("id", DataTypes.LongType, nullable: false),
                new StructField("c", new ArrayType(DataTypes.StringType), nullable: true),
            })
            : new StructType(new[]
            {
                new StructField("id", DataTypes.LongType, nullable: false),
                new StructField("c", new MapType(DataTypes.StringType, DataTypes.LongType), nullable: true),
            });

        // MappedArrayOrMap mints id + physicalName on the container but NO nested.ids — the plain shape #854
        // leaves fail-closed. (The id-mode read-through cells mint via AssignFreshMapping(mode: Id), which DOES
        // attach nested.ids, so they pass the same load door this cell proves rejects the nested.ids-less form.)
        using NestedCdfTable table = NestedCdfTable.FromMapped(NewRoot(), ColumnMappingMode.Id, logical, MappedArrayOrMap(kind), maxColumnId: 2);
        // The raw metaData mints fine on disk (a plain id-mode array/map is a legal shape to WRITE into a log);
        // the #839 gate is ValidateColumnMappingSchema at the load choke point — the READ door is where it
        // fails. Pin the precise no-nested.ids branch message so a wrong-but-thrown typed error cannot pass.
        await table.CreateRawMetadataAsync();
        await AssertLoadFailsClosedAsync(
            table, DeltaChangeFeedRange.FromVersion(0), "carries no", ColumnMapping.NestedIdsKey, "rejected fail-closed");
    }

    /// <summary>
    /// An id-mode nested-within-nested CDF table (<c>array&lt;struct&gt;</c>) is now LOADABLE (866b) when fully
    /// mapped; a MALFORMED one whose <c>items</c> array carries no <c>nested.ids</c> still fails closed at the
    /// load/read door (unstamped interior → unreadable). NAME/none mode load-succeeds companion is
    /// <see cref="NestedWithinNested_NameModeCdfTable_LoadsInteriorStruct_NotSilentlySkipped"/>.
    /// </summary>
    [Fact]
    public async Task NestedWithinNested_IdModeCdfTable_NoNestedIds_FailsClosedAtLoadDoor_866()
    {
        const ColumnMappingMode mode = ColumnMappingMode.Id;
        var logical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("items", new ArrayType(new StructType(new[]
            {
                new StructField("x", DataTypes.LongType, nullable: true),
            })), nullable: true),
        });

        using NestedCdfTable table = NestedCdfTable.FromMapped(NewRoot(), mode, logical, MappedNestedWithinNested(), maxColumnId: 2);
        await table.CreateRawMetadataAsync();
        await AssertLoadFailsClosedAsync(table, DeltaChangeFeedRange.FromVersion(0), "nested.ids");
    }

    /// <summary>
    /// The NAME-mode companion (#866 866a): a nested-within-nested CDF table (<c>array&lt;struct&gt;</c> and
    /// <c>map&lt;*,struct&gt;</c>, with the interior struct child fully mapped) LOADS through the change-feed
    /// door — it is NOT rejected and NOT silently skipped (the interior struct is a first-class part of the
    /// column-mapping identity, its cross-version drift caught by the stability gate — see the
    /// <c>ColumnMappingIdentityTests.IsImmutableFrom_NameMode_*Interior*</c> tamper cells).
    /// </summary>
    [Theory]
    [InlineData("array")]
    [InlineData("map")]
    public async Task NestedWithinNested_NameModeCdfTable_LoadsInteriorStruct_NotSilentlySkipped(string kind)
    {
        StructType mapped = kind == "array" ? MappedNameArrayOfStruct() : MappedNameMapOfStruct();
        using NestedCdfTable table = NestedCdfTable.FromMapped(
            NewRoot(), ColumnMappingMode.Name, StripMapping(mapped), mapped, maxColumnId: 3);
        await table.CreateRawMetadataAsync();
        await AssertLoadSucceedsAsync(table, DeltaChangeFeedRange.FromVersion(0));
    }

    // ------------------------------------------------------------------------------------------------
    // AC4 · Boundary — per-guard NON-VACUITY isolators (metadata-only, no data files)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The clean per-guard NON-VACUITY isolators. Each case commits a metaData-ONLY table (no data files) whose
    /// mapped schema carries EXACTLY ONE structural defect on a NEW column while every other column's identity
    /// is well-formed. With no data files there is NO downstream read backstop (the duplicate-Parquet-field-name
    /// check, the id-mode UnsupportedFeature reader, the missing-column schema-evolution check), so the load door
    /// (<c>ValidateColumnMappingSchema</c>) is the SOLE possible catcher: neutering the specific branch under
    /// test makes the corresponding case here read through (fail). This is the gold-standard isolation the
    /// data-bearing fuzz cannot always give — some fuzz tampers are (correctly) backstopped downstream and so do
    /// not flip the fuzz when their load-door branch alone is neutered. The verified guard→case neuter map lives
    /// in the accompanying report's non-vacuity table. Substrings pin each case to ITS branch's message so a
    /// wrong-but-thrown typed error cannot masquerade as the guard under test.
    /// </summary>
    [Theory]
    [InlineData(NestedTamper.DropChildId, "no 'delta.columnMapping.id'", "nx.c")]
    [InlineData(NestedTamper.DuplicateSiblingPhysicalName, "assigned to more than one column", "col-dup")]
    [InlineData(NestedTamper.EmbeddedDotNestedPhysicalName, "not a safe path component", "col.dot")]
    [InlineData(NestedTamper.ControlCharNestedPhysicalName, "not a safe path component", "nx.c")]
    [InlineData(NestedTamper.OutOfRangeNestedId, "outside the valid column-mapping", "nx.c")]
    [InlineData(NestedTamper.InjectNestedIds, "delta.columnMapping.nested.ids", "array/map interior")]
    public async Task StructuralTamper_MetadataOnly_IsolatesItsColumnMappingGuard(
        NestedTamper tamper, string sub1, string sub2)
    {
        (StructType mapped, long maxColumnId, ColumnMappingMode mode) = BoundaryMappedForTamper(tamper);
        using NestedCdfTable table =
            NestedCdfTable.FromMapped(NewRoot(), mode, StripMapping(mapped), mapped, maxColumnId);
        await table.CreateRawMetadataAsync();
        await AssertLoadFailsClosedAsync(table, DeltaChangeFeedRange.FromVersion(0), sub1, sub2);
    }

    // Builds a metaData-ONLY mapped schema carrying exactly ONE structural defect on a NEW 'nx' column: a
    // well-formed top-level 'id' (id 1, col-id) plus the offending column. maxColumnId covers the largest
    // WELL-FORMED id used (DropChildId's child has no id; OutOfRangeNestedId's child id is the -1 defect — both
    // keep the base ceiling of 2). Mode is name for every defect (all are mode-agnostic here).
    private static (StructType Mapped, long MaxColumnId, ColumnMappingMode Mode) BoundaryMappedForTamper(
        NestedTamper tamper)
    {
        var id = new StructField("id", DataTypes.LongType, false, MappingMeta(1, "col-id"));
        return tamper switch
        {
            NestedTamper.DropChildId =>
                (new StructType(new[] { id, MissingIdStructColumn(1) }), 2L, ColumnMappingMode.Name),
            NestedTamper.DuplicateSiblingPhysicalName =>
                (new StructType(new[] { id, DuplicatePhysicalStructColumn(1) }), 4L, ColumnMappingMode.Name),
            NestedTamper.EmbeddedDotNestedPhysicalName =>
                (new StructType(new[] { id, NestedPhysicalStructColumn(1, "col.dot") }), 3L, ColumnMappingMode.Name),
            NestedTamper.ControlCharNestedPhysicalName =>
                (new StructType(new[] { id, NestedPhysicalStructColumn(1, "col\u0001x") }), 3L, ColumnMappingMode.Name),
            NestedTamper.OutOfRangeNestedId =>
                (new StructType(new[] { id, OutOfRangeIdStructColumn(1) }), 2L, ColumnMappingMode.Name),
            NestedTamper.InjectNestedIds =>
                (InjectNestedIdsKey(
                    new StructType(new[]
                    {
                        id,
                        new StructField("nx", new ArrayType(DataTypes.StringType), true, MappingMeta(2, "col-nx")),
                    }),
                    "nx"), 2L, ColumnMappingMode.Name),
            _ => throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null),
        };
    }

    // Strips all column-mapping metadata (recursing into struct children) to recover a plain LOGICAL schema. The
    // metaData-only boundary tables never write data, so the logical schema is only a shape placeholder — the
    // load door validates the MAPPED schema committed in the metaData.
    private static StructType StripMapping(StructType mapped) =>
        new(mapped.Select(f => new StructField(
            f.Name,
            f.DataType is StructType s ? StripMapping(s) : f.DataType,
            f.Nullable)).ToList());

    // ------------------------------------------------------------------------------------------------
    // Tamper-fuzz plumbing
    // ------------------------------------------------------------------------------------------------

    // Each tamper isolates ONE production guard. The critical design rule (red-team round 1): a tamper must
    // change ONLY the specific defect under test and PRESERVE every pre-existing column's identity (id +
    // physicalName) EXACTLY as the history minted it — otherwise ColumnMappingIdentity.IsImmutableFrom trips on
    // the perturbed identity of a COMMON column and masks whether the specific structural guard fires at all
    // (the cell goes VACUOUS: the structural guard could be dead and the fuzz still pass). So:
    //   • IDENTITY tampers MUTATE an existing (common) nested child's identity — they are SUPPOSED to be caught
    //     by IsImmutableFrom (via its nested descent); that IS the guard they isolate.
    //   • STRUCTURAL tampers ADD a NEW top-level offending column (leaving every existing identity byte-exact).
    //     A new column is present only at the end version, so IsImmutableFrom (which compares only COMMON
    //     columns) cannot mask it — the specific ValidateColumnMappingSchema branch under test is REACHED (on a
    //     clean build the fuzz evidence shows each tamper caught by ITS branch's precise message, never an
    //     IsImmutableFrom message). NOTE the data-bearing fuzz is defense-in-depth-layered: once the load-door
    //     branch passes (were it neutered), a downstream backstop may still fail the read closed (a duplicate
    //     Parquet field name, the id-mode UnsupportedFeature reader, or a missing-column schema-evolution
    //     error). So the clean SOLE-catcher per-guard isolation is proven by the metaData-only boundary Theory
    //     StructuralTamper_MetadataOnly_IsolatesItsColumnMappingGuard above (no data ⇒ no backstop); the fuzz
    //     adds varied-data breadth on top. The verified guard→case neuter map is in the accompanying report.
    public enum NestedTamper
    {
        // ---- IDENTITY tampers (isolate ColumnMappingIdentity.IsImmutableFrom via nested descent) ----
        SwapSiblingPhysicalNames,   // swap pt.a/pt.b physicalName    → common-column identity change
        SwapSiblingFieldIds,        // swap pt.a/pt.b field id         → common-column identity change
        ReassignChildPhysicalName,  // reassign pt.a physicalName      → common-column identity change

        // ---- STRUCTURAL tampers (isolate a specific ValidateColumnMappingSchema branch; add-new-column) ----
        DropChildId,                    // new struct child with NO id             → missing-id branch
        DuplicateSiblingPhysicalName,   // two new siblings share a physicalName   → per-level physicalName-uniqueness
        EmbeddedDotNestedPhysicalName,  // new nested child physicalName has a '.' → EnsureNestedPhysicalNameSafe (dot)
        ControlCharNestedPhysicalName,  // new nested child physicalName has \u0001 → EnsureNestedPhysicalNameSafe (ctrl)
        OutOfRangeNestedId,             // new nested child id <= 0                 → nested id-range branch
        InjectNestedIds,                // delta.columnMapping.nested.ids on pt     → nested.ids reject
        InjectIdModeArray,              // new id-mode array column                 → id-mode array/map (#839) gate
        InjectNestedWithinNested,       // new array<struct> column                → RejectNestedWithinNested (#585)
    }

    // #839 needs id mode to fire; route InjectIdModeArray there. Every other tamper is mode-agnostic (its guard
    // fires in both name and id mode), so it draws a mode at random for cross-mode coverage.
    private static ColumnMappingMode SelectMode(ColumnMappingMode[] modes, NestedTamper tamper, Random rng) =>
        tamper == NestedTamper.InjectIdModeArray ? ColumnMappingMode.Id : modes[rng.Next(modes.Length)];

    // Builds a struct history whose mid-history metaData at the last version is `MutateForTamper`d, then reads
    // the whole range through the production door. The caller asserts the read throws a TYPED exception. The
    // DATA is VARIED per iteration off `rng` (RT-2): a random append count, random disjoint-domain values, and
    // random null/empty/multi struct rows — so the fuzz is not pinned to a single 1001/2001 fixture. The read is
    // expected to fail closed regardless of the data content (the defect is in the metaData identity/shape), so
    // varying the data only broadens the physical files the fail-closed door is exercised against.
    private async Task RunTamperIterationAsync(ColumnMappingMode mode, NestedTamper tamper, Random rng)
    {
        using NestedCdfTable table = NewStructTable(mode);
        await table.CreateAsync();                                            // v0 clean

        int appendVersions = 1 + rng.Next(3);                                // 1..3 clean data-file versions
        for (int v = 0; v < appendVersions; v++)
        {
            await table.AppendAsync(StructBatchNullable(table, RandomStructRows(rng)));
        }

        StructType tampered = MutateForTamper(table.MappedSchema, tamper, table.MaxColumnId);
        long maxColumnId = TamperMaxColumnId(table.MaxColumnId, tamper);
        await table.CommitMetadataAsync(tampered, maxColumnId);              // forged metaData (last version)

        (_, _) = await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, table.LatestVersion), DecodeStruct);
    }

    // A per-iteration random struct batch: 1..4 rows drawn from DISJOINT domains (a from [1000..], b from
    // [2000..], id from [1..]) with a random mix of full / null-child / null-struct rows. Disjoint domains keep
    // a positional mis-bind detectable; the null mix varies the physical file's validity bitmap.
    private static (long Id, long? A, long? B, bool NullStruct)[] RandomStructRows(Random rng)
    {
        int rows = 1 + rng.Next(4);
        var result = new (long Id, long? A, long? B, bool NullStruct)[rows];
        for (int i = 0; i < rows; i++)
        {
            long id = 1 + rng.Next(900);
            int shape = rng.Next(4);
            result[i] = shape switch
            {
                0 => (id, null, null, true),                                  // null struct row
                1 => (id, null, 2000L + rng.Next(900), false),               // null a child
                2 => (id, 1000L + rng.Next(900), null, false),               // null b child
                _ => (id, 1000L + rng.Next(900), 2000L + rng.Next(900), false),
            };
        }

        return result;
    }

    // The clean baseline: identical shape to the fuzz history but the mid-history metaData RESTATES the same
    // identity, so the read succeeds and returns exactly the model multiset (non-vacuity guard for the fuzz).
    private async Task AssertCleanBaselineReadsCleanAsync(ColumnMappingMode mode)
    {
        using NestedCdfTable table = NewStructTable(mode);
        await table.CreateAsync();
        await table.AppendAsync(StructBatch(table, (1, 1001, 2001)));
        await table.AppendAsync(StructBatch(table, (2, 1002, 2002)));
        await table.CommitMetadataAsync(table.MappedSchema, table.MaxColumnId);  // clean restatement

        (_, List<ActualChange> changes) =
            await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, table.LatestVersion), DecodeStruct);

        AssertMultisetEqual(
            new List<ExpectedChange> { Insert(1, StructSig(1, 1001, 2001)), Insert(2, StructSig(2, 1002, 2002)) },
            changes);
    }

    // The metaData maxColumnId a tamper commits. A structural tamper that ADDS one or more WELL-FORMED-id
    // columns bumps the ceiling to cover them; DropChildId (new child has no id) and OutOfRangeNestedId (the
    // defect IS an out-of-[1,ceiling] id) keep the base ceiling; the identity tampers and InjectNestedIds do not
    // touch ids so the base ceiling stands.
    private static long TamperMaxColumnId(long baseMax, NestedTamper tamper) => tamper switch
    {
        NestedTamper.DuplicateSiblingPhysicalName => baseMax + 3,   // nx + two new children
        NestedTamper.EmbeddedDotNestedPhysicalName
            or NestedTamper.ControlCharNestedPhysicalName => baseMax + 2,   // nx + one new child
        NestedTamper.DropChildId => baseMax + 1,                    // nx (its child carries no id)
        NestedTamper.InjectIdModeArray
            or NestedTamper.InjectNestedWithinNested => baseMax + 1,   // nx container only
        _ => baseMax,
    };

    // Author the forged/tampered mapped schema a poisoned metaData would carry. `mapped` is the history's ACTUAL
    // mapped schema (so existing identities are reused byte-exact); `baseMax` seeds fresh ids for added columns.
    private StructType MutateForTamper(StructType mapped, NestedTamper tamper, long baseMax) => tamper switch
    {
        // IDENTITY tampers — mutate an existing common nested child (isolate IsImmutableFrom).
        NestedTamper.SwapSiblingPhysicalNames => SwapStructChildPhysicalNames(mapped, "pt", "a", "b"),
        NestedTamper.SwapSiblingFieldIds => SwapStructChildFieldIds(mapped, "pt", "a", "b"),
        NestedTamper.ReassignChildPhysicalName => ReassignStructChildPhysicalName(mapped, "pt", "a", "col-forged-a"),

        // STRUCTURAL tampers — ADD a new offending top-level column; every pre-existing identity is preserved.
        NestedTamper.DropChildId => AppendTopLevel(mapped, MissingIdStructColumn(baseMax)),
        NestedTamper.DuplicateSiblingPhysicalName => AppendTopLevel(mapped, DuplicatePhysicalStructColumn(baseMax)),
        NestedTamper.EmbeddedDotNestedPhysicalName => AppendTopLevel(mapped, NestedPhysicalStructColumn(baseMax, "col.dot")),
        NestedTamper.ControlCharNestedPhysicalName => AppendTopLevel(mapped, NestedPhysicalStructColumn(baseMax, "col\u0001x")),
        NestedTamper.OutOfRangeNestedId => AppendTopLevel(mapped, OutOfRangeIdStructColumn(baseMax)),
        NestedTamper.InjectNestedIds => InjectNestedIdsKey(mapped, "pt"),
        NestedTamper.InjectIdModeArray => AppendTopLevel(mapped, IdModeArrayColumn(baseMax)),
        NestedTamper.InjectNestedWithinNested => AppendTopLevel(mapped, NestedWithinNestedColumn(baseMax)),
        _ => throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null),
    };

    // ---- new-offending-column builders (each returns ONE new top-level mapped field carrying its defect) ----

    // A new struct column whose nested child carries a physicalName but NO id → the missing-id branch fires on
    // the child (nx itself is well-formed: id baseMax+1). Neutering the missing-id branch lets the whole nx
    // struct null-fill in the pre-tamper files → read succeeds → cell is load-bearing.
    private static StructField MissingIdStructColumn(long baseMax) =>
        new(
            "nx",
            new StructType(new[] { new StructField("c", DataTypes.LongType, true, PhysicalOnlyMeta("col-nx-c")) }),
            true,
            MappingMeta(baseMax + 1, "col-nx"));

    // A new struct column whose TWO nested children share one physicalName → the per-level physicalName-
    // uniqueness guard fires. Both children carry well-formed unique ids so ONLY the duplicate physicalName is
    // the defect.
    private static StructField DuplicatePhysicalStructColumn(long baseMax) =>
        new(
            "nx",
            new StructType(new[]
            {
                new StructField("c1", DataTypes.LongType, true, MappingMeta(baseMax + 2, "col-dup")),
                new StructField("c2", DataTypes.LongType, true, MappingMeta(baseMax + 3, "col-dup")),
            }),
            true,
            MappingMeta(baseMax + 1, "col-nx"));

    // A new struct column whose nested child physicalName is unsafe (embedded '.' or a control char) →
    // EnsureNestedPhysicalNameSafe fires. The child's id is well-formed so ONLY the physical name is the defect.
    private static StructField NestedPhysicalStructColumn(long baseMax, string childPhysical) =>
        new(
            "nx",
            new StructType(new[] { new StructField("c", DataTypes.LongType, true, MappingMeta(baseMax + 2, childPhysical)) }),
            true,
            MappingMeta(baseMax + 1, "col-nx"));

    // A new struct column whose nested child has id <= 0 → the nested id-range branch fires. The physicalName is
    // safe/unique so ONLY the out-of-range id is the defect.
    private static StructField OutOfRangeIdStructColumn(long baseMax) =>
        new(
            "nx",
            new StructType(new[] { new StructField("c", DataTypes.LongType, true, MappingMeta(-1, "col-nx-c")) }),
            true,
            MappingMeta(baseMax + 1, "col-nx"));

    // A new id-mode array column → the id-mode array/map (#839) gate fires (only under id mode).
    private static StructField IdModeArrayColumn(long baseMax) =>
        new("nx", new ArrayType(DataTypes.StringType), true, MappingMeta(baseMax + 1, "col-nx"));

    // A new nested-within-nested (array<struct>) column → RejectNestedWithinNested (#585) fires.
    private static StructField NestedWithinNestedColumn(long baseMax) =>
        new(
            "nx",
            new ArrayType(new StructType(new[] { new StructField("x", DataTypes.LongType, nullable: true) })),
            true,
            MappingMeta(baseMax + 1, "col-nx"));

    // Appends one new top-level field to a mapped schema (identity-preserving: every existing field is copied
    // verbatim, so a CDF read's IsImmutableFrom sees no change to any COMMON column).
    private static StructType AppendTopLevel(StructType mapped, StructField newField) =>
        new(mapped.Append(newField).ToList());

    // ------------------------------------------------------------------------------------------------
    // Schema-mutation helpers (author the forged/tampered mapped schema a poisoned metaData would carry)
    // ------------------------------------------------------------------------------------------------

    private static StructType RenameStructChild(StructType mapped, string container, string from, string to) =>
        MapContainerChildren(mapped, container, children =>
            children.Select(c => c.Name == from
                ? new StructField(to, c.DataType, c.Nullable, c.Metadata)
                : c).ToArray());

    // Drops a nested struct child from a mapped schema (a metadata-only DROP): the child is simply not emitted;
    // its id is retired (never reused — maxColumnId only ever increases). Every OTHER child is copied verbatim
    // (identity-preserving), so a CDF read's IsImmutableFrom sees no change to any COMMON leaf.
    private static StructType DropStructChild(StructType mapped, string container, string child) =>
        MapContainerChildren(mapped, container, children =>
            children.Where(c => c.Name != child).ToArray());

    private static StructType ReassignStructChildPhysicalName(
        StructType mapped, string container, string child, string newPhysical) =>
        MapContainerChildren(mapped, container, children =>
            children.Select(c => c.Name == child
                ? c.WithMetadata(SetMeta(c.Metadata, ColumnMapping.PhysicalNameKey, MetadataValue.String(newPhysical)))
                : c).ToArray());

    private static StructType SwapStructChildPhysicalNames(
        StructType mapped, string container, string left, string right) =>
        MapContainerChildren(mapped, container, children =>
        {
            string leftPhysical = Physical(children.First(c => c.Name == left));
            string rightPhysical = Physical(children.First(c => c.Name == right));
            return children.Select(c => c.Name switch
            {
                _ when c.Name == left => c.WithMetadata(
                    SetMeta(c.Metadata, ColumnMapping.PhysicalNameKey, MetadataValue.String(rightPhysical))),
                _ when c.Name == right => c.WithMetadata(
                    SetMeta(c.Metadata, ColumnMapping.PhysicalNameKey, MetadataValue.String(leftPhysical))),
                _ => c,
            }).ToArray();
        });

    private static StructType SwapStructChildFieldIds(
        StructType mapped, string container, string left, string right) =>
        MapContainerChildren(mapped, container, children =>
        {
            long leftId = Id(children.First(c => c.Name == left));
            long rightId = Id(children.First(c => c.Name == right));
            return children.Select(c => c.Name switch
            {
                _ when c.Name == left => c.WithMetadata(
                    SetMeta(c.Metadata, ColumnMapping.IdKey, MetadataValue.Long(rightId))),
                _ when c.Name == right => c.WithMetadata(
                    SetMeta(c.Metadata, ColumnMapping.IdKey, MetadataValue.Long(leftId))),
                _ => c,
            }).ToArray();
        });

    private static StructType InjectNestedIdsKey(StructType mapped, string container) =>
        new(mapped.Select(f => f.Name == container
            ? f.WithMetadata(SetMeta(f.Metadata, ColumnMapping.NestedIdsKey, MetadataValue.String("1")))
            : f).ToList());

    private static StructType MapContainerChildren(
        StructType mapped, string container, Func<IReadOnlyList<StructField>, IReadOnlyList<StructField>> transform)
    {
        var fields = mapped.Select(f =>
        {
            if (f.Name != container)
            {
                return f;
            }

            var inner = (StructType)f.DataType;
            IReadOnlyList<StructField> newChildren = transform(inner.ToList());
            return new StructField(f.Name, new StructType(newChildren.ToList()), f.Nullable, f.Metadata);
        }).ToList();
        return new StructType(fields);
    }

    private static FieldMetadata SetMeta(FieldMetadata metadata, string key, MetadataValue value)
    {
        var entries = metadata.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        entries[key] = value;
        return FieldMetadata.FromValues(entries);
    }

    // Hand-authored MAPPED (id + physicalName) fields — the exact shape a poisoned/foreign metaData carries.
    private static FieldMetadata MappingMeta(long id, string physical) =>
        FieldMetadata.FromValues(new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            [ColumnMapping.IdKey] = MetadataValue.Long(id),
            [ColumnMapping.PhysicalNameKey] = MetadataValue.String(physical),
        });

    // A mapped field carrying ONLY a physicalName (NO id) — the exact shape the DropChildId tamper needs so the
    // missing-id branch (not the physical-name checks, which run first and must PASS) is the sole defect.
    private static FieldMetadata PhysicalOnlyMeta(string physical) =>
        FieldMetadata.FromValues(new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            [ColumnMapping.PhysicalNameKey] = MetadataValue.String(physical),
        });

    // {id:long, c:<array|map>} MAPPED (ids 1,2). Used for the id-mode #839 boundary + tamper.
    private static StructType MappedArrayOrMap(string kind)
    {
        DataType container = kind == "array"
            ? new ArrayType(DataTypes.StringType)
            : new MapType(DataTypes.StringType, DataTypes.LongType);
        return new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, false, MappingMeta(1, "col-id")),
            new StructField("c", container, true, MappingMeta(2, "col-c")),
        });
    }

    // {id:long, items:array<struct<x>>} MAPPED (ids 1,2). A nested-within-nested #585 shape.
    private static StructType MappedNestedWithinNested() =>
        new(new[]
        {
            new StructField("id", DataTypes.LongType, false, MappingMeta(1, "col-id")),
            new StructField(
                "items",
                new ArrayType(new StructType(new[] { new StructField("x", DataTypes.LongType) })),
                true,
                MappingMeta(2, "col-items")),
        });

    // A NAME-mode array<struct<x:long>> whose interior struct child carries full column-mapping metadata (so
    // the depth>1 validator accepts it) — the #866 866a load-succeeds fixture. maxColumnId = 3.
    private static StructType MappedNameArrayOfStruct() =>
        new(new[]
        {
            new StructField("id", DataTypes.LongType, false, MappingMeta(1, "col-id")),
            new StructField(
                "items",
                new ArrayType(new StructType(new[]
                {
                    new StructField("x", DataTypes.LongType, true, MappingMeta(3, "col-x")),
                })),
                true,
                MappingMeta(2, "col-items")),
        });

    // A NAME-mode map<string, struct<v:long>> whose interior value-struct child carries full metadata.
    private static StructType MappedNameMapOfStruct() =>
        new(new[]
        {
            new StructField("id", DataTypes.LongType, false, MappingMeta(1, "col-id")),
            new StructField(
                "m",
                new MapType(DataTypes.StringType, new StructType(new[]
                {
                    new StructField("v", DataTypes.LongType, true, MappingMeta(3, "col-v")),
                })),
                true,
                MappingMeta(2, "col-m")),
        });

    // ------------------------------------------------------------------------------------------------
    // Fail-closed assertions
    // ------------------------------------------------------------------------------------------------

    // Fails closed at EITHER the load door (structurally-invalid tamper) OR the read enumeration (valid-but-
    // different identity). Asserts a TYPED exception either way — never a returned mis-mapped batch, and never
    // a stray NRE/InvalidOperation masquerading as fail-closed. Optional discriminant substrings pin the
    // message (matched against the caught exception AND its inner chain) so a WRONG-but-thrown typed error
    // cannot masquerade as the gate under test — matching the pinning the widening/#839/#585 cells use.
    private static async Task AssertReadFailsClosedAsync(
        NestedCdfTable table, DeltaChangeFeedRange range, params string[] expectedSubstrings)
    {
        Exception? caught = null;
        try
        {
            await table.ReadRangeAsync(range, DecodeStruct);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        AssertTypedFailClosed(caught);
        string chain = ExceptionChainText(caught!);
        foreach (string expected in expectedSubstrings)
        {
            Assert.Contains(expected, chain, StringComparison.Ordinal);
        }
    }

    // The concatenated Message of an exception and every inner exception — so a reason pinned on either the
    // outer classification (DeltaReadException) or the wrapped cause (DeltaProtocolException/DeltaStorageException)
    // matches regardless of which layer carries it.
    private static string ExceptionChainText(Exception exception)
    {
        var builder = new StringBuilder();
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            builder.Append(e.Message).Append('\n');
        }

        return builder.ToString();
    }

    // The shared id-mode nested-leaf WIDENING fail-closed assertion (#546 §9 O1): reading across a widening
    // boundary where a narrow (int) nested leaf is resolved by field_id against the END (long) leaf must throw
    // a TYPED SchemaMismatch whose (inner) reason is the physical-type disagreement ("... does not match the
    // requested ..."), never a silent 32→64-bit mis-read. Shared by the struct/array/map id-mode widening cells.
    private static async Task AssertIdModeWideningFailsClosedAsync<T>(
        NestedCdfTable table, DeltaChangeFeedRange range, Func<ChangeRowCursor, T> decode)
    {
        Exception? caught = null;
        try
        {
            await table.ReadRangeAsync(range, decode);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        AssertTypedFailClosed(caught);
        Assert.Contains("SchemaMismatch", caught!.Message, StringComparison.Ordinal);
        Assert.Contains("does not match the requested", ExceptionChainText(caught), StringComparison.Ordinal);
    }

    // Mirrors AssertReadFailsClosedAsync but drives the raw load+drain door directly (the boundary cells whose
    // metaData is only rejected downstream). Asserts the caught exception is one of the TYPED fail-closed set
    // (never ANY exception — a stray NRE would otherwise pass as "fail-closed"); optional discriminant
    // substrings additionally pin the message so a WRONG-but-thrown typed error cannot masquerade as the gate
    // under test (e.g. the #839/#585 cells pin their issue tag + defect phrase).
    // The load-SUCCEEDS companion (#866 866a): a name/none-mode nested-within-nested CDF table loads and
    // drains the change feed WITHOUT throwing — proof the interior struct is enabled (part of the identity),
    // not rejected and not silently skipped. The cross-version interior-drift catch is proven at the gate's
    // unit level (ColumnMappingIdentityTests.IsImmutableFrom_NameMode_*Interior* cells).
    private static async Task AssertLoadSucceedsAsync(NestedCdfTable table, DeltaChangeFeedRange range)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(table.Root);
        DeltaChangeFeedInfo info = await source.LoadChangeFeedAsync(range);
        await foreach (var _ in source.ReadChangeBatchesAsync(info))
        {
            // drain — must not throw for a name-mode depth>1 table (it is enabled, not rejected).
        }
    }

    private static async Task AssertLoadFailsClosedAsync(
        NestedCdfTable table, DeltaChangeFeedRange range, params string[] expectedSubstrings)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(table.Root);
        Exception? caught = null;
        try
        {
            DeltaChangeFeedInfo info = await source.LoadChangeFeedAsync(range);
            await foreach (var _ in source.ReadChangeBatchesAsync(info))
            {
                // drain — a silent skip would return rows and never throw.
            }
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        AssertTypedFailClosed(caught);
        foreach (string expected in expectedSubstrings)
        {
            Assert.Contains(expected, caught!.Message, StringComparison.Ordinal);
        }
    }

    // The single typed-fail-closed discriminator: the read/load must have thrown, and the thrown exception must
    // be one of the storage layer's TYPED fail-closed exceptions — a DeltaReadException, DeltaProtocolException,
    // or DeltaStorageException. An untyped NullReferenceException / InvalidOperationException / etc. is a BUG,
    // not a fail-closed, and must NOT be accepted (the old Assert.ThrowsAnyAsync<Exception> would have).
    private static void AssertTypedFailClosed(Exception? caught)
    {
        Assert.True(caught is not null, "the CDF read must fail closed with a typed exception, but it returned normally");
        Assert.True(
            caught is DeltaReadException or DeltaProtocolException or DeltaStorageException,
            $"the CDF read must fail closed with a TYPED exception (DeltaReadException/DeltaProtocolException/"
            + $"DeltaStorageException), but threw {caught!.GetType().FullName}: {caught.Message}");
    }

    // ------------------------------------------------------------------------------------------------
    // Model + decode
    // ------------------------------------------------------------------------------------------------

    private readonly record struct ExpectedChange(long Version, string ChangeType, long Id, string NestedSignature);

    private readonly record struct ActualChange(long Version, string ChangeType, long Id, string NestedSignature);

    private static ExpectedChange Insert(long version, (long Id, string Sig) row) =>
        new(version, ChangeDataWriter.InsertChange, row.Id, row.Sig);

    private static void AddInserts(List<ExpectedChange> model, long version, params (long Id, string Sig)[] rows)
    {
        foreach ((long id, string sig) in rows)
        {
            model.Add(new ExpectedChange(version, ChangeDataWriter.InsertChange, id, sig));
        }
    }

    private static void AddDeletes(List<ExpectedChange> model, long version, params (long Id, string Sig)[] rows)
    {
        foreach ((long id, string sig) in rows)
        {
            model.Add(new ExpectedChange(version, ChangeDataWriter.DeleteChange, id, sig));
        }
    }

    private static void AssertMultisetEqual(List<ExpectedChange> model, List<ActualChange> actual)
    {
        var expected = model
            .Select(m => (m.Version, m.ChangeType, m.Id, m.NestedSignature))
            .OrderBy(t => t.Version).ThenBy(t => t.ChangeType, StringComparer.Ordinal).ThenBy(t => t.Id)
            .ToArray();
        var observed = actual
            .Select(a => (a.Version, a.ChangeType, a.Id, a.NestedSignature))
            .OrderBy(t => t.Version).ThenBy(t => t.ChangeType, StringComparer.Ordinal).ThenBy(t => t.Id)
            .ToArray();
        Assert.Equal(expected, observed);
    }

    // ---- per-shape decoders (nested content, not just row counts) ----

    private static ActualChange DecodeStruct(ChangeRowCursor c) => DecodeStructWithChild(c, "b");

    private static ActualChange DecodeStructWithChild(ChangeRowCursor c, string secondChild)
    {
        var pt = (StructColumnVector)c.Batch.Column(c.Schema.IndexOf("pt"));
        // Assert the reconciled output surfaces the second child under its END logical name (physical→logical).
        Assert.Equal(secondChild, ((StructType)FindField(c.Schema, "pt").DataType)[1].Name);

        string sig;
        if (pt.IsNull(c.Row))
        {
            sig = StructSig(c.Id, null, null, nullStruct: true).Sig;      // a NULL struct row, distinct from present
        }
        else
        {
            ColumnVector aVec = pt.Child(0);
            ColumnVector bVec = pt.Child(1);
            long? a = aVec.IsNull(c.Row) ? null : aVec.GetValue<long>(c.Row);
            long? b = bVec.IsNull(c.Row) ? null : bVec.GetValue<long>(c.Row);
            sig = StructSig(c.Id, a, b, nullStruct: false).Sig;           // present, with per-child null distinct
        }

        return new ActualChange(c.Version, c.ChangeType, c.Id, sig);
    }

    // Decodes a struct that has had its second child (b) DROPPED — the reconciled output surfaces struct<a>
    // only. Renders child a (a null struct or a null a-child stays distinct from a present 0), so a mis-bind or
    // a stray un-projected b would surface as a signature mismatch.
    private static ActualChange DecodeStructChildAOnly(ChangeRowCursor c)
    {
        var ptType = (StructType)FindField(c.Schema, "pt").DataType;
        Assert.Equal(new[] { "a" }, ptType.Select(f => f.Name).ToArray());   // b projected out of the END shape
        var pt = (StructColumnVector)c.Batch.Column(c.Schema.IndexOf("pt"));
        // Directly encode "no stray b at index 1": the decoded vector itself carries exactly ONE field child,
        // not merely the schema shape — a dropped-but-still-materialized b would surface here.
        Assert.Equal(1, pt.FieldCount);

        string sig;
        if (pt.IsNull(c.Row))
        {
            sig = "struct=<null>";
        }
        else
        {
            ColumnVector aVec = pt.Child(0);
            long? a = aVec.IsNull(c.Row) ? null : aVec.GetValue<long>(c.Row);
            sig = StructASig(c.Id, a).Sig;
        }

        return new ActualChange(c.Version, c.ChangeType, c.Id, sig);
    }

    private static ActualChange DecodeArray(ChangeRowCursor c)
    {
        var tags = (ListColumnVector)c.Batch.Column(c.Schema.IndexOf("tags"));
        if (tags.IsNull(c.Row))
        {
            return new ActualChange(c.Version, c.ChangeType, c.Id, NullArray(c.Id).Sig); // NULL list ≠ empty list
        }

        ColumnVector elements = tags.ElementsAt(c.Row);
        var values = new List<string>();
        for (int e = 0; e < elements.Length; e++)
        {
            values.Add(elements.IsNull(e) ? "<null>" : Encoding.UTF8.GetString(elements.GetBytes(e)));
        }

        return new ActualChange(c.Version, c.ChangeType, c.Id, ArraySig(c.Id, values.ToArray()).Sig);
    }

    // Decodes a scalar-LONG array's elements (the array-element widening cell reads its END array<long>) — the
    // pre-widening narrow int elements must arrive here already promoted to long, so a lost promotion surfaces
    // as a decode/type mismatch rather than a silent 32-bit read.
    private static ActualChange DecodeLongArray(ChangeRowCursor c)
    {
        var tags = (ListColumnVector)c.Batch.Column(c.Schema.IndexOf("tags"));
        if (tags.IsNull(c.Row))
        {
            return new ActualChange(c.Version, c.ChangeType, c.Id, NullArray(c.Id).Sig);
        }

        ColumnVector elements = tags.ElementsAt(c.Row);
        var values = new List<long>();
        for (int e = 0; e < elements.Length; e++)
        {
            values.Add(elements.GetValue<long>(e));
        }

        return new ActualChange(c.Version, c.ChangeType, c.Id, LongArraySig(c.Id, values.ToArray()).Sig);
    }

    private static ActualChange DecodeMap(ChangeRowCursor c)
    {
        var props = (MapColumnVector)c.Batch.Column(c.Schema.IndexOf("props"));
        if (props.IsNull(c.Row))
        {
            return new ActualChange(c.Version, c.ChangeType, c.Id, NullMap(c.Id).Sig); // NULL map ≠ empty map
        }

        ColumnVector keys = props.KeysAt(c.Row);
        ColumnVector vals = props.ValuesAt(c.Row);
        var entries = new List<(string, long?)>();
        for (int e = 0; e < keys.Length; e++)
        {
            long? value = vals.IsNull(e) ? null : vals.GetValue<long>(e);
            entries.Add((Encoding.UTF8.GetString(keys.GetBytes(e)), value));
        }

        return new ActualChange(c.Version, c.ChangeType, c.Id, MapSig(c.Id, entries.ToArray()).Sig);
    }

    // ---- signatures (canonical, order-preserving string encodings of nested content; null ≠ empty ≠ absent) ----

    private static (long Id, string Sig) StructSig(long id, long a, long b) => StructSig(id, a, b, nullStruct: false);

    // A dropped-child struct signature: only child a survives (child b removed by a metadata-only drop). A null
    // a-child renders "<null>" (distinct from a 0 value), so a lost null cannot pass as an equal value.
    private static (long Id, string Sig) StructASig(long id, long? a) =>
        (id, string.Create(CultureInfo.InvariantCulture, $"struct(a={LongText(a)})"));

    // A whole-null struct row renders "struct=<null>"; a present struct renders its children, each null child as
    // "<null>" (distinct from a 0 value) — so a lost null cannot pass as an equal value.
    private static (long Id, string Sig) StructSig(long id, long? a, long? b, bool nullStruct) =>
        (id, nullStruct
            ? "struct=<null>"
            : string.Create(CultureInfo.InvariantCulture, $"struct(a={LongText(a)},b={LongText(b)})"));

    private static (long Id, string Sig) ArraySig(long id, params string[] elements) =>
        (id, "array[" + string.Join(",", elements) + "]");

    // Long-element array signature (the array-element widening cell): renders each promoted long element.
    private static (long Id, string Sig) LongArraySig(long id, params long[] elements) =>
        (id, "array[" + string.Join(",", elements.Select(e => e.ToString(CultureInfo.InvariantCulture))) + "]");

    private static (long Id, string Sig) NullArray(long id) => (id, "array=<null>");

    private static (long Id, string Sig) MapSig(long id, params (string Key, long Value)[] entries) =>
        MapSig(id, entries.Select(e => (e.Key, (long?)e.Value)).ToArray());

    private static (long Id, string Sig) MapSig(long id, params (string Key, long? Value)[] entries) =>
        (id, "map{" + string.Join(",", entries.Select(e =>
            string.Create(CultureInfo.InvariantCulture, $"{e.Key}={LongText(e.Value)}"))) + "}");

    private static (long Id, string Sig) NullMap(long id) => (id, "map=<null>");

    private static string LongText(long? v) =>
        v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "<null>";

    // ------------------------------------------------------------------------------------------------
    // Batch builders (logical-named nested vectors)
    // ------------------------------------------------------------------------------------------------

    private static ColumnBatch StructBatch(NestedCdfTable table, params (long Id, long A, long B)[] rows) =>
        StructBatchNullable(table, rows.Select(r => (r.Id, (long?)r.A, (long?)r.B, false)).ToArray());

    // A NARROW {id:long, pt:struct<a:int, b:int>} logical batch for the widening fixture — the a,b vectors are
    // INT (the pre-widening leaf type); the file it writes must read-PROMOTE to long once the table widens.
    // Disjoint domains (a from [1000..], b from [2000..]) keep a positional mis-bind detectable.
    private static ColumnBatch StructIntBatch(NestedCdfTable table, params (long Id, int A, int B)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector a = ColumnVectors.Create(DataTypes.IntegerType, rows.Length);
        MutableColumnVector b = ColumnVectors.Create(DataTypes.IntegerType, rows.Length);
        foreach ((long rid, int ra, int rb) in rows)
        {
            id.AppendValue(rid);
            a.AppendValue(ra);
            b.AppendValue(rb);
        }

        var ptType = (StructType)table.LogicalSchema["pt"].DataType;
        var pt = new StructColumnVector(ptType, new ColumnVector[] { a, b }, new bool[rows.Length]);
        return new ManagedColumnBatch(table.LogicalSchema, new ColumnVector[] { id, pt }, rows.Length);
    }

    // Builds a {id, pt:struct<a,b>} logical batch, supporting a NULL struct row (whole container null) and a
    // NULL child (a and/or b null). The struct child NAMES come from the table's CURRENT logical schema; the
    // physical file is authored purely by the physical mapping (RelabelBatch), so a post-rename append is
    // byte-identical regardless of the logical names carried here — the mapping is by physical name / field_id,
    // never by the batch's logical child names. Null is rendered DISTINCT from empty/absent by the decoders +
    // signatures, so a lost null (decoded as 0, or a null struct decoded as present) surfaces as a mismatch.
    private static ColumnBatch StructBatchNullable(
        NestedCdfTable table, params (long Id, long? A, long? B, bool NullStruct)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector a = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector b = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        var structNulls = new bool[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            (long rid, long? ra, long? rb, bool nullStruct) = rows[i];
            id.AppendValue(rid);
            AppendNullableLong(a, ra);
            AppendNullableLong(b, rb);
            structNulls[i] = nullStruct;
        }

        var ptType = (StructType)table.LogicalSchema["pt"].DataType;
        var pt = new StructColumnVector(ptType, new ColumnVector[] { a, b }, structNulls);
        return new ManagedColumnBatch(table.LogicalSchema, new ColumnVector[] { id, pt }, rows.Length);
    }

    private static void AppendNullableLong(MutableColumnVector v, long? value)
    {
        if (value.HasValue)
        {
            v.AppendValue(value.Value);
        }
        else
        {
            v.AppendNull();
        }
    }

    private static ColumnBatch ArrayBatch(NestedCdfTable table, params (long Id, string[] Tags)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector elements = ColumnVectors.Create(DataTypes.StringType, 16);
        var offsets = new int[rows.Length + 1];
        int cursor = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            id.AppendValue(rows[i].Id);
            offsets[i] = cursor;
            foreach (string tag in rows[i].Tags)
            {
                elements.AppendBytes(Encoding.UTF8.GetBytes(tag));
                cursor++;
            }
        }

        offsets[rows.Length] = cursor;
        var arrType = (ArrayType)table.LogicalSchema["tags"].DataType;
        var tags = new ListColumnVector(arrType, elements, offsets, new bool[rows.Length]);
        return new ManagedColumnBatch(table.LogicalSchema, new ColumnVector[] { id, tags }, rows.Length);
    }

    // Narrow (int-element) array builder for the array-element widening fixture — the file it writes must
    // read-promote its elements to long once the table widens (name mode) or fail closed (id mode).
    private static ColumnBatch ArrayIntBatch(NestedCdfTable table, params (long Id, int[] Tags)[] rows) =>
        ArrayScalarBatch(table, DataTypes.IntegerType, rows, (v, x) => v.AppendValue(x));

    // Wide (long-element) array builder — used for the post-widen append (an element > int.MaxValue).
    private static ColumnBatch ArrayLongBatch(NestedCdfTable table, params (long Id, long[] Tags)[] rows) =>
        ArrayScalarBatch(table, DataTypes.LongType, rows, (v, x) => v.AppendValue(x));

    // Shared scalar-array builder: writes each row's fixed-width elements into a shared child, keyed off the
    // table's CURRENT logical `tags` element type (int before a widen, long after).
    private static ColumnBatch ArrayScalarBatch<TElem>(
        NestedCdfTable table, DataType elementType, (long Id, TElem[] Tags)[] rows,
        Action<MutableColumnVector, TElem> append)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector elements = ColumnVectors.Create(elementType, 16);
        var offsets = new int[rows.Length + 1];
        int cursor = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            id.AppendValue(rows[i].Id);
            offsets[i] = cursor;
            foreach (TElem element in rows[i].Tags)
            {
                append(elements, element);
                cursor++;
            }
        }

        offsets[rows.Length] = cursor;
        var arrType = (ArrayType)table.LogicalSchema["tags"].DataType;
        var tags = new ListColumnVector(arrType, elements, offsets, new bool[rows.Length]);
        return new ManagedColumnBatch(table.LogicalSchema, new ColumnVector[] { id, tags }, rows.Length);
    }
    // element string appends a null element into the shared child. Distinct from an empty (non-null) list.
    private static ColumnBatch ArrayBatchNullable(NestedCdfTable table, params (long Id, string?[]? Tags)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector elements = ColumnVectors.Create(DataTypes.StringType, 16);
        var offsets = new int[rows.Length + 1];
        var nulls = new bool[rows.Length];
        int cursor = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            id.AppendValue(rows[i].Id);
            offsets[i] = cursor;
            if (rows[i].Tags is null)
            {
                nulls[i] = true;
                continue;
            }

            foreach (string? tag in rows[i].Tags!)
            {
                if (tag is null)
                {
                    elements.AppendNull();
                }
                else
                {
                    elements.AppendBytes(Encoding.UTF8.GetBytes(tag));
                }

                cursor++;
            }
        }

        offsets[rows.Length] = cursor;
        var arrType = (ArrayType)table.LogicalSchema["tags"].DataType;
        var tags = new ListColumnVector(arrType, elements, offsets, nulls);
        return new ManagedColumnBatch(table.LogicalSchema, new ColumnVector[] { id, tags }, rows.Length);
    }

    private static ColumnBatch MapBatch(NestedCdfTable table, params (long Id, (string Key, long Value)[] Props)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.StringType, 16);
        MutableColumnVector values = ColumnVectors.Create(DataTypes.LongType, 16);
        var offsets = new int[rows.Length + 1];
        int cursor = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            id.AppendValue(rows[i].Id);
            offsets[i] = cursor;
            foreach ((string key, long value) in rows[i].Props)
            {
                keys.AppendBytes(Encoding.UTF8.GetBytes(key));
                values.AppendValue(value);
                cursor++;
            }
        }

        offsets[rows.Length] = cursor;
        var mapType = (MapType)table.LogicalSchema["props"].DataType;
        var props = new MapColumnVector(mapType, keys, values, offsets, new bool[rows.Length]);
        return new ManagedColumnBatch(table.LogicalSchema, new ColumnVector[] { id, props }, rows.Length);
    }

    // Narrow (int-value) map builder for the map-value widening fixture — the file it writes must read-promote
    // its VALUES to long once the table widens (name mode) or fail closed (id mode). The KEY stays string.
    private static ColumnBatch MapIntBatch(NestedCdfTable table, params (long Id, (string Key, int Value)[] Props)[] rows) =>
        MapScalarValueBatch(table, DataTypes.IntegerType, rows, (v, x) => v.AppendValue(x));

    // Wide (long-value) map builder — used for the post-widen append (a value > int.MaxValue).
    private static ColumnBatch MapLongBatch(NestedCdfTable table, params (long Id, (string Key, long Value)[] Props)[] rows) =>
        MapScalarValueBatch(table, DataTypes.LongType, rows, (v, x) => v.AppendValue(x));

    // Shared string-keyed scalar-value map builder, keyed off the table's CURRENT logical `props` value type
    // (int before a widen, long after); the key vector is always string.
    private static ColumnBatch MapScalarValueBatch<TVal>(
        NestedCdfTable table, DataType valueType, (long Id, (string Key, TVal Value)[] Props)[] rows,
        Action<MutableColumnVector, TVal> appendValue)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.StringType, 16);
        MutableColumnVector values = ColumnVectors.Create(valueType, 16);
        var offsets = new int[rows.Length + 1];
        int cursor = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            id.AppendValue(rows[i].Id);
            offsets[i] = cursor;
            foreach ((string key, TVal value) in rows[i].Props)
            {
                keys.AppendBytes(Encoding.UTF8.GetBytes(key));
                appendValue(values, value);
                cursor++;
            }
        }

        offsets[rows.Length] = cursor;
        var mapType = (MapType)table.LogicalSchema["props"].DataType;
        var props = new MapColumnVector(mapType, keys, values, offsets, new bool[rows.Length]);
        return new ManagedColumnBatch(table.LogicalSchema, new ColumnVector[] { id, props }, rows.Length);
    }
    // appends a null map value (the table's props map is valueContainsNull). Distinct from an empty map.
    private static ColumnBatch MapBatchNullable(
        NestedCdfTable table, params (long Id, (string Key, long? Value)[]? Props)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.StringType, 16);
        MutableColumnVector values = ColumnVectors.Create(DataTypes.LongType, 16);
        var offsets = new int[rows.Length + 1];
        var nulls = new bool[rows.Length];
        int cursor = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            id.AppendValue(rows[i].Id);
            offsets[i] = cursor;
            if (rows[i].Props is null)
            {
                nulls[i] = true;
                continue;
            }

            foreach ((string key, long? value) in rows[i].Props!)
            {
                keys.AppendBytes(Encoding.UTF8.GetBytes(key));
                AppendNullableLong(values, value);
                cursor++;
            }
        }

        offsets[rows.Length] = cursor;
        var mapType = (MapType)table.LogicalSchema["props"].DataType;
        var props = new MapColumnVector(mapType, keys, values, offsets, nulls);
        return new ManagedColumnBatch(table.LogicalSchema, new ColumnVector[] { id, props }, rows.Length);
    }

    // ------------------------------------------------------------------------------------------------
    // Fixtures + shared helpers
    // ------------------------------------------------------------------------------------------------

    private NestedCdfTable NewStructTable(ColumnMappingMode mode)
    {
        var logical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("pt", new StructType(new[]
            {
                new StructField("a", DataTypes.LongType, nullable: true),
                new StructField("b", DataTypes.LongType, nullable: true),
            }), nullable: true),
        });
        return new NestedCdfTable(NewRoot(), mode, logical, "cdf-nested-struct");
    }

    // Widening fixture: {id:long, pt:struct<a:int, b:int>} with typeWidening + CDF enabled. A later WidenAsync
    // promotes a,b int->long; the pre-widening (narrow int) file must read-promote to long across the CDF
    // boundary (#546), while a subsequent WIDE append proves genuine widening with a long-only value.
    private NestedCdfTable NewWideningStructTable(ColumnMappingMode mode)
    {
        var logical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("pt", new StructType(new[]
            {
                new StructField("a", DataTypes.IntegerType, nullable: true),
                new StructField("b", DataTypes.IntegerType, nullable: true),
            }), nullable: true),
        });
        return new NestedCdfTable(NewRoot(), mode, logical, "cdf-nested-widen", enableTypeWidening: true);
    }

    // Widens every child of a struct container to long, PRESERVING each child's mapping metadata (id +
    // physicalName) — a nested type widening that keeps the leaf's identity and changes ONLY its type. Applies
    // to both the logical schema (children carry no metadata → the empty metadata rides through) and the mapped
    // schema (children carry id + physicalName → preserved verbatim, so a CDF read sees no identity change).
    private static StructType WidenStructChildrenToLong(StructType schema, string container) =>
        MapContainerChildren(schema, container, children =>
            children.Select(c => new StructField(c.Name, DataTypes.LongType, c.Nullable, c.Metadata)).ToArray());

    // Widening fixture: {id:long, tags:array<int>} with typeWidening + CDF enabled. A later WidenAsync promotes
    // the array ELEMENT int->long; the pre-widening narrow file's elements must read-promote (name mode) or
    // fail closed (id mode, #546 §9 O1).
    private NestedCdfTable NewWideningArrayTable(ColumnMappingMode mode)
    {
        var logical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("tags", new ArrayType(DataTypes.IntegerType), nullable: true),
        });
        return new NestedCdfTable(NewRoot(), mode, logical, "cdf-nested-widen-array", enableTypeWidening: true);
    }

    // Widening fixture: {id:long, props:map<string,int>} with typeWidening + CDF enabled. A later WidenAsync
    // promotes the map VALUE int->long (the KEY stays string — key widening is not a sanctioned/typical change).
    private NestedCdfTable NewWideningMapTable(ColumnMappingMode mode)
    {
        var logical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("props", new MapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true), nullable: true),
        });
        return new NestedCdfTable(NewRoot(), mode, logical, "cdf-nested-widen-map", enableTypeWidening: true);
    }

    // Widens a top-level array<int> container's ELEMENT leaf to long, PRESERVING the container field's mapping
    // metadata (id + physicalName + nested.ids) — only the interior element type changes; the container
    // identity is untouched. Applies to both the logical and the mapped schema.
    private static StructType WidenArrayElementToLong(StructType schema, string container) =>
        new(schema.Select(f => f.Name == container
            ? new StructField(f.Name, new ArrayType(DataTypes.LongType), f.Nullable, f.Metadata)
            : f).ToList());

    // Widens a top-level map<string,int> container's VALUE leaf to long, PRESERVING the container field's
    // mapping metadata (id + physicalName + nested.ids) and its KEY type — only the interior value type
    // changes. Applies to both the logical and the mapped schema.
    private static StructType WidenMapValueToLong(StructType schema, string container) =>
        new(schema.Select(f =>
        {
            if (f.Name != container)
            {
                return f;
            }

            var map = (MapType)f.DataType;
            return new StructField(
                f.Name, new MapType(map.KeyType, DataTypes.LongType, map.ValueContainsNull), f.Nullable, f.Metadata);
        }).ToList());

    private NestedCdfTable NewArrayTable(ColumnMappingMode mode = ColumnMappingMode.Name)
    {
        var logical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("tags", new ArrayType(DataTypes.StringType), nullable: true),
        });
        return new NestedCdfTable(NewRoot(), mode, logical, "cdf-nested-array");
    }

    private NestedCdfTable NewMapTable(ColumnMappingMode mode = ColumnMappingMode.Name)
    {
        var logical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("props", new MapType(DataTypes.StringType, DataTypes.LongType, valueContainsNull: true), nullable: true),
        });
        return new NestedCdfTable(NewRoot(), mode, logical, "cdf-nested-map");
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "ds-cdf-nested-" + Guid.NewGuid().ToString("N"));

    private static StructField FindField(StructType schema, string name)
    {
        Assert.True(schema.TryGetField(name, out StructField field), $"output schema must surface '{name}'");
        return field;
    }

    private static string Physical(StructField field)
    {
        Assert.True(
            field.Metadata.TryGetString(ColumnMapping.PhysicalNameKey, out string? physical) && physical is not null,
            $"field '{field.Name}' has no physicalName");
        return physical!;
    }

    private static long Id(StructField field)
    {
        Assert.True(ColumnMapping.TryGetId(field, out long id), $"field '{field.Name}' has no id");
        return id;
    }

    // A single decoded change row's read cursor (batch + row + reconciled schema + metadata).
    private readonly record struct ChangeRowCursor(
        ColumnBatch Batch, StructType Schema, int Row, long Id, long Version, string ChangeType);

    // ------------------------------------------------------------------------------------------------
    // NestedCdfTable — a raw-authored, CDF-enabled, column-mapped NESTED Delta table over a temp backend.
    // Reads go through the UNMODIFIED production ChangeFeedReader; only the log authoring is hand-rolled (the
    // DeltaWriteTarget facade cannot encode nested-typed batches — see NestedColumnMappingTests). Deterministic:
    // physical names are seeded, data-file names are counter-based; no Guid in any asserted surface.
    // ------------------------------------------------------------------------------------------------
    private sealed class NestedCdfTable : IDisposable
    {
        private readonly string _root;
        private readonly ColumnMappingMode _mode;
        private readonly bool _enableTypeWidening;
        private StructType _logical;
        private StructType _mapped;
        private long _maxColumnId;
        private readonly LocalFileSystemBackend _backend;
        private StructType? _physical;
        private long _version = -1;
        private int _fileCounter;

        private NestedCdfTable(
            string root, ColumnMappingMode mode, StructType logical, StructType mapped, long maxColumnId,
            bool enableTypeWidening = false)
        {
            _root = root;
            _mode = mode;
            _enableTypeWidening = enableTypeWidening;
            _logical = logical;
            _mapped = mapped;
            _maxColumnId = maxColumnId;
            Directory.CreateDirectory(root);
            _backend = new LocalFileSystemBackend(root);
        }

        /// <summary>Mints a fresh mapping from a logical schema (the normal, writable path). The mint runs in
        /// the table's own <paramref name="mode"/> so an id-mode <c>array</c>/<c>map</c> container mints its
        /// interior element/key/value ids and carries a <c>delta.columnMapping.nested.ids</c> value (#839/#854)
        /// — the shape the CDF read door now reads THROUGH. (For a struct, id and name minting are byte-identical:
        /// every StructField gets an id + physicalName and no nested.ids, so the pre-existing struct cells are
        /// unaffected.)</summary>
        public NestedCdfTable(
            string root, ColumnMappingMode mode, StructType logical, string seed, bool enableTypeWidening = false)
            : this(root, mode, logical, Mint(logical, seed, mode, out long max), max, enableTypeWidening)
        {
        }

        /// <summary>Wraps an ALREADY-MAPPED (possibly deferred-shape) schema without minting — for the
        /// #839/#585 boundary tables whose shape <see cref="ColumnMapping.AssignFreshMapping"/> would reject.</summary>
        public static NestedCdfTable FromMapped(
            string root, ColumnMappingMode mode, StructType logical, StructType mapped, long maxColumnId) =>
            new(root, mode, logical, mapped, maxColumnId);

        private static StructType Mint(StructType logical, string seed, ColumnMappingMode mode, out long maxColumnId)
        {
            (StructType mapped, long max) =
                ColumnMapping.AssignFreshMapping(logical, new SeededPhysicalNameSource(seed), mode);
            maxColumnId = max;
            return mapped;
        }

        public string Root => _root;

        public StructType LogicalSchema => _logical;

        public StructType MappedSchema => _mapped;

        public long MaxColumnId => _maxColumnId;

        public long LatestVersion => _version;

        public readonly record struct FileRef(string Path, long Size, int RowCount);

        public void Dispose()
        {
            _backend.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }

        /// <summary>v0: protocol + metaData (CDF enabled, column-mapped nested schema) — an EMPTY table so the
        /// change-feed range starts from the empty baseline.</summary>
        public async Task CreateAsync()
        {
            await CommitAsync(0, ProtocolLine(), MetadataLine(_mapped, _maxColumnId));
            _version = 0;
        }

        /// <summary>Same as <see cref="CreateAsync"/> but for the boundary tables: a raw v0 metaData whose shape
        /// is only rejected downstream at the load/read door (never written to, so no physical schema needed).</summary>
        public Task CreateRawMetadataAsync() => CreateAsync();

        /// <summary>Appends one data file (a real nested Parquet file) as an <c>add(dataChange=true)</c> —
        /// derived at read time as an <c>insert</c> per row.</summary>
        public async Task<FileRef> AppendAsync(ColumnBatch logicalBatch)
        {
            FileRef file = await WriteDataAsync(logicalBatch);
            await CommitAsync(++_version, AddLine(file));
            return file;
        }

        /// <summary>Removes the given data files in one commit (<c>remove(dataChange=true)</c>) — derived at
        /// read time as a <c>delete</c> per live row of each removed file (the file stays on disk for the read).</summary>
        public async Task<long> RemoveAsync(params FileRef[] files)
        {
            long v = ++_version;
            await CommitAsync(v, files.Select(RemoveLine).ToArray());
            return v;
        }

        /// <summary>Static overwrite: remove the given files and add a new one in ONE commit — derived as
        /// <c>delete</c> (removed rows) + <c>insert</c> (new rows) at that version.</summary>
        public async Task<FileRef> OverwriteAsync(ColumnBatch logicalBatch, params FileRef[] removeFiles)
        {
            FileRef added = await WriteDataAsync(logicalBatch);
            string[] lines = removeFiles.Select(RemoveLine).Append(AddLine(added)).ToArray();
            await CommitAsync(++_version, lines);
            return added;
        }

        /// <summary>Commits a metaData-ONLY version carrying <paramref name="mapped"/> — a clean restatement OR
        /// a forged/tampered identity. Contributes ZERO change rows (dataChange=false).</summary>
        public async Task<long> CommitMetadataAsync(StructType mapped, long maxColumnId)
        {
            long v = ++_version;
            await CommitAsync(v, MetadataLine(mapped, maxColumnId));
            return v;
        }

        /// <summary>Commits a metaData-ONLY version that WIDENS the table to <paramref name="widenedMapped"/>
        /// (a nested leaf's type promoted, every id/physicalName preserved) and RE-BASES the table so a
        /// subsequent append writes the WIDER physical type. Contributes ZERO change rows; the pre-widening
        /// data files keep their NARROW bytes on disk (the read must promote them to the END type).</summary>
        public async Task<long> WidenAsync(StructType widenedLogical, StructType widenedMapped, long maxColumnId)
        {
            long v = ++_version;
            await CommitAsync(v, MetadataLine(widenedMapped, maxColumnId));
            _logical = widenedLogical;
            _mapped = widenedMapped;
            _maxColumnId = maxColumnId;
            _physical = null;   // recompute the physical write schema against the widened mapping on next append
            return v;
        }

        /// <summary>Reads a CDF range through the production door and decodes each change row with
        /// <paramref name="decode"/> (which sees the reconciled output schema + the row's nested vectors).</summary>
        public async Task<(StructType Schema, List<T> Rows)> ReadRangeAsync<T>(
            DeltaChangeFeedRange range, Func<ChangeRowCursor, T> decode)
        {
            using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
            DeltaChangeFeedInfo info = await source.LoadChangeFeedAsync(range);
            StructType schema = info.Schema;
            int idIdx = schema.IndexOf("id");
            int changeTypeIdx = schema.IndexOf(ChangeDataWriter.ChangeTypeColumn);
            int versionIdx = schema.IndexOf(ChangeDataWriter.CommitVersionColumn);

            var rows = new List<T>();
            await foreach (ColumnBatch batch in source.ReadChangeBatchesAsync(info))
            {
                ColumnVector id = batch.Column(idIdx);
                ColumnVector changeType = batch.Column(changeTypeIdx);
                ColumnVector version = batch.Column(versionIdx);
                for (int r = 0; r < batch.RowCount; r++)
                {
                    rows.Add(decode(new ChangeRowCursor(
                        batch,
                        schema,
                        r,
                        id.GetValue<long>(r),
                        version.GetValue<long>(r),
                        Encoding.UTF8.GetString(changeType.GetBytes(r)))));
                }
            }

            return (schema, rows);
        }

        /// <summary>The current (latest) committed logical schema — nested leaves carry their physical
        /// names / field ids; the physical↔logical reconciliation witness.</summary>
        public async Task<StructType> LoadEndSchemaAsync()
        {
            Snapshot snapshot = await new DeltaLog(_backend).LoadSnapshotAsync(version: null);
            return snapshot.Schema;
        }

        private async Task<FileRef> WriteDataAsync(ColumnBatch logicalBatch)
        {
            _physical ??= ColumnMapping.MapWriteSchemaToPhysical(_logical, _mapped, _mode);
            ColumnBatch physicalBatch = RelabelBatch(logicalBatch, _physical);
            byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(_physical, new[] { physicalBatch });
            string path = string.Create(CultureInfo.InvariantCulture, $"part-{_fileCounter++:D5}.parquet");
            await _backend.PutIfAbsentAsync(path, bytes, CancellationToken.None);
            return new FileRef(path, bytes.Length, logicalBatch.RowCount);
        }

        private async Task CommitAsync(long version, params string[] lines)
        {
            string name = "_delta_log/" + version.ToString("D20", CultureInfo.InvariantCulture) + ".json";
            byte[] body = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
            await _backend.PutIfAbsentAsync(name, body, CancellationToken.None);
        }

        private string ProtocolLine()
        {
            // typeWidening requires the feature named in BOTH reader and writer feature lists at reader v3 /
            // writer v7 (TypeWideningFeature.Supports) — that is exactly what makes the CDF read PROMOTE
            // pre-widening (narrow) files to the END (wider) type. columnMapping/changeDataFeed are always on.
            string writerFeatures = _enableTypeWidening
                ? "[\"columnMapping\",\"changeDataFeed\",\"typeWidening\"]"
                : "[\"columnMapping\",\"changeDataFeed\"]";
            string readerFeatures = _enableTypeWidening
                ? "[\"columnMapping\",\"typeWidening\"]"
                : "[\"columnMapping\"]";
            return "{\"protocol\":{\"minReaderVersion\":3,\"minWriterVersion\":7,"
                + "\"readerFeatures\":" + readerFeatures + ","
                + "\"writerFeatures\":" + writerFeatures + "}}";
        }

        private string MetadataLine(StructType mapped, long maxColumnId)
        {
            string modeName = _mode == ColumnMappingMode.Id ? "id" : "name";
            string schemaJson = System.Text.Json.JsonSerializer.Serialize(DeltaSchemaJson.ToJson(mapped));
            string typeWideningProperty = _enableTypeWidening ? ",\"delta.enableTypeWidening\":\"true\"" : string.Empty;
            string config =
                "{\"delta.columnMapping.mode\":\"" + modeName + "\","
                + "\"delta.columnMapping.maxColumnId\":\""
                + maxColumnId.ToString(CultureInfo.InvariantCulture) + "\","
                + "\"delta.enableChangeDataFeed\":\"true\"" + typeWideningProperty + "}";
            return "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
                + "\"schemaString\":" + schemaJson + ",\"partitionColumns\":[],\"configuration\":" + config + "}}";
        }

        private static string AddLine(FileRef file) =>
            "{\"add\":{\"path\":\"" + file.Path + "\",\"partitionValues\":{},\"size\":"
            + file.Size.ToString(CultureInfo.InvariantCulture)
            + ",\"modificationTime\":0,\"dataChange\":true,\"stats\":\"{\\\"numRecords\\\":"
            + file.RowCount.ToString(CultureInfo.InvariantCulture) + "}\"}}";

        private static string RemoveLine(FileRef file) =>
            "{\"remove\":{\"path\":\"" + file.Path + "\",\"deletionTimestamp\":0,\"dataChange\":true,"
            + "\"partitionValues\":{},\"size\":" + file.Size.ToString(CultureInfo.InvariantCulture) + "}}";

        // Rewraps a logical-named nested batch under the PHYSICAL schema (only STRUCT columns carry field names,
        // so only they need reconstruction; array/map interiors and scalar leaves ride through unchanged). Same
        // technique as NestedColumnMappingTests.RelabelBatch.
        private static ColumnBatch RelabelBatch(ColumnBatch batch, StructType physicalSchema)
        {
            var cols = new ColumnVector[physicalSchema.Count];
            for (int i = 0; i < physicalSchema.Count; i++)
            {
                ColumnVector col = batch.Column(i);
                if (physicalSchema[i].DataType is StructType pst && col is StructColumnVector scv)
                {
                    var children = new ColumnVector[pst.Count];
                    for (int j = 0; j < pst.Count; j++)
                    {
                        children[j] = scv.Child(j);
                    }

                    var nulls = new bool[scv.Length];
                    for (int r = 0; r < scv.Length; r++)
                    {
                        nulls[r] = scv.IsNull(r);
                    }

                    cols[i] = new StructColumnVector(pst, children, nulls);
                }
                else
                {
                    cols[i] = col;
                }
            }

            return new ManagedColumnBatch(physicalSchema, cols, batch.RowCount);
        }
    }
}

/// <summary>File-scoped fluent helper: rebuild a <see cref="StructField"/> with replaced metadata (no
/// <c>WithMetadata</c> on the abstraction, so the nested-CDF tamper authoring adds one).</summary>
internal static class NestedCdfStructFieldExtensions
{
    public static StructField WithMetadata(this StructField field, FieldMetadata metadata) =>
        new(field.Name, field.DataType, field.Nullable, metadata);
}
