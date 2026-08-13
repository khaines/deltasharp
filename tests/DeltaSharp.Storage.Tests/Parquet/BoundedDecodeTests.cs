using System.Diagnostics;
using System.Linq;
using System.Threading;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Tests.Delta;
using DeltaSharp.Types;
using Parquet;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Unit + integration coverage for the shared <see cref="BoundedDecode"/> wall-clock decode policy and its
/// wiring into the data-file door (design §5.4 C-DECODE — the bounded wall-clock decode ceiling;
/// #647/#699/#716). Parquet.Net 6.0.3 can be driven by a single corrupted byte into unbounded,
/// cancellation-ignoring work; the policy converts that non-termination into a deterministic, typed
/// fail-closed exception within a bounded time, and bounds the residual of the abandoned work.
/// </summary>
public sealed class BoundedDecodeTests
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
    public async Task RunAsync_ReturnsResult_WhenWorkFinishesFirst()
    {
        int result = await new BoundedDecoder(strandCountCap: 4).RunAsync(
            _ => Task.FromResult(42),
            TimeSpan.FromSeconds(5),
            static _ => new InvalidOperationException("must not time out"),
            CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunAsync_PropagatesWorkException_Unwrapped_WhenWorkFinishesFirst()
    {
        // A typed fail-closed exception the work itself throws must propagate unwrapped — never remapped to the
        // timeout exception. This is the property that keeps UnsupportedFeature / CorruptData contracts intact.
        DeltaStorageException thrown = await Assert.ThrowsAsync<DeltaStorageException>(() =>
            new BoundedDecoder(strandCountCap: 4).RunAsync<int>(
                _ => throw DeltaStorageException.UnsupportedFeature("valid but unsupported"),
                TimeSpan.FromSeconds(5),
                static _ => DeltaStorageException.CorruptData("must not be surfaced"),
                CancellationToken.None));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, thrown.Kind);
        Assert.Equal("valid but unsupported", thrown.Message);
    }

    [Fact]
    public async Task RunAsync_ThrowsOnTimeout_WhenWorkIgnoresTokenAndNeverTerminatesInBudget()
    {
        // The work IGNORES its token and blocks past the budget (a detached decode the runtime cannot abort).
        // The caller must be released at the budget with the caller-supplied fail-closed exception, well under
        // the watchdog.
        var stopwatch = Stopwatch.StartNew();
        var thrown = await RunWatchdoggedAsync(() =>
            new BoundedDecoder(strandCountCap: 4).RunAsync<int>(
                _ =>
                {
                    Thread.Sleep(TimeSpan.FromSeconds(3)); // ignores the token entirely
                    return Task.FromResult(1);
                },
                TimeSpan.FromMilliseconds(200),
                static _ => DeltaStorageException.DecodeBudgetExceeded("bounded-decode budget exceeded"),
                CancellationToken.None));
        stopwatch.Stop();

        DeltaStorageException ex = Assert.IsType<DeltaStorageException>(thrown);
        Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, ex.Kind);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"expected release near the budget, took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_SurfacesCallerCancellation_NotTimeout()
    {
        // Caller cancellation is control flow: it must surface OperationCanceledException, NEVER be masked as
        // the timeout exception — even though the work ignores the token and the budget is long.
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var thrown = await RunWatchdoggedAsync(() =>
            new BoundedDecoder(strandCountCap: 4).RunAsync<int>(
                _ =>
                {
                    Thread.Sleep(TimeSpan.FromSeconds(3));
                    return Task.FromResult(1);
                },
                TimeSpan.FromSeconds(30),
                static _ => new InvalidOperationException("timeout must not win over caller cancellation"),
                cts.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
    }

    [Fact]
    public void ParquetDecodeLimits_RejectsNonPositiveDecodeBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParquetDecodeLimits(decodeTimeBudget: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParquetDecodeLimits(decodeTimeBudget: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void ParquetDecodeLimits_RejectsBudgetAboveTheAcceptedCeiling()
    {
        // Upper-bound validation (MEDIUM finding): a budget beyond MaxBudget disables the DoS control, so it
        // must fail fast at construction with an explicit paramName — never surface later as a raw Task.Delay
        // ArgumentOutOfRangeException mid-decode.
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ParquetDecodeLimits(decodeTimeBudget: BoundedDecode.MaxBudget + TimeSpan.FromSeconds(1)));
        Assert.Equal("decodeTimeBudget", ex.ParamName);
    }

    [Fact]
    public void ParquetDecodeLimits_DefaultDecodeBudget_IsTheSharedDefault()
    {
        Assert.Equal(BoundedDecode.DefaultBudget, ParquetDecodeLimits.Default.DecodeTimeBudget);
    }

    [Fact]
    public async Task DataFileDoor_FailsClosedWithinBudget_OnLastFooterByteFlip()
    {
        // #699 on the DATA-FILE door. A single bit flip in the last footer byte (the terminal Thrift STOP of
        // FileMetaData, index len-9 before the 4-byte footer_length + PAR1 magic) drives ParquetReader
        // .CreateAsync into unbounded, token-ignoring work. The bounded-time open must fail closed with the
        // DISTINCT typed DecodeBudgetExceeded (NOT CorruptData — a wall-clock timeout is a resource fault, not
        // proof the bytes are corrupt) within the budget rather than hanging the read.
        byte[] file = await BuildDataFileAsync();
        byte[] mutated = (byte[])file.Clone();
        mutated[^9] ^= 1;

        var budget = TimeSpan.FromMilliseconds(300);
        var reader = new ParquetFileReader(
            new ParquetDecodeLimits(decodeTimeBudget: budget),
            dataFileDecoder: IsolatedDataFileDecoder());
        var stopwatch = Stopwatch.StartNew();
        var thrown = await RunWatchdoggedAsync(() => ReadAllAsync(reader, mutated));
        stopwatch.Stop();

        DeltaStorageException ex = Assert.IsType<DeltaStorageException>(thrown);
        // The signal UNIQUE to the bounded-decode path (not the generic CorruptData a normal malformed footer
        // maps to): reverting the bounded-decode wrapper would either hang (watchdog fires) or, if the decode
        // somehow terminated, surface CorruptData — either way this assertion fails.
        Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, ex.Kind);
        Assert.True(stopwatch.Elapsed >= budget, $"expected the read to run at least the budget, took {stopwatch.Elapsed}.");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"expected a fast fail-closed, took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task DataFileDoor_Timeout_IncrementsDecodeBudgetExceededCounter_WithDataFileDoor()
    {
        // Observability: every deadline trip must emit the door-dimensioned decode.budget_exceeded metric so an
        // operator can alert on a decode-DoS without a code-level repro. Asserts the counter increments on the
        // REAL data-file door path with door = data_file (and no untrusted content on the metric).
        byte[] file = await BuildDataFileAsync();
        byte[] mutated = (byte[])file.Clone();
        mutated[^9] ^= 1;

        using var telemetry = new DeltaSharp.Storage.Diagnostics.DeltaStorageTelemetry();
        using var meters = new MeterCapture(telemetry.StorageMeter);
        var reader = new ParquetFileReader(
            new ParquetDecodeLimits(decodeTimeBudget: TimeSpan.FromMilliseconds(300)),
            timeProvider: null,
            telemetry: telemetry,
            dataFileDecoder: IsolatedDataFileDecoder());

        var thrown = await RunWatchdoggedAsync(() => ReadAllAsync(reader, mutated));

        Assert.IsType<DeltaStorageException>(thrown);
        MeterCapture.Measurement metric = Assert.Single(meters.ForInstrument("deltasharp.storage.decode.budget_exceeded"));
        Assert.Equal(1, metric.Value);
        Assert.Equal("data_file", metric.Tags["deltasharp.decode.door"]);
        // The byte-flip is a footer/open defect, so the OPEN-stage discriminator distinguishes it from a
        // non-terminating row-group page decode (both otherwise land on door=data_file).
        Assert.Equal("open", metric.Tags["deltasharp.decode.stage"]);
    }

    [Fact]
    public async Task Admission_RejectsBeyondCap_FailFast_OnIsolatedDecoder()
    {
        // The admission cap (CRITICAL fix #2): once strandCountCap decodes are stranded past their
        // deadline, a NEW decode is rejected fail-fast with DecodeCapacityExhaustedException WITHOUT starting,
        // so the stranded-decode residual can never grow without bound. Exercised on an ISOLATED decoder with
        // caps of 1 so the assertion is deterministic and never touches the (widened) shared tier.
        var decoder = new BoundedDecoder(strandCountCap: 1);
        using var gate = new ManualResetEventSlim(initialState: false);

        // Strand one decode: it ignores its token and blocks on the gate past a tiny budget, so RunAsync times
        // out and detaches it (DetachedDecodeCount → 1 = cap). It DRAINS when the test releases the gate.
        var firstTimeout = await Assert.ThrowsAsync<DeltaStorageException>(() =>
            decoder.RunAsync<int>(
                _ => { gate.Wait(); return Task.FromResult(1); },
                TimeSpan.FromMilliseconds(100),
                static _ => DeltaStorageException.DecodeBudgetExceeded("strand"),
                CancellationToken.None));
        Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, firstTimeout.Kind);
        await WaitForAsync(() => decoder.DetachedDecodeCount == 1);

        // The cap is now full: a new decode is rejected fail-fast WITHOUT starting — proven by the work
        // delegate never running (started stays false) even though a huge budget would otherwise let it run.
        bool started = false;
        DecodeCapacityExhaustedException saturated = await Assert.ThrowsAsync<DecodeCapacityExhaustedException>(() =>
            decoder.RunAsync<int>(
                _ => { started = true; return Task.FromResult(2); },
                TimeSpan.FromSeconds(30),
                static _ => DeltaStorageException.DecodeBudgetExceeded("must not time out"),
                CancellationToken.None));
        Assert.False(started, "a rejected decode must not start its work.");

        // MEDIUM — the rejection message must be TRUTHFUL: it reports the STRANDED residual (bytes + count)
        // that is full of permanent strands, distinguishing a genuine strand-saturated door from healthy
        // in-flight load (which is never charged here). Here the single strand slot is full, so the message
        // reports strandedStrands=1/1.
        Assert.Contains("strandedStrands=1/1", saturated.Message);
        Assert.Contains("Healthy in-flight decodes are never charged here", saturated.Message);

        // Release the strand so it drains (residual is reclaimed as the work finally terminates), then a fresh
        // decode is admitted again — the cap is a live gate, not a permanent latch.
        gate.Set();
        await WaitForAsync(() => decoder.DetachedDecodeCount == 0);
        int ok = await decoder.RunAsync(
            _ => Task.FromResult(7),
            TimeSpan.FromSeconds(5),
            static _ => DeltaStorageException.DecodeBudgetExceeded("must not time out"),
            CancellationToken.None);
        Assert.Equal(7, ok);
    }

    [Fact]
    public async Task AbandonedSuccessfulResult_IsDisposed_ViaHook()
    {
        // The disposal hook for a LATE win (HIGH finding): when the work completes SUCCESSFULLY after the
        // deadline (a non-terminating decode that eventually wins), its result — a ParquetReader that owns its
        // input stream — would leak. The onAbandonedResult disposer must dispose it. Exercised on an isolated
        // decoder with a disposable stand-in whose Dispose is observable.
        var decoder = new BoundedDecoder(strandCountCap: 4);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new ObservableDisposable(disposed);

        var thrown = await Assert.ThrowsAsync<DeltaStorageException>(() =>
            decoder.RunAsync<ObservableDisposable>(
                _ => { Thread.Sleep(TimeSpan.FromMilliseconds(400)); return Task.FromResult(resource); },
                TimeSpan.FromMilliseconds(100),
                static _ => DeltaStorageException.DecodeBudgetExceeded("late win"),
                CancellationToken.None,
                onAbandonedResult: r => r.Dispose()));
        Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, thrown.Kind);

        // The late (post-deadline) success is disposed via the hook — not leaked.
        Task completed = await Task.WhenAny(disposed.Task, Task.Delay(Watchdog));
        Assert.True(completed == disposed.Task, "the abandoned successful result was not disposed within the watchdog.");
        Assert.True(resource.IsDisposed);
    }

    [Fact]
    public async Task DataFileDoor_ReadsCleanly_UnderTheBoundedDecodePolicy()
    {
        // A well-formed file must still decode correctly with the policy in place (the budget wraps the open
        // and every row-group decode without changing the result on valid input).
        byte[] file = await BuildDataFileAsync();
        var reader = new ParquetFileReader(new ParquetDecodeLimits(decodeTimeBudget: TimeSpan.FromSeconds(5)));
        using var stream = new MemoryStream(file, writable: false);
        long total = 0;
        await foreach (ColumnBatch batch in reader.ReadAsync(
            stream, DataSchema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None))
        {
            total += batch.LogicalRowCount;
        }

        Assert.Equal(500, total);
    }

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
        int strandCount = Environment.ProcessorCount + 1;
        int cap = strandCount + 1; // room for the strands PLUS the one healthy decode
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

    [Fact]
    public void StrandBurst_AllRejectedFailFast_WhenResidualFullOfStrands_CountBranch()
    {
        // The strand admission gate under the charge-at-DETACH model (Round-6): healthy in-flight is NEVER
        // throttled, so the cap applies ONLY to STRANDS. Once the door's stranded residual is full (strand COUNT
        // == cap), a concurrent BURST of NEW admissions must ALL be rejected fail-fast with the DISTINCT
        // DecodeCapacityExhaustedException — WITHOUT starting the work — and each rejection must fire
        // onWorkSettled EXACTLY ONCE (the lease-leak fix on the capacity-rejection path). DETERMINISTIC: the
        // decoder is first pre-filled to its cap with gated strands, then the burst rendezvouses on a Barrier
        // across DEDICATED THREADS and every call is rejected because the residual is already full.
        const int cap = 4;
        const int burst = 32;

        var decoder = new BoundedDecoder(strandCountCap: cap);
        using var strandGate = new ManualResetEventSlim(initialState: false);

        // Pre-fill the residual to the cap with gated, never-terminating strands.
        FillStrandsToCap(decoder, cap, strandGate);
        SpinWaitFor(() => decoder.DetachedDecodeCount == cap);

        using var barrier = new Barrier(burst);
        int rejected = 0;
        int started = 0;
        int settled = 0;

        var threads = new Thread[burst];
        for (int t = 0; t < burst; t++)
        {
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    decoder.RunAsync<int>(
                        _ => { Interlocked.Increment(ref started); return Task.FromResult(1); },
                        TimeSpan.FromSeconds(30),
                        static _ => DeltaStorageException.DecodeBudgetExceeded("must not run"),
                        CancellationToken.None,
                        onWorkSettled: () => Interlocked.Increment(ref settled)).GetAwaiter().GetResult();
                }
                catch (DecodeCapacityExhaustedException)
                {
                    Interlocked.Increment(ref rejected);
                }
            })
            {
                IsBackground = true,
                Name = $"burst-count-{t}",
            };
            threads[t].Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(Watchdog), "a burst thread did not settle within the watchdog.");
        }

        Assert.Equal(burst, rejected); // ALL rejected fail-fast (residual full of strands)
        Assert.Equal(0, started); // no rejected decode started its work
        Assert.Equal(burst, settled); // onWorkSettled fired EXACTLY once per rejected call (no lease leak)
        Assert.Equal(cap, decoder.DetachedDecodeCount); // still exactly `cap` strands (the burst added none)

        strandGate.Set();
        SpinWaitFor(() => decoder.DetachedDecodeCount == 0);
    }

    [Fact]
    public async Task StrandBurst_AllRejectedFailFast_WhenResidualFullOfStrands_ByteBranch()
    {
        // The BYTE branch of the strand admission gate (Round-6 charge-at-detach): the load-bearing memory bound
        // is the stranded-residual BYTES. With a generous count cap but a residual budget that fits only two
        // maximal-footprint strands, once two byte-charging strands detach (strandedBytes >= budget) a
        // concurrent BURST of new admissions must ALL be rejected fail-fast by the BYTE branch — even though the
        // count cap is nowhere near reached — each firing onWorkSettled exactly once.
        const long footprint = 64L * 1024 * 1024;
        const long budget = footprint * 2; // fits exactly two maximal strands
        const int burst = 24;

        var decoder = new BoundedDecoder(
            strandCountCap: 1000, // count cap deliberately far from binding
            residualBudgetBytes: budget,
            maxFootprintBytes: footprint);
        using var strandGate = new ManualResetEventSlim(initialState: false);

        // Two byte-charging strands fill the residual budget (each charges its full footprint at detach).
        for (int i = 0; i < 2; i++)
        {
            await RunWatchdoggedThrowsAsync(() => decoder.RunAsync<int>(
                _ => { strandGate.Wait(); return Task.FromResult(0); },
                TimeSpan.FromMilliseconds(60),
                static _ => DeltaStorageException.DecodeBudgetExceeded("strand"),
                CancellationToken.None,
                estimatedRetainedBytes: footprint));
        }

        SpinWaitFor(() => decoder.StrandedDecodeBytes == budget && decoder.DetachedDecodeCount == 2);

        using var barrier = new Barrier(burst);
        int rejected = 0;
        int started = 0;
        int settled = 0;

        var threads = new Thread[burst];
        for (int t = 0; t < burst; t++)
        {
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    decoder.RunAsync<int>(
                        _ => { Interlocked.Increment(ref started); return Task.FromResult(1); },
                        TimeSpan.FromSeconds(30),
                        static _ => DeltaStorageException.DecodeBudgetExceeded("must not run"),
                        CancellationToken.None,
                        onWorkSettled: () => Interlocked.Increment(ref settled),
                        estimatedRetainedBytes: footprint).GetAwaiter().GetResult();
                }
                catch (DecodeCapacityExhaustedException)
                {
                    Interlocked.Increment(ref rejected);
                }
            })
            {
                IsBackground = true,
                Name = $"burst-byte-{t}",
            };
            threads[t].Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(Watchdog), "a burst thread did not settle within the watchdog.");
        }

        Assert.Equal(burst, rejected); // ALL rejected by the BYTE branch
        Assert.Equal(0, started); // none started
        Assert.Equal(burst, settled); // onWorkSettled once per rejected call
        Assert.True(decoder.DetachedDecodeCount < decoder.StrandCountCap,
            "the COUNT cap must NOT be the binding constraint here — the byte residual is.");

        strandGate.Set();
        SpinWaitFor(() => decoder.DetachedDecodeCount == 0);
    }

    // Serially strands `count` gated, never-terminating decodes so the door's stranded residual is at its
    // count cap; each drains when `gate` is released.
    private static void FillStrandsToCap(BoundedDecoder decoder, int count, ManualResetEventSlim gate)
    {
        for (int i = 0; i < count; i++)
        {
            try
            {
                decoder.RunAsync<int>(
                    _ => { gate.Wait(); return Task.FromResult(0); },
                    TimeSpan.FromMilliseconds(60),
                    static _ => DeltaStorageException.DecodeBudgetExceeded("strand"),
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (DeltaStorageException)
            {
                // Expected: the gated decode timed out and detached into a strand.
            }
        }
    }

    [Fact]
    public async Task ExecutionStartDeadline_BudgetMeasuredFromWorkStart_NotAdmissionOrStrandAge()
    {
        await RunTestBodyWatchdoggedAsync(async () =>
        {
            // I3 — the deadline starts at EXECUTION start, not at admission/enqueue, and admission/queue wait (here
            // modelled as a long-lived pre-existing strand's age) is NEVER charged to a later healthy decode's
            // budget. Driven by a deterministic controllable clock whose timers fire only on Advance, so there is
            // no wall-clock flake. A strand is created and then the virtual clock is advanced FIVE HOURS to age it;
            // a healthy decode submitted afterwards must still get its FULL budget measured from ITS OWN start —
            // advancing the clock by less than that budget must not trip it. If the budget were armed at admission
            // or measured from an absolute epoch, the 5-hour advance would have already blown it.
            var clock = new ControllableClock(TimeSpan.FromHours(1).Ticks);
            var decoder = new BoundedDecoder(strandCountCap: 4);
            using var strandGate = new ManualResetEventSlim(initialState: false);
            var strandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<int> strandTask = decoder.RunAsync<int>(
                _ => { strandStarted.TrySetResult(); strandGate.Wait(); return Task.FromResult(0); },
                TimeSpan.FromSeconds(1),
                static _ => DeltaStorageException.DecodeBudgetExceeded("strand"),
                CancellationToken.None,
                timeProvider: clock);

            await strandStarted.Task; // strand executing; RunAsync will arm its Task.Delay after started fires
            // DETERMINISM: wait until RunAsync has actually CREATED+ARMED the strand's deadline timer before
            // advancing. Advancing before the timer is armed would re-anchor its due instant past the advance and
            // it would never fire (the MEDIUM clock race). This replaces the old symptom-patch.
            await WaitForAsync(() => clock.ArmedTimerCount() == 1);
            clock.Advance(TimeSpan.FromSeconds(1)); // trip the strand → detaches; work stays gate-blocked
            DeltaStorageException stranded = await Assert.ThrowsAsync<DeltaStorageException>(() => strandTask);
            Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, stranded.Kind);
            await WaitForAsync(() => decoder.DetachedDecodeCount == 1);

            // Age the strand a LOT. This elapsed virtual time must NOT be charged to the healthy decode below.
            clock.Advance(TimeSpan.FromHours(5));

            using var proceed = new ManualResetEventSlim(initialState: false);
            var healthyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int> healthyTask = decoder.RunAsync<int>(
                _ => { healthyStarted.TrySetResult(); proceed.Wait(); return Task.FromResult(42); },
                TimeSpan.FromSeconds(1),
                static _ => DeltaStorageException.DecodeBudgetExceeded("healthy decode must not time out"),
                CancellationToken.None,
                timeProvider: clock);

            await healthyStarted.Task; // healthy executing; RunAsync arms its delay at NOW (its own start)
            // DETERMINISM: the strand's timer has fired+disarmed (and its Task.Delay disposed on completion), so
            // once the healthy decode's fresh deadline timer is armed there is exactly ONE armed timer. Wait for
            // it before advancing so the advance is measured against an ALREADY-armed budget.
            await WaitForAsync(() => clock.ArmedTimerCount() == 1);
            clock.Advance(TimeSpan.FromMilliseconds(500)); // < the healthy budget FROM ITS OWN START → must not trip
            proceed.Set();
            int result = await healthyTask;
            Assert.Equal(42, result); // succeeded on its own full budget, unaffected by the aged strand

            strandGate.Set();
            await WaitForAsync(() => decoder.DetachedDecodeCount == 0);
        });
    }

    public enum DataFileEntryPoint
    {
        Open, // ReadDataSchemaAsync — footer open door
        Metadata, // GetRowCountAsync — metadata scan door (its OWN open is the first RunAsync)
        RowGroupEntry, // ReadAsync — the streaming read (its open is the first RunAsync)
    }

    [Theory]
    [InlineData(DataFileEntryPoint.Open)]
    [InlineData(DataFileEntryPoint.Metadata)]
    [InlineData(DataFileEntryPoint.RowGroupEntry)]
    public async Task DataFileDoor_Saturation_MapsToDecoderSaturated_ThroughTheInjectedDecoderSeam(DataFileEntryPoint entry)
    {
        // C1-gap #1 — drive the data-file capacity branch through the REAL read path via the injected
        // dataFileDecoder seam (no test-only widening). A cap=1 decoder whose single strand slot is already
        // filled by a gated strand rejects the NEXT decode fail-fast WITHOUT starting. Every data-file entry
        // point (open, metadata, row-group read) admits its FIRST bounded decode through this door, so each maps
        // the rejection through the shared DataFileDecoderSaturated helper. Asserts: (1) the DISTINCT retryable
        // DecoderSaturated kind (never a decode-timeout); (2) exactly one decode.capacity_exhausted{door=
        // data_file}; (3) decode.budget_exceeded EMPTY (a saturation is not a timeout — the de-conflation
        // contract); (4) no untrusted byte content on the message.
        var decoder = new BoundedDecoder(strandCountCap: 1, execution: DecodeExecution.Pool);
        using var strandGate = new ManualResetEventSlim(initialState: false);

        // Fill the single strand slot with a gated, never-terminating strand.
        await RunWatchdoggedThrowsAsync(() => decoder.RunAsync<int>(
            _ => { strandGate.Wait(); return Task.FromResult(0); },
            TimeSpan.FromMilliseconds(80),
            static _ => DeltaStorageException.DecodeBudgetExceeded("occupying strand"),
            CancellationToken.None));
        await WaitForAsync(() => decoder.DetachedDecodeCount == 1);

        using var telemetry = new DeltaSharp.Storage.Diagnostics.DeltaStorageTelemetry();
        using var meters = new MeterCapture(telemetry.StorageMeter);
        var reader = new ParquetFileReader(ParquetDecodeLimits.Default, telemetry: telemetry, dataFileDecoder: decoder);
        byte[] file = await BuildDataFileAsync();

        DeltaStorageException saturated = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            using var stream = new MemoryStream(file, writable: false);
            switch (entry)
            {
                case DataFileEntryPoint.Open:
                    _ = await reader.ReadDataSchemaAsync(stream, CancellationToken.None);
                    break;
                case DataFileEntryPoint.Metadata:
                    _ = await reader.GetRowCountAsync(stream, CancellationToken.None);
                    break;
                default:
                    await foreach (ColumnBatch batch in reader.ReadAsync(
                        stream, DataSchema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None))
                    {
                        _ = batch.LogicalRowCount;
                    }

                    break;
            }
        });

        Assert.Equal(StorageErrorKind.DecoderSaturated, saturated.Kind); // retryable, NOT a decode-timeout
        Assert.DoesNotContain(".parquet", saturated.Message, StringComparison.OrdinalIgnoreCase); // no byte/path content

        MeterCapture.Measurement capacity = Assert.Single(meters.ForInstrument("deltasharp.storage.decode.capacity_exhausted"));
        Assert.Equal(1, capacity.Value);
        Assert.Equal("data_file", capacity.Tags["deltasharp.decode.door"]);
        Assert.Empty(meters.ForInstrument("deltasharp.storage.decode.budget_exceeded")); // de-conflation

        strandGate.Set();
        await WaitForAsync(() => decoder.DetachedDecodeCount == 0);
    }

    [Fact]
    public void CapacityExhausted_Telemetry_IsDistinctCounter_NotConflatedWithBudgetExceeded()
    {
        // I8 — the metric-conflation fix: a door saturation (capacity exhaustion — the decode never started)
        // increments ONLY the door-dimensioned decode.capacity_exhausted counter and must NOT increment
        // decode.budget_exceeded (which means a decode RAN past budget). They are categorically distinct.
        using var telemetry = new DeltaSharp.Storage.Diagnostics.DeltaStorageTelemetry();
        using var meters = new MeterCapture(telemetry.StorageMeter);

        telemetry.RecordDecodeCapacityExhausted(DeltaSharp.Storage.Diagnostics.DecodeDoor.DataFile);

        MeterCapture.Measurement capacity = Assert.Single(meters.ForInstrument("deltasharp.storage.decode.capacity_exhausted"));
        Assert.Equal(1, capacity.Value);
        Assert.Equal("data_file", capacity.Tags["deltasharp.decode.door"]);
        Assert.Empty(meters.ForInstrument("deltasharp.storage.decode.budget_exceeded"));
    }

    [Fact]
    public void DecoderSaturated_IsADistinctRetryableStorageErrorKind()
    {
        // I8 — the typed retryable classification: door saturation maps to a PUBLIC DecoderSaturated storage
        // error, a distinct kind from a decode-timeout (DecodeBudgetExceeded) and from corruption (CorruptData),
        // so a caller/engine can back off and retry rather than treat it as a permanent failure.
        DeltaStorageException saturated = DeltaStorageException.DecoderSaturated("at capacity");
        Assert.Equal(StorageErrorKind.DecoderSaturated, saturated.Kind);
        Assert.NotEqual(StorageErrorKind.DecodeBudgetExceeded, saturated.Kind);
        Assert.NotEqual(StorageErrorKind.CorruptData, saturated.Kind);
    }

    [Theory]
    [InlineData(256L * 1024 * 1024)] // tiny pod
    [InlineData(512L * 1024 * 1024)]
    [InlineData(1L * 1024 * 1024 * 1024)]
    [InlineData(4L * 1024 * 1024 * 1024)]
    [InlineData(64L * 1024 * 1024 * 1024)] // large executor
    public void DeriveDoorSizing_AlwaysAdmitsOneMaximalDecode_AndTheResidualBoundHolds(long processMemoryBytes)
    {
        // THE decisive fix (Round-6), pinned as a PURE table test across pod sizes: the residual budget is
        // FLOORED so at least one maximal legitimate decode/part is ALWAYS admissible (no derivation to a cap of
        // 1 that lets one crafted input deny every tenant), AND the honest residual bound holds — a single
        // maximal strand can push the residual to at most residualBudget + maxFootprint (one strand can cross the
        // line, never an unbounded pile). Verified for BOTH doors' real max-footprints.
        foreach (long maxFootprint in new[]
                 {
                     BoundedDecode.DataFileMaxFootprintBytes,
                     BoundedDecode.CheckpointMaxFootprintBytes,
                 })
        {
            DoorSizing sizing = BoundedDecode.DeriveDoorSizing(processMemoryBytes, maxFootprint);

            // (1) One maximal legitimate decode is always admissible: the residual budget is at least one
            // footprint (so an empty door admits it) and the count cap is at least the floor multiple (≥ 2 ≥ 1).
            Assert.True(sizing.ResidualBudgetBytes >= maxFootprint,
                $"pod={processMemoryBytes}, footprint={maxFootprint}: residual budget must fit one maximal decode.");
            Assert.True(sizing.StrandCountCap >= 1,
                $"pod={processMemoryBytes}, footprint={maxFootprint}: count cap must admit at least one strand.");

            // (2) The count cap is DECOUPLED from the byte budget (Round-8 #1a) — sized from the thread/fd
            // budget and floored at StrandCountFloor (≥ 64), clamped to the ceiling — so it never binds under
            // the byte residual for maximal strands and neither gate is degenerate.
            Assert.True(sizing.StrandCountCap >= BoundedDecode.StrandCountFloor,
                $"pod={processMemoryBytes}, footprint={maxFootprint}: count cap must be at least the floor.");
            Assert.True(sizing.StrandCountCap <= BoundedDecode.StrandCountCeiling,
                $"pod={processMemoryBytes}, footprint={maxFootprint}: count cap must not exceed the ceiling.");

            // (3) The honest residual bound: the worst-case retained bytes when a single new strand crosses the
            // budget is residualBudget + one maxFootprint — a REAL, finite bound (not the false
            // "byte-bounded ≤ memory budget" claim the old design made).
            long worstCaseResidual = sizing.ResidualBudgetBytes + sizing.MaxFootprintBytes;
            Assert.True(worstCaseResidual > sizing.ResidualBudgetBytes,
                "the residual bound must be residualBudget + one footprint (finite, non-degenerate).");
            Assert.Equal(maxFootprint, sizing.MaxFootprintBytes);
        }
    }

    [Fact]
    public void DeriveDoorSizing_IsMonotonicInProcessMemory_AndConstructionRejectsAnUnadmittableBudget()
    {
        // The residual budget is monotonic non-decreasing in process memory (a bigger pod never gets a SMALLER
        // residual), and the constructor refuses a door that cannot even admit one maximal part (residualBudget
        // < maxFootprint) — documenting the small-pod contract instead of silently deriving a cap of 1.
        long footprint = BoundedDecode.CheckpointMaxFootprintBytes;
        long previous = 0;
        foreach (long pod in new[]
                 {
                     128L * 1024 * 1024, 256L * 1024 * 1024, 1L * 1024 * 1024 * 1024, 32L * 1024 * 1024 * 1024,
                 })
        {
            long residual = BoundedDecode.DeriveDoorSizing(pod, footprint).ResidualBudgetBytes;
            Assert.True(residual >= previous, $"residual budget must be monotonic in pod memory (pod={pod}).");
            previous = residual;
        }

        // A budget below one footprint is unadmittable and must be rejected at construction (fail fast, not a
        // cap-of-1 that denies every tenant).
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedDecoder(strandCountCap: 4, residualBudgetBytes: footprint - 1, maxFootprintBytes: footprint));
    }

    [Theory]
    // (pod, cores) → (residualBudget, strandCountCap, underProvisioned) for a fixed 1-GiB max footprint.
    [InlineData(64L * 1024 * 1024 * 1024, 8, 8L * 1024 * 1024 * 1024, 64, false)] // big pod: residual=mem/8, count floors
    [InlineData(16L * 1024 * 1024 * 1024, 4, 2L * 1024 * 1024 * 1024, 64, false)] // count clamps UP to the floor (8×4=32<64)
    [InlineData(2L * 1024 * 1024 * 1024, 16, 1L * 1024 * 1024 * 1024, 128, true)] // tiny pod: residual capped at mem/2, under-provisioned
    [InlineData(256L * 1024 * 1024 * 1024, 64, 32L * 1024 * 1024 * 1024, 256, false)] // count clamps DOWN to the ceiling (8×64=512>256)
    public void DeriveDoorSizing_ProducesConcretePerPodSizing_WithDecoupledCountCap(
        long pod, int cores, long expectedResidual, int expectedCount, bool expectedUnderProvisioned)
    {
        // Round-8 #1 + #10 — pin the CONCRETE derived sizing per pod so a silent recalibration is RED. Three
        // properties are load-bearing and would each regress a real DoS control:
        //   (a) the residual budget targets mem/8, floored at 2×footprint, and CAPPED at mem/2 (#10 — so a tiny
        //       pod's byte gate can never floor ABOVE pod memory and go inoperative);
        //   (b) the strand-count cap is DECOUPLED from residualBudget/maxFootprint (#1a — which was 2 on every
        //       pod ≤ 64 GiB and could wedge a door with two cheap strands) and sized from the thread budget
        //       (8×cores) clamped to [64, 256] — the floor and ceiling clamps are both exercised here;
        //   (c) the under-provisioned flag fires exactly when the 2×footprint floor cannot fit the mem/2 cap.
        const long footprint = 1L * 1024 * 1024 * 1024;
        DoorSizing sizing = BoundedDecode.DeriveDoorSizing(pod, footprint, processorCount: cores);

        Assert.Equal(expectedResidual, sizing.ResidualBudgetBytes);
        Assert.Equal(expectedCount, sizing.StrandCountCap);
        Assert.Equal(expectedUnderProvisioned, sizing.UnderProvisioned);

        // The count cap is at least the floor (never the old degenerate 2) and never above the ceiling.
        Assert.True(sizing.StrandCountCap >= BoundedDecode.StrandCountFloor);
        Assert.True(sizing.StrandCountCap <= BoundedDecode.StrandCountCeiling);

        // The residual is bounded ABOVE by max(mem/2, one footprint) — the #10 upper bound vs process memory,
        // so a small pod's byte gate stays reachable rather than flooring above the pod it protects.
        long memCap = Math.Max(pod / BoundedDecode.ResidualBudgetMemoryCapDivisor, footprint);
        Assert.True(sizing.ResidualBudgetBytes <= memCap,
            $"pod={pod}: residual {sizing.ResidualBudgetBytes} must not exceed the mem/2 cap {memCap} (#10).");
        // …and never below one footprint (one legit part is always admissible).
        Assert.True(sizing.ResidualBudgetBytes >= footprint);
    }

    [Fact]
    public async Task DeriveDoorSizing_BehavioralOracle_OneMaximalStrandDoesNotWedgeTheDoor_AndCheapStrandsDoNotCloseTheCountGate()
    {
        // Round-8 #1 (the decisive behavioral regression catch) — build a REAL door from the derived sizing and
        // prove the two failure modes the old count cap (= residualBudget/maxFootprint = 2 on every pod ≤ 64 GiB)
        // introduced are BOTH closed:
        //   (1) WEDGE: detaching TWO maximal-footprint strands must NOT wedge the door — a healthy decode is
        //       still admitted. Under the old cap=2, the second strand hit the count gate and every healthy
        //       decode was rejected process-wide. RED under a revert to the coupled cap.
        //   (2) CHEAP-STRAND COUNT GATE: detaching several SMALL-charge strands (well under the byte residual)
        //       must NOT close the door either — the count gate is far from binding (≥ 64) while the byte gate
        //       (the load-bearing one) is nowhere near full. Under the old cap=2 the 3rd cheap strand wedged it.
        const long footprint = 1L * 1024 * 1024 * 1024;
        DoorSizing sizing = BoundedDecode.DeriveDoorSizing(64L * 1024 * 1024 * 1024, footprint, processorCount: 8);
        Assert.Equal(64, sizing.StrandCountCap); // decoupled cap — NOT 2

        // (1) Two MAXIMAL strands, then a healthy decode is still admitted.
        var wedgeDoor = BoundedDecoder.FromSizing(sizing, DecodeExecution.Pool);
        using (var gate = new ManualResetEventSlim(initialState: false))
        {
            DetachStrand(wedgeDoor, gate, charge: footprint);
            DetachStrand(wedgeDoor, gate, charge: footprint);
            await WaitForAsync(() => wedgeDoor.DetachedDecodeCount == 2);
            Assert.Equal(footprint * 2, wedgeDoor.StrandedDecodeBytes); // 2 GiB stranded, well under the 8 GiB residual

            int admitted = await wedgeDoor.RunAsync(
                _ => Task.FromResult(1),
                TimeSpan.FromSeconds(5),
                static _ => DeltaStorageException.DecodeBudgetExceeded("healthy decode must not be wedged out"),
                CancellationToken.None,
                estimatedRetainedBytes: footprint);
            Assert.Equal(1, admitted); // NOT wedged — old cap=2 would have rejected this
            gate.Set();
            await WaitForAsync(() => wedgeDoor.DetachedDecodeCount == 0);
        }

        // (2) Several CHEAP strands (tiny charge), then a healthy decode is still admitted.
        var cheapDoor = BoundedDecoder.FromSizing(sizing, DecodeExecution.Pool);
        using (var gate = new ManualResetEventSlim(initialState: false))
        {
            const long cheap = 8L * 1024 * 1024; // 8 MiB — far under the byte residual
            for (int i = 0; i < 5; i++)
            {
                DetachStrand(cheapDoor, gate, charge: cheap);
            }

            await WaitForAsync(() => cheapDoor.DetachedDecodeCount == 5);
            int admitted = await cheapDoor.RunAsync(
                _ => Task.FromResult(2),
                TimeSpan.FromSeconds(5),
                static _ => DeltaStorageException.DecodeBudgetExceeded("cheap strands must not close the count gate"),
                CancellationToken.None,
                estimatedRetainedBytes: cheap);
            Assert.Equal(2, admitted); // count gate (64) far from binding — old cap=2 would have rejected this
            gate.Set();
            await WaitForAsync(() => cheapDoor.DetachedDecodeCount == 0);
        }
    }

    [Fact]
    public async Task WedgedDoorSignal_FiresWhenSaturatedWithNoStrandDrain_AndClearsWhenAStrandDrains()
    {
        // Round-8 #1 wedged-door signal — a saturated door whose strands do NOT drain within
        // WedgedDrainStallWindow is reported IsWedged so a liveness probe can recycle the pod (its
        // DecoderSaturated is not genuinely retryable). Driven by an injected clock so it is deterministic:
        //   - a NON-saturated door is never wedged (even after advancing past the window);
        //   - a saturated door is not wedged immediately (a strand just appeared);
        //   - after the stall window with NO drain it reports wedged;
        //   - once a strand DRAINS the door is no longer saturated → not wedged.
        var clock = new ControllableClock(TimeSpan.FromHours(1).Ticks);
        const long footprint = 64L * 1024 * 1024;
        var decoder = new BoundedDecoder(
            strandCountCap: 1, residualBudgetBytes: footprint, maxFootprintBytes: footprint,
            execution: DecodeExecution.Pool, clock: clock);

        // A fresh, non-saturated door is never wedged — even after advancing well past the stall window.
        clock.Advance(BoundedDecode.WedgedDrainStallWindow + TimeSpan.FromMinutes(1));
        Assert.False(decoder.IsWedged);

        using var gate = new ManualResetEventSlim(initialState: false);
        // Detach ONE strand (charge 0 so only the COUNT gate saturates the cap-1 door). Its deadline uses REAL
        // time (default timeProvider) so it detaches without advancing the injected door clock; the door records
        // the strand's activity against the INJECTED clock, which the test then advances to cross the window.
        DetachStrand(decoder, gate, charge: 0);
        await WaitForAsync(() => decoder.DetachedDecodeCount == 1);
        Assert.False(decoder.IsWedged); // saturated, but the strand just appeared — no stall yet

        clock.Advance(BoundedDecode.WedgedDrainStallWindow + TimeSpan.FromMinutes(1)); // no drain across the window
        Assert.True(decoder.IsWedged); // wedged — a liveness probe should recycle the pod

        gate.Set(); // the strand drains → the door is no longer saturated
        await WaitForAsync(() => decoder.DetachedDecodeCount == 0);
        Assert.False(decoder.IsWedged);
    }

    [Fact]
    public async Task CancellationLaunderedStrand_IsCountedAndCharged_OnATokenIgnoringNonTerminatingDecode()
    {
        // Round-8 #5 — a non-terminating decode abandoned via CALLER CANCELLATION (not a deadline) must be
        // charged AND counted, otherwise it launders the bound: it holds a thread + bytes + lease forever while
        // invisible to the residual. Proven with a token-IGNORING decode (it never observes cancellation and
        // never terminates): after the caller cancels, it is booked as a CANCELLED strand — counted in the
        // detached gauge (and its cancelled sub-dimension) and charged its footprint — and it STAYS booked while
        // it keeps running. A HEALTHY cancelled decode (covered elsewhere) drains in ms and costs nothing.
        const long footprint = 64L * 1024 * 1024;
        var decoder = new BoundedDecoder(
            strandCountCap: 4, residualBudgetBytes: footprint * 8, maxFootprintBytes: footprint);
        using var cts = new CancellationTokenSource();
        using var started = new ManualResetEventSlim(initialState: false);
        using var neverReleased = new ManualResetEventSlim(initialState: false);

        Task<int> run = decoder.RunAsync<int>(
            _ => { started.Set(); neverReleased.Wait(); return Task.FromResult(0); }, // IGNORES the token, never terminates
            TimeSpan.FromSeconds(30),
            static _ => DeltaStorageException.DecodeBudgetExceeded("must not time out"),
            cts.Token,
            estimatedRetainedBytes: footprint);

        Assert.True(started.Wait(Watchdog));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // The laundered strand is VISIBLE to the bound: counted (with the cancelled sub-dimension) and charged.
        await WaitForAsync(() => decoder.DetachedDecodeCount == 1);
        Assert.Equal(1, decoder.CancelledDetachedDecodeCount);
        Assert.Equal(footprint, decoder.StrandedDecodeBytes);

        // It STAYS booked while it keeps running (the token-ignoring work never settles) — not laundered away.
        await Task.Delay(50);
        Assert.Equal(1, decoder.DetachedDecodeCount);
        Assert.Equal(footprint, decoder.StrandedDecodeBytes);

        neverReleased.Set(); // let it finally terminate so the strand drains and the test cleans up
        await WaitForAsync(() => decoder.DetachedDecodeCount == 0);
        Assert.Equal(0L, decoder.StrandedDecodeBytes);
    }

    [Fact]
    public async Task StrandCharge_IsClampedToDoorFootprint_AndNeverNegative()
    {
        // Round-8 #2 clamp semantics (the RunAsync side of the charge oracle) — the charge a strand books is
        // clamp(estimate, 0, maxFootprint): a 4×maxFootprint over-estimate is clamped DOWN to one footprint
        // (so a mis-estimate can never over-charge the residual and the bound stays provable), and a negative
        // estimate is clamped UP to 0 (never a negative residual). RED if the clamp is removed.
        const long footprint = 64L * 1024 * 1024;
        var over = new BoundedDecoder(
            strandCountCap: 4, residualBudgetBytes: footprint * 8, maxFootprintBytes: footprint);
        using var gate = new ManualResetEventSlim(initialState: false);

        DetachStrand(over, gate, charge: footprint * 4); // 4× the door footprint
        await WaitForAsync(() => over.DetachedDecodeCount == 1);
        Assert.Equal(footprint, over.StrandedDecodeBytes); // clamped DOWN to one footprint

        DetachStrand(over, gate, charge: -1); // negative estimate
        await WaitForAsync(() => over.DetachedDecodeCount == 2);
        Assert.Equal(footprint, over.StrandedDecodeBytes); // +0 for the negative strand → still exactly one footprint

        gate.Set();
        await WaitForAsync(() => over.DetachedDecodeCount == 0);
    }

    [Fact]
    public async Task OpenStrandChargeOracle_ChargesTheInputLengthDerivedFootprint_NotZero()
    {
        // Round-8 #7 end-to-end charge oracle — strand a REAL open (footer bit-flip drives a non-terminating
        // CreateAsync) through an injected decoder and assert its stranded residual is the input-length-derived
        // footprint (floored at MinStrandChargeBytes, clamped to the door footprint), NOT the old fixed 16-MiB
        // fiction and NOT zero. RED if the open call site's `estimatedRetainedBytes:` is mutated to 0 (the
        // charge would collapse to 0 instead of the floored file-length footprint).
        byte[] file = await BuildDataFileAsync();
        byte[] mutated = (byte[])file.Clone();
        mutated[^9] ^= 1; // hang the open

        var decoder = new BoundedDecoder(strandCountCap: 4, execution: DecodeExecution.Pool);
        var reader = new ParquetFileReader(
            new ParquetDecodeLimits(decodeTimeBudget: TimeSpan.FromMilliseconds(200)),
            dataFileDecoder: decoder);

        var thrown = await RunWatchdoggedAsync(() => ReadAllAsync(reader, mutated));
        Assert.IsType<DeltaStorageException>(thrown);
        await WaitForAsync(() => decoder.DetachedDecodeCount == 1);

        long expected = Math.Max((long)mutated.Length, BoundedDecode.MinStrandChargeBytes);
        expected = Math.Clamp(expected, 0, decoder.MaxFootprintBytes);
        Assert.Equal(expected, decoder.StrandedDecodeBytes); // the input-length-derived floor, never 0
    }

    [Fact]
    public async Task EstimateRowGroupRetainedBytes_ChargesSumAcrossProjectedColumns_ClampsAndFaultsToCeiling()
    {
        // Round-8 #2 direct estimator test — the per-row-group strand charge is Σ (decompressed + rows×width)
        // ACROSS ALL projected columns (the decode materializes ColumnVector[requested.Count] simultaneously),
        // NOT one column's / one ceiling's worth. Pinned directly on the internal estimator:
        //   - a real 2-column row group estimates STRICTLY MORE than the same rows projected to 1 column
        //     (proves it sums across columns — RED if reverted to a single-column truncation);
        //   - the estimate is positive and within the door's max footprint (the clamp holds);
        //   - a fault (an out-of-range group index) falls back to the door footprint (never under-counts).
        byte[] file = await BuildDataFileAsync();
        using var stream = new MemoryStream(file, writable: false);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);

        var oneColumn = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
        ParquetFileReader.ResolvedColumn[] twoFields = ParquetFileReader.ResolveFileFields(reader.Schema, DataSchema, false, false, null);
        ParquetFileReader.ResolvedColumn[] oneField = ParquetFileReader.ResolveFileFields(reader.Schema, oneColumn, false, false, null);

        long twoColumnEstimate = ParquetFileReader.EstimateRowGroupRetainedBytes(
            reader, 0, DataSchema, twoFields, ParquetDecodeLimits.Default);
        long oneColumnEstimate = ParquetFileReader.EstimateRowGroupRetainedBytes(
            reader, 0, oneColumn, oneField, ParquetDecodeLimits.Default);

        Assert.True(twoColumnEstimate > 0, "a real non-empty row group must estimate a positive footprint (never 0).");
        Assert.True(twoColumnEstimate > oneColumnEstimate,
            $"Σ across 2 columns ({twoColumnEstimate}) must exceed 1 column ({oneColumnEstimate}) — Round-8 #2.");
        Assert.True(twoColumnEstimate <= BoundedDecode.DataFileMaxFootprintBytes, "the estimate must be clamped to the door footprint.");

        // Fault path — an out-of-range group index faults OpenRowGroupReader → fall back to the door footprint.
        long faulted = ParquetFileReader.EstimateRowGroupRetainedBytes(
            reader, group: 9999, DataSchema, twoFields, ParquetDecodeLimits.Default);
        Assert.Equal(BoundedDecode.DataFileMaxFootprintBytes, faulted);
    }

    [Fact]
    public async Task HealthyDecode_SettlesSynchronously_BeforeRunAsyncReturns_NoPolling()
    {
        // Round-8 test charge (non-polling synchronous settle) — on the HEALTHY path onWorkSettled fires
        // SYNCHRONOUSLY before RunAsync returns (the caller's lease is released — and its reader/stream
        // deterministically disposed — before control returns), NOT via a background continuation the caller
        // would have to poll for. Asserted WITHOUT WaitForAsync: the settled flag is already set the instant the
        // await returns.
        var decoder = new BoundedDecoder(strandCountCap: 4);
        int settled = 0;

        int result = await decoder.RunAsync(
            _ => Task.FromResult(11),
            TimeSpan.FromSeconds(5),
            static _ => DeltaStorageException.DecodeBudgetExceeded("must not time out"),
            CancellationToken.None,
            onWorkSettled: () => Interlocked.Increment(ref settled));

        Assert.Equal(11, result);
        Assert.Equal(1, Volatile.Read(ref settled)); // already settled synchronously — no poll/await needed
        Assert.Equal(0, decoder.DetachedDecodeCount);
        Assert.Equal(0L, decoder.StrandedDecodeBytes);
    }

    [Theory]
    [InlineData(InnerDecodeEntry.MetadataScan)]
    [InlineData(InnerDecodeEntry.RowGroup)]
    public async Task DataFileDoor_InnerDecodeSaturation_AfterOpenSucceeds_MapsToDecoderSaturated(InnerDecodeEntry entry)
    {
        // C1-gap — the TWO remaining data-file capacity branches (the metadata-scan RunAsync and the row-group
        // RunAsync, each a SECOND decode after a SUCCESSFUL open) are not reached by saturating BEFORE the open
        // (that rejects the open). Cover them with deterministic open-then-saturate gating (no sleeps): a gated
        // input stalls the open's I/O while it is in-flight; during that window a permanent strand fills the
        // cap-1 door; releasing the gate lets the open COMPLETE, so the inner decode is the one rejected — the
        // branch under test. Delete either inner `catch (DecodeCapacityExhaustedException)` → this goes RED.
        var decoder = new BoundedDecoder(strandCountCap: 1, execution: DecodeExecution.Pool);
        byte[] file = await BuildDataFileAsync();
        var openBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseOpen = new ManualResetEventSlim(initialState: false);
        var gatedStream = new GatingStream(new MemoryStream(file, writable: false), openBlocked, releaseOpen);
        var reader = new ParquetFileReader(ParquetDecodeLimits.Default, dataFileDecoder: decoder);
        using var strandGate = new ManualResetEventSlim(initialState: false);

        // Start the read; the open's first I/O blocks on the gated stream (the open is admitted + in-flight).
        Task<Exception?> read = RunWatchdoggedAsync(async () =>
        {
            switch (entry)
            {
                case InnerDecodeEntry.MetadataScan:
                    _ = await reader.GetRowCountAsync(gatedStream, CancellationToken.None);
                    break;
                default:
                    await foreach (ColumnBatch batch in reader.ReadAsync(
                        gatedStream, DataSchema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None))
                    {
                        _ = batch.LogicalRowCount;
                    }

                    break;
            }
        });

        await openBlocked.Task; // the open is in-flight, blocked on I/O — its admission already passed
        // Fill the single strand slot WHILE the open is blocked, so the open still completes but the SECOND
        // (inner) decode is rejected.
        await RunWatchdoggedThrowsAsync(() => decoder.RunAsync<int>(
            _ => { strandGate.Wait(); return Task.FromResult(0); },
            TimeSpan.FromMilliseconds(80),
            static _ => DeltaStorageException.DecodeBudgetExceeded("occupying strand"),
            CancellationToken.None));
        await WaitForAsync(() => decoder.DetachedDecodeCount == 1);

        releaseOpen.Set(); // the open completes; the inner decode now admits into a saturated door → rejected
        Exception? thrown = await read;
        DeltaStorageException saturated = Assert.IsType<DeltaStorageException>(thrown);
        Assert.Equal(StorageErrorKind.DecoderSaturated, saturated.Kind); // the inner-decode branch mapped it

        strandGate.Set();
        await WaitForAsync(() => decoder.DetachedDecodeCount == 0);
    }

    public enum InnerDecodeEntry
    {
        MetadataScan, // GetRowCountAsync — open succeeds, the metadata-scan RunAsync is rejected
        RowGroup, // ReadAsync — open succeeds, the per-row-group decode RunAsync is rejected
    }

    [Fact]
    public async Task SynchronousCheckpointDecodeBody_RunsOffThePool_OnTheDedicatedBoundedDecodeThread()
    {
        // §5.4 thread-affinity claim (Round-8 doc-backing) — the checkpoint door's synchronous decode body runs
        // OFF the ThreadPool, on the dedicated background "deltasharp-bounded-decode" thread, so a non-terminating
        // synchronous decode cannot pin a pool thread. Observed directly from inside the decode body.
        var decoder = new BoundedDecoder(strandCountCap: 4, execution: DecodeExecution.DedicatedThread);
        bool? isThreadPoolThread = null;
        string? threadName = null;

        int result = await decoder.RunAsync(
            _ =>
            {
                isThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
                threadName = Thread.CurrentThread.Name;
                return Task.FromResult(5);
            },
            TimeSpan.FromSeconds(5),
            static _ => DeltaStorageException.DecodeBudgetExceeded("must not time out"),
            CancellationToken.None);

        Assert.Equal(5, result);
        Assert.False(isThreadPoolThread, "the synchronous checkpoint decode body must NOT run on a ThreadPool thread.");
        Assert.Equal("deltasharp-bounded-decode", threadName);
    }

    // Serially strands ONE gated, never-terminating decode charging `charge` bytes; it drains when `gate` is
    // released. Used by the sizing behavioral oracles and the clamp oracle.
    private static void DetachStrand(BoundedDecoder decoder, ManualResetEventSlim gate, long charge)
    {
        try
        {
            decoder.RunAsync<int>(
                _ => { gate.Wait(); return Task.FromResult(0); },
                TimeSpan.FromMilliseconds(60),
                static _ => DeltaStorageException.DecodeBudgetExceeded("strand"),
                CancellationToken.None,
                estimatedRetainedBytes: charge).GetAwaiter().GetResult();
        }
        catch (DeltaStorageException)
        {
            // Expected: the gated decode timed out and detached into a strand booking `charge`.
        }
    }

    [Fact]
    public void PerDoorIsolation_DataFileAndCheckpointDoors_HaveIndependentSizingAndExecution()
    {
        // I5 per-door isolation, pinned: the two production doors are DISTINCT instances with independent
        // residual budgets (a flood on one cannot consume the other's), the data-file door runs strands on the
        // Pool while the checkpoint door uses a DedicatedThread, and each door's sizing is exactly what
        // DeriveDoorSizing produces for its documented max footprint.
        Assert.NotSame(BoundedDecode.DataFileDecoder, BoundedDecode.CheckpointDecoder);

        Assert.Equal(DecodeExecution.Pool, BoundedDecode.DataFileDecoder.Execution);
        Assert.Equal(DecodeExecution.DedicatedThread, BoundedDecode.CheckpointDecoder.Execution);

        Assert.Equal(BoundedDecode.DataFileMaxFootprintBytes, BoundedDecode.DataFileDecoder.MaxFootprintBytes);
        Assert.Equal(BoundedDecode.CheckpointMaxFootprintBytes, BoundedDecode.CheckpointDecoder.MaxFootprintBytes);

        DoorSizing dataSizing = BoundedDecode.DeriveDoorSizing(
            BoundedDecode.ProcessMemoryBytes, BoundedDecode.DataFileMaxFootprintBytes);
        DoorSizing checkpointSizing = BoundedDecode.DeriveDoorSizing(
            BoundedDecode.ProcessMemoryBytes, BoundedDecode.CheckpointMaxFootprintBytes);

        Assert.Equal(dataSizing.ResidualBudgetBytes, BoundedDecode.DataFileDecoder.ResidualBudgetBytes);
        Assert.Equal(dataSizing.StrandCountCap, BoundedDecode.DataFileDecoder.StrandCountCap);
        Assert.Equal(checkpointSizing.ResidualBudgetBytes, BoundedDecode.CheckpointDecoder.ResidualBudgetBytes);
        Assert.Equal(checkpointSizing.StrandCountCap, BoundedDecode.CheckpointDecoder.StrandCountCap);
    }

    [Fact]
    public async Task ByteAwareCap_RejectsWhenResidualBudgetExhausted_BeforeCountCap()
    {
        // Critical #1 (Round-6 charge-at-DETACH model) — the load-bearing memory bound is the STRANDED-residual
        // BYTES, charged at detach on the strand's REAL retained footprint (clamped to the door's max footprint),
        // NOT a fictional fixed representative. Healthy in-flight is never charged; only strands consume the
        // residual. Admission of a NEW untrusted decode is rejected when the CURRENT stranded residual is already
        // at budget — even though the COUNT cap (100) is nowhere near reached. Proven with a residual budget that
        // fits exactly two maximal 64 MiB strands: the 3rd admission is rejected by the BYTE branch.
        const long footprint = 64L * 1024 * 1024;
        var decoder = new BoundedDecoder(
            strandCountCap: 100, // count cap is deliberately far from binding
            residualBudgetBytes: footprint * 2, // fits exactly 2 maximal strands
            maxFootprintBytes: footprint);
        using var gate = new ManualResetEventSlim(initialState: false);

        // Two strands consume the residual budget (each times out and charges its real footprint at detach).
        for (int i = 0; i < 2; i++)
        {
            await RunWatchdoggedThrowsAsync(() => decoder.RunAsync<int>(
                _ => { gate.Wait(); return Task.FromResult(0); },
                TimeSpan.FromMilliseconds(80),
                static _ => DeltaStorageException.DecodeBudgetExceeded("strand"),
                CancellationToken.None,
                estimatedRetainedBytes: footprint));
        }

        await WaitForAsync(() => decoder.DetachedDecodeCount == 2);
        Assert.Equal(footprint * 2, decoder.StrandedDecodeBytes);

        // The 3rd decode is rejected by the BYTE branch though the COUNT cap (100) is nowhere near reached.
        DecodeCapacityExhaustedException rejected = await Assert.ThrowsAsync<DecodeCapacityExhaustedException>(() =>
            decoder.RunAsync<int>(
                _ => Task.FromResult(1),
                TimeSpan.FromSeconds(5),
                static _ => DeltaStorageException.DecodeBudgetExceeded("must not run"),
                CancellationToken.None,
                estimatedRetainedBytes: footprint));
        // The truthful saturation message reports the STRANDED bytes (not a healthy-in-flight or reserved gauge).
        Assert.Contains("strandedBytes", rejected.Message, StringComparison.Ordinal);
        Assert.True(decoder.DetachedDecodeCount < decoder.StrandCountCap,
            "the count cap must NOT be the binding constraint here — the byte residual is.");

        // BYTE-BRANCH BACK-OUT (Round-6): a rejected admission charges NOTHING (the residual is unchanged), and
        // once a strand drains the residual frees so a later decode admits again.
        Assert.Equal(footprint * 2, decoder.StrandedDecodeBytes); // rejection did not charge
        gate.Set();
        await WaitForAsync(() => decoder.DetachedDecodeCount == 0);
        Assert.Equal(0, decoder.StrandedDecodeBytes); // residual fully released as strands drained

        // A later healthy decode admits and completes now that the residual is free.
        int ok = await decoder.RunAsync<int>(
            _ => Task.FromResult(7),
            TimeSpan.FromSeconds(5),
            static _ => DeltaStorageException.DecodeBudgetExceeded("must not run"),
            CancellationToken.None,
            estimatedRetainedBytes: footprint);
        Assert.Equal(7, ok);
    }

    [Fact]
    public async Task CallerCancellation_ReleasesLease_AndIsNotCountedAsAStrand()
    {
        // High #3 + High #6 — a routine caller cancellation of a healthy in-flight decode must (a) fire
        // onWorkSettled EXACTLY ONCE so a caller-held lease is released (no leak), and (b) NOT inflate the
        // detached-strand gauge (a cancelled healthy decode is not a strand). Proven directly: a decode is
        // cancelled mid-flight; the onWorkSettled callback fires exactly once and the detached count stays 0.
        var decoder = new BoundedDecoder(strandCountCap: 4);
        using var cts = new CancellationTokenSource();
        using var started = new ManualResetEventSlim(initialState: false);
        int settledCount = 0;

        Task<int> run = decoder.RunAsync<int>(
            token => { started.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return Task.FromResult(0); },
            TimeSpan.FromSeconds(30),
            static _ => DeltaStorageException.DecodeBudgetExceeded("must not time out"),
            cts.Token,
            onWorkSettled: () => Interlocked.Increment(ref settledCount));

        Assert.True(started.Wait(Watchdog));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // The lease was released exactly once, and the cancellation was NOT counted as a strand.
        await WaitForAsync(() => Volatile.Read(ref settledCount) == 1);
        Assert.Equal(1, Volatile.Read(ref settledCount));
        await WaitForAsync(() => decoder.DetachedDecodeCount == 0);
        Assert.Equal(0, decoder.DetachedDecodeCount);
    }

    [Fact]
    public async Task PreStartCancellation_FiresOnWorkSettled_Once_NoLeak()
    {
        // High #3 — the precise lease-leak path: the caller's token is ALREADY cancelled when RunAsync is
        // entered (the Retain→RunAsync window closed with a cancel), so the FIRST act is a pre-start
        // ThrowIfCancellationRequested BEFORE any work task exists. onWorkSettled must still fire exactly once
        // (releasing the caller's lease), and no strand is created.
        var decoder = new BoundedDecoder(strandCountCap: 4);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        int settledCount = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoder.RunAsync<int>(
            _ => Task.FromResult(0),
            TimeSpan.FromSeconds(5),
            static _ => DeltaStorageException.DecodeBudgetExceeded("must not run"),
            cts.Token,
            onWorkSettled: () => Interlocked.Increment(ref settledCount)));

        Assert.Equal(1, Volatile.Read(ref settledCount)); // released exactly once on the pre-start throw
        Assert.Equal(0, decoder.DetachedDecodeCount); // never a strand
        Assert.Equal(0L, decoder.StrandedDecodeBytes); // no residual charged (nothing ran, nothing detached)
    }

    [Fact]
    public async Task CallerCancelDuringMultiRowGroupRead_DisposesReaderStream_ViaRefCountedLease()
    {
        // High #3 (end-to-end) — the lease-leak fix on the REAL read path: cancelling the caller token DURING a
        // multi-row-group read must release the ref-counted lease so the `ParquetReader` (opened with
        // leaveStreamOpen:false) is disposed, which disposes the caller's input stream. A leak would leave the
        // stream/reader undisposed forever. Observed directly via a disposal-signalling stream wrapper.
        byte[] file = await BuildMultiRowGroupFileAsync(rows: 8, rowsPerGroup: 1); // 8 row groups
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new DisposalObservingStream(new MemoryStream(file, writable: false), disposed);
        var reader = new ParquetFileReader();
        using var cts = new CancellationTokenSource();

        Task read = Task.Run(async () =>
        {
            int seen = 0;
            await foreach (ColumnBatch batch in reader.ReadAsync(
                stream, DataSchema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, cts.Token))
            {
                _ = batch.LogicalRowCount;
                if (++seen == 1)
                {
                    cts.Cancel(); // cancel mid-stream, in the Retain→decode window of the NEXT row group
                }
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);

        // The lease drained and the reader (hence the caller's stream) was disposed — no leak.
        Assert.True(
            await Task.WhenAny(disposed.Task, Task.Delay(Watchdog)) == disposed.Task,
            "the reader's input stream must be disposed after a mid-read caller cancellation (lease-leak regression).");
    }

    private static async Task ReadAllAsync(ParquetFileReader reader, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await foreach (ColumnBatch batch in reader.ReadAsync(
            stream, DataSchema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None))
        {
            _ = batch.LogicalRowCount;
        }
    }

    // Runs the operation on the thread pool under the watchdog so a genuine non-termination fails the test
    // rather than stalling CI, and returns the exception it threw (null if it completed).
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
                $"The bounded operation did not terminate within {Watchdog.TotalSeconds:0}s — the bounded-decode "
                + "policy failed to release the caller (regression of #647/#699/#716).");
        }

        return await run;
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

    // Synchronous sibling of WaitForAsync for the thread-based burst test (no async context inside the thread
    // accounting loop): spins on a condition up to the watchdog.
    private static void SpinWaitFor(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > Watchdog)
            {
                Assert.Fail("The expected bounded-decode state was not reached within the watchdog.");
            }

            Thread.Sleep(5);
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

    // Runs an async test body under the watchdog so a deadlock/regression is a RED failure, not a hung CI job
    // (the MEDIUM ControllableClock/ExecutionStartDeadline race fix — that test awaits multiple TCS/strand
    // tasks with no intrinsic timeout). Rethrows the body's exception/assertion so it surfaces as the failure.
    private static async Task RunTestBodyWatchdoggedAsync(Func<Task> body)
    {
        Exception? failure = await RunWatchdoggedAsync(body);
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }

    private sealed class ObservableDisposable(TaskCompletionSource disposedSignal) : IDisposable
    {
        internal bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
            disposedSignal.TrySetResult();
        }
    }

    // A pass-through stream that signals when it is disposed, so a test can observe the ref-counted lease
    // disposing the ParquetReader (opened leaveStreamOpen:false) and thus the caller's input stream.
    private sealed class DisposalObservingStream(Stream inner, TaskCompletionSource disposedSignal) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                disposedSignal.TrySetResult();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            disposedSignal.TrySetResult();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    // A pass-through seekable stream that BLOCKS every read until `release` is set, signalling `firstReadEntered`
    // the first time a read is attempted. Used to hold a data-file OPEN in-flight (its I/O blocked) so a test can
    // saturate the door WHILE the open is admitted-but-not-complete, then release the open so the SECOND (inner)
    // decode is the one rejected — the deterministic open-then-saturate gating (no sleeps).
    private sealed class GatingStream(Stream inner, TaskCompletionSource firstReadEntered, ManualResetEventSlim release) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            Gate();
            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            Gate();
            return inner.Read(buffer);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Gate();
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Gate();
            return inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Gate()
        {
            firstReadEntered.TrySetResult();
            release.Wait();
        }
    }

    // A fully controllable virtual clock: GetTimestamp/GetUtcNow read a settable tick counter, and CreateTimer
    // returns a one-shot timer that fires ONLY when Advance pushes the clock past its due instant. This lets a
    // bounded decode's Task.Delay(budget, this) trip deterministically at an exact virtual instant — used to
    // prove the deadline is measured from EXECUTION start and not charged for admission/strand age (I3), with
    // no real wall-clock wait or flake.
    private sealed class ControllableClock(long startTicks) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<FakeTimer> _timers = new();
        private long _ticks = startTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        // The number of currently ARMED (not disarmed, not yet fired) one-shot timers. A test uses this to
        // deterministically wait until a bounded decode's Task.Delay(budget, this) has actually been created and
        // armed BEFORE advancing the clock — closing the race (the MEDIUM flake) where an Advance that lands
        // before the timer is armed would re-anchor the due instant past the advance and never fire.
        internal int ArmedTimerCount()
        {
            lock (_gate)
            {
                int n = 0;
                foreach (FakeTimer t in _timers)
                {
                    if (t.DueTicksSnapshot() != FakeTimer.Disarmed)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        public override long GetTimestamp()
        {
            lock (_gate)
            {
                return _ticks;
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return new DateTimeOffset(_ticks, TimeSpan.Zero);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        internal void Advance(TimeSpan by)
        {
            var due = new List<FakeTimer>();
            lock (_gate)
            {
                _ticks += by.Ticks;
                foreach (FakeTimer t in _timers)
                {
                    long d = t.DueTicksSnapshot();
                    if (d != FakeTimer.Disarmed && d <= _ticks)
                    {
                        due.Add(t);
                    }
                }
            }

            foreach (FakeTimer t in due)
            {
                t.Fire();
            }
        }

        private long NowTicks()
        {
            lock (_gate)
            {
                return _ticks;
            }
        }

        private void Remove(FakeTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class FakeTimer(ControllableClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal const long Disarmed = long.MinValue;

            // Guarded via Interlocked so a Change/Fire write can never tear against Advance's read on another
            // thread (the MEDIUM torn-read fix). long sentinel instead of long? keeps the read a single atomic op.
            private long _dueTicks = Disarmed;

            internal long DueTicksSnapshot() => Interlocked.Read(ref _dueTicks);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                // One-shot (period ignored): schedule a due instant, or disarm on an infinite/negative due.
                long value = dueTime < TimeSpan.Zero ? Disarmed : clock.NowTicks() + dueTime.Ticks;
                Interlocked.Exchange(ref _dueTicks, value);
                return true;
            }

            internal void Fire()
            {
                // Atomically claim-and-disarm so a concurrent Advance cannot fire the one-shot twice.
                long previous = Interlocked.Exchange(ref _dueTicks, Disarmed);
                if (previous == Disarmed)
                {
                    return;
                }

                callback(state);
            }

            public void Dispose() => clock.Remove(this);

            public ValueTask DisposeAsync()
            {
                clock.Remove(this);
                return ValueTask.CompletedTask;
            }
        }
    }

    // A fresh, ISOLATED data-file BoundedDecoder for a DoS test that STRANDS a decode permanently (a
    // never-terminating footer-flip open). Routing such a test through its own decoder keeps the process-wide
    // BoundedDecode.DataFileDecoder — whose PRODUCTION strand-count cap is deliberately small (residualBudget /
    // the real 4 GiB max footprint) — from being saturated by a test's permanent strand and poisoning every
    // later test that shares it. The door tag on telemetry is stamped by ParquetFileReader, not the decoder, so
    // the REAL door path is still exercised.
    private static BoundedDecoder IsolatedDataFileDecoder() =>
        new(strandCountCap: 16, execution: DecodeExecution.Pool);

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

    private static async Task<byte[]> BuildDataFileAsync()
    {
        const int rows = 500;
        MutableColumnVector idVector = ColumnVectors.Create(DataTypes.LongType, rows);
        MutableColumnVector nameVector = ColumnVectors.Create(DataTypes.StringType, rows);
        for (int i = 0; i < rows; i++)
        {
            idVector.AppendValue((long)i);
            nameVector.AppendBytes(System.Text.Encoding.UTF8.GetBytes("row-" + (i % 37)));
        }

        var batch = new ManagedColumnBatch(DataSchema, new ColumnVector[] { idVector, nameVector }, rows);
        var writer = new ParquetFileWriter();
        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, DataSchema, new[] { batch }, CancellationToken.None);
        return stream.ToArray();
    }
}
