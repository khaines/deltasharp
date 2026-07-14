using System.Collections.Immutable;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta.DeletionVectors;

/// <summary>
/// WRITE-path column-mapping correctness for merge-on-read DELETE (#529). A DELETE against a column-mapped
/// <c>name</c>-mode table must:
/// <list type="bullet">
/// <item>read the physically-named Parquet data and relabel it to the LOGICAL schema so the predicate sees
/// LOGICAL column names/values (proven by deleting on a NON-first logical column whose physical name
/// differs — a relabel/ordinal bug would delete the wrong rows and fail the by-value survivor assertion);</item>
/// <item>emit a deletion vector that is POSITIONAL over the PHYSICAL data file (column mapping never moves a
/// row's physical position), so a read-back through <see cref="DeltaReadSource"/> excludes EXACTLY the
/// predicate rows;</item>
/// <item>stay fail-closed for <c>id</c> mode (#523) and leave <c>none</c> mode unchanged (regression).</item>
/// </list>
/// Serialized on the shared filesystem collection so the fail-closed safety assertion never flakes.
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

    // ------------------------------------------------------------------ id mode: fail closed (#523)

    [Fact]
    public async Task Delete_IdMode_FailsClosed_DeferredTo523()
    {
        // END-TO-END fail-closed: a raw id-mode table declaring the deletionVectors + columnMapping features.
        // A DELETE via the public path loads the snapshot through DeltaLog, whose column-mapping gate
        // (ColumnMapping.EnsureModeGate -> EnsureReadWriteSupported) is the PRIMARY choke point and rejects
        // id mode at LOAD, before any file is scanned or DV written (id mode resolves columns by Parquet
        // field_id — not implemented; a wrong relabel would delete the wrong rows). The DELETE-local guard is
        // pinned separately/independently by Delete_IdMode_FailsClosed_AtDeleteLocalGuard_Independent.
        await WriteRawIdModeTableAsync();

        var delete = NewDelete("id-mode-fail-closed");

        await Assert.ThrowsAsync<DeltaProtocolException>(
            () => delete.DeleteAsync(WhereId(id => id == 1)));

        // The table is untouched: no DV .bin was written.
        Assert.Equal(0, CountFiles("*.bin"));
    }

    [Fact]
    public async Task Delete_IdMode_FailsClosed_AtDeleteLocalGuard_Independent()
    {
        // Reliability oracle (#529): the DELETE-local fail-closed guard
        // (ColumnMapping.EnsureReadWriteSupported in DeltaDelete.RunDeleteAsync) is DEFENSE-IN-DEPTH,
        // secondary to the primary snapshot-load gate that Delete_IdMode_FailsClosed_DeferredTo523 exercises.
        // Pin it INDEPENDENTLY: hand-build an id-mode snapshot that never passed through DeltaLog's
        // EnsureModeGate, feed it to the internal read-snapshot seam, and require the DELETE to STILL fail
        // closed at its own guard (never fall through to a field-id-blind name/positional read that could
        // delete the wrong rows). If the DELETE-local guard were removed, this DELETE would proceed (empty
        // active files -> silent no-op) and NOT throw — so this test makes that guard independently
        // load-bearing.
        Snapshot idModeSnapshot = BuildUngatedColumnMappingSnapshot(
            mode: "id", schemaString: "{\"type\":\"struct\",\"fields\":[]}");

        var delete = NewDelete("id-mode-local-guard");

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => delete.DeleteAsync(idModeSnapshot, WhereId(id => id == 1)));

        Assert.Contains("column mapping mode 'id'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, CountFiles("*.bin"));
    }

    // ----------------------------------------------------- name mode: nested top-level column fails closed

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
        var schema = new StructType(new[]
        {
            new StructField("payload", nested, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
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

    // Writes a raw version-0 log for an id-mode column-mapped, DV-enabled table (no data files needed — the
    // DELETE must fail closed at/near snapshot load before any file is scanned).
    private async Task WriteRawIdModeTableAsync()
    {
        using var backend = new LocalFileSystemBackend(_root);
        const string protocol =
            "{\"protocol\":{\"minReaderVersion\":3,\"minWriterVersion\":7,"
            + "\"readerFeatures\":[\"columnMapping\",\"deletionVectors\"],"
            + "\"writerFeatures\":[\"columnMapping\",\"deletionVectors\"]}}";
        const string emptySchema = "{\\\"type\\\":\\\"struct\\\",\\\"fields\\\":[]}";
        const string metadata =
            "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
            + "\"schemaString\":\"" + emptySchema + "\",\"partitionColumns\":[],"
            + "\"configuration\":{\"delta.columnMapping.mode\":\"id\",\"delta.columnMapping.maxColumnId\":\"3\","
            + "\"delta.enableDeletionVectors\":\"true\"}}}";
        byte[] content = Encoding.UTF8.GetBytes(protocol + "\n" + metadata + "\n");
        await backend.PutIfAbsentAsync(
            "_delta_log/00000000000000000000.json", content, CancellationToken.None);
    }

    // A column-mapped, DV-enabled snapshot constructed DIRECTLY (not via DeltaLog.LoadSnapshotAsync), so it
    // deliberately BYPASSES the snapshot-load column-mapping gate (ColumnMapping.EnsureModeGate). This lets a
    // test reach DeltaDelete's OWN defense-in-depth guard with a mode the primary gate would otherwise have
    // rejected at load. No data files are needed — the DELETE fails closed before any file is scanned.
    private static Snapshot BuildUngatedColumnMappingSnapshot(string mode, string schemaString)
    {
        var protocol = new ProtocolAction(
            3, 7,
            ImmutableArray.Create("columnMapping", "deletionVectors"),
            ImmutableArray.Create("columnMapping", "deletionVectors"));
        ImmutableSortedDictionary<string, string> configuration = ImmutableSortedDictionary<string, string>.Empty
            .Add("delta.columnMapping.mode", mode)
            .Add("delta.columnMapping.maxColumnId", "8")
            .Add("delta.enableDeletionVectors", "true");
        var metadata = new MetadataAction(
            Id: "ungated-colmap",
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
            SchemaString: schemaString,
            PartitionColumns: ImmutableArray<string>.Empty,
            Configuration: configuration,
            CreatedTime: null);
        return new Snapshot(
            version: 0,
            protocol,
            metadata,
            ImmutableArray<AddFileAction>.Empty,
            ImmutableArray<RemoveFileAction>.Empty,
            ImmutableSortedDictionary<string, long>.Empty,
            SnapshotLoadMetrics.Empty);
    }

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
