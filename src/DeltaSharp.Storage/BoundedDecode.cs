using DeltaSharp.Storage.Diagnostics;

namespace DeltaSharp.Storage;

/// <summary>How a <see cref="BoundedDecoder"/> runs the untrusted decode delegate.</summary>
internal enum DecodeExecution
{
    /// <summary>Run on the shared <see cref="System.Threading.ThreadPool"/> via <see cref="Task.Run(Func{Task})"/>.
    /// Used by the <b>data-file</b> door: <c>DecodeGroupAsync</c>/<c>OpenAsync</c> await async storage reads that
    /// resume on the pool anyway, so a dedicated thread would sit blocked in <c>GetResult()</c> while the
    /// non-terminating CPU loop still ran on the pool — it bought ZERO isolation for a large per-decode cost.
    /// An abandoned non-terminating decode holds ~1 pool thread + its retained bytes until process restart;
    /// that residual is bounded by the byte-aware admission cap (there is no shared custom scheduler for it to
    /// starve — the pool injects threads).</summary>
    Pool,

    /// <summary>Run on its own dedicated background <see cref="Thread"/> (<see cref="Thread.IsBackground"/> =
    /// <see langword="true"/>). Used by the <b>checkpoint</b> door only: there the decode is <b>synchronous</b>
    /// over a pre-buffered <c>byte[]</c>, so the dedicated thread genuinely CONTAINS the CPU-bound work (it does
    /// not hand it back to the pool at an <c>await</c>). A stranded thread holds its own thread + its isolated
    /// byte copy until process restart; that residual is bounded by the byte-aware admission cap.</summary>
    DedicatedThread,
}

