using DeltaSharp.Storage.Diagnostics;

namespace DeltaSharp.Storage;

/// <summary>
/// A shared <b>bounded-time (wall-clock deadline) decode policy</b> for handing untrusted bytes to a
/// decoder that ignores the <see cref="CancellationToken"/> (design §5.4 C-DECODE — the bounded wall-clock
/// decode ceiling). It converts a non-terminating decode into a deterministic, typed fail-closed exception so a
/// crafted <c>_delta_log</c> / data-file cannot stall a table read indefinitely (#647, #699, #716), and it
/// bounds the <b>residual</b> cost of the abandoned work so a crafted input cannot leak resources without a
/// ceiling.
/// </summary>
/// <remarks>
/// <para>Parquet.Net (6.0.3) can be driven by a single corrupted byte (a flipped terminal footer
/// <c>STOP</c>, a corrupt data-page header) into effectively unbounded, <b>synchronous</b> CPU work that
/// observes <b>no</b> cancellation mid-decode. A hang is not an exception, so no <c>try</c>/<c>catch</c> and
/// no token can interrupt it, and <b>.NET cannot abort a running thread</b> — a non-terminating decode
/// therefore cannot be reclaimed. The only things this policy can do are (a) bound the <b>count</b> of
/// stranded threads, (b) prevent them from self-renewing (the checkpoint negative cache), and (c) ensure a
/// strand never consumes the capacity a <b>healthy</b> decode needs.</para>
/// <para><b>Containment mechanism (the Round-2 redesign).</b> Each untrusted decode runs on its <b>own
/// dedicated background <see cref="Thread"/></b> (<see cref="Thread.IsBackground"/> = <see langword="true"/>),
/// created only after <b>atomically reserving a strand slot</b> under a hard per-door cap. This deliberately
/// replaces the old <c>LimitedConcurrencyLevelTaskScheduler</c>, which capped how many decodes could run
/// concurrently: a stranded decode pinned one of those scarce slots, so a queued <b>healthy</b> decode never
/// started — a bounded number of crafted files caused a permanent, process-wide decode outage (the Round-1
/// Critical). With a dedicated thread per decode there is <b>no shared queue</b>: a healthy decode submitted
/// while <c>N</c> strands exist (<c>N &lt; cap</c>) reserves a free slot and runs on its own thread
/// immediately — it never waits behind a strand. A stranded thread holds its reserved slot forever but never
/// occupies ThreadPool/scheduling capacity healthy work needs.</para>
/// <list type="bullet">
///   <item><b>No starvation (I1).</b> Because every decode gets its own thread, N live strands never block a
///   healthy decode as long as free slots remain. Only when strands fill the <i>entire</i> per-door cap does a
///   new decode fail fast — a distinct, retryable <see cref="DecodeCapacityExhaustedException"/>, never a
///   decode-timeout.</item>
///   <item><b>Atomic hard cap (I2).</b> The strand slot is reserved with a single
///   <see cref="Interlocked.Increment(ref int)"/> that backs out if it lands over the cap — no check-then-act
///   TOCTOU, so a burst can never admit more than <c>maxDetachedDecodes</c>. The slot is released when the
///   work <i>actually</i> completes (in-budget OR late); for a genuine non-terminating strand it is never
///   released, which is exactly the bounded residual.</item>
///   <item><b>Execution-start deadline (I3).</b> The work signals a start gate as its FIRST statement inside
///   the thread; the <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> budget is armed only
///   after that signal. Thread-start/admission latency is never charged to the decode budget.</item>
///   <item><b>Per-door isolation (I5).</b> The data-file and checkpoint doors have <b>independent</b> decoders
///   with independent caps, so a poisoned data file can never exhaust the capacity healthy checkpoint decodes
///   need (and vice-versa).</item>
/// </list>
/// <para><b>No healthy-concurrency throttle (I9).</b> The old process-wide <c>ProcessorCount/4</c> cap on
/// concurrently-executing decodes is gone. The per-door cap is sized generously — far above any realistic
/// pod's healthy concurrent-decode count — so it bounds the accumulation of <b>strands</b> without throttling
/// healthy decodes, which release their slot on completion.</para>
/// <para><b>Bounded residual / accepted degradation.</b> The worst-case retained cost is
/// <c>maxDetachedDecodes × (max bytes one decode pins)</c> per door. A late-completing SUCCESSFUL result is
/// disposed via <c>onAbandonedResult</c>; a strand over a caller-shared reader keeps that reader alive via an
/// <c>onWorkSettled</c> lease release (the data-file door) so it never touches a caller-disposed object, and
/// the checkpoint door hands its strand an isolated in-memory copy of the bytes. Under a sustained flood of
/// <b>distinct</b> crafted inputs a door's cap can fill with strands; further decodes on that door then fail
/// fast (<see cref="DecodeCapacityExhaustedException"/>) — a bounded, contained degradation, not a shared-pool
/// meltdown. The checkpoint layer additionally negatively caches a timed-out checkpoint identity so a
/// known-bad checkpoint is not re-decoded on every snapshot load (which is what stops strands self-renewing).</para>
/// <para><b>NativeAOT-safe:</b> a dedicated <see cref="Thread"/>, <see cref="Interlocked"/>,
/// <see cref="TaskCompletionSource"/>, <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>,
/// and a linked <see cref="CancellationTokenSource"/> use no dynamic codegen or reflection.</para>
/// </remarks>
internal static class BoundedDecode
{
    /// <summary>The conservative default wall-clock budget for a single decode (open or row-group). A real
    /// decode of a legitimate part completes in milliseconds; this ceiling only ever trips a genuinely
    /// non-terminating decode of crafted bytes. It is a conservative documented default; benchmark-backed
    /// calibration is tracked in #802. The production config seam that would let an operator lower it per tier
    /// is tracked in #803 — it is currently settable only from tests.</summary>
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    /// <summary>The upper bound accepted for a configured decode budget (24&#160;hours). A budget beyond this
    /// is a misconfiguration (it disables the DoS control), rejected fail-fast at construction rather than
    /// silently letting a non-terminating decode run effectively forever.</summary>
    internal static readonly TimeSpan MaxBudget = TimeSpan.FromHours(24);

