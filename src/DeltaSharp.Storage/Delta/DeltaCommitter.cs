using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using DeltaSharp.Diagnostics;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Diagnostics;
using DeltaSharp.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeltaSharp.Storage.Delta;

/// <summary>The outcome of a successful commit: the <see cref="Version"/> that became visible and the
/// number of put-if-absent <see cref="Attempts"/> it took (1 ⇒ won on the first try; &gt;1 ⇒ the writer
/// observed a retryable conflict, rebased onto a newer version, and retried). <see cref="Skipped"/> is
/// <see langword="true"/> when the commit was <b>idempotently skipped</b> because its application
/// transaction was already committed (§2.11.4) — no new version was written, and <see cref="Version"/> is
/// a version at/after the prior commit.</summary>
internal readonly record struct DeltaCommitResult(long Version, int Attempts, bool Skipped = false);

/// <summary>
/// Publishes a Delta commit with optimistic concurrency (design §2.11): it conditionally creates
/// <c>_delta_log/&lt;N&gt;.json</c> at <c>N = readSnapshot.version + 1</c> via the atomic single-winner
/// <see cref="IStorageBackend.PutIfAbsentAsync"/>, and on a lost race reads the winning commits, runs
/// <see cref="DeltaConflictChecker"/>, and either <b>rebases</b> onto the new latest version and retries or
/// <b>aborts</b> with a Delta-parity conflict exception. An ambiguous put acknowledgement is re-resolved
/// idempotently by re-reading <c>&lt;N&gt;.json</c> and matching this attempt's embedded commit nonce, so
/// an ack lost after a durable write never double-commits (§2.11.3/§2.11.6, STORY-05.3.1 AC4).
///
/// <para>Exactly one writer wins each version; a blind append rebases past concurrent appends without
/// duplicating data (the same nonce-stable bytes are re-published at the new version). The engine never
/// blindly retries an ambiguous slot and never advances as if a commit definitely failed — either could
/// double-commit — and fails closed with <see cref="DeltaCommitUnknownStateException"/> when an outcome
/// cannot be resolved. A transient storage failure is retried with bounded backoff (§2.11.3).</para>
///
/// <para><b>Reentrancy.</b> A <see cref="DeltaCommitter"/> holds only immutable configuration; all
/// per-commit state is local to <see cref="CommitAsync"/>. A single instance is therefore safe to share
/// across concurrent <see cref="CommitAsync"/> calls (the concurrency tests use one instance per writer
/// only to model independent writers). The <see cref="BeforePutProbe"/> seam is test-only.</para>
///
/// <para><b>Consistency assumption.</b> Recovery from a lost/ambiguous race depends on the backend being
/// <b>read-after-write consistent</b> for a commit object versus a failed/ambiguous
/// <see cref="IStorageBackend.PutIfAbsentAsync"/> on the same key: a re-GET must observe a just-durable
/// commit. Under that contract no double-commit is possible (a durable-but-unacknowledged commit is
/// recognized by its nonce on both the ambiguous and the definite-conflict paths). The idempotency nonce is
/// <b>in-memory, per <see cref="CommitAsync"/> call</b>: it makes a commit exactly-once <i>within</i> one
/// call (including its retries), <b>not</b> across a driver restart mid-commit — cross-process idempotency
/// is provided by <c>txn{appId,version}</c> (design §2.11.4, STORY-05.3.2 / #187).</para>
/// </summary>
internal sealed class DeltaCommitter
{
    /// <summary>The <c>commitInfo</c> key that carries this attempt's idempotency nonce (Delta's
    /// <c>txnId</c>) so an ambiguous-ack re-GET can recognize this writer's own commit.</summary>
    internal const string CommitNonceKey = "txnId";

    /// <summary>The <c>commitInfo.engineInfo</c> string stamped on every commit (Delta parity): the engine
    /// name and version. Derived once from the assembly informational version (the build-time
    /// <c>VersionPrefix</c>) with any <c>+&lt;metadata&gt;</c> suffix stripped, so it is deterministic within
    /// a build and never per-run random.</summary>
    internal static readonly string EngineInfo = BuildEngineInfo();

    /// <summary>A generous bound on rebase-retries; reaching it implies sustained contention (or a bug) and
    /// fails closed with <see cref="DeltaCommitContentionException"/> rather than spinning forever.</summary>
    internal const int DefaultMaxAttempts = 64;

    /// <summary>The bound on consecutive transient-failure retries for a single storage operation before the
    /// transient error is surfaced (design §2.11.3 "bounded retries").</summary>
    internal const int MaxTransientRetries = 8;

    private readonly IStorageBackend _backend;
    private readonly DeltaLog _log;
    private readonly int _maxAttempts;
    private readonly Func<string> _nonceFactory;
    private readonly Func<int, CancellationToken, Task> _transientBackoff;
    private readonly ILogger<DeltaCommitter> _logger;
    private readonly DeltaStorageTelemetry _telemetry;
    private readonly Func<int, CancellationToken, Task>? _rebaseJitter;
    private readonly TimeProvider _timeProvider;

    /// <summary>The shared <c>deltasharp.component</c>/<c>deltasharp.operation</c> correlation scope attached
    /// to every commit log line (design §7.2.1). Cached so <see cref="ILogger.BeginScope"/> allocates no new
    /// state array per commit.</summary>
    private static readonly KeyValuePair<string, object?>[] CommitLogScope =
    {
        new(DeltaSharpTelemetry.ComponentKey, DeltaStorageTelemetry.DeltaComponent),
        new(DeltaSharpTelemetry.OperationKey, DeltaStorageTelemetry.CommitOperation),
    };

    /// <summary>Test seam (null/inert in production): awaited immediately before each put-if-absent with
    /// <c>(attemptIndex, targetVersion)</c>, so a test can deterministically interleave a racing writer.</summary>
    internal volatile Func<int, long, CancellationToken, Task>? BeforePutProbe;

    public DeltaCommitter(IStorageBackend backend)
        : this(backend, DefaultMaxAttempts, nonceFactory: null, transientBackoff: null)
    {
    }

