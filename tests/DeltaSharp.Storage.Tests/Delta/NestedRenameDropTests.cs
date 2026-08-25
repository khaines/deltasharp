using System.Collections.Immutable;
using System.Globalization;
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
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// The §3 oracle for #840 — metadata-only rename/drop of a <b>nested struct child</b> addressed by a
/// <b>segment array</b> (never a dotted string). Covers the §3.1 conjunctive no-rewrite centerpiece,
/// read-through identity, drop-then-re-add fresh identity, top-level delegation, the segment-array addressing
/// contract, the F1–F10 fail-closed matrix (each asserting its DISTINCT §2.5 message SHAPE), the
/// rename-to-same-name no-op carve-out, the boundary-preserving diagnostic render, and the seeded property
/// harness.
/// </summary>
/// <remarks>
/// Because the write-facade cannot encode nested batches, the metadata-only round-trips author the nested
/// data files with the merged real nested writer (#834, via <see cref="ParquetTestHelpers.WriteToBytesAsync"/>)
/// paired with a hand-authored <c>_delta_log</c>, then exercise the writer's segment-array ALTER doors against
/// that committed table. Every same-typed sibling draws its values from a DISJOINT domain so a positional
/// mis-bind cannot pass on equal values.
/// </remarks>
[Collection(ColumnMappingTestCollection.Name)]
public sealed class NestedRenameDropTests : IDisposable
{
    private const string Seed = "nested-rename-drop-840";

    private readonly string _root;

    public NestedRenameDropTests() =>
        _root = Path.Combine(Path.GetTempPath(), "nestedrd-" + Guid.NewGuid().ToString("N"));

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

