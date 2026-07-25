using System.Globalization;
using System.Text;
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

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// A single, process-global, fault-injection-driven read-path regression that must run SERIALLY: it drives
/// <see cref="LocalFileSystemBackend.IoFaultHook"/> (a static seam) to reproduce a genuine on-disk TOCTOU
/// against a deletion-vector data file. It lives in its own class in the
/// <see cref="BackendFaultInjectionCollection"/> (the canonical <c>DisableParallelization = true</c> home for
/// the global IoFaultHook seam) so it never races the parallel <see cref="DeltaReadSourceTests"/> — the
/// hook must never leak across concurrent tests.
/// </summary>
[Collection(BackendFaultInjectionCollection.Name)]
public sealed class DeltaReadSourceToctouTests : IDisposable
{
    private static int _counter;
    private readonly string _root;

    private static readonly StructType FlatSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    public DeltaReadSourceToctouTests()
    {
        // Deterministic per-instance root (no Guid/DateTime/Random): ProcessId + a monotonic ordinal.
        long ordinal = System.Threading.Interlocked.Increment(ref _counter);
        _root = Path.Combine(
            AppContext.BaseDirectory,
            string.Create(CultureInfo.InvariantCulture, $"deltaread-toctou-{Environment.ProcessId}-{ordinal}"));
    }

    public void Dispose()
    {
        // Defensive: never let the process-global seam leak past this test, even on an assertion failure.
        LocalFileSystemBackend.IoFaultHook = null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task DvConsumedRowCountMismatch_Toctou_FailsClosedWithoutEchoingDataFilePath()
    {
        // Message hygiene (#653 / DeletionVectorMask.EnsureConsumed — the post-read TOCTOU backstop). A
        // DV-carrying add opens its data file TWICE in ONE logical read (DeltaReadSource.ReadFileAsync):
        //   (1) GetRowCountAsync reads the Parquet FOOTER -> physicalRecords, and the DV is validated against
        //       it and cross-checked against stats.numRecords (:335);
        //   (2) the STREAMED OpenReadAsync decodes the data.
        // If the file is SWAPPED on disk (2-row -> 3-row, same schema) BETWEEN those two opens, the footer
        // still reports 2 (so :335 passes and the DV is validated against 2) but the stream now yields 3, so
        // EnsureConsumed(3) != PhysicalRecords(2) fires. The fix DROPPED the attacker-controllable data-file
        // path from BOTH the method signature and the message (git diff: EnsureConsumed(long, string path) ->
        // EnsureConsumed(long); pre-fix message: $"File '{path}' carries a deletion vector validated against
        // …"). This is the END-TO-END proof that the backstop is genuinely REACHABLE via the public read seam
        // (an earlier handoff wrongly believed it unreachable; a facade-level TOCTOU disproved that) and that
        // the fixed message leaks NO path.
        //
        // Seam: the only interception point is the process-global static LocalFileSystemBackend.IoFaultHook,
        // invoked with a TAG ("read-open") — never the path. The hook's closure captures the data file's
        // absolute path and, on the specific "read-open" that is the STREAMED data read, OVERWRITES that file
        // with a valid 3-row Parquet as a pure SIDE EFFECT and returns null (no injected fault). The correct
        // "read-open" ordinal is SELF-CALIBRATED (not hard-coded): a clean dry-run read counts ReadBatchesAsync's
        // read-opens, and the streamed data read is the LAST one (ReadBatchesAsync reloads the log fully, then
        // per active file opens footer-then-stream); the table is authored deterministically so the ordinal is
        // stable across the two passes.
        const string sentinel = "dvtoctou-att4cker_s3ntinel.parquet";   // CONFINED name; a leak would echo this token

        // ---- Author a table whose SOLE active file is a DV-carrying add at the sentinel path ----
        var backend = new LocalFileSystemBackend(_root);
        try
        {
            // v0: a DV-enabled table (its throwaway "alice" file is tombstoned below so only the sentinel
            // remains active — a single active file makes the streamed read the unambiguous last read-open).
            using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
            {
                await target.CreateDeletionVectorTableAsync(
                    FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "alice")) });
            }

            Snapshot snap0 = await new DeltaLog(backend).LoadSnapshotAsync();
            AddFileAction alice = Assert.Single(snap0.ActiveFiles);

