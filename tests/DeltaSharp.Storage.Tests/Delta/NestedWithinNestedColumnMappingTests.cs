using System.Globalization;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// The §3 oracle for NESTED-WITHIN-NESTED (depth&gt;1) column mapping, NAME-mode half (#866 866a): a real
/// write→read→resolve round trip over the depth&gt;1 shape matrix through the production writer/reader doors,
/// the structural-location presence cells (added-after-write null-fill AT DEPTH, all-children-replaced retains
/// array lengths, the INV-PARITY null-filled-sibling-under-a-repeated-ancestor cell), and the retained
/// ID-mode depth&gt;1 fail-closed cells (create/evolve/validate/write, all still rejecting #866 until 866b).
/// </summary>
/// <remarks>
/// Every same-typed sibling draws its values from a DISJOINT domain (design §3 preamble) so a positional
/// mis-bind cannot pass on equal values. Physical names are minted by a deterministic seeded source.
/// </remarks>
[Collection(ColumnMappingTestCollection.Name)]
public sealed class NestedWithinNestedColumnMappingTests : IDisposable
{
    private const string Seed = "nwn-column-mapping-866a";
    private readonly string _root;

    public NestedWithinNestedColumnMappingTests() =>
        _root = Path.Combine(Path.GetTempPath(), "nwncm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }

        GC.SuppressFinalize(this);
    }

    // ---- §3.4/§3.5 · name-mode depth>1 round-trip identity + read-exit type equality + maxColumnId ----

