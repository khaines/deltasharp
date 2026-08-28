using System.Diagnostics;
using System.Threading;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// The two <see cref="BoundedDecodeTests"/> cases whose assertions are genuinely time-based and therefore
/// raced the deadline under full-parallel suite load (#869). They live in the non-parallel
/// <see cref="TimingSensitiveDecodeCollection"/> so no sibling test starves the shared
/// <see cref="ThreadPool"/> while they run; both still exercise the REAL bounded-decode machinery (real
/// threads, real wall-clock budgets) — nothing about what they verify is weakened.
/// </summary>
[Collection(TimingSensitiveDecodeCollection.Name)]
public sealed class BoundedDecodeTimingSensitiveTests
{
    // A generous test watchdog: the policy releases the caller at the (much smaller) budget, so this only trips
    // on a genuine regression (the policy failing to release the caller), converting it to a test failure.
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

    private static readonly StructType DataSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    [Fact]
    public async Task PerOperationBudget_DoesNotChargeConsumerTimeBetweenRowGroups()
    {
        // Critical #2a — the decode budget bounds only the DECODE OPERATIONS themselves (per open, per
        // row-group decode), NEVER the streaming iterator's `yield return` suspensions. The Round-2 aggregate
        // deadline was created at ReadAsync entry and spanned every MoveNextAsync gap, so downstream engine work
        // between batches (shuffle write, spill, join build, sink backpressure) was charged to the "decode"
        // budget and a healthy query failed mid-scan with a false DecodeBudgetExceeded. Proven with a genuinely
        // SLOW consumer: a multi-row-group file read under a SHORT budget, where the consumer sleeps far LONGER
        // than the budget between batches. Each fast (valid) row-group decode wins its own per-op budget, so the
        // read completes and yields ALL rows — an aggregate-deadline model would instead trip on the second
        // MoveNextAsync (consumer time > budget). Real wall-clock (no injected clock) so the consumer sleep is
        // truly charged if the scoping regressed.
        //
        // #869: this premise (each row-group decode finishes well under the 200 ms per-op budget) only fails when
        // the shared ThreadPool is starved by the rest of the suite so a HEALTHY decode's execution/await-resume
        // is delayed past the budget — an artifact of racing wall-clock under sibling-thread contention, NOT a
        // scoping regression. Isolating the test in the non-parallel collection removes that contention so the
        // real-wall-clock design (deliberately not clock-injected) stays intact and non-flaky.
        byte[] file = await BuildMultiRowGroupFileAsync(rows: 6, rowsPerGroup: 1);
        var reader = new ParquetFileReader(new ParquetDecodeLimits(decodeTimeBudget: TimeSpan.FromMilliseconds(200)));

        long total = 0;
        int batches = 0;
        using var stream = new MemoryStream(file, writable: false);
        Task<(long, int)> run = Task.Run(async () =>
        {
            long t = 0;
            int b = 0;
            await foreach (ColumnBatch batch in reader.ReadAsync(
                stream, DataSchema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None))
            {
                t += batch.LogicalRowCount;
                b++;
                // Consumer time far exceeding the decode budget — must NOT be charged to the decode.
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }

            return (t, b);
        });

        if (await Task.WhenAny(run, Task.Delay(Watchdog)) != run)
        {
            Assert.Fail("A healthy slow-consumer read did not complete — per-op budget scoping regression (#2a).");
        }

        (total, batches) = await run;
        Assert.Equal(6, total); // ALL rows yielded despite the consumer sleeping > budget between every batch
        Assert.Equal(6, batches);
    }