/// <summary>
/// A shared <b>bounded-time (wall-clock deadline) decode policy</b> for handing untrusted bytes to a
/// decoder that ignores the <see cref="CancellationToken"/> (design §5.4 C-DECODE — the bounded wall-clock
/// decode ceiling). It converts a non-terminating decode into a deterministic, typed fail-closed exception so a
/// crafted <c>_delta_log</c> / data-file cannot stall a table read indefinitely (#647, #699, #716), and it
/// bounds the <b>byte residual</b> of the abandoned work so a crafted input cannot exhaust process memory
/// before the ceiling engages.
/// </summary>
/// <remarks>
/// <para>Parquet.Net (6.0.3) can be driven by a single corrupted byte (a flipped terminal footer
/// <c>STOP</c>, a corrupt data-page header) into effectively unbounded, <b>synchronous</b> CPU work that
/// observes <b>no</b> cancellation mid-decode. A hang is not an exception, so no <c>try</c>/<c>catch</c> and
/// no token can interrupt it, and <b>.NET cannot abort a running thread</b> — a non-terminating decode
/// therefore cannot be reclaimed. The only things this policy can do are (a) bound the <b>retained bytes</b>
/// of abandoned work with a <b>byte-aware admission cap</b>, (b) prevent it from self-renewing (the checkpoint
/// negative cache), and (c) ensure a strand never consumes the capacity a <b>healthy</b> decode needs.</para>
/// <para><b>Containment mechanism (the Round-4 redesign).</b> Each untrusted decode is admitted only after
/// <b>atomically reserving both a strand slot AND its estimated retained bytes</b> under a hard per-door
/// <b>memory budget</b> (a fraction of process/GC memory). The <b>data-file</b> door runs the decode on the
/// shared <see cref="System.Threading.ThreadPool"/> (<see cref="DecodeExecution.Pool"/>): a Round-2 dedicated
/// thread bought no isolation there because <c>DecodeGroupAsync</c> awaits async reads that resume on the pool,
/// so the dedicated thread sat blocked in <c>GetResult()</c> while the non-terminating loop ran on the pool
/// anyway — pure cost (measured 68–74× per decode) for a false "never the ThreadPool" claim. The
/// <b>checkpoint</b> door keeps its dedicated thread (<see cref="DecodeExecution.DedicatedThread"/>) because
/// there the decode is synchronous over a pre-buffered <c>byte[]</c>, so the thread genuinely contains it. In
/// neither door is there a shared custom scheduler a strand can starve: the pool injects threads, and each
/// checkpoint strand has its own thread.</para>
/// <list type="bullet">
///   <item><b>Byte-aware hard cap (I2).</b> Admission reserves the decode's estimated retained bytes with a
///   single <see cref="Interlocked.Add(ref long, long)"/> that backs out if it lands over the door's memory
///   budget (and a secondary strand-COUNT cap sized so <c>count-cap × representative-strand-bytes ≤ memory
///   budget</c>). There is no check-then-act TOCTOU, so a burst can never admit past either bound. This fixes
///   the Round-2 count-only cap, under which tens of GiB of strands (64 checkpoint × ≤512&#160;MiB + 128
///   data-file × up to 4&#160;GiB) OOM-killed the pod at ~15–30 strands BEFORE the count cap engaged. The
///   reservation is released when the work <i>actually</i> completes (in-budget OR late); for a genuine
///   non-terminating strand it is never released — the bounded byte residual.</item>
///   <item><b>No starvation (I1).</b> Neither door has a shared, count-capped scheduler. A healthy decode
///   submitted while N strands exist is admitted as long as the memory budget has room; only when strands fill
///   the budget does a new decode fail fast with a distinct, fail-closed
///   <see cref="DecodeCapacityExhaustedException"/>, never a decode-timeout.</item>
///   <item><b>Execution-start deadline (I3).</b> The work signals a start gate as its FIRST statement inside
///   the pool task / thread; the <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> budget is
///   armed only after that signal. Admission latency is never charged to the decode budget.</item>
///   <item><b>Per-operation deadline scope (I7).</b> The budget passed in bounds ONLY one decode operation
///   (one open, one row-group decode, or one buffered checkpoint part). The caller must NOT pass a deadline
///   that also spans streaming iteration, consumer time, or storage I/O — those are not the decode.</item>
///   <item><b>Per-door isolation (I5).</b> The data-file and checkpoint doors have <b>independent</b> decoders
///   with independent memory budgets, so a poisoned data file can never exhaust the capacity healthy checkpoint
///   decodes need (and vice-versa).</item>
/// </list>
/// <para><b>Bounded residual / accepted degradation.</b> The worst-case retained cost is bounded by each door's
/// <b>memory budget</b> (not merely a strand count). A late-completing SUCCESSFUL result is disposed via
/// <c>onAbandonedResult</c>; a strand over a caller-shared reader keeps that reader alive via an
/// <c>onWorkSettled</c> lease release (the data-file door) so it never touches a caller-disposed object, and
/// the checkpoint door hands its strand an isolated in-memory copy of the bytes. Under a sustained flood of
/// <b>distinct</b> crafted inputs a door's budget can fill with strands; further decodes on that door then fail
/// fast (<see cref="DecodeCapacityExhaustedException"/>) — a bounded, contained degradation, not an OOM kill.
/// The checkpoint layer additionally negatively caches a timed-out checkpoint identity so a known-bad
/// checkpoint is not re-decoded on every snapshot load (which is what stops strands self-renewing). A routine
/// caller cancellation of a HEALTHY decode is NOT counted as a strand (the detached gauge is not inflated by
/// it).</para>
/// <para><b>NativeAOT-safe:</b> <see cref="Task.Run(Func{Task})"/>, a dedicated <see cref="Thread"/>,
/// <see cref="Interlocked"/>, <see cref="TaskCompletionSource"/>,
/// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>, and a linked
/// <see cref="CancellationTokenSource"/> use no dynamic codegen or reflection.</para>
/// </remarks>
internal static class BoundedDecode
{
    /// <summary>The conservative default wall-clock budget for a single decode OPERATION (one open or one
    /// row-group decode). A real decode of a legitimate part completes in milliseconds; this ceiling only ever
    /// trips a genuinely non-terminating decode of crafted bytes. It is a conservative documented default;
    /// benchmark-backed calibration (including the memory-budget dimension) is tracked in #802. The production
    /// config seam that would let an operator lower it per tier is tracked in #803 — it is currently settable
    /// only from tests.</summary>
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    /// <summary>The upper bound accepted for a configured decode budget (24&#160;hours). A budget beyond this
    /// is a misconfiguration (it disables the DoS control), rejected fail-fast at construction rather than
    /// silently letting a non-terminating decode run effectively forever.</summary>
    internal static readonly TimeSpan MaxBudget = TimeSpan.FromHours(24);

