using System.Collections.Immutable;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
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

    private static StagedDataFile StagedPartitioned(string path, params (string Key, string? Value)[] partitionValues)
    {
        ImmutableSortedDictionary<string, string?> pv = ImmutableSortedDictionary
            .CreateRange(StringComparer.Ordinal, partitionValues.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));
        return new StagedDataFile(path, pv, Size: 1L, ModificationTime: 1L, Stats: null);
    }

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

    [Fact]
    public async Task Append_FooterDerivedSchemaDivergesFromDeclared_IsRejected_GatesRealBytes()
    {
        // #497 (the core anti-bypass proof): write a REAL Parquet file with schema {id} only, derive its
        // DataSchema from the ACTUAL footer (ReadDataSchemaAsync), then commit declaring the wider schema
        // {id, name}. Because DataSchema comes from the real bytes — not the declaration — the commit-time
        // cross-check catches the divergence and rejects it. This is what makes the check gate the real
        // bytes rather than re-validating the declaration against itself.
        StructType declared = Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("name", DataTypes.StringType, nullable: true));
        await SeedSchemaTableAsync(declared);
        Snapshot readSnapshot = await LoadAsync();

        StagedDataFile realFile = await StageRealFileAsync(
            "real.parquet", Struct(F("id", DataTypes.LongType, nullable: false)), new long[] { 1, 2, 3 });
        // The footer-derived DataSchema reflects the real narrow bytes, not the wide declaration.
        Assert.NotNull(realFile.DataSchema);
        Assert.Single(realFile.DataSchema!);
        Assert.Equal("id", realFile.DataSchema![0].Name);

        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() =>
            Writer().AppendAsync(readSnapshot, declared, new[] { realFile }));

        Assert.Equal(DeltaSchemaMismatchKind.PhysicalWriteSchemaMismatch, ex.Kind);
        Assert.Contains("real.parquet", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0L, (await LoadAsync()).Version); // nothing published
    }

    [Fact]
    public async Task Append_StagedFileColumnRenamed_SameCountAndTypes_IsRejected()
    {
        // #497: DataColumnsMatch compares NAME and type. A staged file with the same column count and types
        // but a DIFFERENT column name than declared (a rename divergence) must still be rejected — the name
        // half of the comparison, distinct from the count/type cases.
        StructType declared = Struct(F("id", DataTypes.LongType, nullable: false));
        await SeedSchemaTableAsync(declared);
        Snapshot readSnapshot = await LoadAsync();

        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(() =>
            Writer().AppendAsync(
                readSnapshot,
                declared,
                new[] { StagedWith("a.parquet", Struct(F("identifier", DataTypes.LongType, nullable: false))) }));

        Assert.Equal(DeltaSchemaMismatchKind.PhysicalWriteSchemaMismatch, ex.Kind);
        Assert.Equal(0L, (await LoadAsync()).Version);
    }

    // Writes a REAL Parquet file (id:long column) to the backend under an arbitrary declared schema, then
    // records a StagedDataFile whose DataSchema is derived from the ACTUAL written footer (mirroring the
    // production write-door), so a test can prove the commit-time check gates real bytes.
    private async Task<StagedDataFile> StageRealFileAsync(string path, StructType writeSchema, long[] ids)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, ids.Length);
        foreach (long value in ids)
        {
            id.AppendValue(value);
        }

        var batch = new ManagedColumnBatch(writeSchema, new ColumnVector[] { id }, ids.Length);
        using var buffer = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(buffer, writeSchema, new[] { batch }, CancellationToken.None);
        byte[] bytes = buffer.ToArray();
        await _backend.PutIfAbsentAsync(path, bytes, CancellationToken.None);

        using var footer = new MemoryStream(bytes, writable: false);
        StructType actual = await new ParquetFileReader().ReadDataSchemaAsync(footer, CancellationToken.None);
        return new StagedDataFile(path, NoPartition, Size: bytes.LongLength, ModificationTime: 1L, Stats: null, DataSchema: actual);
    }

    private static string[] ActivePaths(Snapshot snapshot) =>
        snapshot.ActiveFiles.Select(a => a.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();

    // Writes a REAL single-column Int32 Parquet file to the backend and returns its bytes, so a #495
    // end-to-end test can widen the schema over the OLD narrow file and read it back promoted.
    private async Task<byte[]> WriteNarrowIntFileAsync(string path, int[] values)
    {
        var schema = new StructType(new[] { new StructField("value", DataTypes.IntegerType, nullable: true) });
        MutableColumnVector column = ColumnVectors.Create(DataTypes.IntegerType, values.Length);
        foreach (int value in values)
        {
            column.AppendValue(value);
        }

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { column }, values.Length);
        using var buffer = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(buffer, schema, new[] { batch }, CancellationToken.None);
        byte[] bytes = buffer.ToArray();
        await _backend.PutIfAbsentAsync(path, bytes, CancellationToken.None);
        return bytes;
    }

    // ------------------------------------------------------- #495: type widening end-to-end

    [Fact]
    public async Task Append_WidenType_WhenFeatureEnabled_CommitsWidenedSchemaAndTypeChanges_AndPromotesOnRead()
    {
        // Seed a typeWidening-ENABLED table (protocol v3/v7 declaring the feature + delta.enableTypeWidening)
        // whose v0 holds a real Int32 data file.
        var initial = Struct(F("value", DataTypes.IntegerType, nullable: true));
        byte[] oldFile = await WriteNarrowIntFileAsync("v0.parquet", new[] { 1, 2, 3 });
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.ProtocolWithReaderFeature("typeWidening"),
            DeltaTestHarness.MetadataWithSchemaAndConfig(initial, new[] { ("delta.enableTypeWidening", "true") }),
            DeltaTestHarness.Add("v0.parquet"));

        // A widening append (int → long) is now APPLIED: it commits the widened metaData atomically with the
        // new add, in one version.
        Snapshot readSnapshot = await LoadAsync();
        DeltaCommitResult result = await Writer().AppendAsync(
            readSnapshot,
            Struct(F("value", DataTypes.LongType, nullable: true)),
            new[] { Staged("v1.parquet") },
            SchemaEvolutionMode.MergeSchema);

        IReadOnlyList<DeltaAction> committed = await Log().ReadCommitActionsAsync(result.Version, CancellationToken.None);
        Assert.Single(committed.OfType<MetadataAction>());
        Assert.Contains(committed.OfType<AddFileAction>(), a => a.Path == "v1.parquet");

        // The committed schema is `long` and carries a delta.typeChanges {fromType:"integer",toType:"long"}.
        StructType evolved = (await LoadAsync()).Schema;
        StructField field = evolved["value"];
        Assert.Equal(DataTypes.LongType, field.DataType);
        Assert.True(field.Metadata.TryGetValue("delta.typeChanges", out MetadataValue? changes));
        Assert.True(changes!.TryGetArray(out IReadOnlyList<MetadataValue>? entries));
        MetadataValue only = Assert.Single(entries!);
        Assert.True(only.TryGetNested(out FieldMetadata? nested));
        Assert.True(nested!.TryGetString("fromType", out string? from));
        Assert.True(nested.TryGetString("toType", out string? to));
        Assert.Equal("integer", from);
        Assert.Equal("long", to);

        // The OLD Int32 v0 file is still readable under the now-wide `long` schema: promotion on read (the
        // committed table declares the typeWidening feature, so the read-side promotion gate is open).
        List<ColumnBatch> promoted = await ParquetTestHelpers.ReadAllAsync(oldFile, evolved, keepRowGroup: null, allowTypeWideningPromotion: true);
        Assert.Equal(new long[] { 1L, 2L, 3L }, promoted.Single().Column(0).GetValues<long>().ToArray());
    }

    // Writes a REAL single-column Int64 Parquet file to the backend, so a #535 cross-family end-to-end test
    // can widen long→decimal over the OLD narrow file and read it back promoted.
    private async Task<byte[]> WriteNarrowLongFileAsync(string path, long[] values)
    {
        var schema = new StructType(new[] { new StructField("value", DataTypes.LongType, nullable: true) });
        MutableColumnVector column = ColumnVectors.Create(DataTypes.LongType, values.Length);
        foreach (long value in values)
        {
            column.AppendValue(value);
        }

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { column }, values.Length);
        using var buffer = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(buffer, schema, new[] { batch }, CancellationToken.None);
        byte[] bytes = buffer.ToArray();
        await _backend.PutIfAbsentAsync(path, bytes, CancellationToken.None);
        return bytes;
    }

    // ------------------------------------------------------- #535: cross-family type widening (read-only)

    [Fact]
    public async Task Append_CrossFamilyIntToDouble_WhenFeatureEnabled_IsRejectedBeforeAnyCommit()
    {
        // #535: int→double is Delta-`isTypeChangeSupported` (readable / applicable by an explicit Spark ALTER
        // TABLE CHANGE COLUMN TYPE) but is NOT eligible for automatic schema evolution
        // (`isTypeChangeSupportedForSchemaEvolution` excludes int→double and integral→decimal). DeltaSharp
        // applies widenings only on the append/reconcile (schema-evolution) path, so — matching Spark — a
        // cross-family widening on append is REJECTED fail-closed as TypeWideningUnsupported EVEN WITH the
        // feature fully enabled, and NO version is written. Cross-family is supported ONLY on READ (below).
        var initial = Struct(F("value", DataTypes.IntegerType, nullable: true));
        await WriteNarrowIntFileAsync("v0.parquet", new[] { 1, 2, 3 });
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.ProtocolWithReaderFeature("typeWidening"),
            DeltaTestHarness.MetadataWithSchemaAndConfig(initial, new[] { ("delta.enableTypeWidening", "true") }),
            DeltaTestHarness.Add("v0.parquet"));

        Snapshot readSnapshot = await LoadAsync();
        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(
            () => Writer().AppendAsync(
                readSnapshot,
                Struct(F("value", DataTypes.DoubleType, nullable: true)),
                new[] { Staged("v1.parquet") },
                SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        // No new version committed: the log tip is still v0.
        Assert.Equal(0L, (await LoadAsync()).Version);
    }

    [Fact]
    public async Task Append_CrossFamilyLongToDecimal_WhenFeatureEnabled_IsRejectedBeforeAnyCommit()
    {
        // #535: long→decimal(20,0) is likewise read-only — Delta-supported for read/ALTER TABLE but not
        // schema-evolution-eligible. Append rejects it fail-closed as TypeWideningUnsupported; no version.
        var wide = DataTypes.CreateDecimalType(20, 0);
        var initial = Struct(F("value", DataTypes.LongType, nullable: true));
        await WriteNarrowLongFileAsync("v0.parquet", new[] { 10L, -7L, long.MaxValue });
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.ProtocolWithReaderFeature("typeWidening"),
            DeltaTestHarness.MetadataWithSchemaAndConfig(initial, new[] { ("delta.enableTypeWidening", "true") }),
            DeltaTestHarness.Add("v0.parquet"));

        Snapshot readSnapshot = await LoadAsync();
        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(
            () => Writer().AppendAsync(
                readSnapshot,
                Struct(F("value", wide, nullable: true)),
                new[] { Staged("v1.parquet") },
                SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal(0L, (await LoadAsync()).Version);
    }

    [Fact]
    public async Task Read_CrossFamilyIntToDouble_PromotesNarrowFileUnderWidenedSchema()
    {
        // #535 READ path (the supported half): a Spark/delta-rs table that applied int→double via ALTER TABLE
        // has a current `double` schema (typeWidening feature) over an OLD narrow Int32 file. DeltaSharp reads
        // that unchanged Int32 file and PROMOTES it into the double lane — no data-file rewrite. This is the
        // interop capability #535 delivers; ParquetTypeWideningPromotionTests covers each cross-family pair at
        // the reader level, this pins the int→double end-to-end shape (narrow file, widened requested schema).
        byte[] narrow = await WriteNarrowIntFileAsync("v0.parquet", new[] { 1, 2, 3 });
        var widened = Struct(F("value", DataTypes.DoubleType, nullable: true));

        List<ColumnBatch> promoted = await ParquetTestHelpers.ReadAllAsync(
            narrow, widened, keepRowGroup: null, allowTypeWideningPromotion: true);

        Assert.Equal(new[] { 1d, 2d, 3d }, promoted.Single().Column(0).GetValues<double>().ToArray());
    }

    // ------------------------------------------------------- #534: enable type widening on an EXISTING table

    [Fact]
    public async Task EnableTypeWidening_OnPlainTable_UpgradesProtocolAndSetsProperty_MetadataOnly()
    {
        // A plain table (reader 1 / writer 2, no features, type widening NOT enabled) WITH an active data
        // file. #534: enabling type widening commits a METADATA-ONLY protocol + metaData upgrade — reader→3 /
        // writer→7 with `typeWidening` in BOTH feature lists and `delta.enableTypeWidening=true` — that adds
        // no data file and, critically, does NOT echo the table's existing active `add` into the upgrade
        // commit (seeding a v0 data file makes the zero-add/zero-remove assertions non-vacuous).
        var initial = Struct(F("value", DataTypes.IntegerType, nullable: true));
        await WriteNarrowIntFileAsync("v0.parquet", new[] { 1, 2, 3 });
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchema(initial),
            DeltaTestHarness.Add("v0.parquet"));

        DeltaCommitResult result = await Writer().EnableTypeWideningAsync();
        Assert.False(result.Skipped);
        Assert.Equal(1L, result.Version);

        Snapshot after = await LoadAsync();
        Assert.True(TypeWideningFeature.IsWriteEnabled(after));
        Assert.Equal(ProtocolSupport.TableFeaturesReaderVersion, after.Protocol.MinReaderVersion);
        Assert.Equal(ProtocolSupport.TableFeaturesWriterVersion, after.Protocol.MinWriterVersion);
        Assert.Contains(TypeWideningFeature.Feature, after.Protocol.ReaderFeatures);
        Assert.Contains(TypeWideningFeature.Feature, after.Protocol.WriterFeatures);
        Assert.Equal("true", after.Metadata.Configuration[TypeWideningFeature.EnablePropertyKey]);
        // The prior data file stays active (metadata-only upgrade never rewrites/removes data).
        Assert.Equal(new[] { "v0.parquet" }, ActivePaths(after));

        // Metadata-only: the upgrade commit (version 1) carries exactly one protocol + one metaData, and does
        // NOT echo the table's existing active `add` (v0, committed at version 0) — no add/remove at all.
        IReadOnlyList<DeltaAction> committed = await Log().ReadCommitActionsAsync(result.Version, CancellationToken.None);
        Assert.Single(committed.OfType<ProtocolAction>());
        Assert.Single(committed.OfType<MetadataAction>());
        Assert.Empty(committed.OfType<AddFileAction>());
        Assert.Empty(committed.OfType<RemoveFileAction>());
    }

    [Fact]
    public async Task EnableTypeWidening_ThenAppendWiderColumn_AppliesWidening()
    {
        // Enable-on-existing THEN widen: a plain int table with a data file, enable type widening, then append
        // a `long`-typed column — the same-family widening int→long is now APPLIED (merged schema carries
        // `long` + a delta.typeChanges entry), where before enablement it fails closed (see
        // Append_WidenIntToDouble_WhenFeatureDisabled and #495's disabled-path tests). The old int file is NOT
        // rewritten — it stays active and reads back promoted into the widened `long` type.
        var initial = Struct(F("value", DataTypes.IntegerType, nullable: true));
        byte[] oldFile = await WriteNarrowIntFileAsync("v0.parquet", new[] { 1, 2, 3 });
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchema(initial),
            DeltaTestHarness.Add("v0.parquet"));

        await Writer().EnableTypeWideningAsync();

        await Writer().AppendAsync(
            await LoadAsync(),
            Struct(F("value", DataTypes.LongType, nullable: true)),
            new[] { Staged("v1.parquet") },
            SchemaEvolutionMode.MergeSchema);

        Snapshot after = await LoadAsync();
        StructType evolved = after.Schema;
        StructField field = evolved["value"];
        Assert.Equal(DataTypes.LongType, field.DataType);
        Assert.True(field.Metadata.TryGetValue("delta.typeChanges", out _));
        // The old narrow int file is not rewritten — still active — and reads back promoted into `long`.
        Assert.Contains("v0.parquet", ActivePaths(after));
        List<ColumnBatch> promoted = await ParquetTestHelpers.ReadAllAsync(
            oldFile, evolved, keepRowGroup: null, allowTypeWideningPromotion: true);
        Assert.Equal(new[] { 1L, 2L, 3L }, promoted.Single().Column(0).GetValues<long>().ToArray());
    }

    [Fact]
    public async Task EnableTypeWidening_WhenAlreadyEnabled_IsIdempotentNoOp()
    {
        // A table that already declares the feature AND sets the property: EnableTypeWideningAsync writes NO
        // new version (idempotent no-op), reporting the current version with Skipped=true.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.ProtocolWithReaderFeature("typeWidening"),
            DeltaTestHarness.MetadataWithSchemaAndConfig(
                Struct(F("value", DataTypes.IntegerType, nullable: true)),
                new[] { ("delta.enableTypeWidening", "true") }));

        DeltaCommitResult result = await Writer().EnableTypeWideningAsync();

        Assert.True(result.Skipped);
        Assert.Equal(0L, result.Version);
        Assert.Equal(0L, (await LoadAsync()).Version); // no new version published
    }

    [Fact]
    public async Task EnableTypeWidening_PreservesExistingFeatures()
    {
        // Enabling type widening on a table that already declares ANOTHER feature (deletionVectors, reader 3 /
        // writer 7) keeps that feature and ADDS `typeWidening` to both lists — an existing feature is never
        // dropped by the upgrade.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.ProtocolWithReaderFeature("deletionVectors"),
            DeltaTestHarness.MetadataWithSchema(Struct(F("value", DataTypes.IntegerType, nullable: true))));

        await Writer().EnableTypeWideningAsync();

        Snapshot after = await LoadAsync();
        Assert.Contains("deletionVectors", after.Protocol.ReaderFeatures);
        Assert.Contains("deletionVectors", after.Protocol.WriterFeatures);
        Assert.Contains(TypeWideningFeature.Feature, after.Protocol.ReaderFeatures);
        Assert.Contains(TypeWideningFeature.Feature, after.Protocol.WriterFeatures);
        Assert.True(TypeWideningFeature.IsWriteEnabled(after));
    }

    [Fact]
    public async Task EnableTypeWidening_WhenFeaturePresentButPropertyUnset_SetsPropertyOnly()
    {
        // Partial state: a table declares the `typeWidening` feature (reader 3 / writer 7) but has NOT set
        // `delta.enableTypeWidening`. IsWriteEnabled is false (both are required), so EnableTypeWideningAsync
        // completes the enablement by setting the property; the protocol is already at the floor with the
        // feature present, so it converges to a supported+enabled table.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.ProtocolWithReaderFeature("typeWidening"),
            DeltaTestHarness.MetadataWithSchema(Struct(F("value", DataTypes.IntegerType, nullable: true))));

        DeltaCommitResult result = await Writer().EnableTypeWideningAsync();

        Assert.False(result.Skipped);
        Snapshot after = await LoadAsync();
        Assert.True(TypeWideningFeature.IsWriteEnabled(after));
        Assert.Equal("true", after.Metadata.Configuration[TypeWideningFeature.EnablePropertyKey]);
    }

    [Fact]
    public async Task EnableTypeWidening_WhenPropertySetButFeatureAbsent_AddsFeatureAndUpgrades()
    {
        // Malformed state: a legacy table (reader 1 / writer 2) has `delta.enableTypeWidening=true` in its
        // config but does NOT declare the feature. IsWriteEnabled is false (Supports() is false), so
        // EnableTypeWideningAsync adds the feature and upgrades the protocol — converging to a supported +
        // enabled table.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchemaAndConfig(
                Struct(F("value", DataTypes.IntegerType, nullable: true)),
                new[] { ("delta.enableTypeWidening", "true") }));

        DeltaCommitResult result = await Writer().EnableTypeWideningAsync();

        Assert.False(result.Skipped);
        Snapshot after = await LoadAsync();
        Assert.True(TypeWideningFeature.IsWriteEnabled(after));
        Assert.Contains(TypeWideningFeature.Feature, after.Protocol.WriterFeatures);
        Assert.Equal(ProtocolSupport.TableFeaturesWriterVersion, after.Protocol.MinWriterVersion);
    }

    [Theory]
    [InlineData("delta.constraints.ck", "id > 0")] // a CHECK constraint (legacy checkConstraints feature)
    public async Task EnableTypeWidening_OnLegacyTableWithActiveConstraint_EnumeratesTheFeature(
        string configKey, string configValue)
    {
        // #568: a legacy (writer < 7) table that declares an ACTIVE CHECK constraint (or column invariant) is
        // now upgraded to the table-features protocol with the feature ENUMERATED into writerFeatures (it is
        // enforced per row at the write seam, #581), rather than being refused fail-closed. A new version is
        // published carrying typeWidening + checkConstraints.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchemaAndConfig(
                Struct(F("value", DataTypes.IntegerType, nullable: true)),
                new[] { (configKey, configValue) }));

        await Writer().EnableTypeWideningAsync();

        Snapshot after = await LoadAsync();
        Assert.Equal(1L, after.Version); // upgrade committed
        Assert.True(TypeWideningFeature.IsWriteEnabled(after));
        Assert.Contains("checkConstraints", after.Protocol.WriterFeatures);
        Assert.DoesNotContain("checkConstraints", after.Protocol.ReaderFeatures);
        Assert.Equal(ProtocolSupport.TableFeaturesWriterVersion, after.Protocol.MinWriterVersion);
    }

    [Fact]
    public async Task EnableTypeWidening_OnLegacyAppendOnlyTable_UpgradesAndEnumeratesAppendOnly()
    {
        // #549: a legacy (writer 2) table with an ACTIVE delta.appendOnly=true is now upgradeable — the upgrade
        // enumerates the appendOnly writer feature (writer-only) so append-only stays ACTIVE across the
        // table-features upgrade (Delta "Active Features"), and delta.appendOnly=true is preserved in config.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchemaAndConfig(
                Struct(F("value", DataTypes.IntegerType, nullable: true)),
                new[] { ("delta.appendOnly", "true") }));

        DeltaCommitResult result = await Writer().EnableTypeWideningAsync();

        Assert.False(result.Skipped);
        Snapshot after = await LoadAsync();
        Assert.Equal(ProtocolSupport.TableFeaturesWriterVersion, after.Protocol.MinWriterVersion);
        Assert.Contains(TypeWideningFeature.Feature, after.Protocol.WriterFeatures);
        Assert.Contains(AppendOnlyFeature.Feature, after.Protocol.WriterFeatures);
        Assert.DoesNotContain(AppendOnlyFeature.Feature, after.Protocol.ReaderFeatures); // writer-only
        Assert.Equal("true", after.Metadata.Configuration[AppendOnlyFeature.PropertyKey]);
        Assert.True(TypeWideningFeature.IsWriteEnabled(after));
    }

    [Fact]
    public async Task EnableTypeWidening_OnLegacyAppendOnlyTable_EnforcementPersistsAfterUpgrade()
    {
        // After the upgrade, append-only enforcement is still active on the upgraded (writer 7) snapshot: a
        // data-changing remove (DELETE / OVERWRITE) against it is refused fail-closed.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchemaAndConfig(
                Struct(F("value", DataTypes.IntegerType, nullable: true)),
                new[] { ("delta.appendOnly", "true") }),
            DeltaTestHarness.Add("seed.parquet"));

        await Writer().EnableTypeWideningAsync();
        Snapshot upgraded = await LoadAsync();

        var remove = new RemoveFileAction(
            "seed.parquet", DeletionTimestamp: 1L, DataChange: true, ExtendedFileMetadata: false,
            NoPartition, Size: null);

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                upgraded, new DeltaAction[] { remove }, DeltaReadScope.WholeTable));

        Assert.Equal(DeltaProtocolErrorKind.AppendOnlyViolation, ex.Kind);
    }

    [Fact]
    public async Task Append_WidenIntToDouble_WhenFeatureDisabled_IsRejectedBeforeAnyCommit()
    {
        // AC3: with the feature DISABLED (no delta.enableTypeWidening), a cross-family widening is REJECTED
        // fail-closed as TypeWideningUnsupported and NO version is written.
        var initial = Struct(F("value", DataTypes.IntegerType, nullable: true));
        await WriteNarrowIntFileAsync("v0.parquet", new[] { 1, 2, 3 });
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchema(initial),
            DeltaTestHarness.Add("v0.parquet"));

        Snapshot readSnapshot = await LoadAsync();
        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(
            () => Writer().AppendAsync(
                readSnapshot,
                Struct(F("value", DataTypes.DoubleType, nullable: true)),
                new[] { Staged("v1.parquet") },
                SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        // No new version committed: the log tip is still v0.
        Snapshot after = await LoadAsync();
        Assert.Equal(0L, after.Version);
    }

    [Fact]
    public async Task Append_WidenIntToTooNarrowDecimal_WhenFeatureEnabled_IsRejectedAsIncompatible_NoCommit()
    {
        // AC5: int→decimal(9,0) cannot hold int's 10-digit range → NOT a sanctioned widening (would truncate).
        // Rejected fail-closed as IncompatibleType even with the feature enabled; no version is written.
        var initial = Struct(F("value", DataTypes.IntegerType, nullable: true));
        await WriteNarrowIntFileAsync("v0.parquet", new[] { 1 });
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.ProtocolWithReaderFeature("typeWidening"),
            DeltaTestHarness.MetadataWithSchemaAndConfig(initial, new[] { ("delta.enableTypeWidening", "true") }),
            DeltaTestHarness.Add("v0.parquet"));

        Snapshot readSnapshot = await LoadAsync();
        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(
            () => Writer().AppendAsync(
                readSnapshot,
                Struct(F("value", DataTypes.CreateDecimalType(9, 0), nullable: true)),
                new[] { Staged("v1.parquet") },
                SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
        Snapshot after = await LoadAsync();
        Assert.Equal(0L, after.Version);
    }

    [Fact]
    public async Task Append_WidenPartitionColumn_WhenFeatureEnabled_CommitsWidenedSchemaAndTypeChanges_KeepsPartitionColumnsLogical()
    {
        // #537: seed a typeWidening-ENABLED, PARTITIONED table whose partition column `part` is int and whose
        // v0 add records the partition value as the canonical STRING "5" (Delta stores partition values as
        // strings in the log / directory names, never in the Parquet data file). Widening the partition
        // column int→long is metadata-only: NO data file is rewritten — the widening is applied purely by
        // emitting delta.typeChanges + re-interpreting the (unchanged) partition string under the wide type.
        var initial = Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("part", DataTypes.IntegerType, nullable: true));
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.ProtocolWithReaderFeature("typeWidening"),
            DeltaTestHarness.MetadataWithSchemaAndConfig(
                initial, new[] { ("delta.enableTypeWidening", "true") }, partitionColumns: new[] { "part" }),
            DeltaTestHarness.Add("v0.parquet", partitionValues: new[] { ("part", "5") }));

        // A widening append (int → long on the PARTITION column) is APPLIED: it commits the widened metaData
        // atomically with the new add, in one version — exactly like a non-partition widening (#495).
        Snapshot readSnapshot = await LoadAsync();
        DeltaCommitResult result = await Writer().AppendAsync(
            readSnapshot,
            Struct(F("id", DataTypes.LongType, nullable: false), F("part", DataTypes.LongType, nullable: true)),
            new[] { StagedPartitioned("v1.parquet", ("part", "10")) },
            SchemaEvolutionMode.MergeSchema);

        IReadOnlyList<DeltaAction> committed = await Log().ReadCommitActionsAsync(result.Version, CancellationToken.None);
        MetadataAction meta = Assert.Single(committed.OfType<MetadataAction>());

        // GOLDEN SHAPE 1: metaData.partitionColumns keeps the LOGICAL name, unchanged by the widening.
        Assert.Equal(new[] { "part" }, meta.PartitionColumns);

        // GOLDEN SHAPE 2: the committed schema's partition field widened to `long` and carries a
        // delta.typeChanges {fromType:"integer",toType:"long"} on the partition StructField.
        Snapshot evolved = await LoadAsync();
        StructField part = evolved.Schema["part"];
        Assert.Equal(DataTypes.LongType, part.DataType);
        Assert.True(part.Metadata.TryGetValue("delta.typeChanges", out MetadataValue? changes));
        Assert.True(changes!.TryGetArray(out IReadOnlyList<MetadataValue>? entries));
        MetadataValue only = Assert.Single(entries!);
        Assert.True(only.TryGetNested(out FieldMetadata? nested));
        Assert.True(nested!.TryGetString("fromType", out string? from));
        Assert.True(nested.TryGetString("toType", out string? to));
        Assert.Equal("integer", from);
        Assert.Equal("long", to);

        // GOLDEN SHAPE 3: NO data file was rewritten — the OLD v0 add is still active and its
        // add.partitionValues string is UNCHANGED ("5", physical/logical identical, un-mapped). The read door
        // const-fills that string under the widened `long` type at read time; the bytes on disk are untouched.
        AddFileAction v0 = Assert.Single(evolved.ActiveFiles.Where(a => a.Path == "v0.parquet"));
        Assert.Equal("5", v0.PartitionValues["part"]);
    }

    [Fact]
    public async Task Append_WidenPartitionColumn_WithoutFeatureEnabled_IsRejectedBeforeAnyCommit()
    {
        // Feature-disabled fails closed: the SAME int→long partition widening that applies when enabled stays
        // rejected (TypeWideningUnsupported) when the table has NOT enabled type widening, and no version is
        // published (the table's latest version is unchanged).
        var initial = Struct(
            F("id", DataTypes.LongType, nullable: false),
            F("part", DataTypes.IntegerType, nullable: true));
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchema(initial, partitionColumns: new[] { "part" }),
            DeltaTestHarness.Add("v0.parquet", partitionValues: new[] { ("part", "5") }));

        Snapshot readSnapshot = await LoadAsync();
        DeltaSchemaMismatchException ex = await Assert.ThrowsAsync<DeltaSchemaMismatchException>(
            () => Writer().AppendAsync(
                readSnapshot,
                Struct(F("id", DataTypes.LongType, nullable: false), F("part", DataTypes.LongType, nullable: true)),
                new[] { StagedPartitioned("v1.parquet", ("part", "10")) },
                SchemaEvolutionMode.MergeSchema));

        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, ex.Kind);
        Assert.Equal(0L, (await LoadAsync()).Version);
    }
}
