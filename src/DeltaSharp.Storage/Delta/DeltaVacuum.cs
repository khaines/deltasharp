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
        TimeSpan effective = retention ?? _policy.DefaultRetention;
        if (effective < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), effective, "Retention must be non-negative.");
        }

        using IDisposable? logScope = _logger.BeginScope(VacuumLogScope);

        // AC2: enforce the safety threshold BEFORE loading the snapshot, listing, or selecting anything —
        // a rejected VACUUM must never touch the store or leak a candidate listing.
        if (effective < _policy.SafetyThreshold && !unsafeOverride)
        {
            DeltaVacuumLog.VacuumRejectedRetention(
                _logger, effective.TotalHours, _policy.SafetyThreshold.TotalHours);
            _telemetry.RecordVacuumTerminal(VacuumOutcome.RejectedUnsafeRetention, durationSeconds: 0);
            throw new VacuumRetentionSafetyException(effective, _policy.SafetyThreshold);
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        using Activity? activity = _telemetry.StartVacuumActivity(_backend.Kind);
        try
        {
            VacuumResult result = await RunAsync(effective, dryRun, unsafeOverride, cancellationToken)
                .ConfigureAwait(false);

            double seconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
            _telemetry.RecordVacuumTerminal(dryRun ? VacuumOutcome.DryRun : VacuumOutcome.Completed, seconds);
            SetOutcomeTag(activity, dryRun ? VacuumOutcome.DryRun : VacuumOutcome.Completed);
            DeltaVacuumLog.VacuumCompleted(
                _logger,
                result.Audit.Length,
                result.DeletablePaths.Length,
                result.DeletedPaths.Length,
                dryRun,
                seconds * 1000);
            return result;
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
        TimeSpan retention, bool dryRun, bool unsafeOverride, CancellationToken cancellationToken)
    {
        Snapshot snapshot = await _log.LoadSnapshotAsync(version: null, cancellationToken).ConfigureAwait(false);

        long nowMillis = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        long cutoffMillis = nowMillis - (long)retention.TotalMilliseconds;

        DeltaVacuumLog.VacuumStarted(
            _logger, _backend.Kind.ToLabel(), snapshot.Version, retention.TotalHours, dryRun, unsafeOverride);

        // Candidate discovery: list the table directory and keep every object except the _delta_log (the
        // log is metadata truth, never a reclamation target). Active files are deliberately NOT excluded
        // here — passing them through SelectDeletable lets the contract exclude them AND lets the audit
        // record an "active" decision for each, so the audit covers every discovered candidate (AC3).
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

        // The single source of the deletion decision (design §2.11.5): active files, retention-protected
        // tombstones, and recently-staged files are excluded fail-safe by the contract — VACUUM never
        // re-implements or widens this.
        IReadOnlyList<string> deletable =
            OrphanCleanup.SelectDeletable(snapshot, candidates, cutoffMillis);
        var deletableSet = deletable.ToImmutableHashSet(StringComparer.Ordinal);

        (ImmutableArray<string> deletedPaths, ImmutableArray<VacuumAuditEntry> audit) =
            await ApplyAndAuditAsync(snapshot, candidates, deletableSet, cutoffMillis, dryRun, cancellationToken)
                .ConfigureAwait(false);

        RecordDecisionCounts(audit);

        return new VacuumResult(
            snapshot.Version,
            dryRun,
            retention,
            cutoffMillis,
            deletable.ToImmutableArray(),
            deletedPaths,
            audit);
    }

    /// <summary>Deletes the eligible candidates (idempotently, AC4) unless <paramref name="dryRun"/>, and
    /// builds the per-candidate audit (AC3). The deletion set is authoritative (<paramref name="deletableSet"/>
    /// is <see cref="OrphanCleanup.SelectDeletable"/>'s output); the kept-reason is derived only to annotate
    /// <i>why</i> a retained file was kept.</summary>
    private async Task<(ImmutableArray<string> Deleted, ImmutableArray<VacuumAuditEntry> Audit)> ApplyAndAuditAsync(
        Snapshot snapshot,
        IReadOnlyList<OrphanCandidate> candidates,
        ImmutableHashSet<string> deletableSet,
        long cutoffMillis,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ImmutableHashSet<string> active = snapshot.ActiveFiles
            .Select(add => add.Path)
            .ToImmutableHashSet(StringComparer.Ordinal);
        ImmutableHashSet<string> protectedTombstones = snapshot.Tombstones
            .Where(remove => (remove.DeletionTimestamp ?? long.MaxValue) >= cutoffMillis)
            .Select(remove => remove.Path)
            .ToImmutableHashSet(StringComparer.Ordinal);

        var deleted = ImmutableArray.CreateBuilder<string>();
        var audit = ImmutableArray.CreateBuilder<VacuumAuditEntry>(candidates.Count);
        foreach (OrphanCandidate candidate in candidates)
        {
            bool eligible = deletableSet.Contains(candidate.Path);
            VacuumDecision decision = eligible
                ? VacuumDecision.Deletable
                : ClassifyKept(candidate, active, protectedTombstones, cutoffMillis);

            bool wasDeleted = false;
            if (eligible && !dryRun)
            {
                // DeleteAsync is idempotent: a missing object (already reclaimed by a prior partial run) is
                // a no-op success, so a VACUUM retry after a crash mid-delete converges (AC4).
                await _backend.DeleteAsync(candidate.Path, cancellationToken).ConfigureAwait(false);
                deleted.Add(candidate.Path);
                wasDeleted = true;
            }

            audit.Add(new VacuumAuditEntry(candidate.Path, decision, wasDeleted));
            DeltaVacuumLog.VacuumCandidateDecision(
                _logger, candidate.Path, DeltaStorageTelemetry.ToLabel(decision), wasDeleted);
        }

        return (deleted.ToImmutable(), audit.ToImmutable());
    }

    /// <summary>Annotates <i>why</i> a non-deletable candidate was retained, mirroring the exclusion order in
    /// <see cref="OrphanCleanup.SelectDeletable"/> (active → protected tombstone → recently staged). This is a
    /// diagnostic label only; the deletion decision is never made here.</summary>
    private static VacuumDecision ClassifyKept(
        OrphanCandidate candidate,
        ImmutableHashSet<string> active,
        ImmutableHashSet<string> protectedTombstones,
        long cutoffMillis)
    {
        if (active.Contains(candidate.Path))
        {
            return VacuumDecision.Active;
        }

        if (protectedTombstones.Contains(candidate.Path))
        {
            return VacuumDecision.RetentionProtectedTombstone;
        }

        if (candidate.ModificationTimeMillis >= cutoffMillis)
        {
            return VacuumDecision.RecentlyStaged;
        }

        // Unreachable in practice: a candidate that is none of the above is deletable and never routed here.
        return VacuumDecision.RecentlyStaged;
    }

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