    /// <summary>Creates a committer with an explicit <paramref name="timeProvider"/> so the wall-clock
    /// <c>commitInfo.timestamp</c> is deterministic (the production write door threads its injected clock in;
    /// tests pin a fake). All other seams default to production.</summary>
    public DeltaCommitter(IStorageBackend backend, TimeProvider timeProvider)
        : this(backend, DefaultMaxAttempts, nonceFactory: null, transientBackoff: null, timeProvider: timeProvider)
    {
    }

    /// <param name="rebaseJitter">Optional, <b>off by default</b> (null ⇒ current zero-delay behavior): when
    /// supplied, it is awaited after a safe rebase and before the next put-if-absent, spreading colliding
    /// writers in time to reduce livelock under contention (visible via the conflict metric). A deterministic
    /// delegate is injected in tests so it never perturbs the existing seams or timing.</param>
    /// <param name="timeProvider">The clock stamped into <c>commitInfo.timestamp</c> (epoch-ms). Defaults to
    /// <see cref="TimeProvider.System"/>; production wiring threads the write door's injected clock so a test
    /// can pin the commit timestamp. Never a banned <c>DateTimeOffset.UtcNow</c>.</param>
    internal DeltaCommitter(
        IStorageBackend backend,
        int maxAttempts,
        Func<string>? nonceFactory,
        Func<int, CancellationToken, Task>? transientBackoff = null,
        ILogger<DeltaCommitter>? logger = null,
        DeltaStorageTelemetry? telemetry = null,
        Func<int, CancellationToken, Task>? rebaseJitter = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "maxAttempts must be at least 1.");
        }

        _backend = backend;
        _log = new DeltaLog(backend);
        _maxAttempts = maxAttempts;
        _nonceFactory = nonceFactory ?? DefaultNonceFactory;
        _transientBackoff = transientBackoff ?? DefaultTransientBackoffAsync;
        _logger = logger ?? NullLogger<DeltaCommitter>.Instance;
        _telemetry = telemetry ?? DeltaStorageTelemetry.Shared;
        _rebaseJitter = rebaseJitter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>A bounded, deterministic-friendly rebase jitter suitable for the <c>rebaseJitter</c> seam:
    /// full-jitter over <c>[0, cap)</c> milliseconds using the crypto RNG (never the banned
    /// <c>System.Random</c>). Off in production by default; a host/story opts in explicitly.</summary>
    internal static Task DefaultRebaseJitterAsync(int attempt, CancellationToken cancellationToken)
    {
        int capMs = Math.Min(50, 2 * (1 << Math.Min(attempt, 4)));
        int delayMs = RandomNumberGenerator.GetInt32(0, capMs + 1);
        return delayMs == 0 ? Task.CompletedTask : Task.Delay(delayMs, cancellationToken);
    }

    /// <summary>The production idempotency-nonce source: 128 bits from a cryptographic RNG, hex-encoded.
    /// Uses <see cref="RandomNumberGenerator"/> (not the banned <c>Guid.NewGuid</c>/<c>System.Random</c>) so
    /// nonces are collision-resistant, while a deterministic factory can be injected in tests.</summary>
    internal static string DefaultNonceFactory() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>The production transient-retry backoff: capped exponential delay with full jitter
    /// (RandomNumberGenerator, not the banned <c>System.Random</c>). Tests inject a no-op for determinism.</summary>
    private static async Task DefaultTransientBackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        int baseMs = Math.Min(1000, 25 * (1 << Math.Min(attempt, 5)));
        int delayMs = baseMs + RandomNumberGenerator.GetInt32(0, baseMs + 1);
        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits <paramref name="actions"/> against <paramref name="readSnapshot"/> under
    /// <paramref name="readScope"/>, returning the version that became visible.
    /// </summary>
    /// <exception cref="DeltaProtocolException">The table's writer protocol is unsupported, or the table is
    /// append-only (<c>delta.appendOnly=true</c>) and the commit changes committed data (fail closed).</exception>
    /// <exception cref="DeltaConcurrentModificationException">A concurrent commit logically conflicts with
    /// this one (aborted, not rebased).</exception>
    /// <exception cref="DeltaCommitContentionException">The commit did not converge within the rebase-retry
    /// budget under sustained concurrency (retryable — the commit provably did not land).</exception>
    /// <exception cref="DeltaCommitUnknownStateException">An ambiguous outcome could not be resolved.</exception>
    public async Task<DeltaCommitResult> CommitAsync(
        Snapshot readSnapshot,
        IReadOnlyList<DeltaAction> actions,
        DeltaReadScope readScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(readScope);
        if (actions.Count == 0)
        {
            throw new ArgumentException("A commit must contain at least one action.", nameof(actions));
        }

        // Column mapping (#523 read / #572 write): all three modes (none/name/id) are now writable through
        // this commit choke point. id-mode DATA columns resolve by the Parquet field_id — the write door
        // stages the physical Parquet with the field_id stamped and commits the id-mode metaData/protocol
        // exactly like name mode (ColumnMapping.ToPhysicalSchema / MapWriteSchemaToPhysical carry the id).
        // The committer does NOT refuse id outright; the fail-closed invariants that remain (nested top-level
        // columns, OPTIMIZE on a column-mapped table, DELETE without deletion-vector support) are enforced by
        // their own dedicated guards. The ONE column-mapping invariant the committer itself asserts (in
        // CommitCoreAsync) is that a committed metaData may not CHANGE an EXISTING table's column-mapping mode
        // — a mode transition is sanctioned only on a fresh create — defense-in-depth behind the write door's
        // TableExistsAsync guard.

        // A BlindAppend read scope registers no read set, so it performs no data-conflict detection; a
        // commit that *removes* files (a delete/overwrite) must therefore use WholeTable or ReadFiles so a
        // concurrent delete/overwrite is caught. Reject a remove-bearing blind append up front rather than
        // silently rebasing past a concurrent same-file remove (the ConcurrentDeleteDelete cell, deferred
        // with the blind-overwrite scope to STORY-05.3.3 / #188).
        if (readScope is DeltaReadScope.BlindAppendScope && actions.Any(a => a is RemoveFileAction))
        {
            throw new ArgumentException(
                "A BlindAppend commit must be append-only (it contains no read set to detect a concurrent delete); a commit that removes files must use DeltaReadScope.WholeTable or DeltaReadScope.ReadFiles.",
                nameof(actions));
        }

        // Observability wrapper (design §7, #479): spans/metrics/logs are side-effect-free on commit
        // semantics — the inner core is byte-for-byte the prior control flow. The stopwatch is monotonic
        // (never the wall clock, checklist 09b) and the span is a null no-op until a listener samples it.
        long startTimestamp = Stopwatch.GetTimestamp();
        using Activity? activity = _telemetry.StartCommitActivity(_backend.Kind);
        using IDisposable? logScope = _logger.BeginScope(CommitLogScope);
        int attempts = 0;
        try
        {
            return await CommitCoreAsync(
                readSnapshot, actions, readScope, activity, startTimestamp, a => attempts = a, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DeltaCommitUnknownStateException ex)
        {
            // Thrown deep in ambiguous/winner recovery; record + log the terminal here where the exception
            // carries the version. (Conflict/contention/partial-txn are recorded at their in-core sites,
            // where the attempt/class context is richer.)
            RecordTerminal(activity, startTimestamp, CommitOutcome.UnknownState, ex.Version, attempts);
            DeltaCommitLog.CommitUnknownState(_logger, ex.Version);
            throw;
        }
        catch (DeltaProtocolException)
        {
            // Fail-closed protocol rejection before any write: no version was attempted, so pass version:-1
            // (the pre-write span omits a version tag) and emit an Error terminal log (Architect Low #2).
            RecordTerminal(activity, startTimestamp, CommitOutcome.Failure, version: -1, attempts);
            DeltaCommitLog.CommitFailed(_logger, -1, attempts, nameof(DeltaProtocolException));
            throw;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a commit failure: record a distinct terminal (Ok/Unset status, not Error) and propagate.
            RecordTerminal(activity, startTimestamp, CommitOutcome.Cancelled, readSnapshot.Version + 1, attempts);
            DeltaCommitLog.CommitCanceled(_logger, readSnapshot.Version + 1, attempts);
            throw;
        }
        catch (Exception ex) when (ex is not PartialTransactionException and not DeltaConcurrentModificationException and not DeltaCommitContentionException)
        {
            // Unclassified/persistent failure — no in-core terminal recorded — the highest-value on-call
            // signal. The three excluded types already recorded their terminal in-core, so excluding them
            // prevents a double count. (DeltaCommitUnknownStateException/DeltaProtocolException are caught by
            // the earlier specific blocks, so they never reach here.)
            RecordTerminal(activity, startTimestamp, CommitOutcome.Failure, readSnapshot.Version + 1, attempts);
            DeltaCommitLog.CommitFailed(_logger, readSnapshot.Version + 1, attempts, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>The commit control flow (unchanged semantics); the surrounding <see cref="CommitAsync"/>
    /// owns the span/stopwatch and the terminal recording for the deep-recovery exceptions. Each in-core
    /// terminal (success, idempotent skip, conflict abort, partial-txn, contention) records its own metric,
    /// span status, and log before returning/throwing, because that is where the attempt count and conflict
    /// class are known.</summary>
    private async Task<DeltaCommitResult> CommitCoreAsync(
        Snapshot readSnapshot,
        IReadOnlyList<DeltaAction> actions,
        DeltaReadScope readScope,
        Activity? activity,
        long startTimestamp,
        Action<int> reportAttempts,
        CancellationToken cancellationToken)
    {
        // Writer protocol negotiation: fail closed before any write if the table requires a writer
        // version/feature this build does not enforce (design §2.11 / §2.14 P3). Validate both the current
        // table protocol and any protocol this commit itself installs (an installed protocol must not raise
        // the table to a writer version/feature this writer cannot honor, nor to a reader version/feature it
        // could not read back — never publish a table this build cannot itself read).
        ProtocolSupport.EnsureWritable(readSnapshot.Protocol);
        foreach (DeltaAction action in actions)
        {
            if (action is ProtocolAction committedProtocol)
            {
                ProtocolSupport.EnsureWritable(committedProtocol);
                ProtocolSupport.EnsureReadable(committedProtocol);
            }
        }

        // #572 defense-in-depth: enabling or changing a table's column-mapping mode is sanctioned ONLY on a
        // FRESH create — the write door commits the initial mode against the synthetic empty snapshot
        // (version -1). On an EXISTING table (version >= 0) a committed metaData must NOT change the read
        // snapshot's column-mapping mode. The write door already refuses this (DeltaWriteTarget.TableExistsAsync
        // rejects enabling column mapping on an existing table); this committer-level assertion is the
        // lower-level safety net that preserves the removed id-write gate's protection WITHOUT refusing a
        // legitimate id-mode write — an append/overwrite/rename metaData preserves the mode key, so only an
        // actual mode TRANSITION on an existing table trips this guard, before any bytes are published.
        if (readSnapshot.Version >= 0)
        {
            ColumnMappingMode currentMode = ColumnMapping.ResolveMode(readSnapshot.Metadata.Configuration);
            foreach (DeltaAction action in actions)
            {
                if (action is not MetadataAction committedMetadata)
                {
                    continue;
                }

                ColumnMappingMode committedMode = ColumnMapping.ResolveMode(committedMetadata.Configuration);
                if (committedMode != currentMode)
                {
                    throw DeltaProtocolException.Unsupported(
                        $"A committed metaData changes the table's column-mapping mode from "
                        + $"'{ModeLabel(currentMode)}' to '{ModeLabel(committedMode)}' on an existing table. "
                        + "Enabling or changing column mapping on an existing table is unsupported in this "
                        + "build and fails closed; column mapping can only be enabled on a fresh table "
                        + "(first write).");
                }
            }

            static string ModeLabel(ColumnMappingMode mode) => mode switch
            {
                ColumnMappingMode.Name => "name",
                ColumnMappingMode.Id => "id",
                _ => "none",
            };
        }

        // #572 / deltaspec N3 defense-in-depth (completes the B3 committer hardening): a committed metaData
        // must be validated for the SAME schema invariants the snapshot-load choke point enforces, BEFORE its
        // bytes are published (fail-closed at COMMIT, table unchanged, no version advanced — not silently
        // committed to surface only on the NEXT load). The invariants, run for BOTH the sanctioned
        // fresh-create (version -1) AND existing tables:
        //   (1) ALL modes (none/name/id) — every logical partitionColumns entry names a column present in the
        //       committed schema (deltaspec N3/R4 finding #3); and
        //   (1b) none mode only — every LOGICAL partition column name is a safe path segment (char + length),
        //       because with no physical mapping the logical name IS the partition-directory path segment
        //       (deltaspec R7; name/id physical segments are covered by (2)); and
        //   (2) mapped modes (name/id) only — the mapping is internally consistent
        //       (ColumnMapping.ValidateColumnMappingSchema at DeltaLog.LoadSnapshotAsync): every field a LEAF
        //       carrying a UNIQUE id in [1, int.MaxValue] and <= maxColumnId, with globally-unique physicalNames
        //       that are safe path segments (char + length); a malformed mapped metaData (duplicate/missing/
        //       out-of-range id, duplicate or unsafe physicalName, a nested mapped column, or an unparseable/
        //       non-struct schema) fails closed here.
        // The public write doors always construct a consistent, in-schema mapping, but the committer is a
        // lower-level primitive (the central id-write gate is gone). Only metaData-bearing commits reach the
        // body — a plain append/delete carries no metaData, so the hot path never parses a schema. O(columns).
        foreach (DeltaAction action in actions)
        {
            if (action is not MetadataAction committedMetadata)
            {
                continue;
            }

            // Parse the committed schema once; both checks below validate against it (logical field names).
            StructType committedSchema = ParseCommittedSchema(committedMetadata);

            // (0) all-mode case-insensitive column-name uniqueness: DeltaSharp stores names case-sensitively,
            // but a strict reader that resolves names case-insensitively (Spark's default) rejects a schema with
            // e.g. `region` AND `REGION` (COLUMN_ALREADY_EXISTS). Enforced at the committer for every mode so no
            // NEW case-colliding table is published; complements the evolution path's identical guard.
            ColumnMapping.EnsureNoCaseInsensitiveDuplicateColumns(committedSchema);

            // (0b) all-mode: a committed `delta.appendOnly` value must be a valid boolean. AppendOnlyFeature
            // .IsEnabled throws Malformed on a non-boolean; validating the COMMITTED config here fails a
            // malformed value closed at commit rather than at a later overwrite (which parses it via the same
            // path and would otherwise be the first — surprising — point of failure).
            _ = AppendOnlyFeature.IsEnabled(committedMetadata.Configuration);

            // (1) all-mode partition existence: partitionColumns store LOGICAL names, compared against the
            // logical StructType field names (never physical). Enforced at the committer (not at snapshot
            // load — a load-side check is too broad for the stub-schema log/checkpoint fixture corpus), so no
            // NEW bad-partition metaData is published; fails closed before publish, table unchanged.
            ColumnMapping.EnsurePartitionColumnsInSchema(committedSchema, committedMetadata.PartitionColumns);

            // (2) mapped-mode column-mapping consistency (none mode is a no-op for the mapping validator).
            ColumnMappingMode committedMode = ColumnMapping.ResolveMode(committedMetadata.Configuration);
            if (committedMode == ColumnMappingMode.None)
            {
                // none mode has no physical mapping, so the LOGICAL partition column names ARE the
                // partition-directory path segments — validate their path-safety here (#572 deltaspec R7). In
                // name/id mode the segment is the mapped physical name, validated by ValidateColumnMappingSchema
                // below, and the logical name is decoupled from the path.
                ColumnMapping.EnsureNoneModePartitionNamesSafe(committedMetadata.PartitionColumns);
                continue;
            }

            ColumnMapping.ValidateColumnMappingSchema(
                committedMode, committedSchema, committedMetadata.Configuration);
        }

        // Idempotency via txn (§2.11.4): if this commit records application transactions whose versions the
        // read snapshot already reflects (snapshot.txn[appId] >= version), the batch already committed —
        // report success without re-writing, so a streaming/micro-batch retry that re-reads a fresh snapshot
        // (or a cross-restart retry) never duplicates rows. Idempotency is all-or-nothing for the atomic
        // batch: skip only if EVERY txn is covered; fail closed on a partial overlap (some covered, some not)
        // rather than silently dropping the uncommitted transactions and their data.
        switch (ClassifyTxnCoverage(actions, readSnapshot.Transactions))
        {
            case TxnCoverage.All:
                // Already-committed idempotent retry — a no-op skip. Must NOT re-evaluate append-only: the
                // batch already landed (legitimately, when it was accepted), so a table that has SINCE become
                // append-only must still skip the replay, not raise AppendOnlyViolation (§2.11.4 idempotency).
                return Succeed(activity, startTimestamp, new DeltaCommitResult(readSnapshot.Version, Attempts: 0, Skipped: true));
            case TxnCoverage.Partial:
                throw PartialConflict(activity, startTimestamp, actions, readSnapshot.Transactions, attempts: 0);
            case TxnCoverage.None:
                // A genuinely new commit (no idempotency key covered). Append-only enforcement (Delta
                // "Append-only Tables"; #549) runs HERE — after protocol negotiation (EnsureWritable keeps
                // precedence) and the idempotency skip, but before any write, and inside CommitAsync's
                // telemetry try so a rejection records the fail-closed terminal (the DeltaProtocolException
                // catch) like the sibling EnsureWritable rejection. It refuses a commit that DELETEs OR CHANGEs
                // committed data (a remove with dataChange=true — DELETE/OVERWRITE) on a delta.appendOnly=true
                // table, regardless of read scope; compaction removes (dataChange=false, e.g. OPTIMIZE) are
                // allowed, matching Spark's `if (removes.exists(_.dataChange)) assertRemovable(snapshot)`.
                // Keyed off the read snapshot's own metadata (Spark's IS_APPEND_ONLY.fromMetaData), covering a
                // legacy writer-2 appendOnly table and a writer-7 table that enumerates the feature alike. A
                // concurrent delta.appendOnly toggle aborts this commit via MetadataChanged, so a single
                // evaluation on the read snapshot is sufficient.
                AppendOnlyFeature.EnsureCommitAllowed(readSnapshot.Metadata.Configuration, actions);
                break;
        }

        (IReadOnlyList<DeltaAction> payload, string nonce) = BuildPayload(actions, _nonceFactory());
        byte[] bytes = DeltaLogActionWriter.SerializeCommit(payload);

        DeltaCommitLog.CommitStarted(_logger, readSnapshot.Version + 1, _backend.Kind.ToLabel());

        long baseVersion = readSnapshot.Version; // R — rebased forward on each safe retry.
        int rebaseCount = 0;
        for (int attempt = 0; attempt < _maxAttempts; attempt++)
        {
            reportAttempts(attempt + 1);
            long target = baseVersion + 1; // N
            string path = DeltaLogFiles.CommitPath(target);

            if (BeforePutProbe is { } probe)
            {
                await probe(attempt, target, cancellationToken).ConfigureAwait(false);
            }

            bool won;
            try
            {
                won = await WithTransientRetryAsync(
                    ct => _backend.PutIfAbsentAsync(path, bytes, ct).AsTask(), cancellationToken).ConfigureAwait(false);
            }
            catch (DeltaStorageException ex) when (ex.Kind == StorageErrorKind.RetryUnsafeAmbiguous)
            {
                switch (await ResolveAmbiguousAsync(target, nonce, cancellationToken).ConfigureAwait(false))
                {
                    case AmbiguousResolution.OursCommitted:
                        return Succeed(activity, startTimestamp, new DeltaCommitResult(target, attempt + 1)); // ack lost, write landed.
                    case AmbiguousResolution.SlotFree:
                        DeltaCommitLog.CommitRetry(
                            _logger, attempt + 1, target, DeltaStorageTelemetry.ToLabel(CommitRetryReason.AmbiguousSlotFree), rebaseCount);
                        activity?.AddEvent(new ActivityEvent("retry.ambiguous_slot_free"));
                        continue; // our put did not land; the slot is unclaimed — retry the same version.
                    default: // LostToOther: <N>.json exists but is not ours → resolve as a definite conflict.
                        won = false;
                        break;
                }
            }

            if (won)
            {
                return Succeed(activity, startTimestamp, new DeltaCommitResult(target, attempt + 1));
            }

            // Definite conflict: read the winners over (baseVersion, M], classify, then rebase or abort. The
            // read (not a separate existence probe) also nonce-checks each winner: if our own durable-but-
            // unacknowledged commit surfaced here as a "winner" (a lost ack, or a HEAD/GET that lagged the
            // durable put), succeed idempotently rather than rebasing past it — which would double-commit at
            // target+1 (§2.11.6). Doing the check on the SAME read that classifies winners makes it robust to
            // an intra-attempt visibility flap (no two disagreeing existence probes).
            (long latest, IReadOnlyList<DeltaAction> winners, long? ownCommitVersion) =
                await ReadWinnersAsync(baseVersion, nonce, cancellationToken).ConfigureAwait(false);
            if (ownCommitVersion is { } ourVersion)
            {
                return Succeed(activity, startTimestamp, new DeltaCommitResult(ourVersion, attempt + 1));
            }

            // Idempotency via txn on the conflict path (§2.11.4): if the winners already recorded this
            // commit's application transactions (a stale-snapshot or racing retry of the same appId whose
            // prior attempt landed — the in-memory nonce differs across attempts, so the own-commit check
            // above misses it), succeed idempotently rather than rebasing or raising ConcurrentTransactionException.
            //
            // DIVERGENCE (intentional): stock Delta fails a same-appId conflict LOUD with
            // ConcurrentTransactionException even when the winner is this appId's OWN prior landed attempt.
            // DeltaSharp instead treats a covering winner (txnState[appId] >= our version) as proof our batch
            // already committed and skips — strictly more robust for a lost-ack / stale-snapshot retry, which
            // is the exact failure a txn idempotency key exists to absorb. A NON-covering same-appId winner
            // (a genuine concurrent writer, lower version) does NOT match and still fails loud below. All-or-
            // nothing: a partial overlap fails closed (never a silent drop of the uncommitted transactions).
            ImmutableSortedDictionary<string, long> winnerTxns = TxnStateOf(winners);
            switch (ClassifyTxnCoverage(actions, winnerTxns))
            {
                case TxnCoverage.All:
                    return Succeed(activity, startTimestamp, new DeltaCommitResult(latest, attempt + 1, Skipped: true));
                case TxnCoverage.Partial:
                    throw PartialConflict(activity, startTimestamp, actions, winnerTxns, attempts: attempt + 1);
                case TxnCoverage.None:
                    break; // no winner covered our txn — fall through to conflict classification.
            }

            try
            {
                DeltaConflictChecker.Check(actions, readScope, winners); // throws on a logical conflict
            }
            catch (DeltaConcurrentModificationException ex)
            {
                // A definite, non-rebasable conflict: a domain outcome (Warning, outcome=conflict), not a
                // runtime error (§7.2.3). Record the conflict class + terminal, then propagate unchanged.
                string conflictClass = DeltaStorageTelemetry.ToConflictClass(ex.Kind);
                _telemetry.RecordConflict(ex.Kind);
                DeltaCommitLog.CommitConflict(_logger, attempt + 1, target, conflictClass);
                activity?.AddEvent(new ActivityEvent("conflict.detected",
                    tags: new ActivityTagsCollection { { DeltaStorageTelemetry.ConflictClassKey, conflictClass } }));
                RecordTerminal(activity, startTimestamp, CommitOutcome.Conflict, target, attempt + 1);
                throw;
            }

            // Safe rebase: a concurrent write we can rebase past (counted as a conflict we recovered from).
            rebaseCount++;
            _telemetry.RecordConflict(null); // null ⇒ concurrent_write (a safe rebase, not an aborting kind)
            DeltaCommitLog.CommitRetry(
                _logger, attempt + 1, target, DeltaStorageTelemetry.ToLabel(CommitRetryReason.ConflictRebase), rebaseCount);
            activity?.AddEvent(new ActivityEvent("retry.conflict_rebase",
                tags: new ActivityTagsCollection { { DeltaSharpTelemetry.TableVersionKey, latest } }));

            // Optional, off-by-default rebase jitter (#479 nice-to-have): spread colliding writers in time so
            // sustained contention is less likely to livelock. Null in production and in every existing test.
            if (_rebaseJitter is { } jitter)
            {
                await jitter(attempt, cancellationToken).ConfigureAwait(false);
            }

            baseVersion = latest; // safe: rebase onto M and retry with the same nonce-stable bytes.
        }

        // Budget exhausted under sustained contention: the commit provably did NOT land (every attempt ended
        // in a lost race or a safe rebase), so this is a known, retryable outcome — distinct from the genuine
        // unknown-state paths (§2.11.3).
        long contendedVersion = baseVersion + 1;
        RecordTerminal(activity, startTimestamp, CommitOutcome.Contention, contendedVersion, _maxAttempts);
        DeltaCommitLog.CommitContentionExhausted(_logger, contendedVersion, _maxAttempts);
        throw new DeltaCommitContentionException(
            contendedVersion,
            _maxAttempts,
            $"The commit did not converge within {_maxAttempts} attempts under sustained concurrent writers; it did not land — retry from a fresh snapshot.");
    }

    /// <summary>Records the success/skip terminal signals (metric, span status, log) and returns the result
    /// so a call site reads <c>return Succeed(...)</c>.</summary>
    private DeltaCommitResult Succeed(Activity? activity, long startTimestamp, DeltaCommitResult result)
    {
        CommitOutcome outcome = result.Skipped ? CommitOutcome.Skipped : CommitOutcome.Success;
        RecordTerminal(activity, startTimestamp, outcome, result.Version, result.Attempts);
        if (result.Skipped)
        {
            DeltaCommitLog.CommitSkipped(_logger, result.Version, "idempotent-txn");
        }
        else
        {
            DeltaCommitLog.CommitCompleted(
                _logger, result.Version, result.Attempts, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }

        return result;
    }

    /// <summary>Records the partial-transaction terminal (fail-closed) and builds the exception to throw so a
    /// call site reads <c>throw PartialConflict(...)</c>.</summary>
    private PartialTransactionException PartialConflict(
        Activity? activity, long startTimestamp, IReadOnlyList<DeltaAction> actions, ImmutableSortedDictionary<string, long> txnState, int attempts)
    {
        (int committed, int uncommitted) = CountTxnCoverage(actions, txnState);
        RecordTerminal(activity, startTimestamp, CommitOutcome.PartialTransaction, version: -1, attempts);
        DeltaCommitLog.CommitPartialTransaction(_logger, committed, uncommitted);
        return PartialTxn(actions, txnState);
    }

    /// <summary>Records the terminal metric measurement (duration, outcome count, attempt depth) and stamps
    /// the span with the outcome, table version, attempt, and status. A single call per commit.</summary>
    private void RecordTerminal(Activity? activity, long startTimestamp, CommitOutcome outcome, long version, int attempts)
    {
        double seconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
        _telemetry.RecordCommitTerminal(outcome, seconds, attempts);
        if (activity is not null)
        {
            activity.SetTag(DeltaSharpTelemetry.OutcomeKey, DeltaStorageTelemetry.ToLabel(outcome));
            if (version >= 0)
            {
                activity.SetTag(DeltaSharpTelemetry.TableVersionKey, version);
            }

            activity.SetTag(DeltaSharpTelemetry.AttemptKey, attempts);

            // Success/Skipped are Ok; a Cancelled commit is left Unset (a cancel is not a failure); every
            // other terminal (Failure/Conflict/Contention/UnknownState/PartialTransaction) is Error.
            if (outcome is CommitOutcome.Success or CommitOutcome.Skipped)
            {
                activity.SetStatus(ActivityStatusCode.Ok);
            }
            else if (outcome is not CommitOutcome.Cancelled)
            {
                activity.SetStatus(ActivityStatusCode.Error);
            }
        }
    }

    // Parses a committed metaData's schemaString into the StructType the column-mapping schema validator needs
    // (#572 / N3), mirroring how the load path materializes snapshot.Schema (Snapshot.ParseSchema via
    // SchemaJson.FromJson). A malformed/non-struct schemaString on a mapped-mode commit is itself an
    // inconsistent metaData — surfaced as the same fail-closed DeltaProtocolException the load path raises, so
    // it is refused at commit time rather than published and only rejected on the next load.
    private static StructType ParseCommittedSchema(MetadataAction metadata)
    {
        DataType parsed;
        try
        {
            parsed = SchemaJson.FromJson(metadata.SchemaString);
        }
        catch (SchemaValidationException ex)
        {
            throw DeltaProtocolException.Inconsistent(
                "A committed metaData carrying a column-mapping mode has an unparseable schemaString.", ex);
        }

        if (parsed is not StructType structType)
        {
            throw DeltaProtocolException.Inconsistent(
                "A committed metaData carrying a column-mapping mode has a schemaString that is not a struct.");
        }

        return structType;
    }

    /// <summary>Reads the actions of every commit over <c>(afterExclusive, M]</c> — the winners since the
    /// read snapshot — returning the latest version <c>M</c>, their concatenated actions, and the version
    /// that carries <paramref name="nonce"/> if <b>our own</b> commit is among them (a durable-but-
    /// unacknowledged commit surfacing as a winner, §2.11.6). Nonce-checking here — on the same read that
    /// classifies winners — is robust to an intra-attempt visibility flap. The walk terminates at the first
    /// absent version; it is bounded in practice by the backend's finite, monotonic commit log.</summary>
    private async Task<(long Latest, IReadOnlyList<DeltaAction> Winners, long? OwnCommitVersion)> ReadWinnersAsync(
        long afterExclusive, string nonce, CancellationToken cancellationToken)
    {
        var winners = new List<DeltaAction>();
        long? ownCommitVersion = null;
        long version = afterExclusive + 1;
        while (await CommitVisibleAsync(version, cancellationToken).ConfigureAwait(false))
        {
            IReadOnlyList<DeltaAction> commit = await ReadCommitAsync(version, cancellationToken).ConfigureAwait(false);
            if (ownCommitVersion is null && CommitCarriesNonce(commit, nonce))
            {
                ownCommitVersion = version;
            }

            winners.AddRange(commit);
            version++;
        }

        long latest = version - 1;
        if (latest <= afterExclusive)
        {
            // The put-if-absent was rejected as already-existing, yet no commit at that version is visible —
            // an unresolved state (never silently retry, which could double-commit).
            throw new DeltaCommitUnknownStateException(
                afterExclusive + 1,
                $"The commit at version {afterExclusive + 1} was rejected as already-existing, but no commit at that version is visible.");
        }

        return (latest, winners, ownCommitVersion);
    }

    private Task<bool> CommitVisibleAsync(long version, CancellationToken cancellationToken) =>
        WithTransientRetryAsync(ct => _log.CommitExistsAsync(version, ct), cancellationToken);

    private Task<IReadOnlyList<DeltaAction>> ReadCommitAsync(long version, CancellationToken cancellationToken) =>
        WithTransientRetryAsync(ct => _log.ReadCommitActionsAsync(version, ct), cancellationToken);

    /// <summary>Runs a storage operation, retrying a <see cref="StorageErrorKind.Transient"/> failure with
    /// bounded backoff (design §2.11.3). A non-transient failure (including
    /// <see cref="StorageErrorKind.RetryUnsafeAmbiguous"/>) propagates immediately to its handler.</summary>
    private async Task<T> WithTransientRetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        for (int retry = 0; ; retry++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (DeltaStorageException ex) when (ex.Kind == StorageErrorKind.Transient && retry < MaxTransientRetries)
            {
                // A transient retry keeps the terminal outcome clean (attempts=1 success), so surface it as
                // its own measurable signal: a counter increment + a span event let an SRE distinguish a
                // clean commit from a transient-degraded one (Quality Med). The commit span is the ambient
                // current activity.
                DeltaCommitLog.CommitTransientRetry(_logger, retry);
                _telemetry.RecordTransientRetry();
                Activity.Current?.AddEvent(new ActivityEvent("retry.transient"));
                await _transientBackoff(retry, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Re-resolves an ambiguous put-if-absent (design §2.11.3): re-GET <c>&lt;version&gt;.json</c>
    /// and decide whether this writer's own commit landed (nonce match), the slot is still free, or another
    /// writer won it. Correctness of the <see cref="AmbiguousResolution.SlotFree"/> verdict depends on the
    /// backend being read-after-write consistent for this key versus the ambiguous put (a re-GET must see a
    /// just-durable commit); a residual lag is caught anyway on the subsequent retry, where the
    /// definite-conflict path nonce-checks the winners it reads (<see cref="ReadWinnersAsync"/>) and returns
    /// idempotent success rather than rebasing past our own commit. Transient read failures are retried; a
    /// persistent failure fails closed as unknown-state.</summary>
    private async Task<AmbiguousResolution> ResolveAmbiguousAsync(
        long version, string nonce, CancellationToken cancellationToken)
    {
        bool exists;
        try
        {
            exists = await CommitVisibleAsync(version, cancellationToken).ConfigureAwait(false);
        }
        catch (DeltaStorageException ex)
        {
            throw new DeltaCommitUnknownStateException(
                version, $"Could not determine whether commit {version} landed after an ambiguous put-if-absent.", ex);
        }

        if (!exists)
        {
            return AmbiguousResolution.SlotFree;
        }

        IReadOnlyList<DeltaAction> committed;
        try
        {
            committed = await ReadCommitAsync(version, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeltaStorageException or DeltaProtocolException)
        {
            throw new DeltaCommitUnknownStateException(
                version, $"Could not read commit {version} to resolve an ambiguous put-if-absent.", ex);
        }

        return CommitCarriesNonce(committed, nonce)
            ? AmbiguousResolution.OursCommitted
            : AmbiguousResolution.LostToOther;
    }

    /// <summary>Builds the serialized payload: a single leading <c>commitInfo</c> carrying this attempt's
    /// idempotency <paramref name="nonce"/> (merged over any caller-supplied <c>commitInfo</c>) plus the
    /// stamped <c>timestamp</c> (from the injected clock) and <c>engineInfo</c>, followed by the caller's
    /// non-<c>commitInfo</c> actions in order. The caller-supplied <c>operation</c>/<c>operationParameters</c>/
    /// <c>operationMetrics</c> (from <see cref="DeltaCommitInfo"/>) ride through unchanged — the engine owns
    /// only the clock/nonce/engine stamps.</summary>
    private (IReadOnlyList<DeltaAction> Payload, string Nonce) BuildPayload(
        IReadOnlyList<DeltaAction> actions, string nonce)
    {
        var entries = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var rest = new List<DeltaAction>(actions.Count);
        string? operation = null;
        ImmutableSortedDictionary<string, string>? operationParameters = null;
        ImmutableSortedDictionary<string, string>? operationMetrics = null;
        foreach (DeltaAction action in actions)
        {
            if (action is CommitInfoAction commitInfo)
            {
                foreach (KeyValuePair<string, string> entry in commitInfo.Entries)
                {
                    entries[entry.Key] = entry.Value;
                }

                // First non-null wins (a commit builds at most one operation-bearing commitInfo).
                operation ??= commitInfo.Operation;
                operationParameters ??= commitInfo.OperationParameters;
                operationMetrics ??= commitInfo.OperationMetrics;
            }
            else
            {
                rest.Add(action);
            }
        }

        // The engine owns the idempotency nonce: overwrite any caller-supplied commitInfo["txnId"] so the
        // nonce is authoritative for ambiguous-ack recognition (a caller cannot forge/override it).
        entries[CommitNonceKey] = nonce;

        // The engine also owns the wall-clock timestamp (from the injected TimeProvider, never a banned
        // DateTimeOffset.UtcNow) and the engineInfo stamp — a write site only declares WHAT it did.
        long timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var stampedCommitInfo = new CommitInfoAction(
            entries.ToImmutable(),
            Timestamp: timestamp,
            Operation: operation,
            OperationParameters: operationParameters,
            OperationMetrics: operationMetrics,
            EngineInfo: EngineInfo);

        var payload = new List<DeltaAction>(rest.Count + 1) { stampedCommitInfo };
        payload.AddRange(rest);
        return (payload, nonce);
    }

    /// <summary>Derives the <see cref="EngineInfo"/> stamp — <c>DeltaSharp/&lt;version&gt;</c> — from the
    /// assembly informational version, stripping any <c>+&lt;build-metadata&gt;</c> (source-link) suffix so
    /// the value is deterministic within a build (no per-run randomness).</summary>
    private static string BuildEngineInfo()
    {
        string? informational = typeof(DeltaCommitter).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = informational is null ? "unknown" : StripBuildMetadata(informational);
        return $"DeltaSharp/{version}";
    }

    private static string StripBuildMetadata(string informationalVersion)
    {
        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }

    private static bool CommitCarriesNonce(IReadOnlyList<DeltaAction> actions, string nonce)
    {
        foreach (DeltaAction action in actions)
        {
            if (action is CommitInfoAction commitInfo
                && commitInfo.Entries.TryGetValue(CommitNonceKey, out string? value)
                && string.Equals(value, nonce, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How a commit's application transactions (<c>txn</c>) relate to what is already committed
    /// (design §2.11.4): <see cref="None"/> — no <c>txn</c>, or none already committed (proceed);
    /// <see cref="All"/> — every <c>txn</c> already committed (idempotent skip); <see cref="Partial"/> —
    /// some committed and some not (an inconsistent atomic batch — fail closed).</summary>
    private enum TxnCoverage
    {
        None,
        All,
        Partial,
    }

    /// <summary>Classifies whether this commit's application transactions are already committed per
    /// <paramref name="txnState"/> (appId → last committed version). Idempotency for the atomic batch is
    /// all-or-nothing: a <c>txn{appId, version}</c> is covered iff <paramref name="txnState"/>[appId] ≥
    /// version. <see cref="TxnCoverage.All"/> ⇒ the whole batch already landed (skip); <see cref="TxnCoverage.None"/>
    /// ⇒ proceed; <see cref="TxnCoverage.Partial"/> ⇒ the batch mixes committed and uncommitted transactions
    /// and cannot be idempotently resolved (§2.11.4) — the caller must not bundle unrelated/non-monotonic
    /// transactions into one commit. A normal single-<c>txn</c> commit only ever yields None or All.</summary>
    private static TxnCoverage ClassifyTxnCoverage(
        IReadOnlyList<DeltaAction> actions, ImmutableSortedDictionary<string, long> txnState)
    {
        int total = 0;
        int covered = 0;
        foreach (DeltaAction action in actions)
        {
            if (action is TxnAction txn)
            {
                total++;
                if (IsTxnCovered(txn, txnState))
                {
                    covered++;
                }
            }
        }

        if (covered == 0)
        {
            return TxnCoverage.None; // no txn at all, or none already committed.
        }

        return covered == total ? TxnCoverage.All : TxnCoverage.Partial;
    }

    private static bool IsTxnCovered(TxnAction txn, ImmutableSortedDictionary<string, long> txnState) =>
        txnState.TryGetValue(txn.AppId, out long committedVersion) && committedVersion >= txn.Version;

    /// <summary>Counts how many of this commit's application transactions are already committed versus not
    /// (for the fail-closed partial-transaction telemetry), without allocating the diagnostic name lists.</summary>
    private static (int Committed, int Uncommitted) CountTxnCoverage(
        IReadOnlyList<DeltaAction> actions, ImmutableSortedDictionary<string, long> txnState)
    {
        int committed = 0;
        int uncommitted = 0;
        foreach (DeltaAction action in actions)
        {
            if (action is TxnAction txn)
            {
                if (IsTxnCovered(txn, txnState))
                {
                    committed++;
                }
                else
                {
                    uncommitted++;
                }
            }
        }

        return (committed, uncommitted);
    }

    /// <summary>Builds the fail-closed exception for a partially-committed atomic batch, naming which
    /// application transactions are already committed and which are not (so an operator can reconcile).</summary>
    private static PartialTransactionException PartialTxn(
        IReadOnlyList<DeltaAction> actions, ImmutableSortedDictionary<string, long> txnState)
    {
        var committed = new List<string>();
        var uncommitted = new List<string>();
        foreach (DeltaAction action in actions)
        {
            if (action is TxnAction txn)
            {
                (IsTxnCovered(txn, txnState) ? committed : uncommitted).Add($"{txn.AppId}@{txn.Version}");
            }
        }

        return new PartialTransactionException(
            $"This commit carries {committed.Count + uncommitted.Count} application transactions of which "
            + $"{committed.Count} are already committed [{string.Join(", ", committed)}] and "
            + $"{uncommitted.Count} are not [{string.Join(", ", uncommitted)}]; idempotency for an atomic "
            + "batch is all-or-nothing, so a partially-committed batch is failed closed rather than skipped "
            + "(which would drop the uncommitted transactions and their data) or committed (which would "
            + "double-apply the committed ones). Do not bundle unrelated or non-monotonic application "
            + "transactions into a single commit (§2.11.4).");
    }

    /// <summary>Builds the per-appId last-committed transaction version map from a set of winning commit
    /// actions (appId → max version), so the conflict path can recognize an already-recorded transaction.</summary>
    private static ImmutableSortedDictionary<string, long> TxnStateOf(IReadOnlyList<DeltaAction> winners)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
        foreach (DeltaAction action in winners)
        {
            if (action is TxnAction txn)
            {
                builder[txn.AppId] = builder.TryGetValue(txn.AppId, out long existing)
                    ? Math.Max(existing, txn.Version)
                    : txn.Version;
            }
        }

        return builder.ToImmutable();
    }

    private enum AmbiguousResolution
    {
        /// <summary>Re-GET found this writer's own commit — the ack was lost but the write is durable.</summary>
        OursCommitted,

        /// <summary>Re-GET found no commit at the version — the put did not land; the slot is unclaimed.</summary>
        SlotFree,

        /// <summary>Re-GET found a different writer's commit — resolve as a definite conflict.</summary>
        LostToOther,
    }
}
