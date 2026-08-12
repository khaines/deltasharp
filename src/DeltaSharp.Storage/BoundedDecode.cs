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
/// no token can interrupt it. This policy runs the decode on a <b>dedicated, hard-capped</b> scheduler
/// (<see cref="LimitedConcurrencyLevelTaskScheduler"/>, sized to a fraction of the pod CPU) and races it
/// against <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>; when the delay wins, the
/// linked <see cref="CancellationTokenSource"/> is cancelled and the caller-supplied fail-closed exception is
/// thrown — releasing the caller deterministically.</para>
/// <para><b>The background thread cannot be aborted.</b> .NET provides no safe way to abort a running
/// thread, so on a genuine non-termination the underlying decode keeps running <b>detached</b> until it
/// completes or faults on its own (its eventual fault is observed here so it is never raised as an unobserved
/// task exception; a late-completing <b>successful</b> result is disposed via <c>onAbandonedResult</c> so a
/// reader/stream that wins after the deadline is never leaked). The DoS control is that the <b>caller</b> is
/// freed AND the residual is bounded, not that the CPU is reclaimed instantly:</para>
/// <list type="bullet">
///   <item><b>Pinned threads are capped.</b> At most <c>maxConcurrentDecodes</c> untrusted decodes execute at
///   once on the dedicated scheduler, so a stranded decode can never starve the shared <see cref="ThreadPool"/>
///   (it never runs on the pool's default scheduler) regardless of how many malicious reads occur; further
///   strands queue on the scheduler without consuming a thread.</item>
///   <item><b>Concurrency is admission-capped.</b> At most <c>maxDetachedDecodes</c> decodes may be
///   <b>detached</b> (running past their deadline) at once; a call that would exceed the cap is rejected
///   fail-fast with <see cref="DecodeCapacityExhaustedException"/> <b>without starting</b>, so the strand
///   count can never grow without bound.</item>
/// </list>
/// <para><b>Bounded residual.</b> The worst-case retained cost is
/// <c>maxDetachedDecodes × (max bytes one decode pins)</c>: at most <c>maxDetachedDecodes</c> stranded decodes,
/// each pinning at most one part/row-group worth of buffered-or-decoded bytes — ≤
/// <see cref="DeltaSharp.Storage.Delta.DeltaCheckpointReader.MaxCheckpointPartBytes"/> (512&#160;MiB) for the
/// checkpoint door (which decodes an <b>isolated</b> in-memory copy, never a caller-owned stream), or the
/// data-file row group under decode for the data-file door — and at most <c>maxConcurrentDecodes</c> of them
/// consuming a thread at once. The checkpoint layer additionally keeps a bounded <b>negative cache</b> of
/// timed-out checkpoint identities so a known-bad checkpoint is not re-decoded on every snapshot load, which
/// is what stops the strands from being self-renewing.</para>
/// <para><b>Accepted degradation.</b> Under a sustained flood of <b>distinct</b> crafted inputs the dedicated
/// tier can fill with strands; new decodes then fail fast (<see cref="DecodeCapacityExhaustedException"/>) —
/// decode throughput on this tier degrades in a <b>bounded, contained</b> way rather than melting the shared
/// pool. This is the deliberate residual (design §5.4 C-DECODE).</para>
/// <para><b>NativeAOT-safe:</b> the scheduler, <see cref="TaskFactory"/>,
/// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>, and a linked
/// <see cref="CancellationTokenSource"/> use no dynamic codegen or reflection.</para>
/// </remarks>
internal static class BoundedDecode
{
    /// <summary>The conservative default wall-clock budget for a single decode (open or row-group). A real
    /// decode of a legitimate part completes in milliseconds; this ceiling only ever trips a genuinely
    /// non-terminating decode of crafted bytes. Configurable per call (the data-file door threads it via
    /// <see cref="DeltaSharp.Storage.Parquet.ParquetDecodeLimits.DecodeTimeBudget"/>; the checkpoint door via
    /// its <c>ReadAsync</c> <c>decodeBudget</c> parameter) so an operator can lower it on a latency-sensitive
    /// tier or a test can shrink it for fast, deterministic coverage.</summary>
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    /// <summary>The upper bound accepted for a configured decode budget (24&#160;hours). A budget beyond this
    /// is a misconfiguration (it disables the DoS control), rejected fail-fast at construction rather than
    /// silently letting a non-terminating decode run effectively forever.</summary>
    internal static readonly TimeSpan MaxBudget = TimeSpan.FromHours(24);

    /// <summary>The default hard cap on <b>concurrently executing</b> untrusted decodes — a fraction of the pod
    /// CPU (a quarter of the available cores, at least one). A stranded (non-terminating) decode can never
    /// occupy more than this many worker threads at once, so the shared <see cref="ThreadPool"/> stays
    /// healthy.</summary>
    internal static int DefaultMaxConcurrentDecodes => Math.Max(1, Environment.ProcessorCount / 4);

