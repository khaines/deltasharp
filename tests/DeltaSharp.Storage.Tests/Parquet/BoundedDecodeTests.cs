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
        await Assert.ThrowsAsync<DecodeCapacityExhaustedException>(() =>
            decoder.RunAsync<int>(
                _ => { started = true; return Task.FromResult(2); },
                TimeSpan.FromSeconds(30),
                static _ => DeltaStorageException.DecodeBudgetExceeded("must not time out"),
                CancellationToken.None));
        Assert.False(started, "a rejected decode must not start its work.");

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
    public async Task AggregateDeadline_BoundsTotalReadTime_RegardlessOfRowGroupCount()
    {
        // The aggregate-deadline invariant (HIGH finding): a SINGLE per-read wall-clock deadline is shared
        // across the open AND every row-group decode, so worst-case total read time is O(budget), NOT
        // O(RowGroupCount) × budget. A crafted footer declaring millions of row groups must not multiply the
        // budget. Proven with a deterministic stepping clock: each decode step advances the virtual clock, so
        // the shared deadline expires after a BOUNDED number of steps and the read fails closed with
        // DecodeBudgetExceeded having yielded the SAME number of batches for a 5-group and a 50-group file — a
        // per-group reset would instead read them ALL (5 vs 50, no timeout).
        byte[] fiveGroups = await BuildMultiRowGroupFileAsync(rows: 5, rowsPerGroup: 1);
        byte[] fiftyGroups = await BuildMultiRowGroupFileAsync(rows: 50, rowsPerGroup: 1);

        (Exception? thrownA, int batchesA) = await ReadUnderSteppingClockAsync(fiveGroups);
        (Exception? thrownB, int batchesB) = await ReadUnderSteppingClockAsync(fiftyGroups);

        // Both fail closed with the bounded-decode signal...
        Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, Assert.IsType<DeltaStorageException>(thrownA).Kind);
        Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, Assert.IsType<DeltaStorageException>(thrownB).Kind);
        // ...after the SAME bounded number of batches, though one file has 10× the row groups — proving the
        // deadline did NOT reset per group (which would have read all 5 vs all 50 before finishing).
        Assert.Equal(batchesA, batchesB);
        Assert.True(batchesA < 5, $"expected fewer batches than the 5-group file's groups, got {batchesA}.");
    }

    [Fact]
    public async Task Starvation_HealthyDecodeStillSucceeds_WhileStrandsExist_OnIsolatedDecoder()
    {
        // I1 — THE headline Critical: with the Round-1 shared, count-capped scheduler, a handful of
        // non-terminating strands pinned every execution slot, so a QUEUED healthy decode never ran — a
        // permanent process-wide outage from as little as ONE crafted file on a small pod. The redesign runs
        // every decode on its OWN dedicated thread behind only a strand-COUNT cap, so a healthy decode submitted
        // while N strands exist still executes immediately (any free slot) and SUCCEEDS. Proven on an isolated
        // decoder with a generous cap and several strands: the healthy decode returns its value well under the
        // watchdog. Reverting to a shared execution queue that strands can fill would hang here (watchdog fires).
        const int cap = 8;
        const int strandCount = 5; // more than a small pod's ProcessorCount/4 — the Round-1 scheduler width
        var decoder = new BoundedDecoder(maxDetachedDecodes: cap);
        using var gate = new ManualResetEventSlim(initialState: false);

        for (int i = 0; i < strandCount; i++)
        {
            DeltaStorageException timedOut = await Assert.ThrowsAsync<DeltaStorageException>(() =>
                decoder.RunAsync<int>(
                    _ => { gate.Wait(); return Task.FromResult(0); },
                    TimeSpan.FromMilliseconds(100),
                    static _ => DeltaStorageException.DecodeBudgetExceeded("strand"),
                    CancellationToken.None));
            Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, timedOut.Kind);
        }

        await WaitForAsync(() => decoder.DetachedDecodeCount == strandCount);

        // The invariant: a healthy decode submitted while 5 strands exist STILL executes and succeeds.
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
    public async Task AtomicCap_ConcurrentBurst_NeverAdmitsMoreThanCap_OnIsolatedDecoder()
    {
        // I2 — the atomic hard cap: a concurrent BURST must never admit more than the cap (the Round-1
        // check-then-act let cap=1 admit 8). Reservation is a single Interlocked.Increment that backs out if it
        // lands over the cap, so exactly `cap` of the burst are admitted (become strands, holding their slot
        // because the gate never opens) and every other call is rejected fail-fast with the DISTINCT
        // DecodeCapacityExhaustedException — WITHOUT starting. The detached count converges to EXACTLY the cap
        // and never exceeds it.
        const int cap = 4;
        const int burst = 40;
        var decoder = new BoundedDecoder(maxDetachedDecodes: cap);
        using var gate = new ManualResetEventSlim(initialState: false);
        int rejected = 0;
        int peakReserved = 0;

        Task[] tasks = Enumerable.Range(0, burst).Select(_ => Task.Run(async () =>
        {
            try
            {
                await decoder.RunAsync<int>(
                    _ => { gate.Wait(); return Task.FromResult(1); },
                    TimeSpan.FromMilliseconds(100),
                    static _ => DeltaStorageException.DecodeBudgetExceeded("strand"),
                    CancellationToken.None);
            }
            catch (DecodeCapacityExhaustedException)
            {
                Interlocked.Increment(ref rejected);
            }
            catch (DeltaStorageException)
            {
                // Admitted then timed out → this call became a strand (holds its slot).
            }

            // Sample the reserved count concurrently; it must never exceed the cap.
            int seen = decoder.ReservedDecodeCount;
            InterlockedMax(ref peakReserved, seen);
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(peakReserved <= cap, $"reserved slots exceeded the cap ({peakReserved} > {cap}) — TOCTOU race.");
        Assert.True(decoder.DetachedDecodeCount <= cap, "detached strands exceeded the cap.");
        await WaitForAsync(() => decoder.DetachedDecodeCount == cap);
        Assert.Equal(burst - cap, rejected); // exactly `cap` admitted, the rest rejected fail-fast

        gate.Set();
        await WaitForAsync(() => decoder.DetachedDecodeCount == 0);

        static void InterlockedMax(ref int target, int value)
        {
            int seen;
            do
            {
                seen = Volatile.Read(ref target);
                if (value <= seen)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, seen) != seen);
        }
    }

    [Fact]
    public async Task ExecutionStartDeadline_BudgetMeasuredFromWorkStart_NotAdmissionOrStrandAge()
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
    }

    [Fact]
    public async Task RowGroupDoor_Timeout_CarriesRowGroupStageDiscriminator_DistinctFromOpen()
    {
        // The #647 row-group door discriminator: a decode-budget trip during a ROW-GROUP decode must carry the
        // stage=row_group signal, DISTINCT from the stage=open signal a footer/open trip carries — both
        // otherwise land on door=data_file. Driven by the deterministic stepping clock: the OPEN completes in
        // budget (its per-step race wins), then the shared aggregate deadline trips at a row-group pre-check,
        // emitting exactly the row_group stage and never the open stage.
        byte[] file = await BuildMultiRowGroupFileAsync(rows: 50, rowsPerGroup: 1);
        var budget = TimeSpan.FromSeconds(10);
        long step = (long)(budget.Ticks * 0.35);
        var clock = new SteppingTimeProvider(step);

        using var telemetry = new DeltaSharp.Storage.Diagnostics.DeltaStorageTelemetry();
        using var meters = new MeterCapture(telemetry.StorageMeter);
        var reader = new ParquetFileReader(
            new ParquetDecodeLimits(decodeTimeBudget: budget), timeProvider: clock, telemetry: telemetry);

        var thrown = await RunWatchdoggedAsync(() => ReadAllAsync(reader, file));
        Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, Assert.IsType<DeltaStorageException>(thrown).Kind);

        MeterCapture.Measurement metric = Assert.Single(meters.ForInstrument("deltasharp.storage.decode.budget_exceeded"));
        Assert.Equal("data_file", metric.Tags["deltasharp.decode.door"]);
        Assert.Equal("row_group", metric.Tags["deltasharp.decode.stage"]);
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

    private static async Task<(Exception? Thrown, int Batches)> ReadUnderSteppingClockAsync(byte[] bytes)
    {
        var budget = TimeSpan.FromSeconds(10);
        // Advance the virtual clock ~0.35 × budget per GetTimestamp call (the deadline queries it once at
        // creation, once at the open, and once per row-group pre-check), so the aggregate deadline expires
        // after ~3 steps regardless of how many row groups follow. A real decode of a valid group finishes in
        // ms and wins its per-step race (the clock's timer never fires), so no batch is spuriously timed out on
        // its own — only the shared aggregate deadline trips the read.
        long step = (long)(budget.Ticks * 0.35);
        var clock = new SteppingTimeProvider(step);
        var reader = new ParquetFileReader(new ParquetDecodeLimits(decodeTimeBudget: budget), timeProvider: clock);
        int batches = 0;
        Exception? thrown = null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            await foreach (ColumnBatch batch in reader.ReadAsync(
                stream, DataSchema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None))
            {
                batches++;
            }
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        return (thrown, batches);
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

    private sealed class ObservableDisposable(TaskCompletionSource disposedSignal) : IDisposable
    {
        internal bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
            disposedSignal.TrySetResult();
        }
    }

    // A deterministic virtual clock whose GetTimestamp advances a fixed step on every query, driving the
    // aggregate DecodeDeadline without any real wall-clock wait. Its timer never fires, so a bounded decode's
    // Task.Delay(budget, this) never wins — the (fast, valid) decode always completes its per-step race and
    // only the shared GetTimestamp-driven deadline pre-check trips the read.
    private sealed class SteppingTimeProvider(long step) : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _timestamp, step) - step;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new NoopTimer();

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
                    if (t.DueTicks is long d && d <= _ticks)
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
            internal long? DueTicks { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                // One-shot (period ignored): schedule a due instant, or disarm on an infinite/negative due.
                DueTicks = dueTime < TimeSpan.Zero ? null : clock.NowTicks() + dueTime.Ticks;
                return true;
            }

            internal void Fire()
            {
                if (DueTicks is null)
                {
                    return;
                }

                DueTicks = null; // one-shot
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
