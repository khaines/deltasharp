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
    /// that residual is bounded by the charge-at-detach stranded-residual budget (there is no shared custom
    /// scheduler for it to starve — the pool injects threads).</summary>
    Pool,

    /// <summary>Run on its own dedicated background <see cref="Thread"/> (<see cref="Thread.IsBackground"/> =
    /// <see langword="true"/>). Used by the <b>checkpoint</b> door only: there the decode is <b>predominantly
    /// synchronous</b> over a pre-buffered <c>byte[]</c>, so the dedicated thread contains the CPU-bound work off
    /// the pool (it does not hand it back at an <c>await</c>). A stranded thread holds its own thread + its
    /// isolated byte copy until process restart; that residual is bounded by the charge-at-detach
    /// stranded-residual budget.</summary>
    DedicatedThread,
}

/// <summary>
/// A shared <b>bounded-time (wall-clock deadline) decode policy</b> for handing untrusted bytes to a
/// decoder that ignores the <see cref="CancellationToken"/> (design §5.4 C-DECODE — the bounded wall-clock
/// decode ceiling). It converts a non-terminating decode into a deterministic, typed fail-closed exception so a
/// crafted <c>_delta_log</c> / data-file cannot stall a table read indefinitely (#647, #699, #716), and it
/// bounds the <b>byte residual</b> of the abandoned (stranded) work so a crafted input cannot exhaust process
/// memory before the ceiling engages — <b>without</b> ever charging or throttling a healthy in-flight decode.
/// </summary>
/// <remarks>
/// <para>Parquet.Net (6.0.3) can be driven by a single corrupted byte (a flipped terminal footer
/// <c>STOP</c>, a corrupt data-page header) into effectively unbounded, <b>synchronous</b> CPU work that
/// observes <b>no</b> cancellation mid-decode. A hang is not an exception, so no <c>try</c>/<c>catch</c> and
/// no token can interrupt it, and <b>.NET cannot abort a running thread</b> — a non-terminating decode
/// therefore cannot be reclaimed. The only things this policy can do are (a) bound the <b>retained bytes</b>
/// of <b>stranded</b> work (a decode that ran past its deadline while the caller was released), (b) prevent it
/// from self-renewing (the checkpoint negative cache), and (c) ensure a strand never consumes the capacity a
/// <b>healthy</b> decode needs.</para>
/// <para><b>The charge-at-DETACH residual model (the Round-6 redesign).</b> The Round-4 model was fundamentally
/// mis-designed: it charged a <b>healthy in-flight</b> decode against the <b>same</b> budget as a permanent
/// strand — and charged a <b>fictional fixed</b> representative (64&#160;MiB) instead of the real retained
/// footprint — so a healthy multi-core executor got spurious <see cref="DecodeCapacityExhaustedException"/>,
/// a small pod derived a checkpoint cap of <b>1</b> (one crafted checkpoint permanently denied ALL tables),
/// and the residual was under-bounded so an OOM was still reachable. The model now is:</para>
/// <list type="bullet">
///   <item><b>Healthy in-flight is NEVER charged and NEVER throttled (the decisive fix).</b> A decode that
///   completes within its budget consumes <b>zero</b> residual and is <b>never</b> rejected for byte/count
///   reasons. Healthy scan concurrency is unbounded by this control — a 16-core executor never sees a spurious
///   saturation.</item>
///   <item><b>The residual is reserved only at DETACH.</b> When a decode strands past its deadline (the caller
///   is released but the un-abortable decode keeps running) the door charges the decode's <b>actual retained
///   footprint</b> — the projected decoded footprint it was permitted, clamped to the enforced ceiling
///   (<paramref name="maxFootprintBytes"/>) — against the door's <b>residual budget</b>. A strand un-charges
///   only if it eventually terminates; a genuine non-terminating strand holds its charge forever (the bounded
///   residual).</item>
///   <item><b>Admission (fail-fast) is checked against the current STRANDED residual, not healthy in-flight.</b>
///   A new untrusted decode is admitted unless the door's stranded residual is already full
///   (<c>strandedBytes ≥ residualBudget</c> OR <c>strandedCount ≥ countCap</c>), in which case it is rejected
///   fail-fast with a distinct <see cref="DecodeCapacityExhaustedException"/> → the retryable
///   <see cref="StorageErrorKind.DecoderSaturated"/> (never a decode-timeout, never negatively cached). It is
///   admitted <b>without charging</b> anything. This bounds the stranded residual to
///   <c>residualBudget + (C × maxFootprint)</c>, where <c>C</c> is the number of untrusted decodes in flight
///   when the residual crossed the budget — <c>C = 1</c> on the serial checkpoint/data-file load path, so the
///   practical bound is <c>residualBudget + one_max_footprint</c>. That IS a real memory bound, while a healthy
///   read is only ever rejected once the residual is genuinely full of <i>permanent strands</i>.</item>
///   <item><b>Floored residual budget (small-pod behavior).</b> The residual budget is floored so at least one
///   maximal legitimate decode/part is always admissible against an empty residual and a single strand can
///   never instantly saturate the door (<c>max(processMem/8, k × maxFootprint)</c>, k≥2). On a small pod this
///   means the DoS residual can be a larger fraction of pod memory than on a large pod — the accepted
///   degradation, because you cannot simultaneously bound the residual below one footprint AND admit a legit
///   decode that needs one footprint. Construction rejects a residual budget that cannot admit one legit part.</item>
///   <item><b>Execution-start deadline (I3).</b> The work signals a start gate as its FIRST statement inside
///   the pool task / thread; the <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> budget is
///   armed only after that signal. Admission latency is never charged to the decode budget.</item>
///   <item><b>Per-operation deadline scope (I7).</b> The budget passed in bounds ONLY one decode operation
///   (one open, one row-group decode, or one buffered checkpoint part). The caller must NOT pass a deadline
///   that also spans streaming iteration, consumer time, or storage I/O — those are not the decode.</item>
///   <item><b>Per-door isolation (I5).</b> The data-file and checkpoint doors have <b>independent</b> decoders
///   with independent residual budgets, so a poisoned data file can never exhaust the capacity healthy
///   checkpoint decodes need (and vice-versa).</item>
/// </list>
/// <para><b>Execution surface.</b> The <b>data-file</b> door runs the decode on the shared
/// <see cref="System.Threading.ThreadPool"/> (<see cref="DecodeExecution.Pool"/>): a Round-2 dedicated thread
/// bought no isolation there because <c>DecodeGroupAsync</c> awaits async reads that resume on the pool, so the
/// dedicated thread sat blocked in <c>GetResult()</c> while the non-terminating loop ran on the pool anyway —
/// pure cost (measured 68–74× per decode). The <b>checkpoint</b> door keeps its dedicated thread
/// (<see cref="DecodeExecution.DedicatedThread"/>) because there the decode is <b>predominantly</b> synchronous
/// over a pre-buffered <c>byte[]</c>, so the thread contains the CPU-bound loop off the pool. In neither door is
/// there a shared custom scheduler a strand can starve: the pool injects threads, and each checkpoint strand has
/// its own thread.</para>
/// <para><b>Bounded residual / accepted degradation.</b> A late-completing SUCCESSFUL result is disposed via
/// <c>onAbandonedResult</c>; a strand over a caller-shared reader keeps that reader alive via an
/// <c>onWorkSettled</c> lease release (the data-file door) so it never touches a caller-disposed object, and
/// the checkpoint door hands its strand an isolated in-memory copy of the bytes. Under a sustained flood of
/// <b>distinct</b> crafted inputs a door's residual can fill with strands; further decodes on that door then
/// fail fast (<see cref="DecodeCapacityExhaustedException"/>) — a bounded, contained degradation, not an OOM
/// kill. The checkpoint layer additionally negatively caches a timed-out checkpoint identity so a known-bad
/// checkpoint is not re-decoded on every snapshot load (which is what stops strands self-renewing). A routine
/// caller cancellation of a HEALTHY decode is NOT counted as a strand (the detached gauge is not inflated by
/// it, and it is never charged).</para>
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
    /// benchmark-backed calibration (including the residual-budget dimension) is tracked in #802. The production
    /// config seam that would let an operator lower it per tier is tracked in #803 — it is currently settable
    /// only from tests.</summary>
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    /// <summary>The upper bound accepted for a configured decode budget (24&#160;hours). A budget beyond this
    /// is a misconfiguration (it disables the DoS control), rejected fail-fast at construction rather than
    /// silently letting a non-terminating decode run effectively forever.</summary>
    internal static readonly TimeSpan MaxBudget = TimeSpan.FromHours(24);

    /// <summary>The maximum retained footprint a single <b>data-file</b> strand can pin — the reader's enforced
    /// per-row-group eager-decode ceiling (4&#160;GiB, mirrors <c>ParquetFileReader.MaxRowGroupDecodedBytes</c>
    /// / <c>ParquetDecodeLimits.DefaultMaxRowGroupDecodedBytes</c>). Held locally (rather than referencing that
    /// constant) so the two doors' <c>static readonly</c> field init cannot depend on cross-type init order.
    /// It floors the residual budget and clamps the charge a data-file strand books at detach.</summary>
    internal const long DataFileMaxFootprintBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>The maximum retained footprint a single <b>checkpoint</b> strand can pin — the isolated
    /// buffered part copy it holds (512&#160;MiB, mirrors <c>DeltaCheckpointReader.MaxCheckpointPartBytes</c>).
    /// It floors the residual budget and clamps the charge a checkpoint strand books at detach.</summary>
    internal const long CheckpointMaxFootprintBytes = 512L * 1024 * 1024;

    /// <summary>The floor multiple <c>k</c> applied to the max footprint when flooring a door's residual budget
    /// (<c>k × maxFootprint</c>, k≥2): at least one maximal legitimate decode is always admissible against an
    /// empty residual, AND a single strand can never instantly saturate the door.</summary>
    internal const int ResidualFloorMultiple = 2;

    /// <summary>A generous ceiling on the STRAND count a door tolerates before fail-fast rejection (each strand
    /// also pins a thread/pool slot, so the count is bounded independently of the byte residual). The count cap
    /// applies to <b>strands only</b> — never to healthy in-flight decodes — so it does not throttle healthy
    /// scan concurrency. Calibration is tracked in #802.</summary>
    internal const int StrandCountCeiling = 256;

    /// <summary>The process/GC memory the doors size their budgets against —
    /// <see cref="GCMemoryInfo.TotalAvailableMemoryBytes"/> (container-cgroup-aware), with a conservative
    /// 4&#160;GiB fallback when the runtime reports it as unknown. Captured once at type init.</summary>
    internal static long ProcessMemoryBytes { get; } = DeriveProcessMemoryBytes();

    private static long DeriveProcessMemoryBytes()
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available > 0 ? available : 4L * 1024 * 1024 * 1024;
    }

    /// <summary>The residual budget + strand-count cap a door of a given max-footprint uses, given the process
    /// memory. PURE (no shared state) so it is table-testable across pod sizes: the residual budget is a
    /// fraction of process memory floored at <c>k × maxFootprint</c> (so one legit part is always admissible and
    /// a single strand can never instantly saturate the door); the strand-count cap is
    /// <c>residualBudget / maxFootprint</c> (so both gates saturate together for maximal strands) floored at
    /// <c>k</c> and capped at <see cref="StrandCountCeiling"/>.</summary>
    internal static DoorSizing DeriveDoorSizing(long processMemoryBytes, long maxFootprintBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFootprintBytes);
        long floor = SaturatingMul(ResidualFloorMultiple, maxFootprintBytes);
        long residualBudget = Math.Max(Math.Max(processMemoryBytes, 0L) / 8, floor);
        long derivedCount = residualBudget / maxFootprintBytes;
        int countCap = (int)Math.Clamp(derivedCount, ResidualFloorMultiple, StrandCountCeiling);
        return new DoorSizing(residualBudget, countCap, maxFootprintBytes);
    }

    private static long SaturatingMul(long a, long b)
    {
        if (a == 0 || b == 0)
        {
            return 0;
        }

        return a > long.MaxValue / b ? long.MaxValue : a * b;
    }

    // The two process-wide, per-door decoders. Independent residual budgets confine a flood on one door away
    // from the other (I5). Tests exercise the admission/residual semantics on ISOLATED BoundedDecoder instances
    // with tiny budgets (and can INJECT one into ParquetFileReader / DeltaLog via the constructor seams), so the
    // production defaults here are exercised as-is by the door integration tests (no test-only widening masks
    // the production behavior).
    internal static readonly BoundedDecoder DataFileDecoder =
        BoundedDecoder.FromSizing(DeriveDoorSizing(ProcessMemoryBytes, DataFileMaxFootprintBytes), DecodeExecution.Pool);

    internal static readonly BoundedDecoder CheckpointDecoder =
        BoundedDecoder.FromSizing(DeriveDoorSizing(ProcessMemoryBytes, CheckpointMaxFootprintBytes), DecodeExecution.DedicatedThread);

    /// <summary>The total count of detached (running-past-deadline) strands across both doors — exposed as the
    /// <c>deltasharp.storage.decode.detached</c> observability gauge and for tests that assert strands drain.</summary>
    internal static int DetachedDecodeCount => DataFileDecoder.DetachedDecodeCount + CheckpointDecoder.DetachedDecodeCount;

    /// <summary>The detached-strand count on the data-file door (observability gauge dimension).</summary>
    internal static int DataFileDetachedDecodeCount => DataFileDecoder.DetachedDecodeCount;

    /// <summary>The detached-strand count on the checkpoint door (observability gauge dimension).</summary>
    internal static int CheckpointDetachedDecodeCount => CheckpointDecoder.DetachedDecodeCount;
}