    // The process-wide decoder the production doors use. Sized to a fraction of the pod CPU. Tests replace it
    // (ConfigureSharedForTests) with a larger-capacity instance so the accumulated strands the fail-closed
    // regression tests deliberately create cannot starve/exhaust the shared tier across the suite; the
    // admission/scheduling semantics themselves are tested on isolated BoundedDecoder instances.
    private static volatile BoundedDecoder _shared =
        new(DefaultMaxConcurrentDecodes, 2 * DefaultMaxConcurrentDecodes);

    /// <summary>The current count of detached (running-past-deadline) decodes on the shared decoder — exposed
    /// for tests that assert strands drain.</summary>
    internal static int DetachedDecodeCount => _shared.DetachedDecodeCount;

    /// <summary>Replaces the shared decoder with one of the given capacity. TEST-ONLY seam: the fail-closed
    /// regression tests intentionally create non-terminating (never-draining) strands, so the shared tier is
    /// widened for the test assembly to keep those strands from starving unrelated decodes.</summary>
    internal static void ConfigureSharedForTests(int maxConcurrentDecodes, int maxDetachedDecodes) =>
        _shared = new BoundedDecoder(maxConcurrentDecodes, maxDetachedDecodes);

    /// <inheritdoc cref="BoundedDecoder.RunAsync{T}"/>
    internal static Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> work,
        TimeSpan budget,
        Func<TimeSpan, Exception> onTimeout,
        CancellationToken cancellationToken,
        Action<T>? onAbandonedResult = null,
        TimeProvider? timeProvider = null) =>
        _shared.RunAsync(work, budget, onTimeout, cancellationToken, onAbandonedResult, timeProvider);
}

/// <summary>
/// One bounded-decode execution surface: a dedicated hard-capped scheduler plus an admission cap on
/// concurrently-detached decodes. Production uses a single shared instance (<see cref="BoundedDecode"/>); tests
/// construct isolated instances with tiny caps to exercise the admission/scheduling contract deterministically
/// without touching the shared tier. See <see cref="BoundedDecode"/> for the full policy rationale.
/// </summary>
internal sealed class BoundedDecoder
{
    private readonly LimitedConcurrencyLevelTaskScheduler _scheduler;
    private readonly TaskFactory _factory;
    private readonly int _maxDetachedDecodes;

    // The number of currently-detached (abandoned-past-deadline, still-running) decodes. The admission gate
    // reads it; abandonment increments it; the abandoned work's terminal continuation decrements it.
    private int _detachedDecodes;

