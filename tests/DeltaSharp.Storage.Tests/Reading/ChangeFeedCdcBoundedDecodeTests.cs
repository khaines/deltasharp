using System.Diagnostics;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Tests.Delta.DeletionVectors;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// Pinned deterministic regression for #647: an adversarial <c>cdc</c> input the CDF read-door fuzz strategy
/// produces (<c>DELTASHARP_TEST_SEED=35</c>, byte-flip strategy, iteration 14 — a 951-byte cdc file with an
/// intact footer whose decode Parquet.Net 6.1.0 drove into a non-terminating, cancellation-ignoring CPU loop)
/// must now fail closed with a typed <see cref="DeltaStorageException"/> WITHIN the bounded-decode budget,
/// not hang (design §5.4 C-DECODE — the bounded wall-clock decode ceiling).
/// <para>
/// The pin is DERIVED, not sacred: it is re-created by replaying the shared <see cref="CdcFuzzMutation"/>
/// stream, so when the base cdc file's bytes change the pin is re-derived against the CURRENT file (this is
/// exactly why the mutation strategy is shared with the live fuzz). The original repro
/// (<c>seed=42, iteration=148</c>, a 957-byte file) stopped driving the non-terminating decode once #730 made
/// the string column emit its declared (non-null) footer repetition, shrinking the base cdc file to 951 bytes;
/// the seed/iteration above is the re-minimized input that reproduces the identical bounded-decode fail-closed
/// behavior against the post-#730 file.
/// </para>
/// </summary>
/// <remarks>
/// The cdc file's bytes are reproduced deterministically by rebuilding the same table the fuzz builds (create
/// → enable CDF → append three rows → partial delete, materializing one explicit cdc file) and replaying the
/// same seeded mutation stream (seed 35) to iteration 14. The read is driven at the shared <see cref="ParquetFileReader"/>
/// tier (the engine every Parquet read — snapshot, checkpoint, cdc — shares) so the LOW decode budget seam is
/// reachable, keeping the test fast and deterministic; the real read door uses the same reader with the
/// conservative default budget.
/// </remarks>
[Collection(DeletionVectorFileTestCollection.Name)]
public sealed class ChangeFeedCdcBoundedDecodeTests : IDisposable
{
    private const string FuzzScope = "ChangeFeedCdcFuzzTests";

