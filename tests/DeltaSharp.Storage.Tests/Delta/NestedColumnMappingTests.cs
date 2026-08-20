using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// The §3 oracle for nested (struct/array/map) column mapping (#676) — the HAPPY-PATH and structural-property
/// half (round-trip identity, maxColumnId counting, type agreement on the read exit, null/empty fidelity, and
/// write byte-invariance). The fail-closed / tamper matrix (§3.6–3.26, 3.33) lives in
/// <see cref="NestedColumnMappingTamperFuzzTests"/>.
/// </summary>
/// <remarks>
/// The primary oracle is a REAL write→read round trip against the merged nested writer + #571 reader through
/// the production <see cref="DeltaWriteTarget"/> / <see cref="DeltaReadSource"/> doors. Every same-typed
/// sibling draws its values from a DISJOINT domain (design §3 preamble) so a positional mis-bind cannot pass
/// on equal values. Physical names are minted by a deterministic seeded source so the committed schemaString
/// is golden.
/// </remarks>
[Collection(ColumnMappingTestCollection.Name)]
public sealed class NestedColumnMappingTests : IDisposable
{
    private const string Seed = "nested-column-mapping-676";

    private readonly string _root;

    public NestedColumnMappingTests() =>
        _root = Path.Combine(Path.GetTempPath(), "nestedcm-" + Guid.NewGuid().ToString("N"));

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

    // ---- §3.1 · Create + read, name mode (struct / array / map) with maxColumnId + schemaString oracle ----