    internal BoundedDecoder(int maxConcurrentDecodes, int maxDetachedDecodes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentDecodes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDetachedDecodes, 1);
        _maxDetachedDecodes = maxDetachedDecodes;
        _scheduler = new LimitedConcurrencyLevelTaskScheduler(maxConcurrentDecodes);
        _factory = new TaskFactory(
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskContinuationOptions.DenyChildAttach,
            _scheduler);
    }

    /// <summary>The admission cap on concurrently-detached decodes for this decoder.</summary>
    internal int MaxDetachedDecodes => _maxDetachedDecodes;

    /// <summary>The current count of detached (running-past-deadline) decodes — exposed for tests that assert
    /// the admission cap and that strands drain.</summary>
    internal int DetachedDecodeCount => Volatile.Read(ref _detachedDecodes);

    /// <summary>
    /// Runs <paramref name="work"/> under a wall-clock <paramref name="budget"/> on this decoder's dedicated,
    /// hard-capped scheduler. Returns the work's result when it finishes first (surfacing the work's own
    /// outcome — a value, a typed fail-closed exception, or cancellation — <b>unwrapped</b>). If the budget
    /// expires first, throws the exception produced by <paramref name="onTimeout"/> (a FIXED, sanitized
    /// fail-closed message — no untrusted byte content) and leaves the work running detached (bounded per
    /// <see cref="BoundedDecode"/>). Caller cancellation is distinguished from a genuine deadline: a cancelled
    /// <paramref name="cancellationToken"/> surfaces <see cref="OperationCanceledException"/>, never the
    /// timeout exception.
    /// </summary>
    /// <typeparam name="T">The decode result type.</typeparam>
    /// <param name="work">The decode to bound. It receives a linked token that also trips on caller
    /// cancellation and on deadline expiry (a courtesy — the underlying decoder may ignore it).</param>
    /// <param name="budget">The wall-clock deadline; must be positive.</param>
    /// <param name="onTimeout">Produces the typed fail-closed exception to throw on deadline expiry, so each
    /// door surfaces its own contract (both doors a <see cref="DeltaStorageException"/> with
    /// <see cref="StorageErrorKind.DecodeBudgetExceeded"/>). The message MUST be fixed/sanitized.</param>
    /// <param name="cancellationToken">The caller's real cancellation, honored via the linked token.</param>
    /// <param name="onAbandonedResult">An optional disposer invoked if the work completes SUCCESSFULLY after
    /// the deadline (a late win): it disposes the abandoned result (e.g. a <see cref="Parquet.ParquetReader"/>
    /// that owns its input stream) so a post-deadline success is never leaked. Never invoked on the in-budget
    /// success path (the caller owns the result there).</param>
    /// <param name="timeProvider">The clock the deadline is measured against (default
    /// <see cref="TimeProvider.System"/>); injected so deadline tests can drive it deterministically.</param>
    /// <exception cref="DecodeCapacityExhaustedException">Too many decodes are already detached
    /// (<see cref="MaxDetachedDecodes"/>) — the call is rejected fail-fast without starting.</exception>
    internal async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> work,
        TimeSpan budget,
        Func<TimeSpan, Exception> onTimeout,
        CancellationToken cancellationToken,
        Action<T>? onAbandonedResult = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(onTimeout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget.Ticks, nameof(budget));

        cancellationToken.ThrowIfCancellationRequested();

        // Admission control (fail-fast, BEFORE starting any work): if this decoder is already saturated by
        // other stranded (non-terminating) decodes, reject this one so the detached-decode residual can never
        // exceed MaxDetachedDecodes. This never adds to the strand count.
        if (Volatile.Read(ref _detachedDecodes) >= _maxDetachedDecodes)
        {
            throw new DecodeCapacityExhaustedException(
                "The bounded-decode worker is at capacity: too many untrusted decodes are already running "
                + "past their deadline. The decode was rejected without starting.");
        }

        timeProvider ??= TimeProvider.System;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Run the (partly synchronous, CPU-bound) decode on the dedicated, hard-capped scheduler so the caller
        // is released when the deadline wins even though the decode ignores the token, and a stranded decode
        // can never starve the shared ThreadPool. Do NOT pass the linked token to the scheduling
        // (CancellationToken.None): we always want the work to start, and we cancel it cooperatively via the
        // token we hand the delegate instead.
        Task<T> workTask = _factory.StartNew(
            () => work(linked.Token), CancellationToken.None).Unwrap();
        Task delayTask = Task.Delay(budget, timeProvider, linked.Token);

        await Task.WhenAny(workTask, delayTask).ConfigureAwait(false);

        if (workTask.IsCompleted)
        {
            // Work won the race (success OR a typed/library fault OR its own cancellation). Cancel the delay
            // timer and surface the work's own outcome unwrapped — a typed fail-closed exception, an
            // OperationCanceledException, or the decoded result all propagate as-is (never remapped here).
            // CancelAsync (not Cancel) runs the delay's cancellation callback off this path so an inline
            // callback fault can never mask the work's typed outcome.
            await linked.CancelAsync().ConfigureAwait(false);
            return await workTask.ConfigureAwait(false);
        }

        // The delay won: either the caller cancelled, or the deadline genuinely expired. Distinguish them so
        // caller cancellation stays control flow (OperationCanceledException) and is NEVER masked as a timeout.
        if (cancellationToken.IsCancellationRequested)
        {
            AbandonInBackground(workTask, onAbandonedResult);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Genuine deadline expiry. Cancel the linked token (a courtesy, in case the detached decode observes
        // it) via CancelAsync so an inline callback fault cannot mask the typed timeout, register the abandoned
        // work so its eventual outcome is observed/disposed and the strand count is tracked, and fail closed
        // with the caller-supplied typed exception (fixed, sanitized message).
        await linked.CancelAsync().ConfigureAwait(false);
        AbandonInBackground(workTask, onAbandonedResult);
        throw onTimeout(budget);
    }

    // Register an abandoned (detached, running-past-deadline) decode: count it against the admission cap for as
    // long as it runs, observe its eventual fault so it is never re-raised on the finalizer thread as an
    // unobserved task exception, and dispose a late-completing SUCCESSFUL result so a reader/stream that wins
    // after the deadline is not leaked. The counter is decremented when the work finally terminates, so a
    // stranded decode occupies exactly one admission slot for its whole (possibly unbounded) lifetime.
    private void AbandonInBackground<T>(Task<T> task, Action<T>? onAbandonedResult)
    {
        Interlocked.Increment(ref _detachedDecodes);
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
                            // path; it must never surface (there is no caller to observe it) and never break
                            // the strand accounting below.
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _detachedDecodes);
                }
            },
            onAbandonedResult,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
