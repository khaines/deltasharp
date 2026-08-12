using System.Diagnostics;
using System.Diagnostics.Metrics;
using DeltaSharp.Diagnostics;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;

namespace DeltaSharp.Storage.Diagnostics;

/// <summary>
/// The bounded terminal outcome of a Delta commit — the closed value set behind the shared
/// <see cref="DeltaSharpTelemetry.OutcomeKey"/> label so success, an idempotent skip, an OCC conflict,
/// contention exhaustion, an unresolved outcome, and a failure are never collapsed into one ambiguous
/// count (design §7.1/§7.3, checklist 09b). Each value maps to a low-cardinality string via
/// <see cref="DeltaStorageTelemetry"/>.
/// </summary>
internal enum CommitOutcome
{
    /// <summary>A new version was durably published (won the race, or an ambiguous/own-commit landed).</summary>
    Success,

    /// <summary>The batch was already committed (idempotent <c>txn</c> skip) — no new version written.</summary>
    Skipped,

    /// <summary>A concurrent commit logically conflicted; this writer aborted
    /// (<see cref="DeltaConcurrentModificationException"/>).</summary>
    Conflict,

    /// <summary>The rebase-retry budget was exhausted under sustained contention
    /// (<see cref="DeltaCommitContentionException"/>) — provably did not land, retryable.</summary>
    Contention,

    /// <summary>The outcome could not be resolved to committed-or-not
    /// (<see cref="DeltaCommitUnknownStateException"/>) — fail-closed, non-retryable.</summary>
    UnknownState,

    /// <summary>An atomic batch mixed committed and uncommitted application transactions
    /// (<see cref="PartialTransactionException"/>) — fail-closed caller misuse.</summary>
    PartialTransaction,

    /// <summary>An unexpected/unclassified failure (for example an unsupported writer protocol).</summary>
    Failure,

    /// <summary>The commit was cancelled via its <see cref="System.Threading.CancellationToken"/> before a
    /// terminal outcome was reached. A cancellation is <b>not</b> a commit failure (it does not inflate the
    /// failure SLI and does not mark the span <c>Error</c>).</summary>
    Cancelled,
}

/// <summary>
/// The reason a commit attempt was retried rather than terminating — the low-cardinality <c>reason</c> a
/// per-retry log line carries (design §7.2, checklist 09a: "retry logs include attempt number + retry
/// reason"). Not a metric label (retries are correlation/exemplar-only signals).
/// </summary>
internal enum CommitRetryReason
{
    /// <summary>A definite OCC conflict that was safely rebased onto the new latest version and retried.</summary>
    ConflictRebase,

    /// <summary>An ambiguous put-if-absent acknowledgment resolved to an unclaimed slot — the same version
    /// is retried.</summary>
    AmbiguousSlotFree,

    /// <summary>A transient storage failure was retried with bounded backoff (design §2.11.3).</summary>
    Transient,
}

/// <summary>
/// The bounded reason a classic checkpoint was discarded during snapshot reconstruction and its state was
/// NOT used to seed the snapshot — reconstruction falls back to an older checkpoint or full JSON replay
/// (design §2.10.3; #772). A closed value set, safe as the <see cref="DeltaStorageTelemetry.CheckpointFallbackReasonKey"/>
/// metric label; the discarded checkpoint's <b>version</b> is a correlation/exemplar-only signal that never
/// becomes a metric label (unbounded over a table's lifetime — checklist 09b), so it rides only the log line.
/// </summary>
internal enum CheckpointFallbackReason
{
    /// <summary>The checkpoint is a VALID Parquet file DeltaSharp cannot read (an unsupported feature such as
    /// Parquet Modular Encryption, #681/#698) — a <see cref="DeltaStorageException"/> with
    /// <see cref="StorageErrorKind.UnsupportedFeature"/> from the checkpoint reader.</summary>
    UnsupportedFeature,

    /// <summary>The checkpoint is malformed/corrupt (unreadable Parquet or a protocol-illegal action set) — a
    /// <see cref="DeltaProtocolException"/> raised while seeding.</summary>
    Malformed,

    /// <summary>The checkpoint carries more than one <c>metaData</c> action across its parts — the #671 cross-part
    /// identity forgery the single-<c>metaData</c> seed guard rejects. Distinguished from <see cref="Malformed"/>
    /// because the two are indistinguishable by exception introspection (both surface as a
    /// <see cref="DeltaProtocolException"/> with <see cref="DeltaProtocolErrorKind.MalformedAction"/>); the reason is
    /// therefore minted at the guard call site, not derived from the caught exception. A deliberate, individually
    /// actionable <b>security</b> signal (identity forgery), not routine bit-rot.</summary>
    ForgedMultiMetadata,

    /// <summary>The checkpoint's decode did not terminate within the wall-clock decode budget
    /// (<see cref="BoundedDecode"/>, #647/#699/#716): a crafted checkpoint drove Parquet.Net into effectively
    /// unbounded, cancellation-ignoring work. Distinguished from <see cref="Malformed"/> because a timeout is a
    /// resource/throttling fault, not proof the bytes are malformed — it is minted at the bounded-decode call
    /// site (surfaced as a <see cref="DeltaStorageException"/> with
    /// <see cref="StorageErrorKind.DecodeBudgetExceeded"/>), pairs with its own EventId, and seeds the
    /// checkpoint-layer negative cache so a known-timing-out checkpoint is not re-decoded on every load.</summary>
    DecodeTimeout,

    /// <summary>The checkpoint decode was rejected because the bounded-decode worker's checkpoint door was at
    /// capacity — too many untrusted decodes already stranded (<see cref="DecodeCapacityExhaustedException"/> →
    /// <see cref="StorageErrorKind.DecoderSaturated"/>). Distinguished from <see cref="DecodeTimeout"/> because
    /// the decode NEVER STARTED (a resource/saturation condition, transient), so — unlike a timeout — it is NOT
    /// negatively cached (the checkpoint may be perfectly healthy) and does NOT increment the decode-timeout
    /// counter. Reconstruction still degrades to JSON replay, and this reason plus the sibling
    /// <c>deltasharp.storage.decode.capacity_exhausted{door=checkpoint}</c> counter keep the saturation
    /// observable.</summary>
    DecoderSaturated,