            // A real, CONFINED 2-row data file at the sentinel path (opens + footer-reads cleanly).
            byte[] twoRow = await WriteParquetAsync(FlatBatch((100, "x"), (200, "y")));
            Assert.True(await backend.PutIfAbsentAsync(sentinel, twoRow, CancellationToken.None));

            // Inline DV (no .bin open to perturb the open sequence) masking physical row 0 (cardinality 1),
            // with stats.numRecords = 2 == the honest footer count so the :335 cross-check passes.
            byte[] rawBitmap = RoaringBitmapArray.Serialize(new long[] { 0 });
            DeletionVectorDescriptor inline = DeletionVectorDescriptor.ForInline(rawBitmap, cardinality: 1);
            var remove = new RemoveFileAction(
                alice.Path, DeletionTimestamp: 1, DataChange: true, ExtendedFileMetadata: true,
                alice.PartitionValues, alice.Size, alice.Tags, DeletionVector: null);
            var sentinelAdd = new AddFileAction(
                sentinel, alice.PartitionValues, twoRow.Length, ModificationTime: 1, DataChange: true,
                FileStatistics.Empty with { NumRecords = 2 }, alice.Tags, inline);
            await new DeltaCommitter(backend).CommitAsync(
                snap0, new DeltaAction[] { remove, sentinelAdd }, DeltaReadScope.ReadFiles(new[] { alice.Path }));
        }
        finally
        {
            backend.Dispose();
        }

        string sentinelAbsolutePath = Path.Combine(_root, sentinel);
        Assert.True(File.Exists(sentinelAbsolutePath));   // the swap target must be a real file on disk
        byte[] threeRow = await WriteParquetAsync(FlatBatch((100, "x"), (200, "y"), (300, "z")));

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        long version = (await source.LoadSnapshotAsync(null, null)).Version;

        // ---- PASS 1 (calibration): a clean read; the STREAMED data read is the LAST read-open ----
        int readOpens = 0;
        LocalFileSystemBackend.IoFaultHook = tag =>
        {
            if (tag == "read-open")
            {
                readOpens++;
            }

            return null;   // observe only; inject nothing, mutate nothing
        };
        try
        {
            IReadOnlyList<ColumnBatch> ok = await source.ReadBatchesAsync(version);
            long survivors = 0;
            foreach (ColumnBatch b in ok)
            {
                survivors += b.LogicalRowCount;
            }

            Assert.Equal(1, survivors);   // 2 physical rows, DV masks row 0 -> exactly 1 survivor
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }

        int streamedReadOpen = readOpens;   // the streamed data read is the last of ReadBatchesAsync's opens
        Assert.True(streamedReadOpen >= 2);   // at least footer + stream on the DV data file

        // ---- PASS 2 (attack): swap 2-row -> 3-row exactly on the streamed open (after the footer read) ----
        int seen = 0;
        LocalFileSystemBackend.IoFaultHook = tag =>
        {
            if (tag == "read-open" && ++seen == streamedReadOpen)
            {
                // The footer read (an earlier open) already validated the DV against 2 records; swap the file
                // so the imminent streamed decode yields 3 -> EnsureConsumed(3) != PhysicalRecords(2).
                File.WriteAllBytes(sentinelAbsolutePath, threeRow);
            }

            return null;   // pure side effect; NO injected fault (fires the DV backstop, not a storage error)
        };
        try
        {
            DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(
                () => source.ReadBatchesAsync(version));

            // Load-bearing: the swapped data file's path is NEVER echoed into the message.
            Assert.DoesNotContain("dvtoctou-att4cker_s3ntinel", ex.Message, StringComparison.Ordinal);
            // Bounded, DV-scoped diagnostics remain (the record counts are statistics, not a path/name/cell).
            Assert.Contains("deletion vector", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("on read", ex.Message, StringComparison.Ordinal);
            Assert.Contains("validated against 2", ex.Message, StringComparison.Ordinal);
            Assert.Contains("produced 3", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static ColumnBatch FlatBatch(params (long Id, string? Name)[] rows)
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

    private static async Task<byte[]> WriteParquetAsync(ColumnBatch batch)
    {
        using var buffer = new MemoryStream();
        await new ParquetFileWriter().WriteWithStatisticsAsync(
            buffer, FlatSchema, new[] { batch }, StatisticsPolicy.Default, CancellationToken.None);
        return buffer.ToArray();
    }
}
