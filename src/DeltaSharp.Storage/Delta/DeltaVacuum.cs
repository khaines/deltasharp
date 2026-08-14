using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
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

    // #809: non-optional kill-switch. Default OFF = today's exact null-observer reconstruction + unconditional
    // in-window cdc scan (the correctness reference). When ON, VACUUM piggybacks an observer on its existing
    // reconstruction and elides the scan only when the log PROVES CDF was inactive across the full in-window
    // range; every uncertainty (un-proven coverage, inert observer, unaccountable lineage) falls back to SCAN.
    private readonly bool _cdcScanSkipEnabled;

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
        TimeProvider? timeProvider = null,
        bool cdcScanSkipEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _log = new DeltaLog(backend);
        _policy = policy ?? RetentionPolicy.Default;
        _logger = logger ?? NullLogger<DeltaVacuum>.Instance;
        _telemetry = telemetry ?? DeltaStorageTelemetry.Shared;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cdcScanSkipEnabled = cdcScanSkipEnabled;

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
            VacuumResult result = await RunAsync(retention, dryRun, unsafeOverride, activity, cancellationToken)
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
        catch (DeltaProtocolException ex) when (ex.Kind == DeltaProtocolErrorKind.StaleLogListing)
        {
            // #641 item 4: a tail-truncated / stale _delta_log listing (the #640 fail-closed guard). This is a
            // domain outcome, not a runtime fault; when the listing is merely stale it is RETRYABLE once the
            // listing propagates. Record a DISTINCT terminal (aborted_stale_listing) so an operator watching
            // metrics/logs can tell it apart from a generic Failure instead of paging on a false "log
            // corruption". The structured, sanitized log line was already emitted at the throw site (where the
            // two version numbers are in scope); it also warns that PERSISTENT recurrence indicates an
            // inconsistent/forged log, not a transient — see DeltaVacuumLog.VacuumAbortedStaleListing.
            _telemetry.RecordVacuumTerminal(
                VacuumOutcome.AbortedStaleListing, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
            SetOutcomeTag(activity, VacuumOutcome.AbortedStaleListing);
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
        TimeSpan? requestedRetention, bool dryRun, bool unsafeOverride, Activity? activity,
        CancellationToken cancellationToken)
    {
        // CRITICAL-2 (TOCTOU): LIST BEFORE LOAD SNAPSHOT. Delta requires listing files before reading the
        // log so the snapshot is at least as new as the listing: any file the listing shows that is active
        // is then guaranteed to appear in the later-loaded snapshot (and is protected), while any file
        // committed after the load was written after the list and is not a candidate at all. Loading first
        // would let a file committed in the load→list window appear in the listing but not the (older)
        // snapshot — with an mtime below the cutoff (clock skew / preserved-timestamp move / long copy) it
        // would bypass the recency fail-safe and be deleted. Listing first closes that window.
        //
        // NOTE (tracked): candidate discovery lists every object under the table root. Files referenced by a
        // NON-add.path/remove.path field are protected explicitly below: deletion-vector (.bin) sidecars from
        // the snapshot's DVs, and Change-Data-Feed (_change_data/) cdc files from the retained, in-window
        // commit JSONs (#489, see the log scan after the snapshot load) — never from the snapshot's
        // active/checkpoint state, which does not know cdc paths (INV C1).
        if (BeforeListProbe is { } beforeList)
        {
            await beforeList(cancellationToken).ConfigureAwait(false);
        }

        // Candidate discovery: list the table directory and keep every object except the _delta_log (the
        // log is metadata truth, never a reclamation target). Active files are deliberately NOT excluded
        // here — passing them through the contract lets it exclude them AND lets the audit record an
        // "active" decision for each, so the audit covers every discovered candidate (AC3).
        var candidates = new List<OrphanCandidate>();
        long maxListedLogVersion = -1;
        await foreach (StorageObjectInfo info in _backend.ListAsync(prefix: string.Empty, cancellationToken)
            .ConfigureAwait(false))
        {
            if (IsLogObject(info.Path))
            {
                // The table-root listing also enumerates `_delta_log/`. Track the highest VERSION among the
                // version-establishing log artifacts it saw — commits and classic checkpoints. It is an
                // independent (and, being first, EARLIER) view of the log than the one the snapshot is
                // reconstructed from below, so it lets us detect a tail-truncated log listing and fail closed
                // before deleting a live version's files. Extraction and the counted-kinds predicate reuse the
                // SAME helpers the snapshot's log listing uses (DeltaLogFiles.FileName + DeltaLogFile
                // .CountsTowardLatestVersion), so this candidate max and the snapshot's resolved LatestVersion
                // are computed byte-identically — a divergent extraction/kind set would let one see a version
                // the other misses (fail-open) or count one the other skips like a V2 checkpoint (false-abort).
                DeltaLogFile logFile = DeltaLogFiles.Classify(DeltaLogFiles.FileName(info.Path));
                if (logFile.CountsTowardLatestVersion && logFile.Version > maxListedLogVersion)
                {
                    maxListedLogVersion = logFile.Version;
                }

                continue;
            }

            candidates.Add(new OrphanCandidate(info.Path, DeltaTimestamps.ToEpochMillis(info.LastModifiedUtc)));
        }

        // Load the snapshot AND capture the single `_delta_log` listing it was reconstructed from. The
        // in-window cdc scan below reuses THIS listing rather than re-listing the log — two independent
        // listings can diverge (eventual consistency, a transient partial list, or a concurrent log
        // operation), and a staler second listing that omits an in-window commit would drop that commit's
        // referenced `cdc` paths from the protected set, deleting a live change file (data loss, #489).
        // #809: when the skip is enabled, piggyback an UNSEALED metadata observer on this SAME reconstruction
        // (no second replay) so the in-window cdc scan below can be elided when the log proves CDF was never
        // active in-window. When disabled, this is the byte-for-byte null-observer reconstruction of today.
        Snapshot snapshot;
        DeltaLog.LogListing logListing;
        ReplayedMetadataLog? cdcObserver = null;
        if (_cdcScanSkipEnabled)
        {
            (snapshot, logListing, cdcObserver) =
                await _log.LoadSnapshotWithListingAndObserverAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            (snapshot, logListing) =
                await _log.LoadSnapshotWithListingAsync(version: null, cancellationToken).ConfigureAwait(false);
        }

        // Fail closed on a tail-truncated log listing (red-team #640): the table-root candidate pass above is
        // an independent, earlier listing that also enumerated `_delta_log/`. If it saw a version-bearing log
        // artifact (a commit OR a checkpoint) beyond the version the snapshot resolved to, the single log
        // listing the snapshot (and the cdc scan) were built on was stale/partial and missed a live version —
        // whose data AND `_change_data/` files are on disk as candidates. Reclaiming them would be data loss
        // (they are referenced by a version VACUUM simply did not see). Abort rather than delete. A missing
        // MIDDLE commit is already caught fail-closed by snapshot reconstruction's gap detection; this closes
        // the tail. (Guards regular data files too, not just cdc — the divergence is between the candidate
        // listing and the log listing.)
        // ACCEPTED RESIDUAL (#641): a COMPOUND double-tear — the SAME log artifact invisible to BOTH the
        // candidate and the log listing while its data file stays listed and is already aged past retention —
        // is not caught here (maxListedLogVersion never advances). It is inherent to the #489 single-listing
        // invariant (fully closing it needs a second independent log read, the very divergence #489 forbids),
        // is strictly NARROWER than the pre-guard behavior, and is backstopped by the recency window (a
        // fresh unpropagated commit's files are RecentlyStaged, never deleted).
        if (maxListedLogVersion > snapshot.Version)
        {
            // Emit the structured, sanitized abort line HERE (both bounded version numbers are in scope) so
            // the log carries them as fields rather than an opaque pre-formatted detail string; the outer
            // catch classifies the distinct aborted_stale_listing terminal but no longer re-logs.
            DeltaVacuumLog.VacuumAbortedStaleListing(_logger, maxListedLogVersion, snapshot.Version);
            throw DeltaProtocolException.StaleLogListing(string.Create(
                CultureInfo.InvariantCulture,
                $"VACUUM aborted: the table root lists a _delta_log artifact at version {maxListedLogVersion} " +
                $"but the _delta_log listing resolved only to version {snapshot.Version}. The log listing is " +
                $"stale/partial (tail-truncated); reclaiming now could delete files referenced by the missing " +
                $"commit(s)/checkpoint(s)."));
        }

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

        // #489: protect cdc `_change_data/` files referenced by a retained, in-window `cdc` action. These are
        // referenced by a NON-add.path/remove.path field, and a `cdc` action is ignored by snapshot replay
        // (INV C1) and not retained in checkpoints — so the loaded snapshot cannot know their paths. Scan the
        // retained commit range (bounded by delta.logRetentionDuration, resolved from table config) for
        // AddCdcFileAction paths and pass them as an additional protected set into the orphan-cleanup
        // contract, mirroring how DV `.bin` sidecars are protected. A `_change_data/` file referenced only by
        // a commit aged past log retention (below the log cutoff) is correctly absent and stays reclaimable.
        TimeSpan logRetention = _policy.ResolveTableLogRetention(snapshot.Metadata.Configuration);
        long logRetentionCutoffMillis = nowMillis - (long)logRetention.TotalMilliseconds;

        // #489: the cdc scan reads every in-window commit JSON, so it is tempting to short-circuit it on the
        // candidate listing (skip when no candidate "looks like" a `_change_data/` file). That is NOT safe and
        // is deliberately avoided: OrphanCleanup protects a candidate when it matches ANY referenced `cdc`
        // path — regardless of that path's prefix or URI-encoding — and a `cdc` action's path is NOT
        // constrained to `_change_data/` (ParseCdc accepts any path). So no cheap candidate-path predicate can
        // be co-extensive with the protection: a double-encoded (`_change_data%252F…`) or non-canonical cdc
        // candidate would be protected-if-scanned yet skipped by any `_change_data/`-prefix predicate, and then
        // deleted (data loss). The only predicate that never under-protects is derived from the log itself, not
        // the candidate listing. Scanning unconditionally is the correctness reference; a SAFE scan-skip
        // (gated on the retained protocol history ever declaring CDF over the FULL in-window range, computed
        // from the log — not candidate paths, and not the current-snapshot enablement flag) remains an
        // ACCEPTED, un-implemented follow-up (#641 item 3, tracked in #809): a pure cost optimization over an already
        // fail-closed, single-listing scan, and the scan's cost is now observable (see the telemetry below).
        // The scan reuses `logListing` (the snapshot's own log view), so its protected set can never diverge
        // from — or be staler than — the listing the snapshot was built on.

        // #641 item 2: surface the cdc-scan cost (commit JSONs read + elapsed) on the vacuum activity and
        // metrics so an operator can see/alert on a scan whose cost grows with delta.logRetentionDuration
        // depth. The cost is recorded in a `finally` so a scan that THROWS or is CANCELLED mid-read still
        // reports the wall-clock it consumed: the expensive commit-JSON I/O has already been spent whether or
        // not the scan reached a result, so an absent signal would misattribute a costly failed scan as free
        // (or as "never reached" — the earlier claim here was wrong: a mid-scan throw DOES reach the scan yet
        // recorded nothing). On the throwing/cancelled path the completed-commit count is unknown (the scan
        // returns its count only on success), so it is reported as 0 with a bounded `completed=false` terminal
        // tag; the success path reports the true count with `completed=true`.
        IReadOnlyCollection<string> protectedChangeDataPaths;

        // #809: try the log-derived skip FIRST. It returns Skip only when the retained protocol history proves
        // CDF was inactive at BOTH boundaries of EVERY in-window commit (the scan's exact complement, keyed on
        // the scan's own logRetentionCutoffMillis); every uncertainty degrades to a scan reason. On Skip the
        // protected set is provably empty and the scan is elided — its cost histograms are NOT recorded (a skip
        // is not a zero-cost scan); the distinct skipped counter + EventId 4109 carry the signal instead.
        int provenInWindowCommits = 0;
        CdcScanDecision decision = cdcObserver is null
            ? CdcScanDecision.ScanDisabled
            : TryProveInWindowCdfNever(cdcObserver, logListing, logRetentionCutoffMillis, out provenInWindowCommits);
        if (decision == CdcScanDecision.Skip)
        {
            protectedChangeDataPaths = Array.Empty<string>();
            _telemetry.RecordVacuumCdcScanSkipped(activity);
            DeltaVacuumLog.VacuumCdcScanSkipped(
                _logger, provenInWindowCommits, ProvenRangeSpan(logListing, logRetentionCutoffMillis));
        }
        else
        {
            if (_cdcScanSkipEnabled)
            {
                _telemetry.RecordVacuumCdcScanScanned(activity, ScanReasonLabel(decision));
            }

            // #641 item 2: surface the cdc-scan cost (commit JSONs read + elapsed) on the vacuum activity and
            // metrics so an operator can see/alert on a scan whose cost grows with delta.logRetentionDuration
            // depth. The cost is recorded in a `finally` so a scan that THROWS or is CANCELLED mid-read still
            // reports the wall-clock it consumed: the expensive commit-JSON I/O has already been spent whether
            // or not the scan reached a result, so an absent signal would misattribute a costly failed scan as
            // free. On the throwing/cancelled path the completed-commit count is unknown (the scan returns its
            // count only on success), so it is reported as 0 with a bounded `completed=false` terminal tag; the
            // success path reports the true count with `completed=true`.
            long scanTimestamp = Stopwatch.GetTimestamp();
            int scanCommits = 0;
            bool scanCompleted = false;
            try
            {
                DeltaLog.InWindowChangeDataScan scan =
                    await _log.CollectInWindowChangeDataPathsAsync(logListing, logRetentionCutoffMillis, cancellationToken)
                        .ConfigureAwait(false);
                protectedChangeDataPaths = scan.Paths;
                scanCommits = scan.CommitsScanned;
                scanCompleted = true;
            }
            finally
            {
                double scanSeconds = Stopwatch.GetElapsedTime(scanTimestamp).TotalSeconds;
                _telemetry.RecordVacuumCdcScan(activity, scanCommits, scanSeconds, scanCompleted);
                DeltaVacuumLog.VacuumCdcScanCompleted(_logger, scanCommits, scanSeconds * 1000, scanCompleted);
            }
        }

        // The single source of the deletion decision AND the audit reason (design §2.11.5): active files,
        // retention-protected tombstones, referenced change-data files, and recently-staged files are
        // excluded fail-safe by the contract (encoding-robust) — VACUUM never re-implements or widens this.
        IReadOnlyList<OrphanDecision> classified =
            OrphanCleanup.Classify(snapshot, candidates, cutoffMillis, protectedChangeDataPaths);

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

    // #809: the tri-state outcome of the in-window cdc-scan skip decision, preserved through to telemetry so
    // an operator sees WHY a scan was not elided (a coverage regression vs. genuinely-active CDF vs. disabled).
    private enum CdcScanDecision
    {
        Skip,             // proven CDF-off at both boundaries of every in-window commit → elide the scan.
        ScanCdfPresent,   // CDF proven ON at a boundary → a cdc file may exist → scan.
        ScanUnproven,     // an in-window version is below the observer's coverage floor → scan (fail-closed).
        ScanUnprovenInert,// the observer went inert (#712 retention cap) → scan (fail-closed).
        ScanSealDegraded, // sealing the observer's lineage failed → scan (fail-closed).
        ScanDisabled,     // the skip kill-switch is OFF → unconditional scan (today's behavior).
    }

    /// <summary>
    /// #809: the log-derived, fail-closed skip predicate. Returns <see cref="CdcScanDecision.Skip"/> ONLY when
    /// the retained protocol/metadata history PROVES Change Data Feed was inactive — <c>IsEnabled</c> false —
    /// at BOTH the prevailing-before and prevailing-after boundary of EVERY in-window commit. The in-window set
    /// is the scan's EXACT complement (a commit is in-window unless its mtime is KNOWN and strictly below the
    /// scan's own <paramref name="logRetentionCutoffMillis"/>; an unknown-mtime commit is in-window, matching
    /// the scan's fail-safe). Any un-proven version (below the observer's coverage, or the observer inert), or a
    /// failure to seal the observer's lineage, degrades to a scan reason — the observer-free scan is VACUUM's
    /// correctness reference, so every uncertainty is safe to resolve by scanning. Derived SOLELY from the log
    /// (proven prevailing metadata); never the candidate listing, never the current-snapshot enablement flag.
    /// <para>Uses the property-only <see cref="ChangeDataFeedFeature.IsEnabled"/>: since a produced <c>cdc</c>
    /// action requires <see cref="ChangeDataFeedFeature.IsActive"/> (writer feature AND property), proving the
    /// PROPERTY off is a conservative superset proof that no cdc was produced — it can only ever cause an
    /// unnecessary scan, never a wrong skip.</para>
    /// </summary>
    private static CdcScanDecision TryProveInWindowCdfNever(
        ReplayedMetadataLog observer, DeltaLog.LogListing logListing, long logRetentionCutoffMillis,
        out int provenInWindowCommits)
    {
        provenInWindowCommits = 0;
        try
        {
            // Seal in the CONSUMER (the observer was returned unsealed): the lineage cross-check runs here, and
            // an unaccountable lineage → fail-closed SCAN, never a thrown VACUUM.
            observer.Seal();

            int inWindow = 0;
            foreach (long version in logListing.Commits)
            {
                if (IsAgedPastLogRetention(logListing, version, logRetentionCutoffMillis))
                {
                    continue; // not in-window (matches the scan's own skip) — nothing to prove.
                }

                if (!observer.TryGetProvenPrevailing(
                        version, out MetadataAction? before, out MetadataAction? after))
                {
                    // Below the observer's coverage floor, or the observer went inert (#712) → un-proven.
                    return observer.IsInert
                        ? CdcScanDecision.ScanUnprovenInert
                        : CdcScanDecision.ScanUnproven;
                }

                if (IsCdfPropertyOn(before) || IsCdfPropertyOn(after))
                {
                    return CdcScanDecision.ScanCdfPresent; // a cdc file may exist in-window → scan.
                }

                inWindow++;
            }

            provenInWindowCommits = inWindow;
            return CdcScanDecision.Skip; // every in-window commit proven CDF-off at both boundaries.
        }
        catch (DeltaProtocolException)
        {
            // Seal or a per-version corroboration failed closed → SCAN (the correctness reference), never a
            // thrown VACUUM. This is the seal-degrade backstop (also the forged-lineage backstop, §6).
            return CdcScanDecision.ScanSealDegraded;
        }
    }

    // The scan's EXACT in-window predicate, complemented: a commit is aged out (NOT in-window) iff its mtime is
    // KNOWN and strictly below the cutoff — so an unknown-mtime commit is in-window (fail-safe), keyed on the
    // scan's own logRetentionCutoffMillis (NOT the vacuum retention cutoff).
    private static bool IsAgedPastLogRetention(
        DeltaLog.LogListing logListing, long version, long logRetentionCutoffMillis) =>
        logListing.CommitTimestamps.TryGetValue(version, out DateTime modified)
        && DeltaTimestamps.ToEpochMillis(modified) < logRetentionCutoffMillis;

    // Property-only CDF enablement on a proven prevailing metadata; null prevailing (no metadata yet) is off.
    private static bool IsCdfPropertyOn(MetadataAction? metadata) =>
        metadata is not null && ChangeDataFeedFeature.IsEnabled(metadata.Configuration);

    // Bounded, value-type span of the in-window version range (for the path-free skip log), or 0 when empty.
    private static long ProvenRangeSpan(DeltaLog.LogListing logListing, long logRetentionCutoffMillis)
    {
        long min = long.MaxValue;
        long max = long.MinValue;
        foreach (long version in logListing.Commits)
        {
            if (IsAgedPastLogRetention(logListing, version, logRetentionCutoffMillis))
            {
                continue;
            }

            min = Math.Min(min, version);
            max = Math.Max(max, version);
        }

        return max >= min ? max - min + 1 : 0;
    }

    private static string ScanReasonLabel(CdcScanDecision decision) => decision switch
    {
        CdcScanDecision.ScanCdfPresent => "cdf_present",
        CdcScanDecision.ScanUnproven => "unproven_coverage",
        CdcScanDecision.ScanUnprovenInert => "unproven_inert",
        CdcScanDecision.ScanSealDegraded => "seal_degraded",
        CdcScanDecision.ScanDisabled => "disabled",
        _ => "disabled",
    };

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
            // Hive-path PII ruling: the audit LINE renders a description (sanitized file name + sanitized
            // partition COLUMN NAMES, no values); the audit ENTRY above keeps the raw path for the caller.
            //
            // The explicit IsEnabled gate is load-bearing, not belt-and-braces. [LoggerMessage] puts its
            // generated IsEnabled check INSIDE the generated method, so C# argument evaluation runs
            // DescribePath at the CALL SITE regardless of level. This is the one per-candidate (rather than
            // per-fault) DescribePath in the codebase, and it replaced a free field read with a scan +
            // Sanitize + join costing ~1.2 KB per candidate: a 1M-file VACUUM with Debug disabled — the
            // production default — would burn ~1.2 GB of transient Gen0 rendering lines nobody sees.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                DeltaVacuumLog.VacuumCandidateDecision(
                    _logger,
                    DiagnosticText.DescribePath(decision.Path),
                    DeltaStorageTelemetry.ToLabel(auditDecision),
                    wasDeleted);
            }
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
        OrphanClassification.ReferencedChangeData => VacuumDecision.ReferencedChangeData,
        _ => VacuumDecision.RecentlyStaged,
    };

    private void RecordDecisionCounts(ImmutableArray<VacuumAuditEntry> audit)
    {
        long deletable = 0, active = 0, tombstone = 0, staged = 0, referencedCdc = 0;
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
                case VacuumDecision.ReferencedChangeData:
                    referencedCdc++;
                    break;
                default:
                    staged++;
                    break;
            }
        }

        _telemetry.RecordVacuumFiles(VacuumDecision.Deletable, deletable);
        _telemetry.RecordVacuumFiles(VacuumDecision.Active, active);
        _telemetry.RecordVacuumFiles(VacuumDecision.RetentionProtectedTombstone, tombstone);
        _telemetry.RecordVacuumFiles(VacuumDecision.ReferencedChangeData, referencedCdc);
        _telemetry.RecordVacuumFiles(VacuumDecision.RecentlyStaged, staged);
    }

    private static void SetOutcomeTag(Activity? activity, VacuumOutcome outcome) =>
        activity?.SetTag(DeltaSharpTelemetry.OutcomeKey, DeltaStorageTelemetry.ToLabel(outcome));

    private static bool IsLogObject(string path) =>
        path.StartsWith(LogDirectoryPrefix, StringComparison.Ordinal) ||
        string.Equals(path, "_delta_log", StringComparison.Ordinal);
}