    /// <summary>The representative retained-bytes charge accounted per <b>data-file</b> strand for SIZING the
    /// count cap against the memory budget (64&#160;MiB — a generous estimate of a stranded row-group decode's
    /// live footprint). The EXACT per-decode allocation is separately bounded by
    /// <c>ParquetFileReader.EnsureDecodeCeiling</c> (≤ <c>MaxRowGroupDecodedBytes</c>); this figure exists only
    /// to keep <c>count-cap × this ≤ memory budget</c> provable. Calibration is tracked in #802.</summary>
    internal const long DataFileRepresentativeStrandBytes = 64L * 1024 * 1024;

    /// <summary>The representative retained-bytes charge accounted per <b>checkpoint</b> strand (512&#160;MiB —
    /// the isolated buffered part copy a checkpoint strand actually pins, <c>MaxCheckpointPartBytes</c>). Unlike
    /// the data-file figure this is the strand's <b>real</b> retained footprint, so the checkpoint door charges
    /// each admission its actual buffered length.</summary>
    internal const long CheckpointRepresentativeStrandBytes = 512L * 1024 * 1024;

    /// <summary>The process/GC memory the doors size their budgets against —
    /// <see cref="GCMemoryInfo.TotalAvailableMemoryBytes"/> (container-cgroup-aware), with a conservative
    /// 4&#160;GiB fallback when the runtime reports it as unknown. Captured once at type init.</summary>
    internal static long ProcessMemoryBytes { get; } = DeriveProcessMemoryBytes();

    private static long DeriveProcessMemoryBytes()
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available > 0 ? available : 4L * 1024 * 1024 * 1024;
    }

    /// <summary>The per-door memory budget the byte-aware admission cap reserves against — 1/8 of process
    /// memory per door (design §5.4). The two doors are independent (I5); at most 1/4 of process memory can be
    /// pinned by strands across both doors, well under any pod limit, so the control engages long before an OOM
    /// kill. Calibration is tracked in #802.</summary>
    internal static long DataFileMemoryBudgetBytes => ProcessMemoryBytes / 8;

    /// <inheritdoc cref="DataFileMemoryBudgetBytes"/>
    internal static long CheckpointMemoryBudgetBytes => ProcessMemoryBytes / 8;

    /// <summary>The default hard cap on the COUNT of concurrently-reserved (running + detached) untrusted
    /// decodes on the <b>data-file</b> door. It is derived from the memory budget so
    /// <c>cap × DataFileRepresentativeStrandBytes ≤ DataFileMemoryBudgetBytes</c> is provable (the memory
    /// budget, not the count, is the load-bearing bound), clamped to a generous ceiling so it never throttles
    /// healthy scan concurrency on a large pod. Calibration is tracked in #802.</summary>
    internal static int DefaultMaxDataFileDetachedDecodes =>
        DeriveCountCap(DataFileMemoryBudgetBytes, DataFileRepresentativeStrandBytes, Math.Max(128, Environment.ProcessorCount * 16));

    /// <summary>The default hard cap on the COUNT of concurrently-reserved decodes on the <b>checkpoint</b>
    /// door — independent of the data-file cap (I5). Derived from the checkpoint memory budget so
    /// <c>cap × CheckpointRepresentativeStrandBytes ≤ CheckpointMemoryBudgetBytes</c> is provable.</summary>
    internal static int DefaultMaxCheckpointDetachedDecodes =>
        DeriveCountCap(CheckpointMemoryBudgetBytes, CheckpointRepresentativeStrandBytes, Math.Max(64, Environment.ProcessorCount * 8));

    private static int DeriveCountCap(long memoryBudgetBytes, long representativeStrandBytes, int ceiling)
    {
        long derived = memoryBudgetBytes / Math.Max(1, representativeStrandBytes);
        return (int)Math.Clamp(derived, 1, ceiling);
    }

    // The two process-wide, per-door decoders. Independent memory budgets confine a flood on one door away from
    // the other (I5). Tests exercise the admission/scheduling semantics on ISOLATED BoundedDecoder instances
    // with tiny caps/budgets (and can INJECT one into ParquetFileReader / DeltaLog via the constructor seams),
    // so the production defaults here are exercised as-is by the door integration tests (no test-only widening
    // masks the production behavior).
    internal static readonly BoundedDecoder DataFileDecoder =
        new(DefaultMaxDataFileDetachedDecodes, DataFileMemoryBudgetBytes, DecodeExecution.Pool);

    internal static readonly BoundedDecoder CheckpointDecoder =
        new(DefaultMaxCheckpointDetachedDecodes, CheckpointMemoryBudgetBytes, DecodeExecution.DedicatedThread);

    /// <summary>The total count of detached (running-past-deadline) strands across both doors — exposed as the
    /// <c>deltasharp.storage.decode.detached</c> observability gauge and for tests that assert strands drain.</summary>
    internal static int DetachedDecodeCount => DataFileDecoder.DetachedDecodeCount + CheckpointDecoder.DetachedDecodeCount;

    /// <summary>The detached-strand count on the data-file door (observability gauge dimension).</summary>
    internal static int DataFileDetachedDecodeCount => DataFileDecoder.DetachedDecodeCount;

    /// <summary>The detached-strand count on the checkpoint door (observability gauge dimension).</summary>
    internal static int CheckpointDetachedDecodeCount => CheckpointDecoder.DetachedDecodeCount;
}

