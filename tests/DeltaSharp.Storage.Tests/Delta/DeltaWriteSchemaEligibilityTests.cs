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
    // has to cover (array element, map VALUE, nested struct field). A map KEY cannot be built at all —
    // MapType's constructor already rejects a NullType key — so that position is closed upstream.
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

    // A metaData commit line carrying an ARBITRARY schemaString verbatim (the harness helpers serialize a
    // StructType through the write-side serializer, which by design refuses the hostile inputs above).
    private static string MetadataLine(string schemaJson) =>
        "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},\"schemaString\":"
        + JsonSerializer.Serialize(schemaJson)
        + ",\"partitionColumns\":[],\"configuration\":{}}}";
}
