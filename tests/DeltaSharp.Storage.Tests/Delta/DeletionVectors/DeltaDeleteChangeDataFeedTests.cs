using System.Globalization;
using System.Text;
using System.Text.Json;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta.DeletionVectors;

/// <summary>
/// Change Data Feed <b>generation</b> (increment 2, design §2.5): a merge-on-read <see cref="DeltaDelete"/>
/// on a CDF-enabled table materializes its deleted rows as <c>_change_data/</c> <c>cdc</c> files stamped
/// <c>_change_type='delete'</c>, published ATOMICALLY in the DELETE commit, while a CDF-disabled DELETE is
/// byte-for-byte unchanged (INV C1). Every test is oracle-backed over the committed <c>_delta_log</c> actions
/// and the actual <c>_change_data/</c> Parquet bytes (read back through <see cref="ParquetFileReader"/>, since
/// the CDF read door is a later increment), so a "cdc dropped a row" or "wrong branch" mutant fails on VALUES.
///
/// <para>Highest-risk properties (a subsequent red-team scrutinizes exactly these): <b>completeness</b> — a
/// cdc-bearing version must materialize EVERY delete in BOTH branches (a partially-deleted file's
/// <c>newDV \ oldDV</c> and a fully-deleted file's <c>physical \ oldDV</c>), because read-time precedence
/// suppresses all implicit derivation for a cdc-bearing version, so a partial cdc set silently loses changes
/// (INV C2/C3); and <b>atomicity</b> — cdc actions ride the SAME commit as the <c>remove</c>/<c>add</c>.</para>
///
/// <para>Isolated in the shared non-parallel filesystem collection (drives a real temp-directory backend).</para>
/// </summary>
[Collection(DeletionVectorFileTestCollection.Name)]
public sealed class DeltaDeleteChangeDataFeedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dv-cdf-" + Guid.NewGuid().ToString("N"));

    // id (non-null key) + name (nullable string): the same flat shape the DV harness uses.
    private static readonly StructType FlatSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    // A partitioned shape: `region` is the partition column (its values live only on the add/cdc action, never
    // in the file body), `id`/`val` live in the Parquet file. Partitioning yields ONE data file per region, so
    // this drives the multi-file DELETE (one cdc file per affected file) and the partition-exclusion oracle.
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

    // ---------------------------------------------------------------- CDF-HP-02: DV delete → cdc == new deletes

    [Fact]
    public async Task Delete_DvDelete_WritesCdcFile_WithExactlyNewlyDeletedRows()
    {
        // CDF-HP-02: a partial (DV-carrying) DELETE on a CDF-enabled table writes ONE _change_data/ file whose
        // rows are EXACTLY the newly-deleted rows — full DATA payload, each stamped _change_type='delete' — and
        // publishes ONE cdc action in the SAME commit as the remove + residual add.
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b"), (3, "c"), (4, "d"), (5, "e")));
        var backend = new LocalFileSystemBackend(_root);

        DeleteResult result = await NewCdfDelete(backend, "cdf-hp-02", SequentialCdcTokens())
            .DeleteAsync(WhereId(id => id is 2 or 4));

        Assert.NotNull(result.CommittedVersion);
        Assert.Equal(2, result.RowsDeleted);
        Assert.Equal(1, result.FilesWithDeletionVector);
        Assert.Equal(0, result.FilesFullyDeleted);

        // Exactly one cdc file, referenced by exactly one cdc action in the SAME commit.
        IReadOnlyList<DeltaAction> committed = await ReadCommitActionsAsync(backend, result.CommittedVersion!.Value);
        AddCdcFileAction cdcAction = Assert.Single(committed.OfType<AddCdcFileAction>());
        Assert.Single(committed.OfType<RemoveFileAction>());          // prior file tombstoned
        Assert.Single(committed.OfType<AddFileAction>());             // residual DV-carrying add (partial delete)

        List<string> cdcFiles = CdcFilePaths();
        string cdcFile = Assert.Single(cdcFiles);
        Assert.Equal(cdcFile, cdcAction.Path);                       // the action references the file we wrote
        Assert.Equal(new FileInfo(Path.Combine(_root, cdcFile)).Length, cdcAction.Size); // Size == real bytes
        Assert.Empty(cdcAction.PartitionValues);                     // unpartitioned table

        // The cdc BODY is exactly the two newly-deleted rows, full payload, all 'delete'.
        List<(long Id, string? Name, string ChangeType)> cdc = DecodeFlat(await ReadCdcAsync(cdcFile));
        Assert.Equal(
            new (long, string?, string)[] { (2L, "b", ChangeDataWriter.DeleteChange), (4L, "d", ChangeDataWriter.DeleteChange) },
            cdc.OrderBy(r => r.Id).ToList());

        // A normal snapshot read still excludes the deleted rows (the DV path is unaffected, INV C1 on reads).
        Assert.Equal(
            new (long, string?)[] { (1L, "a"), (3L, "c"), (5L, "e") },
            (await ReadLatestFlatAsync()).OrderBy(r => r.Item1).ToList());
    }

    // ---------------------------------------------------------------- both-branches completeness (INV C2/C3)

    [Fact]
    public async Task Delete_FullyDeletesFile_MaterializesEveryRowAsCdc()
    {
        // A DELETE that removes EVERY physical row of a file takes the fully-deleted branch (a bare remove, no
        // residual add). Completeness (INV C2/C3) requires that branch to ALSO emit cdc — one file with all
        // rows, each 'delete'. A mutant that only wrote cdc on the partial branch would drop these deletes.
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b"), (3, "c")));
        var backend = new LocalFileSystemBackend(_root);

        DeleteResult result = await NewCdfDelete(backend, "full-delete", SequentialCdcTokens())
            .DeleteAsync(WhereId(_ => true));

        Assert.Equal(3, result.RowsDeleted);
        Assert.Equal(0, result.FilesWithDeletionVector);   // no DV — the file is gone outright
        Assert.Equal(1, result.FilesFullyDeleted);

        IReadOnlyList<DeltaAction> committed = await ReadCommitActionsAsync(backend, result.CommittedVersion!.Value);
        Assert.Single(committed.OfType<RemoveFileAction>());
        Assert.Empty(committed.OfType<AddFileAction>());    // fully deleted → NO residual add
        Assert.Single(committed.OfType<AddCdcFileAction>()); // …but STILL a cdc action (completeness)

        List<(long Id, string? Name, string ChangeType)> cdc = DecodeFlat(await ReadCdcAsync(Assert.Single(CdcFilePaths())));
        Assert.Equal(
            new (long, string?, string)[]
            {
                (1L, "a", ChangeDataWriter.DeleteChange),
                (2L, "b", ChangeDataWriter.DeleteChange),
                (3L, "c", ChangeDataWriter.DeleteChange),
            },
            cdc.OrderBy(r => r.Id).ToList());

        Assert.Empty(await ReadLatestFlatAsync());          // the table is now empty
    }

    [Fact]
    public async Task Delete_FullyDeletesPreviouslyVectoredFile_CdcIsPhysicalMinusOldDv()
    {
        // The sharp both-branches case: a file already carries an old DV ({position 1} = id 2 from a first
        // partial delete), then a second DELETE removes the REST. The union becomes the whole file → the
        // fully-deleted branch. Its cdc must be `physical \ oldDV` (the remaining-LIVE rows this delete
        // removed) — NOT `all physical` (which would wrongly re-emit id 2, already deleted in v1) and NOT
        // empty. This pins the uniform "newly-deleted" capture that makes both branches collapse to the same
        // set (partial: newDV\oldDV; full: physical\oldDV == newlyMatched\oldDV).
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b"), (3, "c"), (4, "d")));
        var backend = new LocalFileSystemBackend(_root);

        // v1: partial delete of id 2 → oldDV {1}. (Its own cdc file is asserted elsewhere; here we only need
        // the resulting old DV.)
        DeleteResult v1 = await NewCdfDelete(backend, "pre-dv", SequentialCdcTokens("v1-"))
            .DeleteAsync(WhereId(id => id == 2));
        Assert.Equal(1, v1.FilesWithDeletionVector);
        List<string> afterV1 = CdcFilePaths();
        Assert.Single(afterV1);

        // v2: delete ids 1,3,4 → newly matched {0,2,3}; union with oldDV {1} = {0,1,2,3} = all 4 → FULL delete.
        DeleteResult v2 = await NewCdfDelete(backend, "rest-dv", SequentialCdcTokens("v2-"))
            .DeleteAsync(WhereId(id => id is 1 or 3 or 4));
        Assert.Equal(3, v2.RowsDeleted);                    // only the 3 NEW deletes (id 2 already masked)
        Assert.Equal(0, v2.FilesWithDeletionVector);
        Assert.Equal(1, v2.FilesFullyDeleted);

        IReadOnlyList<DeltaAction> committed = await ReadCommitActionsAsync(backend, v2.CommittedVersion!.Value);
        Assert.Empty(committed.OfType<AddFileAction>());    // fully deleted → no residual add
        AddCdcFileAction cdcAction = Assert.Single(committed.OfType<AddCdcFileAction>());

        // The v2 cdc file is the newly-created one (afterV1's file is v1's).
        string v2Cdc = Assert.Single(CdcFilePaths().Except(afterV1, StringComparer.Ordinal));
        Assert.Equal(v2Cdc, cdcAction.Path);
        List<(long Id, string? Name, string ChangeType)> cdc = DecodeFlat(await ReadCdcAsync(v2Cdc));

        // physical \ oldDV = ids {1,3,4} — id 2 (already masked in v1) is NOT re-emitted.
        Assert.Equal(
            new (long, string?, string)[]
            {
                (1L, "a", ChangeDataWriter.DeleteChange),
                (3L, "c", ChangeDataWriter.DeleteChange),
                (4L, "d", ChangeDataWriter.DeleteChange),
            },
            cdc.OrderBy(r => r.Id).ToList());
    }

    // ---------------------------------------------------------------- multi-file + mixed branches, one commit

    [Fact]
    public async Task Delete_MultiFile_MixedBranches_EmitsOneCdcPerFile_InOneAtomicCommit()
    {
        // Two data files (one per region). A single DELETE PARTIALLY deletes the east file (id 3 survives) and
        // FULLY deletes the west file. Completeness + atomicity require BOTH branches to emit cdc in ONE
        // commit: 2 removes, 1 residual add (east), 2 cdc actions — and each cdc file carries only its own
        // file's newly-deleted rows with the file's partition value on the ACTION (not in the body, §2.4).
        await CreateCdfPartitionedTableAsync(
            PartBatch((1, "east", 10), (2, "east", 20), (3, "east", 30), (4, "west", 40), (5, "west", 50)));
        var backend = new LocalFileSystemBackend(_root);

        long readVersion = (await new DeltaLog(backend).LoadSnapshotAsync()).Version;
        DeleteResult result = await NewCdfDelete(backend, "multi-file", SequentialCdcTokens())
            .DeleteAsync(WhereId(id => id != 3));

        // ATOMICITY: exactly one new version (a single commit carrying every remove/add/cdc).
        Assert.Equal(readVersion + 1, result.CommittedVersion);
        Assert.Equal(4, result.RowsDeleted);
        Assert.Equal(1, result.FilesWithDeletionVector);   // east (partial)
        Assert.Equal(1, result.FilesFullyDeleted);         // west (full)

        IReadOnlyList<DeltaAction> committed = await ReadCommitActionsAsync(backend, result.CommittedVersion!.Value);
        Assert.Equal(2, committed.OfType<RemoveFileAction>().Count());
        AddFileAction residual = Assert.Single(committed.OfType<AddFileAction>()); // ONLY east has a residual add
        Assert.NotNull(residual.DeletionVector);
        List<AddCdcFileAction> cdcActions = committed.OfType<AddCdcFileAction>().ToList();
        Assert.Equal(2, cdcActions.Count);                 // ONE cdc per affected file

        Assert.Equal(2, CdcFilePaths().Count);             // two cdc files on disk

        // Map each cdc action to its partition and assert its body is that file's newly-deleted rows.
        AddCdcFileAction eastCdc = Assert.Single(cdcActions, c => c.PartitionValues["region"] == "east");
        AddCdcFileAction westCdc = Assert.Single(cdcActions, c => c.PartitionValues["region"] == "west");

        Assert.Equal(
            new (long, long?, string)[] { (1L, 10L, ChangeDataWriter.DeleteChange), (2L, 20L, ChangeDataWriter.DeleteChange) },
            DecodeIdVal(await ReadCdcAsync(eastCdc.Path)).OrderBy(r => r.Id).ToList());
        Assert.Equal(
            new (long, long?, string)[] { (4L, 40L, ChangeDataWriter.DeleteChange), (5L, 50L, ChangeDataWriter.DeleteChange) },
            DecodeIdVal(await ReadCdcAsync(westCdc.Path)).OrderBy(r => r.Id).ToList());

        // Metrics reflect BOTH branches: 4 rows, 2 change files.
        CommitInfoAction commitInfo = committed.OfType<CommitInfoAction>().Single();
        Assert.Equal("\"4\"", commitInfo.OperationMetrics!["numDeletedRows"]);
        Assert.Equal("\"2\"", commitInfo.OperationMetrics!["numAddedChangeFiles"]);
    }

    // ---------------------------------------------------------------- INV C1: disabled ⇒ no cdc, unchanged

    [Fact]
    public async Task Delete_CdfDisabled_WritesNoCdc_AndCommitsUnchangedDataActions()
    {
        // INV C1: with CDF disabled, a DELETE writes NO _change_data/ file, emits NO cdc action, and its
        // remove/add DATA actions are exactly the pre-increment shape. operationMetrics is still recorded (the
        // sanctioned addition), with numAddedChangeFiles=0 — an honest metric set for a no-cdc delete.
        await CreateDvOnlyFlatTableAsync(Batch((1, "a"), (2, "b"), (3, "c")));
        var backend = new LocalFileSystemBackend(_root);

        DeleteResult result = await NewCdfDelete(backend, "disabled", ThrowingCdcTokens())
            .DeleteAsync(WhereId(id => id == 2));

        Assert.Equal(1, result.RowsDeleted);
        Assert.Equal(1, result.FilesWithDeletionVector);

        // No cdc directory / files at all.
        Assert.False(Directory.Exists(Path.Combine(_root, ChangeDataWriter.ChangeDataDirectory)));
        Assert.Empty(CdcFilePaths());

        IReadOnlyList<DeltaAction> committed = await ReadCommitActionsAsync(backend, result.CommittedVersion!.Value);
        Assert.Empty(committed.OfType<AddCdcFileAction>());          // no cdc action
        Assert.Single(committed.OfType<RemoveFileAction>());         // unchanged data-plane: 1 remove
        AddFileAction add = Assert.Single(committed.OfType<AddFileAction>());
        Assert.NotNull(add.DeletionVector);                          // …+ 1 residual DV-carrying add
        Assert.Equal(1L, add.DeletionVector!.Cardinality);
        Assert.Equal(3L, add.Stats!.NumRecords);                     // physical count unchanged

        // operationMetrics present with numAddedChangeFiles=0 (the ONE intended commitInfo addition).
        CommitInfoAction commitInfo = committed.OfType<CommitInfoAction>().Single();
        Assert.Equal("\"1\"", commitInfo.OperationMetrics!["numDeletedRows"]);
        Assert.Equal("\"0\"", commitInfo.OperationMetrics!["numAddedChangeFiles"]);

        // Raw on-disk: the metric is a JSON string "0" (Map<String,String>), never a bare number.
        JsonElement raw = await ReadRawOperationMetricsAsync(result.CommittedVersion!.Value);
        Assert.Equal(JsonValueKind.String, raw.GetProperty("numAddedChangeFiles").ValueKind);
        Assert.Equal("0", raw.GetProperty("numAddedChangeFiles").GetString());
    }

    // ---------------------------------------------------------------- idempotent re-delete emits no cdc

    [Fact]
    public async Task Delete_IdempotentReDelete_EmitsNoCdcAndNoCommit()
    {
        // Re-deleting a row already masked by the prior DV is a pure no-op: NewlyDeletedCount==0 for the file,
        // so the DELETE never commits and never writes a cdc file (a cdc file with zero rows would be a lie).
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b"), (3, "c")));
        var backend = new LocalFileSystemBackend(_root);

        DeleteResult first = await NewCdfDelete(backend, "idem-1", SequentialCdcTokens())
            .DeleteAsync(WhereId(id => id == 2));
        Assert.NotNull(first.CommittedVersion);
        Assert.Single(CdcFilePaths());                              // v1 wrote exactly one cdc file

        long versionAfterFirst = (await new DeltaLog(backend).LoadSnapshotAsync()).Version;

        // Re-delete the SAME id: no rows are newly deleted, so no commit and no new cdc file.
        DeleteResult second = await NewCdfDelete(backend, "idem-2", ThrowingCdcTokens())
            .DeleteAsync(WhereId(id => id == 2));
        Assert.Null(second.CommittedVersion);
        Assert.Equal(0, second.RowsDeleted);
        Assert.Single(CdcFilePaths());                             // STILL just v1's file — none added
        Assert.Equal(versionAfterFirst, (await new DeltaLog(backend).LoadSnapshotAsync()).Version);
    }

    // ---------------------------------------------------------------- operationMetrics numbers

    [Fact]
    public async Task Delete_OperationMetrics_RecordsDeletedRowsAndChangeFiles()
    {
        // The DELETE operationMetrics carry the canonical delta-io/delta DELETE keys numDeletedRows and
        // numAddedChangeFiles (verified against delta v4.3.1), as quoted number-strings, on a real cdc-bearing
        // commit (2 deleted rows in one file → 1 change file).
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b"), (3, "c"), (4, "d")));
        var backend = new LocalFileSystemBackend(_root);

        DeleteResult result = await NewCdfDelete(backend, "metrics", SequentialCdcTokens())
            .DeleteAsync(WhereId(id => id is 2 or 3));

        IReadOnlyList<DeltaAction> committed = await ReadCommitActionsAsync(backend, result.CommittedVersion!.Value);
        CommitInfoAction commitInfo = committed.OfType<CommitInfoAction>().Single();
        Assert.Equal("DELETE", commitInfo.Operation);
        Assert.Equal("\"2\"", commitInfo.OperationMetrics!["numDeletedRows"]);
        Assert.Equal("\"1\"", commitInfo.OperationMetrics!["numAddedChangeFiles"]);

        // On disk every operationMetrics value is a JSON string (Delta Map<String,String>), never a bare number.
        JsonElement raw = await ReadRawOperationMetricsAsync(result.CommittedVersion!.Value);
        foreach (string key in new[] { "numDeletedRows", "numAddedChangeFiles" })
        {
            Assert.Equal(JsonValueKind.String, raw.GetProperty(key).ValueKind);
        }

        Assert.Equal("2", raw.GetProperty("numDeletedRows").GetString());
        Assert.Equal("1", raw.GetProperty("numAddedChangeFiles").GetString());
    }

    // ---------------------------------------------------------------- deterministic cdc file naming

    [Fact]
    public async Task Delete_CdcFileName_ComesFromInjectedDeterministicFactory()
    {
        // The cdc file name is drawn from the injected deterministic naming seam (never Guid.NewGuid /
        // DateTime.UtcNow — those are banned build errors in src/), so a golden fixture pins the exact path.
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b"), (3, "c")));
        var backend = new LocalFileSystemBackend(_root);

        DeleteResult result = await NewCdfDelete(backend, "naming", () => "FIXEDTOKEN")
            .DeleteAsync(WhereId(id => id == 2));

        string expected = ChangeDataWriter.ChangeDataDirectory + "/cdc-FIXEDTOKEN.parquet";
        Assert.Equal(expected, Assert.Single(CdcFilePaths()));

        IReadOnlyList<DeltaAction> committed = await ReadCommitActionsAsync(backend, result.CommittedVersion!.Value);
        Assert.Equal(expected, Assert.Single(committed.OfType<AddCdcFileAction>()).Path);
    }

    [Fact]
    public void DefaultFileNameFactory_ProducesHexToken_NotAGuid()
    {
        // The production default seam is hex of 128 crypto-random bits — 32 hex chars, NO dashes (a Guid.ToString
        // would carry dashes) — proving the deterministic-token contract even in the non-injected default.
        string token = ChangeDataWriter.DefaultFileNameFactory();
        Assert.Equal(32, token.Length);
        Assert.DoesNotContain('-', token);
        Assert.All(token, c => Assert.True(Uri.IsHexDigit(c), $"'{c}' is not a hex digit."));

        // Two calls differ (a fresh random each time), so concurrent writers never collide on a cdc path.
        Assert.NotEqual(token, ChangeDataWriter.DefaultFileNameFactory());
    }

    // ---------------------------------------------------------------- column mapping (name mode)

    [Fact]
    public async Task Delete_NameMode_CdcBodyUsesPhysicalNames_AndExcludesPartitionColumns()
    {
        // Under column mapping (name mode) the cdc body stores the DATA columns by their PHYSICAL (col-<uuid>)
        // names — exactly like a data file — and the synthesized _change_type keeps its literal logical name
        // (never column-mapped). Partition columns are NOT in the body (§2.4). Proven on a partitioned,
        // name-mapped, CDF-enabled table by reading the cdc file's footer schema.
        await CreateNameMappedCdfPartitionedTableAsync(
            PartBatch((1, "east", 10), (2, "east", 20), (3, "west", 30)));
        var backend = new LocalFileSystemBackend(_root);

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync();
        string[] physicalNames = ColumnMappingProjection.ResolvePhysicalNames(snapshot.Schema, ColumnMappingMode.Name);
        StructType dataSchema = ColumnMappingProjection.BuildDataSchema(
            snapshot.Schema, physicalNames, snapshot.Metadata.PartitionColumns);

        DeleteResult result = await NewCdfDelete(backend, "name-mode", SequentialCdcTokens())
            .DeleteAsync(WhereId(id => id == 1));    // deletes only in the `east` file → exactly one cdc file
        Assert.NotNull(result.CommittedVersion);

        (StructType cdcSchema, List<ColumnBatch> batches) = await ReadCdcWithSchemaAsync(Assert.Single(CdcFilePaths()));

        // The cdc footer schema = the physical DATA columns (col-<uuid>, id + val; region EXCLUDED) then the
        // literal _change_type. dataSchema is the exact physical data schema the DELETE resolves.
        string[] expectedNames = dataSchema.Select(f => f.Name).Append(ChangeDataWriter.ChangeTypeColumn).ToArray();
        Assert.Equal(expectedNames, cdcSchema.Select(f => f.Name).ToArray());
        Assert.All(dataSchema, f => Assert.StartsWith("col-", f.Name, StringComparison.Ordinal)); // truly physical

        // The partition column's physical name is absent from the cdc body.
        string regionPhysical = physicalNames[snapshot.Schema.IndexOf("region")];
        Assert.DoesNotContain(regionPhysical, cdcSchema.Select(f => f.Name));

        // _change_type is literal (not mapped) and its value is the delete marker.
        Assert.Equal(ChangeDataWriter.ChangeTypeColumn, cdcSchema[cdcSchema.Count - 1].Name);
        List<(long Id, long? Val, string ChangeType)> rows = DecodeIdVal(batches);
        (long Id, long? Val, string ChangeType) only = Assert.Single(rows);
        Assert.Equal((1L, 10L, ChangeDataWriter.DeleteChange), only);
    }

    // ---------------------------------------------------------------- fail-closed: nested data column

    [Fact]
    public void EnsureWritableDataSchema_RejectsNestedDataColumn_FailsClosed()
    {
        // cdc generation is scalar-only (the selection-gather + scalar Parquet writer cannot materialize a
        // nested column). It fails closed EARLY on a nested data column rather than publish an incomplete cdc
        // set that read-time precedence would make silently lossy.
        var nested = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("nested", new StructType(new[] { new StructField("x", DataTypes.LongType, nullable: true) }), nullable: true),
        });

        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(
            () => ChangeDataWriter.EnsureWritableDataSchema(nested));
        Assert.Contains("Change Data Feed", ex.Message, StringComparison.Ordinal);

        // A purely scalar schema is accepted (no throw).
        ChangeDataWriter.EnsureWritableDataSchema(new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("name", DataTypes.StringType, nullable: true),
        }));
    }

    // ------------------------------------------------------------------ helpers

    private DeltaDelete NewCdfDelete(LocalFileSystemBackend backend, string idSeed, Func<string> cdcFileNameFactory) =>
        new(backend, new DeltaLog(backend),
            idSource: new SeededDeletionVectorIdSource(idSeed),
            cdcFileNameFactory: cdcFileNameFactory);

    // A deterministic, DISTINCT cdc-name seam: cdc-<prefix>0000, cdc-<prefix>0001, … so a single commit's
    // per-file cdc files never collide, and every run is byte-for-byte reproducible (no ambient state).
    private static Func<string> SequentialCdcTokens(string prefix = "t")
    {
        int n = 0;
        return () => prefix + (n++).ToString("D4", CultureInfo.InvariantCulture);
    }

    // A seam that must never be called (CDF disabled / a re-delete with no new rows): if generation invokes it
    // the test fails loudly rather than silently writing a stray cdc file.
    private static Func<string> ThrowingCdcTokens() =>
        () => throw new InvalidOperationException("cdc file naming must not be invoked when no cdc is written.");

    private async Task CreateCdfFlatTableAsync(params ColumnBatch[] batches)
    {
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateDeletionVectorTableAsync(FlatSchema, Array.Empty<string>(), batches);
        }

        await new DeltaTableWriter(new LocalFileSystemBackend(_root)).EnableChangeDataFeedAsync();
    }

    private async Task CreateDvOnlyFlatTableAsync(params ColumnBatch[] batches)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        await target.CreateDeletionVectorTableAsync(FlatSchema, Array.Empty<string>(), batches);
    }

    private async Task CreateCdfPartitionedTableAsync(params ColumnBatch[] batches)
    {
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateDeletionVectorTableAsync(PartitionedSchema, new[] { "region" }, batches);
        }

        await new DeltaTableWriter(new LocalFileSystemBackend(_root)).EnableChangeDataFeedAsync();
    }

    private async Task CreateNameMappedCdfPartitionedTableAsync(params ColumnBatch[] batches)
    {
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateNameMappedDeletionVectorTableAsync(
                PartitionedSchema, new[] { "region" }, batches, new SeededPhysicalNameSource("cdf-name-mode"));
        }

        await new DeltaTableWriter(new LocalFileSystemBackend(_root)).EnableChangeDataFeedAsync();
    }

    private static DeltaDeletePredicate WhereId(Func<long, bool> match) =>
        DeltaDeletePredicate.FromRowPredicate((batch, row) => match(batch.SelectedColumn(0).GetValue<long>(row)));

    private async Task<IReadOnlyList<DeltaAction>> ReadCommitActionsAsync(LocalFileSystemBackend backend, long version) =>
        await new DeltaLog(backend).ReadCommitActionsAsync(version, CancellationToken.None);

    // Every *.parquet under _change_data/, as table-root-relative '/'-separated paths (matching an action Path).
    private List<string> CdcFilePaths()
    {
        string dir = Path.Combine(_root, ChangeDataWriter.ChangeDataDirectory);
        if (!Directory.Exists(dir))
        {
            return new List<string>();
        }

        return Directory.GetFiles(dir, "*.parquet", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    // Reads a cdc Parquet file back through its OWN footer schema (physical data names + _change_type), since
    // the CDF read door is a later increment — so the oracle sees exactly the bytes generation wrote.
    private async Task<List<ColumnBatch>> ReadCdcAsync(string relativePath)
    {
        (_, List<ColumnBatch> batches) = await ReadCdcWithSchemaAsync(relativePath);
        return batches;
    }

    private async Task<(StructType Schema, List<ColumnBatch> Batches)> ReadCdcWithSchemaAsync(string relativePath)
    {
        var reader = new ParquetFileReader();
        string full = Path.Combine(_root, relativePath);

        StructType schema;
        await using (FileStream footer = File.OpenRead(full))
        {
            schema = await reader.ReadDataSchemaAsync(footer, CancellationToken.None);
        }

        var batches = new List<ColumnBatch>();
        await using (FileStream data = File.OpenRead(full))
        {
            await foreach (ColumnBatch batch in reader.ReadAsync(
                data, schema, keepRowGroup: null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None))
            {
                batches.Add(batch);
            }
        }

        return (schema, batches);
    }

    private static List<(long Id, string? Name, string ChangeType)> DecodeFlat(List<ColumnBatch> batches)
    {
        var rows = new List<(long, string?, string)>();
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector name = batch.SelectedColumn(1);
            ColumnVector changeType = batch.SelectedColumn(batch.ColumnCount - 1);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((id.GetValue<long>(r), name.IsNull(r) ? null : Utf8(name, r), Utf8(changeType, r)));
            }
        }

        return rows;
    }

    private static List<(long Id, long? Val, string ChangeType)> DecodeIdVal(List<ColumnBatch> batches)
    {
        var rows = new List<(long, long?, string)>();
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector val = batch.SelectedColumn(1);
            ColumnVector changeType = batch.SelectedColumn(batch.ColumnCount - 1);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((id.GetValue<long>(r), val.IsNull(r) ? null : val.GetValue<long>(r), Utf8(changeType, r)));
            }
        }

        return rows;
    }

    private static string Utf8(ColumnVector vector, int row) => Encoding.UTF8.GetString(vector.GetBytes(row));

    private async Task<List<(long, string?)>> ReadLatestFlatAsync()
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        var rows = new List<(long, string?)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector name = batch.SelectedColumn(1);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((id.GetValue<long>(r), name.IsNull(r) ? null : Utf8(name, r)));
            }
        }

        return rows;
    }

    // Reads the RAW on-disk commitInfo.operationMetrics for a version, so a test can assert the JSON value KIND
    // (a Delta Map<String,String> value must be a JSON string, never a bare number).
    private async Task<JsonElement> ReadRawOperationMetricsAsync(long version)
    {
        string path = Path.Combine(_root, "_delta_log", version.ToString("D20", CultureInfo.InvariantCulture) + ".json");
        foreach (string line in await File.ReadAllLinesAsync(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("commitInfo", out JsonElement commitInfo)
                && commitInfo.TryGetProperty("operationMetrics", out JsonElement metrics))
            {
                return metrics.Clone();
            }
        }

        throw new InvalidOperationException($"No commitInfo.operationMetrics in version {version}.");
    }

    private static ColumnBatch Batch(params (long Id, string? Name)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((long i, string? n) in rows)
        {
            id.AppendValue(i);
            if (n is null)
            {
                name.AppendNull();
            }
            else
            {
                name.AppendBytes(Encoding.UTF8.GetBytes(n));
            }
        }

        return new ManagedColumnBatch(FlatSchema, new ColumnVector[] { id, name }, rows.Length);
    }

    private static ColumnBatch PartBatch(params (long Id, string Region, long? Val)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        MutableColumnVector val = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        foreach ((long i, string reg, long? v) in rows)
        {
            id.AppendValue(i);
            region.AppendBytes(Encoding.UTF8.GetBytes(reg));
            if (v is null)
            {
                val.AppendNull();
            }
            else
            {
                val.AppendValue(v.Value);
            }
        }

        return new ManagedColumnBatch(PartitionedSchema, new ColumnVector[] { id, region, val }, rows.Length);
    }
}
