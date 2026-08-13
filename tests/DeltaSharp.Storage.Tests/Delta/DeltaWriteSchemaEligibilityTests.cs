using System.Collections.Immutable;
using System.Text.Json;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// #702 (Round-1 review, Blocker 1): end-to-end pins on the <b>declared-write-schema type-eligibility
/// door</b> (<see cref="DeltaWriteSchemaEligibility"/>).
/// </summary>
/// <remarks>
/// <para>
/// The pre-review verdict on #702 held that <c>NullType</c> (<c>"void"</c>) could never reach a committed
/// <c>metaData.schemaString</c> because <c>ParquetTypeMapping.CreateField</c> rejects it first. That was
/// FALSE. <c>CreateField</c> is a PER-FILE guard, and a <b>zero-file create</b> — an empty write to a fresh
/// path, which legitimately creates the empty table at version 0 — stages no file at all:
/// <c>DeltaTableWriter.CreateOrAppendAsync</c> routes <c>readSnapshot is null</c> to the create BEFORE the
/// <c>files.Count == 0</c> short-circuit, <c>ValidateStagedWriteSchema</c> then iterates an empty list, and
/// <c>CreateTableCoreAsync</c> committed <c>SchemaJson.ToJson(writeSchema)</c> verbatim. The result was a
/// version-0 table carrying <c>"type":"void"</c> that DeltaSharp itself could not read.
/// </para>
/// <para>
/// These tests drive the exact repro (and its nested variants) and assert the write is refused with
/// <see cref="StorageErrorKind.UnsupportedFeature"/> having published NOTHING — no <c>_delta_log</c>, no
/// staged Parquet. The last test pins the deliberate other half of the contract: READ tolerance of a foreign
/// <c>"void"</c> schemaString is unchanged (delta-rs 1.6.2 maps it to Arrow Null), because the rejection is
/// on the declared WRITE schema, not on the serializer.
/// </para>
/// </remarks>
public sealed class DeltaWriteSchemaEligibilityTests : IDisposable
{
    private readonly string _root;

    public DeltaWriteSchemaEligibilityTests() =>
        _root = Path.Combine(Path.GetTempPath(), "deltavoid-tests-" + Guid.NewGuid().ToString("N"));

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

    private static StructField F(string name, DataType type, bool nullable = true) => new(name, type, nullable);

    private static StructType Struct(params StructField[] fields) => new(fields);