/// <summary>A door's derived sizing: its stranded-residual byte budget, its strand-count cap, and the max
/// single-strand footprint the budget was floored against — see <see cref="BoundedDecode.DeriveDoorSizing"/>.</summary>
internal readonly record struct DoorSizing(long ResidualBudgetBytes, int StrandCountCap, long MaxFootprintBytes);

/// <summary>
/// One bounded-decode execution surface: a charge-at-DETACH stranded-residual budget (healthy in-flight is
/// never charged, never throttled), each decode run on the shared <see cref="System.Threading.ThreadPool"/>
/// (data-file door) or its own dedicated background <see cref="Thread"/> (checkpoint door). Production uses one
/// shared instance per door (<see cref="BoundedDecode"/>); tests construct isolated instances with tiny budgets
/// to exercise the admission/residual contract deterministically. See <see cref="BoundedDecode"/> for the full
/// rationale.
/// </summary>
internal sealed class BoundedDecoder
{
    private readonly int _strandCountCap;
    private readonly long _residualBudgetBytes;
    private readonly long _maxFootprintBytes;
    private readonly DecodeExecution _execution;

    // The STRANDED residual (the load-bearing bound). Charged ONLY at DETACH (a genuine deadline expiry, when
    // the caller is released but the un-abortable decode keeps running) with the decode's actual retained
    // footprint clamped to _maxFootprintBytes; un-charged only if a strand eventually terminates. A healthy
    // in-flight decode is NEVER charged here, so it can never be throttled by this control. A genuine
    // non-terminating strand holds its charge forever — the bounded residual.
    private long _strandedBytes;