    /// <summary>The checkpoint part was SKIPPED entirely because it is already in the process-wide negative
    /// cache (it provably tripped the bounded decode budget on a PRIOR load and is still within its — possibly
    /// backoff-extended — TTL). No decode ran on this load, so this is categorically distinct from
    /// <see cref="DecodeTimeout"/> (a decode ran past budget): it must NOT increment the
    /// <c>decode.budget_exceeded</c> counter (the PR's de-conflation fix), and instead pairs with the dedicated
    /// <c>deltasharp.storage.decode.negative_cache_skip{door=checkpoint}</c> counter so a persistently bad
    /// checkpoint stays observable without re-incurring the decode or inflating the timeout signal.</summary>
    NegativeCacheSkip,
}

/// <summary>
/// Which decode door tripped its wall-clock budget (design §5.4 C-DECODE): the general data-file
/// (<c>ParquetFileReader</c>) reader or the classic-checkpoint (<c>DeltaCheckpointReader</c>) reader. A closed
/// two-value set, safe as the <see cref="DeltaStorageTelemetry.DecodeDoorKey"/> metric label so an operator can
/// tell WHICH untrusted-decode surface is being driven non-terminating.
/// </summary>
internal enum DecodeDoor
{
    /// <summary>The general data-file Parquet reader (open + row-group decode).</summary>
    DataFile,

    /// <summary>The classic-checkpoint Parquet reader.</summary>
    Checkpoint,
}

/// <summary>
/// Which STAGE of a data-file read tripped its wall-clock decode budget (design §5.4 C-DECODE): the footer/
/// schema <see cref="Open"/>, a per-row-group column-page/level <see cref="RowGroup"/> decode, or the
/// row-count footer <see cref="Metadata"/> scan. A closed value set, safe as the
/// <see cref="DeltaStorageTelemetry.DecodeStageKey"/> metric label so the #647 non-terminating row-group
/// decode is distinguishable from a #699 non-terminating open (both otherwise land on
/// <c>door=data_file</c>). The checkpoint door decodes a whole part as one unit and uses <see cref="Whole"/>.
/// </summary>
internal enum DecodeStage
{
    /// <summary>The Parquet footer parse + schema materialization (open, #699).</summary>
    Open,

    /// <summary>A row group's column-page/level decode (#647).</summary>
    RowGroup,

    /// <summary>The row-count footer metadata scan (GetRowCountAsync).</summary>
    Metadata,

    /// <summary>The whole checkpoint part decode (a single unit; the checkpoint door).</summary>
    Whole,
}

/// <summary>
/// The bounded terminal outcome of a Delta VACUUM (design §2.14, STORY-05.6.2) behind the shared
/// <see cref="DeltaSharpTelemetry.OutcomeKey"/> label — so a dry-run, a real reclamation, a fail-closed
/// sub-threshold rejection, a cancellation, and an unexpected failure are never collapsed into one
/// ambiguous count (design §7.1/§7.3, checklist 09b). A closed value set, safe as a metric label.
/// </summary>
internal enum VacuumOutcome
{
    /// <summary>A dry-run listed the deletion-eligible paths without deleting anything (AC1).</summary>
    DryRun,

    /// <summary>A real VACUUM reclaimed the deletion-eligible files (idempotently, AC4).</summary>
    Completed,

    /// <summary>The requested retention was below the safety threshold and the unsafe override was not
    /// enabled, so VACUUM was rejected fail-closed before any selection (AC2).</summary>
    RejectedUnsafeRetention,

    /// <summary>VACUUM was cancelled via its <see cref="System.Threading.CancellationToken"/> before a
    /// terminal outcome. A cancellation is <b>not</b> a failure.</summary>
    Cancelled,

    /// <summary>An unexpected/unclassified failure (fail-closed; nothing protected is deleted).</summary>
    Failure,
}

/// <summary>
/// The bounded terminal outcome of a Delta OPTIMIZE / small-file compaction (design §2.11.7,
/// STORY-05.6.1 / #195) behind the shared <see cref="DeltaSharpTelemetry.OutcomeKey"/> label — so a dry-run
/// plan, a real compaction, a no-op (nothing to compact), a fail-closed abort (a concurrent change to an
/// input, or a pre-commit failure), a cancellation, and an unexpected failure are never collapsed into one
/// ambiguous count (design §7.1/§7.3, checklist 09b). A closed value set, safe as a metric label.
/// </summary>
internal enum OptimizeOutcome
{
    /// <summary>A dry-run reported the compaction plan without writing or committing anything.</summary>
    DryRun,

    /// <summary>A real OPTIMIZE published the single compaction commit (inputs removed, outputs added).</summary>
    Completed,

    /// <summary>Nothing was eligible to compact (no partition had more than one small file), so no commit
    /// was made — a benign no-op, distinct from a real completion.</summary>
    NoOp,

    /// <summary>OPTIMIZE aborted fail-closed before publishing because a concurrent commit changed one of
    /// the compaction inputs (an OCC conflict, <see cref="DeltaConcurrentModificationException"/>). The inputs
    /// stay active and any written outputs are ignorable orphans — the table is unchanged (AC4). A non-conflict
    /// pre-commit failure is reported as <see cref="Failure"/>, not this outcome.</summary>
    Aborted,

    /// <summary>OPTIMIZE was cancelled via its <see cref="System.Threading.CancellationToken"/> before a
    /// terminal outcome. A cancellation is <b>not</b> a failure.</summary>
    Cancelled,

    /// <summary>An unexpected/unclassified failure (fail-closed; the table is unchanged).</summary>
    Failure,
}