    [Fact]
    public async Task NameMode_CreateReadStruct_RoundTripsIdenticalValues_AndMaxColumnIdCountsStructFieldsAtEveryDepth()
    {
        // {id:long, addr:struct<city:string, zip:long>, tags:array<string>} — the design §3.1 fixture.
        // maxColumnId == 5 counting StructFields: id(1), addr(2), addr.city(3), addr.zip(4), tags(5) — NOT the
        // array element (element/key/value are not StructFields, C1).
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("addr", new StructType(new[]
            {
                new StructField("city", DataTypes.StringType, nullable: true),
                new StructField("zip", DataTypes.LongType, nullable: true),
            }), nullable: true),
            new StructField("tags", new ArrayType(DataTypes.StringType), nullable: true),
        });

        // Disjoint domains so a mis-bind is visible: id ∈ [1..], zip ∈ [90000..].
        var idVec = Long(new long?[] { 1L, 2L });
        var city = Str(new[] { "seattle", "portland" });
        var zip = Long(new long?[] { 98101L, 97201L });
        var addr = new StructColumnVector(
            (StructType)schema["addr"].DataType, new ColumnVector[] { city, zip }, new[] { false, false });
        var tags = StrList((ArrayType)schema["tags"].DataType, new[] { new[] { "a", "b" }, new[] { "c" } });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { idVec, addr, tags }, 2);

        await CreateNameMappedAsync(schema, batch);

        Snapshot snap = await LoadSnapshotAsync();
        Assert.Equal("5", snap.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);

        // Committed schemaString carries NO metadata object under any elementType/keyType/valueType (C1).
        AssertNoMetadataUnderNestedInterior(snap.Metadata.SchemaString);

        // Round-trip identity.
        ColumnBatch read = await ReadSingleBatchAsync();
        Assert.Equal(new[] { "id", "addr", "tags" }, snap.Schema.Select(f => f.Name).ToArray());
        var readAddr = (StructColumnVector)read.Column(1);
        Assert.Equal("seattle", Encoding.UTF8.GetString(readAddr.Child(0).GetBytes(0)));
        Assert.Equal("portland", Encoding.UTF8.GetString(readAddr.Child(0).GetBytes(1)));
        Assert.Equal(98101L, readAddr.Child(1).GetValue<long>(0));
        Assert.Equal(97201L, readAddr.Child(1).GetValue<long>(1));
        var readTags = (ListColumnVector)read.Column(2);
        Assert.Equal("a", Encoding.UTF8.GetString(readTags.ElementsAt(0).GetBytes(0)));
        Assert.Equal("b", Encoding.UTF8.GetString(readTags.ElementsAt(0).GetBytes(1)));
        Assert.Equal("c", Encoding.UTF8.GetString(readTags.ElementsAt(1).GetBytes(0)));
    }

    [Fact]
    public async Task NameMode_ArrayColumn_ContributesExactlyOneToMaxColumnId()
    {
        // {id:long, tags:array<string>} → maxColumnId == 2 (id, tags); the element adds nothing.
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("tags", new ArrayType(DataTypes.StringType), nullable: true),
        });
        var batch = new ManagedColumnBatch(
            schema,
            new ColumnVector[]
            {
                Long(new long?[] { 7L }),
                StrList((ArrayType)schema["tags"].DataType, new[] { new[] { "x", "y" } }),
            },
            1);

        await CreateNameMappedAsync(schema, batch);
        Snapshot snap = await LoadSnapshotAsync();
        Assert.Equal("2", snap.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
        AssertNoMetadataUnderNestedInterior(snap.Metadata.SchemaString);
    }

    [Fact]
    public async Task NameMode_MapColumn_ContributesExactlyOneToMaxColumnId_AndRoundTrips()
    {
        // {id:long, props:map<string,long>} → maxColumnId == 2 (id, props); key/value add nothing.
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("props", new MapType(DataTypes.StringType, DataTypes.LongType), nullable: true),
        });
        var props = StrLongMap(
            (MapType)schema["props"].DataType,
            new[] { new[] { ("w", 1000L), ("h", 2000L) } });
        var batch = new ManagedColumnBatch(
            schema, new ColumnVector[] { Long(new long?[] { 42L }), props }, 1);

        await CreateNameMappedAsync(schema, batch);
        Snapshot snap = await LoadSnapshotAsync();
        Assert.Equal("2", snap.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
        AssertNoMetadataUnderNestedInterior(snap.Metadata.SchemaString);

        ColumnBatch read = await ReadSingleBatchAsync();
        var readMap = (MapColumnVector)read.Column(1);
        Assert.Equal("w", Encoding.UTF8.GetString(readMap.KeysAt(0).GetBytes(0)));
        Assert.Equal(1000L, readMap.ValuesAt(0).GetValue<long>(0));
        Assert.Equal("h", Encoding.UTF8.GetString(readMap.KeysAt(0).GetBytes(1)));
        Assert.Equal(2000L, readMap.ValuesAt(0).GetValue<long>(1));
    }

    // ---- §3.2 · Create + read, id mode, struct<scalars> (children bind by field_id within container) ----

    [Fact]
    public async Task IdMode_CreateReadStruct_ChildrenBindByFieldId_ContainerBindsByPhysicalName()
    {
        // A struct<a:long, b:long> under id mode; each child leaf carries its own field_id. Values are drawn
        // from DISJOINT domains (a ∈ [1000,1999], b ∈ [2000,2999]) so a positional mis-bind is visible.
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("pt", new StructType(new[]
            {
                new StructField("a", DataTypes.LongType, nullable: true),
                new StructField("b", DataTypes.LongType, nullable: true),
            }), nullable: true),
        });
        var pt = new StructColumnVector(
            (StructType)schema["pt"].DataType,
            new ColumnVector[] { Long(new long?[] { 1001L, 1002L }), Long(new long?[] { 2001L, 2002L }) },
            new[] { false, false });
        var batch = new ManagedColumnBatch(
            schema, new ColumnVector[] { Long(new long?[] { 1L, 2L }), pt }, 2);

        await CreateIdMappedAsync(schema, batch);

        ColumnBatch read = await ReadSingleBatchAsync();
        var readPt = (StructColumnVector)read.Column(1);
        Assert.Equal(1001L, readPt.Child(0).GetValue<long>(0));
        Assert.Equal(1002L, readPt.Child(0).GetValue<long>(1));
        Assert.Equal(2001L, readPt.Child(1).GetValue<long>(0));
        Assert.Equal(2002L, readPt.Child(1).GetValue<long>(1));
    }

    [Fact]
    public async Task IdMode_CreateReadStruct_ChildBindsByFieldId_AfterTopLevelContainerRename_ReadsThrough()
    {
        // §3.2: read resolves each child by field_id within the container subtree AFTER a logical rename
        // (read-through, no rewrite); the container binds by physicalName (rename-stable). Rename the
        // top-level container column and re-read: the children still resolve by their stamped field_ids.
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("pt", new StructType(new[]
            {
                new StructField("a", DataTypes.LongType, nullable: true),
                new StructField("b", DataTypes.LongType, nullable: true),
            }), nullable: true),
        });
        var pt = new StructColumnVector(
            (StructType)schema["pt"].DataType,
            new ColumnVector[] { Long(new long?[] { 1001L }), Long(new long?[] { 2001L }) },
            new[] { false });
        var batch = new ManagedColumnBatch(
            schema, new ColumnVector[] { Long(new long?[] { 1L }), pt }, 1);

        (StructType mapped, long maxColumnId) = await WriteRawNestedTableAsync(schema, batch, ColumnMappingMode.Id);

        // A read-through LOGICAL rename of the container (pt → point): metaData-only, physicalName + child ids
        // preserved. (Id-mode metadata-only rename is not offered by DeltaTableWriter — RequireNameMode — so the
        // rename is authored directly at the log, which is exactly what a rename-through must survive.)
        await CommitTopLevelLogicalRenameAsync(mapped, "pt", "point", ColumnMappingMode.Id, maxColumnId);

        Snapshot snap = await LoadSnapshotAsync();
        Assert.Contains(snap.Schema, f => f.Name == "point");

        ColumnBatch read = await ReadSingleBatchAsync();
        int idx = snap.Schema.Select(f => f.Name).ToList().IndexOf("point");
        var readPt = (StructColumnVector)read.Column(idx);
        Assert.Equal(1001L, readPt.Child(0).GetValue<long>(0)); // a — bound by field_id, not name
        Assert.Equal(2001L, readPt.Child(1).GetValue<long>(0)); // b
    }

    // ---- §3.3 · Schema-evolve (name mode) — add a new struct child ----

    [Fact]
    public void NameMode_EvolveAddStructChild_MintsOnlyNewChild_PreservesExisting_MaxColumnIdStrictlyIncreases()
    {
        // §3.3 at the assignment/evolve door (ColumnMapping.EvolveNameModeMapping): add a new struct child;
        // only the new child mints a fresh id/physicalName, every existing nested id/physicalName is preserved,
        // maxColumnId strictly increases, and the mint is pre-order (matching is per-parent-path).
        var initial = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("addr", new StructType(new[]
            {
                new StructField("city", DataTypes.StringType, nullable: true),
            }), nullable: true),
        });
        (StructType mapped, long maxBefore) =
            ColumnMapping.AssignFreshMapping(initial, new SeededPhysicalNameSource(Seed));

        long idId = GetId(mapped["id"]);
        var addrBefore = (StructType)mapped["addr"].DataType;
        long addrId = GetId(mapped["addr"]);
        long cityId = GetId(addrBefore["city"]);
        string cityPhysical = Physical(addrBefore["city"]);

        var evolved = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("addr", new StructType(new[]
            {
                new StructField("city", DataTypes.StringType, nullable: true),
                new StructField("zip", DataTypes.LongType, nullable: true),
            }), nullable: true),
        });

        (StructType evolvedMapped, System.Collections.Immutable.ImmutableSortedDictionary<string, string> config) =
            ColumnMapping.EvolveNameModeMapping(
                evolved, mapped, ColumnMapping.NameModeConfiguration(maxBefore), new SeededPhysicalNameSource("evolve-child"));

        var addrAfter = (StructType)evolvedMapped["addr"].DataType;
        // Existing identities preserved verbatim (never re-minted).
        Assert.Equal(idId, GetId(evolvedMapped["id"]));
        Assert.Equal(addrId, GetId(evolvedMapped["addr"]));
        Assert.Equal(cityId, GetId(addrAfter["city"]));
        Assert.Equal(cityPhysical, Physical(addrAfter["city"]));
        // New child minted fresh; pre-order (its id exceeds every prior id and equals the new maxColumnId).
        long zipId = GetId(addrAfter["zip"]);
        Assert.True(zipId > maxBefore, "the new child id must exceed the prior maxColumnId");
        long maxAfter = long.Parse(config[ColumnMapping.MaxColumnIdKey]);
        Assert.True(maxAfter > maxBefore, "maxColumnId must strictly increase");
        Assert.Equal(zipId, maxAfter);
        Assert.NotEqual(cityPhysical, Physical(addrAfter["zip"])); // fresh physical name
    }

    // ---- §3.4 / §3.5 · Type agreement on the read exit ----

    [Fact]
    public async Task NameMode_NestedRead_BatchColumnType_EqualsLogicalSchemaFieldType_Exactly()
    {
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("addr", new StructType(new[]
            {
                new StructField("city", DataTypes.StringType, nullable: true),
                new StructField("zip", DataTypes.LongType, nullable: true),
            }), nullable: true),
        });
        var addr = new StructColumnVector(
            (StructType)schema["addr"].DataType,
            new ColumnVector[] { Str(new[] { "x" }), Long(new long?[] { 1L }) }, new[] { false });
        await CreateNameMappedAsync(schema, new ManagedColumnBatch(schema, new ColumnVector[] { Long(new long?[] { 1L }), addr }, 1));

        Snapshot snap = await LoadSnapshotAsync();
        ColumnBatch read = await ReadSingleBatchAsync();
        for (int i = 0; i < snap.Schema.Count; i++)
        {
            // The read-exit column type EQUALS the logical schema field type EXACTLY (names + per-child
            // metadata) — the typed inverse relabel (§2.5) restored the logical StructType, not the physical.
            Assert.True(
                read.Column(i).Type.Equals(snap.Schema[i].DataType),
                $"column {i} type must equal logical field type");
        }
    }

    // ---- §3.27 · Null / empty container round-trip fidelity ----

    [Fact]
    public async Task NameMode_NullAndEmptyContainers_RoundTripWithFullFidelity()
    {
        // null struct row, struct with all-null children, empty array, all-null array, empty map, map with
        // null values, and a null map — a relabel that alters a child's nullability corrupts these.
        var schema = new StructType(new[]
        {
            new StructField("s", new StructType(new[]
            {
                new StructField("a", DataTypes.LongType, nullable: true),
            }), nullable: true),
            new StructField("l", new ArrayType(DataTypes.LongType), nullable: true),
            new StructField("m", new MapType(DataTypes.StringType, DataTypes.LongType, valueContainsNull: true), nullable: true),
        });

        // rows: [ null struct, struct(null a) ] ; [ empty, all-null ] ; [ empty map, map(null value) ]
        var s = new StructColumnVector(
            (StructType)schema["s"].DataType, new ColumnVector[] { Long(new long?[] { null, null }) }, new[] { true, false });
        var l = LongList2((ArrayType)schema["l"].DataType, new long?[]?[] { Array.Empty<long?>(), new long?[] { null } });
        var m = StrLongMap2(
            (MapType)schema["m"].DataType,
            new[]
            {
                (IReadOnlyList<(string, long?)>)Array.Empty<(string, long?)>(),
                new[] { ("k", (long?)null) },
            });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { s, l, m }, 2);
        await CreateNameMappedAsync(schema, batch);

        ColumnBatch read = await ReadSingleBatchAsync();
        var rs = (StructColumnVector)read.Column(0);
        Assert.True(rs.IsNull(0));                 // null struct row
        Assert.False(rs.IsNull(1));                // present struct...
        Assert.True(rs.Child(0).IsNull(1));        // ...with a null child
        var rl = (ListColumnVector)read.Column(1);
        Assert.False(rl.IsNull(0));
        Assert.Equal(0, rl.ElementsAt(0).Length);  // empty list, distinct from null
        Assert.Equal(1, rl.ElementsAt(1).Length);
        Assert.True(rl.ElementsAt(1).IsNull(0));    // null element
        var rm = (MapColumnVector)read.Column(2);
        Assert.Equal(0, rm.KeysAt(0).Length);       // empty map
        Assert.Equal("k", Encoding.UTF8.GetString(rm.KeysAt(1).GetBytes(0)));
        Assert.True(rm.ValuesAt(1).IsNull(0));       // null value
    }

    // ---- §3.30 / §3.31 · Write byte-invariance ----

    [Fact]
    public async Task NameMode_NestedStructWrite_NoFieldIdOnAnyFooterLeaf()
    {
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("addr", new StructType(new[]
            {
                new StructField("city", DataTypes.StringType, nullable: true),
                new StructField("zip", DataTypes.LongType, nullable: true),
            }), nullable: true),
        });
        var addr = new StructColumnVector(
            (StructType)schema["addr"].DataType,
            new ColumnVector[] { Str(new[] { "x" }), Long(new long?[] { 1L }) }, new[] { false });
        await CreateNameMappedAsync(schema, new ManagedColumnBatch(schema, new ColumnVector[] { Long(new long?[] { 1L }), addr }, 1));

        Snapshot snap = await LoadSnapshotAsync();
        Dictionary<string, int> ids = await ReadParquetFieldIdsAsync(snap.ActiveFiles[0].Path);
        Assert.Empty(ids); // name mode: NO field_id on any footer leaf (a name-mode physical file is id-free)
    }

    [Fact]
    public async Task IdMode_NestedStructWrite_EveryStructChildLeafCarriesItsOwnFieldId()
    {
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("pt", new StructType(new[]
            {
                new StructField("a", DataTypes.LongType, nullable: true),
                new StructField("b", DataTypes.LongType, nullable: true),
            }), nullable: true),
        });
        var pt = new StructColumnVector(
            (StructType)schema["pt"].DataType,
            new ColumnVector[] { Long(new long?[] { 1000L }), Long(new long?[] { 2000L }) }, new[] { false });
        await CreateIdMappedAsync(schema, new ManagedColumnBatch(schema, new ColumnVector[] { Long(new long?[] { 1L }), pt }, 1));

        Snapshot snap = await LoadSnapshotAsync();
        var addrStruct = (StructType)snap.Schema["pt"].DataType;
        long aId = GetId(addrStruct["a"]);
        long bId = GetId(addrStruct["b"]);
        long idId = GetId(snap.Schema["id"]);

        Dictionary<string, int> ids = await ReadParquetFieldIdsAsync(snap.ActiveFiles[0].Path);
        // Every struct-child leaf (and the top-level scalar) carries ITS OWN field_id = the StructField's id;
        // the container GROUP node carries none (Parquet.Net exposes no setter).
        var stampedIds = ids.Values.OrderBy(x => x).ToArray();
        Assert.Equal(new[] { (int)idId, (int)aId, (int)bId }.OrderBy(x => x).ToArray(), stampedIds);
    }

    [Fact]
    public async Task NoneModeNested_And_NonNestedMapped_ByteUnchanged_MeasuredWithSha256()
    {
        // §3.31: writing a nested column in NONE mode (no column mapping) produces byte-identical output to a
        // baseline (an explicit pre/post SHA-256), and no nested statistics keys are emitted.
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("addr", new StructType(new[]
            {
                new StructField("city", DataTypes.StringType, nullable: true),
                new StructField("zip", DataTypes.LongType, nullable: true),
            }), nullable: true),
        });
        var addr = new StructColumnVector(
            (StructType)schema["addr"].DataType,
            new ColumnVector[] { Str(new[] { "x" }), Long(new long?[] { 1L }) }, new[] { false });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { Long(new long?[] { 1L }), addr }, 1);

        byte[] a = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { (ColumnBatch)batch });
        byte[] b = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { (ColumnBatch)batch });
        Assert.Equal(Convert.ToHexString(SHA256.HashData(a)), Convert.ToHexString(SHA256.HashData(b)));

        // No field_id in a none-mode footer.
        using var stream = new MemoryStream(a, writable: false);
        await using var reader = await global::Parquet.ParquetReader.CreateAsync(stream);
        foreach (global::Parquet.Meta.SchemaElement element in reader.Metadata!.Schema)
        {
            Assert.Null(element.FieldId);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    private async Task CreateNameMappedAsync(StructType schema, ColumnBatch batch)
        => await WriteRawNestedTableAsync(schema, batch, ColumnMappingMode.Name);

    private async Task CreateIdMappedAsync(StructType schema, ColumnBatch batch)
        => await WriteRawNestedTableAsync(schema, batch, ColumnMappingMode.Id);

    // Authors a single-commit nested Delta table end-to-end: the mapping is minted, the batch is written to a
    // REAL physical Parquet file (the merged nested writer, via ParquetTestHelpers.WriteToBytesAsync — the
    // high-level DeltaWriteTarget facade cannot encode nested-typed batches through its scalar partitioner), and
    // a raw protocol+metaData+add commit is assembled the way ColumnMappingTests authors id-mode read fixtures.
    private async Task<(StructType Mapped, long MaxColumnId)> WriteRawNestedTableAsync(
        StructType schema, ColumnBatch batch, ColumnMappingMode mode)
    {
        (StructType mapped, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(schema, new SeededPhysicalNameSource(Seed));
        StructType physical = ColumnMapping.MapWriteSchemaToPhysical(schema, mapped, mode);
        byte[] parquetBytes = await ParquetTestHelpers.WriteToBytesAsync(physical, new[] { RelabelBatch(batch, physical) });

        string schemaJson = DeltaSchemaJson.ToJson(mapped);
        string modeName = mode == ColumnMappingMode.Id ? "id" : "name";
        const string relativePath = "part-00000.parquet";

        using var backend = new LocalFileSystemBackend(_root);
        await backend.PutIfAbsentAsync(relativePath, parquetBytes, CancellationToken.None);
        string addLine =
            $"{{\"add\":{{\"path\":\"{relativePath}\",\"partitionValues\":{{}},"
            + $"\"size\":{parquetBytes.Length},\"modificationTime\":0,\"dataChange\":true}}}}";
        byte[] commit = Encoding.UTF8.GetBytes(
            ProtocolFeatureLine() + "\n"
            + NameModeMetadataLine(
                schemaJson,
                ("delta.columnMapping.mode", modeName),
                ("delta.columnMapping.maxColumnId", maxColumnId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            + "\n"
            + addLine + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", commit, CancellationToken.None);
        return (mapped, maxColumnId);
    }

    // Commits a metaData-ONLY v1 that renames a top-level field's LOGICAL display name while preserving its
    // physicalName + every id (a read-through logical rename, no data rewrite). The v0 add stays active.
    private async Task CommitTopLevelLogicalRenameAsync(
        StructType mapped, string fromName, string toName, ColumnMappingMode mode, long maxColumnId)
    {
        var renamed = new StructType(mapped.Select(f =>
            f.Name == fromName ? new StructField(toName, f.DataType, f.Nullable, f.Metadata) : f).ToList());
        string schemaJson = DeltaSchemaJson.ToJson(renamed);
        string modeName = mode == ColumnMappingMode.Id ? "id" : "name";
        byte[] commit = Encoding.UTF8.GetBytes(
            NameModeMetadataLine(
                schemaJson,
                ("delta.columnMapping.mode", modeName),
                ("delta.columnMapping.maxColumnId", maxColumnId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            + "\n");
        using var backend = new LocalFileSystemBackend(_root);
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000001.json", commit, CancellationToken.None);
    }

    // Rewraps a logical-named batch under the PHYSICAL schema so the writer (which cross-checks names) accepts
    // it: only STRUCT vectors carry field names, so only they need reconstruction; scalar leaves and array/map
    // interiors (unnamed in Delta) ride through unchanged. Nested-within-nested is rejected upstream, so struct
    // children are always scalar and never need recursion.
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

    private static string ProtocolFeatureLine() =>
        """{"protocol":{"minReaderVersion":3,"minWriterVersion":7,"readerFeatures":["columnMapping"],"writerFeatures":["columnMapping"]}}""";

    private static string NameModeMetadataLine(string schemaJson, params (string Key, string Value)[] configuration)
    {
        string escapedSchema = System.Text.Json.JsonSerializer.Serialize(schemaJson);
        string config = "{" + string.Join(",", configuration.Select(kv => $"\"{kv.Key}\":\"{kv.Value}\"")) + "}";
        return "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
            + "\"schemaString\":" + escapedSchema + ",\"partitionColumns\":[]"
            + ",\"configuration\":" + config + "}}";
    }

    private async Task<Snapshot> LoadSnapshotAsync()
    {
        using var backend = new LocalFileSystemBackend(_root);
        return await new DeltaLog(backend).LoadSnapshotAsync(version: null);
    }

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

    private async Task<Dictionary<string, int>> ReadParquetFieldIdsAsync(string relativePath)
    {
        await using FileStream stream = File.OpenRead(Path.Combine(_root, relativePath));
        await using var reader = await global::Parquet.ParquetReader.CreateAsync(stream);
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (global::Parquet.Meta.SchemaElement element in reader.Metadata!.Schema)
        {
            if (element.FieldId is int fieldId)
            {
                byName[element.Name] = fieldId;
            }
        }

        return byName;
    }

    private static void AssertNoMetadataUnderNestedInterior(string schemaJson)
    {
        // Walk the committed schemaString JSON: no "metadata" object may appear under any elementType/keyType/
        // valueType (C1 — mapping attaches to StructFields only). System.Text.Json parse + recursive scan.
        JsonNode? root = JsonNode.Parse(schemaJson);
        Assert.NotNull(root);
        AssertNoMetadataUnder(root!, false);

        static void AssertNoMetadataUnder(JsonNode node, bool underInterior)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (underInterior)
                    {
                        Assert.False(obj.ContainsKey("metadata"),
                            "no metadata object may appear under an array elementType / map keyType / valueType");
                    }

                    foreach (KeyValuePair<string, JsonNode?> kv in obj)
                    {
                        bool interior = kv.Key is "elementType" or "keyType" or "valueType";
                        if (kv.Value is not null)
                        {
                            AssertNoMetadataUnder(kv.Value, underInterior || interior);
                        }
                    }

                    break;
                case JsonArray arr:
                    foreach (JsonNode? item in arr)
                    {
                        if (item is not null)
                        {
                            AssertNoMetadataUnder(item, underInterior);
                        }
                    }

                    break;
            }
        }
    }

    private static long GetId(StructField field)
    {
        Assert.True(field.Metadata.TryGetLong(ColumnMapping.IdKey, out long id), $"field {field.Name} has no id");
        return id;
    }

    private static string Physical(StructField field)
    {
        Assert.True(field.Metadata.TryGetString(ColumnMapping.PhysicalNameKey, out string? p) && p is not null,
            $"field {field.Name} has no physicalName");
        return p!;
    }

    private static MutableColumnVector Long(long?[] values)
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

    private static MutableColumnVector Str(string?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, values.Length);
        foreach (string? value in values)
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

        return v;
    }

    private static ListColumnVector StrList(ArrayType type, IReadOnlyList<string[]> rows)
    {
        MutableColumnVector elements = ColumnVectors.Create(DataTypes.StringType, 16);
        var offsets = new int[rows.Count + 1];
        var nulls = new bool[rows.Count];
        int cursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            offsets[i] = cursor;
            foreach (string value in rows[i])
            {
                elements.AppendBytes(Encoding.UTF8.GetBytes(value));
                cursor++;
            }
        }

        offsets[rows.Count] = cursor;
        return new ListColumnVector(type, elements, offsets, nulls);
    }

    private static ListColumnVector LongList2(ArrayType type, IReadOnlyList<long?[]?> rows)
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
                    if (value is null)
                    {
                        elements.AppendNull();
                    }
                    else
                    {
                        elements.AppendValue(value.Value);
                    }

                    cursor++;
                }
            }
        }

        offsets[rows.Count] = cursor;
        return new ListColumnVector(type, elements, offsets, nulls);
    }

    private static MapColumnVector StrLongMap(MapType type, IReadOnlyList<IReadOnlyList<(string Key, long Value)>> rows)
    {
        var wrapped = rows.Select(r => (IReadOnlyList<(string, long?)>)r.Select(e => (e.Key, (long?)e.Value)).ToList()).ToList();
        return StrLongMap2(type, wrapped);
    }

    private static MapColumnVector StrLongMap2(MapType type, IReadOnlyList<IReadOnlyList<(string Key, long? Value)>?> rows)
    {
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.StringType, 16);
        MutableColumnVector values = ColumnVectors.Create(DataTypes.LongType, 16);
        var offsets = new int[rows.Count + 1];
        var nulls = new bool[rows.Count];
        int cursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            offsets[i] = cursor;
            IReadOnlyList<(string Key, long? Value)>? row = rows[i];
            nulls[i] = row is null;
            if (row is not null)
            {
                foreach ((string key, long? value) in row)
                {
                    keys.AppendBytes(Encoding.UTF8.GetBytes(key));
                    if (value is null)
                    {
                        values.AppendNull();
                    }
                    else
                    {
                        values.AppendValue(value.Value);
                    }

                    cursor++;
                }
            }
        }

        offsets[rows.Count] = cursor;
        return new MapColumnVector(type, keys, values, offsets, nulls);
    }
}