    [Fact]
    public async Task NameMode_ArrayOfStruct_RoundTrip_TypeEquals_MaxColumnIdCountsDeepStructFields()
    {
        // { id:long, items:array<struct<a:long, b:string>> } — array<struct> is §1's most common shape.
        // maxColumnId counts StructFields at every depth: id(1), items(2), items.element.a(3), .b(4) — the
        // array element GROUP is not a StructField (C1), so no extra id.
        var elem = new StructType(new[]
        {
            new StructField("a", DataTypes.LongType, nullable: true),
            new StructField("b", DataTypes.StringType, nullable: true),
        });
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("items", new ArrayType(elem), nullable: true),
        });

        // Disjoint domains: id ∈ [1..], a ∈ [1000..], b = strings. A null row, an EMPTY row, a multi-element row.
        ListColumnVector items = ArrayOfStruct(
            (ArrayType)schema["items"].DataType,
            new[]
            {
                new (long?, string?)[] { (1001L, "x"), (1002L, "y") }, // multi-element
                Array.Empty<(long?, string?)>(),                       // empty array
                null,                                                  // null array
            });
        var batch = new ManagedColumnBatch(
            schema, new ColumnVector[] { Long(1L, 2L, 3L), items }, 3);

        long maxColumnId = await WriteNameMappedAsync(schema, batch);
        Assert.Equal(4L, maxColumnId);

        ColumnBatch read = await ReadSingleBatchAsync();

        // Read-exit type equality (R6 inverse relabel): the column carries the LOGICAL type exactly.
        Assert.Equal(schema["items"].DataType.SimpleString, read.Column(1).Type.SimpleString);

        var readItems = (ListColumnVector)read.Column(1);
        Assert.Equal(2, readItems.ElementLength(0));
        Assert.Equal(0, readItems.ElementLength(1));
        Assert.True(readItems.IsNull(2));

        var e0 = (StructColumnVector)readItems.ElementsAt(0);
        Assert.Equal(1001L, e0.Child("a").GetValue<long>(0));
        Assert.Equal("x", Encoding.UTF8.GetString(e0.Child("b").GetBytes(0)));
        Assert.Equal(1002L, e0.Child("a").GetValue<long>(1));
        Assert.Equal("y", Encoding.UTF8.GetString(e0.Child("b").GetBytes(1)));
    }

    [Fact]
    public async Task NameMode_StructOfStruct_Depth3_RoundTrip_MaxColumnIdCountsEveryStructField()
    {
        // { addr: struct<geo: struct<lat:long, lng:long>> } — struct-in-struct, depth 3.
        // maxColumnId: addr(1), addr.geo(2), lat(3), lng(4).
        var geoStruct = new StructType(new[]
        {
            new StructField("lat", DataTypes.LongType, nullable: true),
            new StructField("lng", DataTypes.LongType, nullable: true),
        });
        var addrStruct = new StructType(new[] { new StructField("geo", geoStruct, nullable: true) });
        var schema = new StructType(new[] { new StructField("addr", addrStruct, nullable: true) });

        // lat ∈ [47..], lng ∈ [-122..] disjoint domains.
        var geoVec = new StructColumnVector(
            geoStruct, new ColumnVector[] { Long(47L, 48L), Long(-122L, -123L) }, new[] { false, false });
        var addrVec = new StructColumnVector(addrStruct, new ColumnVector[] { geoVec }, new[] { false, false });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { addrVec }, 2);

        long maxColumnId = await WriteNameMappedAsync(schema, batch);
        Assert.Equal(4L, maxColumnId);

        ColumnBatch read = await ReadSingleBatchAsync();
        Assert.Equal(schema[0].DataType.SimpleString, read.Column(0).Type.SimpleString);
        var rAddr = (StructColumnVector)read.Column(0);
        var rGeo = (StructColumnVector)rAddr.Child("geo");
        Assert.Equal(47L, rGeo.Child("lat").GetValue<long>(0));
        Assert.Equal(-122L, rGeo.Child("lng").GetValue<long>(0));
        Assert.Equal(48L, rGeo.Child("lat").GetValue<long>(1));
        Assert.Equal(-123L, rGeo.Child("lng").GetValue<long>(1));
    }

    [Fact]
    public async Task NameMode_MapOfStruct_RoundTrip_MaxColumnId()
    {
        // { m: map<string, struct<v:long>> } — struct inside a map value. maxColumnId: m(1), m.value.v(2).
        var valStruct = new StructType(new[] { new StructField("v", DataTypes.LongType, nullable: true) });
        var schema = new StructType(new[]
        {
            new StructField("m", new MapType(DataTypes.StringType, valStruct), nullable: true),
        });

        MapColumnVector m = MapOfStruct(
            (MapType)schema["m"].DataType,
            new[]
            {
                new[] { ("w", (long?)7000L), ("h", 8000L) },
            });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { m }, 1);

        long maxColumnId = await WriteNameMappedAsync(schema, batch);
        Assert.Equal(2L, maxColumnId);

        ColumnBatch read = await ReadSingleBatchAsync();
        Assert.Equal(schema[0].DataType.SimpleString, read.Column(0).Type.SimpleString);
        var rm = (MapColumnVector)read.Column(0);
        Assert.Equal("w", Encoding.UTF8.GetString(rm.KeysAt(0).GetBytes(0)));
        Assert.Equal(7000L, ((StructColumnVector)rm.ValuesAt(0)).Child("v").GetValue<long>(0));
        Assert.Equal("h", Encoding.UTF8.GetString(rm.KeysAt(0).GetBytes(1)));
        Assert.Equal(8000L, ((StructColumnVector)rm.ValuesAt(0)).Child("v").GetValue<long>(1));
    }

    [Fact]
    public async Task NameMode_ArrayOfArray_RoundTrip_MaxColumnIdCountsContainerOnly()
    {
        // { aa: array<array<long>> } — array of array, no intervening struct. maxColumnId: aa(1) only (no
        // interior StructField in name mode).
        var schema = new StructType(new[]
        {
            new StructField("aa", new ArrayType(new ArrayType(DataTypes.LongType)), nullable: true),
        });

        ListColumnVector aa = ArrayOfLongArray(
            (ArrayType)schema["aa"].DataType,
            new[]
            {
                new long[]?[] { new long[] { 10L, 20L }, Array.Empty<long>(), new long[] { 30L } },
                null,
            });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { aa }, 2);

        long maxColumnId = await WriteNameMappedAsync(schema, batch);
        Assert.Equal(1L, maxColumnId);

        ColumnBatch read = await ReadSingleBatchAsync();
        Assert.Equal(schema[0].DataType.SimpleString, read.Column(0).Type.SimpleString);
        var rAa = (ListColumnVector)read.Column(0);
        Assert.Equal(3, rAa.ElementLength(0));
        Assert.True(rAa.IsNull(1));
        var inner = (ListColumnVector)rAa.ElementsAt(0);
        Assert.Equal(10L, inner.ElementsAt(0).GetValue<long>(0));
        Assert.Equal(20L, inner.ElementsAt(0).GetValue<long>(1));
        Assert.Equal(0, inner.ElementLength(1));
        Assert.Equal(30L, inner.ElementsAt(2).GetValue<long>(0));
    }

    [Fact]
    public async Task NameMode_Depth3_ArrayStructStruct_PresentNestedStruct_RoundTrip()
    {
        // { rows: array<struct<a:long, b:struct<c:long>>> } — the depth-3 present-nested-struct shape (M4
        // positive case in NAME mode). maxColumnId: rows(1), a(2), b(3), c(4). Disjoint domains a∈[100..],
        // c∈[900..].
        var inner = new StructType(new[] { new StructField("c", DataTypes.LongType, nullable: true) });
        var elem = new StructType(new[]
        {
            new StructField("a", DataTypes.LongType, nullable: true),
            new StructField("b", inner, nullable: true),
        });
        var schema = new StructType(new[] { new StructField("rows", new ArrayType(elem), nullable: true) });

        ListColumnVector rows = ArrayOfStructWithNestedStruct(
            (ArrayType)schema["rows"].DataType,
            new[]
            {
                new (long?, long?)[] { (100L, 900L), (101L, 901L) },
            });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { rows }, 1);

        long maxColumnId = await WriteNameMappedAsync(schema, batch);
        Assert.Equal(4L, maxColumnId);

        ColumnBatch read = await ReadSingleBatchAsync();
        Assert.Equal(schema[0].DataType.SimpleString, read.Column(0).Type.SimpleString);
        var rRows = (ListColumnVector)read.Column(0);
        var e = (StructColumnVector)rRows.ElementsAt(0);
        Assert.Equal(100L, e.Child("a").GetValue<long>(0));
        Assert.Equal(900L, ((StructColumnVector)e.Child("b")).Child("c").GetValue<long>(0));
        Assert.Equal(101L, e.Child("a").GetValue<long>(1));
        Assert.Equal(901L, ((StructColumnVector)e.Child("b")).Child("c").GetValue<long>(1));
    }

    [Fact]
    public async Task NameMode_StructOfArray_RoundTrip_MaxColumnId()
    {
        // { data: struct<vals: array<long>, tag: string> } — a STRUCT child that is a CONTAINER (array). This
        // exercises the struct-arm → array-arm name-mode recursion. maxColumnId: data(1), vals(2), tag(3).
        var dataStruct = new StructType(new[]
        {
            new StructField("vals", new ArrayType(DataTypes.LongType), nullable: true),
            new StructField("tag", DataTypes.StringType, nullable: true),
        });
        var schema = new StructType(new[] { new StructField("data", dataStruct, nullable: true) });

        ListColumnVector vals = ArrayOfScalarLong(
            new ArrayType(DataTypes.LongType), new[] { new long?[] { 5L, 6L, 7L }, Array.Empty<long?>() });
        var dataVec = new StructColumnVector(
            dataStruct, new ColumnVector[] { vals, Str("alpha", "beta") }, new[] { false, false });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { dataVec }, 2);

        long maxColumnId = await WriteNameMappedAsync(schema, batch);
        Assert.Equal(3L, maxColumnId);

        ColumnBatch read = await ReadSingleBatchAsync();
        Assert.Equal(schema[0].DataType.SimpleString, read.Column(0).Type.SimpleString);
        var rData = (StructColumnVector)read.Column(0);
        var rVals = (ListColumnVector)rData.Child("vals");
        Assert.Equal(3, rVals.ElementLength(0));
        Assert.Equal(5L, rVals.ElementsAt(0).GetValue<long>(0));
        Assert.Equal(7L, rVals.ElementsAt(0).GetValue<long>(2));
        Assert.Equal(0, rVals.ElementLength(1));
        Assert.Equal("alpha", Encoding.UTF8.GetString(rData.Child("tag").GetBytes(0)));
        Assert.Equal("beta", Encoding.UTF8.GetString(rData.Child("tag").GetBytes(1)));
    }

    // ---- §3.8d/§3.8l/§3.8r · structural-location presence cells (NAME mode, #857 at depth) ----

    [Fact]
    public async Task NameMode_ReadOldFileAfterDepth2Add_NullFillsAbsentNullableChild()
    {
        // Write array<struct<a:long>>, then commit a metaData whose mapped schema adds a NULLABLE b:string at
        // depth 2 (fresh physicalName not in the old file). Reading the OLD file: the array structure/lengths
        // are read (structural location present), a is intact, and b null-fills per element (#857 AT DEPTH).
        var oldElem = new StructType(new[] { new StructField("a", DataTypes.LongType, nullable: true) });
        var oldSchema = new StructType(new[] { new StructField("items", new ArrayType(oldElem), nullable: true) });

        ListColumnVector oldItems = ArrayOfSingleLong(
            (ArrayType)oldSchema["items"].DataType,
            new[]
            {
                new long?[] { 1L, 2L },   // multi-element
                Array.Empty<long?>(),     // empty
                new long?[] { 3L },       // single
            });
        var oldBatch = new ManagedColumnBatch(oldSchema, new ColumnVector[] { oldItems }, 3);

        (StructType oldMapped, long oldMax) = await WriteNameMappedFileAndV0Async(oldSchema, oldBatch);

        // Evolve: add a nullable b at depth 2, minting only the new leaf.
        var newElem = new StructType(new[]
        {
            new StructField("a", DataTypes.LongType, nullable: true),
            new StructField("b", DataTypes.StringType, nullable: true),
        });
        var newSchema = new StructType(new[] { new StructField("items", new ArrayType(newElem), nullable: true) });
        (StructType newMapped, ImmutableConfig cfg) = EvolveName(newSchema, oldMapped, oldMax);
        Assert.Equal(oldMax + 1, cfg.MaxColumnId); // only b minted
        await CommitMetadataAsync(newMapped, cfg.MaxColumnId, version: 1);

        ColumnBatch read = await ReadSingleBatchAsync();
        Assert.Equal(newSchema[0].DataType.SimpleString, read.Column(0).Type.SimpleString);
        var rItems = (ListColumnVector)read.Column(0);

        // Array lengths preserved (structural location present).
        Assert.Equal(2, rItems.ElementLength(0));
        Assert.Equal(0, rItems.ElementLength(1));
        Assert.Equal(1, rItems.ElementLength(2));

        var e0 = (StructColumnVector)rItems.ElementsAt(0);
        Assert.Equal(1L, e0.Child("a").GetValue<long>(0));
        Assert.Equal(2L, e0.Child("a").GetValue<long>(1));
        Assert.True(e0.Child("b").IsNull(0)); // b null-filled per element
        Assert.True(e0.Child("b").IsNull(1));
        var e2 = (StructColumnVector)rItems.ElementsAt(2);
        Assert.Equal(3L, e2.Child("a").GetValue<long>(0));
        Assert.True(e2.Child("b").IsNull(0));
    }

    [Fact]
    public async Task NameMode_AllChildrenReplaced_RetainsArrayLengths_M5()
    {
        // M5 (NAME mode): write array<struct<a:long>>, then commit a metaData whose mapped schema requests
        // ONLY a fresh b:long (a dropped, b added — its physicalName is NOT in the old file). Reading the OLD
        // file: the array container is structurally located (per-row lengths read from the file), and b is
        // null-filled per element — NOT a null array. Includes an empty row and a multi-element row.
        var oldElem = new StructType(new[] { new StructField("a", DataTypes.LongType, nullable: true) });
        var oldSchema = new StructType(new[] { new StructField("items", new ArrayType(oldElem), nullable: true) });
        ListColumnVector oldItems = ArrayOfSingleLong(
            (ArrayType)oldSchema["items"].DataType,
            new[] { new long?[] { 5L, 6L }, Array.Empty<long?>(), new long?[] { 7L } });
        (StructType oldMapped, long oldMax) =
            await WriteNameMappedFileAndV0Async(oldSchema, new ManagedColumnBatch(oldSchema, new ColumnVector[] { oldItems }, 3));

        // Build a v1 mapped schema: items->element struct carries ONLY b (a fresh physicalName), a is dropped.
        StructType newMapped = ReplaceArrayElementStructChildWithFreshLeaf(oldMapped, "items", "b", oldMax + 1);
        await CommitMetadataAsync(newMapped, oldMax + 1, version: 1);

        ColumnBatch read = await ReadSingleBatchAsync();
        var rItems = (ListColumnVector)read.Column(0);
        Assert.False(rItems.IsNull(0)); // NOT a null array — structure retained
        Assert.Equal(2, rItems.ElementLength(0));
        Assert.Equal(0, rItems.ElementLength(1));
        Assert.Equal(1, rItems.ElementLength(2));
        var e0 = (StructColumnVector)rItems.ElementsAt(0);
        Assert.True(e0.Child("b").IsNull(0)); // b null-filled per element
        Assert.True(e0.Child("b").IsNull(1));
    }

    [Fact]
    public async Task NameMode_NullFilledSibling_UnderRepeatedAncestor_ParityHolds()
    {
        // INV-PARITY (NAME-mode analogue of §3.8r): array<struct<present:long, absent:long>> where `absent` is
        // added-after-write. The null-filled sibling under the repeated (array) ancestor keeps per-row parity
        // with the present sibling — incl. an EMPTY row and a MULTI-element row.
        var oldElem = new StructType(new[] { new StructField("present", DataTypes.LongType, nullable: true) });
        var oldSchema = new StructType(new[] { new StructField("rows", new ArrayType(oldElem), nullable: true) });
        ListColumnVector oldRows = ArrayOfSingleLong(
            (ArrayType)oldSchema["rows"].DataType,
            new[] { new long?[] { 11L, 12L }, Array.Empty<long?>(), new long?[] { 13L } });
        (StructType oldMapped, long oldMax) =
            await WriteNameMappedFileAndV0Async(oldSchema, new ManagedColumnBatch(oldSchema, new ColumnVector[] { oldRows }, 3));

        var newElem = new StructType(new[]
        {
            new StructField("present", DataTypes.LongType, nullable: true),
            new StructField("absent", DataTypes.LongType, nullable: true),
        });
        var newSchema = new StructType(new[] { new StructField("rows", new ArrayType(newElem), nullable: true) });
        (StructType newMapped, ImmutableConfig cfg) = EvolveName(newSchema, oldMapped, oldMax);
        await CommitMetadataAsync(newMapped, cfg.MaxColumnId, version: 1);

        ColumnBatch read = await ReadSingleBatchAsync();
        var rRows = (ListColumnVector)read.Column(0);
        Assert.Equal(2, rRows.ElementLength(0));
        Assert.Equal(0, rRows.ElementLength(1));
        Assert.Equal(1, rRows.ElementLength(2));
        var e0 = (StructColumnVector)rRows.ElementsAt(0);
        Assert.Equal(11L, e0.Child("present").GetValue<long>(0));
        Assert.Equal(12L, e0.Child("present").GetValue<long>(1));
        Assert.True(e0.Child("absent").IsNull(0));
        Assert.True(e0.Child("absent").IsNull(1));
        var e2 = (StructColumnVector)rRows.ElementsAt(2);
        Assert.Equal(13L, e2.Child("present").GetValue<long>(0));
        Assert.True(e2.Child("absent").IsNull(0));
    }

    // ---- id-mode depth>1 is now LIFTED (866b): create / evolve / write SUCCEED (see the §3.8 cells) ----

    [Theory]
    [InlineData("array<struct>")]
    [InlineData("struct<struct>")]
    [InlineData("map<string,struct>")]
    [InlineData("array<array>")]
    public void IdMode_Depth2_Create_Succeeds_866b(string shape)
    {
        StructType logical = new(new[] { new StructField("payload", NwnType(shape), nullable: true) });
        (StructType mapped, long max) = ColumnMapping.AssignFreshMapping(
            logical, new SeededPhysicalNameSource("id-nwn"), ColumnMappingMode.Id);

        // The container gets an id + physicalName; depth>1 leaves/interiors advance maxColumnId past 1.
        Assert.True(max >= 2);
        Assert.True(ColumnMapping.TryGetId(mapped[0], out long payloadId));
        Assert.Equal(1L, payloadId);

        // The committed schema validates round-trip under id mode (no #866 reject).
        ColumnMapping.ValidateColumnMappingSchema(ColumnMappingMode.Id, mapped, ColumnMapping.IdModeConfiguration(max));
    }

    [Fact]
    public void IdMode_Depth2_Evolve_Succeeds_866b()
    {
        (StructType current, long max) = ColumnMapping.AssignFreshMapping(
            new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) }),
            new SeededPhysicalNameSource("id-base"), ColumnMappingMode.Id);
        var evolved = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("payload", NwnType("array<struct>"), nullable: true),
        });
        (StructType mappedEvolved, var config) = ColumnMapping.EvolveNameModeMapping(
            evolved, current, ColumnMapping.IdModeConfiguration(max),
            new SeededPhysicalNameSource("id-evolve"), ColumnMappingMode.Id);

        Assert.Equal(2, mappedEvolved.Count);
        ColumnMapping.ValidateColumnMappingSchema(ColumnMappingMode.Id, mappedEvolved, config);
    }

    [Fact]
    public void IdMode_Depth2_Write_Succeeds_866b()
    {
        // ToPhysicalSchema (the write door) now relabels an id-mode depth>1 schema (866b).
        (StructType mapped, _) = ColumnMapping.AssignFreshMapping(
            new StructType(new[] { new StructField("payload", NwnType("array<struct>"), nullable: true) }),
            new SeededPhysicalNameSource("id-write"), ColumnMappingMode.Id);
        StructType physical = ColumnMapping.ToPhysicalSchema(mapped, ColumnMappingMode.Id);
        Assert.Single(physical);
        Assert.IsType<ArrayType>(physical[0].DataType);
    }

    // ==================================================================================================
    // §3.8a–§3.8s · ID-MODE depth>1 write→read→resolve round trips (866b) — the critical-correctness cells
    // ==================================================================================================

    // §3.8a — array<struct<a,b>> id-mode round trip: children bind by their DIRECT StructField id (the element
    // GROUP id is structural-only), NOT positionally. Disjoint domains (a∈[1000..], b=strings) pin id-binding.
    [Fact]
    public async Task IdMode_ArrayOfStruct_RoundTrip_ResolvesChildrenByDirectId()
    {
        var elem = new StructType(new[]
        {
            new StructField("a", DataTypes.LongType, nullable: true),
            new StructField("b", DataTypes.StringType, nullable: true),
        });
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("items", new ArrayType(elem), nullable: true),
        });
        ListColumnVector items = ArrayOfStruct(
            (ArrayType)schema["items"].DataType,
            new[]
            {
                new (long?, string?)[] { (1001L, "x"), (1002L, "y") },
                Array.Empty<(long?, string?)>(),
                null,
            });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { Long(1L, 2L, 3L), items }, 3);

        (_, long max) = await WriteIdMappedFileAndV0Async(schema, batch);
        Assert.Equal(5L, max); // id(1), items(2), element-group ⌂(3), a(4), b(5)

        ColumnBatch read = await ReadSingleBatchAsync();
        var readItems = (ListColumnVector)read.Column(1);
        Assert.Equal(2, readItems.ElementLength(0));
        Assert.Equal(0, readItems.ElementLength(1));
        Assert.True(readItems.IsNull(2));
        var e0 = (StructColumnVector)readItems.ElementsAt(0);
        Assert.Equal(1001L, e0.Child("a").GetValue<long>(0));
        Assert.Equal("x", Encoding.UTF8.GetString(e0.Child("b").GetBytes(0)));
        Assert.Equal(1002L, e0.Child("a").GetValue<long>(1));
        Assert.Equal("y", Encoding.UTF8.GetString(e0.Child("b").GetBytes(1)));
    }

    // §3.8h (M4) — array<struct<a, b:struct<c>>>: the PRESENT nested struct `b` is bound STRUCTURALLY (its
    // group id is expected-absent and must NOT trigger null-fill) and recursed; a by id, b.c by id. Witness
    // that a naive own-id-absence null-fill (which would DROP present b.c) is not taken.
    [Fact]
    public async Task IdMode_Depth3_ArrayStructStruct_PresentNestedStruct_ReadsCorrectly()
    {
        var inner = new StructType(new[] { new StructField("c", DataTypes.LongType, nullable: true) });
        var elem = new StructType(new[]
        {
            new StructField("a", DataTypes.LongType, nullable: true),
            new StructField("b", inner, nullable: true),
        });
        var schema = new StructType(new[] { new StructField("rows", new ArrayType(elem), nullable: true) });

        ListColumnVector rows = ArrayStructAB(
            (ArrayType)schema["rows"].DataType,
            new[]
            {
                new (long?, long?)[] { (10L, 900L), (11L, 901L) }, // a∈[10..], c∈[900..] disjoint
                new (long?, long?)[] { (12L, 902L) },
            });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { rows }, 2);

        await WriteIdMappedFileAndV0Async(schema, batch);

        ColumnBatch read = await ReadSingleBatchAsync();
        var rItems = (ListColumnVector)read.Column(0);
        var e0 = (StructColumnVector)rItems.ElementsAt(0);
        Assert.Equal(10L, e0.Child("a").GetValue<long>(0));
        var b0 = (StructColumnVector)e0.Child("b");
        Assert.Equal(900L, b0.Child("c").GetValue<long>(0)); // present nested struct NOT dropped
        Assert.Equal(901L, b0.Child("c").GetValue<long>(1));
    }

    // §3.8c — read an OLD id-mode file after adding a NULLABLE child: the added child NULL-FILLS (not
    // SchemaMismatch/unreadable). a values intact.
    [Fact]
    public async Task IdMode_ReadOldFileAfterDepth2Add_NullFillsAbsentNullableChild()
    {
        var oldElem = new StructType(new[] { new StructField("a", DataTypes.LongType, nullable: true) });
        var oldSchema = new StructType(new[] { new StructField("items", new ArrayType(oldElem), nullable: true) });
        ListColumnVector oldItems = ArrayOfSingleLong(
            (ArrayType)oldSchema["items"].DataType,
            new long?[]?[] { new long?[] { 100L, 101L }, new long?[] { 102L } });
        (StructType oldMapped, long oldMax) =
            await WriteIdMappedFileAndV0Async(oldSchema, new ManagedColumnBatch(oldSchema, new ColumnVector[] { oldItems }, 2));

        var newElem = new StructType(new[]
        {
            new StructField("a", DataTypes.LongType, nullable: true),
            new StructField("b", DataTypes.LongType, nullable: true),
        });
        var newLogical = new StructType(new[] { new StructField("items", new ArrayType(newElem), nullable: true) });
        (StructType newMapped, ImmutableConfig cfg) = EvolveId(newLogical, oldMapped, oldMax);
        await CommitMetadataIdAsync(newMapped, cfg.MaxColumnId, version: 1);

        ColumnBatch read = await ReadSingleBatchAsync();
        var rItems = (ListColumnVector)read.Column(0);
        var e0 = (StructColumnVector)rItems.ElementsAt(0);
        Assert.Equal(100L, e0.Child("a").GetValue<long>(0)); // a intact
        Assert.True(e0.Child("b").IsNull(0));                // b null-filled, NOT unreadable
        Assert.True(e0.Child("b").IsNull(1));
    }

    // §3.8l (M5) — array<struct<a>> evolved to array<struct<b>> (drop a, add b): the OLD file physically holds
    // the array with NON-TRIVIAL per-row lengths (empty, multi, single). The array container is located by its
    // stable physicalName (present), so its per-row LENGTHS are read from the file and b is null-filled per
    // element — reads back the SAME lengths, a dropped. Asserts array lengths preserved, NOT a null array.
    [Fact]
    public async Task IdMode_AllChildrenReplaced_RetainsArrayLengths_M5()
    {
        var oldElem = new StructType(new[] { new StructField("a", DataTypes.LongType, nullable: true) });
        var oldSchema = new StructType(new[] { new StructField("items", new ArrayType(oldElem), nullable: true) });
        ListColumnVector oldItems = ArrayOfSingleLong(
            (ArrayType)oldSchema["items"].DataType,
            new long?[]?[] { Array.Empty<long?>(), new long?[] { 100L, 101L }, new long?[] { 102L } });
        (StructType oldMapped, long oldMax) =
            await WriteIdMappedFileAndV0Async(oldSchema, new ManagedColumnBatch(oldSchema, new ColumnVector[] { oldItems }, 3));

        // Rebuild the mapped schema replacing the element child with a fresh nullable `b` (drop `a`, add `b`).
        StructType newMapped = ReplaceArrayElementChild(oldMapped, "items", "b", oldMax + 1);
        await CommitMetadataIdAsync(newMapped, oldMax + 1, version: 1);

        ColumnBatch read = await ReadSingleBatchAsync();
        var rItems = (ListColumnVector)read.Column(0);
        Assert.Equal(0, rItems.ElementLength(0)); // empty row preserved
        Assert.Equal(2, rItems.ElementLength(1)); // multi row preserved
        Assert.Equal(1, rItems.ElementLength(2)); // single row preserved
        var e1 = (StructColumnVector)rItems.ElementsAt(1);
        Assert.True(e1.Child("b").IsNull(0)); // b null-filled per element (a dropped)
        Assert.True(e1.Child("b").IsNull(1));
    }

    // §3.8o (R8/M6) — array<array<int>> id-mode: the inner element leaf binds by its MULTI-TOKEN nested.ids id
    // (P.element.element) threaded through the ReadListAsync→ReadListAsync recursion, NOT positionally.
    [Fact]
    public async Task IdMode_ArrayArray_InnerElementResolvedById_RoundTrip()
    {
        var schema = new StructType(new[]
        {
            new StructField("aa", new ArrayType(new ArrayType(DataTypes.LongType)), nullable: true),
        });
        ListColumnVector aa = ArrayOfArrayLong(
            (ArrayType)schema["aa"].DataType,
            new long?[]?[]?[]
            {
                new long?[]?[] { new long?[] { 7L, 8L }, new long?[] { 9L } },
                new long?[]?[] { Array.Empty<long?>() },
            });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { aa }, 2);

        await WriteIdMappedFileAndV0Async(schema, batch);
        ColumnBatch read = await ReadSingleBatchAsync();
        var rAa = (ListColumnVector)read.Column(0);
        Assert.Equal(2, rAa.ElementLength(0));
        var inner0 = (ListColumnVector)rAa.ElementsAt(0);
        Assert.Equal(7L, ((ListColumnVector)rAa.ElementsAt(0)).ElementsAt(0).GetValue<long>(0));
        Assert.Equal(8L, inner0.ElementsAt(0).GetValue<long>(1));
        Assert.Equal(9L, inner0.ElementsAt(1).GetValue<long>(0));
    }

    // §3.8s (R8/M6 — StructField re-seed) — struct<b: array<int>>: the inner array element binds by `b`'s OWN
    // nested.ids id via the re-seed at the struct boundary, NOT positionally / not by the parent's ids.
    [Fact]
    public async Task IdMode_StructArray_InnerElementResolvedById_ReSeed()
    {
        var schema = new StructType(new[]
        {
            new StructField("s", new StructType(new[]
            {
                new StructField("b", new ArrayType(DataTypes.LongType), nullable: true),
            }), nullable: true),
        });
        var bVec = ArrayOfScalarLong(
            (ArrayType)((StructType)schema["s"].DataType)["b"].DataType,
            new long?[]?[] { new long?[] { 55L, 56L }, new long?[] { 57L } });
        var sVec = new StructColumnVector((StructType)schema["s"].DataType, new ColumnVector[] { bVec }, new[] { false, false });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { sVec }, 2);

        await WriteIdMappedFileAndV0Async(schema, batch);
        ColumnBatch read = await ReadSingleBatchAsync();
        var rs = (StructColumnVector)read.Column(0);
        var rb = (ListColumnVector)rs.Child("b");
        Assert.Equal(2, rb.ElementLength(0));
        Assert.Equal(55L, rb.ElementsAt(0).GetValue<long>(0));
        Assert.Equal(56L, rb.ElementsAt(0).GetValue<long>(1));
        Assert.Equal(57L, rb.ElementsAt(1).GetValue<long>(0));
    }

    // §3.8q — map<string, array<long>>: the id threads through the MAP VALUE into the inner array
    // (ReadMapAsync→ReadListAsync), so the inner long binds by its multi-token nested.ids id (P.value.element).
    [Fact]
    public async Task IdMode_MapValueArray_IdThreadsThroughIntoInnerArray()
    {
        var schema = new StructType(new[]
        {
            new StructField("m", new MapType(DataTypes.StringType, new ArrayType(DataTypes.LongType)), nullable: true),
        });
        var mVec = MapStringToArrayLong(
            (MapType)schema["m"].DataType,
            new[]
            {
                new (string, long?[])[] { ("k1", new long?[] { 200L, 201L }) },
                new (string, long?[])[] { ("k2", new long?[] { 202L }) },
            });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { mVec }, 2);

        await WriteIdMappedFileAndV0Async(schema, batch);
        ColumnBatch read = await ReadSingleBatchAsync();
        var rm = (MapColumnVector)read.Column(0);
        var values0 = (ListColumnVector)rm.ValuesAt(0);
        Assert.Equal(200L, values0.ElementsAt(0).GetValue<long>(0));
        Assert.Equal(201L, values0.ElementsAt(0).GetValue<long>(1));
    }

    // §3.8b companion — a container map KEY stays fail-closed on write in id mode (§2.6).
    [Fact]
    public void IdMode_MapKeyContainer_FailsClosed()
    {
        var schema = new StructType(new[]
        {
            new StructField(
                "m",
                new MapType(new StructType(new[] { new StructField("x", DataTypes.LongType) }), DataTypes.LongType),
                nullable: true),
        });
        Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.AssignFreshMapping(schema, new SeededPhysicalNameSource("mk"), ColumnMappingMode.Id));
    }

    // §3.8e/§3.8j/§3.8n — REQUIRED-absent depth>1 fail-closed cells are pinned at the READER level (clean
    // ColumnNotPresentInFile) in NestedIdModeRenameDiscriminationTests; through the full scan path a
    // required-added column is caught earlier by the schema-evolution guard (also fail-closed).

    // §3.8m — struct-field container M5 companion: struct<s: struct<a>> evolved to struct<s: struct<b>>, old
    // file holds s + s.a; s is located by its physicalName (PRESENT) so its per-row presence structure is read
    // and `b` is null-filled per row — `s` is NOT null-filled whole even though `b` is its only requested child.
    [Fact]
    public async Task IdMode_StructField_AllChildrenReplaced_StructPresent_NullFillsLeaves_M5()
    {
        var oldInner = new StructType(new[] { new StructField("a", DataTypes.LongType, nullable: true) });
        var oldSchema = new StructType(new[] { new StructField("s", oldInner, nullable: true) });
        var aVec = Long(10L, 11L);
        var sVec = new StructColumnVector(oldInner, new ColumnVector[] { aVec }, new[] { false, false });
        (StructType oldMapped, long oldMax) =
            await WriteIdMappedFileAndV0Async(oldSchema, new ManagedColumnBatch(oldSchema, new ColumnVector[] { sVec }, 2));

        // Replace `a` with a fresh nullable `b` (struct `s` unchanged/present).
        StructType newMapped = ReplaceStructFieldChild(oldMapped, "s", "b", oldMax + 1);
        await CommitMetadataIdAsync(newMapped, oldMax + 1, version: 1);

        ColumnBatch read = await ReadSingleBatchAsync();
        var rs = (StructColumnVector)read.Column(0);
        Assert.False(rs.IsNull(0)); // s PRESENT (not null-filled whole)
        Assert.False(rs.IsNull(1));
        Assert.True(rs.Child("b").IsNull(0)); // b null-filled per row (a dropped)
        Assert.True(rs.Child("b").IsNull(1));
    }

    // ---- required-absent / evolve mapped-schema builders (id mode) ----

    private static DeltaSharp.Types.FieldMetadata IdMeta(long id) =>
        DeltaSharp.Types.FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(ColumnMapping.PhysicalNameKey, MetadataValue.String("col-" + id.ToString(CultureInfo.InvariantCulture))),
            new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(id)),
        });

    private static StructType ReplaceStructFieldChild(StructType mapped, string structColumn, string newChild, long id)
    {
        var fields = mapped.Select(f =>
        {
            if (f.Name != structColumn)
            {
                return f;
            }

            var inner = new StructType(new[] { new StructField(newChild, DataTypes.LongType, nullable: true, IdMeta(id)) });
            return new StructField(f.Name, inner, f.Nullable, f.Metadata);
        }).ToList();
        return new StructType(fields);
    }

    // ---- id-mode test vector builders ----

    private static ListColumnVector ArrayStructAB(ArrayType type, IReadOnlyList<(long? A, long? C)[]> rows)
    {
        var elemType = (StructType)type.ElementType;
        var innerType = (StructType)elemType["b"].DataType;
        MutableColumnVector a = ColumnVectors.Create(DataTypes.LongType, 16);
        MutableColumnVector c = ColumnVectors.Create(DataTypes.LongType, 16);
        var innerNulls = new List<bool>();
        var elemNulls = new List<bool>();
        var offsets = new int[rows.Count + 1];
        int cursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            offsets[i] = cursor;
            foreach ((long? av, long? cv) in rows[i])
            {
                AppendLong(a, av);
                AppendLong(c, cv);
                innerNulls.Add(false);
                elemNulls.Add(false);
                cursor++;
            }
        }

        offsets[rows.Count] = cursor;
        var innerVec = new StructColumnVector(innerType, new ColumnVector[] { c }, innerNulls.ToArray());
        var elemVec = new StructColumnVector(elemType, new ColumnVector[] { a, innerVec }, elemNulls.ToArray());
        return new ListColumnVector(type, elemVec, offsets, new bool[rows.Count]);
    }

    private static ListColumnVector ArrayOfArrayLong(ArrayType type, IReadOnlyList<long?[]?[]?> rows)
    {
        var innerType = (ArrayType)type.ElementType;
        MutableColumnVector leaf = ColumnVectors.Create(DataTypes.LongType, 16);
        var innerOffsets = new List<int> { 0 };
        var innerNulls = new List<bool>();
        var outerOffsets = new int[rows.Count + 1];
        var outerNulls = new bool[rows.Count];
        int leafCursor = 0;
        int innerCursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            outerOffsets[i] = innerCursor;
            long?[]?[]? row = rows[i];
            outerNulls[i] = row is null;
            if (row is not null)
            {
                foreach (long?[]? innerRow in row)
                {
                    innerNulls.Add(innerRow is null);
                    if (innerRow is not null)
                    {
                        foreach (long? v in innerRow)
                        {
                            AppendLong(leaf, v);
                            leafCursor++;
                        }
                    }

                    innerOffsets.Add(leafCursor);
                    innerCursor++;
                }
            }
        }

        outerOffsets[rows.Count] = innerCursor;
        var innerList = new ListColumnVector(innerType, leaf, innerOffsets.ToArray(), innerNulls.ToArray());
        return new ListColumnVector(type, innerList, outerOffsets, outerNulls);
    }

    private static MapColumnVector MapStringToArrayLong(MapType type, IReadOnlyList<(string K, long?[] V)[]> rows)
    {
        var valueType = (ArrayType)type.ValueType;
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.StringType, 16);
        MutableColumnVector leaf = ColumnVectors.Create(DataTypes.LongType, 16);
        var valInnerOffsets = new List<int> { 0 };
        var valInnerNulls = new List<bool>();
        var entryOffsets = new int[rows.Count + 1];
        int entryCursor = 0;
        int leafCursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            entryOffsets[i] = entryCursor;
            foreach ((string k, long?[] v) in rows[i])
            {
                AppendStr(keys, k);
                foreach (long? lv in v)
                {
                    AppendLong(leaf, lv);
                    leafCursor++;
                }

                valInnerOffsets.Add(leafCursor);
                valInnerNulls.Add(false);
                entryCursor++;
            }
        }

        entryOffsets[rows.Count] = entryCursor;
        var values = new ListColumnVector(valueType, leaf, valInnerOffsets.ToArray(), valInnerNulls.ToArray());
        return new MapColumnVector(type, keys, values, entryOffsets, new bool[rows.Count]);
    }

    // Rebuilds a mapped id-mode array<struct> so the element struct has a SINGLE fresh nullable child (drop the
    // old children, add `newChild` with a fresh id + physicalName) — the M5 all-children-replaced evolution.
    private static StructType ReplaceArrayElementChild(
        StructType mapped, string arrayColumn, string newChild, long newId)
    {
        var fields = mapped.Select(f =>
        {
            if (f.Name != arrayColumn)
            {
                return f;
            }

            var arr = (ArrayType)f.DataType;
            var newElem = new StructType(new[]
            {
                new StructField(newChild, DataTypes.LongType, nullable: true, DeltaSharp.Types.FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>(ColumnMapping.PhysicalNameKey, MetadataValue.String("col-" + newId.ToString(CultureInfo.InvariantCulture))),
                    new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(newId)),
                })),
            });
            return new StructField(f.Name, new ArrayType(newElem, arr.ContainsNull), f.Nullable, f.Metadata);
        }).ToList();
        return new StructType(fields);
    }

    // ==================================================================================================
    // StackOverflow DoS guard — the new depth>1 recursions are bounded (#866 866a, red-team)
    // ==================================================================================================

    [Fact]
    public void Dos_DeepStructSchema_NearCapSucceeds_OverCapFailsClosed_AllPaths()
    {
        // A directly-constructed in-memory schema nested deeper than the cap must fail closed with a TYPED
        // exception on EVERY new recursion (assign / validate / physical / read), never a StackOverflowException.
        const int nearCap = 55;   // within the 64-level cap
        const int overCap = 200;  // well past the cap

        // --- assignment (AssignMappedType) ---
        (StructType nearMapped, long nearMax) = ColumnMapping.AssignFreshMapping(
            DeepLogicalStruct(nearCap), new SeededPhysicalNameSource("dos-assign"), ColumnMappingMode.Name);
        Assert.Throws<DeltaProtocolException>(() => ColumnMapping.AssignFreshMapping(
            DeepLogicalStruct(overCap), new SeededPhysicalNameSource("dos-assign-over"), ColumnMappingMode.Name));

        // --- validation (ValidateMappedInterior / ValidateMappedLevel) ---
        ColumnMapping.ValidateColumnMappingSchema(
            ColumnMappingMode.Name, nearMapped, ColumnMapping.NameModeConfiguration(nearMax));
        Assert.Throws<DeltaProtocolException>(() => ColumnMapping.ValidateColumnMappingSchema(
            ColumnMappingMode.Name, DeepMappedStruct(overCap), ColumnMapping.NameModeConfiguration(overCap + 2)));

        // --- physical schema (ToPhysicalType) ---
        _ = ColumnMapping.ToPhysicalSchema(nearMapped, ColumnMappingMode.Name);
        Assert.Throws<DeltaProtocolException>(() => ColumnMapping.ToPhysicalSchema(
            DeepMappedStruct(overCap), ColumnMappingMode.Name));

        // --- read schema build (ColumnMappingProjection.BuildPhysicalDataType) ---
        StructType overMapped = DeepMappedStruct(overCap);
        string[] overNames = ColumnMappingProjection.ResolvePhysicalNames(overMapped, ColumnMappingMode.Name);
        Assert.Throws<DeltaStorageException>(() => ColumnMappingProjection.BuildDataSchema(
            overMapped, overNames, System.Collections.Immutable.ImmutableArray<string>.Empty));

        // the near-cap read schema builds fine
        string[] nearNames = ColumnMappingProjection.ResolvePhysicalNames(nearMapped, ColumnMappingMode.Name);
        _ = ColumnMappingProjection.BuildDataSchema(
            nearMapped, nearNames, System.Collections.Immutable.ImmutableArray<string>.Empty);

        // --- evolution (EvolveMappedType) — #886 item 2: the evolve recursion is depth-bounded too (symmetry
        // with assign/validate/physical/read). Evolve a shallow { id } mapping by ADDING an over-cap-deep column:
        // the new column's recursion fails closed with a TYPED DeltaProtocolException before any descent, never
        // a StackOverflowException; a near-cap addition evolves fine.
        (StructType shallowMapped, long shallowMax) = ColumnMapping.AssignFreshMapping(
            new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) }),
            new SeededPhysicalNameSource("dos-evolve-base"), ColumnMappingMode.Name);
        var overCapEvolvedLogical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("deep", DeepLogicalStruct(overCap), nullable: true),
        });
        Assert.Throws<DeltaProtocolException>(() => ColumnMapping.EvolveNameModeMapping(
            overCapEvolvedLogical, shallowMapped, ColumnMapping.NameModeConfiguration(shallowMax),
            new SeededPhysicalNameSource("dos-evolve-over"), ColumnMappingMode.Name));

        var nearCapEvolvedLogical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("deep", DeepLogicalStruct(nearCap), nullable: true),
        });
        _ = ColumnMapping.EvolveNameModeMapping(
            nearCapEvolvedLogical, shallowMapped, ColumnMapping.NameModeConfiguration(shallowMax),
            new SeededPhysicalNameSource("dos-evolve-near"), ColumnMappingMode.Name);
    }

    // A logical struct nested `depth` levels deep: { c1: struct<c2: struct< … struct<leaf:long> >> }.
    private static StructType DeepLogicalStruct(int depth)
    {
        DataType inner = new StructType(new[] { new StructField("leaf", DataTypes.LongType, nullable: true) });
        for (int i = depth; i >= 1; i--)
        {
            inner = new StructType(new[] { new StructField("c" + i.ToString(CultureInfo.InvariantCulture), inner, nullable: true) });
        }

        return (StructType)inner;
    }

    // The same shape, hand-mapped: each StructField carries a unique (id, physicalName) so it is a valid
    // NAME-mode mapped schema (except for its illegal depth), exercising the validate/physical/read guards
    // without going through the (depth-guarded) assignment path.
    private static StructType DeepMappedStruct(int depth)
    {
        long id = depth + 2;
        StructField field = new(
            "leaf", DataTypes.LongType, nullable: true, MappingMeta(id));
        for (int i = depth; i >= 1; i--)
        {
            var s = new StructType(new[] { field });
            field = new StructField("c" + i.ToString(CultureInfo.InvariantCulture), s, nullable: true, MappingMeta(i));
        }

        return new StructType(new[] { field });
    }

    private static DeltaSharp.Types.FieldMetadata MappingMeta(long id) =>
        DeltaSharp.Types.FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(id)),
            new KeyValuePair<string, MetadataValue>(
                ColumnMapping.PhysicalNameKey, MetadataValue.String("col-" + id.ToString(CultureInfo.InvariantCulture))),
        });

    // ==================================================================================================
    // Harness
    // ==================================================================================================

    private readonly record struct ImmutableConfig(long MaxColumnId, System.Collections.Immutable.ImmutableSortedDictionary<string, string> Config);

    private static DataType NwnType(string shape) => shape switch
    {
        "array<struct>" => new ArrayType(new StructType(new[] { new StructField("x", DataTypes.LongType) })),
        "struct<struct>" => new StructType(new[] { new StructField("inner", new StructType(new[] { new StructField("x", DataTypes.LongType) })) }),
        "map<string,struct>" => new MapType(DataTypes.StringType, new StructType(new[] { new StructField("x", DataTypes.LongType) })),
        "array<array>" => new ArrayType(new ArrayType(DataTypes.LongType)),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private async Task<long> WriteNameMappedAsync(StructType schema, ColumnBatch batch)
    {
        (_, long maxColumnId) = await WriteNameMappedFileAndV0Async(schema, batch);
        return maxColumnId;
    }

    // ---- id-mode round-trip helpers (866b) ----

    // Assigns an ID mapping, writes the physical batch (leaf field_ids stamped) via the production writer, and
    // commits a raw protocol+metaData+add v0 under column-mapping ID mode. Returns the mapped schema + max.
    private async Task<(StructType Mapped, long MaxColumnId)> WriteIdMappedFileAndV0Async(
        StructType schema, ColumnBatch batch)
    {
        (StructType mapped, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(schema, new SeededPhysicalNameSource(Seed), ColumnMappingMode.Id);
        StructType physical = ColumnMapping.MapWriteSchemaToPhysical(schema, mapped, ColumnMappingMode.Id);
        byte[] parquetBytes = await ParquetTestHelpers.WriteToBytesAsync(physical, new[] { RelabelForWrite(batch, physical) });

        using var backend = new LocalFileSystemBackend(_root);
        await backend.PutIfAbsentAsync("part-00000.parquet", parquetBytes, CancellationToken.None);
        string addLine =
            $"{{\"add\":{{\"path\":\"part-00000.parquet\",\"partitionValues\":{{}},"
            + $"\"size\":{parquetBytes.Length},\"modificationTime\":0,\"dataChange\":true}}}}";
        byte[] commit = Encoding.UTF8.GetBytes(
            ProtocolFeatureLine() + "\n" + MetadataLineMode(mapped, maxColumnId, "id") + "\n" + addLine + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", commit, CancellationToken.None);
        return (mapped, maxColumnId);
    }

    private async Task CommitMetadataIdAsync(StructType mapped, long maxColumnId, int version)
    {
        using var backend = new LocalFileSystemBackend(_root);
        byte[] commit = Encoding.UTF8.GetBytes(MetadataLineMode(mapped, maxColumnId, "id") + "\n");
        await backend.PutIfAbsentAsync(
            $"_delta_log/{version.ToString("D20", CultureInfo.InvariantCulture)}.json", commit, CancellationToken.None);
    }

    private static (StructType Mapped, ImmutableConfig Config) EvolveId(
        StructType evolvedLogical, StructType currentMapped, long currentMax)
    {
        (StructType mapped, System.Collections.Immutable.ImmutableSortedDictionary<string, string> config) =
            ColumnMapping.EvolveNameModeMapping(
                evolvedLogical, currentMapped, ColumnMapping.IdModeConfiguration(currentMax),
                new SeededPhysicalNameSource(Seed + "-evolve"), ColumnMappingMode.Id);
        long newMax = long.Parse(config[ColumnMapping.MaxColumnIdKey], CultureInfo.InvariantCulture);
        return (mapped, new ImmutableConfig(newMax, config));
    }

    // Assigns a name mapping, writes the physical batch to a real Parquet file via the production writer, and
    // commits a raw protocol+metaData+add v0. Returns the mapped schema + maxColumnId for evolve tests.
    private async Task<(StructType Mapped, long MaxColumnId)> WriteNameMappedFileAndV0Async(
        StructType schema, ColumnBatch batch)
    {
        (StructType mapped, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(schema, new SeededPhysicalNameSource(Seed), ColumnMappingMode.Name);
        StructType physical = ColumnMapping.MapWriteSchemaToPhysical(schema, mapped, ColumnMappingMode.Name);
        byte[] parquetBytes = await ParquetTestHelpers.WriteToBytesAsync(physical, new[] { RelabelForWrite(batch, physical) });

        using var backend = new LocalFileSystemBackend(_root);
        await backend.PutIfAbsentAsync("part-00000.parquet", parquetBytes, CancellationToken.None);
        string addLine =
            $"{{\"add\":{{\"path\":\"part-00000.parquet\",\"partitionValues\":{{}},"
            + $"\"size\":{parquetBytes.Length},\"modificationTime\":0,\"dataChange\":true}}}}";
        byte[] commit = Encoding.UTF8.GetBytes(
            ProtocolFeatureLine() + "\n" + MetadataLine(mapped, maxColumnId) + "\n" + addLine + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", commit, CancellationToken.None);
        return (mapped, maxColumnId);
    }

    private async Task CommitMetadataAsync(StructType mapped, long maxColumnId, int version)
    {
        using var backend = new LocalFileSystemBackend(_root);
        byte[] commit = Encoding.UTF8.GetBytes(MetadataLine(mapped, maxColumnId) + "\n");
        await backend.PutIfAbsentAsync(
            $"_delta_log/{version.ToString("D20", CultureInfo.InvariantCulture)}.json", commit, CancellationToken.None);
    }

    private static (StructType Mapped, ImmutableConfig Config) EvolveName(
        StructType evolvedLogical, StructType currentMapped, long currentMax)
    {
        (StructType mapped, System.Collections.Immutable.ImmutableSortedDictionary<string, string> config) =
            ColumnMapping.EvolveNameModeMapping(
                evolvedLogical, currentMapped, ColumnMapping.NameModeConfiguration(currentMax),
                new SeededPhysicalNameSource(Seed + "-evolve"), ColumnMappingMode.Name);
        long newMax = long.Parse(config[ColumnMapping.MaxColumnIdKey], CultureInfo.InvariantCulture);
        return (mapped, new ImmutableConfig(newMax, config));
    }

    // Rebuilds a mapped schema so the named array<struct> column's element struct carries a SINGLE fresh
    // nullable leaf (physicalName col-<newId>) — modelling an all-children-replaced evolution for the M5 read.
    private static StructType ReplaceArrayElementStructChildWithFreshLeaf(
        StructType mapped, string arrayColumn, string newChildName, long newId)
    {
        var fields = mapped.Select(f =>
        {
            if (f.Name != arrayColumn)
            {
                return f;
            }

            var arr = (ArrayType)f.DataType;
            var newElem = new StructType(new[]
            {
                new StructField(newChildName, DataTypes.LongType, nullable: true, DeltaSharp.Types.FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>(ColumnMapping.PhysicalNameKey, MetadataValue.String("col-" + newId.ToString(CultureInfo.InvariantCulture))),
                    new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(newId)),
                })),
            });
            return new StructField(f.Name, new ArrayType(newElem, arr.ContainsNull), f.Nullable, f.Metadata);
        }).ToList();
        return new StructType(fields);
    }

    private static ColumnBatch RelabelForWrite(ColumnBatch batch, StructType physicalSchema)
    {
        var cols = new ColumnVector[physicalSchema.Count];
        for (int i = 0; i < physicalSchema.Count; i++)
        {
            cols[i] = RelabelColumn(batch.Column(i), physicalSchema[i].DataType);
        }

        return new ManagedColumnBatch(physicalSchema, cols, batch.RowCount);
    }

    private static ColumnVector RelabelColumn(ColumnVector column, DataType targetType) => (column, targetType) switch
    {
        (StructColumnVector s, StructType st) => s.RelabelTo(st),
        (ListColumnVector l, ArrayType at) => l.RelabelTo(at),
        (MapColumnVector m, MapType mt) => m.RelabelTo(mt),
        _ => column,
    };

    private async Task<ColumnBatch> ReadSingleBatchAsync()
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        var batches = new List<ColumnBatch>();
        foreach (ColumnBatch b in await source.ReadBatchesAsync(info.Version))
        {
            batches.Add(b);
        }

        return Assert.Single(batches);
    }

    private static string ProtocolFeatureLine() =>
        """{"protocol":{"minReaderVersion":3,"minWriterVersion":7,"readerFeatures":["columnMapping"],"writerFeatures":["columnMapping"]}}""";

    private static string MetadataLine(StructType mapped, long maxColumnId) =>
        MetadataLineMode(mapped, maxColumnId, "name");

    private static string MetadataLineMode(StructType mapped, long maxColumnId, string mode)
    {
        string schemaJson = DeltaSchemaJson.ToJson(mapped);
        string escapedSchema = System.Text.Json.JsonSerializer.Serialize(schemaJson);
        string config = "{\"delta.columnMapping.mode\":\"" + mode + "\",\"delta.columnMapping.maxColumnId\":\""
            + maxColumnId.ToString(CultureInfo.InvariantCulture) + "\"}";
        return "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
            + "\"schemaString\":" + escapedSchema + ",\"partitionColumns\":[]"
            + ",\"configuration\":" + config + "}}";
    }

    // ---- vector builders ----

    private static MutableColumnVector Long(params long?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.LongType, values.Length);
        foreach (long? value in values)
        {
            if (value is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendValue(value.Value);
            }
        }

        return v;
    }

    // Builds a nullable string leaf vector.
    private static MutableColumnVector Str(params string?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, values.Length);
        foreach (string? value in values)
        {
            AppendStr(v, value);
        }

        return v;
    }

    // Builds an array<long> (scalar element) vector.
    private static ListColumnVector ArrayOfScalarLong(ArrayType type, IReadOnlyList<long?[]?> rows)
    {
        MutableColumnVector elements = ColumnVectors.Create(DataTypes.LongType, 16);
        var offsets = new int[rows.Count + 1];
        var nulls = new bool[rows.Count];
        int cursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            offsets[i] = cursor;
            long?[]? row = rows[i];
            nulls[i] = row is null;
            if (row is not null)
            {
                foreach (long? value in row)
                {
                    AppendLong(elements, value);
                    cursor++;
                }
            }
        }

        offsets[rows.Count] = cursor;
        return new ListColumnVector(type, elements, offsets, nulls);
    }

    // Builds an array<struct<childLong>> where each element struct has exactly ONE nullable long child (the
    // element struct type is type.ElementType). Used by the evolve/null-fill cells whose OLD element is a
    // single-child struct.
    private static ListColumnVector ArrayOfSingleLong(ArrayType type, IReadOnlyList<long?[]?> rows)
    {
        var elemType = (StructType)type.ElementType;
        MutableColumnVector child = ColumnVectors.Create(DataTypes.LongType, 16);
        var elemNulls = new List<bool>();
        var offsets = new int[rows.Count + 1];
        var listNulls = new bool[rows.Count];
        int cursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            offsets[i] = cursor;
            long?[]? row = rows[i];
            listNulls[i] = row is null;
            if (row is not null)
            {
                foreach (long? value in row)
                {
                    AppendLong(child, value);
                    elemNulls.Add(false);
                    cursor++;
                }
            }
        }

        offsets[rows.Count] = cursor;
        var elements = new StructColumnVector(elemType, new ColumnVector[] { child }, elemNulls.ToArray());
        return new ListColumnVector(type, elements, offsets, listNulls);
    }

    private static ListColumnVector ArrayOfStruct(ArrayType type, IReadOnlyList<(long? A, string? B)[]?> rows)
    {
        var elemType = (StructType)type.ElementType;
        MutableColumnVector a = ColumnVectors.Create(DataTypes.LongType, 16);
        MutableColumnVector b = ColumnVectors.Create(DataTypes.StringType, 16);
        var elemNulls = new List<bool>();
        var offsets = new int[rows.Count + 1];
        var listNulls = new bool[rows.Count];
        int cursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            offsets[i] = cursor;
            (long? A, string? B)[]? row = rows[i];
            listNulls[i] = row is null;
            if (row is not null)
            {
                foreach ((long? av, string? bv) in row)
                {
                    AppendLong(a, av);
                    AppendStr(b, bv);
                    elemNulls.Add(false);
                    cursor++;
                }
            }
        }

        offsets[rows.Count] = cursor;
        var elements = new StructColumnVector(elemType, new ColumnVector[] { a, b }, elemNulls.ToArray());
        return new ListColumnVector(type, elements, offsets, listNulls);
    }

    private static ListColumnVector ArrayOfStructWithNestedStruct(
        ArrayType type, IReadOnlyList<(long? A, long? C)[]?> rows)
    {
        var elemType = (StructType)type.ElementType;
        var innerType = (StructType)elemType["b"].DataType;
        MutableColumnVector a = ColumnVectors.Create(DataTypes.LongType, 16);
        MutableColumnVector c = ColumnVectors.Create(DataTypes.LongType, 16);
        var innerNulls = new List<bool>();
        var elemNulls = new List<bool>();
        var offsets = new int[rows.Count + 1];
        var listNulls = new bool[rows.Count];
        int cursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            offsets[i] = cursor;
            (long? A, long? C)[]? row = rows[i];
            listNulls[i] = row is null;
            if (row is not null)
            {
                foreach ((long? av, long? cv) in row)
                {
                    AppendLong(a, av);
                    AppendLong(c, cv);
                    innerNulls.Add(false);
                    elemNulls.Add(false);
                    cursor++;
                }
            }
        }

        offsets[rows.Count] = cursor;
        var inner = new StructColumnVector(innerType, new ColumnVector[] { c }, innerNulls.ToArray());
        var elements = new StructColumnVector(elemType, new ColumnVector[] { a, inner }, elemNulls.ToArray());
        return new ListColumnVector(type, elements, offsets, listNulls);
    }

    private static ListColumnVector ArrayOfLongArray(ArrayType type, IReadOnlyList<long[]?[]?> rows)
    {
        var innerType = (ArrayType)type.ElementType;
        MutableColumnVector innerElements = ColumnVectors.Create(DataTypes.LongType, 16);
        var innerOffsets = new List<int> { 0 };
        var innerNulls = new List<bool>();
        var outerOffsets = new int[rows.Count + 1];
        var outerNulls = new bool[rows.Count];
        int innerCursor = 0;
        int outerCursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            outerOffsets[i] = outerCursor;
            long[]?[]? row = rows[i];
            outerNulls[i] = row is null;
            if (row is not null)
            {
                foreach (long[]? innerRow in row)
                {
                    innerNulls.Add(innerRow is null);
                    if (innerRow is not null)
                    {
                        foreach (long value in innerRow)
                        {
                            innerElements.AppendValue(value);
                            innerCursor++;
                        }
                    }

                    innerOffsets.Add(innerCursor);
                    outerCursor++;
                }
            }
        }

        outerOffsets[rows.Count] = outerCursor;
        var innerList = new ListColumnVector(innerType, innerElements, innerOffsets.ToArray(), innerNulls.ToArray());
        return new ListColumnVector(type, innerList, outerOffsets, outerNulls);
    }

    private static MapColumnVector MapOfStruct(MapType type, IReadOnlyList<(string Key, long? V)[]?> rows)
    {
        var valStruct = (StructType)type.ValueType;
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.StringType, 16);
        MutableColumnVector v = ColumnVectors.Create(DataTypes.LongType, 16);
        var valNulls = new List<bool>();
        var offsets = new int[rows.Count + 1];
        var mapNulls = new bool[rows.Count];
        int cursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            offsets[i] = cursor;
            (string Key, long? V)[]? row = rows[i];
            mapNulls[i] = row is null;
            if (row is not null)
            {
                foreach ((string key, long? vv) in row)
                {
                    keys.AppendBytes(Encoding.UTF8.GetBytes(key));
                    AppendLong(v, vv);
                    valNulls.Add(false);
                    cursor++;
                }
            }
        }

        offsets[rows.Count] = cursor;
        var values = new StructColumnVector(valStruct, new ColumnVector[] { v }, valNulls.ToArray());
        return new MapColumnVector(type, keys, values, offsets, mapNulls);
    }

    private static void AppendLong(MutableColumnVector v, long? value)
    {
        if (value is null)
        {
            v.AppendNull();
        }
        else
        {
            v.AppendValue(value.Value);
        }
    }

    private static void AppendStr(MutableColumnVector v, string? value)
    {
        if (value is null)
        {
            v.AppendNull();
        }
        else
        {
            v.AppendBytes(Encoding.UTF8.GetBytes(value));
        }
    }
}