/// <summary>
/// The bounded terminal outcome of a Delta merge-on-read DELETE (STORY-05.5.1 / #192) behind the shared
/// <see cref="DeltaSharpTelemetry.OutcomeKey"/> label — so a no-op (predicate matched nothing), a real
/// deletion-vector commit, a fail-closed OCC abort (a concurrent DV update to the same file), a
/// cancellation, and an unexpected failure are never collapsed into one ambiguous count. A closed value
/// set, safe as a metric label.
/// </summary>
internal enum DeleteOutcome
{
    /// <summary>The delete predicate matched no rows, so no deletion vector was written and no commit was
    /// made — a benign no-op.</summary>
    NoOp,

    /// <summary>A real DELETE published the merge-on-read commit (deletion vectors written; add-with-DV +
    /// remove committed; no data file rewritten).</summary>
    Completed,

    /// <summary>DELETE aborted fail-closed because a concurrent commit changed one of the files it was
    /// deleting from (an OCC conflict). No delete was lost — the table is unchanged and the caller retries.</summary>
    Aborted,

    /// <summary>DELETE was cancelled via its <see cref="System.Threading.CancellationToken"/> before a
    /// terminal outcome. A cancellation is <b>not</b> a failure.</summary>
    Cancelled,

    /// <summary>An unexpected/unclassified failure (fail-closed; the table is unchanged).</summary>
    Failure,
}
/// candidate file was kept or is deletion-eligible. The <b>deletion</b> decision itself is always the
/// <see cref="DeltaSharp.Storage.Delta.OrphanCleanup.SelectDeletable"/> contract's output; the four kept
/// reasons annotate <i>why</i> the contract retained a file, for the audit trail. A closed, low-cardinality
/// set — safe as the <see cref="DeltaStorageTelemetry.VacuumDecisionKey"/> metric label (a candidate path is
/// never a metric tag).
/// </summary>
internal enum VacuumDecision
{
    /// <summary>Retention-expired and unreferenced: the contract selected it for deletion.</summary>
    Deletable,

    /// <summary>An active file in the current snapshot — never an orphan, always protected.</summary>
    Active,

    /// <summary>A tombstone removed within the retention window (or with an unknown deletion time, treated
    /// as <c>+∞</c>) — a stale reader pinned to an older snapshot may still read it.</summary>
    RetentionProtectedTombstone,

    /// <summary>Modified within the retention window (<c>mtime &gt;= cutoff</c>, inclusive) — it may belong
    /// to an in-flight commit, so it is protected against listing lag / a torn view.</summary>
    RecentlyStaged,

    /// <summary>Referenced by a <c>cdc</c> action in a retained, in-window commit JSON — a Change-Data-Feed
    /// <c>_change_data/</c> file that is not an active file (INV C1) but is protected while a commit within
    /// <c>delta.logRetentionDuration</c> still references it (#489).</summary>
    ReferencedChangeData,
}

/// <summary>
/// The commit-path telemetry surface for <c>DeltaSharp.Storage</c> — the two <see cref="Meter"/>s and two
/// <see cref="ActivitySource"/>s the storage layer owns (named <c>DeltaSharp.Storage</c> and
/// <c>DeltaSharp.Delta</c> per design §7.3/§7.4), plus the Delta <b>commit</b> instruments, built on the
/// shared <see cref="DeltaSharpTelemetry"/> vocabulary. It is <b>exporter-agnostic and a safe no-op until a
/// collector subscribes</b>: a <c>Counter.Add</c>/<c>Histogram.Record</c> with no <see cref="MeterListener"/>
/// and a <c>StartActivity</c> with no <see cref="ActivityListener"/> perform no work, mirroring the ambient
/// <c>ExecutionAudit</c> seam. Instruments are created <b>once</b> per instance and reused (checklist 09b);
/// durations are measured with the monotonic <see cref="Stopwatch"/>, never the wall clock (banned).
/// </summary>
/// <remarks>
/// <para>The process-wide production instance is <see cref="Shared"/>. Tests inject a private instance whose
/// distinct <see cref="Meter"/>/<see cref="ActivitySource"/> objects a scoped <see cref="MeterListener"/>/
/// <see cref="ActivityListener"/> can subscribe to in isolation, so parallel test classes never cross-
/// contaminate one another's measurements. The type is AOT/trim-clean: <see cref="Meter"/>,
/// <see cref="ActivitySource"/>, and the instrument types are compile-time and reflection-free.</para>
/// <para>#479 instruments only the commit path, so only the <c>DeltaSharp.Delta</c> meter/source carry
/// instruments today; the <c>DeltaSharp.Storage</c> meter/source are created so the surface is complete and
/// adapter-I/O instruments (design §7.3) can attach without re-establishing the naming.</para>
/// </remarks>
internal sealed class DeltaStorageTelemetry : IDisposable
{
    /// <summary>The <c>DeltaSharp.Delta</c> meter/source name (log/commit/snapshot/maintenance).</summary>
    internal const string DeltaName = DeltaSharpTelemetry.RootName + ".Delta";

    /// <summary>The <c>DeltaSharp.Storage</c> meter/source name (adapter + Parquet I/O).</summary>
    internal const string StorageName = DeltaSharpTelemetry.RootName + ".Storage";

    /// <summary>The bounded <c>deltasharp.backend</c> attribute key — re-exported from the shared vocabulary
    /// (<see cref="DeltaSharpTelemetry.BackendKey"/>) so the storage layer and its tests bind one source of
    /// truth (design §7.3 minted label).</summary>
    internal const string BackendKey = DeltaSharpTelemetry.BackendKey;

    /// <summary>The bounded <c>deltasharp.conflict.class</c> label key sub-classifying an
    /// <c>outcome=conflict</c> — re-exported from the shared vocabulary
    /// (<see cref="DeltaSharpTelemetry.ConflictClassKey"/>) so the registry stays the single source of truth
    /// (design §7.3 minted label).</summary>
    internal const string ConflictClassKey = DeltaSharpTelemetry.ConflictClassKey;

    /// <summary>The stable commit span name (design §7.4): variables live in bounded attributes.</summary>
    internal const string CommitActivityName = DeltaName + ".Commit";

    /// <summary>The bounded <c>deltasharp.operation=commit</c> value for the commit path.</summary>
    internal const string CommitOperation = "commit";

