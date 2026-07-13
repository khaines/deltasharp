using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
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
    public async Task Delete_ExcludesDeletedRows_WithoutRewritingTheDataFile()
    {
        await CreateDeletionVectorTableAsync(
            Batch((1, "a"), (2, "b"), (3, "c"), (4, "d"), (5, "e")));

        int parquetBefore = CountFiles("*.parquet");

        var delete = new DeltaDelete(new LocalFileSystemBackend(_root));
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
    public async Task Delete_UpdatesResidualNumRecords_OnTheDvCarryingAdd()
    {
        await CreateDeletionVectorTableAsync(Batch((10, "x"), (20, "y"), (30, "z"), (40, "w")));

        var backend = new LocalFileSystemBackend(_root);
        var delete = new DeltaDelete(backend);
        await delete.DeleteAsync(WhereId(id => id == 20));

        // Writer requirement: the DV-carrying add reports the residual physical count (4 − 1 = 3) in stats.
        Snapshot snapshot = await new DeltaLog(backend).LoadSnapshotAsync();
        AddFileAction dvAdd = Assert.Single(snapshot.ActiveFiles.Where(a => a.DeletionVector is not null));
        Assert.NotNull(dvAdd.Stats);
        Assert.Equal(3L, dvAdd.Stats!.NumRecords);
        Assert.Equal(1L, dvAdd.DeletionVector!.Cardinality);
    }

    // ------------------------------------------------------------------ AC2 edge: whole-file delete

    [Fact]
    public async Task Delete_EveryRowInFile_RemovesFileOutright_NoResidualDv()
    {
        await CreateDeletionVectorTableAsync(Batch((1, "a"), (2, "b"), (3, "c")));

        var backend = new LocalFileSystemBackend(_root);
        var delete = new DeltaDelete(backend);
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

        var delete = new DeltaDelete(new LocalFileSystemBackend(_root));
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

        var winner = new DeltaDelete(backend);
        var loser = new DeltaDelete(backend);

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

        var delete = new DeltaDelete(new LocalFileSystemBackend(_root));

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
            add.Stats! with { NumRecords = 3 }, add.Tags, inline);

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
            add.Stats! with { NumRecords = 3 }, add.Tags, poisoned);

        await new DeltaCommitter(backend).CommitAsync(
            readSnapshot,
            new DeltaAction[] { remove, poisonedAdd },
            DeltaReadScope.ReadFiles(new[] { add.Path }));

        await Assert.ThrowsAsync<DeltaReadException>(() => ReadLatestAsync());
    }

    // ------------------------------------------------------------------ helpers

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
