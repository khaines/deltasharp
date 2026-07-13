using System.Collections.Immutable;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// End-to-end schema enforcement/evolution tests for <see cref="DeltaTableWriter"/> over a real
/// <see cref="LocalFileSystemBackend"/> (STORY-05.4.2, design §2.12.2). Proves all four acceptance
/// criteria at the write path: an incompatible write is REJECTED before any commit is published so the
/// table is unchanged (AC1); an allowed evolution commits the merged schema's <c>metaData</c> ATOMICALLY
/// with the data adds in one version (AC2); the enforcer's deterministic compatibility rules are applied to
/// real seeded schemas (AC3, with the exhaustive rule matrix in <see cref="DeltaSchemaEnforcerTests"/>);
/// and a write that validated against a now-stale schema CONFLICTS with a concurrent schema change and must
/// refresh (AC4). Mirrors <see cref="DeltaTableWriterTests"/> + <see cref="DeltaTestHarness"/>.
/// </summary>
public sealed class DeltaSchemaEvolutionWriterTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public DeltaSchemaEvolutionWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deltaschema-tests-" + Guid.NewGuid().ToString("N"));
        _backend = new LocalFileSystemBackend(_root);
    }

    public void Dispose()
    {
        _backend.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static StructField F(string name, DataType type, bool nullable) => new(name, type, nullable);

    private static StructType Struct(params StructField[] fields) => new(fields);

    private static readonly ImmutableSortedDictionary<string, string?> NoPartition =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    private static StagedDataFile Staged(string path) =>
        new(path, NoPartition, Size: 1L, ModificationTime: 1L, Stats: null);

    private DeltaTableWriter Writer() => new(_backend);

    private DeltaLog Log() => new(_backend);

    private Task<Snapshot> LoadAsync(long? version = null) => Log().LoadSnapshotAsync(version);

    private async Task SeedSchemaTableAsync(StructType schema)
    {
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchema(schema));
    }

    private async Task CommitRawAsync(long version, params string[] lines) =>
        await DeltaTestHarness.WriteCommitAsync(_backend, version, lines);

    // ------------------------------------------------------- AC1: reject BEFORE any commit

    [Fact]
    public async Task Append_IncompatibleType_IsRejectedBeforeAnyCommit()
    {
        // AC1: an incompatible column type is rejected before a version is written — the table stays at v0.
        await SeedSchemaTableAsync(Struct(F("id", DataTypes.LongType, nullable: false)));
        Snapshot readSnapshot = await LoadAsync();

        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() =>
            Writer().AppendAsync(
                readSnapshot,
                Struct(F("id", DataTypes.StringType, nullable: false)),
                new[] { Staged("a.parquet") }));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
        Assert.Equal(0L, (await LoadAsync()).Version); // nothing published
    }

    [Fact]
    public async Task Append_MissingRequiredColumn_IsRejectedBeforeAnyCommit()
    {
        // AC1: a write that omits a required (non-nullable) column is rejected; the table is unchanged.
        await SeedSchemaTableAsync(Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("name", DataTypes.StringType, nullable: true)));
        Snapshot readSnapshot = await LoadAsync();

        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() =>
            Writer().AppendAsync(
                readSnapshot,
                Struct(F("name", DataTypes.StringType, nullable: true)),
                new[] { Staged("a.parquet") },
                SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.MissingRequiredColumn, ex.Kind);
        Assert.Equal(0L, (await LoadAsync()).Version);
    }

    [Fact]
    public async Task Append_NullabilityViolation_IsRejectedBeforeAnyCommit()
    {
        // AC1: writing a nullable column into a required one is rejected; the table is unchanged.
        await SeedSchemaTableAsync(Struct(F("id", DataTypes.LongType, nullable: false)));
        Snapshot readSnapshot = await LoadAsync();

        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() =>
            Writer().AppendAsync(
                readSnapshot,
                Struct(F("id", DataTypes.LongType, nullable: true)),
                new[] { Staged("a.parquet") },
                SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.NullabilityViolation, ex.Kind);
        Assert.Equal(0L, (await LoadAsync()).Version);
    }

    [Fact]
    public async Task Append_NewColumnWithoutEvolution_IsRejectedBeforeAnyCommit()
    {
        // AC1: a new column with strict enforcement (mode None) is rejected; the table is unchanged.
        await SeedSchemaTableAsync(Struct(F("id", DataTypes.LongType, nullable: false)));
        Snapshot readSnapshot = await LoadAsync();

        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() =>
            Writer().AppendAsync(
                readSnapshot,
                Struct(
                    F("id", DataTypes.LongType, nullable: false),
                    F("name", DataTypes.StringType, nullable: true)),
                new[] { Staged("a.parquet") }));

        Assert.Equal(DeltaSchemaMismatchKind.NewColumnNotAllowed, ex.Kind);
        Assert.Equal(0L, (await LoadAsync()).Version);
    }

    [Fact]
    public async Task Overwrite_IncompatibleType_IsRejectedBeforeAnyCommit()
    {
        // AC1: enforcement guards the overwrite path too — a prior append survives an incompatible overwrite.
        await SeedSchemaTableAsync(Struct(F("id", DataTypes.LongType, nullable: false)));
        await Writer().AppendAsync(
            await LoadAsync(), Struct(F("id", DataTypes.LongType, nullable: false)), new[] { Staged("a.parquet") });
        Snapshot readSnapshot = await LoadAsync();

        await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() =>
            Writer().OverwriteAsync(
                readSnapshot,
                Struct(F("id", DataTypes.StringType, nullable: false)),
                new[] { Staged("b.parquet") }));

        Snapshot after = await LoadAsync();
        Assert.Equal(readSnapshot.Version, after.Version); // no new version
        Assert.Equal(new[] { "a.parquet" }, ActivePaths(after)); // prior data intact
    }

    // ------------------------------------------------------- AC2: evolution commits atomically (one version)

    [Fact]
    public async Task Append_AddNewNullableColumn_CommitsMetadataAndAdd_InOneVersion()
    {
        // AC2: an mergeSchema append commits the merged schema (metaData) AND the new add in ONE version.
        await SeedSchemaTableAsync(Struct(F("id", DataTypes.LongType, nullable: false)));
        Snapshot readSnapshot = await LoadAsync();

        DeltaCommitResult result = await Writer().AppendAsync(
            readSnapshot,
            Struct(
                F("id", DataTypes.LongType, nullable: false),
                F("name", DataTypes.StringType, nullable: true)),
            new[] { Staged("a.parquet") },
            SchemaEvolutionMode.AddNewColumns);

        Assert.Equal(readSnapshot.Version + 1, result.Version);
        IReadOnlyList<DeltaAction> committed = await Log().ReadCommitActionsAsync(result.Version, CancellationToken.None);
        Assert.Single(committed.OfType<MetadataAction>()); // exactly one metadata change ...
        Assert.Equal("a.parquet", Assert.Single(committed.OfType<AddFileAction>()).Path); // ... in the same commit

        StructType evolved = (await LoadAsync()).Schema;
        Assert.Equal(2, evolved.Count);
        Assert.Equal("name", evolved[1].Name);
        Assert.True(evolved[1].Nullable);
    }

    [Fact]
    public async Task Append_WidenType_IsRejectedBeforeAnyCommit()
    {
        // FIX 1: a type-widening append (int → long) is fail-closed at the write path — rejected before any
        // version is written, because widening the logical schema without the typeWidening feature would make
        // the table's existing Parquet files unreadable even by DeltaSharp (#495).
        await SeedSchemaTableAsync(Struct(F("value", DataTypes.IntegerType, nullable: true)));
        Snapshot readSnapshot = await LoadAsync();

        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() =>
            Writer().AppendAsync(
                readSnapshot,
                Struct(F("value", DataTypes.LongType, nullable: true)),
                new[] { Staged("a.parquet") },
                SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal(0L, (await LoadAsync()).Version); // nothing published
    }

    [Fact]
    public async Task Append_CompatibleWriteWithoutSchemaChange_CommitsAddOnly()
    {
        // AC2 boundary: a compatible write that needs no schema change emits NO metaData — just the add.
        await SeedSchemaTableAsync(Struct(F("id", DataTypes.LongType, nullable: false)));
        Snapshot readSnapshot = await LoadAsync();

        DeltaCommitResult result = await Writer().AppendAsync(
            readSnapshot,
            Struct(F("id", DataTypes.LongType, nullable: false)),
            new[] { Staged("a.parquet") },
            SchemaEvolutionMode.MergeSchema);

        IReadOnlyList<DeltaAction> committed = await Log().ReadCommitActionsAsync(result.Version, CancellationToken.None);
        Assert.Empty(committed.OfType<MetadataAction>());
        Assert.Single(committed.OfType<AddFileAction>());
    }

    [Fact]
    public async Task Overwrite_Evolution_CommitsMetadataRemovesAndAdds_InOneVersion()
    {
        // AC2: a full overwrite that also evolves the schema commits metaData + every remove + the new adds
        // as ONE atomic version.
        await SeedSchemaTableAsync(Struct(F("id", DataTypes.LongType, nullable: false)));
        await Writer().AppendAsync(
            await LoadAsync(), Struct(F("id", DataTypes.LongType, nullable: false)), new[] { Staged("a.parquet") });
        Snapshot readSnapshot = await LoadAsync();

        DeltaCommitResult result = await Writer().OverwriteAsync(
            readSnapshot,
            Struct(
                F("id", DataTypes.LongType, nullable: false),
                F("name", DataTypes.StringType, nullable: true)),
            new[] { Staged("b.parquet") },
            PartitionOverwriteMode.Static,
            SchemaEvolutionMode.AddNewColumns);

        Assert.Equal(readSnapshot.Version + 1, result.Version);
        IReadOnlyList<DeltaAction> committed = await Log().ReadCommitActionsAsync(result.Version, CancellationToken.None);
        Assert.Single(committed.OfType<MetadataAction>());
        Assert.Equal("a.parquet", Assert.Single(committed.OfType<RemoveFileAction>()).Path);
        Assert.Equal("b.parquet", Assert.Single(committed.OfType<AddFileAction>()).Path);

        Snapshot after = await LoadAsync();
        Assert.Equal(new[] { "b.parquet" }, ActivePaths(after));
        Assert.Equal(2, after.Schema.Count);
    }

    // ------------------------------------------------------- AC4: stale-schema writes conflict, need refresh

    [Fact]
    public async Task Append_EvolutionWrite_LosesToConcurrentSchemaChange()
    {
        // AC4(a): an evolution write that validated against schema vN aborts if a concurrent winner changed
        // the schema first — the loser must refresh onto the new schema and retry.
        await SeedSchemaTableAsync(Struct(F("id", DataTypes.LongType, nullable: false)));
        Snapshot readSnapshot = await LoadAsync();
        // Concurrent winner evolves the schema at v1.
        await CommitRawAsync(1, DeltaTestHarness.MetadataWithSchema(Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("region", DataTypes.StringType, nullable: true))));

        await Assert.ThrowsAsync<MetadataChangedException>(() =>
            Writer().AppendAsync(
                readSnapshot,
                Struct(
                    F("id", DataTypes.LongType, nullable: false),
                    F("name", DataTypes.StringType, nullable: true)),
                new[] { Staged("a.parquet") },
                SchemaEvolutionMode.AddNewColumns));
    }

    [Fact]
    public async Task Append_PlainStaleSchemaWrite_LosesToConcurrentSchemaChange()
    {
        // AC4(b): the load-bearing stale-schema case — a PLAIN append (no schema change of its own) that
        // validated against schema vN still aborts when a concurrent winner changed the schema, because the
        // winner's metaData makes every concurrent commit conflict. The table schema is part of the write's
        // read dependency, so the writer cannot silently append under a stale schema.
        await SeedSchemaTableAsync(Struct(F("id", DataTypes.LongType, nullable: false)));
        Snapshot readSnapshot = await LoadAsync();
        // Concurrent winner evolves the schema at v1.
        await CommitRawAsync(1, DeltaTestHarness.MetadataWithSchema(Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("region", DataTypes.StringType, nullable: true))));

        // The loser's write is itself compatible (mode None, same schema) — it emits NO metadata — yet it
        // still conflicts with the winner's schema change.
        await Assert.ThrowsAsync<MetadataChangedException>(() =>
            Writer().AppendAsync(readSnapshot, readSnapshot.Schema, new[] { Staged("a.parquet") }));
    }

    [Fact]
    public async Task Append_EvolutionWrite_LosesToConcurrentPlainAppend()
    {
        // AC4(c): a schema-evolving write requires exclusive access, so it aborts even against a concurrent
        // DATA-ONLY append (which changed no schema) — the loser-exclusivity rule. It must refresh so its
        // schema change is computed against the latest committed state.
        await SeedSchemaTableAsync(Struct(F("id", DataTypes.LongType, nullable: false)));
        Snapshot readSnapshot = await LoadAsync();
        // Concurrent winner appends data only (no metadata) at v1.
        await CommitRawAsync(1, DeltaTestHarness.Add("winner.parquet"));

        await Assert.ThrowsAsync<MetadataChangedException>(() =>
            Writer().AppendAsync(
                readSnapshot,
                Struct(
                    F("id", DataTypes.LongType, nullable: false),
                    F("name", DataTypes.StringType, nullable: true)),
                new[] { Staged("a.parquet") },
                SchemaEvolutionMode.AddNewColumns));
    }

    // ------------------------------------------------------- #497: physical write-schema validation

    private StagedDataFile StagedWith(string path, StructType dataSchema) =>
        new(path, NoPartition, Size: 1L, ModificationTime: 1L, Stats: null, DataSchema: dataSchema);

    [Fact]
    public async Task Append_StagedFileMissingColumn_IsRejectedBeforeAnyCommit()
    {
        // #497: the declared write schema says {id, name} but the staged file was physically written with
        // only {id}. Enforcement gates the REAL bytes, so this divergence is rejected before any commit —
        // a caller cannot slip an incompatible file past enforcement by declaring a conforming schema.
        StructType schema = Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("name", DataTypes.StringType, nullable: true));
        await SeedSchemaTableAsync(schema);
        Snapshot readSnapshot = await LoadAsync();

        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() =>
            Writer().AppendAsync(
                readSnapshot,
                schema,
                new[] { StagedWith("a.parquet", Struct(F("id", DataTypes.LongType, nullable: false))) }));

        Assert.Equal(DeltaSchemaMismatchKind.PhysicalWriteSchemaMismatch, ex.Kind);
        Assert.Contains("a.parquet", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0L, (await LoadAsync()).Version); // nothing published
    }

    [Fact]
    public async Task Append_StagedFileWrongColumnType_IsRejectedBeforeAnyCommit()
    {
        // #497: the declared write schema says id:long but the staged file physically stores id:string. The
        // physical schema disagrees with the declaration, so enforcement rejects it (a declared-vs-real-bytes
        // type lie is caught before the enforcer even runs its logical rules).
        StructType schema = Struct(F("id", DataTypes.LongType, nullable: false));
        await SeedSchemaTableAsync(schema);
        Snapshot readSnapshot = await LoadAsync();

        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() =>
            Writer().AppendAsync(
                readSnapshot,
                schema,
                new[] { StagedWith("a.parquet", Struct(F("id", DataTypes.StringType, nullable: false))) }));

        Assert.Equal(DeltaSchemaMismatchKind.PhysicalWriteSchemaMismatch, ex.Kind);
        Assert.Equal(0L, (await LoadAsync()).Version);
    }

    [Fact]
    public async Task Append_StagedFileSchemaMatchesDeclared_IsAccepted()
    {
        // #497: when the staged file's physical data schema MATCHES the declared write schema's data columns,
        // the validation is transparent — the append commits normally (v1). This is the production write-door
        // shape (DeltaWriteTarget records the true written schema, which equals what it declares).
        StructType schema = Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("name", DataTypes.StringType, nullable: true));
        await SeedSchemaTableAsync(schema);
        Snapshot readSnapshot = await LoadAsync();

        DeltaCommitResult result = await Writer().AppendAsync(
            readSnapshot, schema, new[] { StagedWith("a.parquet", schema) });

        Assert.Equal(1L, result.Version);
        Assert.Equal(1L, (await LoadAsync()).Version);
    }

    [Fact]
    public async Task CreateOrAppend_CreateWithDivergentStagedSchema_IsRejected()
    {
        // #497: the version-0 metaData schema is gated on the real staged bytes too — a fresh create whose
        // declared schema {id, name} disagrees with the staged file's physical {id} is rejected, and no table
        // is created (the path stays empty).
        StructType declared = Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("name", DataTypes.StringType, nullable: true));

        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() =>
            Writer().CreateOrAppendAsync(
                declared,
                Array.Empty<string>(),
                new[] { StagedWith("a.parquet", Struct(F("id", DataTypes.LongType, nullable: false))) }));

        Assert.Equal(DeltaSchemaMismatchKind.PhysicalWriteSchemaMismatch, ex.Kind);
        Assert.Null(await Log().GetLatestCommitVersionAsync(CancellationToken.None)); // no table created
    }

    [Fact]
    public async Task Append_StagedFileWithoutDataSchema_SkipsValidation()
    {
        // Back-compat: a StagedDataFile that does NOT carry a DataSchema (a caller that does not supply it)
        // skips the physical cross-check — the append proceeds on the declared schema exactly as before #497.
        StructType schema = Struct(F("id", DataTypes.LongType, nullable: false));
        await SeedSchemaTableAsync(schema);
        Snapshot readSnapshot = await LoadAsync();

        DeltaCommitResult result = await Writer().AppendAsync(
            readSnapshot, schema, new[] { Staged("a.parquet") });

        Assert.Equal(1L, result.Version);
    }

    private static string[] ActivePaths(Snapshot snapshot) =>
        snapshot.ActiveFiles.Select(a => a.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
}