    /// <summary>The default hard cap on <b>detached (stranded, running-past-deadline)</b> untrusted decodes on
    /// the <b>data-file</b> door. Sized generously (never <c>ProcessorCount/4</c>) so it bounds strand
    /// accumulation without ever throttling healthy concurrent scan decodes (I9): healthy decodes release
    /// their slot on completion, so in steady state the cap is occupied only by strands. A conservative
    /// default; calibration is tracked in #802.</summary>
    internal static int DefaultMaxDataFileDetachedDecodes => Math.Max(128, Environment.ProcessorCount * 16);

    /// <summary>The default hard cap on detached decodes on the <b>checkpoint</b> door — independent of the
    /// data-file cap (I5) so a poisoned data file cannot exhaust checkpoint-decode capacity. A conservative
    /// default; calibration is tracked in #802.</summary>
    internal static int DefaultMaxCheckpointDetachedDecodes => Math.Max(64, Environment.ProcessorCount * 8);

    // The two process-wide, per-door decoders. Independent caps confine a flood on one door away from the
    // other (I5). Tests exercise the admission/scheduling semantics on ISOLATED BoundedDecoder instances with
    // tiny caps, so the production defaults here are exercised as-is by the door integration tests (no
    // test-only widening masks the production behavior).
    private static readonly BoundedDecoder DataFileDecoder = new(DefaultMaxDataFileDetachedDecodes);
    private static readonly BoundedDecoder CheckpointDecoder = new(DefaultMaxCheckpointDetachedDecodes);

    /// <summary>The total count of detached (running-past-deadline) strands across both doors — exposed as the
    /// <c>deltasharp.storage.decode.detached</c> observability gauge and for tests that assert strands drain.</summary>
    internal static int DetachedDecodeCount => DataFileDecoder.DetachedDecodeCount + CheckpointDecoder.DetachedDecodeCount;

    /// <summary>The detached-strand count on the data-file door (observability gauge dimension).</summary>
    internal static int DataFileDetachedDecodeCount => DataFileDecoder.DetachedDecodeCount;

    /// <summary>The detached-strand count on the checkpoint door (observability gauge dimension).</summary>
    internal static int CheckpointDetachedDecodeCount => CheckpointDecoder.DetachedDecodeCount;

    /// <inheritdoc cref="BoundedDecoder.RunAsync{T}"/>
    /// <param name="door">Which per-door decoder (and independent strand cap) to run on (I5).</param>
    internal static Task<T> RunAsync<T>(
        DecodeDoor door,
        Func<CancellationToken, Task<T>> work,
        TimeSpan budget,
        Func<TimeSpan, Exception> onTimeout,
        CancellationToken cancellationToken,
        Action<T>? onAbandonedResult = null,
        Action? onWorkSettled = null,
        TimeProvider? timeProvider = null) =>
        Decoder(door).RunAsync(work, budget, onTimeout, cancellationToken, onAbandonedResult, onWorkSettled, timeProvider);