    // A {id:long, address:struct<city:string, zip:long>} table with two rows; disjoint domains so a mis-bind
    // is visible: id ∈ [1..], zip ∈ [90000..].
    private static StructType AddressSchema() => new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("address", new StructType(new[]
        {
            new StructField("city", DataTypes.StringType, nullable: true),
            new StructField("zip", DataTypes.LongType, nullable: true),
        }), nullable: true),
    });

    private static ManagedColumnBatch AddressBatch(StructType schema)
    {
        var city = Str(new[] { "seattle", "portland" });
        var zip = Long(new long?[] { 90001L, 90002L });
        var address = new StructColumnVector(
            (StructType)schema["address"].DataType, new ColumnVector[] { city, zip }, new[] { false, false });
        return new ManagedColumnBatch(
            schema, new ColumnVector[] { Long(new long?[] { 1L, 2L }), address }, 2);
    }

    // ================================================================ §3.1 — centerpiece (conjunctive)

    [Fact]
    public async Task NestedStructChildRename_NameMode_IsMetadataOnly_NoRewrite()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));

        Snapshot before = await LoadSnapshotAsync();
        Dictionary<string, string> shaBefore = await Sha256OfActiveFilesAsync(before);
        StructField zipBefore = ChildField(before.Schema, "address", "zip");
        string zipPhysicalBefore = ColumnMapping.PhysicalName(zipBefore, ColumnMappingMode.Name);
        Assert.True(ColumnMapping.TryGetId(zipBefore, out long zipIdBefore));
        string maxColumnIdBefore = before.Metadata.Configuration[ColumnMapping.MaxColumnIdKey];

        // Rename address.zip -> address.postal_code, addressed by a SEGMENT ARRAY.
        using var backend = new LocalFileSystemBackend(_root);
        DeltaCommitResult result = await new DeltaTableWriter(backend)
            .RenameColumnAsync(new[] { "address", "zip" }, "postal_code");
        Assert.Equal(1L, result.Version);

        // (a) exactly one metaData action ∧ zero add/remove in the commit.
        Dictionary<string, int> actions = await CommitActionKindsAsync(1);
        Assert.Equal(1, actions.GetValueOrDefault("metaData"));
        Assert.Equal(0, actions.GetValueOrDefault("add"));
        Assert.Equal(0, actions.GetValueOrDefault("remove"));

        Snapshot after = await LoadSnapshotAsync();

        // (b) SHA-256 of every data-file byte identical pre/post.
        Dictionary<string, string> shaAfter = await Sha256OfActiveFilesAsync(after);
        Assert.Equal(shaBefore, shaAfter);

        // (c) each AddFile's (path, size, modificationTime, stats, partitionValues) identical pre/post.
        Assert.Equal(before.ActiveFiles.Length, after.ActiveFiles.Length);
        for (int i = 0; i < before.ActiveFiles.Length; i++)
        {
            AddFileAction a = before.ActiveFiles[i];
            AddFileAction b = after.ActiveFiles[i];
            Assert.Equal(a.Path, b.Path);
            Assert.Equal(a.Size, b.Size);
            Assert.Equal(a.ModificationTime, b.ModificationTime);
            Assert.Equal(a.Stats, b.Stats);
            Assert.Equal(a.PartitionValues, b.PartitionValues);
        }

        // (d) maxColumnId unchanged; the renamed child keeps id + physicalName verbatim.
        Assert.Equal(maxColumnIdBefore, after.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
        StructField postalAfter = ChildField(after.Schema, "address", "postal_code");
        Assert.Equal(zipPhysicalBefore, ColumnMapping.PhysicalName(postalAfter, ColumnMappingMode.Name));
        Assert.True(ColumnMapping.TryGetId(postalAfter, out long postalId));
        Assert.Equal(zipIdBefore, postalId);
        Assert.False(((StructType)after.Schema["address"].DataType).TryGetField("zip", out _));

        // (e) post-read returns the same values under the new logical name.
        ColumnBatch read = await ReadSingleBatchAsync();
        var readAddr = (StructColumnVector)read.Column(1);
        Assert.Equal("seattle", Encoding.UTF8.GetString(readAddr.Child(0).GetBytes(0)));
        Assert.Equal("portland", Encoding.UTF8.GetString(readAddr.Child(0).GetBytes(1)));
        Assert.Equal(90001L, readAddr.Child(1).GetValue<long>(0));
        Assert.Equal(90002L, readAddr.Child(1).GetValue<long>(1));
    }

    [Fact]
    public async Task NestedStructChildDrop_NameMode_IsMetadataOnly_NoRewrite()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));

        Snapshot before = await LoadSnapshotAsync();
        Dictionary<string, string> shaBefore = await Sha256OfActiveFilesAsync(before);
        string maxColumnIdBefore = before.Metadata.Configuration[ColumnMapping.MaxColumnIdKey];

        using var backend = new LocalFileSystemBackend(_root);
        DeltaCommitResult result = await new DeltaTableWriter(backend)
            .DropColumnAsync(new[] { "address", "zip" });
        Assert.Equal(1L, result.Version);

        Dictionary<string, int> actions = await CommitActionKindsAsync(1);
        Assert.Equal(1, actions.GetValueOrDefault("metaData"));
        Assert.Equal(0, actions.GetValueOrDefault("add"));
        Assert.Equal(0, actions.GetValueOrDefault("remove"));

        Snapshot after = await LoadSnapshotAsync();
        Assert.Equal(shaBefore, await Sha256OfActiveFilesAsync(after));
        Assert.Equal(maxColumnIdBefore, after.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);

        // AddFile facts identical pre/post.
        for (int i = 0; i < before.ActiveFiles.Length; i++)
        {
            Assert.Equal(before.ActiveFiles[i].Path, after.ActiveFiles[i].Path);
            Assert.Equal(before.ActiveFiles[i].Size, after.ActiveFiles[i].Size);
            Assert.Equal(before.ActiveFiles[i].ModificationTime, after.ActiveFiles[i].ModificationTime);
        }

        // The dropped child is absent from the logical schema; the surviving sibling still reads through.
        var addrAfter = (StructType)after.Schema["address"].DataType;
        Assert.False(addrAfter.TryGetField("zip", out _));
        Assert.True(addrAfter.TryGetField("city", out _));

        ColumnBatch read = await ReadSingleBatchAsync();
        var readAddr = (StructColumnVector)read.Column(1);
        Assert.Equal("seattle", Encoding.UTF8.GetString(readAddr.Child(0).GetBytes(0)));
        Assert.Equal("portland", Encoding.UTF8.GetString(readAddr.Child(0).GetBytes(1)));
    }

    // ================================================================ §3.2/3.3 — read-through identity

    [Fact]
    public async Task NestedStructChildRename_OldFileReadsThroughById_ZeroRewrite()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));

        Snapshot before = await LoadSnapshotAsync();
        StructField zipBefore = ChildField(before.Schema, "address", "zip");
        string dataFileBefore = before.ActiveFiles[0].Path;

        using var backend = new LocalFileSystemBackend(_root);
        await new DeltaTableWriter(backend).RenameColumnAsync(new[] { "address", "zip" }, "postal_code");

        Snapshot after = await LoadSnapshotAsync();
        // The OLD (v0) data file stays active — no rewrite — and the renamed child preserves id+physicalName.
        Assert.Equal(dataFileBefore, after.ActiveFiles[0].Path);
        StructField postalAfter = ChildField(after.Schema, "address", "postal_code");
        Assert.Equal(
            ColumnMapping.PhysicalName(zipBefore, ColumnMappingMode.Name),
            ColumnMapping.PhysicalName(postalAfter, ColumnMappingMode.Name));

        // The old file reads through under the NEW logical name (resolves by the preserved physicalName).
        ColumnBatch read = await ReadSingleBatchAsync();
        var readAddr = (StructColumnVector)read.Column(1);
        Assert.Equal(90001L, readAddr.Child(1).GetValue<long>(0));
        Assert.Equal(90002L, readAddr.Child(1).GetValue<long>(1));
    }

    [Fact]
    public async Task NestedStructChildRename_PreservesIdAndPhysicalName_OnlyLogicalNameChanges()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));

        Snapshot before = await LoadSnapshotAsync();
        StructField zipBefore = ChildField(before.Schema, "address", "zip");
        Assert.True(ColumnMapping.TryGetId(zipBefore, out long idBefore));
        string physBefore = ColumnMapping.PhysicalName(zipBefore, ColumnMappingMode.Name);

        using var backend = new LocalFileSystemBackend(_root);
        await new DeltaTableWriter(backend).RenameColumnAsync(new[] { "address", "zip" }, "postal_code");

        Snapshot after = await LoadSnapshotAsync();
        StructField postalAfter = ChildField(after.Schema, "address", "postal_code");
        Assert.True(ColumnMapping.TryGetId(postalAfter, out long idAfter));
        Assert.Equal(idBefore, idAfter); // id verbatim
        Assert.Equal(physBefore, ColumnMapping.PhysicalName(postalAfter, ColumnMappingMode.Name)); // physicalName verbatim
        Assert.Equal("postal_code", postalAfter.Name); // only the logical Name changed
        Assert.Equal(zipBefore.DataType, postalAfter.DataType); // DataType verbatim
        Assert.Equal(zipBefore.Nullable, postalAfter.Nullable); // Nullable verbatim
    }

    // ================================================================ §3.4 — drop then re-add mints fresh id

    [Fact]
    public async Task NestedStructChildDrop_ThenReAddSameLogicalName_MintsFreshId_MaxColumnIdStrictlyIncreases_OldDataDoesNotSurface()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));

        Snapshot v0 = await LoadSnapshotAsync();
        StructField zipV0 = ChildField(v0.Schema, "address", "zip");
        string zipPhysicalV0 = ColumnMapping.PhysicalName(zipV0, ColumnMappingMode.Name);
        Assert.True(ColumnMapping.TryGetId(zipV0, out long zipIdV0));
        long maxV0 = long.Parse(v0.Metadata.Configuration[ColumnMapping.MaxColumnIdKey], CultureInfo.InvariantCulture);

        // Drop address.zip (v1).
        using (var backend = new LocalFileSystemBackend(_root))
        {
            await new DeltaTableWriter(backend).DropColumnAsync(new[] { "address", "zip" });
        }

        Snapshot v1 = await LoadSnapshotAsync();
        Assert.False(((StructType)v1.Schema["address"].DataType).TryGetField("zip", out _));
        // maxColumnId is UNCHANGED by the drop (a dropped id is never reused).
        Assert.Equal(maxV0, long.Parse(v1.Metadata.Configuration[ColumnMapping.MaxColumnIdKey], CultureInfo.InvariantCulture));

        // Re-add a NEW address.zip (same logical name) with a FRESH id + physicalName and a strictly-increasing
        // maxColumnId — the shape a schema-evolution append would mint. Authored as a metadata-only v2 commit.
        long reAddedId = maxV0 + 1;
        const string reAddedPhysical = "col-readded-zip";
        var addrV1 = (StructType)v1.Schema["address"].DataType;
        var reAddedZip = new StructField(
            "zip",
            DataTypes.LongType,
            nullable: true,
            FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(reAddedId)),
                new KeyValuePair<string, MetadataValue>(ColumnMapping.PhysicalNameKey, MetadataValue.String(reAddedPhysical)),
            }));
        var addrV2 = new StructType(addrV1.Concat(new[] { reAddedZip }));
        var schemaV2 = new StructType(v1.Schema.Select(f =>
            f.Name == "address" ? new StructField("address", addrV2, f.Nullable, f.Metadata) : f));
        await AppendMetadataOnlyCommitAsync(
            2,
            DeltaSchemaJson.ToJson(schemaV2),
            ("delta.columnMapping.mode", "name"),
            ("delta.columnMapping.maxColumnId", reAddedId.ToString(CultureInfo.InvariantCulture)));

        Snapshot v2 = await LoadSnapshotAsync();
        StructField zipV2 = ChildField(v2.Schema, "address", "zip");
        // Fresh identity: different physicalName + different id + strictly greater maxColumnId. Because the
        // re-added child's physicalName DIVERGES from the dropped child's, NO byte of the old v0 data file maps
        // to the re-added logical name — the dropped column's stale data can never surface under it (the
        // soundness anchor).
        Assert.NotEqual(zipPhysicalV0, ColumnMapping.PhysicalName(zipV2, ColumnMappingMode.Name));
        Assert.Equal(reAddedPhysical, ColumnMapping.PhysicalName(zipV2, ColumnMappingMode.Name));
        Assert.True(ColumnMapping.TryGetId(zipV2, out long zipIdV2));
        Assert.NotEqual(zipIdV0, zipIdV2);
        long maxV2 = long.Parse(v2.Metadata.Configuration[ColumnMapping.MaxColumnIdKey], CultureInfo.InvariantCulture);
        Assert.True(maxV2 > maxV0, $"maxColumnId must strictly increase: {maxV0} -> {maxV2}");

        // AC4 (#857): the #840 §3.4 deferral is now CLOSED — read the OLD v0 data file through the v2 schema
        // and assert the re-added address.zip reads NULL for the old rows. Its NEW physicalName
        // ('col-readded-zip') is absent from the old file (whose zip was written under the DROPPED physical
        // name), so the nested reader null-fills it (drop-then-re-add read-back), while the surviving
        // address.city still reads its real values — the metadata-level identity assertion plus a real
        // read-through, no longer deferred.
        ColumnBatch read = await ReadSingleBatchAsync();
        var readAddr = (StructColumnVector)read.Column(1);
        Assert.False(readAddr.IsNull(0));
        Assert.False(readAddr.IsNull(1));
        Assert.Equal("seattle", Encoding.UTF8.GetString(readAddr.Child(0).GetBytes(0)));  // city index 0
        Assert.Equal("portland", Encoding.UTF8.GetString(readAddr.Child(0).GetBytes(1)));
        Assert.True(readAddr.Child(1).IsNull(0)); // zip index 1 → NULL (re-added physical name absent)
        Assert.True(readAddr.Child(1).IsNull(1));
    }

    // ================================================================ §3.2 — AC2 drop→re-add data read-back

    [Fact]
    public async Task NestedStructChildDrop_ThenReAdd_OldFileRows_ReadBackNull_NewRows_ReadValues()
    {
        // The end-to-end #840 §3.4 scenario as a REAL read (AC2): (v0) write struct<city, zip> with real zip
        // values; (v1) drop address.zip; (v2) re-add address.zip (fresh id + physicalName) as a metadata-only
        // commit; (v3) append a NEW data file that physically carries the re-added zip's new physical name with
        // values. Reading the v3 table: rows from the OLD (v0) file read address.zip as NULL (its new physical
        // name is absent → null-fill), while rows from the NEW file read the NEW values — the pre/post split.
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));

        Snapshot v0 = await LoadSnapshotAsync();
        string cityPhysical = ColumnMapping.PhysicalName(ChildField(v0.Schema, "address", "city"), ColumnMappingMode.Name);
        long maxV0 = long.Parse(v0.Metadata.Configuration[ColumnMapping.MaxColumnIdKey], CultureInfo.InvariantCulture);

        // Drop address.zip (v1).
        using (var backend = new LocalFileSystemBackend(_root))
        {
            await new DeltaTableWriter(backend).DropColumnAsync(new[] { "address", "zip" });
        }

        Snapshot v1 = await LoadSnapshotAsync();

        // Re-add a NEW address.zip (same logical name) with a FRESH id + physicalName (v2, metadata-only).
        long reAddedId = maxV0 + 1;
        const string reAddedPhysical = "col-readded-zip";
        var addrV1 = (StructType)v1.Schema["address"].DataType;
        var reAddedZip = new StructField(
            "zip",
            DataTypes.LongType,
            nullable: true,
            FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(reAddedId)),
                new KeyValuePair<string, MetadataValue>(ColumnMapping.PhysicalNameKey, MetadataValue.String(reAddedPhysical)),
            }));
        var addrV2 = new StructType(addrV1.Concat(new[] { reAddedZip }));
        var schemaV2 = new StructType(v1.Schema.Select(f =>
            f.Name == "address" ? new StructField("address", addrV2, f.Nullable, f.Metadata) : f));
        await AppendMetadataOnlyCommitAsync(
            2,
            DeltaSchemaJson.ToJson(schemaV2),
            ("delta.columnMapping.mode", "name"),
            ("delta.columnMapping.maxColumnId", reAddedId.ToString(CultureInfo.InvariantCulture)));

        Snapshot v2 = await LoadSnapshotAsync();

        // (v3) Append a NEW data file that physically carries the re-added zip under its NEW physical name.
        string[] physicalNames = ColumnMappingProjection.ResolvePhysicalNames(v2.Schema, ColumnMappingMode.Name);
        StructType physicalSchema = ColumnMappingProjection.BuildDataSchema(
            v2.Schema, physicalNames, ImmutableArray<string>.Empty);
        Assert.Equal(reAddedPhysical, ((StructType)physicalSchema[1].DataType)[1].Name); // sanity: zip → new phys
        Assert.Equal(cityPhysical, ((StructType)physicalSchema[1].DataType)[0].Name);    // city keeps v0 phys

        ColumnBatch newBatch = NewFileBatch(physicalSchema);
        byte[] newParquet = await ParquetTestHelpers.WriteToBytesAsync(physicalSchema, new[] { newBatch });
        const string newRelative = "part-00001.parquet";
        using (var backend = new LocalFileSystemBackend(_root))
        {
            await backend.PutIfAbsentAsync(newRelative, newParquet, CancellationToken.None);
            byte[] commit = Encoding.UTF8.GetBytes(
                $"{{\"add\":{{\"path\":\"{newRelative}\",\"partitionValues\":{{}},"
                + $"\"size\":{newParquet.Length},\"modificationTime\":0,\"dataChange\":true}}}}\n");
            await backend.PutIfAbsentAsync("_delta_log/00000000000000000003.json", commit, CancellationToken.None);
        }

        // Read the v3 table: two active files (old v0, new v3). Assert the pre/post split on address.zip.
        var addressByFile = new List<StructColumnVector>();
        var cityByFile = new List<string>();
        var zipNullByFile = new List<bool>();
        foreach (ColumnBatch batch in await ReadAllBatchesAsync())
        {
            var addr = (StructColumnVector)batch.Column(1);
            for (int r = 0; r < addr.Length; r++)
            {
                addressByFile.Add(addr);
                cityByFile.Add(addr.Child(0).IsNull(r) ? "<null>" : Encoding.UTF8.GetString(addr.Child(0).GetBytes(r)));
                zipNullByFile.Add(addr.Child(1).IsNull(r));
            }
        }

        // Old (v0) rows: seattle/portland with zip NULL (re-added physical name absent → null-fill).
        // New (v3) row: denver with zip 90003 (present under the new physical name).
        Assert.Contains("seattle", cityByFile);
        Assert.Contains("portland", cityByFile);
        Assert.Contains("denver", cityByFile);

        // Every city that is an old-file row (seattle/portland) read zip NULL; denver read a real zip value.
        for (int i = 0; i < cityByFile.Count; i++)
        {
            if (cityByFile[i] is "seattle" or "portland")
            {
                Assert.True(zipNullByFile[i], $"old-file row '{cityByFile[i]}' must read zip NULL");
            }
            else if (cityByFile[i] == "denver")
            {
                Assert.False(zipNullByFile[i], "new-file row 'denver' must read the new zip value");
            }
        }

        // Confirm the new-file denver row carries the actual re-added value 90003.
        bool sawDenverValue = false;
        foreach (ColumnBatch batch in await ReadAllBatchesAsync())
        {
            var addr = (StructColumnVector)batch.Column(1);
            for (int r = 0; r < addr.Length; r++)
            {
                if (!addr.Child(0).IsNull(r) && Encoding.UTF8.GetString(addr.Child(0).GetBytes(r)) == "denver")
                {
                    Assert.False(addr.Child(1).IsNull(r));
                    Assert.Equal(90003L, addr.Child(1).GetValue<long>(r));
                    sawDenverValue = true;
                }
            }
        }

        Assert.True(sawDenverValue, "expected to observe the new-file denver row's zip value");
    }

    // A one-row new-file batch (id=3, address={city='denver', zip=90003}) under the PHYSICAL schema.
    private static ManagedColumnBatch NewFileBatch(StructType physicalSchema)
    {
        var addrType = (StructType)physicalSchema[1].DataType;
        var city = Str(new[] { "denver" });
        var zip = Long(new long?[] { 90003L });
        var address = new StructColumnVector(addrType, new ColumnVector[] { city, zip }, new[] { false });
        return new ManagedColumnBatch(
            physicalSchema, new ColumnVector[] { Long(new long?[] { 3L }), address }, 1);
    }

    private async Task<IReadOnlyList<ColumnBatch>> ReadAllBatchesAsync()
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        var batches = new List<ColumnBatch>();
        foreach (ColumnBatch b in await source.ReadBatchesAsync(info.Version))
        {
            batches.Add(b);
        }

        return batches;
    }

    // ================================================================ §3.6/3.7/3.8 — segment-array addressing

    [Fact]
    public async Task TopLevelOverload_DelegatesToSingleSegmentPath_BehaviorByteIdentical()
    {
        // The retained flat `string` overload produces a commit identical to the length-1 path overload.
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));

        using var backend = new LocalFileSystemBackend(_root);
        await new DeltaTableWriter(backend).RenameColumnAsync("id", "identifier"); // flat overload
        Snapshot flat = await LoadSnapshotAsync();
        string flatSchemaJson = flat.Metadata.SchemaString;

        // Reset and repeat via the explicit length-1 path overload.
        Directory.Delete(_root, recursive: true);
        StructType schema2 = AddressSchema();
        await WriteNestedNameMappedAsync(schema2, AddressBatch(schema2));
        using var backend2 = new LocalFileSystemBackend(_root);
        await new DeltaTableWriter(backend2).RenameColumnAsync(new[] { "id" }, "identifier"); // path overload
        Snapshot path = await LoadSnapshotAsync();

        Assert.Equal(flatSchemaJson, path.Metadata.SchemaString);
        Assert.Equal("identifier", path.Schema[0].Name);
    }

    [Fact]
    public async Task SegmentArrayNeverComposesDottedString_DotInNameIsOneSegment()
    {
        // A top-level column LITERALLY named "a.b" (a legal logical name with an embedded dot) plus a nested
        // child under it. Addressing is a segment array, so ["a.b"] renames the WHOLE dotted-name column and
        // ["a.b","child"] addresses its nested child — a dotted "a.b.child" would be ambiguous and is never
        // split/accepted.
        var schema = new StructType(new[]
        {
            new StructField("a.b", new StructType(new[]
            {
                new StructField("child", DataTypes.LongType, nullable: true),
            }), nullable: true),
        });
        var child = Long(new long?[] { 42L });
        var ab = new StructColumnVector((StructType)schema["a.b"].DataType, new ColumnVector[] { child }, new[] { false });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { ab }, 1);
        await WriteNestedNameMappedAsync(schema, batch);

        using var backend = new LocalFileSystemBackend(_root);
        // Rename the nested child under the dotted-name column: ["a.b","child"] — one struct hop, one target.
        await new DeltaTableWriter(backend).RenameColumnAsync(new[] { "a.b", "child" }, "kid");

        Snapshot after = await LoadSnapshotAsync();
        var ab2 = (StructType)after.Schema["a.b"].DataType;
        Assert.True(ab2.TryGetField("kid", out _));
        Assert.False(ab2.TryGetField("child", out _));

        // And the whole dotted-name column is renamable as a 1-segment path.
        await new DeltaTableWriter(backend).RenameColumnAsync(new[] { "a.b" }, "c.d");
        Snapshot after2 = await LoadSnapshotAsync();
        Assert.True(after2.Schema.TryGetField("c.d", out _));
        Assert.False(after2.Schema.TryGetField("a.b", out _));
    }

    [Fact]
    public void DescendAndRebuild_SiblingsCarriedByReference_SpineRebuiltFresh()
    {
        // ReferenceEquals identity: only the target and its ancestor spine are new instances; untouched
        // siblings are reference-identical. Exercised directly on the pure transform (no I/O).
        var city = new StructField("city", DataTypes.StringType);
        var zip = new StructField("zip", DataTypes.LongType);
        var address = new StructField("address", new StructType(new[] { city, zip }));
        var id = new StructField("id", DataTypes.LongType, nullable: false);
        var other = new StructField("other", DataTypes.LongType);
        var schema = new StructType(new[] { id, address, other });

        StructType rebuilt = DeltaTableWriter.DescendAndRebuild(
            schema, new[] { "address", "zip" }, DeltaTableWriter.SchemaChangeOp.Rename, "postal_code");

        // Untouched top-level siblings are reference-identical.
        Assert.Same(id, rebuilt[0]);
        Assert.Same(other, rebuilt[2]);
        // The ancestor on the spine is a FRESH instance.
        Assert.NotSame(address, rebuilt[1]);
        var rebuiltAddr = (StructType)rebuilt[1].DataType;
        // The untouched nested sibling (city) is reference-identical; the target (zip) is replaced.
        Assert.Same(city, rebuiltAddr[0]);
        Assert.NotSame(zip, rebuiltAddr[1]);
        Assert.Equal("postal_code", rebuiltAddr[1].Name);
    }

    // ================================================================ §3.9–3.16 — fail-closed matrix

    [Fact]
    public async Task EmptyPath_FailsClosed_F1()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema)); // name-mode, so F5 does not pre-empt
        using var backend = new LocalFileSystemBackend(_root);

        var rename = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).RenameColumnAsync(Array.Empty<string>(), "x"));
        Assert.Contains("empty column path", rename.Message, StringComparison.Ordinal);

        var drop = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).DropColumnAsync(Array.Empty<string>()));
        Assert.Contains("empty column path", drop.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonExistentSegment_AtTarget_FailsClosed_F2()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));
        using var backend = new LocalFileSystemBackend(_root);

        // Present intermediate ("address" exists as a struct) so F3/F4/F4b cannot pre-empt; missing target.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).RenameColumnAsync(new[] { "address", "nope" }, "x"));
        Assert.Contains("no such field", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[\"address\"]", ex.Message, StringComparison.Ordinal); // names the sanitized partial path
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonExistentSegment_AtIntermediate_FailsClosed_F2()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));
        using var backend = new LocalFileSystemBackend(_root);

        // ["address","missing","leaf"]: address exists (struct); "missing" is an absent INTERMEDIATE segment.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).DropColumnAsync(new[] { "address", "missing", "leaf" }));
        Assert.Contains("no such field", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[\"address\"]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntermediateSegmentIsScalar_FailsClosed_F3()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));
        using var backend = new LocalFileSystemBackend(_root);

        // ["address","zip","deeper"]: address (struct) -> zip (SCALAR) is an intermediate that is not a struct.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).RenameColumnAsync(new[] { "address", "zip", "deeper" }, "x"));
        Assert.Contains("is not a", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot descend", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntermediateSegmentIsArray_FailsClosed_Naming585_F4()
    {
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("tags", new ArrayType(DataTypes.StringType), nullable: true),
        });
        var tags = StrList((ArrayType)schema["tags"].DataType, new[] { new[] { "a" } });
        await WriteNestedNameMappedAsync(schema, new ManagedColumnBatch(
            schema, new ColumnVector[] { Long(new long?[] { 1L }), tags }, 1));
        using var backend = new LocalFileSystemBackend(_root);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).DropColumnAsync(new[] { "tags", "element" }));
        Assert.Contains("array element / map key/value", ex.Message, StringComparison.Ordinal);
        Assert.Contains("#585", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntermediateSegmentIsMap_FailsClosed_Naming585_F4()
    {
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("props", new MapType(DataTypes.StringType, DataTypes.LongType), nullable: true),
        });
        var props = StrLongMap((MapType)schema["props"].DataType, new[] { new[] { ("w", 1L) } });
        await WriteNestedNameMappedAsync(schema, new ManagedColumnBatch(
            schema, new ColumnVector[] { Long(new long?[] { 1L }), props }, 1));
        using var backend = new LocalFileSystemBackend(_root);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).RenameColumnAsync(new[] { "props", "value" }, "x"));
        Assert.Contains("array element / map key/value", ex.Message, StringComparison.Ordinal);
        Assert.Contains("#585", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StructWithinStructIntermediate_FailsClosed_Naming585_F4b()
    {
        // A SECOND struct hop (struct<struct<…>> intermediate) is caught by NEITHER F3 (scalar) NOR F4
        // (array/map). This cell is UNCONSTRUCTIBLE via a loaded snapshot today — the load-time C1 gate
        // ColumnMapping.RejectNestedWithinNested rejects the struct<struct<…>> schema before it can be loaded
        // — so the test targets the door's single-hop gate DIRECTLY with a hand-built StructType that bypasses
        // the load door (§2.4/§3.12). A companion loaded-snapshot integration cell is deferred: pending #585.
        var schema = new StructType(new[]
        {
            new StructField("a", new StructType(new[]
            {
                new StructField("b", new StructType(new[]
                {
                    new StructField("c", DataTypes.LongType),
                })),
            })),
        });

        var ex = Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
            schema, new[] { "a", "b", "c" }, DeltaTableWriter.SchemaChangeOp.Rename, "x"));
        Assert.Contains("nested-within-nested", ex.Message, StringComparison.Ordinal);
        Assert.Contains("#585", ex.Message, StringComparison.Ordinal);
        Assert.Contains("single-level nested child", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdMode_NestedRename_FailsClosed_RequireNameMode_F5()
    {
        StructType schema = AddressSchema();
        await WriteNestedIdMappedAsync(schema, AddressBatch(schema));
        using var backend = new LocalFileSystemBackend(_root);

        var rename = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).RenameColumnAsync(new[] { "address", "zip" }, "postal_code"));
        Assert.Contains("name' mode", rename.Message, StringComparison.Ordinal);

        var drop = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).DropColumnAsync(new[] { "address", "zip" }));
        Assert.Contains("name' mode", drop.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_CollidesWithSibling_CaseSensitive_FailsClosed_AtDoor_F6a()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));
        using var backend = new LocalFileSystemBackend(_root);

        // Rename address.city -> "zip": the last-segment Name ("city") DIFFERS from toName ("zip") so the
        // same-name carve-out does not apply, and a sibling ordinally equal to "zip" exists → F6a at the door.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).RenameColumnAsync(new[] { "address", "city" }, "zip"));
        Assert.Contains("already exists at this level", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_CollidesWithSibling_OrdinalIgnoreCase_FailsClosed_AtCommit_F6b()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));
        using var backend = new LocalFileSystemBackend(_root);

        // Rename address.zip -> "CITY": ordinally distinct from sibling "city" (no F6a at the door), but the
        // committer's recursive per-level case-insensitive guard rejects struct<city, CITY> at COMMIT.
        var ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaTableWriter(backend).RenameColumnAsync(new[] { "address", "zip" }, "CITY"));
        Assert.Contains("collides case-insensitively", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_ToSameName_IsNoOp_CollisionSkipped_F6a_CarveOut()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));
        Snapshot before = await LoadSnapshotAsync();
        string maxBefore = before.Metadata.Configuration[ColumnMapping.MaxColumnIdKey];

        using var backend = new LocalFileSystemBackend(_root);
        // ["address","zip"] -> "zip": the same-name carve-out skips F6a and commits a no-op metaData.
        DeltaCommitResult result = await new DeltaTableWriter(backend)
            .RenameColumnAsync(new[] { "address", "zip" }, "zip");
        Assert.Equal(1L, result.Version);

        Dictionary<string, int> actions = await CommitActionKindsAsync(1);
        Assert.Equal(1, actions.GetValueOrDefault("metaData"));
        Assert.Equal(0, actions.GetValueOrDefault("add"));
        Assert.Equal(0, actions.GetValueOrDefault("remove"));

        Snapshot after = await LoadSnapshotAsync();
        Assert.Equal(maxBefore, after.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
        Assert.True(((StructType)after.Schema["address"].DataType).TryGetField("zip", out _));
    }

    [Fact]
    public async Task Drop_TopLevelPartitionColumn_FailsClosed_F7()
    {
        // Partition columns are scalar top-level, so this uses the scalar facade (F7 is the retained flat guard,
        // reached through the segment-array door via delegation).
        var schema = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        });
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames()))
        {
            MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, 1);
            MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 1);
            region.AppendBytes(Encoding.UTF8.GetBytes("us"));
            id.AppendValue(7L);
            await target.CreateNameMappedTableAsync(
                schema, new[] { "region" },
                new[] { (ColumnBatch)new ManagedColumnBatch(schema, new ColumnVector[] { region, id }, 1) },
                new SeededPhysicalNameSource(Seed));
        }

        using var backend = new LocalFileSystemBackend(_root);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).DropColumnAsync(new[] { "region" }));
        Assert.Contains("partition column", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_TopLevelPartitionColumn_UpdatesPartitionColumnsLogicalName_MetadataOnly()
    {
        var schema = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        });
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames()))
        {
            MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, 1);
            MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 1);
            region.AppendBytes(Encoding.UTF8.GetBytes("us"));
            id.AppendValue(7L);
            await target.CreateNameMappedTableAsync(
                schema, new[] { "region" },
                new[] { (ColumnBatch)new ManagedColumnBatch(schema, new ColumnVector[] { region, id }, 1) },
                new SeededPhysicalNameSource(Seed));
        }

        using var backend = new LocalFileSystemBackend(_root);
        await new DeltaTableWriter(backend).RenameColumnAsync(new[] { "region" }, "zone");

        Snapshot after = await LoadSnapshotAsync();
        Assert.Equal(new[] { "zone" }, after.Metadata.PartitionColumns.ToArray());
        Assert.Equal("zone", after.Schema[0].Name);
    }

    [Fact]
    public async Task DependentCheckOnNestedField_Rename_FailsClosed_AsDependentColumnChange_F10()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(
            schema, AddressBatch(schema),
            extraConfig: new[] { ("delta.constraints.zip_positive", "address.zip > 0") });

        using var backend = new LocalFileSystemBackend(_root);
        var enforcer = new RecordingConstraintEnforcer(reject: true);
        await Assert.ThrowsAsync<DeltaConstraintDependentColumnException>(
            () => new DeltaTableWriter(backend).RenameColumnAsync(new[] { "address", "zip" }, "postal_code", enforcer));

        // The guard ran over the POST-RENAME schema with the surviving CHECK and NO batches.
        Assert.Equal(1, enforcer.Calls);
        Assert.Empty(enforcer.Batches!);
        Assert.Equal("zip_positive", Assert.Single(enforcer.Constraints!).Name);
    }

    [Fact]
    public async Task DependentCheckOnNestedField_Drop_FailsClosed_AsDependentColumnChange_F10()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(
            schema, AddressBatch(schema),
            extraConfig: new[] { ("delta.constraints.zip_positive", "address.zip > 0") });

        using var backend = new LocalFileSystemBackend(_root);
        var enforcer = new RecordingConstraintEnforcer(reject: true);
        await Assert.ThrowsAsync<DeltaConstraintDependentColumnException>(
            () => new DeltaTableWriter(backend).DropColumnAsync(new[] { "address", "zip" }, enforcer));
        Assert.Equal(1, enforcer.Calls);
    }

    [Fact]
    public async Task EnsureNoDependentConstraints_NoEnforcerButActiveConstraints_FailsClosed_F10()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(
            schema, AddressBatch(schema),
            extraConfig: new[] { ("delta.constraints.zip_positive", "address.zip > 0") });

        using var backend = new LocalFileSystemBackend(_root);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).DropColumnAsync(new[] { "address", "zip" }, constraintEnforcer: null));
        Assert.Equal(0L, (await LoadSnapshotAsync()).Version); // no drop committed
    }

    // ================================================================ §3.17 — message hygiene / render

    private const string Poison = "col\r\n[CRITICAL] forged\u2028entry\0";

    [Fact]
    public async Task NestedRenameDrop_FailClosedMessages_AreSanitized_NoRawSegment()
    {
        StructType schema = AddressSchema();
        await WriteNestedNameMappedAsync(schema, AddressBatch(schema));
        using var backend = new LocalFileSystemBackend(_root);

        // A poisoned target segment + poisoned toName; the rename message repeats toName, so the amplification
        // pin (#683) applies. The missing intermediate "address" is present so the target-miss branch fires.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeltaTableWriter(backend).RenameColumnAsync(new[] { "address", Poison }, Poison));
        Assert.Equal(0, ex.Message.Count(c => c == '\n'));
        foreach (char c in new[] { '\r', '\0', '\u2028' })
        {
            Assert.DoesNotContain(c, ex.Message);
        }

        Assert.DoesNotContain(Poison, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedRenameDrop_DiagnosticRender_IsBoundaryPreserving_NonCollapsing()
    {
        // A fail-closed message for a path containing a DOT-IN-NAME segment renders each segment in its own
        // ["…"] bracket, so ["a.b"].["zip"] is NOT collapsed to an ambiguous "a.b.zip". A path into a→b→zip and
        // a path ["a.b","zip"] render DISTINGUISHABLY.
        var schema = new StructType(new[]
        {
            new StructField("a.b", new StructType(new[]
            {
                new StructField("other", DataTypes.LongType),
            })),
        });

        // ["a.b","zip"] — "zip" is absent under "a.b" → F2, message names the partial path ["a.b"].
        var ex = Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
            schema, new[] { "a.b", "zip" }, DeltaTableWriter.SchemaChangeOp.Drop, null));

        // Boundary-preserving: the segment "a.b" appears inside its own bracket, never collapsed with a child.
        Assert.Contains("[\"a.b\"]", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("a.b.zip", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================ §3.19 — seeded property harness

    [Fact]
    public void NestedRenameDrop_Property_MetadataOnlyInvariant()
    {
        int seed = TestSeed.Combine(TestSeed.Resolve(), "nested-rename-drop-840-property");
        var rng = new Random(seed);
        const int iterations = 200;

        for (int i = 0; i < iterations; i++)
        {
            int drawSeed = rng.Next();
            try
            {
                RunPropertyIteration(new Random(drawSeed));
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                throw new Xunit.Sdk.XunitException(
                    $"[deltasharp-seed] base={seed} iter={i} draw={drawSeed}: unexpected {ex.GetType().Name}: {ex.Message}");
            }
            catch (Xunit.Sdk.XunitException inner)
            {
                throw new Xunit.Sdk.XunitException(
                    $"[deltasharp-seed] base={seed} iter={i} draw={drawSeed} (rerun with DELTASHARP_TEST_SEED to reproduce): {inner.Message}");
            }
        }
    }

    // One property draw over the PURE transform + door-collision logic (deterministic, no I/O): build a random
    // single-level nested schema, then draw from BOTH generators — a reachable path (assert the metadata-only
    // rebuild preserves identity) OR an enumerated malformed-path tamper operator (assert the SPECIFIC typed
    // fail-closed). This is the write-door analog of the parent §3.33 tamper set; every disjunct is exercised.
    private static void RunPropertyIteration(Random rng)
    {
        // Random schema: {id:long, s:struct<c0..cN>} with disjoint child names; N in [1,4].
        int childCount = rng.Next(1, 5);
        var children = new List<StructField>(childCount);
        for (int j = 0; j < childCount; j++)
        {
            children.Add(new StructField("c" + j, j % 2 == 0 ? DataTypes.LongType : DataTypes.StringType));
        }

        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("s", new StructType(children), nullable: true),
        });
        (StructType mapped, _) = ColumnMapping.AssignFreshMapping(schema, new SeededPhysicalNameSource("prop-" + rng.Next()));

        int targetIdx = rng.Next(childCount);
        string targetName = "c" + targetIdx;

        int op = rng.Next(10);
        try
        {
            RunPropertyCase(op, mapped, childCount, targetIdx, targetName, rng);
        }
        catch (Xunit.Sdk.XunitException inner)
        {
            throw new Xunit.Sdk.XunitException($"op={op} childCount={childCount} targetIdx={targetIdx}: {inner.Message}");
        }
    }

    private static void RunPropertyCase(
        int op, StructType mapped, int childCount, int targetIdx, string targetName, Random rng)
    {
        switch (op)
        {
            case 0: // reachable rename — assert identity preserved, only Name changes
                {
                    StructField original = ChildField(mapped, "s", targetName);
                    StructType rebuilt = DeltaTableWriter.DescendAndRebuild(
                        mapped, new[] { "s", targetName }, DeltaTableWriter.SchemaChangeOp.Rename, "renamed_target");
                    StructField renamed = ChildField(rebuilt, "s", "renamed_target");
                    Assert.Equal(
                        ColumnMapping.PhysicalName(original, ColumnMappingMode.Name),
                        ColumnMapping.PhysicalName(renamed, ColumnMappingMode.Name));
                    Assert.True(ColumnMapping.TryGetId(original, out long oid));
                    Assert.True(ColumnMapping.TryGetId(renamed, out long rid));
                    Assert.Equal(oid, rid);
                    Assert.Equal(original.DataType, renamed.DataType);
                    Assert.Same(mapped[0], rebuilt[0]); // sibling "id" carried by reference
                    break;
                }

            case 1: // reachable drop — target absent, siblings preserved
                {
                    StructType rebuilt = DeltaTableWriter.DescendAndRebuild(
                        mapped, new[] { "s", targetName }, DeltaTableWriter.SchemaChangeOp.Drop, null);
                    var rebuiltS = (StructType)rebuilt["s"].DataType;
                    Assert.False(rebuiltS.TryGetField(targetName, out _));
                    Assert.Equal(childCount - 1, rebuiltS.Count);
                    break;
                }

            case 2: // rename-to-same-name no-op carve-out — no F6a, identical schema shape
                {
                    StructType rebuilt = DeltaTableWriter.DescendAndRebuild(
                        mapped, new[] { "s", targetName }, DeltaTableWriter.SchemaChangeOp.Rename, targetName);
                    var rebuiltS = (StructType)rebuilt["s"].DataType;
                    Assert.True(rebuiltS.TryGetField(targetName, out _));
                    Assert.Equal(childCount, rebuiltS.Count);
                    break;
                }

            case 3: // F1 empty path
                {
                    Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
                        mapped, Array.Empty<string>(), DeltaTableWriter.SchemaChangeOp.Drop, null));
                    break;
                }

            case 4: // F2 absent target
                {
                    var ex = Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
                        mapped, new[] { "s", "absent_" + rng.Next() }, DeltaTableWriter.SchemaChangeOp.Drop, null));
                    Assert.Contains("no such", ex.Message, StringComparison.Ordinal);
                    break;
                }

            case 5: // F2 absent intermediate
                {
                    var ex = Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
                        mapped, new[] { "s", "absent_mid", "leaf" }, DeltaTableWriter.SchemaChangeOp.Rename, "x"));
                    Assert.Contains("no such", ex.Message, StringComparison.Ordinal);
                    break;
                }

            case 6: // F3 scalar intermediate (target child is scalar; descend one more)
                {
                    var ex = Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
                        mapped, new[] { "s", targetName, "deeper" }, DeltaTableWriter.SchemaChangeOp.Rename, "x"));
                    Assert.Contains("cannot descend", ex.Message, StringComparison.Ordinal);
                    break;
                }

            case 7: // F4b struct-within-struct intermediate (hand-built, bypasses load gate)
                {
                    var nested = new StructType(new[]
                    {
                    new StructField("outer", new StructType(new[]
                    {
                        new StructField("inner", new StructType(new[]
                        {
                            new StructField("leaf", DataTypes.LongType),
                        })),
                    })),
                });
                    var ex = Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
                        nested, new[] { "outer", "inner", "leaf" }, DeltaTableWriter.SchemaChangeOp.Drop, null));
                    Assert.Contains("nested-within-nested", ex.Message, StringComparison.Ordinal);
                    Assert.Contains("#585", ex.Message, StringComparison.Ordinal);
                    break;
                }

            case 8: // F6a case-sensitive sibling collision (only when >= 2 children)
                {
                    if (childCount >= 2)
                    {
                        int otherIdx = (targetIdx + 1) % childCount;
                        var ex = Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
                            mapped, new[] { "s", targetName }, DeltaTableWriter.SchemaChangeOp.Rename, "c" + otherIdx));
                        Assert.Contains("already exists at this level", ex.Message, StringComparison.Ordinal);
                    }

                    break;
                }

            case 9: // F4 array intermediate (hand-built with an array child)
                {
                    var arr = new StructType(new[]
                    {
                    new StructField("tags", new ArrayType(DataTypes.StringType)),
                });
                    var ex = Assert.Throws<InvalidOperationException>(() => DeltaTableWriter.DescendAndRebuild(
                        arr, new[] { "tags", "element" }, DeltaTableWriter.SchemaChangeOp.Drop, null));
                    Assert.Contains("array element / map key/value", ex.Message, StringComparison.Ordinal);
                    Assert.Contains("#585", ex.Message, StringComparison.Ordinal);
                    break;
                }
        }
    }

    // ================================================================ Harness helpers

    private static StructField ChildField(StructType schema, string parent, string child)
    {
        Assert.True(schema.TryGetField(parent, out StructField p), $"no parent '{parent}'");
        var pst = Assert.IsType<StructType>(p.DataType);
        Assert.True(pst.TryGetField(child, out StructField c), $"no child '{child}' under '{parent}'");
        return c;
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

    private async Task<Dictionary<string, int>> CommitActionKindsAsync(long version)
    {
        string path = Path.Combine(_root, "_delta_log", $"{version:D20}.json");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? node = JsonNode.Parse(line);
            if (node is JsonObject obj)
            {
                foreach (string key in obj.Select(kv => kv.Key))
                {
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
        }

        return counts;
    }

    private async Task<Dictionary<string, string>> Sha256OfActiveFilesAsync(Snapshot snapshot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (AddFileAction add in snapshot.ActiveFiles)
        {
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(_root, add.Path));
            result[add.Path] = Convert.ToHexString(SHA256.HashData(bytes));
        }

        return result;
    }

    private Task WriteNestedNameMappedAsync(
        StructType schema, ColumnBatch batch, string[]? partitionColumns = null, (string Key, string Value)[]? extraConfig = null)
        => WriteRawNestedTableAsync(schema, batch, ColumnMappingMode.Name, partitionColumns, extraConfig);

    private Task WriteNestedIdMappedAsync(StructType schema, ColumnBatch batch)
        => WriteRawNestedTableAsync(schema, batch, ColumnMappingMode.Id, null, null);

    // Authors a single-commit nested Delta table end-to-end: mint the mapping, write the batch to a REAL
    // physical Parquet file via the merged nested writer (the write-facade cannot encode nested batches), and
    // hand-author a protocol + metaData + add commit.
    private async Task WriteRawNestedTableAsync(
        StructType schema, ColumnBatch batch, ColumnMappingMode mode,
        string[]? partitionColumns, (string Key, string Value)[]? extraConfig)
    {
        (StructType mapped, long maxColumnId) = ColumnMapping.AssignFreshMapping(schema, new SeededPhysicalNameSource(Seed));
        StructType physical = ColumnMapping.MapWriteSchemaToPhysical(schema, mapped, mode);
        byte[] parquetBytes = await ParquetTestHelpers.WriteToBytesAsync(physical, new[] { RelabelBatch(batch, physical) });

        string schemaJson = DeltaSchemaJson.ToJson(mapped);
        string modeName = mode == ColumnMappingMode.Id ? "id" : "name";
        const string relativePath = "part-00000.parquet";

        using var backend = new LocalFileSystemBackend(_root);
        await backend.PutIfAbsentAsync(relativePath, parquetBytes, CancellationToken.None);

        var config = new List<(string Key, string Value)>
        {
            ("delta.columnMapping.mode", modeName),
            ("delta.columnMapping.maxColumnId", maxColumnId.ToString(CultureInfo.InvariantCulture)),
        };
        if (extraConfig is not null)
        {
            config.AddRange(extraConfig);
        }

        string addLine =
            $"{{\"add\":{{\"path\":\"{relativePath}\",\"partitionValues\":{{}},"
            + $"\"size\":{parquetBytes.Length},\"modificationTime\":0,\"dataChange\":true}}}}";
        byte[] commit = Encoding.UTF8.GetBytes(
            ProtocolFeatureLine() + "\n"
            + MetadataLine(schemaJson, partitionColumns ?? Array.Empty<string>(), config.ToArray()) + "\n"
            + addLine + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", commit, CancellationToken.None);
    }

    private async Task AppendMetadataOnlyCommitAsync(
        long version, string schemaJson, params (string Key, string Value)[] configuration)
    {
        using var backend = new LocalFileSystemBackend(_root);
        byte[] commit = Encoding.UTF8.GetBytes(
            MetadataLine(schemaJson, Array.Empty<string>(), configuration) + "\n");
        await backend.PutIfAbsentAsync($"_delta_log/{version:D20}.json", commit, CancellationToken.None);
    }

    // Rewraps a logical-named batch under the PHYSICAL schema so the writer (which cross-checks names) accepts
    // it: only STRUCT vectors carry field names, so only they need reconstruction (single-level scope → struct
    // children are always scalar, never recursive).
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

    private static string MetadataLine(
        string schemaJson, string[] partitionColumns, params (string Key, string Value)[] configuration)
    {
        string escapedSchema = System.Text.Json.JsonSerializer.Serialize(schemaJson);
        string config = "{" + string.Join(",", configuration.Select(kv =>
            $"{System.Text.Json.JsonSerializer.Serialize(kv.Key)}:{System.Text.Json.JsonSerializer.Serialize(kv.Value)}")) + "}";
        string parts = "[" + string.Join(",", partitionColumns.Select(p => System.Text.Json.JsonSerializer.Serialize(p))) + "]";
        return "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
            + "\"schemaString\":" + escapedSchema + ",\"partitionColumns\":" + parts
            + ",\"configuration\":" + config + "}}";
    }

    private static Func<string> FileNames()
    {
        int counter = 0;
        return () => "file" + (counter++).ToString(CultureInfo.InvariantCulture);
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

    private static MapColumnVector StrLongMap(MapType type, IReadOnlyList<IReadOnlyList<(string Key, long Value)>> rows)
    {
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.StringType, 16);
        MutableColumnVector values = ColumnVectors.Create(DataTypes.LongType, 16);
        var offsets = new int[rows.Count + 1];
        var nulls = new bool[rows.Count];
        int cursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            offsets[i] = cursor;
            foreach ((string key, long value) in rows[i])
            {
                keys.AppendBytes(Encoding.UTF8.GetBytes(key));
                values.AppendValue(value);
                cursor++;
            }
        }

        offsets[rows.Count] = cursor;
        return new MapColumnVector(type, keys, values, offsets, nulls);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // A fake IWriteConstraintEnforcer that records the (schema, constraints, batches) it is handed and, when
    // constructed with reject: true, simulates a surviving CHECK that depends on the altered field.
    private sealed class RecordingConstraintEnforcer : IWriteConstraintEnforcer
    {
        private readonly bool _reject;

        public RecordingConstraintEnforcer(bool reject = false) => _reject = reject;

        public int Calls { get; private set; }

        public StructType? Schema { get; private set; }

        public IReadOnlyList<DeltaTableConstraint>? Constraints { get; private set; }

        public IReadOnlyList<ColumnBatch>? Batches { get; private set; }

        public void Enforce(
            StructType schema,
            IReadOnlyList<DeltaTableConstraint> constraints,
            IReadOnlyList<ColumnBatch> batches,
            StructType? priorSchema = null)
        {
            Calls++;
            Schema = schema;
            Constraints = constraints;
            Batches = batches;
            if (_reject)
            {
                throw DeltaConstraintDependentColumnException.ForColumnChange("address", new[] { constraints[0] });
            }
        }
    }
}