/// <summary>
/// One bounded-decode execution surface: a byte-aware hard cap on concurrently-reserved decodes, each decode
/// run on the shared <see cref="System.Threading.ThreadPool"/> (data-file door) or its own dedicated background
/// <see cref="Thread"/> (checkpoint door). Production uses one shared instance per door
/// (<see cref="BoundedDecode"/>); tests construct isolated instances with tiny caps to exercise the
/// admission/scheduling contract deterministically. See <see cref="BoundedDecode"/> for the full rationale.
/// </summary>
internal sealed class BoundedDecoder
{
    private readonly int _maxDetachedDecodes;
    private readonly long _memoryBudgetBytes;
    private readonly DecodeExecution _execution;

    // Atomically-reserved slots: decodes currently running (healthy, transient) PLUS detached strands. Reserved
    // BEFORE any work starts (I2, no TOCTOU) and released when the work's task actually settles (in-budget OR
    // late). A genuine non-terminating strand never settles, so it holds its slot forever — the bounded residual.
    private int _reserved;

    // Atomically-reserved retained bytes (the byte-aware cap, the load-bearing bound). Same lifecycle as
    // _reserved; a strand pins its estimated bytes until it settles.
    private long _reservedBytes;

    // Detached strands only (abandoned past their deadline, still running or never-terminating). Incremented
    // when a decode is abandoned by a GENUINE deadline expiry (never by a routine caller cancellation);
    // decremented when (if) that abandoned work finally settles. Exposed as the observability gauge.
    private int _detached;

