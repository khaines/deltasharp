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
/// and <c>map&lt;string,long&gt;</c> (name mode). Append / overwrite / delete across versions; change-row
/// value fidelity for the nested column (struct children / array elements / map entries — not just counts) AND
/// reconciled OUTPUT SCHEMA physical↔logical fidelity for the nested leaves.</item>
/// <item>AC2 — nested-leaf CDF identity immutability across retained versions: a nested-child LOGICAL rename
/// (id + physicalName preserved) reads through; a forged nested-child identity CHANGE fails closed.</item>
/// <item>AC3 — a seeded 200-iteration nested-leaf tamper fuzz that fails closed with a TYPED exception on every
/// enumerated tamper; same-typed siblings draw from DISJOINT domains so a positional mis-bind cannot pass.</item>
/// <item>AC4 — boundary: an id-mode <c>array</c>/<c>map</c> (#839) and a nested-within-nested (#585) CDF table
/// fail closed at the load/read door (not a silent skip); name-mode array/map stays fully exercised.</item>
/// </list></para>
/// <para>Every same-typed sibling (the <c>struct&lt;a:long,b:long&gt;</c> children, the array elements, the
/// map values) is drawn from a DISJOINT numeric/string domain, so a physical→logical mis-bind surfaces as a
/// value mismatch rather than passing on equal values (design §3 preamble, shared with the #676 oracle).</para>
/// <para><b>Scope.</b> This oracle exercises the IMPLICIT add/remove CDF derivation path only (append /
/// overwrite / delete files); it does NOT cover explicit <c>_change_data</c> (cdc) files or deletion vectors
/// (DV), and its tables carry NO partition columns. Nested type WIDENING (#546) across CDF versions is out of
/// scope here (only the create-time nested shape and its identity-immutability are exercised).</para>
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

        await AssertReadFailsClosedAsync(table, DeltaChangeFeedRange.FromVersion(1, 3));
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

        await AssertReadFailsClosedAsync(table, DeltaChangeFeedRange.FromVersion(1, 4));
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
    // AC4 · Boundary — id-mode array/map (#839) and nested-within-nested (#585) fail closed at load/read
    // ============================================================================================

    /// <summary>
    /// An id-mode CDF table declaring an <c>array</c> or <c>map</c> column (#839) is fail-closed: the load/read
    /// door rejects it with a typed exception rather than silently skipping the unmapped container. The dual is
    /// covered above — the SAME array/map shape is fully exercised under NAME mode.
    /// </summary>
    [Theory]
    [InlineData("array")]
    [InlineData("map")]
    public async Task IdMode_NestedArrayOrMap_CdfTable_FailsClosedAtLoadDoor_839(string kind)
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

        using NestedCdfTable table = NestedCdfTable.FromMapped(NewRoot(), ColumnMappingMode.Id, logical, MappedArrayOrMap(kind), maxColumnId: 2);
        // The raw metaData mints fine on disk (an id-mode array/map is a legal shape to WRITE into a log); the
        // #839 gate is ValidateColumnMappingSchema at the load choke point — the READ door is where it fails.
        await table.CreateRawMetadataAsync();
        await AssertLoadFailsClosedAsync(table, DeltaChangeFeedRange.FromVersion(0), "#839", "id mode");
    }

    /// <summary>
    /// A nested-within-nested CDF table (<c>array&lt;struct&gt;</c>, #585) is fail-closed at the load/read door
    /// under both name and id mode — never a silent skip of the interior struct.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedWithinNested_CdfTable_FailsClosedAtLoadDoor_585(bool idMode)
    {
        ColumnMappingMode mode = idMode ? ColumnMappingMode.Id : ColumnMappingMode.Name;
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
        await AssertLoadFailsClosedAsync(table, DeltaChangeFeedRange.FromVersion(0), "#585", "nested type within a nested type");
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

    // ------------------------------------------------------------------------------------------------
    // Fail-closed assertions
    // ------------------------------------------------------------------------------------------------

    // Fails closed at EITHER the load door (structurally-invalid tamper) OR the read enumeration (valid-but-
    // different identity). Asserts a TYPED exception either way — never a returned mis-mapped batch, and never
    // a stray NRE/InvalidOperation masquerading as fail-closed.
    private static async Task AssertReadFailsClosedAsync(NestedCdfTable table, DeltaChangeFeedRange range)
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
    }

    // Mirrors AssertReadFailsClosedAsync but drives the raw load+drain door directly (the boundary cells whose
    // metaData is only rejected downstream). Asserts the caught exception is one of the TYPED fail-closed set
    // (never ANY exception — a stray NRE would otherwise pass as "fail-closed"); optional discriminant
    // substrings additionally pin the message so a WRONG-but-thrown typed error cannot masquerade as the gate
    // under test (e.g. the #839/#585 cells pin their issue tag + defect phrase).
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

    // A whole-null struct row renders "struct=<null>"; a present struct renders its children, each null child as
    // "<null>" (distinct from a 0 value) — so a lost null cannot pass as an equal value.
    private static (long Id, string Sig) StructSig(long id, long? a, long? b, bool nullStruct) =>
        (id, nullStruct
            ? "struct=<null>"
            : string.Create(CultureInfo.InvariantCulture, $"struct(a={LongText(a)},b={LongText(b)})"));

    private static (long Id, string Sig) ArraySig(long id, params string[] elements) =>
        (id, "array[" + string.Join(",", elements) + "]");

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

    // Null-capable array builder: a null Tags[] renders a NULL list row (validity=true, zero span); a null
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

    // Null-capable map builder: a null Props[] renders a NULL map row (validity=true); a null entry value
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

    private NestedCdfTable NewArrayTable()
    {
        var logical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("tags", new ArrayType(DataTypes.StringType), nullable: true),
        });
        return new NestedCdfTable(NewRoot(), ColumnMappingMode.Name, logical, "cdf-nested-array");
    }

    private NestedCdfTable NewMapTable()
    {
        var logical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("props", new MapType(DataTypes.StringType, DataTypes.LongType, valueContainsNull: true), nullable: true),
        });
        return new NestedCdfTable(NewRoot(), ColumnMappingMode.Name, logical, "cdf-nested-map");
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
        private readonly StructType _logical;
        private readonly StructType _mapped;
        private readonly long _maxColumnId;
        private readonly LocalFileSystemBackend _backend;
        private StructType? _physical;
        private long _version = -1;
        private int _fileCounter;

        private NestedCdfTable(
            string root, ColumnMappingMode mode, StructType logical, StructType mapped, long maxColumnId)
        {
            _root = root;
            _mode = mode;
            _logical = logical;
            _mapped = mapped;
            _maxColumnId = maxColumnId;
            Directory.CreateDirectory(root);
            _backend = new LocalFileSystemBackend(root);
        }

        /// <summary>Mints a fresh mapping from a logical schema (the normal, writable path).</summary>
        public NestedCdfTable(string root, ColumnMappingMode mode, StructType logical, string seed)
            : this(root, mode, logical, Mint(logical, seed, out long max), max)
        {
        }

        /// <summary>Wraps an ALREADY-MAPPED (possibly deferred-shape) schema without minting — for the
        /// #839/#585 boundary tables whose shape <see cref="ColumnMapping.AssignFreshMapping"/> would reject.</summary>
        public static NestedCdfTable FromMapped(
            string root, ColumnMappingMode mode, StructType logical, StructType mapped, long maxColumnId) =>
            new(root, mode, logical, mapped, maxColumnId);

        private static StructType Mint(StructType logical, string seed, out long maxColumnId)
        {
            (StructType mapped, long max) = ColumnMapping.AssignFreshMapping(logical, new SeededPhysicalNameSource(seed));
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

        private static string ProtocolLine() =>
            "{\"protocol\":{\"minReaderVersion\":3,\"minWriterVersion\":7,"
            + "\"readerFeatures\":[\"columnMapping\"],"
            + "\"writerFeatures\":[\"columnMapping\",\"changeDataFeed\"]}}";

        private string MetadataLine(StructType mapped, long maxColumnId)
        {
            string modeName = _mode == ColumnMappingMode.Id ? "id" : "name";
            string schemaJson = System.Text.Json.JsonSerializer.Serialize(DeltaSchemaJson.ToJson(mapped));
            string config =
                "{\"delta.columnMapping.mode\":\"" + modeName + "\","
                + "\"delta.columnMapping.maxColumnId\":\""
                + maxColumnId.ToString(CultureInfo.InvariantCulture) + "\","
                + "\"delta.enableChangeDataFeed\":\"true\"}";
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