    /// <summary>The bounded <c>deltasharp.operation=reconstruct</c> value for the snapshot-reconstruction
    /// path (checkpoint seeding + JSON replay). Used as the <c>ILogger.BeginScope</c> correlation dimension
    /// on the checkpoint-fallback log line (#772).</summary>
    internal const string ReconstructOperation = "reconstruct";

    /// <summary>The bounded <c>deltasharp.component=delta</c> value for the Delta log subsystem.</summary>
    internal const string DeltaComponent = "delta";

    /// <summary>The bounded <c>conflict.class</c> for a concurrent write this writer safely rebased past
    /// (no abort). Part of the design's closed <c>conflict.class</c> value set.</summary>
    internal const string ConcurrentWriteClass = "concurrent_write";

    /// <summary>The bounded <c>deltasharp.operation=vacuum</c> value for the VACUUM maintenance path.</summary>
    internal const string VacuumOperation = "vacuum";

    /// <summary>The bounded <c>deltasharp.operation=optimize</c> value for the OPTIMIZE maintenance path.</summary>
    internal const string OptimizeOperation = "optimize";

    /// <summary>The bounded <c>deltasharp.operation=delete</c> value for the merge-on-read DELETE path.</summary>
    internal const string DeleteOperation = "delete";

    /// <summary>The stable DELETE span name (design §7.4): variables live in bounded attributes.</summary>
    internal const string DeleteActivityName = DeltaName + ".Delete";

    /// <summary>The stable OPTIMIZE span name (design §7.4): variables live in bounded attributes.</summary>
    internal const string OptimizeActivityName = DeltaName + ".Optimize";

    /// <summary>The stable VACUUM span name (design §7.4): variables live in bounded attributes.</summary>
    internal const string VacuumActivityName = DeltaName + ".Vacuum";

    /// <summary>The bounded <c>deltasharp.vacuum.decision</c> metric label key sub-classifying a VACUUM
    /// candidate file count by its retention decision (<c>deletable</c>, <c>active</c>,
    /// <c>retention_protected_tombstone</c>, <c>recently_staged</c>). A closed value set (metric-label-safe);
    /// the candidate <i>path</i> is never a metric tag (unbounded — it lives only on the audit log).</summary>
    internal const string VacuumDecisionKey = "deltasharp.vacuum.decision";

    /// <summary>The bounded <c>deltasharp.checkpoint.fallback.reason</c> metric label key sub-classifying a
    /// discarded checkpoint by why reconstruction fell back to JSON replay (<c>unsupported_feature</c>,
    /// <c>malformed</c>, <c>forged_multi_metadata</c>). A closed value set (metric-label-safe); the discarded
    /// checkpoint <i>version</i> is never a metric tag (unbounded over a table's lifetime — it lives only on the
    /// fallback log line).</summary>
    internal const string CheckpointFallbackReasonKey = "deltasharp.checkpoint.fallback.reason";

    /// <summary>The bounded <c>deltasharp.decode.door</c> metric label key sub-classifying a decode-budget
    /// timeout by which untrusted-decode surface tripped it (<c>data_file</c> / <c>checkpoint</c>). A closed
    /// value set (metric-label-safe); no untrusted byte content or path ever becomes a tag.</summary>
    internal const string DecodeDoorKey = "deltasharp.decode.door";

    /// <summary>The bounded <c>deltasharp.decode.stage</c> metric label key sub-classifying a data-file
    /// decode-budget timeout by WHICH stage of the read tripped it (<c>open</c> = the footer/schema open,
    /// <c>row_group</c> = a column-page/level decode of a row group, <c>metadata</c> = the row-count footer
    /// scan). A closed value set (metric-label-safe): it lets an operator (and the #647 row-group test)
    /// distinguish a non-terminating page decode from a non-terminating open, which both land on
    /// <c>door=data_file</c> otherwise. The checkpoint door is a single decode unit and uses
    /// <c>whole</c>.</summary>
    internal const string DecodeStageKey = "deltasharp.decode.stage";