    // Detached strands only (abandoned past their deadline, still running or never-terminating) — the COUNT
    // companion to _strandedBytes. Incremented at DETACH (never by a routine caller cancellation); decremented
    // when (if) that abandoned work finally settles. Exposed as the observability gauge.
    private int _detached;

    internal BoundedDecoder(
        int strandCountCap,
        long residualBudgetBytes = long.MaxValue,
        long maxFootprintBytes = 1,
        DecodeExecution execution = DecodeExecution.Pool)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(strandCountCap, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(residualBudgetBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFootprintBytes, 1);

        // Reject a residual budget that cannot admit even one maximal legitimate part (design §5.4 floor): with
        // a budget below one footprint, a single strand's charge would dwarf the budget and the bound would be
        // dominated by one footprint rather than the budget — a misconfiguration, rejected at construction.
        if (residualBudgetBytes < maxFootprintBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(residualBudgetBytes),
                residualBudgetBytes,
                $"The stranded-residual budget ({residualBudgetBytes}) must admit at least one maximal decode "
                + $"footprint ({maxFootprintBytes}); a smaller budget cannot admit one legitimate part.");
        }

        _strandCountCap = strandCountCap;
        _residualBudgetBytes = residualBudgetBytes;
        _maxFootprintBytes = maxFootprintBytes;
        _execution = execution;
    }

    /// <summary>Builds a decoder from a derived <see cref="DoorSizing"/> (the production path).</summary>
    internal static BoundedDecoder FromSizing(DoorSizing sizing, DecodeExecution execution) =>
        new(sizing.StrandCountCap, sizing.ResidualBudgetBytes, sizing.MaxFootprintBytes, execution);

    /// <summary>The fail-fast cap on the COUNT of concurrent STRANDS (never healthy in-flight decodes).</summary>
    internal int StrandCountCap => _strandCountCap;

    /// <summary>The stranded-residual byte budget (the load-bearing memory bound), charged only at detach.</summary>
    internal long ResidualBudgetBytes => _residualBudgetBytes;

    /// <summary>The max single-strand footprint the charge is clamped to and the budget was floored against.</summary>
    internal long MaxFootprintBytes => _maxFootprintBytes;

    /// <summary>How a detached strand is hosted (Pool for the data-file door; DedicatedThread for the
    /// checkpoint door) — exposed for the per-door isolation test.</summary>
    internal DecodeExecution Execution => _execution;

    /// <summary>The current count of detached (running-past-deadline) strands — exposed for the observability
    /// gauge and for tests that assert the strand cap and that strands drain.</summary>
    internal int DetachedDecodeCount => Volatile.Read(ref _detached);

    /// <summary>The current stranded-residual bytes (charged at detach, un-charged when a strand terminates) —
    /// exposed for tests that assert the byte-aware residual bound.</summary>
    internal long StrandedDecodeBytes => Volatile.Read(ref _strandedBytes);

    /// <summary>
    /// Runs <paramref name="work"/> under a wall-clock <paramref name="budget"/> (measured from EXECUTION start,
    /// I3), after checking this door is not already saturated by STRANDS (not by healthy in-flight — those are
    /// never charged nor throttled). Returns the work's result when it finishes first (surfacing the work's own
    /// outcome — a value, a typed fail-closed exception, or cancellation — <b>unwrapped</b>). If the budget
    /// expires first, the decode DETACHES: it charges <paramref name="estimatedRetainedBytes"/> (clamped to the
    /// door's max footprint) against the stranded residual, throws the exception produced by
    /// <paramref name="onTimeout"/> (a FIXED, sanitized fail-closed message), and leaves the work running
    /// detached (bounded by the residual budget). Caller cancellation is distinguished from a genuine deadline:
    /// a cancelled <paramref name="cancellationToken"/> surfaces <see cref="OperationCanceledException"/>, never
    /// the timeout exception, is NOT counted as a strand, and is NOT charged.
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
    /// calling never leaks it — the lease-leak fix). On the HEALTHY in-budget completion path it is fired
    /// <b>synchronously</b> before the caller is returned to, so a caller observes the lease released (and thus
    /// its reader/stream deterministically disposed) before <c>RunAsync</c> returns (High #8). The data-file
    /// door uses it to release a caller-shared <see cref="Parquet.ParquetReader"/> lease only once the (possibly
    /// stranded) decode has stopped touching it (I6). For a never-terminating strand that was admitted it is
    /// never invoked, so the reader stays alive (bounded residual) rather than being disposed out from under the
    /// strand.</param>
    /// <param name="timeProvider">The clock the deadline is measured against (default
    /// <see cref="TimeProvider.System"/>); injected so deadline tests can drive it deterministically.</param>
    /// <param name="estimatedRetainedBytes">The decode's actual retained-bytes footprint, charged against the
    /// door's stranded residual ONLY if this decode detaches (clamped to the door's max footprint). A healthy
    /// completion charges nothing. Zero is used by unit tests that exercise the strand-COUNT cap in isolation.</param>
    /// <exception cref="DecodeCapacityExhaustedException">This door's stranded residual is already full (its
    /// strand-count cap OR its residual byte budget is exhausted by permanent strands) — the call is rejected
    /// fail-fast WITHOUT starting.</exception>
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

        // The charge a strand of this decode would book at detach: its real footprint, clamped to the door's
        // max footprint so a mis-estimate can never over-charge the residual (the bound stays provable).
        long strandCharge = Math.Clamp(estimatedRetainedBytes, 0L, _maxFootprintBytes);

        // onWorkSettled EXACTLY ONCE on every path. The data-file door releases its ParquetReader lease here, so
        // a routine caller-cancellation (whose first act below is ThrowIfCancellationRequested) or a capacity
        // rejection BEFORE the decode starts must still release it (the lease-leak fix).
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
            // Validate the budget AFTER the null guards but BEFORE admission so an arg-validation throw still
            // fires Settle (the arg-validation-before-Settle fix): a caller that took a lease before calling with
            // a bad budget never leaks it.
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget.Ticks, nameof(budget));

            // Pre-start caller cancellation: never start a decode for an already-cancelled caller.
            cancellationToken.ThrowIfCancellationRequested();

            // Admission (fail-fast) against the current STRANDED residual — NOT against healthy in-flight, which
            // is never charged nor counted. A new untrusted decode is admitted unless the door is already
            // saturated by PERMANENT strands (strandedBytes ≥ budget OR strandedCount ≥ cap); otherwise it is
            // admitted WITHOUT charging anything. Over-saturation surfaces a DISTINCT fail-closed
            // DecodeCapacityExhaustedException (never a decode-timeout, never negatively cached).
            AdmitOrReject();
        }
        catch
        {
            // Pre-start throw (arg-validation, cancellation, or capacity): fire onWorkSettled so the caller's
            // lease is released exactly once even though no work task will ever settle.
            Settle();
            throw;
        }

        timeProvider ??= TimeProvider.System;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workTcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            StartExecution(work, linked, started, workTcs);
        }
        catch (Exception ex)
        {
            // Execution start failed (e.g. thread-resource exhaustion) BEFORE work began. Nothing was charged
            // (no admission reservation), so just fire Settle (release the lease), dispose the linked source,
            // and surface the failure to the caller. The unobserved workTcs is never awaited — fault it so a
            // late set cannot surface as an unobserved task exception.
            workTcs.TrySetException(ex);
            _ = workTcs.Task.Exception; // observe
            Settle();
            linked.Dispose();
            throw;
        }

        // Wait for the work to ACTUALLY START executing before arming the deadline (I3). This is a thread hop,
        // not an admission/queue wait, so it is negligible and — crucially — never counted against the budget.
        // Cancellable: if the caller cancels during the start hop, do not block on it — the linked token is
        // already cancelled, so the work (once it runs) observes cancellation and settles; abandon it without
        // inflating the strand gauge (not a strand, not charged) and surface the OCE.
        try
        {
            await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AbandonInBackground(workTcs.Task, onAbandonedResult, linked, Settle, strandCharge, countAsStrand: false);
            throw;
        }

        Task delayTask = Task.Delay(budget, timeProvider, linked.Token);

        await Task.WhenAny(workTcs.Task, delayTask).ConfigureAwait(false);

        if (workTcs.Task.IsCompleted)
        {
            // Work won the race (success OR a typed/library fault OR its own cancellation) — a HEALTHY in-budget
            // outcome. Nothing was ever charged, so there is no residual to release. Cancel the delay timer,
            // then fire Settle SYNCHRONOUSLY (High #8): the caller's lease is released — and thus its
            // reader/stream deterministically disposed — before RunAsync returns, closing the race where an
            // async settle continuation could run after the caller had already returned. CancelAsync (not
            // Cancel) runs the delay's cancellation callback off this path so an inline callback fault cannot
            // mask the work's outcome. Then surface the work's own outcome UNWRAPPED.
            await linked.CancelAsync().ConfigureAwait(false);
            Settle();
            linked.Dispose();
            return await workTcs.Task.ConfigureAwait(false);
        }

        // The delay won: either the caller cancelled, or the deadline genuinely expired. Distinguish them so
        // caller cancellation stays control flow (OperationCanceledException) and is NEVER masked as a timeout.
        if (cancellationToken.IsCancellationRequested)
        {
            // Routine caller cancellation of a (likely healthy) decode: do NOT count it as a strand and do NOT
            // charge it (the detached gauge and the residual are not inflated by cancellation). The linked token
            // is already cancelled, so a cooperative decode terminates promptly. Observe/dispose the eventual
            // outcome and fire Settle when it settles (the lease is held until the — possibly still-running —
            // work stops touching the reader).
            AbandonInBackground(workTcs.Task, onAbandonedResult, linked, Settle, strandCharge, countAsStrand: false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Genuine deadline expiry — the decode DETACHES. Cancel the linked token (a courtesy, in case the
        // detached decode observes it), charge the strand's footprint against the stranded residual + increment
        // the strand gauge (un-charged only if the strand eventually terminates), observe/dispose its eventual
        // outcome, and fail closed with the caller-supplied typed exception.
        await linked.CancelAsync().ConfigureAwait(false);
        AbandonInBackground(workTcs.Task, onAbandonedResult, linked, Settle, strandCharge, countAsStrand: true);
        throw onTimeout(budget);
    }

    // Admission gate (fail-fast) against the current STRANDED residual only. A healthy in-flight decode is never
    // charged and never counted, so it is never rejected here — this rejects only when the door is genuinely
    // full of PERMANENT strands. Throws a fail-closed DecodeCapacityExhaustedException with a TRUTHFUL message
    // (strandedBytes/budget + strandedCount/cap) so a byte-saturated vs count-saturated condition is
    // distinguishable in the surfaced text.
    private void AdmitOrReject()
    {
        long strandedBytes = Volatile.Read(ref _strandedBytes);
        int strandedCount = Volatile.Read(ref _detached);
        if (strandedBytes >= _residualBudgetBytes || strandedCount >= _strandCountCap)
        {
            throw Saturated(strandedBytes, strandedCount);
        }
    }

    private DecodeCapacityExhaustedException Saturated(long strandedBytes, int strandedCount) =>
        new(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"The bounded-decode worker is at capacity and rejected the decode without starting: the door's stranded residual is full of permanent strands — strandedBytes={strandedBytes}/{_residualBudgetBytes}, strandedStrands={strandedCount}/{_strandCountCap}. Healthy in-flight decodes are never charged here; retry after a strand quiesces or capacity frees."));

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
            // stranded-residual budget). No dedicated thread is created — it would only sit blocked in
            // GetResult() while the loop ran on the pool anyway (Round-4 simplification).
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

        // Checkpoint door: the decode is PREDOMINANTLY synchronous over a pre-buffered byte[], so a dedicated
        // background thread contains the CPU-bound work off the pool (it does not hand it back at an await).
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

    // Register an abandoned (detached, running-past-deadline) or a cancelled decode: at DETACH charge the
    // strand's footprint against the stranded residual and increment the strand gauge; then observe its eventual
    // fault so it is never re-raised on the finalizer thread as an unobserved task exception, dispose a
    // late-completing SUCCESSFUL result so a reader/stream that wins after the deadline is not leaked, fire the
    // caller's Settle (release the lease once the — possibly stranded — work stops), un-charge the strand if it
    // eventually terminates, and dispose the linked source. When countAsStrand is true (a GENUINE deadline
    // expiry) the residual/gauge are charged here and released when the work finally terminates; a
    // never-terminating strand holds exactly its footprint + one gauge slot for its whole lifetime. When
    // countAsStrand is false (a routine caller cancellation of a healthy decode) nothing is charged.
    private void AbandonInBackground<T>(
        Task<T> task,
        Action<T>? onAbandonedResult,
        CancellationTokenSource linked,
        Action settle,
        long strandCharge,
        bool countAsStrand)
    {
        if (countAsStrand)
        {
            Interlocked.Add(ref _strandedBytes, strandCharge);
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
                        Interlocked.Add(ref _strandedBytes, -strandCharge);
                        Interlocked.Decrement(ref _detached);
                    }

                    // Release the caller's lease now that the (possibly stranded) work has actually stopped
                    // touching the reader. For a never-terminating strand this continuation never runs, so the
                    // lease is held forever (bounded residual) — the reader is not disposed out from under it.
                    settle();
                    linked.Dispose();
                }
            },
            onAbandonedResult,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