    // The declared write schemas that carry a void leaf: top level, then one per nested position the walk
    // has to cover (array element, map VALUE, nested struct field). A BARE void map key (`map<void,string>`)
    // cannot be built at all — MapType's constructor rejects it — so it has no row here; a void nested
    // INSIDE a key IS constructible and is pinned separately below
    // (ZeroFileCreate_WithVoidInsideAMapKey_CommitsNothing).
    public static TheoryData<string> VoidShapes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string shape in new[] { "top-level", "array-element", "map-value", "struct-field" })
            {
                data.Add(shape);
            }

            return data;
        }
    }

    private static StructType VoidSchema(string shape) => shape switch
    {
        "top-level" => Struct(F("id", DataTypes.LongType, nullable: false), F("v", DataTypes.NullType)),
        "array-element" => Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("v", DataTypes.CreateArrayType(DataTypes.NullType, containsNull: true))),
        "map-value" => Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("v", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.NullType, valueContainsNull: true))),
        "struct-field" => Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("v", Struct(F("inner", DataTypes.NullType)))),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    // The exact leaf path the message must name for each shape — so the guard cannot silently stop
    // descending into one of the nested positions and still pass by rejecting some OTHER column.
    private static string ExpectedPath(string shape) => shape switch
    {
        "top-level" => "'v'",
        "array-element" => "'v.element'",
        "map-value" => "'v.value'",
        "struct-field" => "'v.inner'",
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private void AssertNothingWasWritten()
    {
        Assert.False(
            Directory.Exists(Path.Combine(_root, "_delta_log")),
            "the rejected write must not create a _delta_log");

        if (Directory.Exists(_root))
        {
            Assert.Empty(Directory.GetFiles(_root, "*.parquet", SearchOption.AllDirectories));
        }
    }

    // ---- the #702 repro: a ZERO-FILE create ------------------------------------------------------------

    [Theory]
    [MemberData(nameof(VoidShapes))]
    public async Task ZeroFileCreate_WithVoidColumn_CommitsNothing(string shape)
    {
        using var target = DeltaWriteTarget.ForLocalPath(_root);

        // The falsifying call from the review: an empty write (no batches, therefore no staged files) to a
        // fresh path. Every per-file guard is bypassed; only the write-schema door can stop this.
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => target.AppendAsync(VoidSchema(shape), Array.Empty<string>(), Array.Empty<ColumnBatch>()));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, ex.Kind);
        Assert.Contains("NullType column", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ExpectedPath(shape), ex.Message, StringComparison.Ordinal);
        Assert.Contains("no physical layout", ex.Message, StringComparison.Ordinal);
        AssertNothingWasWritten();
    }

    [Theory]
    [MemberData(nameof(VoidShapes))]
    public async Task ZeroFileOverwriteCreate_WithVoidColumn_CommitsNothing(string shape)
    {
        using var target = DeltaWriteTarget.ForLocalPath(_root);

        // The overwrite door's fresh-path branch creates the table exactly as the append door does, so it is
        // the same zero-file hole and must be closed by the same check.
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => target.OverwriteAsync(
                VoidSchema(shape),
                Array.Empty<string>(),
                Array.Empty<ColumnBatch>(),
                DeltaPartitionOverwriteMode.Static));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, ex.Kind);
        Assert.Contains("NullType column", ex.Message, StringComparison.Ordinal);
        AssertNothingWasWritten();
    }

    [Fact]
    public async Task ZeroFileCreate_WithoutVoidColumn_StillCreatesTheEmptyTable()
    {
        // The door must reject ONLY the ineligible type: an empty create of a well-typed schema is a
        // legitimate operation (Spark parity) and still publishes version 0.
        using var target = DeltaWriteTarget.ForLocalPath(_root);

        DeltaWriteResult result = await target.AppendAsync(
            Struct(F("id", DataTypes.LongType, nullable: false)),
            Array.Empty<string>(),
            Array.Empty<ColumnBatch>());

        Assert.Equal(0L, result.Version);
        Assert.Equal(0, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(_root, "_delta_log", "00000000000000000000.json")));
    }

    // ---- the map-KEY arm (Round-3 review, C1: unpinned live control) -----------------------------------

    // MapType's constructor bans a BARE void key, but only the key's OUTERMOST type: `map<array<void>,string>`
    // and `map<struct<k:void>,string>` are perfectly constructible and still declare a leaf with no physical
    // layout. The walk's map-KEY push is therefore load-bearing, yet deleting it left the ENTIRE Storage suite
    // green — no test descended into a key. These rows are that missing control: they fail iff the key push is
    // dropped, and they name the exact leaf INSIDE the key, so the arm cannot pass by rejecting some other
    // position (the map VALUE row above shares the same column shape).
    [Theory]
    [InlineData("key-array-element", "'m.key.element'")]
    [InlineData("key-struct-field", "'m.key.k'")]
    public async Task ZeroFileCreate_WithVoidInsideAMapKey_CommitsNothing(string shape, string expectedPath)
    {
        using var target = DeltaWriteTarget.ForLocalPath(_root);

        // Same zero-file create as the repro above: no staged file, so only the write-schema door can stop it.
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => target.AppendAsync(VoidInsideMapKeySchema(shape), Array.Empty<string>(), Array.Empty<ColumnBatch>()));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, ex.Kind);
        Assert.Contains("NullType column", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expectedPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains("no physical layout", ex.Message, StringComparison.Ordinal);
        AssertNothingWasWritten();
    }

    // The two constructible shapes that hide a void UNDER a map key. The map VALUE is a plain string in both,
    // so the only ineligible leaf in the schema sits on the key side.
    private static StructType VoidInsideMapKeySchema(string shape) => shape switch
    {
        "key-array-element" => Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("m", DataTypes.CreateMapType(
                DataTypes.CreateArrayType(DataTypes.NullType, containsNull: true),
                DataTypes.StringType))),
        "key-struct-field" => Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("m", DataTypes.CreateMapType(Struct(F("k", DataTypes.NullType)), DataTypes.StringType))),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    // ---- the evolution / replacement paths (also independent of staged-file count) ----------------------

    [Fact]
    public async Task SchemaEvolution_AddingAVoidColumn_IsRejectedBeforeAnyCommit()
    {
        using var backend = new LocalFileSystemBackend(_root);
        await DeltaTestHarness.WriteCommitAsync(
            backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchema(Struct(F("id", DataTypes.LongType, nullable: false))));

        var log = new DeltaLog(backend);
        Snapshot snapshot = await log.LoadSnapshotAsync(version: null);
        var writer = new DeltaTableWriter(backend);

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(() =>
            writer.AppendAsync(
                snapshot,
                Struct(F("id", DataTypes.LongType, nullable: false), F("v", DataTypes.NullType)),
                new[]
                {
                    new StagedDataFile(
                        "a.parquet",
                        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal),
                        Size: 1L,
                        ModificationTime: 1L,
                        Stats: null),
                },
                SchemaEvolutionMode.MergeSchema));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, ex.Kind);
        Assert.Contains("NullType column", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0L, (await log.LoadSnapshotAsync(version: null)).Version); // nothing published
    }

    [Fact]
    public async Task OverwriteSchemaReplacement_WithAVoidColumn_IsRejectedBeforeAnyCommit()
    {
        using var backend = new LocalFileSystemBackend(_root);
        await DeltaTestHarness.WriteCommitAsync(
            backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchema(Struct(F("id", DataTypes.LongType, nullable: false))));

        var log = new DeltaLog(backend);
        Snapshot snapshot = await log.LoadSnapshotAsync(version: null);
        var writer = new DeltaTableWriter(backend);

        // The overwriteSchema PLAN is resolved before the door stages a single byte, so the rejection lands
        // there (no partial write to clean up).
        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(() =>
            writer.PlanOverwriteReplaceSchema(
                snapshot,
                Struct(F("id", DataTypes.LongType, nullable: false), F("v", DataTypes.NullType)),
                Array.Empty<string>()));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, ex.Kind);
        Assert.Equal(0L, (await log.LoadSnapshotAsync(version: null)).Version);
    }

    // ---- the walk itself must be bounded (Round-2 review, Blocker 1) ------------------------------------

    // The Round-1 door walked the declared type tree RECURSIVELY, to whatever depth the caller declared, and
    // it runs BEFORE SchemaJson.ToJson — so it ran ahead of the serializer's own depth guard. A programmatic
    // / schema-inference caller supplying a pathologically deep type tree therefore overflowed the stack with
    // an UNCATCHABLE StackOverflowException (a process abort, ~4,000 levels on a thread-pool stack), where
    // the pre-door code path had failed closed with a catchable exception. The walk is now iterative with an
    // explicit stack and a depth bound (the same rule NestedTypeDepth documents), so a pathological schema is
    // a planned refusal.
    //
    // 5,000 is past the thread-pool stack limit that crashed; 20,000 is past the main-thread limit too. Both
    // must return a CATCHABLE exception, and neither may publish anything.
    [Theory]
    [InlineData(5_000)]
    [InlineData(20_000)]
    public async Task DeclaredSchema_NestedAbsurdlyDeep_IsRefusedCatchably_AndCommitsNothing(int levels)
    {
        using var target = DeltaWriteTarget.ForLocalPath(_root);

        // A zero-file create again: the door is the only thing between this schema and a schemaString.
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => target.AppendAsync(
                Struct(F("id", DataTypes.LongType, nullable: false), F("deep", DeepArray(levels))),
                Array.Empty<string>(),
                Array.Empty<ColumnBatch>()));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, ex.Kind);
        Assert.Contains("nests deeper than", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'deep'", ex.Message, StringComparison.Ordinal);
        AssertNothingWasWritten();
    }

    // The bound must not swallow a legitimately (if unusually) nested schema: a depth the schemaString
    // serializer itself accepts still creates the table. This is the other side of the depth pin — a guard
    // that rejected everything would also make the deep-schema test above pass.
    //
    // The rows are the ordinary case (8) and the REAL EDGE (61). 61 is the deepest array chain
    // SchemaJson.ToJson will serialize inside a struct schema: the schema costs 3 JSON containers (struct
    // object + `fields` array + field object) before the column's type opens, then one container per array
    // level, so level 62 trips the serializer's MaxDepth = 64 and level 61 is the last one that commits.
    // Without this row the accept side pinned only depth 8, so an eligibility MaxDepth regressed anywhere
    // into 21..61 — every value of which OVER-REJECTS a schema ToJson is willing to write — stayed green
    // (a MaxDepth = 20 mutant survived the whole suite). Pinning at 61 makes any such regression RED.
    [Theory]
    [InlineData(8)]
    [InlineData(61)]
    public async Task DeclaredSchema_NestedWithinTheBound_IsStillAccepted(int levels)
    {
        using (var target = DeltaWriteTarget.ForLocalPath(_root))
        {
            DeltaWriteResult result = await target.AppendAsync(
                Struct(F("id", DataTypes.LongType, nullable: false), F("deep", DeepArray(levels))),
                Array.Empty<string>(),
                Array.Empty<ColumnBatch>());

            Assert.Equal(0L, result.Version);
        }

        // ...and the committed schemaString round-trips: the accepted depth is not merely committed, it is
        // re-readable (the write-side door and the serializer's shared read/write bound agree at the edge).
        using var backend = new LocalFileSystemBackend(_root);
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        Assert.Equal(0L, snapshot.Version);
        Assert.Equal(2, snapshot.Schema.Count);
        Assert.Equal("deep", snapshot.Schema[1].Name);
        Assert.True(DeepArray(levels).Equals(snapshot.Schema[1].DataType));
    }

    // An `array<array<...<long>>>` chain `levels` deep, built ITERATIVELY (building it recursively would
    // overflow the test's own stack before the door could be reached).
    private static DataType DeepArray(int levels)
    {
        DataType type = DataTypes.LongType;
        for (int i = 0; i < levels; i++)
        {
            type = DataTypes.CreateArrayType(type, containsNull: true);
        }

        return type;
    }

    // ---- the two previously UNPINNED chokepoints (Round-2 review, Blocker 3) ----------------------------

    // Chokepoint 1 — DeltaTableWriter's name/id-mode evolution branch. It calls EnsureCommittable on the
    // MAPPED evolved schema, a different call site from the none-mode LogicalEvolution branch the test above
    // covers, and dropping it left the whole Storage suite green.
    [Fact]
    public async Task NameModeEvolution_AddingAVoidColumn_IsRejectedBeforeAnyCommit()
    {
        using var backend = new LocalFileSystemBackend(_root);
        await DeltaTestHarness.WriteCommitAsync(
            backend, 0, ColumnMappingProtocolLine, NameModeMetadataLine(MappedFields(("id", "long", 1))));

        var log = new DeltaLog(backend);
        Snapshot snapshot = await log.LoadSnapshotAsync(version: null);
        var writer = new DeltaTableWriter(backend);

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(() =>
            writer.AppendAsync(
                snapshot,
                Struct(F("id", DataTypes.LongType, nullable: false), F("v", DataTypes.NullType)),
                new[]
                {
                    new StagedDataFile(
                        "a.parquet",
                        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal),
                        Size: 1L,
                        ModificationTime: 1L,
                        Stats: null),
                },
                SchemaEvolutionMode.MergeSchema));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, ex.Kind);
        Assert.Contains("NullType column", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'v'", ex.Message, StringComparison.Ordinal); // the exact leaf, not merely "a column"
        Assert.Equal(0L, (await log.LoadSnapshotAsync(version: null)).Version); // nothing published
    }

    // Chokepoint 2 — CommitSchemaChangeAsync (the metadata-only ALTER path). It stages no file at all, so no
    // per-file guard can cover it, and it too was unpinned.
    //
    // It also carries a deliberate BEHAVIOR CHANGE worth pinning explicitly: a FOREIGN table that already
    // carries a `void` column stays readable (the read tolerance below is unchanged), but an ALTER of an
    // UNRELATED column now re-declares that same void column in the post-ALTER schema and is therefore
    // refused. That is the safe direction — the ALTER would otherwise re-commit the ineligible type — and it
    // is not a brick: dropping the void column itself still succeeds, which is the remedy.
    [Fact]
    public async Task Alter_OnForeignTableCarryingAVoidColumn_IsRefused_ButDroppingTheVoidColumnSucceeds()
    {
        using var backend = new LocalFileSystemBackend(_root);
        await DeltaTestHarness.WriteCommitAsync(
            backend,
            0,
            ColumnMappingProtocolLine,
            NameModeMetadataLine(MappedFields(("id", "long", 1), ("v", "void", 2))));

        var log = new DeltaLog(backend);

        // The foreign table LOADS (read tolerance for a foreign void column is unchanged).
        Snapshot loaded = await log.LoadSnapshotAsync(version: null);
        Assert.Equal(2, loaded.Schema.Count);

        // ALTER of an UNRELATED column re-declares the void column => refused, nothing committed.
        var writer = new DeltaTableWriter(backend);
        DeltaStorageException rename = await Assert.ThrowsAsync<DeltaStorageException>(
            () => writer.RenameColumnAsync("id", "ident"));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, rename.Kind);
        Assert.Contains("NullType column", rename.Message, StringComparison.Ordinal);
        Assert.Contains("'v'", rename.Message, StringComparison.Ordinal);
        Assert.Equal(0L, (await log.LoadSnapshotAsync(version: null)).Version);

        // ...and the remedy works: dropping the void column itself yields an eligible post-ALTER schema.
        DeltaCommitResult drop = await writer.DropColumnAsync("v");
        Assert.Equal(1L, drop.Version);

        Snapshot after = await new DeltaLog(backend).LoadSnapshotAsync(version: null);
        Assert.Single(after.Schema);
        Assert.Equal("id", after.Schema[0].Name);

        // With the void column gone the previously-refused ALTER succeeds — the refusal was scoped to the
        // ineligible declaration, not to the table.
        DeltaCommitResult renamed = await new DeltaTableWriter(backend).RenameColumnAsync("id", "ident");
        Assert.Equal(2L, renamed.Version);
    }

    private const string ColumnMappingProtocolLine =
        """{"protocol":{"minReaderVersion":3,"minWriterVersion":7,"readerFeatures":["columnMapping"],"writerFeatures":["columnMapping"]}}""";

    // Builds the `fields` array of a name-mode schemaString: each column carries the physicalName + id the
    // mode requires. The `type` is written as a RAW token so a foreign "void" can be declared (the write-side
    // serializer would refuse to produce this schema — that is the point of the fixture).
    private static string MappedFields(params (string Name, string Type, long Id)[] columns) =>
        "{\"type\":\"struct\",\"fields\":["
        + string.Join(",", columns.Select(c =>
            $"{{\"name\":\"{c.Name}\",\"type\":\"{c.Type}\",\"nullable\":true,\"metadata\":"
            + $"{{\"delta.columnMapping.id\":{c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
            + $"\"delta.columnMapping.physicalName\":\"col-{c.Name}\"}}}}"))
        + "]}";

    private static string NameModeMetadataLine(string schemaJson) =>
        "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},\"schemaString\":"
        + JsonSerializer.Serialize(schemaJson)
        + ",\"partitionColumns\":[],\"configuration\":{\"delta.columnMapping.mode\":\"name\","
        + "\"delta.columnMapping.maxColumnId\":\"2\"}}}";

    // ---- the deliberate other half: READ tolerance of a FOREIGN "void" schemaString is unchanged ---------

    [Fact]
    public async Task ForeignVoidSchemaString_StillParsesOnRead()
    {
        // delta-rs 1.6.2 (and Spark) can write "void"; DeltaSharp must still be able to LOAD such a snapshot
        // and report its schema. The #702 rejection is on the DECLARED WRITE schema — a serializer-level or
        // parser-level rejection would break cross-engine read parity instead.
        using var backend = new LocalFileSystemBackend(_root);
        const string SchemaJsonText =
            """{"type":"struct","fields":[{"name":"v","type":"void","nullable":true,"metadata":{}}]}""";
        await DeltaTestHarness.WriteCommitAsync(
            backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            MetadataLine(SchemaJsonText));

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync(version: null);

        Assert.Single(snapshot.Schema);
        Assert.Equal(DataTypes.NullType, snapshot.Schema[0].DataType);
    }

    // ---- read-door negative goldens: invalid UTF-16 in a foreign schemaString (#710 read mirror) ---------

    // Round-1 review (Blocker 2): SchemaJson.FromJson used to let System.Text.Json's untyped
    // InvalidOperationException (escaped lone surrogate) / ArgumentException (raw one) escape past every
    // caller's `catch (SchemaValidationException)`. Through the real snapshot read path that meant an
    // unclassified crash instead of the documented fail-closed DeltaProtocolException.
    [Theory]
    [InlineData("field-name")]
    [InlineData("metadata-key")]
    [InlineData("metadata-value")]
    [InlineData("type-name")]
    public async Task HostileSchemaString_WithLoneSurrogate_FailsClosedAsDeltaProtocolException(string position)
    {
        // The schemaString itself carries the six-character escape \uD800 (a LONE high surrogate), which is
        // legal JSON syntax but decodes to invalid UTF-16.
        string schemaJson = position switch
        {
            "field-name" =>
                """{"type":"struct","fields":[{"name":"x\uD800","type":"string","nullable":true,"metadata":{}}]}""",
            "metadata-key" =>
                """{"type":"struct","fields":[{"name":"f","type":"string","nullable":true,"metadata":{"k\uD800":1}}]}""",
            "metadata-value" =>
                """{"type":"struct","fields":[{"name":"f","type":"string","nullable":true,"metadata":{"k":"v\uD800"}}]}""",
            "type-name" =>
                """{"type":"struct","fields":[{"name":"f","type":"str\uD800","nullable":true,"metadata":{}}]}""",
            _ => throw new ArgumentOutOfRangeException(nameof(position)),
        };

        using var backend = new LocalFileSystemBackend(_root);
        await DeltaTestHarness.WriteCommitAsync(
            backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            MetadataLine(schemaJson));

        // The snapshot load itself validates the schema (DeltaLog.ReconstructAsync touches Snapshot.Schema),
        // so the fail-closed classification has to hold at the READ door, not merely on later access.
        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaLog(backend).LoadSnapshotAsync(version: null));

        Assert.Equal(DeltaProtocolErrorKind.InconsistentLog, ex.Kind);
        Assert.IsType<SchemaValidationException>(ex.InnerException);
        // Content-free at every layer: the malformed token is never echoed.
        Assert.DoesNotContain('\uD800', ex.Message);
        Assert.DoesNotContain('\uD800', ex.InnerException!.Message);
    }

    // Round-2 review (Blocker 4): the broad fail-closed net above also catches an ArgumentException raised by
    // a CONSTRUCTOR reached during the read — StructField's ThrowIfNullOrEmpty(name) for a foreign
    // `"name":""` — which the Round-1 wording described as "invalid UTF-16". The empty name now gets its own
    // precise, content-free diagnostic (and the residual net no longer asserts a cause it cannot know).
    [Fact]
    public async Task ForeignSchemaString_WithEmptyFieldName_FailsClosedWithAPreciseMessage()
    {
        const string SchemaJsonText =
            """{"type":"struct","fields":[{"name":"","type":"string","nullable":true,"metadata":{}}]}""";

        using var backend = new LocalFileSystemBackend(_root);
        await DeltaTestHarness.WriteCommitAsync(
            backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            MetadataLine(SchemaJsonText));

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaLog(backend).LoadSnapshotAsync(version: null));

        Assert.Equal(DeltaProtocolErrorKind.InconsistentLog, ex.Kind);
        var inner = Assert.IsType<SchemaValidationException>(ex.InnerException);
        Assert.Contains("must be non-empty", inner.Message, StringComparison.Ordinal);
        // The mis-attribution the fix removes: an empty name is not a decoding fault.
        Assert.DoesNotContain("UTF-16", inner.Message, StringComparison.Ordinal);
    }

    // A metaData commit line carrying an ARBITRARY schemaString verbatim (the harness helpers serialize a
    // StructType through the write-side serializer, which by design refuses the hostile inputs above).
    private static string MetadataLine(string schemaJson) =>
        "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},\"schemaString\":"
        + JsonSerializer.Serialize(schemaJson)
        + ",\"partitionColumns\":[],\"configuration\":{}}}";
}
