using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Parquet;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>Marks the non-parallel xUnit collection the column-mapping tests run in (FIX #7): a
/// fail-closed SAFETY test must never flake red in CI, so these raw-write / shared-temp-filesystem tests
/// are serialized rather than run in the default parallel collection.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ColumnMappingTestCollection
{
    /// <summary>The collection name shared by <see cref="CollectionDefinitionAttribute"/> and
    /// <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "ColumnMapping raw-write (non-parallel)";
}

/// <summary>
/// End-to-end tests for Delta column mapping <c>name</c> mode (STORY-05.4.3 / #191, design §2.12.3).
/// Covers the four acceptance criteria against a real <see cref="LocalFileSystemBackend"/> table:
/// <list type="bullet">
/// <item><b>AC1</b> (HP-10) — a metadata-only <b>rename</b> reads through from UNCHANGED Parquet: a column
/// whose logical name differs from its physical name still resolves to the correct data/values.</item>
/// <item><b>AC2</b> — a metadata-only <b>drop</b> removes a column from the logical schema while time travel
/// to the prior version still exposes the dropped column and its data.</item>
/// <item><b>AC3</b> (OR-a/OR-b) — a name-mode write emits <b>consistent</b> physical/logical metadata: the
/// committed <c>metaData</c> schema carries per-field id + physicalName (typed JSON), the configuration/
/// protocol declare column mapping, and the Parquet footer stores the <b>physical</b> column names.</item>
/// <item><b>AC4</b> (EE-09) — column mapping without protocol support, a legacy reader-v2 table, and
/// <c>id</c> mode are each rejected fail-closed with a typed error (id mode deferred to #523).</item>
/// </list>
/// The physical names are minted by a deterministic seeded source so the AC3 assertions are golden.
/// </summary>
/// <remarks>
/// FIX #7 (flaky-safety-test isolation): these tests are placed in a NON-parallel xUnit collection. The
/// fail-closed safety tests (e.g. <see cref="IdMode_IsRejectedFailClosed_DeferredTo523"/>) must never present
/// red in CI, and a raw-write column-mapping test flaked once under xUnit parallel execution on the shared
/// temp filesystem. Disabling parallelization for this collection removes that harness race while keeping
/// each test's per-instance temp directory.
/// </remarks>
[Collection(ColumnMappingTestCollection.Name)]
public sealed class ColumnMappingTests : IDisposable
{
    private const string Seed = "story-05.4.3-name-mode";

    // Golden physical names produced by SeededPhysicalNameSource(Seed) for columns 0..2 (id/score/name).
    // A change to the deterministic minting algorithm (or its ordering) changes these and fails the test.
    private const string PhysId = "col-64aae7a2-24b3-20e2-035d-71a25b33dcc3";
    private const string PhysScore = "col-63a3e1b4-852a-1611-b174-1571302cb0ff";
    private const string PhysName = "col-b754bc6e-5f1b-4020-2537-0b5f571152bb";

    // A flat schema with TWO long columns (id, score) so a positional/name misread on read cannot pass by
    // luck: swapping id<->score would corrupt distinct values, and reading by logical (not physical) name
    // after a rename would fail outright.
    private static readonly StructType FlatSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("score", DataTypes.LongType, nullable: true),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    private readonly string _root;

    public ColumnMappingTests() =>
        _root = Path.Combine(Path.GetTempPath(), "deltacolmap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // ---------------------------------------------------------------- AC3: consistent physical/logical

    [Fact]
    public async Task NameModeWrite_EmitsConsistentPhysicalAndLogicalMetadata()
    {
        await CreateNameMappedAsync((1L, 100L, "alice"), (2L, 200L, "bob"));

        // --- The committed metaData carries the logical schema with per-field id + physicalName ---
        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        // Configuration: name mode + tracked maxColumnId (= column count).
        Assert.Equal("name", snapshot.Metadata.Configuration[ColumnMapping.ModeKey]);
        Assert.Equal("3", snapshot.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);

        // Protocol: table-features reader v3 / writer v7 declaring the columnMapping feature in BOTH sets.
        Assert.Equal(3, snapshot.Protocol.MinReaderVersion);
        Assert.Equal(7, snapshot.Protocol.MinWriterVersion);
        Assert.Contains("columnMapping", snapshot.Protocol.ReaderFeatures);
        Assert.Contains("columnMapping", snapshot.Protocol.WriterFeatures);

        // Logical schema unchanged (display names), each field carrying its golden id + physicalName.
        StructType schema = snapshot.Schema;
        AssertMapping(schema[0], "id", 1, PhysId);
        AssertMapping(schema[1], "score", 2, PhysScore);
        AssertMapping(schema[2], "name", 3, PhysName);

        // partitionColumns is empty (unpartitioned) — physical-name tracking is exercised by the partitioned
        // read test below.
        Assert.True(snapshot.Metadata.PartitionColumns.IsDefaultOrEmpty);

        // --- The decoded metaData.schemaString stores id as an UNQUOTED integer (typed interop, #330) and
        // physicalName as the golden string. Assert the EXACT committed schemaString (logical field names,
        // per-field id unquoted int + seeded golden physicalName), matching the byte-for-byte shape the
        // partitioned golden (NameMode_PartitionValues_AreKeyedByPhysicalName_AndReadBack) asserts. A swap or
        // misroute of any field's id/physicalName/type/nullability breaks this exactly. ---
        string schemaJson = await ReadCommittedSchemaStringAsync();
        Assert.Equal(
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":{"
            + $"\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"{PhysId}\"}}}},"
            + "{\"name\":\"score\",\"type\":\"long\",\"nullable\":true,\"metadata\":{"
            + $"\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"{PhysScore}\"}}}},"
            + "{\"name\":\"name\",\"type\":\"string\",\"nullable\":true,\"metadata\":{"
            + $"\"delta.columnMapping.id\":3,\"delta.columnMapping.physicalName\":\"{PhysName}\"}}}}]}}",
            schemaJson);

        // --- The Parquet footer stores the PHYSICAL column names (not the logical display names) ---
        string[] parquetColumns = await ReadParquetColumnNamesAsync(snapshot.ActiveFiles[0].Path);
        Assert.Equal(new[] { PhysId, PhysScore, PhysName }, parquetColumns);
        Assert.DoesNotContain("id", parquetColumns);
        Assert.DoesNotContain("score", parquetColumns);
    }

    // ---------------------------------------------------------------- AC1: rename read-through (HP-10)

    [Fact]
    public async Task Rename_IsMetadataOnly_AndReadsThroughUnchangedParquet()
    {
        await CreateNameMappedAsync((1L, 100L, "alice"), (2L, 200L, "bob"));

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot before = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        string dataFileBefore = before.ActiveFiles[0].Path;
        string physicalScoreBefore = ColumnMapping.PhysicalName(before.Schema[1], ColumnMappingMode.Name);

        // Rename score -> points (a metadata-only edit): physicalName/id unchanged, no data rewrite.
        var writer = new DeltaTableWriter(backend);
        DeltaCommitResult result = await writer.RenameColumnAsync("score", "points");
        Assert.Equal(1L, result.Version);

        Snapshot after = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        // The physical Parquet file is UNCHANGED (same active file, same physicalName + id on the column).
        Assert.Equal(dataFileBefore, after.ActiveFiles[0].Path);
        Assert.Equal("points", after.Schema[1].Name);
        Assert.Equal(physicalScoreBefore, ColumnMapping.PhysicalName(after.Schema[1], ColumnMappingMode.Name));
        Assert.True(ColumnMapping.TryGetId(after.Schema[1], out long renamedId));
        Assert.Equal(2, renamedId);

        // The renamed column reads through correctly from the UNCHANGED Parquet: 'points' resolves to the
        // physical 'score' column, and the distinct long values prove no positional misread.
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(new[] { "id", "points", "name" }, info.Schema.Select(f => f.Name).ToArray());

        List<(long Id, long? Points, string? Name)> rows = await ReadRowsAsync(source, info.Version);
        Assert.Equal(
            new (long, long?, string?)[] { (1L, 100L, "alice"), (2L, 200L, "bob") },
            rows.OrderBy(r => r.Id).ToList());
    }

    // ---------------------------------------------------------------- AC2: metadata-drop + time travel

    [Fact]
    public async Task Drop_IsMetadataOnly_AndTimeTravelStillExposesDroppedColumn()
    {
        await CreateNameMappedAsync((1L, 100L, "alice"), (2L, 200L, "bob"));

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);
        DeltaCommitResult drop = await writer.DropColumnAsync("score");
        Assert.Equal(1L, drop.Version);

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);

        // Latest (v1): 'score' is gone from the logical schema; id + name still read through.
        DeltaSnapshotInfo latest = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(1L, latest.Version);
        Assert.Equal(new[] { "id", "name" }, latest.Schema.Select(f => f.Name).ToArray());
        List<(long, long?, string?)> current = await ReadNarrowRowsAsync(source, latest.Version);
        Assert.Equal(
            new (long, long?, string?)[] { (1L, null, "alice"), (2L, null, "bob") },
            current.OrderBy(r => r.Item1).ToList());

        // Time travel to v0: the dropped column 'score' and its data are still present per that version.
        DeltaSnapshotInfo historical = await source.LoadSnapshotAsync(versionAsOf: 0L, timestampAsOf: null);
        Assert.Equal(0L, historical.Version);
        Assert.Equal(new[] { "id", "score", "name" }, historical.Schema.Select(f => f.Name).ToArray());
        List<(long Id, long? Score, string? Name)> history = await ReadRowsAsync(source, historical.Version);
        Assert.Equal(
            new (long, long?, string?)[] { (1L, 100L, "alice"), (2L, 200L, "bob") },
            history.OrderBy(r => r.Id).ToList());
    }

    // ---------------------------------------------------------------- AC1 (partitioned): physical partition keys

    [Fact]
    public async Task NameMode_PartitionValues_AreKeyedByPhysicalName_AndReadBack()
    {
        var schema = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        });

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames()))
        {
            MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, 2);
            MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 2);
            region.AppendBytes(Encoding.UTF8.GetBytes("us"));
            id.AppendValue(1L);
            region.AppendBytes(Encoding.UTF8.GetBytes("eu"));
            id.AppendValue(2L);
            var batch = new ManagedColumnBatch(schema, new ColumnVector[] { region, id }, 2);
            await target.CreateNameMappedTableAsync(
                schema, new[] { "region" }, new[] { batch }, new SeededPhysicalNameSource(Seed));
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        string physicalRegion = ColumnMapping.PhysicalName(snapshot.Schema[0], ColumnMappingMode.Name);

        // FIX #1 (Spark golden dv-with-columnmapping): metaData.partitionColumns holds the LOGICAL name
        // ("region"), while add.partitionValues keys are PHYSICAL (partition IDENTITY logical, partition VALUE
        // KEY physical). physicalRegion is the seeded col-<uuid>, never the logical "region".
        Assert.Equal(new[] { "region" }, snapshot.Metadata.PartitionColumns.ToArray());
        Assert.NotEqual("region", physicalRegion);
        Assert.All(snapshot.ActiveFiles, add => Assert.True(add.PartitionValues.ContainsKey(physicalRegion)));
        Assert.DoesNotContain(snapshot.ActiveFiles, add => add.PartitionValues.ContainsKey("region"));

        // FIX #4: assert the EXACT committed schemaString — logical field names, per-field id (unquoted int)
        // + golden physicalName, matching the Spark name-mode shape byte-for-byte (region=counter0=PhysId,
        // id=counter1=PhysScore under the deterministic seed). A swap/misroute of any field would break this.
        Assert.Equal(PhysId, physicalRegion);
        string schemaJson = await ReadCommittedSchemaStringAsync();
        Assert.Equal(
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"region\",\"type\":\"string\",\"nullable\":true,\"metadata\":{"
            + $"\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"{PhysId}\"}}}},"
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":{"
            + $"\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"{PhysScore}\"}}}}]}}",
            schemaJson);

        // The read facade relabels back to logical: region reads its partition value; id reads its data.
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(new[] { "region", "id" }, info.Schema.Select(f => f.Name).ToArray());

        var rows = new List<(string?, long)>();
        foreach (ColumnBatch b in await source.ReadBatchesAsync(info.Version))
        {
            for (int r = 0; r < b.LogicalRowCount; r++)
            {
                ColumnVector rc = b.SelectedColumn(0);
                ColumnVector ic = b.SelectedColumn(1);
                rows.Add((rc.IsNull(r) ? null : Encoding.UTF8.GetString(rc.GetBytes(r)), ic.GetValue<long>(r)));
            }
        }

        Assert.Equal(
            new (string?, long)[] { ("eu", 2L), ("us", 1L) },
            rows.OrderBy(r => r.Item1, StringComparer.Ordinal).ToList());
    }

    // ---------------------------------------------------------------- AC4: fail-closed (EE-09)

    [Fact]
    public async Task IdMode_IsRejectedFailClosed_DeferredTo523()
    {
        // A table-features table declaring columnMapping (protocol supports it) but mode = id.
        await WriteRawTableAsync(
            protocol: ProtocolFeatureLine(),
            metadata: MetadataLine(("delta.columnMapping.mode", "id"), ("delta.columnMapping.maxColumnId", "2")));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(
            () => source.LoadSnapshotAsync(null, null));
        Assert.Contains("'id'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not implemented", ex.Message, StringComparison.Ordinal);
        Assert.Contains("523", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyReaderV2_ColumnMappingTable_IsRejectedFailClosed()
    {
        // Reader version 2 = legacy (id-era) column mapping; rejected unconditionally (served only via the
        // table-features reader v3 representation).
        await WriteRawTableAsync(
            protocol: """{"protocol":{"minReaderVersion":2,"minWriterVersion":5}}""",
            metadata: MetadataLine(("delta.columnMapping.mode", "name")));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(
            () => source.LoadSnapshotAsync(null, null));
        Assert.Contains("reader", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ColumnMappingMode_WithoutProtocolSupport_IsRejectedFailClosed()
    {
        // mode = name but the protocol (basic reader v1) does not declare the columnMapping feature: the
        // property must not be honored — a protocol-upgrade error, never a silent positional read.
        await WriteRawTableAsync(
            protocol: """{"protocol":{"minReaderVersion":1,"minWriterVersion":2}}""",
            metadata: MetadataLine(("delta.columnMapping.mode", "name")));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(
            () => source.LoadSnapshotAsync(null, null));
        Assert.Contains("columnMapping", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_OnNonColumnMappedTable_IsRejected()
    {
        // A plain (none-mode) table has no stable physical identity, so a metadata-only rename is refused.
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 1);
            id.AppendValue(1L);
            var schema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
            await target.AppendAsync(
                schema, Array.Empty<string>(), new[] { new ManagedColumnBatch(schema, new ColumnVector[] { id }, 1) });
        }

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.RenameColumnAsync("id", "identifier"));
    }

    // ---------------------------------------------------------------- HIGH: swap read-through

    [Fact]
    public async Task ColumnSwap_ByRename_EachLogicalColumnReadsThroughToItsPhysicalData()
    {
        // Distinct per-column values so a misroute corrupts loudly: id={1,2}, score={100,200}.
        await CreateNameMappedAsync((1L, 100L, "alice"), (2L, 200L, "bob"));

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);

        // Swap the LOGICAL names id<->score via the temp-name 3-step. Each rename is metadata-only, so the
        // PHYSICAL columns (and their data) never move; only the labels swap. After the swap logical "id"
        // resolves to score's physical column and logical "score" to id's.
        await writer.RenameColumnAsync("id", "__tmp");
        await writer.RenameColumnAsync("score", "id");
        await writer.RenameColumnAsync("__tmp", "score");

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);

        int idIdx = info.Schema.IndexOf("id");
        int scoreIdx = info.Schema.IndexOf("score");
        int nameIdx = info.Schema.IndexOf("name");
        Assert.True(idIdx >= 0 && scoreIdx >= 0 && nameIdx >= 0);

        var byName = new Dictionary<string, (long IdVal, long ScoreVal)>(StringComparer.Ordinal);
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector idCol = batch.SelectedColumn(idIdx);
            ColumnVector scoreCol = batch.SelectedColumn(scoreIdx);
            ColumnVector nameCol = batch.SelectedColumn(nameIdx);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                byName[Encoding.UTF8.GetString(nameCol.GetBytes(r))] =
                    (idCol.GetValue<long>(r), scoreCol.GetValue<long>(r));
            }
        }

        // alice originally had id=1, score=100 → after the swap logical "id"=100, "score"=1 (and symmetric
        // for bob). A broken physical-name resolution (positional/name misread) would yield the ORIGINAL
        // unswapped values (id=1) and fail here.
        Assert.Equal((100L, 1L), byName["alice"]);
        Assert.Equal((200L, 2L), byName["bob"]);
    }

    // ---------------------------------------------------------------- HIGH: reordered physical file read-through

    [Fact]
    public async Task NameMode_ReadsThroughReorderedPhysicalFile_ByName_NotByPosition()
    {
        // The direct closer for the mapping layer (GAP 1b): a NAME-mode table whose underlying Parquet data
        // file stores its PHYSICAL columns in a NON-logical order (physical file order = [name, score, id],
        // i.e. the REVERSE of the logical id/score/name order). This is exactly the shape an interop table
        // gets when a Spark writer emits physical columns in an order that differs from the current logical
        // schema. The name-mode read door must resolve each LOGICAL column to its OWN physical data BY NAME
        // (ParquetFileReader.ResolveFileFields), never by file position: a positional-assembly regression
        // would mis-route id<->name (loud type mismatch) and id<->score (loud value swap), so it CANNOT pass
        // this even though the constructed dataSchema is logical-ordered.
        //
        // DeltaSharp's own writer always stages physical columns in logical order, so a reordered physical
        // file is not reachable via the normal write door; we author the physical Parquet file directly (with
        // the physical StructType reversed) and hand-assemble the version-0 _delta_log (name-mode metaData +
        // an add pointing at that file), reusing this fixture's raw-write helpers.
        const string relativePath = "reversed-physical.parquet";

        // Physical file: columns in REVERSED order, each carrying its OWN distinct values.
        var physicalSchemaReversed = new StructType(new[]
        {
            new StructField(PhysName, DataTypes.StringType, nullable: true),
            new StructField(PhysScore, DataTypes.LongType, nullable: true),
            new StructField(PhysId, DataTypes.LongType, nullable: false),
        });
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, 2);
        MutableColumnVector score = ColumnVectors.Create(DataTypes.LongType, 2);
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 2);
        name.AppendBytes(Encoding.UTF8.GetBytes("alice"));
        score.AppendValue(100L);
        id.AppendValue(1L);
        name.AppendBytes(Encoding.UTF8.GetBytes("bob"));
        score.AppendValue(200L);
        id.AppendValue(2L);
        var physicalBatch = new ManagedColumnBatch(
            physicalSchemaReversed, new ColumnVector[] { name, score, id }, 2);

        byte[] parquetBytes;
        using (var buffer = new MemoryStream())
        {
            await new ParquetFileWriter().WriteAsync(
                buffer, physicalSchemaReversed, new[] { physicalBatch }, CancellationToken.None);
            parquetBytes = buffer.ToArray();
        }

        // Confirm the on-disk file really is in reversed physical order (guards the test's own premise).
        Assert.Equal(new[] { PhysName, PhysScore, PhysId }, await ParquetColumnNamesAsync(parquetBytes));

        // The name-mode metaData: LOGICAL schema id/score/name, each field carrying its golden id +
        // physicalName; the physical Parquet file above stores those physical columns REVERSED.
        string schemaJson =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + $"{{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"{PhysId}\"}}}},"
            + "{\"name\":\"score\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
            + $"{{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"{PhysScore}\"}}}},"
            + "{\"name\":\"name\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + $"{{\"delta.columnMapping.id\":3,\"delta.columnMapping.physicalName\":\"{PhysName}\"}}}}]}}";

        using (var backend = new LocalFileSystemBackend(_root))
        {
            await backend.PutIfAbsentAsync(relativePath, parquetBytes, CancellationToken.None);
            string addLine =
                $"{{\"add\":{{\"path\":\"{relativePath}\",\"partitionValues\":{{}},"
                + $"\"size\":{parquetBytes.Length},\"modificationTime\":0,\"dataChange\":true}}}}";
            byte[] commit = Encoding.UTF8.GetBytes(
                ProtocolFeatureLine() + "\n"
                + NameModeMetadataLine(
                    schemaJson,
                    partitionColumns: Array.Empty<string>(),
                    ("delta.columnMapping.mode", "name"),
                    ("delta.columnMapping.maxColumnId", "3")) + "\n"
                + addLine + "\n");
            await backend.PutIfAbsentAsync(
                "_delta_log/00000000000000000000.json", commit, CancellationToken.None);
        }

        // Read via the name-mode read door: each LOGICAL column must return its OWN physical data.
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(new[] { "id", "score", "name" }, info.Schema.Select(f => f.Name).ToArray());

        List<(long Id, long? Score, string? Name)> rows = await ReadRowsAsync(source, info.Version);
        Assert.Equal(
            new (long, long?, string?)[] { (1L, 100L, "alice"), (2L, 200L, "bob") },
            rows.OrderBy(r => r.Id).ToList());
    }

    // ---------------------------------------------------------------- HIGH #3: poisoned physical-name collision

    [Fact]
    public async Task PoisonedPhysicalNameCollision_IsRejected_NotMisread()
    {
        // A name-mode table (protocol supports columnMapping) whose PARTITION column 'part' and DATA column
        // 'col1' share the SAME physicalName 'col-dup'. Without the uniqueness gate the read would serve the
        // partition CONSTANT under col1's logical name (wrong data, no exception). It must fail closed.
        const string dupPhysical = "col-dup00000000000000000000000000000000";
        string schema =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"part\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + $"{{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"{dupPhysical}\"}}}},"
            + "{\"name\":\"col1\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
            + $"{{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"{dupPhysical}\"}}}}]}}";
        await WriteRawTableAsync(
            protocol: ProtocolFeatureLine(),
            metadata: NameModeMetadataLine(
                schema,
                partitionColumns: new[] { "part" },
                ("delta.columnMapping.mode", "name"),
                ("delta.columnMapping.maxColumnId", "2")));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(
            () => source.LoadSnapshotAsync(null, null));
        Assert.Contains("physical name", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(dupPhysical, ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- HIGH #2 (#525): append/overwrite ENABLED

    // #525 round-trip: create a name-mode table, APPEND more rows through the facade, and read back ALL rows
    // by LOGICAL names. The appended file physically carries the col-<uuid> PHYSICAL names (the reader
    // resolves by physicalName), so a positional/name misroute would corrupt the read.
    [Fact]
    public async Task NameMode_Append_ReadsBackAllRows_AndAppendedFileCarriesPhysicalNames()
    {
        // A single shared deterministic file-name counter across create + append so the two writes never
        // collide on the same physical path (create ⇒ part-file0, append ⇒ part-file1).
        Func<string> names = FileNames();

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateNameMappedTableAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
                new SeededPhysicalNameSource(Seed));
        }

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            DeltaWriteResult result = await target.AppendAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (2L, 200L, (string?)"bob"), (3L, 300L, (string?)null) }) });
            Assert.Equal(1L, result.Version);
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        // The teeth: EVERY active data file (the original AND the appended one) physically stores the
        // col-<uuid> PHYSICAL names — a corrupt append would land a logical-named file here.
        Assert.Equal(2, snapshot.ActiveFiles.Length);
        foreach (AddFileAction add in snapshot.ActiveFiles)
        {
            string[] physical = await ReadParquetColumnNamesAsync(add.Path);
            Assert.Equal(new[] { PhysId, PhysScore, PhysName }, physical);
        }

        // Read back through the facade by LOGICAL names: all three rows, correctly associated.
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        List<(long Id, long? Score, string? Name)> rows = await ReadRowsAsync(source, info.Version);
        Assert.Equal(
            new (long, long?, string?)[] { (1L, 100L, "alice"), (2L, 200L, "bob"), (3L, 300L, null) },
            rows.OrderBy(r => r.Id).ToList());
    }

    // #525 partitioned append: the appended file's add.partitionValues are keyed by the PHYSICAL partition
    // name (matching the create path / Delta writer requirement), and read-back resolves by logical name.
    [Fact]
    public async Task NameMode_PartitionedAppend_PartitionValuesKeyedByPhysicalName_AndReadBack()
    {
        var schema = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        });
        Func<string> names = FileNames();

        static ManagedColumnBatch Batch(StructType s, string region, long id)
        {
            MutableColumnVector r = ColumnVectors.Create(DataTypes.StringType, 1);
            MutableColumnVector i = ColumnVectors.Create(DataTypes.LongType, 1);
            r.AppendBytes(Encoding.UTF8.GetBytes(region));
            i.AppendValue(id);
            return new ManagedColumnBatch(s, new ColumnVector[] { r, i }, 1);
        }

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateNameMappedTableAsync(
                schema, new[] { "region" }, new[] { Batch(schema, "us", 1L) }, new SeededPhysicalNameSource(Seed));
        }

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.AppendAsync(schema, new[] { "region" }, new[] { Batch(schema, "eu", 2L) });
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        string physicalRegion = ColumnMapping.PhysicalName(snapshot.Schema[0], ColumnMappingMode.Name);

        // metaData.partitionColumns stay LOGICAL; every add (create + append) keys partitionValues PHYSICALLY.
        Assert.Equal(new[] { "region" }, snapshot.Metadata.PartitionColumns.ToArray());
        Assert.Equal(2, snapshot.ActiveFiles.Length);
        Assert.All(snapshot.ActiveFiles, add => Assert.True(add.PartitionValues.ContainsKey(physicalRegion)));
        Assert.DoesNotContain(snapshot.ActiveFiles, add => add.PartitionValues.ContainsKey("region"));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        var rows = new List<(string?, long)>();
        foreach (ColumnBatch b in await source.ReadBatchesAsync(info.Version))
        {
            for (int r = 0; r < b.LogicalRowCount; r++)
            {
                ColumnVector rc = b.SelectedColumn(0);
                ColumnVector ic = b.SelectedColumn(1);
                rows.Add((rc.IsNull(r) ? null : Encoding.UTF8.GetString(rc.GetBytes(r)), ic.GetValue<long>(r)));
            }
        }

        Assert.Equal(
            new (string?, long)[] { ("eu", 2L), ("us", 1L) },
            rows.OrderBy(r => r.Item1, StringComparer.Ordinal).ToList());
    }

    // #525: OVERWRITE an existing name-mode table replaces its rows; the new file carries PHYSICAL names and
    // read-back returns only the new rows.
    [Fact]
    public async Task NameMode_Overwrite_ReadsBackNewRows()
    {
        Func<string> names = FileNames();

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateNameMappedTableAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice"), (2L, 200L, (string?)"bob") }) },
                new SeededPhysicalNameSource(Seed));
        }

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            DeltaWriteResult result = await target.OverwriteAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (9L, 900L, (string?)"zoe") }) },
                DeltaPartitionOverwriteMode.Static);
            Assert.Equal(1L, result.Version);
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        Assert.Single(snapshot.ActiveFiles);
        Assert.Equal(new[] { PhysId, PhysScore, PhysName }, await ReadParquetColumnNamesAsync(snapshot.ActiveFiles[0].Path));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        List<(long Id, long? Score, string? Name)> rows = await ReadRowsAsync(source, info.Version);
        Assert.Equal(new (long, long?, string?)[] { (9L, 900L, "zoe") }, rows);
    }

    // #525 identity preservation: an append must REUSE the table's existing per-field id/physicalName and
    // leave delta.columnMapping.maxColumnId untouched — never re-mint a physical name for an existing column.
    [Fact]
    public async Task NameMode_Append_PreservesColumnIdentityAndMaxColumnId()
    {
        Func<string> names = FileNames();

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateNameMappedTableAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
                new SeededPhysicalNameSource(Seed));
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot before = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        string maxIdBefore = before.Metadata.Configuration[ColumnMapping.MaxColumnIdKey];
        string schemaBefore = before.Metadata.SchemaString;

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.AppendAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (2L, 200L, (string?)"bob") }) });
        }

        Snapshot after = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        // The mapped schema (per-field id + physicalName) and maxColumnId are byte-for-byte unchanged — a
        // metadata-free data append never re-mints or bumps the mapping.
        Assert.Equal(schemaBefore, after.Metadata.SchemaString);
        Assert.Equal(maxIdBefore, after.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
        AssertMapping(after.Schema[0], "id", 1L, PhysId);
        AssertMapping(after.Schema[1], "score", 2L, PhysScore);
        AssertMapping(after.Schema[2], "name", 3L, PhysName);
    }

    // #525 corruption-guard TEETH (writer level): staging a LOGICAL-named file into a name-mode table is
    // rejected fail-closed by the #497 physical write-schema validation (footer schema derived from the real
    // bytes ≠ the table's PHYSICAL expected schema). This is the last-line defense that makes the write
    // fail-closed if anything ever staged logical names into a physical-name table.
    [Fact]
    public async Task NameModeAppend_WithLogicalNamedFile_IsRejectedFailClosed()
    {
        await CreateNameMappedAsync((1L, 100L, "alice"));

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);

        // A staged file whose footer (DataSchema) carries the LOGICAL names id/score/name — exactly the
        // corrupt shape a non-mapping-aware staging path would produce.
        var staged = new[]
        {
            new StagedDataFile(
                "part-logical.parquet",
                System.Collections.Immutable.ImmutableSortedDictionary<string, string?>.Empty,
                Size: 1,
                ModificationTime: 0,
                Stats: null,
                DataSchema: FlatSchema),
        };

        await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() => writer.AppendAsync(FlatSchema, staged));

        // The rejected write did not commit: the table is still at v0.
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(0L, info.Version);
    }

    // #542: overwriteSchema (wholesale schema replacement) is now SUPPORTED for a name-mode table — a
    // same-schema overwriteSchema through the public door replaces the data and preserves the columnMapping
    // (every column keeps its id + physicalName; maxColumnId unchanged). (Schema-CHANGING overwriteSchema —
    // drop / add-with-mint / retype — is exercised via the DeltaTableWriter mechanism tests below, since the
    // public door stages against the existing mapping and cannot stage a brand-new column.)
    [Fact]
    public async Task OverwriteSchema_OnNameModeTable_SameSchema_ReplacesData_AndPreservesMapping_Issue542()
    {
        Func<string> names = FileNames();
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateNameMappedTableAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
                new SeededPhysicalNameSource(Seed));
        }

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            DeltaWriteResult result = await target.OverwriteAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (9L, 900L, (string?)"zoe") }) },
                DeltaPartitionOverwriteMode.Static,
                overwriteSchema: true);
            Assert.Equal(1L, result.Version);
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        // The mapping is preserved verbatim (same physical names + ids, maxColumnId unchanged at 3).
        AssertMapping(snapshot.Schema["id"], "id", 1, PhysId);
        AssertMapping(snapshot.Schema["score"], "score", 2, PhysScore);
        AssertMapping(snapshot.Schema["name"], "name", 3, PhysName);
        Assert.Equal("3", snapshot.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);

        // The prior row is replaced; only the new row remains, read back through the name-mode read door.
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        List<(long Id, long? Score, string? Name)> rows = await ReadRowsAsync(source, info.Version);
        Assert.Equal(new (long, long?, string?)[] { (9L, 900L, "zoe") }, rows);
    }

    // #525 regression: `id` mode stays fail-closed on the WRITE path too (it is rejected at snapshot load —
    // #523 is a separate, dependency-blocked issue). Only `name` mode append/overwrite is newly enabled.
    [Fact]
    public async Task IdModeAppend_IsRejectedFailClosed_DeferredTo523()
    {
        // A protocol-supported columnMapping table declaring mode = id (raw commit, empty schema).
        await WriteRawTableAsync(
            protocol: ProtocolFeatureLine(),
            metadata: MetadataLine(("delta.columnMapping.mode", "id"), ("delta.columnMapping.maxColumnId", "2")));

        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames());
        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => target.AppendAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) }));
        Assert.Contains("'id'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("523", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- MEDIUM #4/#5: partition rename/drop

    [Fact]
    public async Task PartitionColumnRename_UpdatesLogicalPartitionColumns_AndReadsThrough()
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
            var batch = new ManagedColumnBatch(schema, new ColumnVector[] { region, id }, 1);
            await target.CreateNameMappedTableAsync(
                schema, new[] { "region" }, new[] { batch }, new SeededPhysicalNameSource(Seed));
        }

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);
        string physicalRegion =
            ColumnMapping.PhysicalName((await new DeltaLog(backend).LoadSnapshotAsync(null)).Schema[0], ColumnMappingMode.Name);

        // Rename the PARTITION column region -> zone: metaData.partitionColumns must update to the new LOGICAL
        // name, while add.partitionValues keys stay PHYSICAL (existing files still resolve).
        await writer.RenameColumnAsync("region", "zone");

        Snapshot after = await new DeltaLog(backend).LoadSnapshotAsync(null);
        Assert.Equal(new[] { "zone" }, after.Metadata.PartitionColumns.ToArray());
        Assert.All(after.ActiveFiles, add => Assert.True(add.PartitionValues.ContainsKey(physicalRegion)));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(new[] { "zone", "id" }, info.Schema.Select(f => f.Name).ToArray());

        var rows = new List<(string?, long)>();
        foreach (ColumnBatch b in await source.ReadBatchesAsync(info.Version))
        {
            for (int r = 0; r < b.LogicalRowCount; r++)
            {
                ColumnVector zc = b.SelectedColumn(0);
                ColumnVector ic = b.SelectedColumn(1);
                rows.Add((zc.IsNull(r) ? null : Encoding.UTF8.GetString(zc.GetBytes(r)), ic.GetValue<long>(r)));
            }
        }

        Assert.Equal(new (string?, long)[] { ("us", 7L) }, rows);
    }

    [Fact]
    public async Task PartitionColumnDrop_IsRejected_ByLogicalGuard()
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
            var batch = new ManagedColumnBatch(schema, new ColumnVector[] { region, id }, 1);
            await target.CreateNameMappedTableAsync(
                schema, new[] { "region" }, new[] { batch }, new SeededPhysicalNameSource(Seed));
        }

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);

        // The guard checks the LOGICAL name "region" against metaData.partitionColumns (now logical).
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.DropColumnAsync("region"));
        Assert.Contains("partition column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- cheap edges

    [Fact]
    public async Task RenameThenDropThenRead_PreservesRemainingColumnIdentity()
    {
        await CreateNameMappedAsync((1L, 100L, "alice"), (2L, 200L, "bob"));

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);
        await writer.RenameColumnAsync("score", "points");
        await writer.DropColumnAsync("points");

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(new[] { "id", "name" }, info.Schema.Select(f => f.Name).ToArray());

        var rows = new List<(long, string?)>();
        foreach (ColumnBatch b in await source.ReadBatchesAsync(info.Version))
        {
            for (int r = 0; r < b.LogicalRowCount; r++)
            {
                ColumnVector idc = b.SelectedColumn(0);
                ColumnVector namec = b.SelectedColumn(1);
                rows.Add((idc.GetValue<long>(r), namec.IsNull(r) ? null : Encoding.UTF8.GetString(namec.GetBytes(r))));
            }
        }

        Assert.Equal(
            new (long, string?)[] { (1L, "alice"), (2L, "bob") },
            rows.OrderBy(r => r.Item1).ToList());
    }

    [Fact]
    public async Task SequentialRenames_PreserveColumnIdentity()
    {
        await CreateNameMappedAsync((1L, 100L, "alice"));

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);

        string physicalScore =
            ColumnMapping.PhysicalName((await new DeltaLog(backend).LoadSnapshotAsync(null)).Schema[1], ColumnMappingMode.Name);

        // score -> B -> C: the physicalName/id are invariant across the chain (identity preserved).
        await writer.RenameColumnAsync("score", "B");
        await writer.RenameColumnAsync("B", "C");

        Snapshot after = await new DeltaLog(backend).LoadSnapshotAsync(null);
        Assert.Equal("C", after.Schema[1].Name);
        Assert.Equal(physicalScore, ColumnMapping.PhysicalName(after.Schema[1], ColumnMappingMode.Name));
        Assert.True(ColumnMapping.TryGetId(after.Schema[1], out long id));
        Assert.Equal(2L, id);

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        List<(long Id, long? Score, string? Name)> read = await ReadRowsAsync(source, info.Version);
        Assert.Equal(new (long, long?, string?)[] { (1L, 100L, "alice") }, read);
    }

    [Fact]
    public void EmptySchemaMapping_AssignsNoColumns_MaxIdZero()
    {
        (StructType schema, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(StructType.Empty, new SeededPhysicalNameSource(Seed));
        Assert.Empty(schema);
        Assert.Equal(0L, maxColumnId);
    }

    // ------------------------------------------------- #541: name-mode schema evolution (append/overwrite)

    // A deterministic seed for the MINTED physical name of a new column, DISTINCT from the create-path Seed so
    // the minted name can never collide with an existing column's physical name.
    private const string EvolveSeed = "story-05.4.4-name-mode-evolve";

    [Fact]
    public void EvolveNameModeMapping_AddsColumn_MintsFreshIdentity_AndBumpsMaxColumnId()
    {
        // #541 minting: an additive name-mode evolution REUSES every existing column's id + physicalName
        // verbatim (never re-mints) and mints a fresh physicalName + a fresh monotonic id for the NEW column,
        // bumping maxColumnId.
        (StructType current, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(FlatSchema, new SeededPhysicalNameSource(Seed));
        Assert.Equal(3, maxColumnId);
        System.Collections.Immutable.ImmutableSortedDictionary<string, string> configuration =
            ColumnMapping.NameModeConfiguration(maxColumnId);

        // The evolved LOGICAL schema (no mapping metadata on the existing columns — mirrors the general
        // DeltaSchemaEnforcer.Reconcile output being re-mapped) adds a nullable "extra".
        var evolvedLogical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("score", DataTypes.LongType, nullable: true),
            new StructField("name", DataTypes.StringType, nullable: true),
            new StructField("extra", DataTypes.StringType, nullable: true),
        });
        string expectedExtraPhysical = new SeededPhysicalNameSource(EvolveSeed).NextPhysicalName();

        (StructType mapped, System.Collections.Immutable.ImmutableSortedDictionary<string, string> evolvedConfig) =
            ColumnMapping.EvolveNameModeMapping(
                evolvedLogical, current, configuration, new SeededPhysicalNameSource(EvolveSeed));

        // Existing columns keep their identity verbatim (golden physical names + ids 1..3).
        AssertMapping(mapped["id"], "id", 1, PhysId);
        AssertMapping(mapped["score"], "score", 2, PhysScore);
        AssertMapping(mapped["name"], "name", 3, PhysName);

        // New column: a minted (deterministic, distinct) physicalName + id = maxColumnId + 1 = 4.
        AssertMapping(mapped["extra"], "extra", 4, expectedExtraPhysical);
        Assert.NotEqual(PhysId, expectedExtraPhysical);
        Assert.NotEqual(PhysScore, expectedExtraPhysical);
        Assert.NotEqual(PhysName, expectedExtraPhysical);

        // Configuration maxColumnId bumped to 4; mode preserved.
        Assert.Equal("4", evolvedConfig[ColumnMapping.MaxColumnIdKey]);
        Assert.Equal("name", evolvedConfig[ColumnMapping.ModeKey]);
    }

    [Fact]
    public void EvolveNameModeMapping_WidenedColumn_KeepsIdentity_AndPreservesMetadata()
    {
        // #541: an APPLIED type widening changes an existing column's type but NOT its identity — its id +
        // physicalName are reused, any delta.typeChanges / comment metadata the merged field carries is
        // preserved, and maxColumnId is unchanged (no new column is minted).
        var logical = new StructType(new[] { new StructField("value", DataTypes.IntegerType, nullable: true) });
        (StructType current, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(logical, new SeededPhysicalNameSource(Seed));
        string valuePhysical = ColumnMapping.PhysicalName(current["value"], ColumnMappingMode.Name);
        System.Collections.Immutable.ImmutableSortedDictionary<string, string> configuration =
            ColumnMapping.NameModeConfiguration(maxColumnId);

        // The merged field carries the WIDER type + a comment (standing in for the delta.typeChanges an
        // applied widening records) — none of which the evolution mapping may drop.
        FieldMetadata comment = FieldMetadata.FromEntries(
            new[] { new KeyValuePair<string, string>("comment", "widened") });
        var merged = new StructType(new[] { new StructField("value", DataTypes.LongType, nullable: true, comment) });

        (StructType mapped, System.Collections.Immutable.ImmutableSortedDictionary<string, string> evolvedConfig) =
            ColumnMapping.EvolveNameModeMapping(
                merged, current, configuration, new SeededPhysicalNameSource("unused-no-new-columns"));

        AssertMapping(mapped["value"], "value", 1, valuePhysical); // identity preserved
        Assert.Equal(DataTypes.LongType, mapped["value"].DataType); // type widened
        Assert.True(mapped["value"].Metadata.TryGetString("comment", out string? c) && c == "widened");
        Assert.Equal("1", evolvedConfig[ColumnMapping.MaxColumnIdKey]); // no new column ⇒ unchanged
    }

    [Fact]
    public async Task NameMode_Append_AddNewColumn_MintsAndCommitsEvolvedMapping_Issue541()
    {
        // #541 end-to-end (write path): appending an additive nullable column to a name-mode table with
        // SchemaEvolutionMode.AddNewColumns mints a fresh physicalName+id for the new column, bumps
        // maxColumnId, and commits the evolved mapped metaData atomically with the add. The subsequent snapshot
        // LOAD runs ValidateNameModeSchema, so a successful load proves the evolved schema is a CONSISTENT
        // name-mode schema (unique physical names, every id ≤ maxColumnId).
        await CreateNameMappedAsync((1L, 100L, "alice"), (2L, 200L, "bob"));
        string expectedExtraPhysical = new SeededPhysicalNameSource(EvolveSeed).NextPhysicalName();

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch), new SeededPhysicalNameSource(EvolveSeed));
        Snapshot readSnapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        DeltaCommitResult result = await writer.AppendAsync(
            readSnapshot,
            EvolvedFlatSchema,
            new[] { StagedNoSchema("part-extra.parquet") },
            SchemaEvolutionMode.AddNewColumns);
        Assert.Equal(readSnapshot.Version + 1, result.Version);

        // The evolved metaData + add commit in ONE version.
        IReadOnlyList<DeltaAction> committed =
            await new DeltaLog(backend).ReadCommitActionsAsync(result.Version, CancellationToken.None);
        Assert.Single(committed.OfType<MetadataAction>());
        Assert.Equal("part-extra.parquet", Assert.Single(committed.OfType<AddFileAction>()).Path);

        // Reload (ValidateNameModeSchema runs on load): existing columns keep identity; the new column carries
        // a minted physicalName + id 4; maxColumnId bumped to 4.
        Snapshot evolved = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        Assert.Equal(4, evolved.Schema.Count);
        AssertMapping(evolved.Schema["id"], "id", 1, PhysId);
        AssertMapping(evolved.Schema["score"], "score", 2, PhysScore);
        AssertMapping(evolved.Schema["name"], "name", 3, PhysName);
        AssertMapping(evolved.Schema["extra"], "extra", 4, expectedExtraPhysical);
        Assert.True(evolved.Schema["extra"].Nullable);
        Assert.Equal("4", evolved.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
        Assert.Equal("name", evolved.Metadata.Configuration[ColumnMapping.ModeKey]);
    }

    [Fact]
    public async Task NameMode_Overwrite_AddNewColumn_MintsAndCommitsEvolvedMapping_Issue541()
    {
        // #541 end-to-end (overwrite path): mirrors the append case — a name-mode Static overwrite that adds a
        // nullable column mints a fresh physicalName+id, bumps maxColumnId, and commits the evolved mapped
        // metaData (removing prior files) in one version; the reload proves a consistent name-mode schema.
        await CreateNameMappedAsync((1L, 100L, "alice"), (2L, 200L, "bob"));
        string expectedExtraPhysical = new SeededPhysicalNameSource(EvolveSeed).NextPhysicalName();

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch), new SeededPhysicalNameSource(EvolveSeed));
        Snapshot readSnapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        DeltaCommitResult result = await writer.OverwriteAsync(
            readSnapshot,
            EvolvedFlatSchema,
            new[] { StagedNoSchema("part-extra-ovr.parquet") },
            PartitionOverwriteMode.Static,
            SchemaEvolutionMode.AddNewColumns);
        Assert.Equal(readSnapshot.Version + 1, result.Version);

        Snapshot evolved = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        Assert.Equal(4, evolved.Schema.Count);
        AssertMapping(evolved.Schema["extra"], "extra", 4, expectedExtraPhysical);
        Assert.Equal("4", evolved.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
        // The overwrite removed the prior files and added the new one (schema evolution rode in the same version).
        Assert.Single(evolved.ActiveFiles);
        Assert.Equal("part-extra-ovr.parquet", evolved.ActiveFiles[0].Path);
    }

    // FlatSchema (id, score, name) additively evolved with a nullable "extra" data column (#541).
    private static readonly StructType EvolvedFlatSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("score", DataTypes.LongType, nullable: true),
        new StructField("name", DataTypes.StringType, nullable: true),
        new StructField("extra", DataTypes.StringType, nullable: true),
    });

    // A staged data file with NO footer DataSchema, so the write-door physical cross-check is skipped — the
    // #541 tests assert the committed metaData/mapping, not the physical bytes (mirrors DeltaSchemaEvolution
    // writer tests' Staged helper).
    private static StagedDataFile StagedNoSchema(string path) =>
        new(
            path,
            System.Collections.Immutable.ImmutableSortedDictionary<string, string?>.Empty
                .WithComparers(StringComparer.Ordinal),
            Size: 1L,
            ModificationTime: 0L,
            Stats: null);

    // A second deterministic seed for a SECOND consecutive evolution's minted name (distinct from EvolveSeed).
    private const string EvolveSeed2 = "story-05.4.4-name-mode-evolve-2";

    [Fact]
    public async Task NameMode_Append_AddNewColumn_StagesUnderMintedPhysicalName_AndReadsBack_Issue541()
    {
        // The core data-integrity promise: the new column's REAL Parquet bytes land under its MINTED physical
        // name (proven because the staged file carries a footer DataSchema, so the #497 write-door cross-check
        // runs against the writer's evolved physical schema — a wrong minted name would fail closed here), and
        // reading the evolved snapshot returns the new column's value for new rows and NULL for the
        // pre-evolution rows (#497 null-fill through the name-mode physical read path).
        await CreateNameMappedAsync((1L, 100L, "alice"), (2L, 200L, "bob"));
        string mintedExtra = new SeededPhysicalNameSource(EvolveSeed).NextPhysicalName();

        var evolvedPhysical = new StructType(new[]
        {
            new StructField(PhysId, DataTypes.LongType, nullable: false),
            new StructField(PhysScore, DataTypes.LongType, nullable: true),
            new StructField(PhysName, DataTypes.StringType, nullable: true),
            new StructField(mintedExtra, DataTypes.StringType, nullable: true),
        });

        using var backend = new LocalFileSystemBackend(_root);
        StagedDataFile staged = await StagePhysicalEvolvedAsync(
            backend, "part-extra-real.parquet", evolvedPhysical, (3L, 300L, "carol", "x3"));

        var writer = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch), new SeededPhysicalNameSource(EvolveSeed));
        Snapshot readSnapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        await writer.AppendAsync(
            readSnapshot, EvolvedFlatSchema, new[] { staged }, SchemaEvolutionMode.AddNewColumns);

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(new[] { "id", "score", "name", "extra" }, info.Schema.Select(f => f.Name).ToArray());

        List<(long Id, long? Score, string? Name, string? Extra)> rows =
            await ReadEvolvedRowsAsync(source, info.Version);
        Assert.Equal(
            new (long, long?, string?, string?)[]
            {
                (1L, 100L, "alice", null), // pre-evolution row: new column null-filled (#497)
                (2L, 200L, "bob", null),
                (3L, 300L, "carol", "x3"), // new row carries the new column under its minted physical name
            },
            rows.OrderBy(r => r.Id).ToList());
    }

    [Fact]
    public async Task NameMode_Append_ConsecutiveEvolutions_BumpMaxColumnIdMonotonically_Issue541()
    {
        // #541 monotonicity across commits: two successive additive evolutions (extra id 4, then extra2 id 5)
        // each reuse all prior identities, mint a distinct physical name, and bump maxColumnId monotonically
        // (3 → 4 → 5). Each reload runs ValidateNameModeSchema, so a stale-maxColumnId or duplicate-name bug
        // fails closed.
        await CreateNameMappedAsync((1L, 100L, "alice"));
        string mintedExtra = new SeededPhysicalNameSource(EvolveSeed).NextPhysicalName();
        string mintedExtra2 = new SeededPhysicalNameSource(EvolveSeed2).NextPhysicalName();

        using var backend = new LocalFileSystemBackend(_root);

        var writer1 = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch), new SeededPhysicalNameSource(EvolveSeed));
        await writer1.AppendAsync(
            await new DeltaLog(backend).LoadSnapshotAsync(version: null),
            EvolvedFlatSchema, new[] { StagedNoSchema("v2.parquet") }, SchemaEvolutionMode.AddNewColumns);

        var twiceEvolved = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("score", DataTypes.LongType, nullable: true),
            new StructField("name", DataTypes.StringType, nullable: true),
            new StructField("extra", DataTypes.StringType, nullable: true),
            new StructField("extra2", DataTypes.StringType, nullable: true),
        });
        var writer2 = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch), new SeededPhysicalNameSource(EvolveSeed2));
        await writer2.AppendAsync(
            await new DeltaLog(backend).LoadSnapshotAsync(version: null),
            twiceEvolved, new[] { StagedNoSchema("v3.parquet") }, SchemaEvolutionMode.AddNewColumns);

        Snapshot evolved = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        Assert.Equal(5, evolved.Schema.Count);
        AssertMapping(evolved.Schema["id"], "id", 1, PhysId);
        AssertMapping(evolved.Schema["score"], "score", 2, PhysScore);
        AssertMapping(evolved.Schema["name"], "name", 3, PhysName);
        AssertMapping(evolved.Schema["extra"], "extra", 4, mintedExtra);
        AssertMapping(evolved.Schema["extra2"], "extra2", 5, mintedExtra2);
        Assert.NotEqual(mintedExtra, mintedExtra2);
        Assert.Equal("5", evolved.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
    }

    [Fact]
    public void EvolveNameModeMapping_RetainedColumnWithoutId_FailsClosed_Issue541()
    {
        // A retained name-mode column carrying a physicalName but no id is an inconsistent table — the
        // evolution fails closed (never guesses an id).
        var noId = new StructType(new[]
        {
            new StructField(
                "value", DataTypes.LongType, nullable: true,
                FieldMetadata.FromEntries(new[]
                {
                    new KeyValuePair<string, string>(ColumnMapping.PhysicalNameKey, "col-x"),
                })),
        });
        System.Collections.Immutable.ImmutableSortedDictionary<string, string> config =
            ColumnMapping.NameModeConfiguration(1);

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.EvolveNameModeMapping(noId, noId, config, new SeededPhysicalNameSource("unused")));
        Assert.Contains(ColumnMapping.IdKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EvolveNameModeMapping_MissingMaxColumnId_FailsClosed_Issue541()
    {
        // A name-mode configuration missing maxColumnId is inconsistent — the evolution cannot mint a fresh id
        // safely and fails closed.
        (StructType current, _) =
            ColumnMapping.AssignFreshMapping(FlatSchema, new SeededPhysicalNameSource(Seed));
        System.Collections.Immutable.ImmutableSortedDictionary<string, string> noMax =
            System.Collections.Immutable.ImmutableSortedDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(ColumnMapping.ModeKey, "name");

        Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.EvolveNameModeMapping(
                FlatSchema, current, noMax, new SeededPhysicalNameSource("unused")));
    }

    [Fact]
    public void EvolveNameModeMapping_NestedNewColumn_FailsClosed_Issue541()
    {
        // Nested column mapping is unsupported in this build, so an evolved schema whose NEW column is a nested
        // (struct/array/map) type is rejected fail-closed rather than minted.
        (StructType current, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(FlatSchema, new SeededPhysicalNameSource(Seed));
        System.Collections.Immutable.ImmutableSortedDictionary<string, string> config =
            ColumnMapping.NameModeConfiguration(maxColumnId);
        var nestedEvolved = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("score", DataTypes.LongType, nullable: true),
            new StructField("name", DataTypes.StringType, nullable: true),
            new StructField(
                "payload", new StructType(new[] { new StructField("x", DataTypes.LongType, nullable: true) }),
                nullable: true),
        });

        Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.EvolveNameModeMapping(
                nestedEvolved, current, config, new SeededPhysicalNameSource(EvolveSeed)));
    }

    [Fact]
    public async Task NameMode_Append_WidenColumn_KeepsIdentity_AndRecordsTypeChanges_Issue541()
    {
        // #541 applied widening at the WRITE path: on a name-mode + typeWidening-enabled table, widening an
        // existing column (int → long) keeps its id + physicalName (never re-mints), records delta.typeChanges,
        // and leaves maxColumnId unchanged (no new column). Committed metaData is proven consistent on reload.
        const string ValuePhysical = "col-11111111-2222-3333-4444-555555555555";
        string schemaJson =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"value\",\"type\":\"integer\",\"nullable\":true,\"metadata\":"
            + $"{{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"{ValuePhysical}\"}}}}]}}";
        const string ColumnMappingAndTypeWideningProtocol =
            "{\"protocol\":{\"minReaderVersion\":3,\"minWriterVersion\":7,"
            + "\"readerFeatures\":[\"columnMapping\",\"typeWidening\"],"
            + "\"writerFeatures\":[\"columnMapping\",\"typeWidening\"]}}";

        using var backend = new LocalFileSystemBackend(_root);
        byte[] commit = Encoding.UTF8.GetBytes(
            ColumnMappingAndTypeWideningProtocol + "\n"
            + NameModeMetadataLine(
                schemaJson,
                Array.Empty<string>(),
                ("delta.columnMapping.mode", "name"),
                ("delta.columnMapping.maxColumnId", "1"),
                ("delta.enableTypeWidening", "true")) + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", commit, CancellationToken.None);

        var writer = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch), new SeededPhysicalNameSource("unused-no-new-columns"));
        Snapshot readSnapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        // Stage a REAL widened file whose footer DataSchema is the retained physical column as `long`, so the
        // #497 write-door cross-check runs against the writer's evolved physical schema. A bug that staged the
        // widened bytes under the LOGICAL name (or re-mapped the retained physical name) fails closed here.
        StagedDataFile staged = await StageSingleLongColumnAsync(backend, "v1.parquet", ValuePhysical, 100L, 200L);
        await writer.AppendAsync(
            readSnapshot,
            new StructType(new[] { new StructField("value", DataTypes.LongType, nullable: true) }),
            new[] { staged },
            SchemaEvolutionMode.MergeSchema);

        Snapshot evolved = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        StructField value = evolved.Schema["value"];
        Assert.Equal(DataTypes.LongType, value.DataType); // widened
        AssertMapping(value, "value", 1, ValuePhysical); // identity preserved (id + physicalName)
        Assert.True(value.Metadata.TryGetValue("delta.typeChanges", out _)); // widening recorded
        Assert.Equal("1", evolved.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]); // no new column ⇒ unchanged

        // Read back through the name-mode read door by LOGICAL name: the widened rows resolve from the retained
        // PHYSICAL column, proving the bytes landed under it (a logical/mis-mapped landing would have failed the
        // #497 gate above, or read back nothing here).
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(new[] { "value" }, info.Schema.Select(f => f.Name).ToArray());
        var readBack = new List<long>();
        foreach (ColumnBatch b in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector v = b.SelectedColumn(0);
            for (int r = 0; r < b.LogicalRowCount; r++)
            {
                readBack.Add(v.GetValue<long>(r));
            }
        }

        Assert.Equal(new[] { 100L, 200L }, readBack.OrderBy(x => x).ToArray());
    }

    // Writes a single-column Parquet file (the given physical name, LONG) and returns a staged add carrying
    // that footer DataSchema, so the #497 write-door cross-check binds.
    private static async Task<StagedDataFile> StageSingleLongColumnAsync(
        LocalFileSystemBackend backend, string path, string physicalName, params long[] values)
    {
        var physicalSchema = new StructType(new[] { new StructField(physicalName, DataTypes.LongType, nullable: true) });
        MutableColumnVector col = ColumnVectors.Create(DataTypes.LongType, values.Length);
        foreach (long v in values)
        {
            col.AppendValue(v);
        }

        var batch = new ManagedColumnBatch(physicalSchema, new ColumnVector[] { col }, values.Length);
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await new ParquetFileWriter().WriteAsync(buffer, physicalSchema, new[] { batch }, CancellationToken.None);
            bytes = buffer.ToArray();
        }

        await backend.PutIfAbsentAsync(path, bytes, CancellationToken.None);
        return new StagedDataFile(
            path,
            System.Collections.Immutable.ImmutableSortedDictionary<string, string?>.Empty
                .WithComparers(StringComparer.Ordinal),
            Size: bytes.LongLength,
            ModificationTime: 0L,
            Stats: null,
            DataSchema: physicalSchema);
    }

    [Fact]
    public async Task NameMode_Append_WidenColumn_WithoutTypeWideningFeature_FailsClosed_Issue541()
    {
        // A widening write to a name-mode table that has NOT enabled type widening stays fail-closed (the
        // feature gate is independent of column mapping) — never a silent partial-widen or unmapped column.
        const string ValuePhysical = "col-99999999-8888-7777-6666-555555555555";
        string schemaJson =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"value\",\"type\":\"integer\",\"nullable\":true,\"metadata\":"
            + $"{{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"{ValuePhysical}\"}}}}]}}";

        using var backend = new LocalFileSystemBackend(_root);
        byte[] commit = Encoding.UTF8.GetBytes(
            ProtocolFeatureLine() + "\n" // columnMapping only — no typeWidening feature/enablement
            + NameModeMetadataLine(
                schemaJson,
                Array.Empty<string>(),
                ("delta.columnMapping.mode", "name"),
                ("delta.columnMapping.maxColumnId", "1")) + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", commit, CancellationToken.None);

        var writer = new DeltaTableWriter(backend);
        Snapshot readSnapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() => writer.AppendAsync(
            readSnapshot,
            new StructType(new[] { new StructField("value", DataTypes.LongType, nullable: true) }),
            new[] { StagedNoSchema("v1.parquet") },
            SchemaEvolutionMode.MergeSchema));

        // Fail-closed: the table is unchanged at v0.
        Assert.Equal(0L, (await new DeltaLog(backend).LoadSnapshotAsync(version: null)).Version);
    }

    [Fact]
    public async Task Overwrite_NameMode_DropColumn_RetiresId_KeepsSurvivorIdentity_Issue542()
    {
        // #542: an overwriteSchema that DROPS a column retires that column's id (never reused — maxColumnId
        // stays put) while surviving columns keep their id + physicalName. All prior data is removed.
        await CreateNameMappedAsync((1L, 100L, "alice")); // {id,score,name} ids 1..3, maxColumnId=3

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch), new SeededPhysicalNameSource(EvolveSeed));

        var dropped = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("score", DataTypes.LongType, nullable: true),
        });
        await writer.CreateOrOverwriteAsync(
            dropped, Array.Empty<string>(), new[] { StagedNoSchema("ovr-drop.parquet") },
            PartitionOverwriteMode.Static, overwriteSchema: true, CancellationToken.None);

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        Assert.Equal(2, snapshot.Schema.Count);
        AssertMapping(snapshot.Schema["id"], "id", 1, PhysId);
        AssertMapping(snapshot.Schema["score"], "score", 2, PhysScore);
        Assert.False(snapshot.Schema.TryGetField("name", out _)); // dropped
        // name's id (3) is RETIRED — maxColumnId stays 3, never reused (monotonic).
        Assert.Equal("3", snapshot.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
    }

    [Fact]
    public async Task Overwrite_NameMode_AddColumn_MintsAndReadsBack_Issue542()
    {
        // #542: an overwriteSchema that ADDS a column mints a fresh physicalName+id (bumping maxColumnId) and
        // the new column's real bytes land under the minted physical name (proven by the #497 write-door +
        // read-back). The prior data is replaced.
        await CreateNameMappedAsync((1L, 100L, "alice"));
        string mintedExtra = new SeededPhysicalNameSource(EvolveSeed).NextPhysicalName();
        var evolvedPhysical = new StructType(new[]
        {
            new StructField(PhysId, DataTypes.LongType, nullable: false),
            new StructField(PhysScore, DataTypes.LongType, nullable: true),
            new StructField(PhysName, DataTypes.StringType, nullable: true),
            new StructField(mintedExtra, DataTypes.StringType, nullable: true),
        });

        using var backend = new LocalFileSystemBackend(_root);
        StagedDataFile staged = await StagePhysicalEvolvedAsync(
            backend, "ovr-add.parquet", evolvedPhysical, (5L, 500L, "eve", "x5"));
        var writer = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch), new SeededPhysicalNameSource(EvolveSeed));
        await writer.CreateOrOverwriteAsync(
            EvolvedFlatSchema, Array.Empty<string>(), new[] { staged },
            PartitionOverwriteMode.Static, overwriteSchema: true, CancellationToken.None);

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        AssertMapping(snapshot.Schema["extra"], "extra", 4, mintedExtra);
        Assert.Equal("4", snapshot.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        List<(long Id, long? Score, string? Name, string? Extra)> rows =
            await ReadEvolvedRowsAsync(source, info.Version);
        Assert.Equal(new (long, long?, string?, string?)[] { (5L, 500L, "eve", "x5") }, rows); // prior data replaced
    }

    [Fact]
    public async Task Overwrite_NameMode_RetypeColumn_KeepsIdentity_Issue542()
    {
        // #542: an overwriteSchema that CHANGES a column's type keeps its identity (id + physicalName); the
        // new data is written under the new type (a narrowing/arbitrary retype is legal because all data is
        // rewritten). maxColumnId is unchanged (no new column).
        await CreateNameMappedAsync((1L, 100L, "alice"));

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch), new SeededPhysicalNameSource(EvolveSeed));

        var retyped = new StructType(new[]
        {
            new StructField("id", DataTypes.StringType, nullable: false), // was long
            new StructField("score", DataTypes.LongType, nullable: true),
            new StructField("name", DataTypes.StringType, nullable: true),
        });
        await writer.CreateOrOverwriteAsync(
            retyped, Array.Empty<string>(), new[] { StagedNoSchema("ovr-retype.parquet") },
            PartitionOverwriteMode.Static, overwriteSchema: true, CancellationToken.None);

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        AssertMapping(snapshot.Schema["id"], "id", 1, PhysId); // identity preserved across the type change
        Assert.Equal(DataTypes.StringType, snapshot.Schema["id"].DataType); // retyped
        Assert.Equal("3", snapshot.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]); // no new column
    }

    // Writes a REAL Parquet data file under the evolved PHYSICAL schema (existing physical names + the minted
    // one) and returns a staged add carrying that footer DataSchema, so the #497 write-door cross-check runs.
    private static async Task<StagedDataFile> StagePhysicalEvolvedAsync(
        LocalFileSystemBackend backend, string path, StructType evolvedPhysical,
        (long Id, long Score, string? Name, string? Extra) row)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 1);
        MutableColumnVector score = ColumnVectors.Create(DataTypes.LongType, 1);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, 1);
        MutableColumnVector extra = ColumnVectors.Create(DataTypes.StringType, 1);
        id.AppendValue(row.Id);
        score.AppendValue(row.Score);
        name.AppendBytes(Encoding.UTF8.GetBytes(row.Name!));
        extra.AppendBytes(Encoding.UTF8.GetBytes(row.Extra!));
        var batch = new ManagedColumnBatch(evolvedPhysical, new ColumnVector[] { id, score, name, extra }, 1);

        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await new ParquetFileWriter().WriteAsync(buffer, evolvedPhysical, new[] { batch }, CancellationToken.None);
            bytes = buffer.ToArray();
        }

        await backend.PutIfAbsentAsync(path, bytes, CancellationToken.None);
        return new StagedDataFile(
            path,
            System.Collections.Immutable.ImmutableSortedDictionary<string, string?>.Empty
                .WithComparers(StringComparer.Ordinal),
            Size: bytes.LongLength,
            ModificationTime: 0L,
            Stats: null,
            DataSchema: evolvedPhysical);
    }

    private static async Task<List<(long Id, long? Score, string? Name, string? Extra)>> ReadEvolvedRowsAsync(
        DeltaReadSource source, long version)
    {
        var rows = new List<(long, long?, string?, string?)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(version))
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector score = batch.SelectedColumn(1);
            ColumnVector name = batch.SelectedColumn(2);
            ColumnVector extra = batch.SelectedColumn(3);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((
                    id.GetValue<long>(r),
                    score.IsNull(r) ? null : score.GetValue<long>(r),
                    name.IsNull(r) ? null : Encoding.UTF8.GetString(name.GetBytes(r)),
                    extra.IsNull(r) ? null : Encoding.UTF8.GetString(extra.GetBytes(r))));
            }
        }

        return rows;
    }

    // ---------------------------------------------------------------- helpers

    private static void AssertMapping(StructField field, string logicalName, long id, string physicalName)
    {
        Assert.Equal(logicalName, field.Name);
        Assert.True(ColumnMapping.TryGetId(field, out long actualId));
        Assert.Equal(id, actualId);
        Assert.Equal(physicalName, ColumnMapping.PhysicalName(field, ColumnMappingMode.Name));
    }

    private async Task CreateNameMappedAsync(params (long Id, long Score, string? Name)[] rows)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames());
        await target.CreateNameMappedTableAsync(
            FlatSchema, Array.Empty<string>(), new[] { FlatBatch(rows) }, new SeededPhysicalNameSource(Seed));
    }

    private static ColumnBatch FlatBatch((long Id, long Score, string? Name)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector score = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((long i, long s, string? n) in rows)
        {
            id.AppendValue(i);
            score.AppendValue(s);
            if (n is null)
            {
                name.AppendNull();
            }
            else
            {
                name.AppendBytes(Encoding.UTF8.GetBytes(n));
            }
        }

        return new ManagedColumnBatch(FlatSchema, new ColumnVector[] { id, score, name }, rows.Length);
    }

    private static async Task<List<(long Id, long? Score, string? Name)>> ReadRowsAsync(
        DeltaReadSource source, long version)
    {
        var rows = new List<(long, long?, string?)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(version))
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector score = batch.SelectedColumn(1);
            ColumnVector name = batch.SelectedColumn(2);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((
                    id.GetValue<long>(r),
                    score.IsNull(r) ? null : score.GetValue<long>(r),
                    name.IsNull(r) ? null : Encoding.UTF8.GetString(name.GetBytes(r))));
            }
        }

        return rows;
    }

    // Reads an (id, name) two-column snapshot (post-drop), padding the middle score as null for comparison.
    private static async Task<List<(long, long?, string?)>> ReadNarrowRowsAsync(
        DeltaReadSource source, long version)
    {
        var rows = new List<(long, long?, string?)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(version))
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector name = batch.SelectedColumn(1);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((
                    id.GetValue<long>(r),
                    null,
                    name.IsNull(r) ? null : Encoding.UTF8.GetString(name.GetBytes(r))));
            }
        }

        return rows;
    }

    // Reads the committed version-0 metaData action and returns its DECODED schemaString (the raw schema
    // JSON, with delta.columnMapping.id as an unquoted integer and physicalName as a string).
    private async Task<string> ReadCommittedSchemaStringAsync()
    {
        string[] lines = await File.ReadAllLinesAsync(
            Path.Combine(_root, "_delta_log", "00000000000000000000.json"));
        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            using var document = System.Text.Json.JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("metaData", out System.Text.Json.JsonElement metadata))
            {
                return metadata.GetProperty("schemaString").GetString()!;
            }
        }

        throw new Xunit.Sdk.XunitException("No metaData action found in the version-0 commit.");
    }

    private async Task<string[]> ReadParquetColumnNamesAsync(string relativePath)
    {
        await using FileStream stream = File.OpenRead(Path.Combine(_root, relativePath));
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        return reader.Schema.DataFields.Select(f => f.Name).ToArray();
    }

    // The physical (footer) column names of an in-memory Parquet file, in file order — used to confirm the
    // 1b reorder test's own premise (that the authored file really is in reversed physical order).
    private static async Task<string[]> ParquetColumnNamesAsync(byte[] parquetBytes)
    {
        using var stream = new MemoryStream(parquetBytes);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
        return reader.Schema.DataFields.Select(f => f.Name).ToArray();
    }

    // A deterministic, counter-based data-file name source (so golden paths are stable).
    private static Func<string> FileNames()
    {
        int counter = 0;
        return () => "file" + (counter++).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task WriteRawTableAsync(string protocol, string metadata)
    {
        using var backend = new LocalFileSystemBackend(_root);
        byte[] content = Encoding.UTF8.GetBytes(protocol + "\n" + metadata + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", content, CancellationToken.None);
    }

    private static string ProtocolFeatureLine() =>
        """{"protocol":{"minReaderVersion":3,"minWriterVersion":7,"readerFeatures":["columnMapping"],"writerFeatures":["columnMapping"]}}""";

    private static string MetadataLine(params (string Key, string Value)[] configuration)
    {
        string config = "{" + string.Join(",", configuration.Select(kv => $"\"{kv.Key}\":\"{kv.Value}\"")) + "}";
        const string emptySchema = "{\\\"type\\\":\\\"struct\\\",\\\"fields\\\":[]}";
        return "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
            + "\"schemaString\":\"" + emptySchema + "\",\"partitionColumns\":[],\"configuration\":" + config + "}}";
    }

    // Builds a raw metaData commit line for a name-mode table from a plaintext (unescaped) schema JSON. The
    // schemaString is a JSON-string-encoded copy of the schema (System.Text.Json handles the escaping), and
    // partitionColumns hold the LOGICAL names — the exact shape a poisoned/malformed name-mode table has.
    private static string NameModeMetadataLine(
        string schemaJson, string[] partitionColumns, params (string Key, string Value)[] configuration)
    {
        string escapedSchema = System.Text.Json.JsonSerializer.Serialize(schemaJson);
        string config = "{" + string.Join(",", configuration.Select(kv => $"\"{kv.Key}\":\"{kv.Value}\"")) + "}";
        string parts = "[" + string.Join(",", partitionColumns.Select(p => $"\"{p}\"")) + "]";
        return "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
            + "\"schemaString\":" + escapedSchema + ",\"partitionColumns\":" + parts
            + ",\"configuration\":" + config + "}}";
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
