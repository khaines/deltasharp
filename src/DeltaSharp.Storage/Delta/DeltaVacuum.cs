using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using DeltaSharp.Diagnostics;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Thrown when a VACUUM is requested with a retention window <b>below</b> the configured
/// <see cref="RetentionPolicy.SafetyThreshold"/> and the caller did not enable the explicit unsafe override
/// (STORY-05.6.2 AC2). VACUUM fails closed <b>before</b> any candidate is selected or deleted: a too-short
/// retention is the highest-severity data-loss class (a file a stale reader or recent tombstone still needs
/// would be reclaimed), so the guard rejects rather than trusting the caller (design §2.14, §3.6 oracle).
/// </summary>
internal sealed class VacuumRetentionSafetyException : Exception
{
    internal VacuumRetentionSafetyException(TimeSpan requested, TimeSpan threshold)
        : base(string.Create(
            CultureInfo.InvariantCulture,
            $"VACUUM requested retention {requested} is below the {threshold} safety threshold. " +
            $"Reclaiming files younger than the threshold can corrupt stale readers and time-travel history; " +
            $"pass an explicit unsafe override to proceed anyway."))
    {
        RequestedRetention = requested;
        SafetyThreshold = threshold;
    }

    /// <summary>The rejected retention window the caller requested.</summary>
    internal TimeSpan RequestedRetention { get; }

    /// <summary>The safety floor the request fell below.</summary>
    internal TimeSpan SafetyThreshold { get; }
}

/// <summary>
/// One discovered candidate's audit record (STORY-05.6.2 AC3): the object <see cref="Path"/>, the bounded
/// <see cref="Decision"/> explaining why it was kept or is deletion-eligible, and whether it was actually
/// <see cref="Deleted"/> (always <see langword="false"/> for a dry-run). The audit is the evidence an
/// operator uses to justify a data-loss-sensitive delete — it records a decision for <b>every</b> candidate,
/// not only the deleted ones.
/// </summary>
internal readonly record struct VacuumAuditEntry(string Path, VacuumDecision Decision, bool Deleted);

/// <summary>
/// The structured outcome of a <see cref="DeltaVacuum.VacuumAsync"/> run: the snapshot
/// <see cref="Version"/> the decision was made against, the effective <see cref="Retention"/> and its
/// computed <see cref="RetentionCutoffMillis"/>, whether it was a <see cref="DryRun"/>, the
/// deletion-eligible <see cref="DeletablePaths"/> (AC1), the <see cref="DeletedPaths"/> actually reclaimed
/// (empty for a dry-run), and the per-candidate <see cref="Audit"/> (AC3). The deletion-eligible set is
/// exactly the <see cref="OrphanCleanup.SelectDeletable"/> contract's output — VACUUM never widens it.
/// </summary>
internal sealed record VacuumResult(
    long Version,
    bool DryRun,
    TimeSpan Retention,
    long RetentionCutoffMillis,
    ImmutableArray<string> DeletablePaths,
    ImmutableArray<string> DeletedPaths,
    ImmutableArray<VacuumAuditEntry> Audit);

/// <summary>
/// Retention-aware Delta <b>VACUUM</b> (design §2.14, STORY-05.6.2): reclaims data files that are no longer
/// referenced by the table and are older than the retention window, without ever deleting a file an active
/// or historical reader still needs. It is the reclamation half of the orphan-cleanup contract
/// (§2.11.5) — VACUUM discovers candidates and issues deletes; the <b>deletion decision itself always goes
/// through <see cref="OrphanCleanup.SelectDeletable"/></b>, so the fail-safe selection logic lives in exactly
/// one place.
///
/// <para>The flow (design §2.14): (1) resolve the effective retention and <b>enforce the safety threshold
/// before any selection</b> — a sub-threshold request is rejected unless the unsafe override is set (AC2);
/// (2) load the current <see cref="Snapshot"/> (the log is truth, never a listing, §2.13.1); (3) discover
/// candidates by listing the table directory and excluding the <c>_delta_log</c> (a candidate carries its
/// <c>LastModified</c> as an epoch-millis modification time); (4) compute the retention cutoff
/// <c>now − retention</c> and pass every candidate through <see cref="OrphanCleanup.SelectDeletable"/>;
/// (5) either list the eligible paths (dry-run, AC1) or delete them idempotently (AC4), recording a
/// per-candidate audit either way (AC3).</para>
///
/// <para><b>Fail-safe under listing lag / concurrent readers (AC3).</b> A stale <c>LIST</c> that omits a
/// just-written file simply yields no candidate for it — VACUUM never deletes what it does not see. A file
/// modified within the retention window is protected (<c>mtime &gt;= cutoff</c>, inclusive), a tombstone
/// removed within the window (or with an unknown deletion time) is protected, and an active file is never a
/// candidate for deletion — all enforced by the contract, so a torn or lagging view can only ever keep more,
/// never delete more.</para>
/// </summary>
internal sealed class DeltaVacuum
{
    private const string LogDirectoryPrefix = "_delta_log/";