    [Fact]
    public async Task Starvation_HealthyDecodeStillSucceeds_WhileStrandsExist_OnIsolatedDecoder()
    {
        // I1 — THE headline Critical: with the Round-1 shared, count-capped scheduler, a handful of
        // non-terminating strands pinned every execution slot, so a QUEUED healthy decode never ran — a
        // permanent process-wide outage from as little as ONE crafted file on a small pod. The redesign runs
        // every decode on the pool / its own thread behind a byte-aware cap, so a healthy decode submitted while
        // N strands exist still executes immediately and SUCCEEDS. HOST-INDEPENDENT (High #9): strandCount is
        // ProcessorCount + 1 (so it exceeds ANY host's core count — invisible on ≥32-core hosts otherwise) and
        // the cap leaves exactly one free slot for the healthy decode. Every strand creation is watchdog-wrapped
        // so a regression is RED, not a stuck job on a narrow host.
        //
        // #869: the strands pin ProcessorCount+1 pool threads on gate.Wait, so the healthy decode needs the pool
        // to inject one more thread. Under full-parallel suite load that injection competes with dozens of other
        // blocked sibling threads and can miss the 20 s watchdog even though the DECODER admitted the healthy
        // decode correctly — a ThreadPool-injection race, not a starvation regression. Two things fix it
        // deterministically: (1) this test runs in the non-parallel collection, so no sibling competes for
        // thread injection; (2) we raise the pool's minimum worker-thread count for the duration so the
        // ProcessorCount+1 strand threads PLUS the one healthy-decode thread are created on demand WITHOUT
        // injection throttling. The real starvation the test asserts (strands genuinely pinning slots via
        // gate.Wait) is unchanged — only the incidental thread-injection latency is removed.
        int strandCount = Environment.ProcessorCount + 1;
        int cap = strandCount + 1; // room for the strands PLUS the one healthy decode

        ThreadPool.GetMinThreads(out int minWorker, out int minIo);
        // Guarantee the strands' threads and the healthy decode's thread exist on demand (no injection stall).
        ThreadPool.SetMinThreads(Math.Max(minWorker, cap + Environment.ProcessorCount + 8), minIo);
        try
        {
            var decoder = new BoundedDecoder(strandCountCap: cap);
            using var gate = new ManualResetEventSlim(initialState: false);

            for (int i = 0; i < strandCount; i++)
            {
                DeltaStorageException timedOut = await RunWatchdoggedThrowsAsync(() =>
                    decoder.RunAsync<int>(
                        _ => { gate.Wait(); return Task.FromResult(0); },
                        TimeSpan.FromMilliseconds(100),
                        static _ => DeltaStorageException.DecodeBudgetExceeded("strand"),
                        CancellationToken.None));
                Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, timedOut.Kind);
            }

            await WaitForAsync(() => decoder.DetachedDecodeCount == strandCount);

            // The invariant: a healthy decode submitted while ProcessorCount+1 strands exist STILL executes and
            // succeeds.
            int healthy = await RunHealthyAsync(decoder);
            Assert.Equal(42, healthy);

            gate.Set();
            await WaitForAsync(() => decoder.DetachedDecodeCount == 0);
        }
        finally
        {
            ThreadPool.SetMinThreads(minWorker, minIo);
        }

        static async Task<int> RunHealthyAsync(BoundedDecoder decoder)
        {
            Task<int> run = decoder.RunAsync(
                _ => Task.FromResult(42),
                TimeSpan.FromSeconds(5),
                static _ => DeltaStorageException.DecodeBudgetExceeded("healthy decode must not time out"),
                CancellationToken.None);
            if (await Task.WhenAny(run, Task.Delay(Watchdog)) != run)
            {
                Assert.Fail("A healthy decode did not run while strands existed (I1 starvation regression).");
            }

            return await run;
        }
    }

    // Polls a condition up to the watchdog so a strand-accounting assertion (drain / cap reached) is
    // deterministic without a tight sleep on the exact transition instant.
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > Watchdog)
            {
                Assert.Fail("The expected bounded-decode state was not reached within the watchdog.");
            }

            await Task.Delay(10);
        }
    }

    // Runs a bounded decode expected to throw DeltaStorageException, under the watchdog so a strand-creation
    // regression is RED (a stuck job) rather than a hang (High #9).
    private static async Task<DeltaStorageException> RunWatchdoggedThrowsAsync(Func<Task> operation)
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
                $"A bounded decode did not settle within {Watchdog.TotalSeconds:0}s — strand-creation regression.");
        }

        return Assert.IsType<DeltaStorageException>(await run);
    }

    private static async Task<byte[]> BuildMultiRowGroupFileAsync(int rows, int rowsPerGroup)
    {
        MutableColumnVector idVector = ColumnVectors.Create(DataTypes.LongType, rows);
        MutableColumnVector nameVector = ColumnVectors.Create(DataTypes.StringType, rows);
        for (int i = 0; i < rows; i++)
        {
            idVector.AppendValue((long)i);
            nameVector.AppendBytes(System.Text.Encoding.UTF8.GetBytes("row-" + i));
        }

        var batch = new ManagedColumnBatch(DataSchema, new ColumnVector[] { idVector, nameVector }, rows);
        var writer = new ParquetFileWriter(rowGroupRowLimit: rowsPerGroup);
        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, DataSchema, new[] { batch }, CancellationToken.None);
        return stream.ToArray();
    }
}
