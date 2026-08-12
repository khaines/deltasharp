using System.Diagnostics;
using System.Linq;
using System.Threading;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Tests.Delta;
using DeltaSharp.Types;
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
        int result = await new BoundedDecoder(maxDetachedDecodes: 4).RunAsync(
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
            new BoundedDecoder(maxDetachedDecodes: 4).RunAsync<int>(
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
            new BoundedDecoder(maxDetachedDecodes: 4).RunAsync<int>(
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
            new BoundedDecoder(maxDetachedDecodes: 4).RunAsync<int>(
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
        var reader = new ParquetFileReader(new ParquetDecodeLimits(decodeTimeBudget: budget));
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
            telemetry: telemetry);

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
        // The admission cap (CRITICAL fix #2): once maxDetachedDecodes decodes are stranded past their
        // deadline, a NEW decode is rejected fail-fast with DecodeCapacityExhaustedException WITHOUT starting,
        // so the stranded-decode residual can never grow without bound. Exercised on an ISOLATED decoder with
        // caps of 1 so the assertion is deterministic and never touches the (widened) shared tier.
        var decoder = new BoundedDecoder(maxDetachedDecodes: 1);
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

        // MEDIUM — the rejection message must be TRUTHFUL enough to distinguish "strand-saturated" (a real
        // detached-strand leak that will not self-heal) from "healthy-concurrency-saturated" (transient
        // in-flight load): it carries the reserved/cap slots AND the detached-strand count. Here the single
        // slot is held by a DETACHED strand, so the message must report detachedStrands=1 (not 0).
        Assert.Contains("reserved=1/1 slots", saturated.Message);
        Assert.Contains("detachedStrands=1", saturated.Message);

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
        var decoder = new BoundedDecoder(maxDetachedDecodes: 4);
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
        var decoder = new BoundedDecoder(maxDetachedDecodes: cap);
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
    public void AtomicCap_ConcurrentBurst_NeverAdmitsMoreThanCap_OnIsolatedDecoder()
    {
        // I2 — the atomic hard cap: a concurrent BURST must never admit more than the cap (the Round-1
        // check-then-act let cap=1 admit N). Reservation is a single Interlocked.Increment that backs out if it
        // lands over the cap, so EXACTLY `cap` of the burst are admitted (become strands, holding their slot
        // because the gate never opens) and every other call is rejected fail-fast with the DISTINCT
        // DecodeCapacityExhaustedException — WITHOUT starting.
        //
        // DETERMINISTIC (High #8): the burst rendezvouses on a Barrier across DEDICATED THREADS (not pool
        // ramp-up, which admitted a nondeterministic subset and made the check-then-act only ~50% red), repeats
        // N≥20 rounds, requires EXACTLY `cap` admitted each round, and samples the number of threads CONCURRENTLY
        // INSIDE the work delegate (which — unlike the raw reserved counter, whose atomic increment-then-backout
        // can transiently read cap+1 for the CORRECT implementation — must never exceed the cap). Against an
        // injected check-then-act reservation this is red 100% of the time (some round admits > cap into work).
        const int cap = 4;
        const int burst = 32;
        const int rounds = 25;

        for (int round = 0; round < rounds; round++)
        {
            var decoder = new BoundedDecoder(maxDetachedDecodes: cap);
            using var gate = new ManualResetEventSlim(initialState: false);
            using var barrier = new Barrier(burst);
            int rejected = 0;
            int inWork = 0;
            int admitted = 0;
            int capViolations = 0;

            var threads = new Thread[burst];
            for (int t = 0; t < burst; t++)
            {
                threads[t] = new Thread(() =>
                {
                    // Rendezvous so every thread hits the atomic reservation as simultaneously as the OS allows.
                    barrier.SignalAndWait();
                    try
                    {
                        decoder.RunAsync<int>(
                            _ =>
                            {
                                // Sampled by an ADMITTED strand while it holds its slot: the number of threads
                                // CONCURRENTLY inside the work delegate must never exceed the cap. Mark admission
                                // and entry, sample the peak, then hold the slot on the never-opening gate so it
                                // becomes a strand for the round's accounting.
                                Interlocked.Increment(ref admitted);
                                int concurrent = Interlocked.Increment(ref inWork);
                                if (concurrent > cap)
                                {
                                    Interlocked.Increment(ref capViolations);
                                }

                                gate.Wait();
                                Interlocked.Decrement(ref inWork);
                                return Task.FromResult(1);
                            },
                            TimeSpan.FromMilliseconds(80),
                            static _ => DeltaStorageException.DecodeBudgetExceeded("strand"),
                            CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (DecodeCapacityExhaustedException)
                    {
                        Interlocked.Increment(ref rejected);
                    }
                    catch (DeltaStorageException)
                    {
                        // Admitted then timed out (its budget elapsed while gate-blocked) → it became a strand.
                    }
                })
                {
                    IsBackground = true,
                    Name = $"burst-{round}-{t}",
                };
                threads[t].Start();
            }

            // Wait for the admission decisions to settle: exactly `cap` admitted strands, the rest rejected.
            SpinWaitFor(() => decoder.DetachedDecodeCount == cap && Volatile.Read(ref rejected) == burst - cap);

            Assert.Equal(0, capViolations); // more than `cap` threads never ran the work delegate concurrently
            Assert.Equal(cap, admitted); // EXACTLY cap admitted this round
            Assert.Equal(burst - cap, rejected); // the rest rejected fail-fast, WITHOUT starting
            Assert.Equal(cap, decoder.DetachedDecodeCount);

            // Drain the round's strands before the next round so accounting is independent.
            gate.Set();
            foreach (Thread thread in threads)
            {
                Assert.True(thread.Join(Watchdog), "a burst thread did not settle within the watchdog.");
            }

            SpinWaitFor(() => decoder.DetachedDecodeCount == 0);
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
            var decoder = new BoundedDecoder(maxDetachedDecodes: 4);
            using var strandGate = new ManualResetEventSlim(initialState: false);
            var strandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<int> strandTask = decoder.RunAsync<int>(
                _ => { strandStarted.TrySetResult(); strandGate.Wait(); return Task.FromResult(0); },
                TimeSpan.FromSeconds(1),
                static _ => DeltaStorageException.DecodeBudgetExceeded("strand"),
                CancellationToken.None,
                timeProvider: clock);

            await strandStarted.Task; // strand executing; its delay armed at clock=T0, fires at T0+1s
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

            await healthyStarted.Task; // healthy executing; its delay armed at NOW (its own start), fires at NOW+1s
            clock.Advance(TimeSpan.FromMilliseconds(500)); // < the healthy budget FROM ITS OWN START → must not trip
            proceed.Set();
            int result = await healthyTask;
            Assert.Equal(42, result); // succeeded on its own full budget, unaffected by the aged strand

            strandGate.Set();
            await WaitForAsync(() => decoder.DetachedDecodeCount == 0);
        });
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

    [Fact]
    public async Task ByteAwareCap_RejectsWhenMemoryBudgetExhausted_BeforeCountCap()
    {
        // Critical #1 — the BYTE-AWARE admission cap: a count-only cap admits `cap` strands regardless of their
        // retained bytes, so a handful of large-footprint decodes OOM-kill the pod long before the count control
        // engages. Admission must reserve each strand's ESTIMATED RETAINED BYTES against a documented memory
        // budget and reject with DecodeCapacityExhaustedException when the BYTE budget is exhausted — even
        // though the COUNT cap is nowhere near reached. Proven with a generous count cap (100) but a memory
        // budget that fits only 2 strands of 64 MiB each: the 3rd is rejected by the BYTE bound.
        const long strandBytes = 64L * 1024 * 1024;
        var decoder = new BoundedDecoder(
            maxDetachedDecodes: 100, // count cap is deliberately far from binding
            memoryBudgetBytes: strandBytes * 2 + (strandBytes / 2)); // fits exactly 2 such strands
        using var gate = new ManualResetEventSlim(initialState: false);

        // Two strands consume the byte budget (each times out and holds its bytes).
        for (int i = 0; i < 2; i++)
        {
            await RunWatchdoggedThrowsAsync(() => decoder.RunAsync<int>(
                _ => { gate.Wait(); return Task.FromResult(0); },
                TimeSpan.FromMilliseconds(80),
                static _ => DeltaStorageException.DecodeBudgetExceeded("strand"),
                CancellationToken.None,
                estimatedRetainedBytes: strandBytes));
        }

        await WaitForAsync(() => decoder.DetachedDecodeCount == 2);
        Assert.Equal(strandBytes * 2, decoder.ReservedDecodeBytes);

        // The 3rd decode is rejected by the BYTE budget though the COUNT cap (100) is nowhere near reached.
        DecodeCapacityExhaustedException rejected = await Assert.ThrowsAsync<DecodeCapacityExhaustedException>(() =>
            decoder.RunAsync<int>(
                _ => Task.FromResult(1),
                TimeSpan.FromSeconds(5),
                static _ => DeltaStorageException.DecodeBudgetExceeded("must not run"),
                CancellationToken.None,
                estimatedRetainedBytes: strandBytes));
        // The truthful saturation message distinguishes byte-saturation (reservedBytes near budget) from count.
        Assert.Contains("reservedBytes", rejected.Message, StringComparison.Ordinal);
        Assert.True(decoder.DetachedDecodeCount < decoder.MaxDetachedDecodes,
            "the count cap must NOT be the binding constraint here — the byte budget is.");

        gate.Set();
        await WaitForAsync(() => decoder.DetachedDecodeCount == 0);
    }

    [Fact]
    public async Task CallerCancellation_ReleasesLease_AndIsNotCountedAsAStrand()
    {
        // High #3 + High #6 — a routine caller cancellation of a healthy in-flight decode must (a) fire
        // onWorkSettled EXACTLY ONCE so a caller-held lease is released (no leak), and (b) NOT inflate the
        // detached-strand gauge (a cancelled healthy decode is not a strand). Proven directly: a decode is
        // cancelled mid-flight; the onWorkSettled callback fires exactly once and the detached count stays 0.
        var decoder = new BoundedDecoder(maxDetachedDecodes: 4);
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
        var decoder = new BoundedDecoder(maxDetachedDecodes: 4);
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
        Assert.Equal(0, decoder.ReservedDecodeCount); // the reservation was fully backed out
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