    internal BoundedDecoder(
        int maxDetachedDecodes,
        long memoryBudgetBytes = long.MaxValue,
        DecodeExecution execution = DecodeExecution.Pool)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDetachedDecodes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryBudgetBytes, 1);
        _maxDetachedDecodes = maxDetachedDecodes;
        _memoryBudgetBytes = memoryBudgetBytes;
        _execution = execution;
    }

    /// <summary>The admission cap on concurrently-reserved (running + detached) decodes for this decoder.</summary>
    internal int MaxDetachedDecodes => _maxDetachedDecodes;

    /// <summary>The byte-aware admission budget (the load-bearing memory bound).</summary>
    internal long MemoryBudgetBytes => _memoryBudgetBytes;

    /// <summary>The current count of detached (running-past-deadline) strands — exposed for the observability
    /// gauge and for tests that assert the admission cap and that strands drain.</summary>
    internal int DetachedDecodeCount => Volatile.Read(ref _detached);

    /// <summary>The current count of reserved slots (running + detached) — exposed for tests that assert the
    /// atomic reservation cap under a concurrent burst.</summary>
    internal int ReservedDecodeCount => Volatile.Read(ref _reserved);

    /// <summary>The current reserved retained bytes (running + detached) — exposed for tests that assert the
    /// byte-aware admission cap.</summary>
    internal long ReservedDecodeBytes => Volatile.Read(ref _reservedBytes);

    /// <summary>
    /// Runs <paramref name="work"/> under a wall-clock <paramref name="budget"/> (measured from EXECUTION start,
    /// I3), after atomically reserving a strand slot AND <paramref name="estimatedRetainedBytes"/> against this
    /// door's memory budget (I2). Returns the work's result when it finishes first (surfacing the work's own
    /// outcome — a value, a typed fail-closed exception, or cancellation — <b>unwrapped</b>). If the budget
    /// expires first, throws the exception produced by <paramref name="onTimeout"/> (a FIXED, sanitized
    /// fail-closed message) and leaves the work running detached (bounded by the byte budget). Caller
    /// cancellation is distinguished from a genuine deadline: a cancelled <paramref name="cancellationToken"/>
    /// surfaces <see cref="OperationCanceledException"/>, never the timeout exception, and is NOT counted as a
    /// strand.
    /// </summary>
    /// <typeparam name="T">The decode result type.</typeparam>
    /// <param name="work">The decode to bound. It receives a linked token that also trips on caller
    /// cancellation and on deadline expiry (a courtesy — the underlying decoder may ignore it). It must be a
    /// single decode OPERATION; do not let it span streaming iteration or storage I/O (I7).</param>
    /// <param name="budget">The wall-clock deadline for this ONE operation, measured from EXECUTION start (I3);
    /// must be positive.</param>
    /// <param name="onTimeout">Produces the typed fail-closed exception to throw on deadline expiry. The
    /// message MUST be fixed/sanitized (no untrusted byte content).</param>
    /// <param name="cancellationToken">The caller's real cancellation, honored via the linked token.</param>
    /// <param name="onAbandonedResult">An optional disposer invoked if the work completes SUCCESSFULLY after
    /// the deadline (a late win): it disposes the abandoned result so a post-deadline success is never leaked.
    /// Never invoked on the in-budget success path (the caller owns the result there).</param>
    /// <param name="onWorkSettled">An optional callback invoked EXACTLY ONCE on EVERY exit path — including the
    /// pre-start cancellation and capacity-rejection throws (so a caller that took a resource lease before
    /// calling never leaks it — the lease-leak fix). The data-file door uses it to release a caller-shared
    /// <see cref="Parquet.ParquetReader"/> lease only once the (possibly stranded) decode has stopped touching
    /// it (I6). For a never-terminating strand that was admitted it is never invoked, so the reader stays alive
    /// (bounded residual) rather than being disposed out from under the strand.</param>
    /// <param name="timeProvider">The clock the deadline is measured against (default
    /// <see cref="TimeProvider.System"/>); injected so deadline tests can drive it deterministically.</param>
    /// <param name="estimatedRetainedBytes">The decode's estimated retained-bytes footprint, reserved against
    /// the door's memory budget (I2). Zero disables the byte-aware bound for a call (used by unit tests that
    /// exercise the strand-COUNT cap in isolation).</param>
    /// <exception cref="DecodeCapacityExhaustedException">This door is at capacity (its strand-count cap OR its
    /// memory budget is exhausted) — the call is rejected fail-fast WITHOUT starting.</exception>
    internal async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> work,
        TimeSpan budget,
        Func<TimeSpan, Exception> onTimeout,
        CancellationToken cancellationToken,
        Action<T>? onAbandonedResult = null,
        Action? onWorkSettled = null,
        TimeProvider? timeProvider = null,
        long estimatedRetainedBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(onTimeout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget.Ticks, nameof(budget));

        long estBytes = Math.Max(0L, estimatedRetainedBytes);

        // onWorkSettled EXACTLY ONCE on every path. The data-file door releases its ParquetReader lease here, so
        // a routine caller-cancellation (whose first act below is ThrowIfCancellationRequested) or a capacity
        // rejection BEFORE the decode starts must still release it (the lease-leak fix). The success/late/fault
        // continuation and these pre-start inline throws are mutually exclusive, but the guard makes it robust.
        int settled = 0;
        void Settle()
        {
            if (Interlocked.Exchange(ref settled, 1) == 0)
            {
                onWorkSettled?.Invoke();
            }
        }

        try
        {
            // Pre-start caller cancellation: never start a decode for an already-cancelled caller.
            cancellationToken.ThrowIfCancellationRequested();

            // I2 — ATOMIC byte-aware hard cap, reserved BEFORE any work starts. A single Interlocked.Increment
            // reserves the strand slot and a single Interlocked.Add reserves the bytes; either landing over its
            // bound backs out and rejects fail-fast (no check-then-act window). Over-cap surfaces a DISTINCT
            // fail-closed DecodeCapacityExhaustedException (never a decode-timeout, never negatively cached) —
            // the decode never starts, so it never adds to the strand residual.
            Reserve(estBytes);
        }
        catch
        {
            // Pre-start throw (cancellation or capacity): fire onWorkSettled so the caller's lease is released
            // exactly once even though no work task will ever settle to trigger the continuation.
            Settle();
            throw;
        }

        timeProvider ??= TimeProvider.System;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workTcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Release the reservation (count + bytes) EXACTLY when the work's task settles (in-budget OR late) and
        // fire onWorkSettled. Registered on the work task so it fires on completion/fault regardless of who is
        // awaiting; a true strand never settles, so the slot/bytes stay reserved (the bounded residual).
        _ = workTcs.Task.ContinueWith(
            (_, state) =>
            {
                var self = (BoundedDecoder)state!;
                Interlocked.Decrement(ref self._reserved);
                Interlocked.Add(ref self._reservedBytes, -estBytes);
                Settle();
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            StartExecution(work, linked, started, workTcs);
        }
        catch (Exception ex)
        {
            // Execution start failed (e.g. thread-resource exhaustion) BEFORE work began. Fault the work task so
            // the reservation-release continuation fires (no leaked slot/bytes, and onWorkSettled fires),
            // dispose the linked source, and surface the failure to the caller.
            workTcs.TrySetException(ex);
            linked.Dispose();
            throw;
        }

        // Wait for the work to ACTUALLY START executing before arming the deadline (I3). This is a thread hop,
        // not an admission/queue wait, so it is negligible and — crucially — never counted against the budget.
        // Cancellable: if the caller cancels during the start hop, do not block on it — the linked token is
        // already cancelled, so the work (once it runs) observes cancellation and settles, draining its
        // reservation via the continuation; abandon it without inflating the strand gauge and surface the OCE.
        try
        {
            await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AbandonInBackground(workTcs.Task, onAbandonedResult, linked, countAsStrand: false);
            throw;
        }

        Task delayTask = Task.Delay(budget, timeProvider, linked.Token);

        await Task.WhenAny(workTcs.Task, delayTask).ConfigureAwait(false);

        if (workTcs.Task.IsCompleted)
        {
            // Work won the race (success OR a typed/library fault OR its own cancellation). Cancel the delay
            // timer and surface the work's own outcome UNWRAPPED. CancelAsync (not Cancel) runs the delay's
            // cancellation callback off this path so an inline callback fault cannot mask the work's outcome.
            await linked.CancelAsync().ConfigureAwait(false);
            linked.Dispose();
            return await workTcs.Task.ConfigureAwait(false);
        }

        // The delay won: either the caller cancelled, or the deadline genuinely expired. Distinguish them so
        // caller cancellation stays control flow (OperationCanceledException) and is NEVER masked as a timeout.
        if (cancellationToken.IsCancellationRequested)
        {
            // Routine caller cancellation of a (likely healthy) decode: do NOT count it as a strand (the
            // detached gauge is not inflated by cancellation). The linked token is already cancelled (it is
            // linked to cancellationToken), so a cooperative decode terminates promptly and its reservation
            // drains. Observe/dispose the eventual outcome without touching the strand gauge.
            AbandonInBackground(workTcs.Task, onAbandonedResult, linked, countAsStrand: false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Genuine deadline expiry. Cancel the linked token (a courtesy, in case the detached decode observes
        // it), register the abandoned work as a strand so its eventual outcome is observed/disposed and the
        // strand gauge is tracked, and fail closed with the caller-supplied typed exception.
        await linked.CancelAsync().ConfigureAwait(false);
        AbandonInBackground(workTcs.Task, onAbandonedResult, linked, countAsStrand: true);
        throw onTimeout(budget);
    }

    // Atomically reserve one strand slot AND estBytes against the memory budget. Backs out and throws a
    // fail-closed DecodeCapacityExhaustedException with a TRUTHFUL message (reserved/cap + reserved-bytes/budget
    // + detached count) if either bound would be exceeded, so a "strand-saturated" vs "healthy-concurrency
    // saturated" condition is distinguishable in the surfaced text.
    private void Reserve(long estBytes)
    {
        int reserved = Interlocked.Increment(ref _reserved);
        if (reserved > _maxDetachedDecodes)
        {
            Interlocked.Decrement(ref _reserved);
            throw Saturated(reserved - 1, Volatile.Read(ref _reservedBytes), estBytes);
        }

        long newBytes = Interlocked.Add(ref _reservedBytes, estBytes);
        if (newBytes > _memoryBudgetBytes)
        {
            Interlocked.Add(ref _reservedBytes, -estBytes);
            Interlocked.Decrement(ref _reserved);
            throw Saturated(reserved - 1, newBytes - estBytes, estBytes);
        }
    }

    private DecodeCapacityExhaustedException Saturated(long reservedBefore, long reservedBytesBefore, long estBytes) =>
        new(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"The bounded-decode worker is at capacity and rejected the decode without starting: "
            + $"reserved={reservedBefore}/{_maxDetachedDecodes} slots, "
            + $"reservedBytes={reservedBytesBefore}/{_memoryBudgetBytes} (this decode +{estBytes}), "
            + $"detachedStrands={Volatile.Read(ref _detached)}. Retry after capacity frees."));

    private void StartExecution<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationTokenSource linked,
        TaskCompletionSource started,
        TaskCompletionSource<T> workTcs)
    {
        if (_execution == DecodeExecution.Pool)
        {
            // Data-file door: run on the shared ThreadPool. The state machine resumes on the pool at every
            // await; a non-terminating synchronous page-decode stretch pins ONE pool thread (bounded by the
            // byte-aware cap). No dedicated thread is created — it would only sit blocked in GetResult() while
            // the loop ran on the pool anyway (Round-4 simplification).
            _ = Task.Run(async () =>
            {
                // I3 — signal EXECUTION start as the FIRST statement so the deadline clock starts here.
                started.TrySetResult();
                try
                {
                    workTcs.TrySetResult(await work(linked.Token).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    workTcs.TrySetException(ex);
                }
            });
            return;
        }

        // Checkpoint door: the decode is synchronous over a pre-buffered byte[], so a dedicated background
        // thread genuinely CONTAINS the CPU-bound work (it never hands it back to the pool at an await).
        var thread = new Thread(() =>
        {
            // I3 — signal EXECUTION start as the FIRST statement so the deadline clock starts here.
            started.TrySetResult();
            try
            {
                workTcs.TrySetResult(work(linked.Token).GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                workTcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "deltasharp-bounded-decode",
        };
        thread.Start();
    }

    // Register an abandoned (detached, running-past-deadline) or a cancelled decode: observe its eventual fault
    // so it is never re-raised on the finalizer thread as an unobserved task exception, dispose a
    // late-completing SUCCESSFUL result so a reader/stream that wins after the deadline is not leaked, and
    // dispose the linked source. When countAsStrand is true (a GENUINE deadline expiry) the strand gauge is
    // incremented here and decremented when the work finally terminates; a never-terminating strand holds
    // exactly one strand-gauge slot for its whole lifetime. When countAsStrand is false (a routine caller
    // cancellation of a healthy decode) the strand gauge is NOT touched (not inflated by cancellation).
    private void AbandonInBackground<T>(
        Task<T> task, Action<T>? onAbandonedResult, CancellationTokenSource linked, bool countAsStrand)
    {
        if (countAsStrand)
        {
            Interlocked.Increment(ref _detached);
        }

        _ = task.ContinueWith(
            (t, state) =>
            {
                try
                {
                    if (t.IsFaulted)
                    {
                        _ = t.Exception; // observe so it is not an unobserved task exception
                    }
                    else if (t.Status == TaskStatus.RanToCompletion && state is Action<T> disposer)
                    {
                        try
                        {
                            disposer(t.Result);
                        }
                        catch
                        {
                            // A dispose-time fault on an abandoned result is best-effort cleanup on a detached
                            // path; it must never surface (there is no caller to observe it).
                        }
                    }
                }
                finally
                {
                    if (countAsStrand)
                    {
                        Interlocked.Decrement(ref _detached);
                    }

                    linked.Dispose();
                }
            },
            onAbandonedResult,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
