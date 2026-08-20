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
/// <para>Parquet.Net (6.1.0) can be driven by a single corrupted byte (a flipped terminal footer
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

    /// <summary>The maximum retained footprint a single <b>data-file</b> strand can pin — a bounded
    /// over-approximation of a whole-row-group decode within a documented constant factor (<b>3×</b> the
    /// reader's enforced per-row-group eager-decode ceiling = 12&#160;GiB = 3 × 4&#160;GiB, where 4&#160;GiB
    /// mirrors <c>ParquetFileReader.MaxRowGroupDecodedBytes</c> /
    /// <c>ParquetDecodeLimits.DefaultMaxRowGroupDecodedBytes</c>). A whole-row-group decode retains up to the
    /// decompressed ceiling PLUS the flat total-materialization ceiling (<c>EnsureDecodeCeiling</c> guards (ii)
    /// and (iv) each bound one 4&#160;GiB ceiling, Round-8 #2) PLUS the nested reconstruction that runs under its
    /// OWN independent <c>NestedParquetColumnReader.NestedDecodeBudget(MaxRowGroupDecodedBytes)</c> — which
    /// guards (ii)/(iv) do NOT count (Round-10 #5, Architect M3), so the honest peak is up to 3× the ceiling.
    /// This is a bounded over-approximation, NOT a strict Σ ceiling: variable-width string/binary UTF-16
    /// materialization is separately over-approximated by <c>VariableWidthMaterializationFactor</c> in the
    /// strand charge (<c>EstimateRowGroupRetainedBytes</c>), and per-element object overhead is not modelled
    /// byte-exactly — the guarantee is containment within this documented constant factor, which #802 tracks
    /// calibrating. Held locally (rather than referencing the reader constant) so the two doors'
    /// <c>static readonly</c> field init cannot depend on cross-type init order. It floors the residual budget
    /// and clamps the charge a data-file strand books at detach.</summary>
    internal const long DataFileMaxFootprintBytes = 3L * 4 * 1024 * 1024 * 1024;

    /// <summary>The maximum retained footprint a single <b>checkpoint</b> strand can pin — the isolated buffered
    /// part copy it holds (≤512&#160;MiB, mirrors <c>DeltaCheckpointReader.MaxCheckpointPartBytes</c>) PLUS the
    /// cumulative per-part decoded arrays + <c>List&lt;DeltaAction&gt;</c> across its row groups (≤8&#160;GiB,
    /// <c>DeltaCheckpointReader.MaxCheckpointPartDecodedBytes</c>, enforced in <c>DecodeBufferedAsync</c>) —
    /// 8.5&#160;GiB total (Round-8 #3; the decoded term rose from 4 to 8&#160;GiB in Round-10 #4 so a legit
    /// foreign multi-row-group part is not falsely rejected). Pre-fix the door charged only the compressed
    /// buffer, under-stating the decoded arrays a stranded checkpoint retains; the door footprint covers buffer
    /// + cumulative decoded so the strand's LIVE charge (<c>length + cumulativeDecoded</c>, Round-10 #1) clamps
    /// to a TRUE ceiling, not an under-statement. The 8.5&#160;GiB literal is held locally (rather than
    /// referencing the reader constants) so the two doors' <c>static readonly</c> field init cannot depend on
    /// cross-type init order. It floors the residual budget and clamps the charge a checkpoint strand books at
    /// detach.</summary>
    internal const long CheckpointMaxFootprintBytes = (8L * 1024 * 1024 * 1024) + (512L * 1024 * 1024);

    /// <summary>The floor multiple <c>k</c> applied to the max footprint when flooring a door's residual budget
    /// (<c>k × maxFootprint</c>, k≥2): at least one maximal legitimate decode is always admissible against an
    /// empty residual, AND a single strand can never instantly saturate the door. Calibration is tracked in #802.</summary>
    internal const int ResidualFloorMultiple = 2;

    /// <summary>The divisor of process memory that sets the residual budget's TARGET fraction (<c>1/8</c>): the
    /// residual budget aims for <c>processMem/8</c>, floored at <see cref="ResidualFloorMultiple"/>×maxFootprint
    /// and capped at <see cref="ResidualBudgetMemoryCapDivisor"/> (High #10). Calibration is tracked in #802.</summary>
    internal const long ResidualBudgetMemoryDivisor = 8;

    /// <summary>The divisor bounding the residual budget's UPPER limit against process memory (<c>1/2</c>): the
    /// residual budget is capped at <c>processMem/2</c> so on a small pod the byte gate stays reachable rather
    /// than flooring above pod memory (where a strand pile would OOM before the byte gate ever fired — the
    /// Round-8 High #10 fix). The budget is never dropped below one maximal footprint (construction still needs
    /// to admit one legit part); a door whose one-footprint floor exceeds this cap is flagged
    /// under-provisioned (surfaced as a one-shot startup Warning from the first <see cref="DeltaLog"/>
    /// construction PLUS the <c>door_under_provisioned</c> gauge). The max footprint (row-group / checkpoint-part
    /// class) is pod-independent, so a tiny pod can be structurally unable to admit one legit maximal part.</summary>
    internal const long ResidualBudgetMemoryCapDivisor = 2;

    /// <summary>The FLOOR on a door's strand-count cap (Round-8 High #1a — the count cap is decoupled from the
    /// byte budget). The old cap was <c>residualBudget/maxFootprint</c> = <b>2</b> on every pod ≤ 64&#160;GiB;
    /// with honest (small) charges that COUNT gate fired at 2 strands while <c>strandedBytes ≈ 0</c>, letting 2
    /// crafted decodes wedge a door process-wide. The count cap is now sized from the thread/fd budget
    /// (<c>k × ProcessorCount</c>) and floored here so it never binds under the byte budget — the BYTE residual
    /// (with a floored per-strand charge) stays the load-bearing gate. Calibration is tracked in #802.</summary>
    internal const int StrandCountFloor = 64;

    /// <summary>A generous ceiling on the STRAND count a door tolerates before fail-fast rejection (each strand
    /// also pins a thread/pool slot, so the count is bounded independently of the byte residual). The count cap
    /// applies to <b>strands only</b> — never to healthy in-flight decodes — so it does not throttle healthy
    /// scan concurrency. Calibration is tracked in #802.</summary>
    internal const int StrandCountCeiling = 256;

    /// <summary>The multiple of <see cref="Environment.ProcessorCount"/> the strand-count cap is sized from
    /// (thread/fd-budget proxy), clamped into <c>[<see cref="StrandCountFloor"/>, <see cref="StrandCountCeiling"/>]</c>.
    /// Sized so the count never binds before the byte residual for maximal strands, yet still bounds the thread
    /// pile a tiny-footprint-strand flood would create. Calibration is tracked in #802.</summary>
    internal const int StrandCountThreadMultiple = 8;

    /// <summary>The multiple of <see cref="Environment.ProcessorCount"/> a door's in-flight admission ceiling
    /// (<c>C_max</c>) is sized from (Round-8 High #6 — bounding the number of concurrently-admitted untrusted
    /// decodes). It is deliberately GENEROUS (far above any healthy scan width) so healthy in-flight work is
    /// never throttled, yet C is finite so the stranded residual is provably bounded to
    /// <c>residualBudget + C_max × maxFootprint</c>. Calibration is tracked in #802.</summary>
    internal const int InFlightCeilingThreadMultiple = 64;

    /// <summary>The FLOOR on the per-strand byte charge (64&#160;MiB, Round-8 High #1b). A strand's charge is
    /// <c>clamp(max(estimate, this), 0, maxFootprint)</c> so even a CHEAP strand (a small crafted part) consumes
    /// a meaningful slice of the byte residual — otherwise, with the count cap now decoupled/raised, a flood of
    /// tiny-footprint strands could pile up under the byte gate toward an OOM. Applied at the production call
    /// sites (the data-file open/row-group and the checkpoint part) so the byte budget stays the load-bearing
    /// gate; the door's <see cref="RunAsync"/> charges exactly what it is passed (clamped to the max footprint),
    /// keeping the count-cap-in-isolation unit tests (which pass a zero estimate) literal. Calibration is
    /// tracked in #802.</summary>
    internal const long MinStrandChargeBytes = 64L * 1024 * 1024;

    /// <summary>The wall-clock window (5&#160;min) with ZERO strand drain, while a door is saturated, after which
    /// the door is reported <b>wedged</b> (Round-8 High #1 wedged-door signal): its strands are not draining, so
    /// <see cref="StorageErrorKind.DecoderSaturated"/> is NOT genuinely "retry after backoff" — a liveness probe
    /// should recycle the pod. Surfaced as the <c>deltasharp.storage.decode.wedged</c> gauge (per door).</summary>
    internal static readonly TimeSpan WedgedDrainStallWindow = TimeSpan.FromMinutes(5);

    /// <summary>The wall-clock grace (Round-10 #2) a CALLER-cancelled decode is given to DRAIN before it is
    /// booked as a strand. On the caller-cancellation path the door cancels the linked token and then re-races
    /// the work against this grace: a COOPERATIVE healthy decode observes the cancellation and terminates within
    /// milliseconds, so it wins the race, is NOT counted, and is NOT charged (the "healthy in-flight is NEVER
    /// charged" invariant holds even when cancelled — cancelling many concurrent scans no longer transiently
    /// saturates the door with spurious <see cref="StorageErrorKind.DecoderSaturated"/>). Only a token-IGNORING
    /// non-terminating decode is still running after the grace; it is then booked (counted + charged) exactly as
    /// a deadline strand, so a laundering hang stays bounded. Kept comfortably above a cooperative unwind so a
    /// healthy cancel never races the grace, yet short enough that a laundered strand is booked promptly.</summary>
    internal static readonly TimeSpan CancellationDrainGrace = TimeSpan.FromMilliseconds(500);

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
    /// memory. PURE (no shared state — <paramref name="processorCount"/> is passed in, defaulting to
    /// <see cref="Environment.ProcessorCount"/>) so it is table-testable across pod sizes.
    /// <para><b>Residual budget (the load-bearing byte gate).</b> It targets <c>processMem/8</c>
    /// (<see cref="ResidualBudgetMemoryDivisor"/>), floored at <c>k × maxFootprint</c>
    /// (<see cref="ResidualFloorMultiple"/>, so one legit part is admissible and a single strand can never
    /// instantly saturate the door), then capped at <c>processMem/2</c>
    /// (<see cref="ResidualBudgetMemoryCapDivisor"/>, High #10 — so on a small pod the budget cannot floor
    /// ABOVE pod memory and render the byte gate unreachable). The budget is never dropped below ONE maximal
    /// footprint (construction needs to admit one legit part); a door whose one-footprint floor exceeds the
    /// memory cap is <see cref="DoorSizing.UnderProvisioned"/> (surfaced as a one-shot startup Warning from the
    /// first <see cref="DeltaLog"/> construction PLUS the <c>door_under_provisioned</c> gauge), because a
    /// pod-independent maximal footprint cannot fit a tiny pod.</para>
    /// <para><b>Strand-count cap (decoupled from the byte budget, High #1a).</b> Sized from the thread/fd
    /// budget as <c>StrandCountThreadMultiple × processorCount</c>, clamped to
    /// <c>[<see cref="StrandCountFloor"/>, <see cref="StrandCountCeiling"/>]</c> — NOT
    /// <c>residualBudget/maxFootprint</c> (which was 2 on every pod ≤ 64&#160;GiB and wedged a door at 2 cheap
    /// strands). It never binds before the byte residual for maximal strands, while still bounding the thread
    /// pile a tiny-footprint-strand flood would create.</para></summary>
    internal static DoorSizing DeriveDoorSizing(long processMemoryBytes, long maxFootprintBytes, int processorCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFootprintBytes);
        long mem = Math.Max(processMemoryBytes, 0L);
        long oneFootprint = maxFootprintBytes;
        long preferredFloor = SaturatingMul(ResidualFloorMultiple, maxFootprintBytes);
        long target = Math.Max(mem / ResidualBudgetMemoryDivisor, preferredFloor);
        // High #10 cap: never let the residual budget floor ABOVE a fraction of pod memory (the byte gate must
        // stay reachable), but never below ONE maximal footprint (construction must admit one legit part).
        long memCap = Math.Max(mem / ResidualBudgetMemoryCapDivisor, oneFootprint);
        long residualBudget = Math.Max(Math.Min(target, memCap), oneFootprint);
        bool underProvisioned = preferredFloor > memCap; // can't fit the preferred k-part floor in the mem cap

        int cores = processorCount > 0 ? processorCount : Math.Max(Environment.ProcessorCount, 1);
        long threadBudget = SaturatingMul(StrandCountThreadMultiple, cores);
        int countCap = (int)Math.Clamp(threadBudget, StrandCountFloor, StrandCountCeiling);
        return new DoorSizing(residualBudget, countCap, maxFootprintBytes, underProvisioned);
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

    /// <summary>
    /// The nested-write §2.4b footer RECONCILIATION door: DeltaSharp reading back the footer of a file it just
    /// authored itself, to check the footer's <c>NumRows</c> against the batch-derived row total.
    /// <para>It is a THIRD door, not the data-file one, because that door admits UNTRUSTED reads: sharing
    /// would couple the two in both directions — a flood of hostile reads saturating the door would fail
    /// otherwise-healthy WRITES with <c>DecoderSaturated</c>, and every write would consume an admission slot
    /// sized for untrusted decodes.</para>
    /// <para>It runs on a DEDICATED THREAD (as the checkpoint door does, and for the same reason): a footer
    /// parse over an already-materialized window is predominantly synchronous CPU work, so it must not queue
    /// behind ThreadPool work that untrusted decode strands can pin — a write must remain verifiable while
    /// the pool is under pressure.</para>
    /// <para>It is sized for a FOOTER-only footprint
    /// (<see cref="Parquet.ParquetFileReader.MaxFooterMetadataBytes"/>, which is exactly what a strand of this
    /// read can retain) rather than a whole-data-file one, so a write-verification strand is charged and
    /// budgeted for the work it actually does.</para>
    /// </summary>
    internal static readonly BoundedDecoder ReconciliationDecoder =
        BoundedDecoder.FromSizing(
            DeriveDoorSizing(ProcessMemoryBytes, Parquet.ParquetFileReader.MaxFooterMetadataBytes),
            DecodeExecution.DedicatedThread);

    /// <summary>The total count of detached (running-past-deadline) strands across ALL THREE doors — exposed as
    /// the <c>deltasharp.storage.decode.detached</c> observability gauge and for tests that assert strands
    /// drain. The reconciliation door is included: a strand there holds a thread and bytes exactly like any
    /// other, so leaving it out would make write-verification strands invisible to the detached-strand
    /// gauge.</summary>
    internal static int DetachedDecodeCount =>
        DataFileDecoder.DetachedDecodeCount + CheckpointDecoder.DetachedDecodeCount
        + ReconciliationDecoder.DetachedDecodeCount;

    /// <summary>The detached-strand count on the data-file door (observability gauge dimension).</summary>
    internal static int DataFileDetachedDecodeCount => DataFileDecoder.DetachedDecodeCount;

    /// <summary>The detached-strand count on the checkpoint door (observability gauge dimension).</summary>
    internal static int CheckpointDetachedDecodeCount => CheckpointDecoder.DetachedDecodeCount;

    /// <summary>The detached-strand count on the §2.4b write-reconciliation door (observability gauge
    /// dimension).</summary>
    internal static int ReconciliationDetachedDecodeCount => ReconciliationDecoder.DetachedDecodeCount;

    /// <summary>Whether the §2.4b write-reconciliation door is wedged (see <see cref="DataFileWedged"/>).</summary>
    internal static int ReconciliationWedged => ReconciliationDecoder.IsWedged ? 1 : 0;

    /// <summary>Whether the data-file door is <b>wedged</b> — saturated with ZERO strand drain for
    /// <see cref="WedgedDrainStallWindow"/> (Round-8 High #1). Surfaced as <c>deltasharp.storage.decode.wedged</c>
    /// (0/1) so a liveness probe can recycle the pod (its strands never drain, so <c>DecoderSaturated</c> is not
    /// genuinely retryable).</summary>
    internal static int DataFileWedged => DataFileDecoder.IsWedged ? 1 : 0;

    /// <summary>Whether the checkpoint door is wedged (see <see cref="DataFileWedged"/>).</summary>
    internal static int CheckpointWedged => CheckpointDecoder.IsWedged ? 1 : 0;

    /// <summary>Whether the data-file door is <b>under-provisioned</b> (Round-10 #7): its one-footprint floor
    /// exceeds the process-memory cap, so on this pod it cannot comfortably admit one legit maximal part.
    /// Surfaced as <c>deltasharp.storage.decode.door_under_provisioned</c> (0/1) so the structural limit is
    /// observable rather than an unconsumed flag.</summary>
    internal static int DataFileUnderProvisioned => DataFileDecoder.UnderProvisioned ? 1 : 0;

    /// <summary>Whether the checkpoint door is under-provisioned (see <see cref="DataFileUnderProvisioned"/>).</summary>
    internal static int CheckpointUnderProvisioned => CheckpointDecoder.UnderProvisioned ? 1 : 0;
}

