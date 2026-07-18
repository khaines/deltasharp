using System.Collections.Immutable;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta.DeletionVectors;

/// <summary>
/// WRITE-path column-mapping correctness for merge-on-read DELETE (#529 name / #572 id). A DELETE against a
/// column-mapped table must:
/// <list type="bullet">
/// <item>read the physically-named Parquet data — by physical name in <c>name</c> mode, by the Parquet
/// <c>field_id</c> in <c>id</c> mode (#572) — and relabel it to the LOGICAL schema so the predicate sees
/// LOGICAL column names/values (proven by deleting on a NON-first logical column whose physical name
/// differs — a relabel/ordinal bug would delete the wrong rows and fail the by-value survivor assertion;
/// the id-mode field_id resolution is PINNED against a FOREIGN table whose physical column names/order do
/// NOT match the metaData, so name/positional resolution cannot pass by coincidence);</item>
/// <item>emit a deletion vector that is POSITIONAL over the PHYSICAL data file (column mapping never moves a
/// row's physical position), so a read-back through <see cref="DeltaReadSource"/> excludes EXACTLY the
/// predicate rows;</item>
/// <item>resolve partition values const/null-filled by PHYSICAL name, fail closed on an id-mode table that
/// has NOT enabled deletion vectors, and leave <c>none</c> mode unchanged (regression).</item>
/// </list>
/// Serialized on the shared filesystem collection so the safety assertions never flake.
/// </summary>
[Collection(DeletionVectorFileTestCollection.Name)]
public sealed class DeltaDeleteColumnMappingTests : IDisposable
{
    private const string Seed = "issue-529-colmapped-delete";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dv-colmap-" + Guid.NewGuid().ToString("N"));

