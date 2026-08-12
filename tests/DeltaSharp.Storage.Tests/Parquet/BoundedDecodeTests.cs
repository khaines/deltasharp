using System.Diagnostics;
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
        int result = await BoundedDecode.RunAsync(
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
            BoundedDecode.RunAsync<int>(
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
            BoundedDecode.RunAsync<int>(
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
            BoundedDecode.RunAsync<int>(
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
    }

    [Fact]
    public async Task Admission_RejectsBeyondCap_FailFast_OnIsolatedDecoder()
    {
        // The admission cap (CRITICAL fix #2): once maxDetachedDecodes decodes are stranded past their
        // deadline, a NEW decode is rejected fail-fast with DecodeCapacityExhaustedException WITHOUT starting,
        // so the stranded-decode residual can never grow without bound. Exercised on an ISOLATED decoder with
        // caps of 1 so the assertion is deterministic and never touches the (widened) shared tier.
        var decoder = new BoundedDecoder(maxConcurrentDecodes: 1, maxDetachedDecodes: 1);
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
        var decoder = new BoundedDecoder(maxConcurrentDecodes: 2, maxDetachedDecodes: 4);
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
