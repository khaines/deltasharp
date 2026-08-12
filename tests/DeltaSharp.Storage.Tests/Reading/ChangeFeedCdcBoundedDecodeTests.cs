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
/// Pinned deterministic regression for #647: the exact adversarial <c>cdc</c> input the CDF read-door fuzz
/// flagged (<c>DELTASHARP_TEST_SEED=42</c>, byte-flip strategy, iteration 148 — a 957-byte cdc file with an
/// intact footer whose decode Parquet.Net 6.0.3 drove into a non-terminating, cancellation-ignoring CPU loop)
/// must now fail closed with a typed <see cref="DeltaStorageException"/> WITHIN the bounded-decode budget,
/// not hang (design §5.4 C-DECODE "never hangs").
/// </summary>
/// <remarks>
/// The cdc file's bytes are reproduced deterministically by rebuilding the same table the fuzz builds (create
/// → enable CDF → append three rows → partial delete, materializing one explicit cdc file) and replaying the
/// same seeded mutation stream to iteration 148. The read is driven at the shared <see cref="ParquetFileReader"/>
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

        // The #647 minimized input: seed 42, the fuzz's scope-combined RNG, replayed to iteration 148.
        byte[] mutated = ReplayFuzzMutationToIteration(cdc, baseSeed: 42, iteration: 148);
        Assert.Equal(957, mutated.Length); // pin the exact minimized shape the issue recorded

        // Read the (unmutated) cdc data schema so we can decode the mutated file through the shared reader.
        StructType schema;
        using (var original = new MemoryStream(cdc, writable: false))
        {
            schema = await new ParquetFileReader().ReadDataSchemaAsync(original, CancellationToken.None);
        }

        var reader = new ParquetFileReader(new ParquetDecodeLimits(decodeTimeBudget: TestDecodeBudget));
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

        // (a) a typed fail-closed exception, and (b) it returns well under the real default budget (30s) —
        // without the bounded-decode policy this input runs > 4 minutes.
        DeltaStorageException ex = Assert.IsType<DeltaStorageException>(thrown);
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"expected a fast fail-closed, took {stopwatch.Elapsed}.");
    }

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

    // Replays the EXACT mutation stream the CDF read-door fuzz uses (same seed combine, same Mutate strategy)
    // up to and including the given iteration, returning that iteration's mutated bytes. Byte-for-byte
    // identical to ChangeFeedCdcFuzzTests's private Mutate so the pin tracks the real fuzz.
    private static byte[] ReplayFuzzMutationToIteration(byte[] original, int baseSeed, int iteration)
    {
        var random = new Random(TestSeed.Combine(baseSeed, FuzzScope));
        byte[] mutated = original;
        for (int i = 0; i <= iteration; i++)
        {
            mutated = Mutate(original, random);
        }

        return mutated;
    }

    private static byte[] Mutate(byte[] original, Random random)
    {
        switch (random.Next(4))
        {
            case 0:
                byte[] noise = new byte[random.Next(0, original.Length + 8)];
                random.NextBytes(noise);
                return noise;
            case 1:
                return original[..random.Next(0, original.Length)];
            case 2:
                byte[] flipped = (byte[])original.Clone();
                int flips = random.Next(1, 8);
                for (int f = 0; f < flips; f++)
                {
                    flipped[random.Next(flipped.Length)] ^= (byte)(1 << random.Next(8));
                }

                return flipped;
            default:
                byte[] appended = new byte[original.Length + random.Next(1, 32)];
                original.CopyTo(appended, 0);
                for (int k = original.Length; k < appended.Length; k++)
                {
                    appended[k] = (byte)random.Next(256);
                }

                return appended;
        }
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
