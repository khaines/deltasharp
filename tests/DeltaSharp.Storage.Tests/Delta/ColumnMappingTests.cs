using System.Text;
using System.Text.Json.Nodes;
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
/// <item><b>AC4</b> (EE-09) — column mapping without protocol support and a legacy reader-v2 table are
/// rejected fail-closed with a typed error. An <c>id</c>-mode table is fully read AND written (#523 read /
/// #572 write: create/append/overwrite/delete, columns resolved by the Parquet <c>field_id</c>); out-of-scope
/// id-mode shapes (nested top-level columns) stay fail-closed.</item>
/// </list>
/// The physical names are minted by a deterministic seeded source so the AC3 assertions are golden.
/// </summary>
/// <remarks>
/// FIX #7 (flaky-safety-test isolation): these tests are placed in a NON-parallel xUnit collection. The
/// fail-closed safety tests (e.g. <see cref="IdMode_DuplicateColumnMappingId_IsRejectedFailClosed"/>) must
/// never present red in CI, and a raw-write column-mapping test flaked once under xUnit parallel execution on
/// the shared temp filesystem. Disabling parallelization for this collection removes that harness race while
/// keeping each test's per-instance temp directory.
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

    // ---------------------------------------------------------------- #616: ALTER DROP/RENAME dependent-CHECK guard
    //
    // DropColumnAsync/RenameColumnAsync refuse fail-closed when a surviving named CHECK depends on the
    // removed/renamed column (the dangling-CHECK brick #601/#598 guard, on the ALTER door). These tests use a
    // fake IWriteConstraintEnforcer to prove the WIRING — the ALTER door collects the surviving CHECK, runs the
    // enforcer's resolve pass over the constraint SET against the POST-ALTER schema with NO batches, and
    // propagates the rejection before any commit; and, mirroring the write door's anti-bypass net, refuses
    // fail-closed when constraints exist but no enforcer is wired. The enforcer's REAL dangling-CHECK resolution
    // (real DeltaLocalSink through the ALTER door) is proven by
    // AlterColumnDependentCheckEndToEndTests (Executor), and the identical null-priorSchema Phase-1 by
    // DeltaSinkNestedDropReclassificationTests.

    [Fact]
    public async Task DropColumn_DependentCheck_EnforcerRejectsFailClosed_NoCommit()
    {
        await CreateNameMappedAsync((1L, 100L, "alice"));
        AddCheckConstraintAtV1("score_positive", "score > 0"); // v1: a named CHECK referencing `score`

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);
        var enforcer = new RecordingConstraintEnforcer(reject: true);

        await Assert.ThrowsAsync<DeltaConstraintDependentColumnException>(
            () => writer.DropColumnAsync("score", enforcer));

        // The guard ran over the POST-DROP schema (no `score`) with the surviving CHECK and NO batches, and the
        // rejection aborted the write — no v2 drop commit, so the table is not bricked.
        Assert.Equal(1, enforcer.Calls);
        Assert.False(enforcer.Schema!.TryGetField("score", out _));
        Assert.Equal(new[] { "id", "name" }, enforcer.Schema.Select(f => f.Name).ToArray());
        Assert.Equal("score_positive", Assert.Single(enforcer.Constraints!).Name);
        Assert.Empty(enforcer.Batches!);
        Assert.Equal(1L, (await new DeltaLog(backend).LoadSnapshotAsync(version: null)).Version); // still v1 (no drop committed)
    }

    [Fact]
    public async Task RenameColumn_DependentCheck_EnforcerRejectsFailClosed_NoCommit()
    {
        await CreateNameMappedAsync((1L, 100L, "alice"));
        AddCheckConstraintAtV1("score_positive", "score > 0");

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);
        var enforcer = new RecordingConstraintEnforcer(reject: true);

        await Assert.ThrowsAsync<DeltaConstraintDependentColumnException>(
            () => writer.RenameColumnAsync("score", "points", enforcer));

        // The guard saw the POST-RENAME schema (`points`, not `score`) — a CHECK on `score` would dangle.
        Assert.Equal(1, enforcer.Calls);
        Assert.False(enforcer.Schema!.TryGetField("score", out _));
        Assert.True(enforcer.Schema.TryGetField("points", out _));
        Assert.Equal(1L, (await new DeltaLog(backend).LoadSnapshotAsync(version: null)).Version); // still v1 (no rename committed)
    }

    [Fact]
    public async Task DropColumn_SurvivingUnrelatedCheck_EnforcerAccepts_Commits()
    {
        // Happy path: a CHECK on a SURVIVING column (`id`, not the dropped `score`) resolves clean, so the guard
        // RUNS (Calls==1) but does not block — the DROP commits. Proves the guard is non-blocking on a clean
        // resolve (not merely skipped when there are zero constraints).
        await CreateNameMappedAsync((1L, 100L, "alice"));
        AddCheckConstraintAtV1("id_positive", "id > 0"); // references `id`, which SURVIVES the drop of `score`

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);
        var enforcer = new RecordingConstraintEnforcer(); // reject: false — resolves clean

        DeltaCommitResult drop = await writer.DropColumnAsync("score", enforcer);

        Assert.Equal(1, enforcer.Calls); // the resolvability pass ran over the surviving CHECK
        Assert.Equal("id_positive", Assert.Single(enforcer.Constraints!).Name);
        Assert.Equal(2L, drop.Version); // committed — the CHECK on `id` still resolves against the post-drop schema
    }

    [Fact]
    public async Task DropColumn_NoConstraints_EnforcerNotInvoked_Commits()
    {
        await CreateNameMappedAsync((1L, 100L, "alice")); // no CHECK constraints

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);
        var enforcer = new RecordingConstraintEnforcer();

        DeltaCommitResult drop = await writer.DropColumnAsync("score", enforcer);

        Assert.Equal(0, enforcer.Calls); // no surviving constraints → the resolvability pass is skipped
        Assert.Equal(1L, drop.Version); // drop committed normally
    }

    [Fact]
    public async Task DropColumn_NullEnforcer_WithConstraints_RefusedFailClosed()
    {
        // Anti-bypass net (mirrors the write door, which throws when constraints exist and no enforcer is wired):
        // a table WITH active constraints but NO enforcer supplied is refused fail-closed rather than silently
        // committing a potential dangling-CHECK brick. (An unconstrained table needs no enforcer — see
        // DropColumn_IsMetadataOnly_* / RenameColumn_IsMetadataOnly_*, which drop/rename with no enforcer.)
        await CreateNameMappedAsync((1L, 100L, "alice"));
        AddCheckConstraintAtV1("score_positive", "score > 0");

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(backend);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.DropColumnAsync("score", constraintEnforcer: null));
        Assert.Equal(1L, (await new DeltaLog(backend).LoadSnapshotAsync(version: null)).Version); // no drop committed
    }

    // Adds a named CHECK constraint to the freshly-created (v0) table as a v1 metadata-only commit (mirrors the
    // config a `ALTER TABLE ADD CONSTRAINT` would set: delta.constraints.<name> = <expression>).
    private void AddCheckConstraintAtV1(string name, string expression)
    {
        string logDir = Path.Combine(_root, "_delta_log");
        string metaLine = File.ReadAllLines(Path.Combine(logDir, $"{0:D20}.json"))
            .First(line => line.Contains("\"metaData\"", StringComparison.Ordinal));
        JsonNode root = JsonNode.Parse(metaLine)!;
        JsonObject metadata = root["metaData"]!.AsObject();
        if (metadata["configuration"] is not JsonObject configuration)
        {
            configuration = new JsonObject();
            metadata["configuration"] = configuration;
        }

        configuration[$"delta.constraints.{name}"] = expression;
        File.WriteAllText(Path.Combine(logDir, $"{1:D20}.json"), root.ToJsonString() + "\n");
    }

    // A fake IWriteConstraintEnforcer that records the (schema, constraints, batches) it is handed and,
    // when constructed with reject: true, simulates a surviving CHECK that depends on the altered column by
    // throwing the same DeltaConstraintDependentColumnException the real enforcer's resolve pass raises.
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
                throw DeltaConstraintDependentColumnException.ForColumnChange("score", new[] { constraints[0] });
            }
        }
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
    public async Task IdMode_EmptyTable_LoadsSuccessfully_Since523()
    {
        // Since #523 an id-mode table is READABLE (columns resolved by Parquet field_id), so an id-mode
        // table LOADS instead of failing closed at snapshot load. Since #572 it is also WRITABLE — see the
        // IdModeWrite_* create/append/overwrite and DELETE id-mode tests.
        await WriteRawTableAsync(
            protocol: ProtocolFeatureLine(),
            metadata: MetadataLine(("delta.columnMapping.mode", "id"), ("delta.columnMapping.maxColumnId", "2")));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null); // no throw
        Assert.Equal(0L, info.Version);
    }

    [Fact]
    public async Task IdMode_ReadsByFieldId_IgnoringPhysicalNameAndPosition()
    {
        // #523 — the direct closer for id mode: each LOGICAL column resolves to a Parquet column by its
        // delta.columnMapping.id matched against the file's field_id — NEVER by physical name and NEVER by
        // position. The authored physical Parquet (a) stores its columns in REVERSED order and (b) names them
        // "x0"/"x1" — names that do NOT match the metaData physicalNames — but each is stamped with the
        // correct field_id. A positional read would swap id<->score; a name-based read would not find the
        // physicalNames at all. Only field_id resolution returns the right values.
        const string relativePath = "idmode-data.parquet";

        // Physical file, reversed: [x0 (field_id=2 = logical "score", 100/200), x1 (field_id=1 = logical
        // "id", 10/20)].
        var physicalSchema = new StructType(new[]
        {
            PhysFieldWithId("x0", DataTypes.LongType, nullable: true, id: 2),
            PhysFieldWithId("x1", DataTypes.LongType, nullable: false, id: 1),
        });
        MutableColumnVector x0 = ColumnVectors.Create(DataTypes.LongType, 2);
        MutableColumnVector x1 = ColumnVectors.Create(DataTypes.LongType, 2);
        x0.AppendValue(100L);
        x1.AppendValue(10L);
        x0.AppendValue(200L);
        x1.AppendValue(20L);
        var physicalBatch = new ManagedColumnBatch(physicalSchema, new ColumnVector[] { x0, x1 }, 2);

        byte[] parquetBytes;
        using (var buffer = new MemoryStream())
        {
            await new ParquetFileWriter().WriteAsync(
                buffer, physicalSchema, new[] { physicalBatch }, CancellationToken.None);
            parquetBytes = buffer.ToArray();
        }

        // Guard the premise: the on-disk file really has field_ids in the reversed order [2, 1].
        Assert.Equal(new[] { "x0", "x1" }, await ParquetColumnNamesAsync(parquetBytes));

        // id-mode metaData: logical id(id=1)/score(id=2); physicalNames are col-uuids that DO NOT match the
        // Parquet column names, so a name-based read could not resolve them — only field_id works.
        string schemaJson =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + $"{{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"{PhysId}\"}}}},"
            + "{\"name\":\"score\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
            + $"{{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"{PhysScore}\"}}}}]}}";

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
                    ("delta.columnMapping.mode", "id"),
                    ("delta.columnMapping.maxColumnId", "2")) + "\n"
                + addLine + "\n");
            await backend.PutIfAbsentAsync(
                "_delta_log/00000000000000000000.json", commit, CancellationToken.None);
        }

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(new[] { "id", "score" }, info.Schema.Select(f => f.Name).ToArray());

        int idIdx = info.Schema.IndexOf("id");
        int scoreIdx = info.Schema.IndexOf("score");
        var rows = new List<(long Id, long Score)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector idCol = batch.SelectedColumn(idIdx);
            ColumnVector scoreCol = batch.SelectedColumn(scoreIdx);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((idCol.GetValue<long>(r), scoreCol.GetValue<long>(r)));
            }
        }

        // logical "id" ← field_id=1 (10/20); logical "score" ← field_id=2 (100/200). A positional/name misread
        // would swap these or fail to resolve.
        Assert.Equal(new[] { (10L, 100L), (20L, 200L) }, rows.OrderBy(r => r.Id).ToArray());
    }

    [Fact]
    public void IdModeConfiguration_And_Protocol_HaveExpectedShape()
    {
        // #572: id-mode WRITE is enabled. Pin the metaData.configuration + protocol the fresh-create path
        // commits — mode=id, tracked maxColumnId, and the columnMapping feature declared in BOTH the reader
        // and writer feature sets (reader v3 / writer v7). The protocol is mode-independent (byte-identical to
        // name mode); the mode lives in the configuration.
        var config = ColumnMapping.IdModeConfiguration(3);
        Assert.Equal("id", config[ColumnMapping.ModeKey]);
        Assert.Equal("3", config[ColumnMapping.MaxColumnIdKey]);

        ProtocolAction protocol = ColumnMapping.IdModeProtocol();
        Assert.Equal(3, protocol.MinReaderVersion);
        Assert.Equal(7, protocol.MinWriterVersion);
        Assert.Contains("columnMapping", protocol.ReaderFeatures);
        Assert.Contains("columnMapping", protocol.WriterFeatures);

        // None/Name remain writable through their own config/protocol builders (unaffected).
        Assert.Equal("name", ColumnMapping.NameModeConfiguration(3)[ColumnMapping.ModeKey]);
    }

    // ================================================================ #572: id-mode WRITE (create/append/overwrite)

    // The id-mode sibling of NameModeWrite_EmitsConsistentPhysicalAndLogicalMetadata (AC3): a fresh id-mode
    // create commits mode=id / maxColumnId, the columnMapping protocol feature, a logical schema carrying
    // per-field id+physicalName, and — the id-mode teeth — a Parquet footer whose columns are the PHYSICAL
    // names AND carry the stamped field_id (= delta.columnMapping.id). This is the write half of #523's read;
    // together they are the full DeltaSharp id-mode self-round-trip.
    [Fact]
    public async Task IdModeWrite_EmitsConsistentPhysicalAndLogicalMetadata()
    {
        await CreateIdMappedAsync((1L, 100L, "alice"), (2L, 200L, "bob"));

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        // Configuration: id mode + tracked maxColumnId (= column count).
        Assert.Equal("id", snapshot.Metadata.Configuration[ColumnMapping.ModeKey]);
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
        Assert.True(snapshot.Metadata.PartitionColumns.IsDefaultOrEmpty);

        // The committed metaData.schemaString is byte-identical to the name-mode golden EXCEPT mode=id lives
        // in the configuration — the per-field id (unquoted int) + physicalName shape is the SAME.
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

        // The Parquet footer stores the PHYSICAL column names (not the logical display names).
        string[] parquetColumns = await ReadParquetColumnNamesAsync(snapshot.ActiveFiles[0].Path);
        Assert.Equal(new[] { PhysId, PhysScore, PhysName }, parquetColumns);

        // The id-mode teeth: each PHYSICAL column also carries the stamped field_id (= its columnMapping.id),
        // so a foreign / id-mode reader resolves columns by field_id (name mode would NOT stamp these).
        Dictionary<string, int> fieldIds = await ReadParquetFieldIdsAsync(snapshot.ActiveFiles[0].Path);
        Assert.Equal(1, fieldIds[PhysId]);
        Assert.Equal(2, fieldIds[PhysScore]);
        Assert.Equal(3, fieldIds[PhysName]);
    }

    // Full DeltaSharp id-mode SELF-ROUND-TRIP: WRITE an id-mode table (create) then READ it back by field_id
    // through the public read door — values, schema, and (below, partitioned test) partition values correct.
    [Fact]
    public async Task IdMode_CreateThenReadBack_ByFieldId_RoundTrips()
    {
        await CreateIdMappedAsync((1L, 100L, "alice"), (2L, 200L, "bob"), (3L, 300L, null));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(new[] { "id", "score", "name" }, info.Schema.Select(f => f.Name).ToArray());

        List<(long Id, long? Score, string? Name)> rows = await ReadRowsAsync(source, info.Version);
        Assert.Equal(
            new (long, long?, string?)[] { (1L, 100L, "alice"), (2L, 200L, "bob"), (3L, 300L, null) },
            rows.OrderBy(r => r.Id).ToList());
    }

    // The id-mode sibling of NameMode_Append_ReadsBackAllRows_AndAppendedFileCarriesPhysicalNames (#572): an
    // append to a DeltaSharp-written id-mode table stages under the table's EXISTING physical names WITH the
    // field_id stamped, and read-back by field_id returns every row.
    [Fact]
    public async Task IdMode_Append_ReadsBackAllRows_AndAppendedFileCarriesFieldIds()
    {
        Func<string> names = FileNames();

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateIdMappedTableAsync(
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

        // EVERY active data file (create + append) physically stores the col-<uuid> PHYSICAL names AND the
        // stamped field_id — a corrupt append would land a logical-named / field_id-free file here.
        Assert.Equal(2, snapshot.ActiveFiles.Length);
        foreach (AddFileAction add in snapshot.ActiveFiles)
        {
            Assert.Equal(new[] { PhysId, PhysScore, PhysName }, await ReadParquetColumnNamesAsync(add.Path));
            Dictionary<string, int> fieldIds = await ReadParquetFieldIdsAsync(add.Path);
            Assert.Equal(1, fieldIds[PhysId]);
            Assert.Equal(2, fieldIds[PhysScore]);
            Assert.Equal(3, fieldIds[PhysName]);
        }

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        List<(long Id, long? Score, string? Name)> rows = await ReadRowsAsync(source, info.Version);
        Assert.Equal(
            new (long, long?, string?)[] { (1L, 100L, "alice"), (2L, 200L, "bob"), (3L, 300L, null) },
            rows.OrderBy(r => r.Id).ToList());
    }

    // A FlatSchema variant whose `score` field declares a column invariant (delta.invariants), used to prove
    // the internal fresh-create seams refuse fail-closed rather than create a constrained table unenforced.
    private static StructType ConstrainedFlatSchema() => new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField(
            "score", DataTypes.LongType, nullable: true,
            FieldMetadata.FromEntries(new[]
            {
                new KeyValuePair<string, string>("delta.invariants", "{\"expression\":{\"expression\":\"score > 0\"}}"),
            })),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    // #572 blocking finding 2 (write-path constraint enforcement): the id-mode create seam is a NEW write
    // path, so — exactly like the name-mode / DV create seams (#581) — it must NOT be a per-row-constraint
    // enforcement bypass. A fresh id-mode create whose schema declares a column invariant, with no enforcer
    // wired, is refused fail-closed (RejectUnenforceableCreate) rather than committing unvalidated rows.
    [Fact]
    public async Task IdMode_CreateWithColumnInvariant_NoEnforcer_RefusedFailClosed()
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames());
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.CreateIdMappedTableAsync(
            ConstrainedFlatSchema(), Array.Empty<string>(),
            new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
            new SeededPhysicalNameSource(Seed)));

        // Refused before staging: no version-0 commit was written.
        Assert.False(File.Exists(Path.Combine(_root, "_delta_log", "00000000000000000000.json")));
    }

    // Every INTERNAL fresh-create seam bypasses the shared AppendAsync enforcement (it stages + commits
    // directly to inject a mapped / deletion-vector protocol), so EACH must independently refuse fail-closed a
    // constrained schema with no enforcer (#581 RejectUnenforceableCreate). Pins all five seams — name / id /
    // name-DV / id-DV / DV — so a future regression that drops the guard from any one is caught (#572 council
    // Quality/Security). The guard fires before TableExists staging, so all five run on one fresh root.
    [Fact]
    public async Task AllInternalCreateSeams_WithColumnInvariant_NoEnforcer_RefusedFailClosed()
    {
        StructType constrained = ConstrainedFlatSchema();
        ColumnBatch[] Batch() => new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) };

        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames());

        await Assert.ThrowsAsync<InvalidOperationException>(() => target.CreateNameMappedTableAsync(
            constrained, Array.Empty<string>(), Batch(), new SeededPhysicalNameSource(Seed)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.CreateIdMappedTableAsync(
            constrained, Array.Empty<string>(), Batch(), new SeededPhysicalNameSource(Seed)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.CreateNameMappedDeletionVectorTableAsync(
            constrained, Array.Empty<string>(), Batch(), new SeededPhysicalNameSource(Seed)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.CreateIdMappedDeletionVectorTableAsync(
            constrained, Array.Empty<string>(), Batch(), new SeededPhysicalNameSource(Seed)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.CreateDeletionVectorTableAsync(
            constrained, Array.Empty<string>(), Batch()));

        // No seam committed a version-0 create for the constrained schema.
        Assert.False(File.Exists(Path.Combine(_root, "_delta_log", "00000000000000000000.json")));
    }

    // The id-mode sibling of NameMode_Overwrite_ReadsBackNewRows (#572): OVERWRITE (Static) an existing
    // id-mode table replaces its rows; the new file carries PHYSICAL names + stamped field_ids and read-back
    // returns only the new rows.
    [Fact]
    public async Task IdMode_Overwrite_ReadsBackNewRows()
    {
        Func<string> names = FileNames();

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateIdMappedTableAsync(
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
        Dictionary<string, int> fieldIds = await ReadParquetFieldIdsAsync(snapshot.ActiveFiles[0].Path);
        Assert.Equal(new[] { 1, 2, 3 }, new[] { fieldIds[PhysId], fieldIds[PhysScore], fieldIds[PhysName] });

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        List<(long Id, long? Score, string? Name)> rows = await ReadRowsAsync(source, info.Version);
        Assert.Equal(new (long, long?, string?)[] { (9L, 900L, "zoe") }, rows);
    }

    // The id-mode sibling of NameMode_PartitionedAppend_PartitionValuesKeyedByPhysicalName_AndReadBack (#572):
    // a partitioned id-mode create + append keys add.partitionValues by PHYSICAL name; metaData.partitionColumns
    // stay LOGICAL; the data column carries the stamped field_id; read-back resolves the partition value.
    [Fact]
    public async Task IdMode_PartitionValues_KeyedByPhysicalName_AndReadBack()
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
            await target.CreateIdMappedTableAsync(
                schema, new[] { "region" }, new[] { Batch(schema, "us", 1L) }, new SeededPhysicalNameSource(Seed));
        }

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.AppendAsync(schema, new[] { "region" }, new[] { Batch(schema, "eu", 2L) });
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        string physicalRegion = ColumnMapping.PhysicalName(snapshot.Schema[0], ColumnMappingMode.Id);
        string physicalId = ColumnMapping.PhysicalName(snapshot.Schema[1], ColumnMappingMode.Id);

        // metaData.partitionColumns stay LOGICAL; every add (create + append) keys partitionValues PHYSICALLY.
        Assert.Equal(new[] { "region" }, snapshot.Metadata.PartitionColumns.ToArray());
        Assert.Equal(2, snapshot.ActiveFiles.Length);
        Assert.All(snapshot.ActiveFiles, add => Assert.True(add.PartitionValues.ContainsKey(physicalRegion)));
        Assert.DoesNotContain(snapshot.ActiveFiles, add => add.PartitionValues.ContainsKey("region"));

        // The in-file DATA column ('id') carries its stamped field_id (partition col rides on partitionValues).
        foreach (AddFileAction add in snapshot.ActiveFiles)
        {
            Dictionary<string, int> fieldIds = await ReadParquetFieldIdsAsync(add.Path);
            Assert.Equal(2, fieldIds[physicalId]);
        }

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

    // The id-mode sibling of NameMode_Append_PreservesColumnIdentityAndMaxColumnId (#572): an append REUSES
    // the table's existing per-field id/physicalName and leaves maxColumnId untouched (never re-mints).
    [Fact]
    public async Task IdMode_Append_PreservesColumnIdentityAndMaxColumnId()
    {
        Func<string> names = FileNames();

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateIdMappedTableAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
                new SeededPhysicalNameSource(Seed));
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot before = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        string maxIdBefore = before.Metadata.Configuration[ColumnMapping.MaxColumnIdKey];

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.AppendAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (2L, 200L, (string?)"bob") }) });
        }

        Snapshot after = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        // Identity preserved verbatim: same physical names, same ids, same maxColumnId.
        AssertMapping(after.Schema[0], "id", 1, PhysId);
        AssertMapping(after.Schema[1], "score", 2, PhysScore);
        AssertMapping(after.Schema[2], "name", 3, PhysName);
        Assert.Equal(maxIdBefore, after.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
    }

    // B2 (council deltaspec N2): id-mode SCHEMA-EVOLUTION regression. Only NAME-mode evolution was previously
    // pinned; a refactor that dropped the id during the merge would pass every name-mode test with no id-mode
    // guard. This asserts an APPEND with mergeSchema:true that adds a column (a) mints the new column id
    // maxColumnId+1, (b) leaves existing ids untouched, (c) bumps maxColumnId EXACTLY once, and — the id-mode
    // teeth — (d) STAMPS the new field_id in the APPENDED file's footer while the pre-evolution file stays
    // byte-untouched (still exactly field_ids 1/2/3, no new field_id). Sibling of the name-mode
    // NameMode_Append_AddColumn_ThroughPublicDoor_MintsAndReadsBack_Issue556.
    [Fact]
    public async Task IdMode_Append_AddColumn_EvolvesMapping_StampsNewFieldId()
    {
        Func<string> names = FileNames();
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateIdMappedTableAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
                new SeededPhysicalNameSource(Seed));
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot v0 = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        string v0Path = Assert.Single(v0.ActiveFiles).Path;

        // The append's committer mints from a SEPARATE seeded source, so "extra" mints EvolveSeed's first name.
        string mintedExtra = new SeededPhysicalNameSource(EvolveSeed).NextPhysicalName();
        using (DeltaWriteTarget append = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names, new SeededPhysicalNameSource(EvolveSeed)))
        {
            DeltaWriteResult result = await append.AppendAsync(
                EvolvedFlatSchema, Array.Empty<string>(),
                new[] { EvolvedFlatBatch((2L, 200L, "bob", "x2")) },
                mergeSchema: true);
            Assert.Equal(1L, result.Version);
        }

        Snapshot after = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        // Existing columns keep identity verbatim; "extra" mints id 4; maxColumnId → 4 (bumped exactly once).
        AssertMapping(after.Schema["id"], "id", 1, PhysId);
        AssertMapping(after.Schema["score"], "score", 2, PhysScore);
        AssertMapping(after.Schema["name"], "name", 3, PhysName);
        AssertMapping(after.Schema["extra"], "extra", 4, mintedExtra);
        Assert.Equal("4", after.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);

        // The APPENDED file stamps the new field_id (4 on the minted physical name) alongside the existing
        // 1/2/3; the pre-evolution v0 file is UNTOUCHED (exactly field_ids 1/2/3, no field_id 4).
        AddFileAction appended = Assert.Single(after.ActiveFiles, a => a.Path != v0Path);
        Dictionary<string, int> appendedIds = await ReadParquetFieldIdsAsync(appended.Path);
        Assert.Equal(1, appendedIds[PhysId]);
        Assert.Equal(2, appendedIds[PhysScore]);
        Assert.Equal(3, appendedIds[PhysName]);
        Assert.Equal(4, appendedIds[mintedExtra]);

        Dictionary<string, int> v0Ids = await ReadParquetFieldIdsAsync(v0Path);
        Assert.Equal(new[] { 1, 2, 3 }, new[] { v0Ids[PhysId], v0Ids[PhysScore], v0Ids[PhysName] });
        Assert.DoesNotContain(mintedExtra, v0Ids.Keys);
        Assert.DoesNotContain(4, v0Ids.Values);

        // Full read-back by field_id: original row (extra null-filled) + appended row round-trip correctly.
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        List<(long Id, long? Score, string? Name, string? Extra)> rows =
            await ReadEvolvedRowsAsync(source, info.Version);
        Assert.Equal(
            new (long, long?, string?, string?)[] { (1L, 100L, "alice", null), (2L, 200L, "bob", "x2") },
            rows.OrderBy(r => r.Id).ToList());
    }

    // B2 (council deltaspec N2): id-mode OVERWRITE-REPLACE regression. A wholesale overwriteSchema that DROPS
    // a column, REORDERS the survivors, AND ADDS one must re-key correctly: survivors keep their id +
    // physicalName (identity pinned by LOGICAL name, independent of the new order), the new column gets a
    // FRESH id (maxColumnId+1), the dropped column's id is RETIRED (never reused), maxColumnId is monotonic,
    // and the replacement file's footer field_ids match the survivors' + new column's ids (never the retired
    // one). Sibling of the name-mode Overwrite_NameMode_Reorder/DropThenAdd tests, with id-mode footer teeth.
    [Fact]
    public async Task IdMode_OverwriteReplaceSchema_DropReorderAdd_ReKeysCorrectly()
    {
        Func<string> names = FileNames();
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateIdMappedTableAsync(
                FlatSchema, Array.Empty<string>(),                 // {id(1), score(2), name(3)}, maxColumnId=3
                new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
                new SeededPhysicalNameSource(Seed));
        }

        // Replacement schema: DROP "name", REORDER survivors to [score, id], ADD "fresh".
        var replacement = new StructType(new[]
        {
            new StructField("score", DataTypes.LongType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("fresh", DataTypes.StringType, nullable: true),
        });
        MutableColumnVector scoreV = ColumnVectors.Create(DataTypes.LongType, 1);
        MutableColumnVector idV = ColumnVectors.Create(DataTypes.LongType, 1);
        MutableColumnVector freshV = ColumnVectors.Create(DataTypes.StringType, 1);
        scoreV.AppendValue(900L);
        idV.AppendValue(9L);
        freshV.AppendBytes(Encoding.UTF8.GetBytes("new"));
        var replacementBatch = new ManagedColumnBatch(replacement, new ColumnVector[] { scoreV, idV, freshV }, 1);

        string mintedFresh = new SeededPhysicalNameSource(EvolveSeed).NextPhysicalName();
        using (DeltaWriteTarget overwrite = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names, new SeededPhysicalNameSource(EvolveSeed)))
        {
            DeltaWriteResult result = await overwrite.OverwriteAsync(
                replacement, Array.Empty<string>(), new ColumnBatch[] { replacementBatch },
                DeltaPartitionOverwriteMode.Static, overwriteSchema: true);
            Assert.Equal(1L, result.Version);
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot after = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        // New field ORDER is [score, id, fresh]; survivors keep identity by logical name; "fresh" mints id 4
        // (NOT the retired 3); "name" is gone; maxColumnId → 4.
        Assert.Equal(new[] { "score", "id", "fresh" }, after.Schema.Select(f => f.Name).ToArray());
        AssertMapping(after.Schema["score"], "score", 2, PhysScore);
        AssertMapping(after.Schema["id"], "id", 1, PhysId);
        AssertMapping(after.Schema["fresh"], "fresh", 4, mintedFresh);
        Assert.DoesNotContain("name", after.Schema.Select(f => f.Name));
        Assert.NotEqual(PhysName, mintedFresh);            // retired physicalName not reused
        Assert.Equal("4", after.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);

        // The replacement file's footer stamps the survivors' + new column's field_ids; the retired id 3 is
        // never stamped.
        AddFileAction replaced = Assert.Single(after.ActiveFiles);
        Dictionary<string, int> ids = await ReadParquetFieldIdsAsync(replaced.Path);
        Assert.Equal(2, ids[PhysScore]);
        Assert.Equal(1, ids[PhysId]);
        Assert.Equal(4, ids[mintedFresh]);
        Assert.DoesNotContain(3, ids.Values);
    }

    // B2 (council deltaspec N2): id-mode DROP-then-RE-ADD-same-name. Dropping "name" (id 3) via overwriteSchema
    // RETIRES id 3; a LATER overwriteSchema that RE-ADDS a column ALSO named "name" must mint a NEW id (4),
    // NEVER the retired 3 (id reuse is a Delta corruption class). maxColumnId is monotonic. Sibling of the
    // name-mode Overwrite_NameMode_DropThenAdd_DoesNotReuseRetiredId_Issue542, re-adding the SAME name.
    [Fact]
    public async Task IdMode_OverwriteReplaceSchema_DropThenReAddSameName_MintsNewId()
    {
        Func<string> names = FileNames();
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateIdMappedTableAsync(
                FlatSchema, Array.Empty<string>(),                 // {id(1), score(2), name(3)}, maxColumnId=3
                new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
                new SeededPhysicalNameSource(Seed));
        }

        var idScore = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("score", DataTypes.LongType, nullable: true),
        });

        using var backend = new LocalFileSystemBackend(_root);

        // Overwrite 1: DROP "name" → {id, score}; id 3 retired, maxColumnId stays 3.
        MutableColumnVector idV = ColumnVectors.Create(DataTypes.LongType, 1);
        MutableColumnVector scoreV = ColumnVectors.Create(DataTypes.LongType, 1);
        idV.AppendValue(1L);
        scoreV.AppendValue(100L);
        var dropBatch = new ManagedColumnBatch(idScore, new ColumnVector[] { idV, scoreV }, 1);
        using (DeltaWriteTarget drop = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names, new SeededPhysicalNameSource(EvolveSeed)))
        {
            await drop.OverwriteAsync(
                idScore, Array.Empty<string>(), new ColumnBatch[] { dropBatch },
                DeltaPartitionOverwriteMode.Static, overwriteSchema: true);
        }

        Snapshot afterDrop = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        Assert.Equal(new[] { "id", "score" }, afterDrop.Schema.Select(f => f.Name).ToArray());
        Assert.Equal("3", afterDrop.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]); // id 3 retired, not freed

        // Overwrite 2: RE-ADD "name" → {id, score, name}; "name" mints id 4 (NOT the retired 3).
        string mintedName = new SeededPhysicalNameSource(EvolveSeed2).NextPhysicalName();
        using (DeltaWriteTarget readd = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names, new SeededPhysicalNameSource(EvolveSeed2)))
        {
            await readd.OverwriteAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (7L, 700L, (string?)"zed") }) },
                DeltaPartitionOverwriteMode.Static, overwriteSchema: true);
        }

        Snapshot after = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        AssertMapping(after.Schema["id"], "id", 1, PhysId);
        AssertMapping(after.Schema["score"], "score", 2, PhysScore);
        AssertMapping(after.Schema["name"], "name", 4, mintedName);   // NEW id 4, NOT the retired 3
        Assert.NotEqual(PhysName, mintedName);                        // and NOT the retired physicalName
        Assert.Equal("4", after.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);

        // The re-added "name" stamps field_id 4 in the footer (never the retired 3).
        AddFileAction replaced = Assert.Single(after.ActiveFiles);
        Dictionary<string, int> ids = await ReadParquetFieldIdsAsync(replaced.Path);
        Assert.Equal(4, ids[mintedName]);
        Assert.DoesNotContain(3, ids.Values);
    }

    // Fail-closed parity preserved for an OUT-OF-SCOPE id-mode shape (#572): a nested (struct) top-level
    // column under id mapping is rejected fail-closed at create rather than mis-stamped — exactly as name
    // mode rejects it. Only FLAT top-level id-mode columns are in scope for this build.
    [Fact]
    public async Task IdMode_Create_NestedTopLevelColumn_IsRejectedFailClosed()
    {
        var nested = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("payload", new StructType(new[]
            {
                new StructField("inner", DataTypes.LongType, nullable: true),
            }), nullable: true),
        });

        // AssignFreshMapping rejects a nested top-level column via EnsureLeaf BEFORE any staging, so no data
        // batch is needed (and none could be built — the columnar batch would reject the struct column too).
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames());
        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(() => target.CreateIdMappedTableAsync(
            nested, Array.Empty<string>(), Array.Empty<ColumnBatch>(), new SeededPhysicalNameSource(Seed)));
        Assert.Contains("nested", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The table was NOT created (fail-closed leaves no _delta_log).
        using var backend = new LocalFileSystemBackend(_root);
        Assert.Null(await new DeltaLog(backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    // RT-9 (council nice-to-have): the fresh-create door is create-ONLY. Enabling id-mode column mapping on an
    // ALREADY-EXISTING table is out of scope in this build (a mode transition is refused — see the
    // committer-level guard too); the door pins it at TableExistsAsync → InvalidOperationException. This is the
    // write-door twin of the committer-level RejectsCommit_ModeTransitionOnExistingTable assertion.
    [Fact]
    public async Task IdMode_CreateOnExistingTable_IsRejectedFailClosed()
    {
        Func<string> names = FileNames();
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names);
        await target.CreateIdMappedTableAsync(
            FlatSchema, Array.Empty<string>(),
            new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
            new SeededPhysicalNameSource(Seed));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            target.CreateIdMappedTableAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (2L, 200L, (string?)"bob") }) },
                new SeededPhysicalNameSource(Seed)));
        Assert.Contains("existing table", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Still exactly the v0 create (the rejected second create left no v1).
        using var backend = new LocalFileSystemBackend(_root);
        Assert.Equal(0L, await new DeltaLog(backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    // RT-9: the DELETE-seam id-mode + deletionVectors fresh-create door is likewise create-only.
    [Fact]
    public async Task IdMode_CreateDeletionVectorTableOnExistingTable_IsRejectedFailClosed()
    {
        Func<string> names = FileNames();
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names);
        await target.CreateIdMappedTableAsync(
            FlatSchema, Array.Empty<string>(),
            new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
            new SeededPhysicalNameSource(Seed));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            target.CreateIdMappedDeletionVectorTableAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (2L, 200L, (string?)"bob") }) },
                new SeededPhysicalNameSource(Seed)));
        Assert.Contains("existing table", ex.Message, StringComparison.OrdinalIgnoreCase);

        using var backend = new LocalFileSystemBackend(_root);
        Assert.Equal(0L, await new DeltaLog(backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    // RT-9 (name-mode twin): the name-mode fresh-create door has the same existing-table guard.
    [Fact]
    public async Task NameMode_CreateOnExistingTable_IsRejectedFailClosed()
    {
        Func<string> names = FileNames();
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names);
        await target.CreateNameMappedTableAsync(
            FlatSchema, Array.Empty<string>(),
            new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
            new SeededPhysicalNameSource(Seed));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            target.CreateNameMappedTableAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (2L, 200L, (string?)"bob") }) },
                new SeededPhysicalNameSource(Seed)));
        Assert.Contains("existing table", ex.Message, StringComparison.OrdinalIgnoreCase);

        using var backend = new LocalFileSystemBackend(_root);
        Assert.Equal(0L, await new DeltaLog(backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public void CreateField_StampsParquetFieldId_FromColumnMappingId()
    {
        // Writer half of #523: a StructField carrying delta.columnMapping.id yields a Parquet DataField whose
        // FieldId is stamped (persisted to the Thrift footer). A field WITHOUT the id metadata is left at the
        // Parquet default (-1) — so name/none-mode writes (whose physical schema drops the id) stay
        // byte-unchanged.
        global::Parquet.Schema.DataField stamped =
            ParquetTypeMapping.CreateField(PhysFieldWithId("x", DataTypes.LongType, nullable: false, id: 7));
        Assert.Equal(7, stamped.FieldId);

        global::Parquet.Schema.DataField plain =
            ParquetTypeMapping.CreateField(new StructField("x", DataTypes.LongType, nullable: false));
        Assert.Equal(-1, plain.FieldId);
    }

    // N1 (council deltaspec): the field_id guard is tightened from `< 0` to `<= 0`. Delta column-mapping ids
    // start at 1 (AssignFreshMapping mints 1, 2, …), so the writer NEVER emits 0 — a non-positive id reaching
    // CreateField is a corruption/mis-stamp and is rejected fail-closed rather than persisted as a field_id
    // the spec never assigns. (Ids > int.MaxValue are already rejected as out of the Parquet field_id range.)
    [Fact]
    public void CreateField_RejectsNonPositiveFieldId_FailClosed_Since572()
    {
        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(PhysFieldWithId("x", DataTypes.LongType, nullable: false, id: 0)));
        Assert.Contains("field_id range [1, int.MaxValue]", ex.Message, StringComparison.Ordinal);
    }

    // A physical StructField carrying a delta.columnMapping.id (as a long MetadataValue) — the shape the
    // id-mode writer stamps into the Parquet field_id.
    private static StructField PhysFieldWithId(string name, DataType type, bool nullable, long id) =>
        new(name, type, nullable, FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(id)),
        }));

    // Writes a raw id-mode table (version-0 _delta_log + a physical Parquet data file) with the given logical
    // schemaString, maxColumnId, partition columns, and add.partitionValues — the shape a foreign engine
    // produces (DeltaSharp cannot CREATE id-mode tables). Returns the Parquet byte length.
    private async Task SeedIdModeTableAsync(
        string schemaJson,
        long maxColumnId,
        StructType physicalSchema,
        ManagedColumnBatch dataBatch,
        string[]? partitionColumns = null,
        (string PhysicalName, string Value)[]? partitionValues = null,
        string relativePath = "idmode.parquet")
    {
        byte[] parquetBytes;
        using (var buffer = new MemoryStream())
        {
            await new ParquetFileWriter().WriteAsync(
                buffer, physicalSchema, new[] { dataBatch }, CancellationToken.None);
            parquetBytes = buffer.ToArray();
        }

        using var backend = new LocalFileSystemBackend(_root);
        await backend.PutIfAbsentAsync(relativePath, parquetBytes, CancellationToken.None);
        string pv = partitionValues is null
            ? "{}"
            : "{" + string.Join(",", partitionValues.Select(p => $"\"{p.PhysicalName}\":\"{p.Value}\"")) + "}";
        string addLine =
            $"{{\"add\":{{\"path\":\"{relativePath}\",\"partitionValues\":{pv},"
            + $"\"size\":{parquetBytes.Length},\"modificationTime\":0,\"dataChange\":true}}}}";
        byte[] commit = Encoding.UTF8.GetBytes(
            ProtocolFeatureLine() + "\n"
            + NameModeMetadataLine(
                schemaJson,
                partitionColumns ?? Array.Empty<string>(),
                ("delta.columnMapping.mode", "id"),
                ("delta.columnMapping.maxColumnId", maxColumnId.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "\n"
            + addLine + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", commit, CancellationToken.None);
    }

    private static string IdField(string name, string type, bool nullable, long id, string physicalName) =>
        $"{{\"name\":\"{name}\",\"type\":\"{type}\",\"nullable\":{(nullable ? "true" : "false")},\"metadata\":"
        + $"{{\"delta.columnMapping.id\":{id},\"delta.columnMapping.physicalName\":\"{physicalName}\"}}}}";

    // ---- #523 round-2: fault-branch + partition fail-closed coverage (council R1) ----

    [Fact]
    public async Task IdMode_PartitionValues_ResolvedByPhysicalName_NotNull()
    {
        // CRITICAL fix (Architect/Delta-Specialist): id-mode PARTITION values are keyed by PHYSICAL name in
        // the log (PROTOCOL.md:1021); resolving them by the LOGICAL name returned all-null partition columns.
        // A partitioned id-mode table's partition column must read its real value.
        string schemaJson = "{\"type\":\"struct\",\"fields\":["
            + IdField("region", "string", true, 1, PhysId) + ","
            + IdField("id", "long", false, 2, PhysScore) + "]}";
        // Physical Parquet holds only the DATA column (id), stamped field_id=2; partition column is NOT in
        // the file (its value rides on add.partitionValues, keyed by the PHYSICAL name PhysId).
        var physicalSchema = new StructType(new[] { PhysFieldWithId("d", DataTypes.LongType, nullable: false, id: 2) });
        MutableColumnVector d = ColumnVectors.Create(DataTypes.LongType, 1);
        d.AppendValue(42L);
        var batch = new ManagedColumnBatch(physicalSchema, new ColumnVector[] { d }, 1);
        await SeedIdModeTableAsync(
            schemaJson, maxColumnId: 2, physicalSchema, batch,
            partitionColumns: new[] { "region" },
            partitionValues: new[] { (PhysId, "us") });

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        int regionIdx = info.Schema.IndexOf("region");
        int idIdx = info.Schema.IndexOf("id");

        var rows = new List<(string? Region, long Id)>();
        foreach (ColumnBatch b in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector rc = b.SelectedColumn(regionIdx);
            ColumnVector ic = b.SelectedColumn(idIdx);
            for (int r = 0; r < b.LogicalRowCount; r++)
            {
                rows.Add((rc.IsNull(r) ? null : Encoding.UTF8.GetString(rc.GetBytes(r)), ic.GetValue<long>(r)));
            }
        }

        // region must be "us" (from add.partitionValues[PhysId]), NOT null.
        Assert.Equal(new (string?, long)[] { ("us", 42L) }, rows.ToArray());
    }

    [Fact]
    public async Task IdMode_EmptyStaticOverwrite_TruncatesTable_LikeNameMode()
    {
        // #572: id-mode WRITE is enabled, so an empty static overwrite (TRUNCATE) of an id-mode table now
        // behaves EXACTLY as name/none mode — it removes every active file and adds none, committing a new
        // version with zero active files (no longer refused fail-closed). The overwrite resolves the physical
        // staging by mode=id (nothing to stage here) and commits the remove-all.
        string schemaJson = "{\"type\":\"struct\",\"fields\":[" + IdField("id", "long", false, 1, PhysId) + "]}";
        var physicalSchema = new StructType(new[] { PhysFieldWithId("d", DataTypes.LongType, nullable: false, id: 1) });
        MutableColumnVector d = ColumnVectors.Create(DataTypes.LongType, 1);
        d.AppendValue(7L);
        var batch = new ManagedColumnBatch(physicalSchema, new ColumnVector[] { d }, 1);
        await SeedIdModeTableAsync(schemaJson, maxColumnId: 1, physicalSchema, batch);

        var logical = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames()))
        {
            DeltaWriteResult result = await target.OverwriteAsync(
                logical, Array.Empty<string>(), Array.Empty<ColumnBatch>(),
                DeltaPartitionOverwriteMode.Static, overwriteSchema: false, cancellationToken: CancellationToken.None);
            Assert.Equal(1L, result.Version);
        }

        // The id-mode table is TRUNCATED — a new commit (v1) with zero active files; config stays id mode.
        using var backend = new LocalFileSystemBackend(_root);
        Snapshot after = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        Assert.Equal(1L, after.Version);
        Assert.Empty(after.ActiveFiles);
        Assert.Equal("id", after.Metadata.Configuration[ColumnMapping.ModeKey]);
    }

    [Fact]
    public async Task IdMode_DuplicateColumnMappingId_IsRejectedFailClosed()
    {
        // CRITICAL fix (Quality/Architect): id-mode schema validation now runs at load — two fields sharing
        // delta.columnMapping.id would resolve both logical columns to one file column (silent mis-read).
        await WriteRawTableAsync(
            protocol: ProtocolFeatureLine(),
            metadata: NameModeMetadataLine(
                "{\"type\":\"struct\",\"fields\":["
                + IdField("id", "long", false, 1, PhysId) + ","
                + IdField("score", "long", true, 1, PhysScore) + "]}",  // duplicate id=1
                partitionColumns: Array.Empty<string>(),
                ("delta.columnMapping.mode", "id"),
                ("delta.columnMapping.maxColumnId", "2")));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(() => source.LoadSnapshotAsync(null, null));
        Assert.Contains("id 1 is assigned to more than one column", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdMode_MissingColumnMappingId_IsRejectedFailClosed()
    {
        await WriteRawTableAsync(
            protocol: ProtocolFeatureLine(),
            metadata: NameModeMetadataLine(
                "{\"type\":\"struct\",\"fields\":["
                + IdField("id", "long", false, 1, PhysId) + ","
                + "{\"name\":\"score\",\"type\":\"long\",\"nullable\":true,\"metadata\":{\"delta.columnMapping.physicalName\":\"" + PhysScore + "\"}}]}",  // no id
                partitionColumns: Array.Empty<string>(),
                ("delta.columnMapping.mode", "id"),
                ("delta.columnMapping.maxColumnId", "1")));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(() => source.LoadSnapshotAsync(null, null));
        Assert.Contains("has no 'delta.columnMapping.id'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdMode_IdAboveMaxColumnId_IsRejectedFailClosed()
    {
        await WriteRawTableAsync(
            protocol: ProtocolFeatureLine(),
            metadata: NameModeMetadataLine(
                "{\"type\":\"struct\",\"fields\":[" + IdField("id", "long", false, 9, PhysId) + "]}",  // id=9 > max
                partitionColumns: Array.Empty<string>(),
                ("delta.columnMapping.mode", "id"),
                ("delta.columnMapping.maxColumnId", "1")));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(() => source.LoadSnapshotAsync(null, null));
        Assert.Contains("exceeds the tracked", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdMode_NestedColumn_IsRejectedFailClosed()
    {
        // MINOR fix (Delta-Specialist): the nested-top-level-column guard now covers id mode too (BuildFieldIdMap
        // is flat-only; a nested id-mode column must fail closed at the projection choke point, not null-fill).
        await WriteRawTableAsync(
            protocol: ProtocolFeatureLine(),
            metadata: NameModeMetadataLine(
                "{\"type\":\"struct\",\"fields\":["
                + "{\"name\":\"nested\",\"type\":{\"type\":\"array\",\"elementType\":\"long\",\"containsNull\":true},"
                + "\"nullable\":true,\"metadata\":{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"" + PhysId + "\"}}]}",
                partitionColumns: Array.Empty<string>(),
                ("delta.columnMapping.mode", "id"),
                ("delta.columnMapping.maxColumnId", "1")));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(() => source.ReadBatchesAsync(0L));
        Assert.Contains("nested", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IdMode_DuplicateFileFieldId_IsRejectedFailClosed()
    {
        // MEDIUM fix (Reliability): a foreign Parquet footer with two columns sharing a field_id must fail
        // closed (BuildFieldIdMap), never silently taking the last writer (which would mis-resolve a column).
        string schemaJson = "{\"type\":\"struct\",\"fields\":["
            + IdField("id", "long", false, 1, PhysId) + "," + IdField("score", "long", true, 2, PhysScore) + "]}";
        // Physical file: BOTH columns stamped field_id=1 (a poisoned/foreign footer).
        var physicalSchema = new StructType(new[]
        {
            PhysFieldWithId("a", DataTypes.LongType, nullable: false, id: 1),
            PhysFieldWithId("b", DataTypes.LongType, nullable: true, id: 1),
        });
        MutableColumnVector a = ColumnVectors.Create(DataTypes.LongType, 1);
        MutableColumnVector b = ColumnVectors.Create(DataTypes.LongType, 1);
        a.AppendValue(1L);
        b.AppendValue(2L);
        var batch = new ManagedColumnBatch(physicalSchema, new ColumnVector[] { a, b }, 1);
        await SeedIdModeTableAsync(schemaJson, maxColumnId: 2, physicalSchema, batch);

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(() => source.ReadBatchesAsync(info.Version));
        Assert.Contains("duplicate field_id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdMode_RequestedIdAbsentFromFile_NullFillsNullable()
    {
        // MEDIUM fix (Reliability): a nullable logical column whose id is not in the file (schema evolution:
        // metadata carries a column the older file predates) null-fills — keyed on the ID, not the name.
        string schemaJson = "{\"type\":\"struct\",\"fields\":["
            + IdField("id", "long", false, 1, PhysId) + "," + IdField("added", "long", true, 2, PhysScore) + "]}";
        // File has ONLY field_id=1 ("added" / id=2 is absent).
        var physicalSchema = new StructType(new[] { PhysFieldWithId("d", DataTypes.LongType, nullable: false, id: 1) });
        MutableColumnVector d = ColumnVectors.Create(DataTypes.LongType, 1);
        d.AppendValue(5L);
        var batch = new ManagedColumnBatch(physicalSchema, new ColumnVector[] { d }, 1);
        await SeedIdModeTableAsync(schemaJson, maxColumnId: 2, physicalSchema, batch);

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        int addedIdx = info.Schema.IndexOf("added");
        var results = new List<(long Id, bool AddedNull)>();
        foreach (ColumnBatch b in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector idc = b.SelectedColumn(info.Schema.IndexOf("id"));
            ColumnVector addedc = b.SelectedColumn(addedIdx);
            for (int r = 0; r < b.LogicalRowCount; r++)
            {
                results.Add((idc.GetValue<long>(r), addedc.IsNull(r)));
            }
        }

        Assert.Equal(new[] { (5L, true) }, results.ToArray()); // "added" null-filled
    }

    [Fact]
    public async Task IdMode_RequestedIdAbsentFromFile_NonNullable_Throws()
    {
        // MEDIUM fix (Reliability): a NON-nullable logical column whose id is absent from the file fails closed
        // (ColumnNotPresent), never a silent null-fill.
        string schemaJson = "{\"type\":\"struct\",\"fields\":["
            + IdField("id", "long", false, 1, PhysId) + "," + IdField("required", "long", false, 2, PhysScore) + "]}";
        var physicalSchema = new StructType(new[] { PhysFieldWithId("d", DataTypes.LongType, nullable: false, id: 1) });
        MutableColumnVector d = ColumnVectors.Create(DataTypes.LongType, 1);
        d.AppendValue(5L);
        var batch = new ManagedColumnBatch(physicalSchema, new ColumnVector[] { d }, 1);
        await SeedIdModeTableAsync(schemaJson, maxColumnId: 2, physicalSchema, batch);

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        await Assert.ThrowsAnyAsync<Exception>(() => source.ReadBatchesAsync(info.Version));
    }

    [Fact]
    public async Task IdMode_RequestedIdAboveInt32Max_IsRejectedFailClosed()
    {
        // LOW fix (Reliability F4): a requested delta.columnMapping.id > int.MaxValue is outside the Parquet
        // footer field_id (int32) domain. It is rejected FAIL-CLOSED at ParquetTypeMapping.CreateField (the
        // FIRST-reached guard of the long->int32 cast) — never silently truncated to int32. Here id = 2^32 + 1
        // would unchecked-cast to (int)1, numerically the file's real field_id = 1: only if BOTH this cast
        // guard AND the redundant int-range bound in ParquetFileReader.ResolveFileFields were removed would
        // "evil" misresolve onto column "id"'s data (with just the reader bound, an oversized id null-fills).
        // CreateField fires first and rejects it loudly instead — the conservative fail-closed contract.
        const long overflowId = 4294967297L; // 2^32 + 1  =>  unchecked (int)overflowId == 1
        string schemaJson = "{\"type\":\"struct\",\"fields\":["
            + IdField("id", "long", false, 1, PhysId) + "," + IdField("evil", "long", true, overflowId, PhysScore) + "]}";
        var physicalSchema = new StructType(new[] { PhysFieldWithId("d", DataTypes.LongType, nullable: false, id: 1) });
        MutableColumnVector d = ColumnVectors.Create(DataTypes.LongType, 1);
        d.AppendValue(5L);
        var batch = new ManagedColumnBatch(physicalSchema, new ColumnVector[] { d }, 1);
        await SeedIdModeTableAsync(schemaJson, maxColumnId: overflowId, physicalSchema, batch);

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(
            () => source.ReadBatchesAsync(info.Version));
        Assert.Contains("outside the Parquet field_id range", ex.Message, StringComparison.Ordinal);
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
    // drop / add-with-mint / retype — is exercised via the DeltaTableWriter mechanism tests below and, for
    // add-through-the-door, by OverwriteSchema_OnNameModeTable_AddColumn_ThroughPublicDoor_MintsAndReadsBack_Issue556.)
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

    [Fact]
    public async Task OverwriteSchema_OnNameModeTable_AddColumn_ThroughPublicDoor_MintsAndReadsBack_Issue556()
    {
        // #556: an overwriteSchema that ADDS a brand-new column now succeeds THROUGH THE PUBLIC DOOR. The door
        // reconciles the columnMapping (minting the new column's physicalName+id ONCE), stages the Parquet
        // file under that minted physical name, and commits the SAME mapping — so the staged bytes and the
        // committed metaData agree (no independent door-vs-committer mint). Previously this failed closed (the
        // door staged against the existing mapping and could not stage a brand-new physical column).
        Func<string> names = FileNames();
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateNameMappedTableAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
                new SeededPhysicalNameSource(Seed));
        }

        // The door's committer mints from a SEPARATE seeded source, so "extra" mints EvolveSeed's first name.
        string mintedExtra = new SeededPhysicalNameSource(EvolveSeed).NextPhysicalName();
        using (DeltaWriteTarget overwrite = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names, new SeededPhysicalNameSource(EvolveSeed)))
        {
            DeltaWriteResult result = await overwrite.OverwriteAsync(
                EvolvedFlatSchema, Array.Empty<string>(),
                new[] { EvolvedFlatBatch((2L, 200L, "bob", "x2")) },
                DeltaPartitionOverwriteMode.Static,
                overwriteSchema: true);
            Assert.Equal(1L, result.Version);
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        // Existing columns keep identity; "extra" mints id 4 + the golden EvolveSeed physical name; maxColumnId → 4.
        AssertMapping(snapshot.Schema["id"], "id", 1, PhysId);
        AssertMapping(snapshot.Schema["score"], "score", 2, PhysScore);
        AssertMapping(snapshot.Schema["name"], "name", 3, PhysName);
        AssertMapping(snapshot.Schema["extra"], "extra", 4, mintedExtra);
        Assert.Equal("4", snapshot.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);

        // The teeth (anti-double-mint): the DOOR-staged file physically carries the SAME minted col-<uuid> the
        // COMMIT recorded for "extra" — a second, independent mint would land a different physical name here.
        AddFileAction added = Assert.Single(snapshot.ActiveFiles);
        Assert.Equal(
            new[] { PhysId, PhysScore, PhysName, mintedExtra }, await ReadParquetColumnNamesAsync(added.Path));

        // Prior data is replaced; the new row (incl. the added column's value) round-trips through the read
        // door — had the door staged "extra" under a different physical name than the metaData records, it
        // would read back NULL, not "x2".
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        List<(long Id, long? Score, string? Name, string? Extra)> rows =
            await ReadEvolvedRowsAsync(source, info.Version);
        Assert.Equal(new (long, long?, string?, string?)[] { (2L, 200L, "bob", "x2") }, rows);
    }

    [Fact]
    public async Task NameMode_Append_AddColumn_ThroughPublicDoor_MintsAndReadsBack_Issue556()
    {
        // #556: an APPEND with mergeSchema:true that adds a nullable column now succeeds THROUGH THE PUBLIC
        // DOOR (symmetric with the overwriteSchema-add case). The door mints the new column's physicalName+id
        // ONCE, stages the appended Parquet under it, and commits the evolved mapping atomically with the add —
        // the added file and the committed metaData agree on the physical identity.
        Func<string> names = FileNames();
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateNameMappedTableAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
                new SeededPhysicalNameSource(Seed));
        }

        string mintedExtra = new SeededPhysicalNameSource(EvolveSeed).NextPhysicalName();
        using (DeltaWriteTarget append = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names, new SeededPhysicalNameSource(EvolveSeed)))
        {
            DeltaWriteResult result = await append.AppendAsync(
                EvolvedFlatSchema, Array.Empty<string>(),
                new[] { EvolvedFlatBatch((2L, 200L, "bob", "x2")) },
                mergeSchema: true);
            Assert.Equal(1L, result.Version);
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        // Existing columns keep identity; "extra" mints id 4; maxColumnId → 4.
        AssertMapping(snapshot.Schema["id"], "id", 1, PhysId);
        AssertMapping(snapshot.Schema["score"], "score", 2, PhysScore);
        AssertMapping(snapshot.Schema["name"], "name", 3, PhysName);
        AssertMapping(snapshot.Schema["extra"], "extra", 4, mintedExtra);
        Assert.Equal("4", snapshot.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);

        // Both files remain; the v0 file carries the original 3 physical names, the APPENDED file carries all
        // 4 (incl. the minted "extra") — proving the door staged the new column under the committed physical
        // name (a double-mint would land a different name in the appended file).
        Assert.Equal(2, snapshot.ActiveFiles.Length);
        var fileColumns = new List<string[]>();
        foreach (AddFileAction add in snapshot.ActiveFiles)
        {
            fileColumns.Add(await ReadParquetColumnNamesAsync(add.Path));
        }

        Assert.Contains(fileColumns, c => c.SequenceEqual(new[] { PhysId, PhysScore, PhysName }));
        Assert.Contains(fileColumns, c => c.SequenceEqual(new[] { PhysId, PhysScore, PhysName, mintedExtra }));

        // Union of the original row (extra null-filled — its physical column is absent from the v0 file) and
        // the appended row (extra = "x2", round-tripped through the read door).
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        List<(long Id, long? Score, string? Name, string? Extra)> rows =
            await ReadEvolvedRowsAsync(source, info.Version);
        Assert.Equal(
            new (long, long?, string?, string?)[] { (1L, 100L, "alice", null), (2L, 200L, "bob", "x2") },
            rows.OrderBy(r => r.Id).ToList());
    }

    [Fact]
    public async Task NameMode_Append_Empty_MergeSchema_IsNoOp_VersionUnchanged_Issue556()
    {
        // #556 (Architect/Reliability R1): an EMPTY append with mergeSchema:true that declares a new column is
        // a benign no-op — it neither commits a new version nor mints/persists the would-be column (an empty
        // write carries no rows to define one). The door short-circuits BEFORE planning, so nothing is
        // enforced or minted.
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames());
        await target.CreateNameMappedTableAsync(
            FlatSchema, Array.Empty<string>(),
            new[] { FlatBatch(new[] { (1L, 100L, (string?)"alice") }) },
            new SeededPhysicalNameSource(Seed));

        DeltaWriteResult result = await target.AppendAsync(
            EvolvedFlatSchema, Array.Empty<string>(), Array.Empty<ColumnBatch>(), mergeSchema: true);

        Assert.Equal(0L, result.Version);
        Assert.Equal(0, result.FilesWritten);
        Assert.Equal(0L, result.RowsWritten);

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        Assert.Equal(0L, snapshot.Version);                 // no new commit
        Assert.Equal(3, snapshot.Schema.Count);             // "extra" NOT added
        Assert.Equal("3", snapshot.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]); // maxColumnId unchanged
    }

    [Fact]
    public async Task OverwriteSchema_OnPartitionedNameModeTable_EmptySameSchema_IsNoOp_Issue556()
    {
        // #556 (Architect/DeltaStorage R1): the overwriteSchema empty no-op guard must compare LOGICAL
        // partition columns on BOTH sides. For a PARTITIONED name-mode table an empty overwriteSchema (0 files)
        // with the unchanged schema must short-circuit to Skipped (version unchanged); a physical-vs-logical
        // partition comparison would never match (physical col-<uuid> != logical "region") and spuriously
        // commit a redundant metaData-only version.
        const string PhysRegion = "col-11111111-1111-1111-1111-111111111111";
        const string PhysValue = "col-22222222-2222-2222-2222-222222222222";
        string schemaJson =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"region\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + $"{{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"{PhysRegion}\"}}}},"
            + "{\"name\":\"value\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
            + $"{{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"{PhysValue}\"}}}}]}}";

        using var backend = new LocalFileSystemBackend(_root);
        byte[] commit = Encoding.UTF8.GetBytes(
            ProtocolFeatureLine() + "\n"
            + NameModeMetadataLine(
                schemaJson, new[] { "region" },
                ("delta.columnMapping.mode", "name"),
                ("delta.columnMapping.maxColumnId", "2")) + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", commit, CancellationToken.None);

        var partitionedSchema = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: true),
            new StructField("value", DataTypes.LongType, nullable: true),
        });

        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames());
        DeltaWriteResult result = await target.OverwriteAsync(
            partitionedSchema, new[] { "region" }, Array.Empty<ColumnBatch>(),
            DeltaPartitionOverwriteMode.Static, overwriteSchema: true);

        // Skipped no-op: version stays at 0 — no redundant v1 metaData-only commit.
        Assert.Equal(0L, result.Version);
        Assert.Equal(0L, (await new DeltaLog(backend).LoadSnapshotAsync(version: null)).Version);
    }

    [Fact]
    public async Task NameMode_PartitionedAppend_AddColumn_ThroughPublicDoor_MintsAndReadsBack_Issue556()
    {
        // #556 (Quality R1): a PARTITIONED name-mode add-column append through the door must align the physical
        // partition column with the staging schema. The appended file keys partitionValues by the PHYSICAL
        // region name and carries the minted physical data column; read-back resolves by logical name and the
        // pre-evolution partition's rows null-fill the added column.
        var schema = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: true),   // partition
            new StructField("id", DataTypes.LongType, nullable: false),
        });
        var evolved = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("extra", DataTypes.StringType, nullable: true),
        });
        Func<string> names = FileNames();
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, 1);
            MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 1);
            region.AppendBytes(Encoding.UTF8.GetBytes("us"));
            id.AppendValue(1L);
            await target.CreateNameMappedTableAsync(
                schema, new[] { "region" },
                new[] { new ManagedColumnBatch(schema, new ColumnVector[] { region, id }, 1) },
                new SeededPhysicalNameSource(Seed));
        }

        string mintedExtra = new SeededPhysicalNameSource(EvolveSeed).NextPhysicalName();
        using (DeltaWriteTarget append = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names, new SeededPhysicalNameSource(EvolveSeed)))
        {
            MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, 1);
            MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 1);
            MutableColumnVector extra = ColumnVectors.Create(DataTypes.StringType, 1);
            region.AppendBytes(Encoding.UTF8.GetBytes("eu"));
            id.AppendValue(2L);
            extra.AppendBytes(Encoding.UTF8.GetBytes("x2"));
            DeltaWriteResult result = await append.AppendAsync(
                evolved, new[] { "region" },
                new[] { new ManagedColumnBatch(evolved, new ColumnVector[] { region, id, extra }, 1) },
                mergeSchema: true);
            Assert.Equal(1L, result.Version);
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        // region=id 1, id=id 2, extra=minted id 3; maxColumnId → 3.
        AssertMapping(snapshot.Schema["extra"], "extra", 3, mintedExtra);
        Assert.Equal("3", snapshot.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
        string physicalRegion = ColumnMapping.PhysicalName(snapshot.Schema[0], ColumnMappingMode.Name);
        Assert.All(snapshot.ActiveFiles, add => Assert.True(add.PartitionValues.ContainsKey(physicalRegion)));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(new[] { "region", "id", "extra" }, info.Schema.Select(f => f.Name).ToArray());
        var rows = new List<(string?, long, string?)>();
        foreach (ColumnBatch b in await source.ReadBatchesAsync(info.Version))
        {
            for (int r = 0; r < b.LogicalRowCount; r++)
            {
                ColumnVector rc = b.SelectedColumn(0);
                ColumnVector ic = b.SelectedColumn(1);
                ColumnVector ec = b.SelectedColumn(2);
                rows.Add((
                    rc.IsNull(r) ? null : Encoding.UTF8.GetString(rc.GetBytes(r)),
                    ic.GetValue<long>(r),
                    ec.IsNull(r) ? null : Encoding.UTF8.GetString(ec.GetBytes(r))));
            }
        }

        Assert.Equal(
            new (string?, long, string?)[] { ("us", 1L, null), ("eu", 2L, "x2") },
            rows.OrderBy(r => r.Item2).ToList());
    }

    [Fact]
    public async Task NameMode_Append_WidenColumn_ThroughPublicDoor_Issue556()
    {
        // #556 (Quality R1): an applied type widening (int → long) via mergeSchema through the PUBLIC door
        // keeps the column's identity (no re-mint), records delta.typeChanges, and stages the widened bytes
        // under the retained physical name — proven by read-back through the name-mode read door.
        const string ValuePhysical = "col-33333333-3333-3333-3333-333333333333";
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
                schemaJson, Array.Empty<string>(),
                ("delta.columnMapping.mode", "name"),
                ("delta.columnMapping.maxColumnId", "1"),
                ("delta.enableTypeWidening", "true")) + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", commit, CancellationToken.None);

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames()))
        {
            MutableColumnVector value = ColumnVectors.Create(DataTypes.LongType, 2);
            value.AppendValue(100L);
            value.AppendValue(200L);
            var longSchema = new StructType(new[] { new StructField("value", DataTypes.LongType, nullable: true) });
            DeltaWriteResult result = await target.AppendAsync(
                longSchema, Array.Empty<string>(),
                new[] { new ManagedColumnBatch(longSchema, new ColumnVector[] { value }, 2) },
                mergeSchema: true);
            Assert.Equal(1L, result.Version);
        }

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        StructField valueField = snapshot.Schema["value"];
        Assert.Equal(DataTypes.LongType, valueField.DataType);                     // widened
        AssertMapping(valueField, "value", 1, ValuePhysical);                      // identity preserved (no re-mint)
        Assert.True(valueField.Metadata.TryGetValue("delta.typeChanges", out _));  // widening recorded
        Assert.Equal("1", snapshot.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]); // no new column

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
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
        // LOAD runs ValidateColumnMappingSchema, so a successful load proves the evolved schema is a CONSISTENT
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

        // Reload (ValidateColumnMappingSchema runs on load): existing columns keep identity; the new column carries
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

    // A single-row LOGICAL batch for EvolvedFlatSchema (id, score, name, extra).
    private static ColumnBatch EvolvedFlatBatch((long Id, long Score, string? Name, string? Extra) row)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 1);
        MutableColumnVector score = ColumnVectors.Create(DataTypes.LongType, 1);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, 1);
        MutableColumnVector extra = ColumnVectors.Create(DataTypes.StringType, 1);
        id.AppendValue(row.Id);
        score.AppendValue(row.Score);
        AppendNullableString(name, row.Name);
        AppendNullableString(extra, row.Extra);
        return new ManagedColumnBatch(EvolvedFlatSchema, new ColumnVector[] { id, score, name, extra }, 1);

        static void AppendNullableString(MutableColumnVector v, string? s)
        {
            if (s is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendBytes(Encoding.UTF8.GetBytes(s));
            }
        }
    }

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
        // (3 → 4 → 5). Each reload runs ValidateColumnMappingSchema, so a stale-maxColumnId or duplicate-name bug
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

    [Fact]
    public async Task Overwrite_NameMode_Partitioned_OverwriteSchema_KeysPhysical_MetaDataLogical_Issue542()
    {
        // #542 partitioned: a same-schema overwriteSchema on a PARTITIONED name-mode table replaces the data,
        // keeps metaData.partitionColumns LOGICAL, keys add.partitionValues PHYSICALLY, and reads back only the
        // new rows — exercising the logical-vs-physical partition seam through the overwriteSchema path.
        var schema = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        });
        static ManagedColumnBatch Batch(StructType s, string region, long id)
        {
            MutableColumnVector r = ColumnVectors.Create(DataTypes.StringType, 1);
            MutableColumnVector i = ColumnVectors.Create(DataTypes.LongType, 1);
            r.AppendBytes(Encoding.UTF8.GetBytes(region));
            i.AppendValue(id);
            return new ManagedColumnBatch(s, new ColumnVector[] { r, i }, 1);
        }

        Func<string> names = FileNames();
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            await target.CreateNameMappedTableAsync(
                schema, new[] { "region" }, new[] { Batch(schema, "us", 1L) }, new SeededPhysicalNameSource(Seed));
        }
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), names))
        {
            DeltaWriteResult result = await target.OverwriteAsync(
                schema, new[] { "region" }, new[] { Batch(schema, "eu", 2L) },
                DeltaPartitionOverwriteMode.Static, overwriteSchema: true);
            Assert.Equal(1L, result.Version);
        }

        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        string physicalRegion = ColumnMapping.PhysicalName(snapshot.Schema[0], ColumnMappingMode.Name);
        Assert.Equal(new[] { "region" }, snapshot.Metadata.PartitionColumns.ToArray()); // LOGICAL
        Assert.Single(snapshot.ActiveFiles); // prior partition replaced
        Assert.True(snapshot.ActiveFiles[0].PartitionValues.ContainsKey(physicalRegion)); // PHYSICAL key
        Assert.False(snapshot.ActiveFiles[0].PartitionValues.ContainsKey("region"));
        Assert.NotEqual("region", physicalRegion);

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

        Assert.Equal(new (string?, long)[] { ("eu", 2L) }, rows); // only the replacement row
    }

    [Fact]
    public async Task Overwrite_NameMode_DropThenAdd_DoesNotReuseRetiredId_Issue542()
    {
        // #542 cross-commit id retirement: dropping "name" (id 3) via overwriteSchema retires id 3; a LATER
        // overwriteSchema that adds a column must mint id 4 — NEVER the retired 3 (id reuse is a Delta
        // corruption class). maxColumnId is monotonic across commits.
        await CreateNameMappedAsync((1L, 100L, "alice")); // {id,score,name} maxColumnId=3

        using var backend = new LocalFileSystemBackend(_root);

        // Commit 1: overwriteSchema DROP name → {id,score}, maxColumnId stays 3 (id 3 retired).
        var writer1 = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch), new SeededPhysicalNameSource(EvolveSeed));
        await writer1.CreateOrOverwriteAsync(
            new StructType(new[]
            {
                new StructField("id", DataTypes.LongType, nullable: false),
                new StructField("score", DataTypes.LongType, nullable: true),
            }),
            Array.Empty<string>(), new[] { StagedNoSchema("d1.parquet") },
            PartitionOverwriteMode.Static, overwriteSchema: true, CancellationToken.None);
        Assert.Equal("3", (await new DeltaLog(backend).LoadSnapshotAsync(version: null))
            .Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);

        // Commit 2: overwriteSchema ADD fresh → {id,score,fresh}; "fresh" mints id 4 (not the retired 3).
        var writer2 = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch), new SeededPhysicalNameSource(EvolveSeed2));
        string mintedFresh = new SeededPhysicalNameSource(EvolveSeed2).NextPhysicalName();
        await writer2.CreateOrOverwriteAsync(
            new StructType(new[]
            {
                new StructField("id", DataTypes.LongType, nullable: false),
                new StructField("score", DataTypes.LongType, nullable: true),
                new StructField("fresh", DataTypes.StringType, nullable: true),
            }),
            Array.Empty<string>(), new[] { StagedNoSchema("d2.parquet") },
            PartitionOverwriteMode.Static, overwriteSchema: true, CancellationToken.None);

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        AssertMapping(snapshot.Schema["id"], "id", 1, PhysId);
        AssertMapping(snapshot.Schema["score"], "score", 2, PhysScore);
        AssertMapping(snapshot.Schema["fresh"], "fresh", 4, mintedFresh); // id 4, NOT the retired 3
        Assert.NotEqual(PhysName, mintedFresh); // and not the retired physical name
        Assert.Equal("4", snapshot.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]);
    }

    [Fact]
    public async Task Overwrite_NameMode_Reorder_PreservesIdentityByName_Issue542()
    {
        // #542 reorder: an overwriteSchema that REORDERS columns keeps each column's id + physicalName pinned
        // by LOGICAL name (the canonical column-mapping guarantee), independent of the new field order.
        await CreateNameMappedAsync((1L, 100L, "alice")); // {id,score,name} ids 1..3

        using var backend = new LocalFileSystemBackend(_root);
        var writer = new DeltaTableWriter(
            new DeltaLog(backend), new DeltaCommitter(backend),
            new FixedTimeProvider(DateTimeOffset.UnixEpoch), new SeededPhysicalNameSource(EvolveSeed));
        await writer.CreateOrOverwriteAsync(
            new StructType(new[]
            {
                new StructField("name", DataTypes.StringType, nullable: true),
                new StructField("id", DataTypes.LongType, nullable: false),
                new StructField("score", DataTypes.LongType, nullable: true),
            }),
            Array.Empty<string>(), new[] { StagedNoSchema("reorder.parquet") },
            PartitionOverwriteMode.Static, overwriteSchema: true, CancellationToken.None);

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        // New field ORDER is name, id, score; identities stay pinned by name (name=3, id=1, score=2).
        Assert.Equal(new[] { "name", "id", "score" }, snapshot.Schema.Select(f => f.Name).ToArray());
        AssertMapping(snapshot.Schema["name"], "name", 3, PhysName);
        AssertMapping(snapshot.Schema["id"], "id", 1, PhysId);
        AssertMapping(snapshot.Schema["score"], "score", 2, PhysScore);
        Assert.Equal("3", snapshot.Metadata.Configuration[ColumnMapping.MaxColumnIdKey]); // no new column
    }

    [Fact]
    public async Task Overwrite_NameMode_EmptySameSchema_OnEmptyTable_IsNoOp_Issue542()
    {
        // #542 idempotent no-op guard: an empty overwriteSchema (0 files) against an ALREADY-EMPTY name-mode
        // table whose (reconciled) schema is unchanged must short-circuit to Skipped (version unchanged),
        // never a 0-remove/0-add empty commit.
        const string ValuePhysical = "col-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        string schemaJson =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"value\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
            + $"{{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"{ValuePhysical}\"}}}}]}}";

        using var backend = new LocalFileSystemBackend(_root);
        byte[] commit = Encoding.UTF8.GetBytes(
            ProtocolFeatureLine() + "\n"
            + NameModeMetadataLine(
                schemaJson, Array.Empty<string>(),
                ("delta.columnMapping.mode", "name"),
                ("delta.columnMapping.maxColumnId", "1")) + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", commit, CancellationToken.None);

        var writer = new DeltaTableWriter(backend);
        DeltaCommitResult result = await writer.CreateOrOverwriteAsync(
            new StructType(new[] { new StructField("value", DataTypes.LongType, nullable: true) }),
            Array.Empty<string>(), Array.Empty<StagedDataFile>(),
            PartitionOverwriteMode.Static, overwriteSchema: true, CancellationToken.None);

        Assert.True(result.Skipped);
        Assert.Equal(0L, (await new DeltaLog(backend).LoadSnapshotAsync(version: null)).Version);
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

    private async Task CreateIdMappedAsync(params (long Id, long Score, string? Name)[] rows)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(
            _root, new FixedTimeProvider(DateTimeOffset.UnixEpoch), FileNames());
        await target.CreateIdMappedTableAsync(
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

    // Reads the PHYSICAL-name → Parquet footer field_id map for a data file. Parquet.Net does not populate
    // the high-level DataField.FieldId on decode, so the field_id comes from the Thrift footer's
    // SchemaElement.field_id (the exact channel an id-mode / foreign reader resolves columns by — #523/#572).
    // A leaf element without a field_id is omitted.
    private async Task<Dictionary<string, int>> ReadParquetFieldIdsAsync(string relativePath)
    {
        await using FileStream stream = File.OpenRead(Path.Combine(_root, relativePath));
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
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
