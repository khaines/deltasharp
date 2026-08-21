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
/// </remarks>
[Collection(ColumnMappingTestCollection.Name)]
public sealed class ChangeFeedNestedColumnMappingTests : IDisposable
{
    private const string Scope = nameof(ChangeFeedNestedColumnMappingTests);

    private readonly ITestOutputHelper _output;
    private readonly List<NestedCdfTable> _tables = [];

    public ChangeFeedNestedColumnMappingTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (NestedCdfTable table in _tables)
        {
            table.Dispose();
        }
    }

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
        await table.AppendAsync(StructBatchNamed(table, renamed, (2, 1004, 2004))); // v3 (carries "renamed")

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
        await table.AppendAsync(StructBatchNamed(table, forged, (2, 1004, 2004))); // v3

        await AssertReadFailsClosedAsync(table, DeltaChangeFeedRange.FromVersion(1, 3));
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
                await RunTamperIterationAsync(mode, tamper);
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

    private static string Snippet(string message) =>
        message.Length <= 100 ? message : message[..100];

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

        using NestedCdfTable table = NewTable(
            NestedCdfTable.FromMapped(NewRoot(), ColumnMappingMode.Id, logical, MappedArrayOrMap(kind), maxColumnId: 2));
        // The raw metaData mints fine on disk (an id-mode array/map is a legal shape to WRITE into a log); the
        // #839 gate is ValidateColumnMappingSchema at the load choke point — the READ door is where it fails.
        await table.CreateRawMetadataAsync();
        await AssertLoadFailsClosedAsync(table, DeltaChangeFeedRange.FromVersion(1));
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

        using NestedCdfTable table = NewTable(
            NestedCdfTable.FromMapped(NewRoot(), mode, logical, MappedNestedWithinNested(), maxColumnId: 2));
        await table.CreateRawMetadataAsync();
        await AssertLoadFailsClosedAsync(table, DeltaChangeFeedRange.FromVersion(1));
    }

    // ------------------------------------------------------------------------------------------------
    // Tamper-fuzz plumbing
    // ------------------------------------------------------------------------------------------------

    private enum NestedTamper
    {
        SwapSiblingPhysicalNames,
        SwapSiblingFieldIds,
        ReassignChildPhysicalName,
        DropChildId,
        InjectNestedIds,
        InjectIdModeArray,
        InjectNestedWithinNested,
    }

    // Two tampers are structurally an id-mode-only defect (#839 needs id mode to fire); route them to id mode.
    private static ColumnMappingMode SelectMode(ColumnMappingMode[] modes, NestedTamper tamper, Random rng) =>
        tamper == NestedTamper.InjectIdModeArray ? ColumnMappingMode.Id : modes[rng.Next(modes.Length)];

    // Builds a struct history v0..v3 with a mid-history metaData at v2, whose schema is `MutateForTamper`d,
    // then reads [1,3] through the production door. The caller asserts the read throws a typed exception.
    private async Task RunTamperIterationAsync(ColumnMappingMode mode, NestedTamper tamper)
    {
        using NestedCdfTable table = NewStructTable(mode);
        await table.CreateAsync();                                            // v0 clean
        await table.AppendAsync(StructBatch(table, (1, 1001, 2001)));         // v1
        await table.AppendAsync(StructBatch(table, (2, 1002, 2002)));         // v2 append (clean data file)
        StructType tampered = MutateForTamper(table.MappedSchema, tamper);
        long maxColumnId = TamperMaxColumnId(table.MaxColumnId, tamper);
        await table.CommitMetadataAsync(tampered, maxColumnId);              // v3: forged metaData

        (_, _) = await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, table.LatestVersion), DecodeStruct);
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

    private static long TamperMaxColumnId(long baseMax, NestedTamper tamper) => tamper switch
    {
        // The injected shapes are wholly new 2-column schemas (id + container), maxColumnId == 2.
        NestedTamper.InjectIdModeArray or NestedTamper.InjectNestedWithinNested => 2,
        _ => baseMax,
    };

    private StructType MutateForTamper(StructType mapped, NestedTamper tamper) => tamper switch
    {
        NestedTamper.SwapSiblingPhysicalNames => SwapStructChildPhysicalNames(mapped, "pt", "a", "b"),
        NestedTamper.SwapSiblingFieldIds => SwapStructChildFieldIds(mapped, "pt", "a", "b"),
        NestedTamper.ReassignChildPhysicalName => ReassignStructChildPhysicalName(mapped, "pt", "a", "col-forged-a"),
        NestedTamper.DropChildId => DropStructChildId(mapped, "pt", "a"),
        NestedTamper.InjectNestedIds => InjectNestedIdsKey(mapped, "pt"),
        NestedTamper.InjectIdModeArray => InjectedArrayMapped(),
        NestedTamper.InjectNestedWithinNested => InjectedNestedWithinNestedMapped(),
        _ => throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null),
    };

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

    private static StructType DropStructChildId(StructType mapped, string container, string child) =>
        MapContainerChildren(mapped, container, children =>
            children.Select(c => c.Name == child
                ? c.WithMetadata(RemoveMeta(c.Metadata, ColumnMapping.IdKey))
                : c).ToArray());

    private static StructType InjectNestedIdsKey(StructType mapped, string container) =>
        new(mapped.Select(f => f.Name == container
            ? f.WithMetadata(SetMeta(f.Metadata, ColumnMapping.NestedIdsKey, MetadataValue.String("1")))
            : f).ToList());

    // A wholly-new id-mode array MAPPED schema (id:long + tags:array<string>) — #839 (array/map under id mode).
    // Hand-authored (NOT AssignFreshMapping, which does not gate #839) — the forged metaData a poisoned log
    // carries; the #839 gate is ValidateColumnMappingSchema at the load/read door.
    private static StructType InjectedArrayMapped() => MappedArrayOrMap("array");

    // A wholly-new nested-within-nested MAPPED schema (id:long + items:array<struct<x>>) — #585. Hand-authored
    // because AssignFreshMapping REJECTS #585 during minting; a forged log can still carry this shape, and the
    // load/read door must fail closed on it.
    private static StructType InjectedNestedWithinNestedMapped() => MappedNestedWithinNested();

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

    private static FieldMetadata RemoveMeta(FieldMetadata metadata, string key)
    {
        var entries = metadata.Where(kv => kv.Key != key).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return FieldMetadata.FromValues(entries);
    }

    // Hand-authored MAPPED (id + physicalName) fields — the exact shape a poisoned/foreign metaData carries.
    private static FieldMetadata MappingMeta(long id, string physical) =>
        FieldMetadata.FromValues(new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            [ColumnMapping.IdKey] = MetadataValue.Long(id),
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
    // different identity). Asserts a typed exception either way — never a returned mis-mapped batch.
    private static async Task AssertReadFailsClosedAsync(NestedCdfTable table, DeltaChangeFeedRange range)
    {
        bool threw = false;
        try
        {
            await table.ReadRangeAsync(range, DecodeStruct);
        }
        catch (Exception ex) when (ex is DeltaReadException or DeltaProtocolException or DeltaStorageException)
        {
            threw = true;
        }

        Assert.True(threw, "the CDF read must fail closed with a typed exception");
    }

    private static async Task AssertLoadFailsClosedAsync(NestedCdfTable table, DeltaChangeFeedRange range)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(table.Root);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            try
            {
                DeltaChangeFeedInfo info = await source.LoadChangeFeedAsync(range);
                await foreach (var _ in source.ReadChangeBatchesAsync(info))
                {
                    // drain — a silent skip would return rows and never throw.
                }
            }
            catch (Exception ex) when (ex is DeltaReadException or DeltaProtocolException or DeltaStorageException)
            {
                throw;
            }
        });
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
        long a = pt.Child(0).GetValue<long>(c.Row);
        long b = pt.Child(1).GetValue<long>(c.Row);
        // Assert the reconciled output surfaces the second child under its END logical name (physical→logical).
        Assert.Equal(secondChild, ((StructType)FindField(c.Schema, "pt").DataType)[1].Name);
        return new ActualChange(c.Version, c.ChangeType, c.Id, StructSig(c.Id, a, b).Sig);
    }

    private static ActualChange DecodeArray(ChangeRowCursor c)
    {
        var tags = (ListColumnVector)c.Batch.Column(c.Schema.IndexOf("tags"));
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
        ColumnVector keys = props.KeysAt(c.Row);
        ColumnVector vals = props.ValuesAt(c.Row);
        var entries = new List<(string, long)>();
        for (int e = 0; e < keys.Length; e++)
        {
            entries.Add((Encoding.UTF8.GetString(keys.GetBytes(e)), vals.GetValue<long>(e)));
        }

        return new ActualChange(c.Version, c.ChangeType, c.Id, MapSig(c.Id, entries.ToArray()).Sig);
    }

    // ---- signatures (canonical, order-preserving string encodings of nested content) ----

    private static (long Id, string Sig) StructSig(long id, long a, long b) =>
        (id, string.Create(CultureInfo.InvariantCulture, $"struct(a={a},b={b})"));

    private static (long Id, string Sig) ArraySig(long id, params string[] elements) =>
        (id, "array[" + string.Join(",", elements) + "]");

    private static (long Id, string Sig) MapSig(long id, params (string Key, long Value)[] entries) =>
        (id, "map{" + string.Join(",", entries.Select(e =>
            string.Create(CultureInfo.InvariantCulture, $"{e.Key}={e.Value}"))) + "}");

    // ------------------------------------------------------------------------------------------------
    // Batch builders (logical-named nested vectors)
    // ------------------------------------------------------------------------------------------------

    private static ColumnBatch StructBatch(NestedCdfTable table, params (long Id, long A, long B)[] rows) =>
        StructBatchNamed(table, table.MappedSchema, rows);

    // Builds a {id, pt:struct<a,b>} logical batch. The struct child NAMES follow `mappedSchema` so a
    // post-rename append carries the renamed child (the writer cross-checks against the physical schema).
    private static ColumnBatch StructBatchNamed(
        NestedCdfTable table, StructType mappedSchema, params (long Id, long A, long B)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector a = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector b = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        foreach ((long rid, long ra, long rb) in rows)
        {
            id.AppendValue(rid);
            a.AppendValue(ra);
            b.AppendValue(rb);
        }

        // Logical struct type with the CURRENT logical child names (from the table's logical schema).
        var ptType = (StructType)table.LogicalSchema["pt"].DataType;
        var pt = new StructColumnVector(ptType, new ColumnVector[] { a, b }, new bool[rows.Length]);
        return new ManagedColumnBatch(table.LogicalSchema, new ColumnVector[] { id, pt }, rows.Length);
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
        return NewTable(new NestedCdfTable(NewRoot(), mode, logical, "cdf-nested-struct"));
    }

    private NestedCdfTable NewArrayTable()
    {
        var logical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("tags", new ArrayType(DataTypes.StringType), nullable: true),
        });
        return NewTable(new NestedCdfTable(NewRoot(), ColumnMappingMode.Name, logical, "cdf-nested-array"));
    }

    private NestedCdfTable NewMapTable()
    {
        var logical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("props", new MapType(DataTypes.StringType, DataTypes.LongType), nullable: true),
        });
        return NewTable(new NestedCdfTable(NewRoot(), ColumnMappingMode.Name, logical, "cdf-nested-map"));
    }

    private NestedCdfTable NewTable(NestedCdfTable table)
    {
        _tables.Add(table);
        return table;
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