    /// <summary>The shared correlation scope attached to every VACUUM log line (design §7.2.1), cached so
    /// <see cref="ILogger.BeginScope"/> allocates no new state array per run.</summary>
    private static readonly KeyValuePair<string, object?>[] VacuumLogScope =
    {
        new(DeltaSharpTelemetry.ComponentKey, DeltaStorageTelemetry.DeltaComponent),
        new(DeltaSharpTelemetry.OperationKey, DeltaStorageTelemetry.VacuumOperation),
    };

    private readonly IStorageBackend _backend;
    private readonly DeltaLog _log;
    private readonly RetentionPolicy _policy;
    private readonly ILogger<DeltaVacuum> _logger;
    private readonly DeltaStorageTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;

    /// <summary>Fires once (per process) when a policy with a weak safety threshold is first observed, so the
    /// "guard effectively disabled" warning is not repeated on every VACUUM.</summary>
    private static int s_weakThresholdWarned;

    /// <summary>Test seam (null/inert in production): awaited immediately <b>before</b> the candidate LIST,
    /// so a test can deterministically commit a racing writer in the list/load window. Because listing now
    /// precedes snapshot load (the TOCTOU fix), a file committed here is either seen by the list (and then
    /// present in the later-loaded snapshot, so protected) or not — never listed-but-missing-from-snapshot.
    /// On the pre-fix (load-before-list) ordering this same seam fires after the snapshot load, reproducing
    /// the data-loss race.</summary>
    internal volatile Func<CancellationToken, Task>? BeforeListProbe;

    /// <summary>Test seam (null/inert in production): awaited once after candidate selection and immediately
    /// <b>before</b> any delete, so a test can delete a selected candidate out-of-band to exercise the
    /// idempotent delete-on-missing path (AC4).</summary>
    internal volatile Func<CancellationToken, Task>? BeforeDeleteProbe;

    /// <summary>Creates a VACUUM over <paramref name="backend"/> (rooted at the Delta table directory) with
    /// the default 168-hour retention policy.</summary>
    public DeltaVacuum(IStorageBackend backend)
        : this(backend, policy: null, logger: null, telemetry: null, timeProvider: null)
    {
    }

    /// <param name="policy">The retention/safety configuration; defaults to <see cref="RetentionPolicy.Default"/>.</param>
    /// <param name="timeProvider">The clock used to compute the retention cutoff (<c>now − retention</c>);
    /// tests inject a fake <see cref="TimeProvider"/> for a deterministic cutoff. Defaults to
    /// <see cref="TimeProvider.System"/>.</param>
    internal DeltaVacuum(
        IStorageBackend backend,
        RetentionPolicy? policy = null,
        ILogger<DeltaVacuum>? logger = null,
        DeltaStorageTelemetry? telemetry = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _log = new DeltaLog(backend);
        _policy = policy ?? RetentionPolicy.Default;
        _logger = logger ?? NullLogger<DeltaVacuum>.Instance;
        _telemetry = telemetry ?? DeltaStorageTelemetry.Shared;
        _timeProvider = timeProvider ?? TimeProvider.System;

        WarnIfWeakSafetyThreshold();
    }

    /// <summary>Emits a one-time Warning when the policy's <see cref="RetentionPolicy.SafetyThreshold"/> is
    /// below Delta's 168-hour default (including a zero threshold), because that disables the
    /// sub-threshold-retention guard (AC2) and a too-short VACUUM could reclaim files a stale reader or a
    /// recent tombstone still needs.</summary>
    private void WarnIfWeakSafetyThreshold()
    {
        if (_policy.SafetyThreshold >= RetentionPolicy.DefaultRetentionWindow)
        {
            return;
        }

        if (Interlocked.Exchange(ref s_weakThresholdWarned, 1) == 0)
        {
            DeltaVacuumLog.VacuumWeakSafetyThreshold(
                _logger,
                _policy.SafetyThreshold.TotalHours,
                RetentionPolicy.DefaultRetentionWindow.TotalHours);
        }
    }