    private static BoundedDecoder Decoder(DecodeDoor door) =>
        door == DecodeDoor.Checkpoint ? CheckpointDecoder : DataFileDecoder;
}

/// <summary>
/// One bounded-decode execution surface: a hard cap on concurrently-detached decodes, each decode run on its
/// own dedicated background <see cref="Thread"/>. Production uses one shared instance per door
/// (<see cref="BoundedDecode"/>); tests construct isolated instances with tiny caps to exercise the
/// admission/scheduling contract deterministically. See <see cref="BoundedDecode"/> for the full rationale.
/// </summary>
internal sealed class BoundedDecoder
{
    private readonly int _maxDetachedDecodes;

    // Atomically-reserved slots: decodes currently running (healthy, transient) PLUS detached strands. Reserved
    // BEFORE any work starts (I2, no TOCTOU) and released when the work thread's task actually settles
    // (in-budget OR late). A genuine non-terminating strand never settles, so it holds its slot forever — the
    // bounded residual.
    private int _reserved;

    // Detached strands only (abandoned past their deadline, still running or never-terminating). Incremented
    // when a decode is abandoned; decremented when (if) that abandoned work finally settles. Exposed as the
    // observability gauge.
    private int _detached;

    internal BoundedDecoder(int maxDetachedDecodes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDetachedDecodes, 1);
        _maxDetachedDecodes = maxDetachedDecodes;
    }

    /// <summary>The admission cap on concurrently-reserved (running + detached) decodes for this decoder.</summary>
    internal int MaxDetachedDecodes => _maxDetachedDecodes;

    /// <summary>The current count of detached (running-past-deadline) strands — exposed for the observability
    /// gauge and for tests that assert the admission cap and that strands drain.</summary>
    internal int DetachedDecodeCount => Volatile.Read(ref _detached);

    /// <summary>The current count of reserved slots (running + detached) — exposed for tests that assert the
    /// atomic reservation cap under a concurrent burst.</summary>
    internal int ReservedDecodeCount => Volatile.Read(ref _reserved);

    /// <summary>
    /// Runs <paramref name="work"/> under a wall-clock <paramref name="budget"/> on its own dedicated
    /// background thread, after atomically reserving a strand slot. Returns the work's result when it finishes
    /// first (surfacing the work's own outcome — a value, a typed fail-closed exception, or cancellation —
    /// <b>unwrapped</b>). If the budget expires first, throws the exception produced by
    /// <paramref name="onTimeout"/> (a FIXED, sanitized fail-closed message) and leaves the work running
    /// detached (bounded). Caller cancellation is distinguished from a genuine deadline: a cancelled
    /// <paramref name="cancellationToken"/> surfaces <see cref="OperationCanceledException"/>, never the
    /// timeout exception.
    /// </summary>
    /// <typeparam name="T">The decode result type.</typeparam>
    /// <param name="work">The decode to bound. It receives a linked token that also trips on caller
    /// cancellation and on deadline expiry (a courtesy — the underlying decoder may ignore it).</param>
    /// <param name="budget">The wall-clock deadline, measured from EXECUTION start (I3); must be positive.</param>
    /// <param name="onTimeout">Produces the typed fail-closed exception to throw on deadline expiry. The
    /// message MUST be fixed/sanitized (no untrusted byte content).</param>
    /// <param name="cancellationToken">The caller's real cancellation, honored via the linked token.</param>
    /// <param name="onAbandonedResult">An optional disposer invoked if the work completes SUCCESSFULLY after
    /// the deadline (a late win): it disposes the abandoned result so a post-deadline success is never leaked.
    /// Never invoked on the in-budget success path (the caller owns the result there).</param>
    /// <param name="onWorkSettled">An optional callback invoked EXACTLY ONCE when the work thread's task
    /// settles (in-budget completion, a late completion, or a fault). Used by the data-file door to release a
    /// caller-shared <see cref="Parquet.ParquetReader"/> lease only once the (possibly stranded) decode has
    /// stopped touching it (I6). For a never-terminating strand it is never invoked, so the reader stays alive
    /// (bounded residual) rather than being disposed out from under the strand.</param>
    /// <param name="timeProvider">The clock the deadline is measured against (default
    /// <see cref="TimeProvider.System"/>); injected so deadline tests can drive it deterministically.</param>
    /// <exception cref="DecodeCapacityExhaustedException">This decoder is at capacity
    /// (<see cref="MaxDetachedDecodes"/>) — the call is rejected fail-fast WITHOUT starting.</exception>
    internal async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> work,
        TimeSpan budget,
        Func<TimeSpan, Exception> onTimeout,
        CancellationToken cancellationToken,
        Action<T>? onAbandonedResult = null,
        Action? onWorkSettled = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(onTimeout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget.Ticks, nameof(budget));

        cancellationToken.ThrowIfCancellationRequested();

        // I2 — ATOMIC hard cap, reserved BEFORE any work starts. A single Interlocked.Increment reserves the
        // slot; if it lands OVER the cap we immediately back it out and reject fail-fast. There is no
        // check-then-act window: concurrent callers each increment, and only those whose increment lands within
        // the cap proceed, so a burst can never admit more than _maxDetachedDecodes decodes. Over-cap surfaces
        // a DISTINCT retryable DecodeCapacityExhaustedException (never a decode-timeout, never negatively
        // cached) — the decode never starts, so it never adds to the strand residual.
        int reserved = Interlocked.Increment(ref _reserved);
        if (reserved > _maxDetachedDecodes)
        {
            Interlocked.Decrement(ref _reserved);
            throw new DecodeCapacityExhaustedException(
                "The bounded-decode worker is at capacity: too many untrusted decodes are already running "
                + "past their deadline. The decode was rejected without starting.");
        }

        timeProvider ??= TimeProvider.System;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workTcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Release the reservation EXACTLY when the work thread's task settles (in-budget OR late). Registered on
        // the work task so it fires on completion/fault regardless of who is awaiting; a true strand never
        // settles, so the slot stays reserved (bounded residual). ExecuteSynchronously keeps it allocation-lean
        // and off any custom scheduler.
        _ = workTcs.Task.ContinueWith(
            static (_, state) => Interlocked.Decrement(ref ((BoundedDecoder)state!)._reserved),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        if (onWorkSettled is not null)
        {
            _ = workTcs.Task.ContinueWith(
                static (_, state) => ((Action)state!)(),
                onWorkSettled,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        var thread = new Thread(() =>
        {
            // I3 — signal EXECUTION start as the FIRST statement so the deadline clock starts here (below),
            // never at admission/thread-creation. Any thread-start latency is separate and never charged to the
            // decode budget.
            started.TrySetResult();
            try
            {
                T value = work(linked.Token).GetAwaiter().GetResult();
                workTcs.TrySetResult(value);
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

        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            // Thread creation failed (e.g. resource exhaustion) BEFORE work began. Fault the work task so the
            // reservation-release continuation fires (no leaked slot), dispose the linked source, and surface
            // the failure to the caller.
            workTcs.TrySetException(ex);
            linked.Dispose();
            throw;
        }

        // Wait for the work to ACTUALLY START executing before arming the deadline (I3). This is a thread hop,
        // not an admission/queue wait, so it is negligible and — crucially — never counted against the budget.
        await started.Task.ConfigureAwait(false);

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
            AbandonInBackground(workTcs.Task, onAbandonedResult, linked);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Genuine deadline expiry. Cancel the linked token (a courtesy, in case the detached decode observes
        // it), register the abandoned work so its eventual outcome is observed/disposed and the strand count is
        // tracked, and fail closed with the caller-supplied typed exception (fixed, sanitized message).
        await linked.CancelAsync().ConfigureAwait(false);
        AbandonInBackground(workTcs.Task, onAbandonedResult, linked);
        throw onTimeout(budget);
    }

    // Register an abandoned (detached, running-past-deadline) decode: count it as a strand for as long as it
    // runs, observe its eventual fault so it is never re-raised on the finalizer thread as an unobserved task
    // exception, and dispose a late-completing SUCCESSFUL result so a reader/stream that wins after the deadline
    // is not leaked. The strand counter is decremented (and the linked source disposed) when the work finally
    // terminates; a never-terminating strand holds exactly one strand slot for its whole lifetime.
    private void AbandonInBackground<T>(Task<T> task, Action<T>? onAbandonedResult, CancellationTokenSource linked)
    {
        Interlocked.Increment(ref _detached);
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
                            // path; it must never surface (there is no caller to observe it) and never break the
                            // strand accounting below.
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _detached);
                    linked.Dispose();
                }
            },
            onAbandonedResult,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
