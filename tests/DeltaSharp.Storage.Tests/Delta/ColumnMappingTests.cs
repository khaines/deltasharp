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

    // ---------------------------------------------------------------- HIGH #2: append/overwrite fail-closed

    // RejectExistingNameModeMutation guards FOUR write entry points on DeltaTableWriter (AppendAsync,
    // OverwriteAsync, CreateOrAppendAsync, CreateOrOverwriteAsync). Each MUST fail closed with a typed
    // DeltaProtocolException (citing #525) and leave the table at its prior version (no corrupt commit);
    // parametrizing gives every entry point its own oracle, so a targeted removal from any single path
    // (e.g. just the overwrite door) turns THIS test red rather than shipping green.
    [Theory]
    [InlineData("Append")]
    [InlineData("Overwrite")]
    [InlineData("CreateOrAppend")]
    [InlineData("CreateOrOverwrite")]
    public async Task WriteToNameModeTable_IsRejectedFailClosed(string entryPoint)
    {
        await CreateNameMappedAsync((1L, 100L, "alice"));

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);

        // Stage a lone data file (partition-free) and try to mutate the existing name-mode table via the
        // entry point under test: each must be rejected fail-closed (a silent, corrupt commit is the bug).
        var staged = new[]
        {
            new StagedDataFile(
                "part-appended.parquet",
                System.Collections.Immutable.ImmutableSortedDictionary<string, string?>.Empty,
                Size: 1,
                ModificationTime: 0,
                Stats: null),
        };

        Task<DeltaCommitResult> Attempt() => entryPoint switch
        {
            "Append" => writer.AppendAsync(FlatSchema, staged),
            "Overwrite" => writer.OverwriteAsync(FlatSchema, staged),
            "CreateOrAppend" => writer.CreateOrAppendAsync(FlatSchema, Array.Empty<string>(), staged),
            "CreateOrOverwrite" => writer.CreateOrOverwriteAsync(FlatSchema, Array.Empty<string>(), staged),
            _ => throw new Xunit.Sdk.XunitException($"Unknown entry point '{entryPoint}'."),
        };

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(Attempt);
        Assert.Contains("525", ex.Message, StringComparison.Ordinal);
        Assert.Contains("name-mode", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The rejected mutation must NOT have committed: the table is still at v0 with its original row.
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(0L, info.Version);
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