    /// <summary>
    /// Runs VACUUM against the latest committed snapshot.
    /// </summary>
    /// <param name="retention">The retention window; files younger than <c>now − retention</c> (or removed
    /// within it) are protected. Defaults to the policy's <see cref="RetentionPolicy.DefaultRetention"/>.</param>
    /// <param name="dryRun">When <see langword="true"/>, the deletion-eligible paths are listed but nothing
    /// is deleted (AC1).</param>
    /// <param name="unsafeOverride">When <see langword="true"/>, a retention below the safety threshold is
    /// permitted instead of rejected (AC2) — the caller accepts the stale-reader/time-travel data-loss risk.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retention"/> is negative.</exception>
    /// <exception cref="VacuumRetentionSafetyException">The effective retention is below the safety threshold
    /// and <paramref name="unsafeOverride"/> is <see langword="false"/>.</exception>
    public async Task<VacuumResult> VacuumAsync(
        TimeSpan? retention = null,
        bool dryRun = false,
        bool unsafeOverride = false,
        CancellationToken cancellationToken = default)
    {
        if (retention is { } requested && requested < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), requested, "Retention must be non-negative.");
        }

        using IDisposable? logScope = _logger.BeginScope(VacuumLogScope);

        // AC2: enforce the safety threshold BEFORE loading the snapshot, listing, or selecting anything —
        // a rejected VACUUM must never touch the store or leak a candidate listing. The pre-I/O gate uses
        // the value knowable without a snapshot: an explicit request, else the policy default (which the
        // policy validates is at or above the threshold). When no explicit retention is given, the table's
        // configured retention (read after load, AFTER listing) can only RAISE the effective window, so a
        // no-argument VACUUM is re-checked post-load below — never under-retained past this gate.
        TimeSpan preCheck = retention ?? _policy.DefaultRetention;
        if (preCheck < _policy.SafetyThreshold && !unsafeOverride)
        {
            DeltaVacuumLog.VacuumRejectedRetention(
                _logger, preCheck.TotalHours, _policy.SafetyThreshold.TotalHours);
            _telemetry.RecordVacuumTerminal(VacuumOutcome.RejectedUnsafeRetention, durationSeconds: 0);
            throw new VacuumRetentionSafetyException(preCheck, _policy.SafetyThreshold);
        }

        // Architect: emit the Started line at accepted-request time — after the gate, before any snapshot
        // load — so a load (or listing) failure still leaves a Started breadcrumb. The snapshot version and
        // the effective (possibly table-configured) retention are reported on the Completed line.
        DeltaVacuumLog.VacuumStarted(
            _logger, _backend.Kind.ToLabel(), preCheck.TotalHours, dryRun, unsafeOverride);

        long startTimestamp = Stopwatch.GetTimestamp();
        using Activity? activity = _telemetry.StartVacuumActivity(_backend.Kind);
        try
        {
            VacuumResult result = await RunAsync(retention, dryRun, unsafeOverride, cancellationToken)
                .ConfigureAwait(false);

            double seconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
            _telemetry.RecordVacuumTerminal(dryRun ? VacuumOutcome.DryRun : VacuumOutcome.Completed, seconds);
            SetOutcomeTag(activity, dryRun ? VacuumOutcome.DryRun : VacuumOutcome.Completed);
            DeltaVacuumLog.VacuumCompleted(
                _logger,
                result.Version,
                result.Audit.Length,
                result.DeletablePaths.Length,
                result.DeletedPaths.Length,
                dryRun,
                seconds * 1000);
            return result;
        }
        catch (VacuumRetentionSafetyException)
        {
            // A post-load rejection (the table's configured retention is itself sub-threshold, AC2 + MEDIUM):
            // record the fail-closed terminal, not a generic failure. The rejection Warning was already logged.
            _telemetry.RecordVacuumTerminal(
                VacuumOutcome.RejectedUnsafeRetention, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
            SetOutcomeTag(activity, VacuumOutcome.RejectedUnsafeRetention);
            throw;
        }
        catch (OperationCanceledException)
        {
            _telemetry.RecordVacuumTerminal(
                VacuumOutcome.Cancelled, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
            SetOutcomeTag(activity, VacuumOutcome.Cancelled);
            DeltaVacuumLog.VacuumCanceled(_logger);
            throw;
        }
        catch (Exception ex)
        {
            _telemetry.RecordVacuumTerminal(
                VacuumOutcome.Failure, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
            SetOutcomeTag(activity, VacuumOutcome.Failure);
            activity?.SetStatus(ActivityStatusCode.Error);
            DeltaVacuumLog.VacuumFailed(_logger, ex.GetType().Name);
            throw;
        }
    }

    private async Task<VacuumResult> RunAsync(
        TimeSpan? requestedRetention, bool dryRun, bool unsafeOverride, CancellationToken cancellationToken)
    {
        // CRITICAL-2 (TOCTOU): LIST BEFORE LOAD SNAPSHOT. Delta requires listing files before reading the
        // log so the snapshot is at least as new as the listing: any file the listing shows that is active
        // is then guaranteed to appear in the later-loaded snapshot (and is protected), while any file
        // committed after the load was written after the list and is not a candidate at all. Loading first
        // would let a file committed in the load→list window appear in the listing but not the (older)
        // snapshot — with an mtime below the cutoff (clock skew / preserved-timestamp move / long copy) it
        // would bypass the recency fail-safe and be deleted. Listing first closes that window.
        //
        // NOTE (tracked): candidate discovery + the protected set assume every referenced file is an
        // add.path/remove.path. Deletion-vector (.bin) and Change-Data-Feed (_change_data/) files are
        // referenced by other fields; when those features land their paths MUST be protected here (see
        // OrphanCleanup remarks) — until then this VACUUM must not run on a table using them.
        if (BeforeListProbe is { } beforeList)
        {
            await beforeList(cancellationToken).ConfigureAwait(false);
        }

        // Candidate discovery: list the table directory and keep every object except the _delta_log (the
        // log is metadata truth, never a reclamation target). Active files are deliberately NOT excluded
        // here — passing them through the contract lets it exclude them AND lets the audit record an
        // "active" decision for each, so the audit covers every discovered candidate (AC3).
        var candidates = new List<OrphanCandidate>();
        await foreach (StorageObjectInfo info in _backend.ListAsync(prefix: string.Empty, cancellationToken)
            .ConfigureAwait(false))
        {
            if (IsLogObject(info.Path))
            {
                continue;
            }

            candidates.Add(new OrphanCandidate(info.Path, ToEpochMillis(info.LastModifiedUtc)));
        }

        Snapshot snapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);

        // MEDIUM: resolve the effective retention. When the caller named no explicit window, honor the
        // table's delta.deletedFileRetentionDuration (from Metadata.Configuration) so a table configured for
        // e.g. 30 days does not silently lose history after the 7-day process default. An explicit request
        // always wins. Reading the property requires the loaded snapshot, so re-check the safety threshold
        // against the EFFECTIVE retention here (fail-closed) — the pre-load gate only knew the process
        // default. A property that is present but unparseable throws (fail-closed) via ResolveTableRetention.
        TimeSpan retention = requestedRetention ?? _policy.ResolveTableRetention(snapshot.Metadata.Configuration);
        if (retention < _policy.SafetyThreshold && !unsafeOverride)
        {
            DeltaVacuumLog.VacuumRejectedRetention(
                _logger, retention.TotalHours, _policy.SafetyThreshold.TotalHours);
            throw new VacuumRetentionSafetyException(retention, _policy.SafetyThreshold);
        }

        long nowMillis = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        long cutoffMillis = nowMillis - (long)retention.TotalMilliseconds;

        // The single source of the deletion decision AND the audit reason (design §2.11.5): active files,
        // retention-protected tombstones, and recently-staged files are excluded fail-safe by the contract
        // (encoding-robust) — VACUUM never re-implements or widens this.
        IReadOnlyList<OrphanDecision> classified =
            OrphanCleanup.Classify(snapshot, candidates, cutoffMillis);

        (ImmutableArray<string> deletablePaths, ImmutableArray<string> deletedPaths,
            ImmutableArray<VacuumAuditEntry> audit) =
            await ApplyAndAuditAsync(classified, dryRun, cancellationToken).ConfigureAwait(false);

        RecordDecisionCounts(audit);

        return new VacuumResult(
            snapshot.Version,
            dryRun,
            retention,
            cutoffMillis,
            deletablePaths,
            deletedPaths,
            audit);
    }

    /// <summary>Deletes the <see cref="OrphanClassification.Deletable"/> candidates (idempotently, AC4)
    /// unless <paramref name="dryRun"/>, and builds the per-candidate audit (AC3) from the same
    /// classification — a single source of truth, so the deletion set never diverges from the audit reason.</summary>
    private async Task<(ImmutableArray<string> Deletable, ImmutableArray<string> Deleted, ImmutableArray<VacuumAuditEntry> Audit)> ApplyAndAuditAsync(
        IReadOnlyList<OrphanDecision> classified,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (BeforeDeleteProbe is { } beforeDelete)
        {
            await beforeDelete(cancellationToken).ConfigureAwait(false);
        }

        var deletable = ImmutableArray.CreateBuilder<string>();
        var deleted = ImmutableArray.CreateBuilder<string>();
        var audit = ImmutableArray.CreateBuilder<VacuumAuditEntry>(classified.Count);
        foreach (OrphanDecision decision in classified)
        {
            bool eligible = decision.Classification == OrphanClassification.Deletable;
            if (eligible)
            {
                deletable.Add(decision.Path);
            }

            bool wasDeleted = false;
            if (eligible && !dryRun)
            {
                // DeleteAsync is idempotent: a missing object (already reclaimed by a prior partial run, or
                // removed out-of-band between selection and delete) is a no-op success, so a VACUUM retry
                // after a crash mid-delete converges (AC4).
                await _backend.DeleteAsync(decision.Path, cancellationToken).ConfigureAwait(false);
                deleted.Add(decision.Path);
                wasDeleted = true;
            }

            VacuumDecision auditDecision = ToVacuumDecision(decision.Classification);
            audit.Add(new VacuumAuditEntry(decision.Path, auditDecision, wasDeleted));
            DeltaVacuumLog.VacuumCandidateDecision(
                _logger, decision.Path, DeltaStorageTelemetry.ToLabel(auditDecision), wasDeleted);
        }

        return (deletable.ToImmutable(), deleted.ToImmutable(), audit.ToImmutable());
    }

    /// <summary>Maps the contract's <see cref="OrphanClassification"/> to the bounded telemetry/audit
    /// <see cref="VacuumDecision"/> label. The two enums are intentionally parallel: the contract owns the
    /// reason, telemetry owns its rendering.</summary>
    private static VacuumDecision ToVacuumDecision(OrphanClassification classification) => classification switch
    {
        OrphanClassification.Deletable => VacuumDecision.Deletable,
        OrphanClassification.Active => VacuumDecision.Active,
        OrphanClassification.RetentionProtectedTombstone => VacuumDecision.RetentionProtectedTombstone,
        _ => VacuumDecision.RecentlyStaged,
    };

    private void RecordDecisionCounts(ImmutableArray<VacuumAuditEntry> audit)
    {
        long deletable = 0, active = 0, tombstone = 0, staged = 0;
        foreach (VacuumAuditEntry entry in audit)
        {
            switch (entry.Decision)
            {
                case VacuumDecision.Deletable:
                    deletable++;
                    break;
                case VacuumDecision.Active:
                    active++;
                    break;
                case VacuumDecision.RetentionProtectedTombstone:
                    tombstone++;
                    break;
                default:
                    staged++;
                    break;
            }
        }

        _telemetry.RecordVacuumFiles(VacuumDecision.Deletable, deletable);
        _telemetry.RecordVacuumFiles(VacuumDecision.Active, active);
        _telemetry.RecordVacuumFiles(VacuumDecision.RetentionProtectedTombstone, tombstone);
        _telemetry.RecordVacuumFiles(VacuumDecision.RecentlyStaged, staged);
    }

    private static void SetOutcomeTag(Activity? activity, VacuumOutcome outcome) =>
        activity?.SetTag(DeltaSharpTelemetry.OutcomeKey, DeltaStorageTelemetry.ToLabel(outcome));

    private static bool IsLogObject(string path) =>
        path.StartsWith(LogDirectoryPrefix, StringComparison.Ordinal) ||
        string.Equals(path, "_delta_log", StringComparison.Ordinal);

    private static long ToEpochMillis(DateTime lastModifiedUtc) =>
        new DateTimeOffset(DateTime.SpecifyKind(lastModifiedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
