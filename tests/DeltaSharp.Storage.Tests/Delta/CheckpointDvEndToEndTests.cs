using System.Globalization;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// End-to-end proof for issue #527: a checkpoint-seeded deletion vector actually EXCLUDES the deleted rows
/// on a real materialized data read — not just at the descriptor/Describe level. A real DV-enabled table is
/// written, a real on-disk ('u') <c>.bin</c> DV sidecar is produced by <see cref="DeltaDelete"/>, the table
/// is checkpointed carrying that same on-disk DV, the early <c>*.json</c> commits are deleted so ONLY the
/// checkpoint can seed the DV, and the survivors are read back through <see cref="DeltaReadSource"/> and
/// asserted by value — confirming the RoaringBitmap is applied from the checkpoint-reconstructed descriptor.
///
/// <para>The temp root is a fixed, deterministic path (pre-cleaned per run) so no <c>Guid.NewGuid</c> is
/// introduced; the DV file identity is deterministic via <see cref="SeededDeletionVectorIdSource"/>.</para>
/// </summary>
public sealed class CheckpointDvEndToEndTests : IDisposable
{
    private static readonly StructType FlatSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deltasharp-527-cp-dv-e2e");

    public CheckpointDvEndToEndTests()
    {
        SafeDelete(_root);
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => SafeDelete(_root);

    [Fact]
    public async Task CheckpointSeededOnDiskDeletionVector_ExcludesDeletedRows_OnMaterializedRead()
    {
        var backend = new LocalFileSystemBackend(_root);

        // 1. Create a real DV-enabled table (5 rows) and DELETE ids 2 & 4 → a REAL on-disk 'u' .bin DV
        //    sidecar, committed as remove(original) + add(residual, DV) at version 1 (merge-on-read; the
        //    data file is NOT rewritten).
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateDeletionVectorTableAsync(
                FlatSchema, Array.Empty<string>(),
                new[] { Batch((1, "a"), (2, "b"), (3, "c"), (4, "d"), (5, "e")) });
        }

        var delete = new DeltaDelete(
            backend, new DeltaLog(backend), new DeltaCommitter(backend),
            idSource: new SeededDeletionVectorIdSource("cp-dv-e2e"));
        DeleteResult deleted = await delete.DeleteAsync(
            DeltaDeletePredicate.FromRowPredicate((b, r) => b.SelectedColumn(0).GetValue<long>(r) is 2 or 4));
        Assert.Equal(2, deleted.RowsDeleted);
        Assert.True(CountFiles("*.bin") >= 1, "DELETE must have written a real on-disk DV .bin sidecar.");

        // 2. Capture the REAL committed state at v1: the DV descriptor (which points at the on-disk .bin), the
        //    file's PHYSICAL numRecords, and the table protocol/metadata — the inputs to a faithful checkpoint.
        Snapshot committed = await new DeltaLog(backend).LoadSnapshotAsync();
        Assert.Equal(1, committed.Version);
        AddFileAction dvAdd = Assert.Single(committed.ActiveFiles, a => a.DeletionVector is not null);
        DeletionVectorDescriptor dv = dvAdd.DeletionVector!;
        Assert.Equal(DeletionVectorDescriptor.StorageTypeUuidRelative, dv.StorageType); // 'u' on-disk, not inline
        Assert.Equal(2L, dv.Cardinality);
        long physicalRecords = dvAdd.Stats!.NumRecords ?? throw new InvalidOperationException("DV add must carry stats.numRecords.");

        // 3. Write a classic checkpoint at v1 carrying the SAME on-disk DV descriptor and physical numRecords,
        //    plus a trailing txn commit at v2; then DELETE the early *.json commits so the DV lives ONLY in
        //    the checkpoint and the snapshot can be seeded from nothing else.
        CheckpointFixture checkpoint = new CheckpointFixture()
            .Protocol(
                committed.Protocol.MinReaderVersion, committed.Protocol.MinWriterVersion,
                readerFeatures: committed.Protocol.ReaderFeatures.ToArray(),
                writerFeatures: committed.Protocol.WriterFeatures.ToArray())
            .Metadata(
                committed.Metadata.Id, committed.Metadata.SchemaString,
                partitionColumns: committed.Metadata.PartitionColumns.ToArray(),
                configuration: committed.Metadata.Configuration.Select(kv => (kv.Key, kv.Value)).ToArray())
            .Add(
                dvAdd.Path, size: dvAdd.Size, modificationTime: dvAdd.ModificationTime,
                stats: NumRecordsStats(physicalRecords),
                deletionVector: new CheckpointFixture.DvColumns(
                    dv.StorageType, dv.PathOrInlineDv, dv.Offset, dv.SizeInBytes, dv.Cardinality));
        await DeltaTestHarness.WriteCheckpointAsync(backend, 1, checkpoint);
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, 1);
        await DeltaTestHarness.WriteCommitAsync(backend, 2, DeltaTestHarness.Txn("app-cp-dv", 0));

        DeleteJsonCommit(0);
        DeleteJsonCommit(1);
        Assert.False(File.Exists(JsonCommitPath(0)), "early v0 *.json must be gone (checkpoint-only seed).");
        Assert.False(File.Exists(JsonCommitPath(1)), "early v1 *.json must be gone (checkpoint-only seed).");

        // 4a. The snapshot is seeded from the CHECKPOINT (fast path) — the DV survives with its exact identity,
        //     and there is no early *.json left that could have supplied it.
        Snapshot seeded = await new DeltaLog(backend).LoadSnapshotAsync();
        Assert.Equal(2, seeded.Version);
        Assert.Equal(1, seeded.Metrics.CheckpointVersion);
        AddFileAction seededAdd = Assert.Single(seeded.ActiveFiles, a => a.DeletionVector is not null);
        Assert.Equal(dv.UniqueId, seededAdd.DeletionVector!.UniqueId);

        // 4b. Materialize the data through DeltaReadSource: the checkpoint-reconstructed DV's RoaringBitmap is
        //     applied against the on-disk .bin, EXCLUDING ids 2 & 4 — the exact survivors, by value.
        List<(long Id, string? Name)> survivors = await ReadAllAsync();
        Assert.Equal(
            new (long, string?)[] { (1L, "a"), (3L, "c"), (5L, "e") },
            survivors.OrderBy(r => r.Id).ToList());
    }

    private async Task<List<(long Id, string? Name)>> ReadAllAsync()
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
                rows.Add((id.GetValue<long>(r), name.IsNull(r) ? null : Encoding.UTF8.GetString(name.GetBytes(r))));
            }
        }

        return rows;
    }

    // A minimal stats document carrying only the (required-for-DV) physical numRecords — the reader validates
    // the DV's physical positions against this count. Min/max/nullCount are irrelevant to the DV read.
    private static string NumRecordsStats(long numRecords) =>
        string.Create(CultureInfo.InvariantCulture, $$"""{"numRecords":{{numRecords}}}""");

    private void DeleteJsonCommit(long version)
    {
        string path = JsonCommitPath(version);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string JsonCommitPath(long version) =>
        Path.Combine(_root, "_delta_log", version.ToString(CultureInfo.InvariantCulture).PadLeft(20, '0') + ".json");

    private int CountFiles(string pattern) =>
        Directory.Exists(_root)
            ? Directory.GetFiles(_root, pattern, SearchOption.AllDirectories).Length
            : 0;

    private static void SafeDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
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
}