    private static readonly string AssemblyVersion =
        typeof(DeltaStorageTelemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>The process-wide production surface (no-op until an exporter subscribes).</summary>
    internal static DeltaStorageTelemetry Shared { get; } = new();

    private readonly Meter _deltaMeter;
    private readonly Meter _storageMeter;
    private readonly Histogram<double> _commitDuration;
    private readonly Counter<long> _commitCount;
    private readonly Histogram<int> _commitAttempts;
    private readonly Counter<long> _commitConflicts;
    private readonly Counter<long> _commitTransientRetries;
    private readonly Histogram<double> _vacuumDuration;
    private readonly Counter<long> _vacuumCount;
    private readonly Counter<long> _vacuumFiles;
    private readonly Histogram<double> _optimizeDuration;
    private readonly Counter<long> _optimizeCount;
    private readonly Counter<long> _optimizeFilesRemoved;
    private readonly Counter<long> _optimizeFilesAdded;
    private readonly Counter<long> _optimizeBytes;
    private readonly Histogram<double> _deleteDuration;
    private readonly Counter<long> _deleteCount;
    private readonly Counter<long> _deleteFiles;
    private readonly Counter<long> _deleteRows;
    private readonly Counter<long> _checkpointFallback;
    private readonly Counter<long> _decodeBudgetExceeded;
    private readonly Counter<long> _decodeCapacityExhausted;
    private readonly Counter<long> _decodeNegativeCacheSkip;
#pragma warning disable IDE0052 // Held so the ObservableGauge callback stays subscribed for the meter's lifetime.
    private readonly ObservableGauge<int> _decodeDetached;
#pragma warning restore IDE0052

    internal DeltaStorageTelemetry()
    {
        _deltaMeter = new Meter(DeltaName, AssemblyVersion);
        _storageMeter = new Meter(StorageName, AssemblyVersion);
        DeltaActivitySource = new ActivitySource(DeltaName, AssemblyVersion);
        StorageActivitySource = new ActivitySource(StorageName, AssemblyVersion);

        _commitDuration = _deltaMeter.CreateHistogram<double>(
            "deltasharp.delta.commit.duration", unit: "s",
            description: "Elapsed (monotonic) duration of a Delta commit, by terminal outcome.");
        _commitCount = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.commit.count", unit: "{commit}",
            description: "Terminal Delta commit outcomes (committed-version and failure-classification count).");
        _commitAttempts = _deltaMeter.CreateHistogram<int>(
            "deltasharp.delta.commit.attempts", unit: "{attempt}",
            description: "Optimistic-concurrency put-if-absent attempts per commit (retry depth), by outcome.");
        _commitConflicts = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.commit.conflicts", unit: "{conflict}",
            description: "Detected commit conflicts (aborted or safely rebased), by conflict class.");
        _commitTransientRetries = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.commit.transient_retries", unit: "{retry}",
            description: "Transient storage-failure retries within a commit's put-if-absent attempts (degradation signal).");
        _vacuumDuration = _deltaMeter.CreateHistogram<double>(
            "deltasharp.delta.vacuum.duration", unit: "s",
            description: "Elapsed (monotonic) duration of a Delta VACUUM, by terminal outcome.");
        _vacuumCount = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.vacuum.count", unit: "{vacuum}",
            description: "Terminal Delta VACUUM outcomes (dry-run, completed, rejected, cancelled, failure).");
        _vacuumFiles = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.vacuum.files", unit: "{file}",
            description: "VACUUM candidate files by retention decision (deletable / active / retention-protected / recently-staged).");
        _optimizeDuration = _deltaMeter.CreateHistogram<double>(
            "deltasharp.delta.optimize.duration", unit: "s",
            description: "Elapsed (monotonic) duration of a Delta OPTIMIZE, by terminal outcome.");
        _optimizeCount = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.optimize.count", unit: "{optimize}",
            description: "Terminal Delta OPTIMIZE outcomes (dry-run, completed, no-op, aborted, cancelled, failure).");
        _optimizeFilesRemoved = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.optimize.files_removed", unit: "{file}",
            description: "Small input files compacted away by OPTIMIZE (planned counts under the dry_run outcome), by outcome.");
        _optimizeFilesAdded = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.optimize.files_added", unit: "{file}",
            description: "Compacted output files added by OPTIMIZE (planned counts under the dry_run outcome), by outcome.");
        _optimizeBytes = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.optimize.bytes", unit: "By",
            description: "Total input bytes rewritten by OPTIMIZE (planned counts under the dry_run outcome), by outcome.");
        _deleteDuration = _deltaMeter.CreateHistogram<double>(
            "deltasharp.delta.delete.duration", unit: "s",
            description: "Elapsed (monotonic) duration of a Delta merge-on-read DELETE, by terminal outcome.");
        _deleteCount = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.delete.count", unit: "{delete}",
            description: "Terminal Delta DELETE outcomes (no-op, completed, aborted, cancelled, failure).");
        _deleteFiles = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.delete.files", unit: "{file}",
            description: "Files given a new/updated deletion vector by DELETE (no data file rewritten), by outcome.");
        _deleteRows = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.delete.rows", unit: "{row}",
            description: "Rows logically deleted by DELETE via deletion vectors, by outcome.");
        _checkpointFallback = _deltaMeter.CreateCounter<long>(
            "deltasharp.delta.checkpoint.fallbacks", unit: "{fallback}",
            description: "Selected classic checkpoints discarded while SEEDING a snapshot (a post-selection decode failure — unsupported feature or malformed; state not seeded, reconstruction falls back to an older checkpoint or full JSON replay), by reason. Selection-time skips (incomplete multi-part groups, V2/UUID checkpoints, a failed _last_checkpoint hint) are not counted.");
        _decodeBudgetExceeded = _storageMeter.CreateCounter<long>(
            "deltasharp.storage.decode.budget_exceeded", unit: "{timeout}",
            description: "Untrusted Parquet decodes that did NOT terminate within their wall-clock decode budget and were failed closed (a crafted byte driving a non-terminating decode — #647/#699/#716), by door (data_file / checkpoint). The rate instrument to alert on for a decode-DoS attempt.");
        _decodeCapacityExhausted = _storageMeter.CreateCounter<long>(
            "deltasharp.storage.decode.capacity_exhausted", unit: "{rejection}",
            description: "Untrusted Parquet decodes REJECTED fail-fast because the bounded-decode worker's door was at capacity (too many decodes already stranded past their deadline), by door (data_file / checkpoint). A retryable saturation signal — DISTINCT from decode.budget_exceeded (this decode never started), so it is never conflated with a non-terminating decode or negatively cached.");
        _decodeNegativeCacheSkip = _storageMeter.CreateCounter<long>(
            "deltasharp.storage.decode.negative_cache_skip", unit: "{skip}",
            description: "Checkpoint decodes SKIPPED because the part is already in the process-wide negative cache (it provably tripped its decode budget on a prior load and is still within its TTL), by door. DISTINCT from decode.budget_exceeded (no decode ran on this load) so a persistently bad checkpoint stays observable without inflating the decode-timeout signal.");
        _decodeDetached = _storageMeter.CreateObservableGauge(
            "deltasharp.storage.decode.detached",
            static () => new[]
            {
                new Measurement<int>(
                    BoundedDecode.DataFileDetachedDecodeCount,
                    new KeyValuePair<string, object?>(DecodeDoorKey, ToLabel(DecodeDoor.DataFile))),
                new Measurement<int>(
                    BoundedDecode.CheckpointDetachedDecodeCount,
                    new KeyValuePair<string, object?>(DecodeDoorKey, ToLabel(DecodeDoor.Checkpoint))),
            },
            unit: "{strand}",
            description: "Currently-detached (running-past-deadline, un-reclaimable) untrusted Parquet decode strands per door — the bounded residual of the wall-clock decode ceiling (.NET cannot abort a thread). Rises toward the per-door cap under a distinct-crafted-input flood; a sustained non-zero value is the DoS-pressure gauge.");
    }

    /// <summary>The <c>DeltaSharp.Delta</c> meter (commit instruments). Exposed for reference-identity
    /// filtering by a per-instance <see cref="MeterListener"/> in tests, so parallel test classes that each
    /// build their own surface never observe one another's measurements despite the shared meter name.</summary>
    internal Meter DeltaMeter => _deltaMeter;

    /// <summary>The <c>DeltaSharp.Storage</c> meter (adapter/Parquet I/O instruments; none on the commit
    /// path today).</summary>
    internal Meter StorageMeter => _storageMeter;

    /// <summary>The <c>DeltaSharp.Delta</c> trace source (log/commit/snapshot spans).</summary>
    internal ActivitySource DeltaActivitySource { get; }

    /// <summary>The <c>DeltaSharp.Storage</c> trace source (adapter/Parquet I/O spans; unused on the commit
    /// path today, created so the surface is complete).</summary>
    internal ActivitySource StorageActivitySource { get; }

    /// <summary>Starts the commit span if a listener samples the Delta source; returns <see langword="null"/>
    /// (a cheap no-op) otherwise. The caller sets low-cardinality attributes and status.</summary>
    internal Activity? StartCommitActivity(StorageBackendKind backend)
    {
        Activity? activity = DeltaActivitySource.StartActivity(CommitActivityName, ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(DeltaSharpTelemetry.ComponentKey, DeltaComponent);
            activity.SetTag(DeltaSharpTelemetry.OperationKey, CommitOperation);
            activity.SetTag(BackendKey, backend.ToLabel());
        }

        return activity;
    }

    /// <summary>Records the terminal signals for one commit: the duration histogram, the outcome counter,
    /// and the attempt-depth histogram — all tagged with the bounded <see cref="DeltaSharpTelemetry.OutcomeKey"/>.
    /// A single measurement per commit (never per row/attempt), so it is allocation-light and export-safe.</summary>
    internal void RecordCommitTerminal(CommitOutcome outcome, double durationSeconds, int attempts)
    {
        var outcomeTag = new KeyValuePair<string, object?>(DeltaSharpTelemetry.OutcomeKey, ToLabel(outcome));
        _commitDuration.Record(durationSeconds, outcomeTag);
        _commitCount.Add(1, outcomeTag);
        _commitAttempts.Record(attempts, outcomeTag);
    }

    /// <summary>Increments the conflict counter for one detected conflict, tagged with the bounded
    /// <see cref="ConflictClassKey"/>. The input is a bounded <see cref="DeltaConflictKind"/> (never a raw
    /// string, so no unbounded/free-text value can reach the metric tag): an aborted conflict passes the
    /// winner-driven <c>ex.Kind</c>; a safely rebased conflict passes <see langword="null"/>, which maps to
    /// <see cref="ConcurrentWriteClass"/>.</summary>
    internal void RecordConflict(DeltaConflictKind? kind)
    {
        string conflictClass = kind is { } value ? ToConflictClass(value) : ConcurrentWriteClass;
        _commitConflicts.Add(1, new KeyValuePair<string, object?>(ConflictClassKey, conflictClass));
    }

    /// <summary>Increments the transient-retry counter for one bounded transient storage-failure retry
    /// within a commit's put-if-absent attempts (design §2.11.3). A clean commit records zero; a
    /// transient-degraded-but-successful commit records &gt;0, so an SRE can distinguish the two even though
    /// both terminate as <c>outcome=success</c> with <c>attempts=1</c>.</summary>
    internal void RecordTransientRetry() => _commitTransientRetries.Add(1);

    /// <summary>Starts the VACUUM span if a listener samples the Delta source; returns <see langword="null"/>
    /// (a cheap no-op) otherwise. The caller sets low-cardinality attributes and status.</summary>
    internal Activity? StartVacuumActivity(StorageBackendKind backend)
    {
        Activity? activity = DeltaActivitySource.StartActivity(VacuumActivityName, ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(DeltaSharpTelemetry.ComponentKey, DeltaComponent);
            activity.SetTag(DeltaSharpTelemetry.OperationKey, VacuumOperation);
            activity.SetTag(BackendKey, backend.ToLabel());
        }

        return activity;
    }

    /// <summary>Records the terminal signals for one VACUUM: the duration histogram and the outcome counter,
    /// tagged with the bounded <see cref="DeltaSharpTelemetry.OutcomeKey"/>. A single measurement per VACUUM
    /// (never per candidate), so it is allocation-light and export-safe.</summary>
    internal void RecordVacuumTerminal(VacuumOutcome outcome, double durationSeconds)
    {
        var outcomeTag = new KeyValuePair<string, object?>(DeltaSharpTelemetry.OutcomeKey, ToLabel(outcome));
        _vacuumDuration.Record(durationSeconds, outcomeTag);
        _vacuumCount.Add(1, outcomeTag);
    }

    /// <summary>Adds <paramref name="count"/> VACUUM candidate files to the per-decision counter, tagged
    /// with the bounded <see cref="VacuumDecisionKey"/>. One measurement per decision bucket (never per
    /// file), so an SRE can chart the deletion-eligible vs. protected split without any unbounded path tag.</summary>
    internal void RecordVacuumFiles(VacuumDecision decision, long count)
    {
        if (count <= 0)
        {
            return;
        }

        _vacuumFiles.Add(count, new KeyValuePair<string, object?>(VacuumDecisionKey, ToLabel(decision)));
    }

    /// <summary>Starts the OPTIMIZE span if a listener samples the Delta source; returns
    /// <see langword="null"/> (a cheap no-op) otherwise. The caller sets low-cardinality attributes and
    /// status — never row data, column values, or a file path (checklist 09b redaction-by-omission).</summary>
    internal Activity? StartOptimizeActivity(StorageBackendKind backend)
    {
        Activity? activity = DeltaActivitySource.StartActivity(OptimizeActivityName, ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(DeltaSharpTelemetry.ComponentKey, DeltaComponent);
            activity.SetTag(DeltaSharpTelemetry.OperationKey, OptimizeOperation);
            activity.SetTag(BackendKey, backend.ToLabel());
        }

        return activity;
    }

    /// <summary>Records the terminal signals for one OPTIMIZE: the duration histogram and the outcome
    /// counter, plus the files-removed / files-added / input-bytes counters, all tagged with the bounded
    /// <see cref="DeltaSharpTelemetry.OutcomeKey"/>. A single measurement per OPTIMIZE (never per row, batch,
    /// or file), so it is allocation-light and export-safe; no raw row data or column value is ever
    /// emitted.</summary>
    internal void RecordOptimizeTerminal(
        OptimizeOutcome outcome, double durationSeconds, int filesRemoved, int filesAdded, long bytesCompacted)
    {
        var outcomeTag = new KeyValuePair<string, object?>(DeltaSharpTelemetry.OutcomeKey, ToLabel(outcome));
        _optimizeDuration.Record(durationSeconds, outcomeTag);
        _optimizeCount.Add(1, outcomeTag);
        if (filesRemoved > 0)
        {
            _optimizeFilesRemoved.Add(filesRemoved, outcomeTag);
        }

        if (filesAdded > 0)
        {
            _optimizeFilesAdded.Add(filesAdded, outcomeTag);
        }

        if (bytesCompacted > 0)
        {
            _optimizeBytes.Add(bytesCompacted, outcomeTag);
        }
    }

    /// <summary>Starts the DELETE span if a listener samples the Delta source; returns
    /// <see langword="null"/> (a cheap no-op) otherwise. The caller sets low-cardinality attributes and
    /// status — never row data, a predicate value, or a file path (redaction-by-omission).</summary>
    internal Activity? StartDeleteActivity(StorageBackendKind backend)
    {
        Activity? activity = DeltaActivitySource.StartActivity(DeleteActivityName, ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(DeltaSharpTelemetry.ComponentKey, DeltaComponent);
            activity.SetTag(DeltaSharpTelemetry.OperationKey, DeleteOperation);
            activity.SetTag(BackendKey, backend.ToLabel());
        }

        return activity;
    }

    /// <summary>Records the terminal signals for one DELETE: the duration histogram, the outcome counter,
    /// and the files-with-DV / rows-deleted counters, all tagged with the bounded
    /// <see cref="DeltaSharpTelemetry.OutcomeKey"/>. A single measurement per DELETE (never per row/file), so
    /// it is allocation-light and export-safe; no raw row data or column value is ever emitted.</summary>
    internal void RecordDeleteTerminal(
        DeleteOutcome outcome, double durationSeconds, int filesWithDeletionVector, long rowsDeleted)
    {
        var outcomeTag = new KeyValuePair<string, object?>(DeltaSharpTelemetry.OutcomeKey, ToLabel(outcome));
        _deleteDuration.Record(durationSeconds, outcomeTag);
        _deleteCount.Add(1, outcomeTag);
        if (filesWithDeletionVector > 0)
        {
            _deleteFiles.Add(filesWithDeletionVector, outcomeTag);
        }

        if (rowsDeleted > 0)
        {
            _deleteRows.Add(rowsDeleted, outcomeTag);
        }
    }

    /// <summary>Increments the checkpoint-fallback counter for one classic checkpoint that was discarded
    /// during reconstruction (its state did not seed the snapshot; reconstruction falls back to an older
    /// checkpoint or full JSON replay — design §2.10.3, #772), tagged with the bounded
    /// <see cref="CheckpointFallbackReasonKey"/>. The input is a bounded <see cref="CheckpointFallbackReason"/>
    /// (never a raw string, so no unbounded/free-text value can reach the metric tag); the discarded
    /// checkpoint's version is deliberately NOT a tag (correlation/exemplar-only) and rides the log line.</summary>
    internal void RecordCheckpointFallback(CheckpointFallbackReason reason) =>
        _checkpointFallback.Add(1, new KeyValuePair<string, object?>(CheckpointFallbackReasonKey, ToLabel(reason)));

    /// <summary>Increments the decode-budget-exceeded counter for one untrusted decode that did not terminate
    /// within its wall-clock budget and was failed closed (design §5.4 C-DECODE; #647/#699/#716), tagged with
    /// the bounded <see cref="DecodeDoorKey"/> door dimension and the bounded <see cref="DecodeStageKey"/>
    /// stage dimension (so a non-terminating row-group page decode is distinguishable from a non-terminating
    /// open, #647). The inputs are bounded enums (never a raw string), and no untrusted byte content, path, or
    /// checkpoint version ever becomes a tag — this is the rate instrument an operator alerts on for a
    /// decode-DoS attempt.</summary>
    internal void RecordDecodeBudgetExceeded(DecodeDoor door, DecodeStage stage) =>
        _decodeBudgetExceeded.Add(
            1,
            new KeyValuePair<string, object?>(DecodeDoorKey, ToLabel(door)),
            new KeyValuePair<string, object?>(DecodeStageKey, ToLabel(stage)));

    /// <summary>Increments the decode-capacity-exhausted counter for one untrusted decode that was REJECTED
    /// fail-fast because the bounded-decode worker's door was already at its strand cap (design §5.4 C-DECODE),
    /// tagged with the bounded <see cref="DecodeDoorKey"/> door dimension. DISTINCT from
    /// <see cref="RecordDecodeBudgetExceeded"/>: the decode never started, so this must not increment the
    /// decode-timeout counter (metric-conflation fix). The input is a bounded <see cref="DecodeDoor"/>; no
    /// untrusted content ever becomes a tag.</summary>
    internal void RecordDecodeCapacityExhausted(DecodeDoor door) =>
        _decodeCapacityExhausted.Add(1, new KeyValuePair<string, object?>(DecodeDoorKey, ToLabel(door)));

    /// <summary>Increments the negative-cache-skip counter for one checkpoint part that was SKIPPED (not
    /// re-decoded) because it is already in the process-wide negative cache, tagged with the bounded
    /// <see cref="DecodeDoorKey"/> door dimension. DISTINCT from <see cref="RecordDecodeBudgetExceeded"/>: no
    /// decode ran on this load, so this must NOT increment the decode-timeout counter (the de-conflation fix).
    /// The input is a bounded <see cref="DecodeDoor"/>; no untrusted content ever becomes a tag.</summary>
    internal void RecordDecodeNegativeCacheSkip(DecodeDoor door) =>
        _decodeNegativeCacheSkip.Add(1, new KeyValuePair<string, object?>(DecodeDoorKey, ToLabel(door)));

    /// <summary>The bounded <c>deltasharp.outcome</c> string for an <see cref="OptimizeOutcome"/>.</summary>
    internal static string ToLabel(OptimizeOutcome outcome) => outcome switch
    {
        OptimizeOutcome.DryRun => "dry_run",
        OptimizeOutcome.Completed => "completed",
        OptimizeOutcome.NoOp => "no_op",
        OptimizeOutcome.Aborted => "aborted",
        OptimizeOutcome.Cancelled => "cancelled",
        _ => "failure",
    };

    /// <summary>The bounded <c>deltasharp.outcome</c> string for a <see cref="DeleteOutcome"/>.</summary>
    internal static string ToLabel(DeleteOutcome outcome) => outcome switch
    {
        DeleteOutcome.NoOp => "no_op",
        DeleteOutcome.Completed => "completed",
        DeleteOutcome.Aborted => "aborted",
        DeleteOutcome.Cancelled => "cancelled",
        _ => "failure",
    };

    /// <summary>The bounded <c>deltasharp.outcome</c> string for a <see cref="VacuumOutcome"/>.</summary>
    internal static string ToLabel(VacuumOutcome outcome) => outcome switch
    {
        VacuumOutcome.DryRun => "dry_run",
        VacuumOutcome.Completed => "completed",
        VacuumOutcome.RejectedUnsafeRetention => "rejected_unsafe_retention",
        VacuumOutcome.Cancelled => "cancelled",
        _ => "failure",
    };

    /// <summary>The bounded <c>deltasharp.vacuum.decision</c> string for a <see cref="VacuumDecision"/>.</summary>
    internal static string ToLabel(VacuumDecision decision) => decision switch
    {
        VacuumDecision.Deletable => "deletable",
        VacuumDecision.Active => "active",
        VacuumDecision.RetentionProtectedTombstone => "retention_protected_tombstone",
        VacuumDecision.ReferencedChangeData => "referenced_change_data",
        _ => "recently_staged",
    };

    /// <summary>The bounded <c>deltasharp.outcome</c> string for a <see cref="CommitOutcome"/>.</summary>
    internal static string ToLabel(CommitOutcome outcome) => outcome switch
    {
        CommitOutcome.Success => "success",
        CommitOutcome.Skipped => "skipped",
        CommitOutcome.Conflict => "conflict",
        CommitOutcome.Contention => "contention",
        CommitOutcome.UnknownState => "unknown_state",
        CommitOutcome.PartialTransaction => "partial_transaction",
        CommitOutcome.Cancelled => "cancelled",
        _ => "failure",
    };

    /// <summary>The bounded <c>deltasharp.conflict.class</c> string for a <see cref="DeltaConflictKind"/>
    /// (design §7.3 value set). A safe rebase does not throw and instead records
    /// <see cref="ConcurrentWriteClass"/>.</summary>
    internal static string ToConflictClass(DeltaConflictKind kind) => kind switch
    {
        DeltaConflictKind.ConcurrentAppend => "concurrent_append",
        DeltaConflictKind.ConcurrentDeleteRead => "concurrent_delete_read",
        DeltaConflictKind.MetadataChanged => "metadata_changed",
        DeltaConflictKind.ProtocolChanged => "protocol_changed",
        DeltaConflictKind.ConcurrentTransaction => "concurrent_transaction",
        _ => ConcurrentWriteClass,
    };

    /// <summary>The bounded retry <c>reason</c> string for a per-retry log line.</summary>
    internal static string ToLabel(CommitRetryReason reason) => reason switch
    {
        CommitRetryReason.ConflictRebase => "conflict_rebase",
        CommitRetryReason.AmbiguousSlotFree => "ambiguous_slot_free",
        _ => "transient",
    };

    /// <summary>The bounded <c>deltasharp.checkpoint.fallback.reason</c> string for a
    /// <see cref="CheckpointFallbackReason"/> (design §7.3 closed value set). An unrecognized value maps to
    /// <c>malformed</c> — the fail-safe generic reason — so no free-text value can reach the metric tag.</summary>
    internal static string ToLabel(CheckpointFallbackReason reason) => reason switch
    {
        CheckpointFallbackReason.UnsupportedFeature => "unsupported_feature",
        CheckpointFallbackReason.Malformed => "malformed",
        CheckpointFallbackReason.ForgedMultiMetadata => "forged_multi_metadata",
        CheckpointFallbackReason.DecodeTimeout => "decode_timeout",
        CheckpointFallbackReason.DecoderSaturated => "decoder_saturated",
        CheckpointFallbackReason.NegativeCacheSkip => "negative_cache_skip",
        _ => "malformed",
    };

    /// <summary>The bounded <c>deltasharp.decode.door</c> string for a <see cref="DecodeDoor"/> (design §7.3
    /// closed value set). An unrecognized value maps to <c>data_file</c> — the fail-safe generic door — so no
    /// free-text value can reach the metric tag.</summary>
    internal static string ToLabel(DecodeDoor door) => door switch
    {
        DecodeDoor.DataFile => "data_file",
        DecodeDoor.Checkpoint => "checkpoint",
        _ => "data_file",
    };

    /// <summary>The bounded <c>deltasharp.decode.stage</c> string for a <see cref="DecodeStage"/> (design §7.3
    /// closed value set). An unrecognized value maps to <c>row_group</c> — the fail-safe generic stage — so no
    /// free-text value can reach the metric tag.</summary>
    internal static string ToLabel(DecodeStage stage) => stage switch
    {
        DecodeStage.Open => "open",
        DecodeStage.RowGroup => "row_group",
        DecodeStage.Metadata => "metadata",
        DecodeStage.Whole => "whole",
        _ => "row_group",
    };

    public void Dispose()
    {
        _deltaMeter.Dispose();
        _storageMeter.Dispose();
        DeltaActivitySource.Dispose();
        StorageActivitySource.Dispose();
    }
}