/// <summary>A door's derived sizing: its stranded-residual byte budget, its strand-count cap, the max
/// single-strand footprint the budget was floored against, and whether the door is under-provisioned (its
/// one-footprint floor exceeds the process-memory cap, so it cannot comfortably admit one legit maximal part —
/// a one-shot startup Warning from the first <see cref="DeltaLog"/> construction PLUS the
/// <c>door_under_provisioned</c> gauge, High #10) — see <see cref="BoundedDecode.DeriveDoorSizing"/>.</summary>
internal readonly record struct DoorSizing(
    long ResidualBudgetBytes, int StrandCountCap, long MaxFootprintBytes, bool UnderProvisioned = false);

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
    private readonly int _inFlightCeiling;
    private readonly bool _underProvisioned;

    // The STRANDED residual (the load-bearing bound). Charged ONLY at DETACH (a genuine deadline expiry OR a
    // caller-cancellation that abandons a non-terminating decode, High #5) with the decode's actual retained
    // footprint clamped to _maxFootprintBytes; un-charged only if a strand eventually terminates. A healthy
    // in-flight decode is NEVER charged here, so it can never be throttled by this control. A genuine
    // non-terminating strand holds its charge forever — the bounded residual.
    private long _strandedBytes;

    // Detached strands only (abandoned past their deadline OR abandoned by caller-cancellation and still
    // running) — the COUNT companion to _strandedBytes. Incremented at DETACH; decremented when (if) that
    // abandoned work finally settles. A HEALTHY cancelled decode settles in ms so it drains immediately (costing
    // healthy cancellation nothing); only a token-IGNORING non-terminating cancelled decode holds its slot
    // (High #5). Exposed as the observability gauge.
    private int _detached;

    // The cancelled-flavour sub-count of _detached (a telemetry gauge DIMENSION only, High #5): strands abandoned
    // via caller-cancellation rather than a deadline expiry. Both flavours charge the residual identically; this
    // only distinguishes them for observability.
    private int _cancelledDetached;

    // In-flight admission counter (High #6 — bounding C, the number of concurrently-admitted untrusted decodes).
    // Incremented at admission, decremented when the decode terminally settles (healthy completion, strand drain,
    // or cancellation drain). A never-terminating strand holds its slot forever (as it holds a strand slot). The
    // ceiling (_inFlightCeiling) is GENEROUS (k × ProcessorCount, far above healthy scan width) so healthy work
    // is never throttled, yet C is finite — the residual is bounded to residualBudget + C_max × maxFootprint.
    private int _inFlight;

    // Timestamp (from _clock) of the last strand ACTIVITY (a charge or a drain), for the wedged-door signal
    // (High #1). While the door is saturated and there has been NO strand activity within WedgedDrainStallWindow,
    // the door is reported wedged so a liveness probe can recycle the pod. Seeded at construction (a
    // never-saturated door is never "wedged"). Uses the injected _clock so it is deterministically testable.
    private long _lastStrandActivityTimestamp;

    // One-shot guard (Round-13) so the under-provisioned startup Warning is emitted at most once per door
    // instance. The static door fields have no ILogger in scope at type init, so the Warning fires from the
    // first DeltaLog construction that reaches a logger (see WarnIfUnderProvisioned); this flag makes it
    // idempotent across every such construction.
    private int _underProvisionedWarned;

    private readonly TimeProvider _clock;

    internal BoundedDecoder(
        int strandCountCap,
        long residualBudgetBytes = long.MaxValue,
        long maxFootprintBytes = 1,
        DecodeExecution execution = DecodeExecution.Pool,
        int inFlightCeiling = 0,
        TimeProvider? clock = null,
        bool underProvisioned = false)
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
        _underProvisioned = underProvisioned;
        _clock = clock ?? TimeProvider.System;
        _lastStrandActivityTimestamp = _clock.GetTimestamp();
        // The generous in-flight ceiling (High #6). Default (0) derives k × ProcessorCount, floored well above
        // the strand-count cap so it never binds before the strand gates for a genuine strand pile — it only
        // bounds C (concurrently-admitted decodes) for the memory-bound proof. Tests may pin it explicitly.
        _inFlightCeiling = inFlightCeiling > 0
            ? inFlightCeiling
            : Math.Max(
                strandCountCap,
                (int)Math.Min(
                    (long)BoundedDecode.InFlightCeilingThreadMultiple * Math.Max(Environment.ProcessorCount, 1),
                    int.MaxValue));
    }

    /// <summary>Builds a decoder from a derived <see cref="DoorSizing"/> (the production path). Carries the
    /// sizing's <see cref="DoorSizing.UnderProvisioned"/> flag through so the door can surface it (Round-10 #7).</summary>
    internal static BoundedDecoder FromSizing(DoorSizing sizing, DecodeExecution execution) =>
        new(sizing.StrandCountCap, sizing.ResidualBudgetBytes, sizing.MaxFootprintBytes, execution,
            underProvisioned: sizing.UnderProvisioned);

    /// <summary>The fail-fast cap on the COUNT of concurrent STRANDS (never healthy in-flight decodes).</summary>
    internal int StrandCountCap => _strandCountCap;

    /// <summary>Whether this door is <b>under-provisioned</b> (Round-8 High #10 / Round-10 #7): its one-footprint
    /// floor exceeds the process-memory cap, so it cannot comfortably admit one legit maximal part and admits at
    /// most one maximal strand before saturating (fail-fast, retryable). Surfaced as a one-shot startup Warning
    /// (see <see cref="WarnIfUnderProvisioned"/>, emitted from the first <see cref="DeltaLog"/> construction) PLUS
    /// the <c>deltasharp.storage.decode.door_under_provisioned</c> gauge so the honest structural limit is
    /// observable rather than an unconsumed flag.</summary>
    internal bool UnderProvisioned => _underProvisioned;

    /// <summary>Emits the one-shot startup <b>Warning</b> (Round-13) if this door is under-provisioned — at most
    /// once per door instance (Interlocked-guarded). The process-global door fields have no <see cref="ILogger"/>
    /// in scope at static-field init, so this is called from the first <see cref="DeltaLog"/> construction that
    /// reaches a logger; the once-flag makes it idempotent across every subsequent construction. It is the
    /// operator-valued half of the under-provisioned signal (the sibling
    /// <c>deltasharp.storage.decode.door_under_provisioned</c> gauge is the machine-readable half). The message
    /// names only the fixed <paramref name="door"/> label and the door's own derived sizing (residual budget,
    /// max footprint, process memory) — never any untrusted byte content. A no-op when the door is adequately
    /// provisioned, or when a <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger{T}"/> is wired.</summary>
    internal void WarnIfUnderProvisioned(Microsoft.Extensions.Logging.ILogger logger, string door)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(door);

        // Gate on IsEnabled BEFORE consuming the once-token: a NullLogger (or any logger with Warning
        // suppressed) returns false, so it must NOT flip _underProvisionedWarned — otherwise the first
        // DeltaLog built without a real logger would permanently swallow the warning for every later
        // DeltaLog that DOES provide one. The token is spent only when an emission actually happens.
        if (!_underProvisioned || !logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning))
        {
            return;
        }

        if (Interlocked.Exchange(ref _underProvisionedWarned, 1) != 0)
        {
            return;
        }

        DeltaDecodeLog.DoorUnderProvisioned(
            logger, door, _maxFootprintBytes, _residualBudgetBytes, BoundedDecode.ProcessMemoryBytes);
    }

    /// <summary>The stranded-residual byte budget (the load-bearing memory bound), charged only at detach.</summary>
    internal long ResidualBudgetBytes => _residualBudgetBytes;

    /// <summary>The max single-strand footprint the charge is clamped to and the budget was floored against.</summary>
    internal long MaxFootprintBytes => _maxFootprintBytes;

    /// <summary>How a detached strand is hosted (Pool for the data-file door; DedicatedThread for the
    /// checkpoint door) — exposed for the per-door isolation test.</summary>
    internal DecodeExecution Execution => _execution;

    /// <summary>The generous ceiling (<c>C_max</c>) on concurrently-admitted untrusted decodes (High #6), far
    /// above healthy scan width so healthy work is never throttled; it makes C finite so the residual is bounded
    /// to <c>residualBudget + C_max × maxFootprint</c>.</summary>
    internal int InFlightCeiling => _inFlightCeiling;

    /// <summary>The current count of admitted-but-not-yet-settled decodes (healthy in-flight + live strands) —
    /// exposed for the High #6 in-flight-ceiling behavioral test.</summary>
    internal int InFlightCount => Volatile.Read(ref _inFlight);

    /// <summary>The current count of detached (running-past-deadline) strands — exposed for the observability
    /// gauge and for tests that assert the strand cap and that strands drain.</summary>
    internal int DetachedDecodeCount => Volatile.Read(ref _detached);

    /// <summary>The cancelled-flavour sub-count of <see cref="DetachedDecodeCount"/> (a telemetry gauge
    /// dimension, High #5) — strands abandoned by caller-cancellation rather than a deadline expiry.</summary>
    internal int CancelledDetachedDecodeCount => Volatile.Read(ref _cancelledDetached);

    /// <summary>The current stranded-residual bytes (charged at detach, un-charged when a strand terminates) —
    /// exposed for tests that assert the byte-aware residual bound.</summary>
    internal long StrandedDecodeBytes => Volatile.Read(ref _strandedBytes);

    /// <summary>Whether this door is <b>wedged</b> (High #1): currently saturated (byte OR strand-count OR the
    /// in-flight admission ceiling, Round-10 #10) with NO strand drain for
    /// <see cref="BoundedDecode.WedgedDrainStallWindow"/>. A wedged door's strands are not draining, so
    /// <see cref="DecodeCapacityExhaustedException"/> is not genuinely retryable — a liveness probe should
    /// recycle the pod. Surfaced as the <c>deltasharp.storage.decode.wedged</c> gauge.
    /// <para><b>The in-flight arm is gated on the door having at least one STRAND (Round-13):</b> a door with
    /// ZERO strands cannot be wedged — its admitted decodes are HEALTHY in-flight work that will settle and
    /// release their slots, not un-abortable strands. Without this gate a burst of healthy concurrent decodes
    /// reaching <c>C_max</c> for longer than the stall window (no strand had ever charged, so the activity clock
    /// stays at construction time) would falsely report <c>wedged=1</c> whose documented action is "recycle the
    /// pod" — a false liveness kill. The byte/strand-count arms already imply a strand, so gating only the
    /// in-flight arm keeps a genuine strand pile at <c>C_max</c> reportable while a strand-free saturation is
    /// not.</para></summary>
    internal bool IsWedged
    {
        get
        {
            bool saturated = Volatile.Read(ref _strandedBytes) >= _residualBudgetBytes
                || Volatile.Read(ref _detached) >= _strandCountCap
                || (Volatile.Read(ref _inFlight) >= _inFlightCeiling && Volatile.Read(ref _detached) > 0);
            if (!saturated)
            {
                return false;
            }

            long stalled = _clock.GetElapsedTime(Volatile.Read(ref _lastStrandActivityTimestamp)).Ticks;
            return stalled >= BoundedDecode.WedgedDrainStallWindow.Ticks;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> under a wall-clock <paramref name="budget"/> (measured from EXECUTION start,
    /// I3), after checking this door is not already saturated by STRANDS (not by healthy in-flight — those are
    /// never charged nor throttled). Returns the work's result when it finishes first (surfacing the work's own
    /// outcome — a value, a typed fail-closed exception, or cancellation — <b>unwrapped</b>). If the budget
    /// expires first, the decode DETACHES: it charges <paramref name="estimatedRetainedBytes"/> (clamped to the
    /// door's max footprint) against the stranded residual, throws the exception produced by
    /// <paramref name="onTimeout"/> (a FIXED, sanitized fail-closed message), and leaves the work running
    /// detached (bounded by the residual budget). Caller cancellation is distinguished from a genuine deadline:
    /// a cancelled <paramref name="cancellationToken"/> always surfaces <see cref="OperationCanceledException"/>,
    /// never the timeout exception. Whether that cancellation is charged depends on how the decode responds
    /// (the drain-grace contract, Round-10 #2): a <b>cooperative</b> decode that observes the cancellation and
    /// drains within <see cref="BoundedDecode.CancellationDrainGrace"/> is NOT counted as a strand and NOT
    /// charged (so cancelling many healthy scans never transiently saturates the door); a <b>token-ignoring</b>
    /// decode still running past the grace IS booked as a cancelled strand — counted and charged exactly like a
    /// deadline strand — so a laundered hang stays bounded.
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
    /// completion charges nothing. Zero is used by unit tests that exercise the strand-COUNT cap in isolation.
    /// Ignored when <paramref name="retainedBytesProbe"/> is supplied.</param>
    /// <param name="retainedBytesProbe">An optional LIVE charge probe (Round-10 #1): when supplied, the strand
    /// charge is read from it AT THE MOMENT OF DETACH (not from the fixed <paramref name="estimatedRetainedBytes"/>),
    /// so a decode whose retained footprint GROWS as it runs (the checkpoint part's cumulative decoded arrays)
    /// charges the actual incremental bytes it has materialized when it strands — never a flat ceiling that would
    /// degenerate the byte gate into a de-facto count cap. The probe is invoked on the detach path only; its
    /// result is clamped to the door's max footprint like the static estimate. The production caller floors the
    /// probe's result at <see cref="MinStrandChargeBytes"/> so a cheap strand still consumes the byte residual.</param>
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
        long estimatedRetainedBytes = 0,
        Func<long>? retainedBytesProbe = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(onTimeout);

        // The charge a strand of this decode would book at detach, evaluated AT DETACH so a live probe
        // (Round-10 #1) reflects the actual incremental retained bytes rather than a flat ceiling; clamped to
        // the door's max footprint so a mis-estimate can never over-charge the residual (the bound stays
        // provable). The production call sites FLOOR their estimate/probe at BoundedDecode.MinStrandChargeBytes
        // (High #1b) so a cheap strand still consumes the byte residual; this method stays literal (charges
        // exactly what it reads) so the count-cap-in-isolation unit tests can pass a zero estimate.
        long ComputeStrandCharge() =>
            Math.Clamp(retainedBytesProbe is null ? estimatedRetainedBytes : retainedBytesProbe(), 0L, _maxFootprintBytes);

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

        // Release the in-flight admission slot EXACTLY ONCE (High #6). A never-terminating strand never calls
        // this (its slot is held for its whole life, as its strand slot is), so C is bounded by the in-flight
        // ceiling and the residual by residualBudget + C_max × maxFootprint.
        int inFlightReleased = 0;
        void ReleaseInFlight()
        {
            if (Interlocked.Exchange(ref inFlightReleased, 1) == 0)
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        try
        {
            // Validate the budget AFTER the null guards but BEFORE admission so an arg-validation throw still
            // fires Settle (the arg-validation-before-Settle fix): a caller that took a lease before calling with
            // a bad budget never leaks it. Enforce budget ≤ MaxBudget HERE too (High #8) so a caller cannot
            // disable the DoS control with an absurd budget even on a path that bypassed ParquetDecodeLimits.
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget.Ticks, nameof(budget));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(budget, BoundedDecode.MaxBudget, nameof(budget));

            // Pre-start caller cancellation: never start a decode for an already-cancelled caller.
            cancellationToken.ThrowIfCancellationRequested();

            // Admission (fail-fast) against the current STRANDED residual — NOT against healthy in-flight, which
            // is never charged nor counted. A new untrusted decode is admitted unless the door is already
            // saturated by PERMANENT strands (strandedBytes ≥ budget OR strandedCount ≥ cap) OR the generous
            // in-flight ceiling (C_max) is reached; otherwise it is admitted WITHOUT charging anything and takes
            // an in-flight slot. Over-saturation surfaces a DISTINCT fail-closed DecodeCapacityExhaustedException
            // (never a decode-timeout, never negatively cached).
            AdmitOrReject();
        }
        catch
        {
            // Pre-start throw (arg-validation, cancellation, or capacity): fire onWorkSettled so the caller's
            // lease is released exactly once even though no work task will ever settle. In-flight was NOT
            // incremented (AdmitOrReject is the last try statement and self-backs-out on rejection), so there is
            // no in-flight slot to release here.
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
            // (no strand), but an in-flight slot WAS taken at admission — release it. Fire Settle (release the
            // lease), dispose the linked source, and surface the failure. The unobserved workTcs is never awaited
            // — fault it so a late set cannot surface as an unobserved task exception.
            workTcs.TrySetException(ex);
            _ = workTcs.Task.Exception; // observe
            ReleaseInFlight();
            Settle();
            linked.Dispose();
            throw;
        }

        // Post-start region (High #8). EVERYTHING below (the start hop, arming the delay, the race, the
        // cancel/detach handling) is guarded so an unexpected throw — e.g. a Task.Delay ObjectDisposedException
        // or a CancelAsync fault — never skips Settle/Dispose/strand-accounting and never leaves workTcs
        // unobserved. The three intended throw paths (start-hop cancellation, caller cancellation, deadline
        // expiry) each register their abandonment and set `abandoned` before throwing, so the guard's fallback
        // AbandonInBackground fires ONLY for a genuinely unexpected fault (never double-charging a strand).
        bool abandoned = false;

        // Cancel-and-abandon with a DRAIN GRACE (Round-10 #2/#9). Used by BOTH caller-cancellation branches (the
        // start-hop cancel and the delay-won cancel). Sets `abandoned` BEFORE CancelAsync so a token-registration
        // fault on cancel cannot re-open strand-laundering via the unexpected-fault fallback (#9), cancels the
        // linked token, then re-races the work against a short grace: a COOPERATIVE healthy decode observes the
        // cancellation and terminates within ms — it wins the race, so it is NOT counted and NOT charged
        // (cancelling many concurrent healthy scans never transiently saturates the door — the "healthy in-flight
        // is NEVER charged" invariant holds under cancellation). Only a token-IGNORING non-terminating decode is
        // still running after the grace; it is then booked (counted + charged) exactly like a deadline strand.
        // Fail-safe: countAsStrand = !workTcs.Task.IsCompleted (a decode that has terminated is never booked).
        //
        // Because `abandoned` is already true, a fault from CancelAsync (a throwing token registration) or the
        // grace timer (a custom TimeProvider.CreateTimer) would escape PAST the `catch when (!abandoned)` guard
        // and leak Settle / in-flight / CTS (Balanced Low, Round-13). The CancelAsync + grace race is therefore
        // wrapped: on fault the drain race is treated as NOT completed, so AbandonInBackground still runs exactly
        // once — fail-safe toward BOOKING a strand (countAsStrand: true) — preserving the ordering fix and the
        // exactly-once cleanup, then the original OperationCanceledException surfaces from the caller.
        async Task CancelAbandonAsync()
        {
            abandoned = true;
            bool drained = false;
            try
            {
                await linked.CancelAsync().ConfigureAwait(false);
                await Task.WhenAny(
                    workTcs.Task,
                    Task.Delay(BoundedDecode.CancellationDrainGrace, timeProvider)).ConfigureAwait(false);
                drained = true;
            }
            catch
            {
                // A fault in the courtesy cancel or the grace timer must NOT escape and skip the detach
                // bookkeeping. Fall through to AbandonInBackground with the drain treated as incomplete.
            }

            // On a clean race, book only if the work is still running; on a faulted race, fail safe and book
            // (countAsStrand: true) so nothing leaks and the bound stays conservative.
            bool stillRunning = !drained || !workTcs.Task.IsCompleted;
            AbandonInBackground(
                workTcs.Task, onAbandonedResult, linked, Settle, ReleaseInFlight, ComputeStrandCharge(),
                countAsStrand: stillRunning, cancelled: stillRunning);
        }

        try
        {
            // Wait for the work to ACTUALLY START executing before arming the deadline (I3). This is a thread
            // hop, not an admission/queue wait, so it is negligible and never counted against the budget.
            // Cancellable: if the caller cancels during the start hop, cancel-and-abandon the (now-running)
            // decode under the drain grace (a healthy decode drains free; only a token-ignoring hang is booked,
            // High #5 / Round-10 #2) and surface the OCE.
            try
            {
                await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await CancelAbandonAsync().ConfigureAwait(false);
                throw;
            }

            Task delayTask = Task.Delay(budget, timeProvider, linked.Token);

            await Task.WhenAny(workTcs.Task, delayTask).ConfigureAwait(false);

            if (workTcs.Task.IsCompleted)
            {
                // Work won the race (success OR a typed/library fault OR its own cancellation) — a HEALTHY
                // in-budget outcome. Nothing was ever charged, so there is no residual to release. Cancel the
                // delay timer, release the in-flight slot, then fire Settle SYNCHRONOUSLY (High #8): the caller's
                // lease is released — and thus its reader/stream deterministically disposed — before RunAsync
                // returns, closing the race where an async settle continuation could run after the caller had
                // already returned. CancelAsync completes the delay's cancellation asynchronously (off this
                // path) so the delay's registrations do not run synchronously on the completion path. Then
                // surface the work's own outcome UNWRAPPED.
                await linked.CancelAsync().ConfigureAwait(false);
                ReleaseInFlight();
                Settle();
                linked.Dispose();
                return await workTcs.Task.ConfigureAwait(false);
            }

            // The delay won: either the caller cancelled, or the deadline genuinely expired. Distinguish them so
            // caller cancellation stays control flow (OperationCanceledException) and is NEVER masked as a timeout.
            if (cancellationToken.IsCancellationRequested)
            {
                // Caller cancellation (Round-10 #2). Cancel-and-abandon under the drain grace: a cooperative
                // healthy decode drains in ms and costs nothing (NOT counted, NOT charged), while a token-
                // IGNORING non-terminating decode is booked as a CANCELLED strand so it cannot launder the bound
                // (it would otherwise hold thread+bytes+lease FOREVER, invisible to the residual — High #5).
                await CancelAbandonAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Genuine deadline expiry — the decode DETACHES. Set `abandoned` first (#9), cancel the linked token
            // (a courtesy, in case the detached decode observes it), charge the strand's LIVE footprint against
            // the stranded residual + increment the strand gauge (un-charged only if the strand eventually
            // terminates), observe/dispose its eventual outcome, and fail closed with the caller-supplied typed
            // exception. A deadline strand always counts (the budget already elapsed without completion) — no
            // drain grace here.
            abandoned = true;
            try
            {
                await linked.CancelAsync().ConfigureAwait(false);
            }
            catch
            {
                // The courtesy cancel faulted (a throwing token registration / custom timer). Because `abandoned`
                // is already true it would escape past the `catch when (!abandoned)` guard and skip the detach
                // bookkeeping below, leaking Settle / in-flight / CTS (Balanced Low, Round-13). Swallow it — the
                // AbandonInBackground call below still books the strand and cleans up exactly once, preserving
                // the cancel-then-detach ordering.
            }

            AbandonInBackground(
                workTcs.Task, onAbandonedResult, linked, Settle, ReleaseInFlight, ComputeStrandCharge(),
                countAsStrand: true, cancelled: false);
            throw onTimeout(budget);
        }
        catch when (!abandoned)
        {
            // A genuinely unexpected fault in the post-start region (the intended cancel/timeout paths already
            // set `abandoned` before throwing). Abandon the work so nothing leaks: observe/dispose it, release
            // the lease + in-flight slot, dispose the linked source; then rethrow unchanged.
            AbandonInBackground(
                workTcs.Task, onAbandonedResult, linked, Settle, ReleaseInFlight, ComputeStrandCharge(),
                countAsStrand: false, cancelled: false);
            throw;
        }
    }

    // Admission gate (fail-fast). Rejects when the door is genuinely full of PERMANENT strands (byte residual OR
    // strand count) OR the generous in-flight ceiling (C_max) is reached — never against healthy in-flight for a
    // byte/count reason below C_max, so healthy scan concurrency is never throttled. On admission it takes an
    // in-flight slot (High #6). Throws a fail-closed DecodeCapacityExhaustedException with a TRUTHFUL message so
    // a byte-saturated vs count-saturated vs wedged condition is distinguishable in the surfaced text.
    private void AdmitOrReject()
    {
        long strandedBytes = Volatile.Read(ref _strandedBytes);
        int strandedCount = Volatile.Read(ref _detached);
        if (strandedBytes >= _residualBudgetBytes || strandedCount >= _strandCountCap)
        {
            throw Saturated(strandedBytes, strandedCount, inFlight: Volatile.Read(ref _inFlight));
        }

        // High #6: bound C (concurrently-admitted untrusted decodes) with a generous ceiling so C is finite (the
        // residual is bounded to residualBudget + C_max × maxFootprint). Healthy work never reaches this ceiling.
        int inFlight = Interlocked.Increment(ref _inFlight);
        if (inFlight > _inFlightCeiling)
        {
            Interlocked.Decrement(ref _inFlight);
            throw Saturated(strandedBytes, strandedCount, inFlight: inFlight - 1);
        }
    }

    private DecodeCapacityExhaustedException Saturated(long strandedBytes, int strandedCount, int inFlight)
    {
        // When the door is WEDGED (saturated with zero strand drain over the stall window, High #1) the caller
        // should NOT be told to simply "retry after backoff" — the strands are not draining, so a liveness probe
        // must recycle the pod. Otherwise the condition is a transient, retryable capacity fault.
        string tail = IsWedged
            ? "The door appears WEDGED — its strands have not drained within the stall window, so this is NOT a "
                + "transient condition a retry clears; a liveness probe should recycle the pod "
                + "(deltasharp.storage.decode.wedged=1)."
            : "Healthy in-flight decodes are never charged here; retry after a strand quiesces or capacity frees.";
        return new DecodeCapacityExhaustedException(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"The bounded-decode worker is at capacity and rejected the decode without starting: strandedBytes={strandedBytes}/{_residualBudgetBytes}, strandedStrands={strandedCount}/{_strandCountCap}, inFlight={inFlight}/{_inFlightCeiling}. {tail}"));
    }

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

    // Register an abandoned decode: a DETACHED (running-past-deadline) strand, a CANCELLED strand (a
    // caller-cancelled decode still running — High #5), or an unexpected-fault abandonment. When countAsStrand
    // is true it charges the strand's footprint against the stranded residual and increments the strand gauge
    // (and, when `cancelled`, the cancelled-flavour sub-gauge — a telemetry DIMENSION only); it then observes the
    // eventual fault so it is never re-raised on the finalizer thread as an unobserved task exception, disposes a
    // late-completing SUCCESSFUL result so a reader/stream that wins after abandonment is not leaked, un-charges
    // the strand + releases the in-flight slot + records a drain WHEN the work finally terminates, fires the
    // caller's Settle (release the lease), and disposes the linked source. A never-terminating strand holds
    // exactly its footprint + one strand slot + one in-flight slot for its whole lifetime (the bounded residual).
    // When countAsStrand is false (an unexpected-fault abandonment) nothing is charged, but the in-flight slot is
    // still released when the work settles.
    private void AbandonInBackground<T>(
        Task<T> task,
        Action<T>? onAbandonedResult,
        CancellationTokenSource linked,
        Action settle,
        Action releaseInFlight,
        long strandCharge,
        bool countAsStrand,
        bool cancelled)
    {
        if (countAsStrand)
        {
            Interlocked.Add(ref _strandedBytes, strandCharge);
            Interlocked.Increment(ref _detached);
            if (cancelled)
            {
                Interlocked.Increment(ref _cancelledDetached);
            }

            // A strand just appeared: reset the wedged-door activity clock (High #1) so the "no drain over N
            // minutes" window is measured from strand activity, not from door construction.
            Volatile.Write(ref _lastStrandActivityTimestamp, _clock.GetTimestamp());
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
                        if (cancelled)
                        {
                            Interlocked.Decrement(ref _cancelledDetached);
                        }

                        // A strand drained (High #1 wedged-door signal): record the activity so a door that
                        // KEEPS draining is never reported wedged. A never-terminating strand's continuation
                        // never runs, so a saturated door with no activity crosses the stall window and reports
                        // wedged.
                        Volatile.Write(ref _lastStrandActivityTimestamp, _clock.GetTimestamp());
                    }

                    // Release the in-flight admission slot (High #6) — the decode has finally stopped running.
                    releaseInFlight();

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