    // A flat schema with a non-null key (id), a DISTINCTLY-valued nullable long (score) that is NOT the first
    // column, and a nullable string (name). Deleting on `score` (logical index 1, whose physical name differs
    // under name mode) exercises the physical->logical relabel: a mis-mapped ordinal would evaluate the
    // predicate against the wrong physical column and delete the wrong rows.
    private static readonly StructType FlatSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("score", DataTypes.LongType, nullable: true),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    // A partitioned schema: `region` is the partition column (stored under a PHYSICAL name in
    // add.partitionValues, resolved const/null-filled by the DELETE), `id` and `val` live in the Parquet file.
    private static readonly StructType PartitionedSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("region", DataTypes.StringType, nullable: true),
        new StructField("val", DataTypes.LongType, nullable: true),
    });

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

    // ------------------------------------------------------------------ name mode: exact-row exclusion

    [Fact]
    public async Task Delete_NameMode_OnLogicalColumn_ExcludesExactlyPredicateRows()
    {
        await CreateNameMappedDeletionVectorTableAsync(
            FlatSchema,
            Array.Empty<string>(),
            FlatBatch((1, 100, "a"), (2, 200, "b"), (3, 300, "c"), (4, 200, "d"), (5, 500, "e")));

        int parquetBefore = CountFiles("*.parquet");

        var delete = NewDelete("name-mode-logical-predicate");

        // Predicate over the LOGICAL `score` column (index 1) — the DELETE must relabel the physical Parquet
        // column to this logical position before evaluating the predicate.
        DeleteResult result = await delete.DeleteAsync(WhereScore(score => score == 200));

        Assert.NotNull(result.CommittedVersion);
        Assert.Equal(2, result.RowsDeleted);
        Assert.Equal(1, result.FilesWithDeletionVector);
        Assert.Equal(0, result.FilesFullyDeleted);

        // Merge-on-read: the physically-named data file is NOT rewritten; a positional DV .bin is added.
        Assert.Equal(parquetBefore, CountFiles("*.parquet"));
        Assert.True(CountFiles("*.bin") >= 1, "DELETE must have written a deletion-vector .bin file.");

        // EXACTLY rows with score==200 (ids 2 and 4) are excluded — asserted by LOGICAL column VALUES, so a
        // "wrong relabel / DV ignored" mutant that deletes the wrong rows fails here.
        List<(long, long?, string?)> survivors = await ReadLatestFlatAsync();
        Assert.Equal(
            new (long, long?, string?)[] { (1L, 100L, "a"), (3L, 300L, "c"), (5L, 500L, "e") },
            survivors.OrderBy(r => r.Item1).ToList());
    }

    [Fact]
    public async Task Delete_NameMode_OnKeyColumn_ExcludesExactlyPredicateRows()
    {
        await CreateNameMappedDeletionVectorTableAsync(
            FlatSchema,
            Array.Empty<string>(),
            FlatBatch((10, 1, "j"), (20, 2, "k"), (30, 3, "l"), (40, 4, "m"), (50, 5, "n")));

        var delete = NewDelete("name-mode-key-predicate");
        DeleteResult result = await delete.DeleteAsync(WhereId(id => id == 20 || id == 50));

        Assert.Equal(2, result.RowsDeleted);

        List<(long, long?, string?)> survivors = await ReadLatestFlatAsync();
        Assert.Equal(
            new (long, long?, string?)[] { (10L, 1L, "j"), (30L, 3L, "l"), (40L, 4L, "m") },
            survivors.OrderBy(r => r.Item1).ToList());
    }

    // ------------------------------------------------------------------ name mode + partitioning

    [Fact]
    public async Task Delete_NameMode_Partitioned_ExcludesExactlyPredicateRows()
    {
        // Two partitions (region=us / eu); `region` is resolved from add.partitionValues by PHYSICAL name.
        await CreateNameMappedDeletionVectorTableAsync(
            PartitionedSchema,
            new[] { "region" },
            PartitionedBatch((1, "us", 100), (2, "us", 200), (3, "eu", 300), (4, "eu", 400)));

        var delete = NewDelete("name-mode-partitioned");

        // Delete a row in each partition by the LOGICAL `val` column; the partition value must survive the
        // relabel so the read-back reports the correct region for each survivor.
        DeleteResult result = await delete.DeleteAsync(WhereVal(v => v == 200 || v == 300));

        Assert.NotNull(result.CommittedVersion);
        Assert.Equal(2, result.RowsDeleted);

        List<(long, string?, long?)> survivors = await ReadLatestPartitionedAsync();
        Assert.Equal(
            new (long, string?, long?)[] { (1L, "us", 100L), (4L, "eu", 400L) },
            survivors.OrderBy(r => r.Item1).ToList());
    }

    // ------------------------------------------------------------------ name mode: whole-file delete

    [Fact]
    public async Task Delete_NameMode_EveryRowInFile_RemovesFileOutright()
    {
        await CreateNameMappedDeletionVectorTableAsync(
            FlatSchema, Array.Empty<string>(), FlatBatch((1, 10, "a"), (2, 20, "b"), (3, 30, "c")));

        var delete = NewDelete("name-mode-whole-file");
        DeleteResult result = await delete.DeleteAsync(WhereId(_ => true));

        Assert.Equal(3, result.RowsDeleted);
        Assert.Equal(1, result.FilesFullyDeleted);
        Assert.Equal(0, result.FilesWithDeletionVector);
        Assert.Empty(await ReadLatestFlatAsync());
    }

    // ------------------------------------------------------------------ id mode: field-id-resolved DELETE (#572)

    [Fact]
    public async Task Delete_IdMode_OnLogicalColumn_ExcludesExactlyPredicateRows()
    {
        // #572: id-mode DELETE mirrors name mode — the DELETE reads the physical Parquet by FIELD_ID (not by
        // physical name), relabels to the LOGICAL schema so the predicate sees logical columns/values, and
        // emits a POSITIONAL deletion vector over the physical file. Deleting on the non-first logical `score`
        // column proves the field-id relabel: a mis-resolved ordinal/field_id would delete the wrong rows and
        // fail the by-value survivor assertion.
        await CreateIdMappedDeletionVectorTableAsync(
            FlatSchema,
            Array.Empty<string>(),
            FlatBatch((1, 100, "a"), (2, 200, "b"), (3, 300, "c"), (4, 200, "d"), (5, 500, "e")));

        int parquetBefore = CountFiles("*.parquet");

        var delete = NewDelete("id-mode-logical-predicate");
        DeleteResult result = await delete.DeleteAsync(WhereScore(score => score == 200));

        Assert.NotNull(result.CommittedVersion);
        Assert.Equal(2, result.RowsDeleted);
        Assert.Equal(1, result.FilesWithDeletionVector);
        Assert.Equal(0, result.FilesFullyDeleted);

        // Merge-on-read: the physically-named data file is NOT rewritten; a positional DV .bin is added.
        Assert.Equal(parquetBefore, CountFiles("*.parquet"));
        Assert.True(CountFiles("*.bin") >= 1, "DELETE must have written a deletion-vector .bin file.");

        // EXACTLY rows with score==200 (ids 2 and 4) are excluded — asserted by LOGICAL column VALUES.
        List<(long, long?, string?)> survivors = await ReadLatestFlatAsync();
        Assert.Equal(
            new (long, long?, string?)[] { (1L, 100L, "a"), (3L, 300L, "c"), (5L, 500L, "e") },
            survivors.OrderBy(r => r.Item1).ToList());
    }

    [Fact]
    public async Task Delete_IdMode_Partitioned_ExcludesExactlyPredicateRows()
    {
        // id-mode partitioned DELETE: the in-file DATA columns resolve by field_id while `region` is resolved
        // const/null-filled from add.partitionValues by its PHYSICAL name (the partition value must survive
        // the relabel so each survivor reports the correct region).
        await CreateIdMappedDeletionVectorTableAsync(
            PartitionedSchema,
            new[] { "region" },
            PartitionedBatch((1, "us", 100), (2, "us", 200), (3, "eu", 300), (4, "eu", 400)));

        var delete = NewDelete("id-mode-partitioned");
        DeleteResult result = await delete.DeleteAsync(WhereVal(v => v == 200 || v == 300));

        Assert.NotNull(result.CommittedVersion);
        Assert.Equal(2, result.RowsDeleted);

        List<(long, string?, long?)> survivors = await ReadLatestPartitionedAsync();
        Assert.Equal(
            new (long, string?, long?)[] { (1L, "us", 100L), (4L, "eu", 400L) },
            survivors.OrderBy(r => r.Item1).ToList());
    }

    // -------------------------------------------- id mode: field_id resolution PINNED via a FOREIGN table (#572)

    [Fact]
    public async Task Delete_IdMode_ByFieldId_ForeignTable_IgnoringPhysicalNameAndPosition()
    {
        // B1 (council quality F1 / relspec M6): the DELETE sibling of the READ test
        // IdMode_ReadsByFieldId_IgnoringPhysicalNameAndPosition — it PINS that id-mode DELETE resolves DATA
        // columns by the Parquet FIELD_ID, never by physical name and never by position. Every DeltaSharp-
        // authored id-mode table names its physical Parquet columns col-<uuid> == the metaData physicalName,
        // so name-resolution and field_id-resolution are indistinguishable there (flipping resolveByFieldId to
        // false survives ALL such tests). This FOREIGN table breaks that coincidence: the physical Parquet
        // columns are named z0/z1/z2 (which do NOT match the metaData physicalNames col-A/col-B/col-C) and are
        // stored in a DIFFERENT order than the logical schema, but each carries the correct footer field_id.
        // `score` (field_id 2) sits at a NON-first physical position (physical index 2, logical index 1). A
        // name-based read cannot find col-B; a positional read would target `name`, not `score`. Only field_id
        // resolution deletes the right rows — so flipping DeltaDelete's resolveByFieldId to false turns this
        // RED ("Requested column 'col-…' is not present in the Parquet file schema", or wrong survivors).
        await SeedForeignIdModeDeletionVectorTableAsync();

        int parquetBefore = CountFiles("*.parquet");

        var delete = NewDelete("id-mode-foreign-fieldid");
        DeleteResult result = await delete.DeleteAsync(WhereScore(score => score == 200));

        Assert.NotNull(result.CommittedVersion);
        Assert.Equal(2, result.RowsDeleted);            // ids 2 and 4 (score==200)
        Assert.Equal(1, result.FilesWithDeletionVector);
        Assert.Equal(0, result.FilesFullyDeleted);

        // Merge-on-read: the physically-named data file is NOT rewritten; a positional DV .bin is added.
        Assert.Equal(parquetBefore, CountFiles("*.parquet"));
        Assert.True(CountFiles("*.bin") >= 1, "DELETE must have written a deletion-vector .bin file.");

        // EXACTLY the score==200 rows (ids 2, 4) are excluded — asserted by LOGICAL column VALUES, so a wrong
        // field_id / positional resolution that deleted the wrong rows fails here.
        List<(long, long?, string?)> survivors = await ReadLatestFlatAsync();
        Assert.Equal(
            new (long, long?, string?)[] { (1L, 100L, "a"), (3L, 300L, "c"), (5L, 500L, "e") },
            survivors.OrderBy(r => r.Item1).ToList());
    }

    // -------------------------------------------- id mode: DELETE without deletion vectors fails closed (#572)

    [Fact]
    public async Task Delete_IdMode_WithoutDeletionVectorsEnabled_FailsClosed()
    {
        // M3 (council hygiene): an in-filter killer for the DV protocol gate under id mode. A merge-on-read
        // DELETE against an id-mode table that has NOT enabled deletion vectors must fail closed at
        // DeletionVectorsFeature.EnsureWriteEnabled (the table's protocol declares columnMapping but NOT
        // deletionVectors), NEVER silently upgrade the protocol or drop the delete. (The existing killer for
        // this gate lives OUTSIDE this PR's ~ColumnMapping|~IdMode|~DeltaDeleteColumnMapping test filter; this
        // id-mode-flavored sibling sits INSIDE it.)
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateIdMappedTableAsync(
                FlatSchema,
                Array.Empty<string>(),
                new[] { FlatBatch((1, 100, "a"), (2, 200, "b"), (3, 300, "c")) },
                new SeededPhysicalNameSource(Seed));
        }

        var delete = NewDelete("id-mode-no-dv");
        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => delete.DeleteAsync(WhereScore(score => score == 200)));
        Assert.Contains("deletionVectors", ex.Message, StringComparison.Ordinal);

        // Fail-closed left the table unchanged (no new version; all three rows intact).
        Assert.Equal(3, (await ReadLatestFlatAsync()).Count);
    }

    [Theory]
    [InlineData("struct")]
    [InlineData("array")]
    [InlineData("map")]
    public void ResolvePhysicalNames_NameMode_RejectsNestedTopLevelColumn(string kind)
    {
        // Security/QueryExec nit (#529): the DELETE (and read) path resolves physical names through the shared
        // ColumnMappingProjection, which fails closed on a nested (struct/array/map) top-level column under
        // name mapping — nested column mapping is unsupported in this build — rather than risk mis-associating
        // columns and deleting the wrong rows. Pin that guard directly for each nested kind.
        DataType nested = kind switch
        {
            "struct" => new StructType(new[] { new StructField("x", DataTypes.LongType, nullable: true) }),
            "array" => new ArrayType(DataTypes.LongType),
            _ => new MapType(DataTypes.StringType, DataTypes.LongType),
        };
        // Give BOTH fields valid name-mode physicalName metadata so the ONLY reason ResolvePhysicalNames can
        // throw is the nested-type guard: if that guard were removed, PhysicalName would resolve every field
        // successfully (no incidental "missing physicalName" throw), so this oracle stays load-bearing even if
        // the message assertions below were ever loosened. `payload` is FIRST so the nested check fires before
        // any PhysicalName call.
        var schema = new StructType(new[]
        {
            NameMapped("payload", nested, nullable: true),
            NameMapped("id", DataTypes.LongType, nullable: false),
        });

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMappingProjection.ResolvePhysicalNames(schema, ColumnMappingMode.Name));

        Assert.Contains("nested", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("payload", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePhysicalNames_NoneMode_AllowsNestedTopLevelColumn()
    {
        // The nested-column rejection is NAME-mode specific: in none mode physical == logical (no relabel), so
        // a nested column is NOT rejected here — it is served exactly as before column mapping. Guards against
        // over-broad rejection that would regress plain (non-column-mapped) nested-schema tables.
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField(
                "payload", new StructType(new[] { new StructField("x", DataTypes.LongType, nullable: true) }),
                nullable: true),
        });

        string[] names = ColumnMappingProjection.ResolvePhysicalNames(schema, ColumnMappingMode.None);

        Assert.Equal(new[] { "id", "payload" }, names);
    }

    [Fact]
    public void BuildDataSchema_CarriesFieldMetadataThroughToRewriteSchema_Issue545()
    {
        // #545 fold regression: the shared ColumnMappingProjection.BuildDataSchema feeds three consumers — the
        // DeltaReadSource scan and the merge-on-read DELETE predicate projection (both read-only; a DELETE
        // writes a deletion vector, NOT a rewritten data file), and OPTIMIZE compaction, which re-serializes
        // this schema into the compacted data file's footer schema JSON (org.apache.spark.sql.parquet.row.metadata).
        // The seam must carry each retained field's Metadata through — only the NAME is relabeled to physical —
        // otherwise OPTIMIZE would emit a metadata-stripped footer for a None-mode table with column comments /
        // generated-column config, silently losing self-describing fidelity vs. the source files (the divergence
        // that made the OPTIMIZE fold NOT behavior-identical before this fix). Reconstruction drops Metadata by
        // default (3-arg ctor), so this is the load-bearing oracle.
        FieldMetadata comment = FieldMetadata.FromEntries(
            new[] { new KeyValuePair<string, string>("comment", "the primary key") });
        var schema = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false, comment),
            new StructField("value", DataTypes.StringType, nullable: true),
        });
        string[] physicalNames = ColumnMappingProjection.ResolvePhysicalNames(schema, ColumnMappingMode.None);

        // Unpartitioned: every field retained; the metadata-bearing field keeps its metadata (not FieldMetadata.Empty).
        StructType unpartitioned = ColumnMappingProjection.BuildDataSchema(
            schema, physicalNames, ImmutableArray<string>.Empty);
        Assert.Equal(3, unpartitioned.Count);
        Assert.Equal(comment, unpartitioned["id"].Metadata);
        Assert.False(unpartitioned["id"].Metadata.IsEmpty);

        // Partitioned (exclude "region"): the surviving metadata-bearing data field still carries its metadata.
        StructType partitioned = ColumnMappingProjection.BuildDataSchema(
            schema, physicalNames, ImmutableArray.Create("region"));
        Assert.Equal(new[] { "id", "value" }, new[] { partitioned[0].Name, partitioned[1].Name });
        Assert.Equal(comment, partitioned["id"].Metadata);
    }

    // ------------------------------------------------------------------ none mode: regression (unchanged)

    [Fact]
    public async Task Delete_NoneMode_ExcludesExactlyPredicateRows_Regression()
    {
        // The same new code paths (physicalNames == logical in none mode) must not regress a plain table.
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateDeletionVectorTableAsync(
                FlatSchema,
                Array.Empty<string>(),
                new[] { FlatBatch((1, 100, "a"), (2, 200, "b"), (3, 300, "c")) });
        }

        var delete = NewDelete("none-mode-regression");
        DeleteResult result = await delete.DeleteAsync(WhereScore(score => score == 200));

        Assert.Equal(1, result.RowsDeleted);
        Assert.Equal(
            new (long, long?, string?)[] { (1L, 100L, "a"), (3L, 300L, "c") },
            (await ReadLatestFlatAsync()).OrderBy(r => r.Item1).ToList());
    }

    // ------------------------------------------------------------------ helpers

    private DeltaDelete NewDelete(string idSeed)
    {
        var backend = new LocalFileSystemBackend(_root);
        return new DeltaDelete(
            backend, new DeltaLog(backend), new DeltaCommitter(backend),
            idSource: new SeededDeletionVectorIdSource(idSeed));
    }

    private async Task CreateNameMappedDeletionVectorTableAsync(
        StructType schema, IReadOnlyList<string> partitionColumns, params ColumnBatch[] batches)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        await target.CreateNameMappedDeletionVectorTableAsync(
            schema, partitionColumns, batches, new SeededPhysicalNameSource(Seed));
    }

    // #572: the id-mode sibling of CreateNameMappedDeletionVectorTableAsync — creates a fresh id-mode table
    // with deletion vectors enabled (data files carry PHYSICAL names + stamped field_ids), so a subsequent
    // DELETE exercises the id-mode field_id-resolved WRITE path.
    private async Task CreateIdMappedDeletionVectorTableAsync(
        StructType schema, IReadOnlyList<string> partitionColumns, params ColumnBatch[] batches)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        await target.CreateIdMappedDeletionVectorTableAsync(
            schema, partitionColumns, batches, new SeededPhysicalNameSource(Seed));
    }

    // Authors a FOREIGN id-mode + deletionVectors-enabled table by hand (version-0 _delta_log + a physical
    // Parquet data file) whose physical Parquet column NAMES (z0/z1/z2) do NOT match the metaData
    // physicalNames (col-A/col-B/col-C) and are stored in a DIFFERENT order than the logical schema, but each
    // carries the correct footer field_id — the shape a foreign engine produces (DeltaSharp always names a
    // physical column == its physicalName, which is why only a foreign table can distinguish field_id
    // resolution from name/positional resolution). Logical schema id(id 1)/score(id 2)/name(id 3); physical
    // layout [z0=id(field_id 1), z1=name(field_id 3), z2=score(field_id 2)], so `score` (field_id 2) is at a
    // NON-first physical position (physical index 2, logical index 1). Five rows, score==200 on ids 2 and 4.
    private async Task SeedForeignIdModeDeletionVectorTableAsync()
    {
        var physicalSchema = new StructType(new[]
        {
            PhysFieldWithId("z0", DataTypes.LongType, nullable: false, id: 1),    // logical "id"
            PhysFieldWithId("z1", DataTypes.StringType, nullable: true, id: 3),   // logical "name"
            PhysFieldWithId("z2", DataTypes.LongType, nullable: true, id: 2),     // logical "score"
        });

        (long Id, string Name, long Score)[] rows =
        {
            (1, "a", 100), (2, "b", 200), (3, "c", 300), (4, "d", 200), (5, "e", 500),
        };
        MutableColumnVector z0 = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector z1 = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        MutableColumnVector z2 = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        foreach ((long id, string name, long score) in rows)
        {
            z0.AppendValue(id);
            z1.AppendBytes(Encoding.UTF8.GetBytes(name));
            z2.AppendValue(score);
        }

        var batch = new ManagedColumnBatch(physicalSchema, new ColumnVector[] { z0, z1, z2 }, rows.Length);

        byte[] parquetBytes;
        using (var buffer = new MemoryStream())
        {
            await new ParquetFileWriter().WriteAsync(
                buffer, physicalSchema, new[] { batch }, CancellationToken.None);
            parquetBytes = buffer.ToArray();
        }

        const string relativePath = "foreign-idmode.parquet";
        using var backend = new LocalFileSystemBackend(_root);
        await backend.PutIfAbsentAsync(relativePath, parquetBytes, CancellationToken.None);

        // Logical id/score/name with ids 1/2/3 and physicalNames col-A/col-B/col-C — names that do NOT match
        // the Parquet z0/z1/z2, so a name-based read cannot resolve them; only field_id works.
        const string schemaJson =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
            + "{\"name\":\"score\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"col-B\"}},"
            + "{\"name\":\"name\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":3,\"delta.columnMapping.physicalName\":\"col-C\"}}]}";
        string escapedSchema = System.Text.Json.JsonSerializer.Serialize(schemaJson);

        // reader v3 / writer v7 declaring BOTH columnMapping AND deletionVectors; configuration enables id
        // mode + deletion vectors so DeltaDelete passes DeletionVectorsFeature.EnsureWriteEnabled.
        const string protocol =
            "{\"protocol\":{\"minReaderVersion\":3,\"minWriterVersion\":7,"
            + "\"readerFeatures\":[\"columnMapping\",\"deletionVectors\"],"
            + "\"writerFeatures\":[\"columnMapping\",\"deletionVectors\"]}}";
        string metadata =
            "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
            + "\"schemaString\":" + escapedSchema + ",\"partitionColumns\":[],\"configuration\":{"
            + "\"delta.columnMapping.mode\":\"id\",\"delta.columnMapping.maxColumnId\":\"3\","
            + "\"delta.enableDeletionVectors\":\"true\"}}}";
        string addLine =
            $"{{\"add\":{{\"path\":\"{relativePath}\",\"partitionValues\":{{}},"
            + $"\"size\":{parquetBytes.Length},\"modificationTime\":0,\"dataChange\":true}}}}";

        byte[] commit = Encoding.UTF8.GetBytes(protocol + "\n" + metadata + "\n" + addLine + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", commit, CancellationToken.None);
    }

    // A physical StructField carrying a delta.columnMapping.id (as a long MetadataValue) — the shape the
    // id-mode writer stamps into the Parquet field_id, used to author a foreign id-mode data file by hand.
    private static StructField PhysFieldWithId(string name, DataType type, bool nullable, long id) =>
        new(name, type, nullable, FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(id)),
        }));

    // A leaf field carrying valid name-mode column-mapping metadata (delta.columnMapping.physicalName), so
    // ColumnMapping.PhysicalName(field, Name) resolves it without throwing — used to isolate the nested-type
    // reject as the sole failure cause.
    private static StructField NameMapped(string name, DataType type, bool nullable) =>
        new(name, type, nullable, FieldMetadata.FromEntries(
            new[] { new KeyValuePair<string, string>("delta.columnMapping.physicalName", "col-" + name) }));

    private static DeltaDeletePredicate WhereId(Func<long, bool> match) =>
        DeltaDeletePredicate.FromRowPredicate((batch, row) => match(batch.SelectedColumn(0).GetValue<long>(row)));

    private static DeltaDeletePredicate WhereScore(Func<long, bool> match) =>
        DeltaDeletePredicate.FromRowPredicate((batch, row) =>
            !batch.SelectedColumn(1).IsNull(row) && match(batch.SelectedColumn(1).GetValue<long>(row)));

    private static DeltaDeletePredicate WhereVal(Func<long, bool> match) =>
        DeltaDeletePredicate.FromRowPredicate((batch, row) =>
            !batch.SelectedColumn(2).IsNull(row) && match(batch.SelectedColumn(2).GetValue<long>(row)));

    private async Task<List<(long, long?, string?)>> ReadLatestFlatAsync()
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        var rows = new List<(long, long?, string?)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
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

    private async Task<List<(long, string?, long?)>> ReadLatestPartitionedAsync()
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        var rows = new List<(long, string?, long?)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector region = batch.SelectedColumn(1);
            ColumnVector val = batch.SelectedColumn(2);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((
                    id.GetValue<long>(r),
                    region.IsNull(r) ? null : Encoding.UTF8.GetString(region.GetBytes(r)),
                    val.IsNull(r) ? null : val.GetValue<long>(r)));
            }
        }

        return rows;
    }

    private int CountFiles(string pattern) =>
        Directory.Exists(_root)
            ? Directory.GetFiles(_root, pattern, SearchOption.AllDirectories).Length
            : 0;

    private static ColumnBatch FlatBatch(params (long Id, long Score, string? Name)[] rows)
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

    private static ColumnBatch PartitionedBatch(params (long Id, string Region, long Val)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        MutableColumnVector val = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        foreach ((long i, string reg, long v) in rows)
        {
            id.AppendValue(i);
            region.AppendBytes(Encoding.UTF8.GetBytes(reg));
            val.AppendValue(v);
        }

        return new ManagedColumnBatch(PartitionedSchema, new ColumnVector[] { id, region, val }, rows.Length);
    }
}
