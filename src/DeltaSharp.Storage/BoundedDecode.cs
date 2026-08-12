namespace DeltaSharp.Storage;

/// <summary>
/// A shared <b>bounded-time (wall-clock deadline) decode policy</b> for handing untrusted bytes to a
/// decoder that ignores the <see cref="CancellationToken"/> (design §5.4 C-DECODE, "fail deterministically …
/// fail closed … never hangs"). It converts a non-terminating decode into a deterministic, typed
/// fail-closed exception so a crafted <c>_delta_log</c> / data-file cannot stall a table read indefinitely
/// (#647, #699, #716).
/// </summary>
/// <remarks>
/// <para>Parquet.Net (6.0.3) can be driven by a single corrupted byte (a flipped terminal footer
/// <c>STOP</c>, a corrupt data-page header) into effectively unbounded, <b>synchronous</b> CPU work that
/// observes <b>no</b> cancellation mid-decode. A hang is not an exception, so no <c>try</c>/<c>catch</c> and
/// no token can interrupt it. This policy runs the decode on a threadpool thread via <see cref="Task.Run{T}(Func{Task{T}})"/>
/// and races it against <see cref="Task.Delay(TimeSpan, CancellationToken)"/>; when the delay wins, the
/// linked <see cref="CancellationTokenSource"/> is cancelled and the caller-supplied fail-closed exception is
/// thrown — releasing the caller deterministically.</para>
/// <para><b>The background thread cannot be aborted.</b> .NET provides no safe way to abort a running
/// thread, so on a genuine non-termination the underlying decode keeps running <b>detached</b> on the pool
/// until it completes or faults on its own (its eventual fault is observed here so it is never raised as an
/// unobserved task exception). That is accepted per the issue guidance ("a dedicated worker with a hard
/// deadline"): the DoS control is that the <b>caller</b> is freed, not that the CPU is reclaimed instantly —
/// a single detached decode is bounded work on one pool thread, and a caller that stops using the abandoned
/// result lets the stream/reader be torn down as usual.</para>
/// <para><b>NativeAOT-safe:</b> <see cref="Task.Run{T}(Func{Task{T}})"/>, <see cref="Task.Delay(TimeSpan, CancellationToken)"/>,
/// and a linked <see cref="CancellationTokenSource"/> use no dynamic codegen or reflection.</para>
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

    /// <summary>
    /// Runs <paramref name="work"/> under a wall-clock <paramref name="budget"/>. Returns the work's result
    /// when it finishes first (surfacing the work's own outcome — a value, a typed fail-closed exception, or
    /// cancellation — <b>unwrapped</b>). If the budget expires first, throws the exception produced by
    /// <paramref name="onTimeout"/> (a FIXED, sanitized fail-closed message — no untrusted byte content).
    /// Caller cancellation is distinguished from a genuine deadline: a cancelled
    /// <paramref name="cancellationToken"/> surfaces <see cref="OperationCanceledException"/>, never the
    /// timeout exception.
    /// </summary>
    /// <typeparam name="T">The decode result type.</typeparam>
    /// <param name="work">The decode to bound. It receives a linked token that also trips on caller
    /// cancellation and on deadline expiry (a courtesy — the underlying decoder may ignore it).</param>
    /// <param name="budget">The wall-clock deadline; must be positive.</param>
    /// <param name="onTimeout">Produces the typed fail-closed exception to throw on deadline expiry, so each
    /// door surfaces its own contract (the data-file door a <see cref="DeltaStorageException"/> with
    /// <see cref="StorageErrorKind.CorruptData"/>; the checkpoint door a
    /// <see cref="DeltaSharp.Storage.Delta.DeltaProtocolException"/>). The message MUST be fixed/sanitized.</param>
    /// <param name="cancellationToken">The caller's real cancellation, honored via the linked token.</param>
    internal static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> work,
        TimeSpan budget,
        Func<TimeSpan, Exception> onTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(onTimeout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget.Ticks);

        cancellationToken.ThrowIfCancellationRequested();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Run the (partly synchronous, CPU-bound) decode on a threadpool thread so the caller is released when
        // the deadline wins even though the decode ignores the token. Do NOT pass the linked token to Task.Run's
        // SCHEDULING (CancellationToken.None): we always want the work to start, and we cancel it cooperatively
        // via the token we hand the delegate instead.
        Task<T> workTask = Task.Run(() => work(linked.Token), CancellationToken.None);
        Task delayTask = Task.Delay(budget, linked.Token);

        await Task.WhenAny(workTask, delayTask).ConfigureAwait(false);

        if (workTask.IsCompleted)
        {
            // Work won the race (success OR a typed/library fault OR its own cancellation). Cancel the delay
            // timer and surface the work's own outcome unwrapped — a typed fail-closed exception, an
            // OperationCanceledException, or the decoded result all propagate as-is (never remapped here).
            linked.Cancel();
            return await workTask.ConfigureAwait(false);
        }

        // The delay won: either the caller cancelled, or the deadline genuinely expired. Distinguish them so
        // caller cancellation stays control flow (OperationCanceledException) and is NEVER masked as a timeout.
        if (cancellationToken.IsCancellationRequested)
        {
            ObserveInBackground(workTask);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Genuine deadline expiry. Cancel the linked token (a courtesy, in case the detached decode observes
        // it), observe the abandoned work's eventual fault so it is not surfaced as an unobserved task
        // exception, and fail closed with the caller-supplied typed exception (fixed, sanitized message).
        linked.Cancel();
        ObserveInBackground(workTask);
        throw onTimeout(budget);
    }

    // Attach a fault-only continuation so an abandoned (detached, non-terminating-then-eventually-faulting)
    // decode's exception is observed and never re-raised on the finalizer thread as an unobserved task
    // exception. ExecuteSynchronously keeps it allocation-cheap; it runs only if the task faults.
    private static void ObserveInBackground(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
