using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta.DeletionVectors;

/// <summary>
/// End-to-end merge-on-read deletion-vector tests through the real write → DELETE → read pipeline
/// (STORY-05.5.1, all four ACs):
/// <list type="bullet">
/// <item><b>AC1</b> — a scan of a table with a committed DV excludes exactly the deleted row positions
/// (asserted on surviving row VALUES, not counts, so a "DV ignored" mutant fails).</item>
/// <item><b>AC2</b> — DELETE produces a DV (file is NOT rewritten), records the residual <c>numRecords</c>,
/// and two concurrent DELETEs to the same file conflict so one aborts with no lost delete.</item>
/// <item><b>AC3</b> — DELETE against a table that does not enable deletion vectors fails closed.</item>
/// <item><b>AC4</b> — time travel: the pre-DELETE version returns every row, the post-DELETE version returns
/// only survivors.</item>
/// </list>
/// Isolated in a non-parallel collection because it drives a real temp-directory backend.
/// </summary>
[Collection(DeletionVectorFileTestCollection.Name)]
public sealed class DeletionVectorReadWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dv-rw-" + Guid.NewGuid().ToString("N"));

    private static readonly StructType FlatSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
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

    // ------------------------------------------------------------------ AC1 + AC2: DELETE round-trip

    [Fact]
    public async Task Delete_WritesDeleteCommitInfo()
    {
        // #510: a DELETE records operation="DELETE" in commitInfo (interop/parity for DESCRIBE HISTORY),
        // alongside the engine-stamped txnId/engineInfo/timestamp. The injected clock pins the timestamp.
        await CreateDeletionVectorTableAsync(Batch((1, "a"), (2, "b"), (3, "c")));

        var instant = new DateTimeOffset(2032, 6, 7, 8, 9, 10, TimeSpan.Zero);
        var backend = new LocalFileSystemBackend(_root);
        DeleteResult result = await NewDelete(backend, "delete-commitinfo", new FixedTimeProvider(instant))
            .DeleteAsync(WhereId(id => id == 2));

        IReadOnlyList<DeltaAction> committed =
            await new DeltaLog(backend).ReadCommitActionsAsync(result.CommittedVersion!.Value, CancellationToken.None);
        CommitInfoAction commitInfo = committed.OfType<CommitInfoAction>().Single();
        Assert.Equal("DELETE", commitInfo.Operation);
        Assert.Equal(instant.ToUnixTimeMilliseconds(), commitInfo.Timestamp);
        Assert.StartsWith("DeltaSharp/", commitInfo.EngineInfo);
        Assert.True(commitInfo.Entries.ContainsKey("txnId"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Delete_ExcludesDeletedRows_WithoutRewritingTheDataFile()
    {
        await CreateDeletionVectorTableAsync(
            Batch((1, "a"), (2, "b"), (3, "c"), (4, "d"), (5, "e")));

        int parquetBefore = CountFiles("*.parquet");

        var delete = NewDelete(new LocalFileSystemBackend(_root), "excludes-deleted-rows");
        DeleteResult result = await delete.DeleteAsync(WhereId(id => id == 2 || id == 4));

        Assert.NotNull(result.CommittedVersion);
        Assert.Equal(2, result.RowsDeleted);
        Assert.Equal(1, result.FilesWithDeletionVector);
        Assert.Equal(0, result.FilesFullyDeleted);

        // AC2: the data file is untouched (merge-on-read) — same parquet file, a new .bin DV alongside it.
        Assert.Equal(parquetBefore, CountFiles("*.parquet"));
        Assert.True(CountFiles("*.bin") >= 1, "DELETE must have written a deletion-vector .bin file.");

        // AC1: the scan excludes exactly rows 2 and 4 — surviving VALUES, not just a count.
        List<(long, string?)> survivors = await ReadLatestAsync();
        Assert.Equal(
            new (long, string?)[] { (1L, "a"), (3L, "c"), (5L, "e") },
            survivors.OrderBy(r => r.Item1).ToList());
    }

    [Fact]
    public async Task Delete_KeepsPhysicalNumRecords_OnTheDvCarryingAdd()
    {
        await CreateDeletionVectorTableAsync(Batch((10, "x"), (20, "y"), (30, "z"), (40, "w")));

        var backend = new LocalFileSystemBackend(_root);
        var delete = NewDelete(backend, "keeps-physical-numrecords");
        await delete.DeleteAsync(WhereId(id => id == 20));

        // Writer requirement (matching Spark): the DV-carrying add reports the PHYSICAL data-file row count
        // (4 — the total rows in the Parquet file, NOT the residual 4−1=3) in stats.numRecords; the residual
        // logical count is numRecords − cardinality. TightBounds is cleared.
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync();
        AddFileAction dvAdd = Assert.Single(snapshot.ActiveFiles.Where(a => a.DeletionVector is not null));
        Assert.NotNull(dvAdd.Stats);
        Assert.Equal(4L, dvAdd.Stats!.NumRecords);
        Assert.False(dvAdd.Stats!.TightBounds);
        Assert.Equal(1L, dvAdd.DeletionVector!.Cardinality);

        // The scan still excludes exactly id 20 (survivors by value), so physical numRecords did not leak a row.
        Assert.Equal(
            new (long, string?)[] { (10L, "x"), (30L, "z"), (40L, "w") },
            (await ReadLatestAsync()).OrderBy(r => r.Item1).ToList());
    }

    // ------------------------------------------------------------------ AC2 edge: whole-file delete

    [Fact]
    public async Task Delete_EveryRowInFile_RemovesFileOutright_NoResidualDv()
    {
        await CreateDeletionVectorTableAsync(Batch((1, "a"), (2, "b"), (3, "c")));

        var backend = new LocalFileSystemBackend(_root);
        var delete = NewDelete(backend, "every-row-removes-file");
        DeleteResult result = await delete.DeleteAsync(WhereId(_ => true));

        Assert.Equal(3, result.RowsDeleted);
        Assert.Equal(1, result.FilesFullyDeleted);
        Assert.Equal(0, result.FilesWithDeletionVector);

        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync();
        Assert.Empty(snapshot.ActiveFiles);
        Assert.Empty(await ReadLatestAsync());
    }

    // ------------------------------------------------------------------ AC4: time travel around a DV commit

    [Fact]
    public async Task Delete_TimeTravel_PreVersionAllRows_PostVersionSurvivors()
    {
        long v0 = await CreateDeletionVectorTableAsync(Batch((1, "a"), (2, "b"), (3, "c"), (4, "d")));

        var delete = NewDelete(new LocalFileSystemBackend(_root), "time-travel");
        DeleteResult result = await delete.DeleteAsync(WhereId(id => id == 1 || id == 3));
        long v1 = result.CommittedVersion!.Value;
        Assert.True(v1 > v0);

        // Pre-DV snapshot: the add has no DV → every original row is visible.
        Assert.Equal(
            new (long, string?)[] { (1L, "a"), (2L, "b"), (3L, "c"), (4L, "d") },
            (await ReadVersionAsync(v0)).OrderBy(r => r.Item1).ToList());

        // Post-DV snapshot: the same file's add now carries the DV → only survivors are visible.
        Assert.Equal(
            new (long, string?)[] { (2L, "b"), (4L, "d") },
            (await ReadVersionAsync(v1)).OrderBy(r => r.Item1).ToList());
    }

    // ------------------------------------------------------------------ AC2: concurrent DELETE conflict

    [Fact]
    public async Task Delete_ConcurrentDeleteToSameFile_LoserAborts_NoLostDelete()
    {
        await CreateDeletionVectorTableAsync(Batch((1, "a"), (2, "b"), (3, "c")));

        var backend = new LocalFileSystemBackend(_root);
        Snapshot readSnapshot = await new DeltaLog(backend).LoadSnapshotAsync();

        var winner = NewDelete(backend, "concurrent-winner");
        var loser = NewDelete(backend, "concurrent-loser");

        // The loser reads the same snapshot, but just before it commits, the winner commits a DELETE of a
        // different row from the SAME file. The loser's ReadFiles scope over that path then detects the
        // concurrent change and aborts — the winner's delete is never lost.
        loser.BeforeCommitProbe = ct => winner.DeleteAsync(readSnapshot, WhereId(id => id == 1), ct);

        await Assert.ThrowsAsync<ConcurrentDeleteReadException>(
            () => loser.DeleteAsync(readSnapshot, WhereId(id => id == 2)));

        // The winner's delete survived; the loser's (id 2) did not take effect → no lost delete, no double.
        Assert.Equal(
            new (long, string?)[] { (2L, "b"), (3L, "c") },
            (await ReadLatestAsync()).OrderBy(r => r.Item1).ToList());
    }

    // ------------------------------------------------------------------ AC3: protocol gate (fail closed)

    [Fact]
    public async Task Delete_OnTableWithoutDeletionVectorSupport_FailsClosed()
    {
        // A plain table written through the ordinary append door does NOT enable deletion vectors.
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { Batch((1, "a"), (2, "b")) });
        }

        var delete = NewDelete(new LocalFileSystemBackend(_root), "fail-closed-no-dv-support");

        // The DELETE must fail closed (never silently ignore the request and leave deleted rows readable).
        await Assert.ThrowsAsync<DeltaProtocolException>(
            () => delete.DeleteAsync(WhereId(id => id == 1)));

        // The table is unchanged: both rows still present, no DV file written.
        Assert.Equal(
            new (long, string?)[] { (1L, "a"), (2L, "b") },
            (await ReadLatestAsync()).OrderBy(r => r.Item1).ToList());
        Assert.Equal(0, CountFiles("*.bin"));
    }

    // ------------------------------------------------------------------ AC1: inline DV read exclusion

    [Fact]
    public async Task Read_InlineDeletionVector_ExcludesDeletedRows()
    {
        // A single data file with rows at physical positions 0..4 (ids 10,20,30,40,50).
        await CreateDeletionVectorTableAsync(Batch((10, "j"), (20, "k"), (30, "l"), (40, "m"), (50, "n")));

        var backend = new LocalFileSystemBackend(_root);
        var log = new DeltaLog(backend);
        Snapshot readSnapshot = await log.LoadSnapshotAsync();
        AddFileAction add = Assert.Single(readSnapshot.ActiveFiles);
        Assert.NotNull(add.Stats);

        // Build an INLINE DV (storageType 'i', Z85-encoded native RoaringBitmapArray) excluding physical
        // positions {1,3} — ids 20 and 40 — and commit remove(prior add) + add(same path, inline DV, residual
        // numRecords=3). This drives DeltaReadSource's inline decode branch (DecodeInlineBytes → Deserialize),
        // the sibling of the on-disk path the DELETE command exercises.
        byte[] rawBitmap = RoaringBitmapArray.Serialize(new long[] { 1, 3 });
        DeletionVectorDescriptor inline = DeletionVectorDescriptor.ForInline(rawBitmap, cardinality: 2);

        var remove = new RemoveFileAction(
            add.Path, DeletionTimestamp: 1, DataChange: true, ExtendedFileMetadata: true,
            add.PartitionValues, add.Size, DeletionVector: null);
        var residualAdd = new AddFileAction(
            add.Path, add.PartitionValues, add.Size, ModificationTime: 1, DataChange: true,
            add.Stats! with { NumRecords = 5 }, add.Tags, inline); // physical count (5), matching Spark

        var committer = new DeltaCommitter(backend);
        await committer.CommitAsync(
            readSnapshot,
            new DeltaAction[] { remove, residualAdd },
            DeltaReadScope.ReadFiles(new[] { add.Path }));

        // AC1 inline: the scan returns only the survivors, by VALUE.
        Assert.Equal(
            new (long, string?)[] { (10L, "j"), (30L, "l"), (50L, "n") },
            (await ReadLatestAsync()).OrderBy(r => r.Item1).ToList());
    }

    // ------------------------------------------------------------------ AC1: poisoned DV fails closed on read

    [Fact]
    public async Task Read_PoisonedInlineDeletionVector_OutOfRangePosition_FailsClosed()
    {
        // Rows at positions 0..4 (5 records). Commit an inline DV that references position 99 — an index at or
        // beyond the file's record count. The reader MUST fail closed (never silently drop the offending
        // position and never return the deleted rows as if the DV did not apply).
        await CreateDeletionVectorTableAsync(Batch((1, "a"), (2, "b"), (3, "c"), (4, "d"), (5, "e")));

        var backend = new LocalFileSystemBackend(_root);
        Snapshot readSnapshot = await new DeltaLog(backend).LoadSnapshotAsync();
        AddFileAction add = Assert.Single(readSnapshot.ActiveFiles);

        byte[] rawBitmap = RoaringBitmapArray.Serialize(new long[] { 1, 99 });
        DeletionVectorDescriptor poisoned = DeletionVectorDescriptor.ForInline(rawBitmap, cardinality: 2);

        var remove = new RemoveFileAction(
            add.Path, DeletionTimestamp: 1, DataChange: true, ExtendedFileMetadata: true,
            add.PartitionValues, add.Size, DeletionVector: null);
        var poisonedAdd = new AddFileAction(
            add.Path, add.PartitionValues, add.Size, ModificationTime: 1, DataChange: true,
            add.Stats! with { NumRecords = 5 }, add.Tags, poisoned); // honest physical count (5); the DV lies

        await new DeltaCommitter(backend).CommitAsync(
            readSnapshot,
            new DeltaAction[] { remove, poisonedAdd },
            DeltaReadScope.ReadFiles(new[] { add.Path }));

        // The read fails closed (position 99 ≥ the file's 5 physical rows) with a TYPED read fault, and no
        // rows leak — a "DV silently ignored" mutant would instead return the survivors and fail this test.
        var ex = await Assert.ThrowsAsync<DeltaReadException>(() => ReadLatestAsync());
        Assert.Contains("deletion vector", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ reliability: delete-twice DV union

    [Fact]
    public async Task Delete_Twice_UnionsDeletionVectors_NoResurrection()
    {
        await CreateDeletionVectorTableAsync(Batch((1, "a"), (2, "b"), (3, "c"), (4, "d"), (5, "e")));
        var backend = new LocalFileSystemBackend(_root);

        // First DELETE removes id 2 (physical position 1) → DV {1}, cardinality 1, physical numRecords 5.
        DeleteResult r1 = await NewDelete(backend, "dv-first").DeleteAsync(WhereId(id => id == 2));
        Assert.Equal(1, r1.RowsDeleted);
        Assert.Equal(1, r1.FilesWithDeletionVector);

        // Second DELETE removes id 4 (physical position 3). It must read the prior DV back through the
        // physical-numRecords semantics (numRecords IS the physical count 5, so a "numRecords + cardinality"
        // reader would over-count the file and throw) and UNION with it — never resurrecting id 2.
        DeleteResult r2 = await NewDelete(backend, "dv-second").DeleteAsync(WhereId(id => id == 4));
        Assert.Equal(1, r2.RowsDeleted);              // only the NEW row counts toward RowsDeleted
        Assert.Equal(1, r2.FilesWithDeletionVector);

        Snapshot snap = await new DeltaLog(backend).LoadSnapshotAsync();
        AddFileAction dvAdd = Assert.Single(snap.ActiveFiles);         // single active file (never rewritten)
        Assert.NotNull(dvAdd.DeletionVector);
        Assert.Equal(2L, dvAdd.DeletionVector!.Cardinality);          // the UNION {1,3}, not a lone {3}
        Assert.Equal(5L, dvAdd.Stats!.NumRecords);                    // physical count unchanged by DV growth

        // Survivors: ids 1, 3, 5 — id 2 stays deleted (no resurrection), id 4 now deleted.
        Assert.Equal(
            new (long, string?)[] { (1L, "a"), (3L, "c"), (5L, "e") },
            (await ReadLatestAsync()).OrderBy(r => r.Item1).ToList());
    }

    // ------------------------------------------------------------------ reliability: row-group boundary

    [Fact]
    public async Task Delete_PositionsStraddlingRowGroupBoundary_ExactSurvivorsOnRead()
    {
        await CreateDeletionVectorTableAsync(
            Batch((1, "a"), (2, "b"), (3, "c"), (4, "d"), (5, "e"), (6, "f")));

        var backend = new LocalFileSystemBackend(_root);
        AddFileAction add = Assert.Single((await new DeltaLog(backend).LoadSnapshotAsync()).ActiveFiles);

        // Re-lay the SAME six rows into 2-row row groups (3 groups) so the reader yields 3 batches and the
        // deleted positions {1,2} straddle the group-0/group-1 boundary — exercising the running fileRowOffset
        // arithmetic in BOTH the DELETE writer's plan and the read-path exclusion. Row VALUES are unchanged,
        // so the physical row count (6) still matches stats.numRecords.
        await RewriteWithRowGroupsAsync(
            add.Path, rowGroupRowLimit: 2,
            Batch((1, "a"), (2, "b"), (3, "c"), (4, "d"), (5, "e"), (6, "f")));

        DeleteResult result = await NewDelete(backend, "straddle").DeleteAsync(WhereId(id => id is 2 or 3));
        Assert.Equal(2, result.RowsDeleted);
        Assert.Equal(1, result.FilesWithDeletionVector);

        // Positions 1 and 2 deleted → survivors sit at positions 0,3,4,5 = ids 1,4,5,6.
        Assert.Equal(
            new (long, string?)[] { (1L, "a"), (4L, "d"), (5L, "e"), (6L, "f") },
            (await ReadLatestAsync()).OrderBy(r => r.Item1).ToList());
    }

    // ------------------------------------------------------------------ quality edges: none / last-row

    [Fact]
    public async Task Delete_NoRowMatches_IsNoOp_WritesNoDeletionVector()
    {
        long v0 = await CreateDeletionVectorTableAsync(Batch((1, "a"), (2, "b"), (3, "c")));
        var backend = new LocalFileSystemBackend(_root);

        DeleteResult result = await NewDelete(backend, "none").DeleteAsync(WhereId(id => id == 999));

        // An empty predicate match is a pure no-op: no commit, no DV file, no version bump.
        Assert.Null(result.CommittedVersion);
        Assert.Equal(0, result.RowsDeleted);
        Assert.Equal(0, result.FilesWithDeletionVector);
        Assert.Equal(0, CountFiles("*.bin"));
        Assert.Equal(v0, (await new DeltaLog(backend).LoadSnapshotAsync()).Version);

        Assert.Equal(
            new (long, string?)[] { (1L, "a"), (2L, "b"), (3L, "c") },
            (await ReadLatestAsync()).OrderBy(r => r.Item1).ToList());
    }

    [Fact]
    public async Task Delete_LastRowInFile_ExcludesOnlyThatRow()
    {
        await CreateDeletionVectorTableAsync(Batch((1, "a"), (2, "b"), (3, "c")));
        var backend = new LocalFileSystemBackend(_root);

        // Deleting the final physical position (2) must exclude exactly that row on read (offset arithmetic
        // must not run off the end of the last batch).
        DeleteResult result = await NewDelete(backend, "last").DeleteAsync(WhereId(id => id == 3));
        Assert.Equal(1, result.RowsDeleted);
        Assert.Equal(1, result.FilesWithDeletionVector);

        Snapshot snap = await new DeltaLog(backend).LoadSnapshotAsync();
        AddFileAction dvAdd = Assert.Single(snap.ActiveFiles);
        Assert.Equal(1L, dvAdd.DeletionVector!.Cardinality);
        Assert.Equal(3L, dvAdd.Stats!.NumRecords);

        Assert.Equal(
            new (long, string?)[] { (1L, "a"), (2L, "b") },
            (await ReadLatestAsync()).OrderBy(r => r.Item1).ToList());
    }

    // ------------------------------------------------------------------ helpers

    private static DeltaDelete NewDelete(
        LocalFileSystemBackend backend, string idSeed, TimeProvider? timeProvider = null) =>
        // committer omitted → built from the injected timeProvider so commitInfo.timestamp is pinned (#510).
        new(backend, new DeltaLog(backend), timeProvider: timeProvider,
            idSource: new SeededDeletionVectorIdSource(idSeed));

    private async Task RewriteWithRowGroupsAsync(string relativePath, int rowGroupRowLimit, ColumnBatch batch)
    {
        var writer = new ParquetFileWriter(rowGroupRowLimit);
        using var buffer = new MemoryStream();
        await writer.WriteAsync(buffer, FlatSchema, new[] { batch }, CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(_root, relativePath), buffer.ToArray());
    }

    private async Task<long> CreateDeletionVectorTableAsync(params ColumnBatch[] batches)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        DeltaWriteResult result = await target.CreateDeletionVectorTableAsync(
            FlatSchema, Array.Empty<string>(), batches);
        return result.Version;
    }

    private static DeltaDeletePredicate WhereId(Func<long, bool> match) =>
        DeltaDeletePredicate.FromRowPredicate((batch, row) => match(batch.SelectedColumn(0).GetValue<long>(row)));

    private async Task<List<(long, string?)>> ReadLatestAsync()
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        return await ReadVersionAsync(info.Version);
    }

    private async Task<List<(long, string?)>> ReadVersionAsync(long version)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        var rows = new List<(long, string?)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(version))
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector name = batch.SelectedColumn(1);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((id.GetValue<long>(r), name.IsNull(r) ? null : Encoding.UTF8.GetString(name.GetBytes(r))));
            }
        }

        return rows;
    }

    private int CountFiles(string pattern) =>
        Directory.Exists(_root)
            ? Directory.GetFiles(_root, pattern, SearchOption.AllDirectories).Length
            : 0;

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
                byte[] utf8 = Encoding.UTF8.GetBytes(n);
                name.AppendBytes(utf8);
            }
        }

        return new ManagedColumnBatch(FlatSchema, new ColumnVector[] { id, name }, rows.Length);
    }
}