    private static readonly TimeSpan TestDecodeBudget = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task ParquetReader_FailsClosedWithinBudget_OnMinimized647CdcInput()
    {
        byte[] cdc = await BuildFuzzCdcFileAsync();

        // The #647 minimized input: seed 35, the fuzz's scope-combined RNG, replayed to iteration 14, via the
        // SHARED mutation helper both suites call (so this pin cannot drift off the live fuzz strategy).
        // Re-derived against the post-#730 base cdc file (see the type-level remarks); the pre-#730 input
        // (seed 42, iteration 148) no longer drives the non-terminating decode once the file shrank to 951 B.
        byte[] mutated = CdcFuzzMutation.ReplayToIteration(cdc, baseSeed: 35, scope: FuzzScope, iteration: 14);

        // Pin the exact minimized shape by CONTENT (SHA-256), not merely length: a length check alone would
        // accept a different 951-byte mutation if the (deterministic) build or replay drifted.
        Assert.Equal(951, mutated.Length);
        Assert.Equal(
            // Re-derived under Parquet.Net 6.1.0 (#832): the library's own footer/page bytes shifted, so the
            // fixture's exact bytes changed while its length and structure invariants above are unchanged.
            "6f873b8a2dd0b8d1141d72b59ed8326b66f563557893ed3dd72d2d4dedfc4704",
            Sha256Hex(mutated));

        // Read the (unmutated) cdc data schema so we can decode the mutated file through the shared reader.
        StructType schema;
        using (var original = new MemoryStream(cdc, writable: false))
        {
            schema = await new ParquetFileReader().ReadDataSchemaAsync(original, CancellationToken.None);
        }

        using var telemetry = new DeltaSharp.Storage.Diagnostics.DeltaStorageTelemetry();
        using var storageMeter = new DeltaSharp.Storage.Tests.Delta.MeterCapture(telemetry.StorageMeter);
        var reader = new ParquetFileReader(
            new ParquetDecodeLimits(decodeTimeBudget: TestDecodeBudget),
            telemetry: telemetry,
            dataFileDecoder: new BoundedDecoder(strandCountCap: 16, execution: DecodeExecution.Pool));
        var stopwatch = Stopwatch.StartNew();
        Exception? thrown = await RunWatchdoggedAsync(async () =>
        {
            using var stream = new MemoryStream(mutated, writable: false);
            await foreach (ColumnBatch batch in reader.ReadAsync(
                stream, schema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None))
            {
                _ = batch.LogicalRowCount;
            }
        });
        stopwatch.Stop();

        // Non-vacuous proof the ROW-GROUP/page-decode bounded path fired (not the generic malformed-footer
        // fast-fail): (a) the DISTINCT DecodeBudgetExceeded kind — unique to the bounded-decode timeout, so
        // deleting the row-group BoundedDecode wrapper turns this red (the input hangs → watchdog fires, or the
        // decode never terminates as anything else); and (b) the read ran AT LEAST the budget — a fast-fail
        // corrupt-detection would return well under it. Without the policy this input runs > 4 minutes.
        DeltaStorageException ex = Assert.IsType<DeltaStorageException>(thrown);
        Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, ex.Kind);
        Assert.True(stopwatch.Elapsed >= TestDecodeBudget, $"expected the read to run at least the budget, took {stopwatch.Elapsed}.");
        // No tight wall-clock upper bound here: termination is already guaranteed by the 20s watchdog
        // (a true #647 hang runs > 4 min and trips it), and fail-closed by the DecodeBudgetExceeded kind
        // above. A fixed "fast" ceiling only adds CI flake, because the timeout-delivery continuation
        // competes with the CPU-spinning decode for thread-pool threads on a constrained runner.

        // The #647 door + STAGE discriminator on the REAL read path (this real, fuzzer-found input drives the
        // forced footer/row-group-reader init inside the OPEN into the non-terminating loop): the emitted
        // decode.budget_exceeded counter must carry door=data_file AND stage=open. Asserted on the REAL read
        // path (not a stepping-clock fake), so it is red if the door/stage labels regress.
        DeltaSharp.Storage.Tests.Delta.MeterCapture.Measurement metric =
            Assert.Single(storageMeter.ForInstrument("deltasharp.storage.decode.budget_exceeded"));
        Assert.Equal("data_file", metric.Tags["deltasharp.decode.door"]);
        Assert.Equal("open", metric.Tags["deltasharp.decode.stage"]);
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    // Rebuilds the same explicit cdc file the CDF read-door fuzz mutates: create empty → enable CDF → append
    // three east rows → partial delete of two ids, which materializes exactly one cdc file (the explicit CDF
    // path). Returns its raw bytes.
    private async Task<byte[]> BuildFuzzCdcFileAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "ds-cdf-647-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var table = new CdfTable(root);
        await table.CreateEmptyAsync();
        await table.EnableCdfAsync();
        await table.AppendAsync(new CdfRow[] { new(1, "east", 10), new(2, "east", 20), new(3, "east", 30) });
        _ = await table.DeleteAsync(new long[] { 1, 2 });

        IReadOnlyList<string> cdcFiles = table.CdcFilePaths();
        Assert.NotEmpty(cdcFiles);
        return await File.ReadAllBytesAsync(table.AbsolutePath(cdcFiles[0]));
    }

    private static async Task<Exception?> RunWatchdoggedAsync(Func<Task> operation)
    {
        Task<Exception?> run = Task.Run(async () =>
        {
            try
            {
                await operation();
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        if (await Task.WhenAny(run, Task.Delay(Watchdog)) != run)
        {
            Assert.Fail(
                $"The cdc decode did not terminate within {Watchdog.TotalSeconds:0}s — the bounded-decode policy "
                + "failed to release the caller (regression of #647).");
        }

        return await run;
    }
}
