using System.Diagnostics;
using System.Globalization;
using System.Linq;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Reads a Delta table's <c>_delta_log</c> from an <see cref="IStorageBackend"/> (rooted at the table
/// directory) and reconstructs an immutable <see cref="Snapshot"/> (design §2.4, §2.10.4). The active
/// file set comes only from committed log actions and checkpoints — never a data-directory listing.
///
/// <para><b>Checkpoint fast path (STORY-05.2.2).</b> When a usable classic checkpoint at version
/// <c>C ≤ target</c> exists, its surviving actions seed the initial state and only JSON commits
/// <c>(C, target]</c> are replayed (design §2.10.4), so open cost is O(commits-since-checkpoint) rather
/// than O(total history). The checkpoint is selected from the validated <c>_last_checkpoint</c> hint or,
/// if that is missing/stale, by listing the log; a <b>corrupt or partial checkpoint falls back to full
/// JSON replay from version 0</b> (design §2.10.3, STORY-05.2.2 AC2) — the reconstructed state is
/// identical either way (checkpoint-vs-JSON-replay parity, AC3). V2/UUID checkpoints are skipped here and
/// gated by protocol negotiation (§2.10.5).</para>
///
/// <para><b>Time travel (STORY-05.4.1, design §2.12.1).</b> <see cref="LoadSnapshotAsync(long?, CancellationToken)"/>
/// reconstructs the state at an <b>exact version</b> (<c>versionAsOf</c>); <see cref="LoadSnapshotAsOfTimestampAsync"/>
/// resolves a <b>timestamp</b> (<c>timestampAsOf</c>) to the latest version whose commit timestamp is at or
/// before it and reports the resolved version. Both fail closed on a target older than the earliest retained
/// log (<see cref="DeltaProtocolErrorKind.RetentionGap"/>) rather than returning current data, and both bound
/// checkpoint selection to <c>≤ target</c> so a <b>later</b> checkpoint/commit can never mutate historical
/// state (AC4).</para>
/// </summary>
internal sealed class DeltaLog
{
    private const string LogPrefix = "_delta_log/";
    private const int VersionDigits = DeltaLogFiles.VersionDigits;

    /// <summary>The maximum size of a single untrusted <c>_delta_log</c> object (a JSON commit or the
    /// <c>_last_checkpoint</c> hint) this reader will buffer (design §5.4 C-DECODE). An oversized/corrupt
    /// object fails closed rather than driving an unbounded read, mirroring the checkpoint part cap.</summary>
    internal const long MaxLogObjectBytes = 256L * 1024 * 1024;

    private readonly IStorageBackend _backend;
    private readonly long _maxLogObjectBytes;

    /// <summary>Creates a reader over <paramref name="backend"/>, which must be rooted at the Delta table
    /// directory (so <c>_delta_log/…</c> is reachable).</summary>
    public DeltaLog(IStorageBackend backend)
        : this(backend, MaxLogObjectBytes)
    {
    }

    /// <summary>Creates a reader with an explicit untrusted-object read ceiling (tests use a small ceiling
    /// to exercise the fail-closed bound without materializing a multi-hundred-MiB object).</summary>
    internal DeltaLog(IStorageBackend backend, long maxLogObjectBytes)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _maxLogObjectBytes = maxLogObjectBytes;
    }

    /// <summary>
    /// Loads the snapshot at <paramref name="version"/> (default: the latest committed version) —
    /// Spark-parity <c>versionAsOf</c> time travel (design §2.12.1; STORY-05.4.1 AC1).
    /// </summary>
    /// <exception cref="DeltaProtocolException">The log is empty (not a Delta table), the requested
    /// version is out of the <c>[0, latest]</c> range, the version chain has a gap, a commit is malformed,
    /// or the reconstructed state is missing a protocol/metaData action
    /// (<see cref="DeltaProtocolErrorKind.InconsistentLog"/>); or the requested version is below the earliest
    /// retained version because its log files were removed by log cleanup
    /// (<see cref="DeltaProtocolErrorKind.RetentionGap"/>, AC3).</exception>
    public async Task<Snapshot> LoadSnapshotAsync(long? version = null, CancellationToken cancellationToken = default)
    {
        (Snapshot snapshot, _) = await LoadSnapshotWithListingAsync(version, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>Loads the snapshot AND returns the single <see cref="LogListing"/> it was reconstructed from,
    /// so a caller (VACUUM) can drive further log-derived work — the in-window <c>cdc</c> scan
    /// (<see cref="CollectInWindowChangeDataPathsAsync"/>) — off the <b>same</b> listing rather than a second,
    /// independently-listed view of <c>_delta_log</c>. Two separate listings can diverge (an eventually
    /// consistent or transiently partial store, or a concurrent log operation), and a staler second listing
    /// that omits an in-window commit would silently drop that commit's referenced <c>cdc</c> path from the
    /// protected set — VACUUM would then delete a live change file (data loss, #489). Reusing one listing makes
    /// the cdc protection provably co-extensive with the snapshot's own log view.</summary>
    internal async Task<(Snapshot Snapshot, LogListing Listing)> LoadSnapshotWithListingAsync(
        long? version = null, CancellationToken cancellationToken = default)
    {
        long start = Stopwatch.GetTimestamp();
        LogListing listing = await ListLogAsync(cancellationToken).ConfigureAwait(false);
        Snapshot snapshot = await LoadSnapshotFromListingAsync(listing, version, start, null, cancellationToken)
            .ConfigureAwait(false);
        return (snapshot, listing);
    }

    /// <summary>Resolves and reconstructs a snapshot from an <b>already-obtained</b> <see cref="LogListing"/> —
    /// <see cref="LoadSnapshotWithListingAsync"/> minus the <c>_delta_log</c> LIST — so a caller holding a
    /// listing can load a second snapshot (or drive a listing-derived scan) without re-listing (#691).
    /// Resolution is identical to the listing-owning overload (<see cref="RequireLatest"/> +
    /// <see cref="ResolveExplicitVersionTarget"/>), so an out-of-range or retention-gapped
    /// <paramref name="version"/> fails closed with the SAME typed error. <paramref name="replayObserver"/>,
    /// when supplied, records the JSON commits this reconstruction actually replays (see
    /// <see cref="ReplayedMetadataLog"/>); passing <see langword="null"/> leaves the reconstruction path
    /// byte-for-byte unchanged.</summary>
    private async Task<Snapshot> LoadSnapshotFromListingAsync(
        LogListing listing, long? version, long startTimestamp, ReplayedMetadataLog? replayObserver,
        CancellationToken cancellationToken)
    {
        long latest = RequireLatest(listing);
        long target = ResolveExplicitVersionTarget(listing, latest, version);
        return await ReconstructAsync(listing, target, startTimestamp, replayObserver, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the snapshot as of <paramref name="asOf"/> — Spark-parity <c>timestampAsOf</c> time travel
    /// (design §2.12.1; STORY-05.4.1 AC2) — resolving the timestamp to a version and reporting it in the
    /// returned <see cref="TimeTravelResult"/>.
    ///
    /// <para><b>Resolution rule (Delta parity).</b> The selected version is the <b>latest</b> version whose
    /// effective commit timestamp is at or before <paramref name="asOf"/>. The commit timestamp for version
    /// <c>N</c> is the modification time of its <c>&lt;N&gt;.json</c> object
    /// (<see cref="StorageObjectInfo.LastModifiedUtc"/> from <see cref="IStorageBackend.ListAsync"/>),
    /// adjusted to be <b>strictly monotonic</b> — <c>eff(N) = max(mtime(N), eff(N-1) + 1ms)</c> — so equal or
    /// out-of-order file mtimes still resolve deterministically. This mirrors Delta's
    /// <c>DeltaHistoryManager.getActiveCommitAtTime</c> (which lists the delta files, adjusts monotonically,
    /// and picks the last commit ≤ the timestamp). Timestamps are compared in UTC.</para>
    ///
    /// <para>An <paramref name="asOf"/> before the earliest retained commit's effective timestamp fails
    /// closed with <see cref="DeltaProtocolErrorKind.RetentionGap"/> (AC3) rather than returning the earliest
    /// state. An <paramref name="asOf"/> at (inclusive) or between commit timestamps resolves to the latest
    /// version at or before it; an <paramref name="asOf"/> <b>strictly after the latest commit's</b> effective
    /// timestamp is out of range and, when <paramref name="canReturnLatest"/> is <see langword="false"/> (the
    /// default, matching Delta batch reads' <c>canReturnLastCommit=false</c>), fails closed with
    /// <see cref="DeltaProtocolErrorKind.TimestampAfterLatest"/> rather than silently clamping to current data.
    /// Pass <paramref name="canReturnLatest"/> <see langword="true"/> to opt into clamping a future timestamp
    /// to the latest version instead.</para>
    ///
    /// <para><b>Only retained <c>&lt;N&gt;.json</c> commits are timestamp-addressable</b> (mirroring Delta,
    /// which resolves against the listed delta json files): a version reachable only through a checkpoint —
    /// with no surviving commit file — carries no commit timestamp and cannot be selected by
    /// <c>timestampAsOf</c>.</para>
    /// </summary>
    /// <param name="asOf">The instant to resolve to a version (compared in UTC).</param>
    /// <param name="canReturnLatest">When <see langword="false"/> (default), a timestamp strictly after the
    /// latest commit fails closed; when <see langword="true"/>, it clamps to the latest version.</param>
    /// <param name="cancellationToken">Cancels the log listing and reconstruction I/O.</param>
    /// <exception cref="DeltaProtocolException">The log is empty (not a Delta table); the timestamp predates
    /// the earliest retained commit (<see cref="DeltaProtocolErrorKind.RetentionGap"/>, AC3); the timestamp is
    /// strictly after the latest commit and <paramref name="canReturnLatest"/> is <see langword="false"/>
    /// (<see cref="DeltaProtocolErrorKind.TimestampAfterLatest"/>); or the resolved version cannot be
    /// reconstructed (malformed/gap/missing protocol — <see cref="DeltaProtocolErrorKind.InconsistentLog"/>).</exception>
    public async Task<TimeTravelResult> LoadSnapshotAsOfTimestampAsync(
        DateTimeOffset asOf, bool canReturnLatest = false, CancellationToken cancellationToken = default)
    {
        long start = Stopwatch.GetTimestamp();
        LogListing listing = await ListLogAsync(cancellationToken).ConfigureAwait(false);
        _ = RequireLatest(listing);
        long resolved = ResolveTimestampTarget(listing, asOf, canReturnLatest);
        Snapshot snapshot = await ReconstructAsync(listing, resolved, start, null, cancellationToken).ConfigureAwait(false);
        return new TimeTravelResult(snapshot, resolved);
    }

    /// <summary>The latest reconstructable version, or a fail-closed "not a Delta table" error when the log
    /// has no commits or checkpoints.</summary>
    private static long RequireLatest(LogListing listing) =>
        listing.LatestVersion
        ?? throw DeltaProtocolException.Inconsistent(
            "No Delta commit files or checkpoints were found under _delta_log; the path is not a Delta table.");

    /// <summary>Validates and resolves an explicit <c>versionAsOf</c> request against the discovered log:
    /// <see langword="null"/> ⇒ latest; a negative version or one <b>above</b> <paramref name="latest"/> is
    /// an out-of-range error naming the available <c>[earliest, latest]</c> range; a version <b>below</b> the
    /// earliest retained version is a retention gap (AC3).</summary>
    private static long ResolveExplicitVersionTarget(LogListing listing, long latest, long? version)
    {
        if (version is not { } requested)
        {
            return latest;
        }

        long earliest = EarliestReconstructableVersion(listing);
        if (requested < 0 || requested > latest)
        {
            // Delta versionNotFound reports the actually-available range (not always 0-based), so a
            // log-cleaned table names its earliest retained version rather than an unreconstructable 0.
            throw DeltaProtocolException.Inconsistent(string.Create(
                CultureInfo.InvariantCulture,
                $"Requested Delta version {requested} does not exist; the table has versions {earliest} through {latest}."));
        }

        if (requested < earliest)
        {
            throw DeltaProtocolException.VersionNoLongerRetained(requested, earliest);
        }

        return requested;
    }

    /// <summary>The strict-monotonicity step (in milliseconds) each Delta commit's effective timestamp is
    /// forced past its predecessor's, so equal / out-of-order file mtimes still yield a strictly-increasing
    /// timeline (mirrors Delta's <c>DeltaHistoryManager</c> monotonic adjustment).</summary>
    private const long MonotonicStepMillis = 1;

    /// <summary>Resolves a <c>timestampAsOf</c> request to the latest version whose <b>effective</b> commit
    /// timestamp (monotonic-adjusted <c>&lt;N&gt;.json</c> mtime) is at or before <paramref name="asOf"/>.
    /// Candidates are the retained commit files at or above the earliest reconstructable version, so the
    /// resolved version is always reconstructable; a version reachable only through a checkpoint (no surviving
    /// <c>&lt;N&gt;.json</c>) carries no timestamp and is <b>not</b> timestamp-addressable, matching Delta.
    /// A timestamp before the earliest candidate fails closed (retention gap, or a first-commit error when the
    /// earliest candidate is version 0); a timestamp <b>strictly after the latest</b> commit fails closed with
    /// <see cref="DeltaProtocolErrorKind.TimestampAfterLatest"/> unless <paramref name="canReturnLatest"/> is
    /// set (then it clamps to latest). The effective timestamps are strictly increasing so the last qualifying
    /// version is the answer.</summary>
    private static long ResolveTimestampTarget(LogListing listing, DateTimeOffset asOf, bool canReturnLatest)
    {
        EffectiveCommitTimeline timeline = BuildEffectiveCommitTimeline(listing);
        if (timeline.Count == 0)
        {
            throw DeltaProtocolException.RetentionGap(
                "No retained Delta commit files carry a timestamp; timestamp time travel is unavailable "
                + "(the JSON commits required to resolve a timestamp were removed by log cleanup).");
        }

        long asOfMillis = asOf.ToUnixTimeMilliseconds();
        int resolvedIndex = -1;
        for (int i = 0; i < timeline.Count; i++)
        {
            if (timeline.EffectiveMillis[i] > asOfMillis)
            {
                // Effective timestamps are strictly increasing, so no later version can qualify either.
                break;
            }

            resolvedIndex = i;
        }

        if (resolvedIndex < 0)
        {
            long earliestCandidate = timeline.Versions[0];
            DateTimeOffset earliestTs = DateTimeOffset.FromUnixTimeMilliseconds(timeline.EffectiveMillis[0]);
            throw earliestCandidate == 0
                // v0 is retained: the timestamp is simply before the table's first commit, not log-cleaned.
                ? DeltaProtocolException.TimestampBeforeFirstCommit(asOf, earliestTs)
                // v0 was log-cleaned (earliest candidate > 0): earlier history was removed by log cleanup.
                : DeltaProtocolException.TimestampBeforeRetention(asOf, earliestCandidate, earliestTs);
        }

        // When the request is strictly after the latest commit's effective timestamp, `resolved` is the latest
        // candidate and its effective timestamp is `resolvedEffective`. Delta batch reads
        // (canReturnLastCommit=false) throw rather than silently clamp; keep parity unless the caller opts in.
        long resolved = timeline.Versions[resolvedIndex];
        long resolvedEffective = timeline.EffectiveMillis[resolvedIndex];
        long latestVersion = timeline.Versions[^1];
        if (resolved == latestVersion && asOfMillis > resolvedEffective && !canReturnLatest)
        {
            throw DeltaProtocolException.TimestampAfterLatest(
                asOf, latestVersion, DateTimeOffset.FromUnixTimeMilliseconds(resolvedEffective));
        }

        return resolved;
    }

    /// <summary>
    /// The <b>single source</b> of the Delta commit-timestamp policy (design §2.12.1) — the reconstructable
    /// commit versions (ascending) and each one's <b>effective</b> commit timestamp in epoch millis: the
    /// <c>&lt;N&gt;.json</c> object modification time forced <b>strictly monotonic</b> —
    /// <c>eff(N) = max(mtime(N), eff(N-1) + 1ms)</c> — so equal / out-of-order file mtimes still yield a
    /// deterministic, strictly-increasing timeline. It underpins BOTH <c>timestampAsOf</c> time travel
    /// (<see cref="ResolveTimestampTarget"/>) AND Change Data Feed range/timestamp resolution and
    /// <c>_commit_timestamp</c> stamping (<see cref="LoadChangeFeedLogAsync"/>, design §2.8) — computed once,
    /// here, so the two can never diverge (a stamped <c>_commit_timestamp</c> resolves back through
    /// <c>timestampAsOf</c> to the same version). Candidates are the retained commit files at or above the
    /// earliest reconstructable version; a version reachable only through a checkpoint (no surviving
    /// <c>&lt;N&gt;.json</c>) carries no timestamp and is excluded.
    /// </summary>
    private static EffectiveCommitTimeline BuildEffectiveCommitTimeline(LogListing listing)
    {
        long floor = EarliestReconstructableVersion(listing);
        // listing.Commits is a SortedSet<long>, so this enumerates ascending — the timeline order the
        // monotonic adjustment and the strictly-increasing-search invariants both rely on.
        long[] versions = listing.Commits.Where(v => v >= floor).ToArray();
        var effective = new long[versions.Length];
        long previous = long.MinValue;
        for (int i = 0; i < versions.Length; i++)
        {
            long mtime = DeltaTimestamps.ToEpochMillis(listing.CommitTimestamps[versions[i]]);
            long e = i == 0 ? mtime : Math.Max(mtime, previous + MonotonicStepMillis);
            effective[i] = e;
            previous = e;
        }

        return new EffectiveCommitTimeline(versions, effective);
    }

    /// <summary>
    /// Lists <c>_delta_log</c> once and returns the state a Change Data Feed range read (design §2.6) resolves
    /// against: the latest committed <see cref="ChangeFeedLog.LatestVersion"/> (the default range end), the
    /// <see cref="ChangeFeedLog.EarliestReconstructableVersion"/> floor (below it a range's <c>start</c> has
    /// aged past log retention — the CDF-readable-window lower bound, §2.6/CDF-EE-09), and the reconstructable
    /// commit versions with their <b>effective</b> commit timestamps (<see cref="BuildEffectiveCommitTimeline"/>)
    /// so a timestamp endpoint resolves — and every replayed version's <c>_commit_timestamp</c> is stamped —
    /// off the <b>same</b> monotonic <c>&lt;N&gt;.json</c>-mtime policy <c>timestampAsOf</c> uses (§2.8), never
    /// a second, divergent clock. One listing pass, so the feed's resolution is a consistent view of the log.
    /// </summary>
    /// <exception cref="DeltaProtocolException">The log is empty (not a Delta table).</exception>
    internal async Task<ChangeFeedLog> LoadChangeFeedLogAsync(CancellationToken cancellationToken)
    {
        LogListing listing = await ListLogAsync(cancellationToken).ConfigureAwait(false);
        long latest = RequireLatest(listing);
        long earliest = EarliestReconstructableVersion(listing);
        EffectiveCommitTimeline timeline = BuildEffectiveCommitTimeline(listing);
        var effectiveByVersion = new Dictionary<long, long>(timeline.Count);
        for (int i = 0; i < timeline.Count; i++)
        {
            effectiveByVersion[timeline.Versions[i]] = timeline.EffectiveMillis[i];
        }

        return new ChangeFeedLog(latest, earliest, timeline.Versions, timeline.EffectiveMillis, effectiveByVersion);
    }

    /// <summary>The earliest version whose snapshot can still be reconstructed from the retained log: version
    /// <c>0</c> when its commit survives, else the oldest complete classic checkpoint (a self-contained seed).
    /// Below this floor the required <c>&lt;N&gt;.json</c>/checkpoints were log-cleaned, so a request there is a
    /// retention gap (AC3) rather than a silent fallback to current data.</summary>
    private static long EarliestReconstructableVersion(LogListing listing)
    {
        if (listing.Commits.Contains(0))
        {
            return 0;
        }

        long? earliestCheckpoint = null;
        foreach (KeyValuePair<long, CheckpointGroup> entry in listing.Checkpoints)
        {
            if (entry.Value.IsComplete)
            {
                earliestCheckpoint = earliestCheckpoint is { } current ? Math.Min(current, entry.Key) : entry.Key;
            }
        }

        if (earliestCheckpoint is { } checkpoint)
        {
            return checkpoint;
        }

        // No surviving version 0 and no complete checkpoint: nothing below the earliest present commit is
        // reconstructable. Use it as the floor; reconstruction fails closed (gap) if the log is truly broken.
        return listing.Commits.Count > 0 ? listing.Commits.Min : (listing.LatestVersion ?? 0);
    }

    /// <summary>Reconstructs the immutable snapshot at <paramref name="target"/>: seed from the newest usable
    /// checkpoint <c>≤ target</c> (never a later one — AC4), replay JSON commits up to <paramref name="target"/>,
    /// materialize, and fail closed on an unsupported protocol before serving.
    /// <para><paramref name="replayObserver"/> (optional, <see langword="null"/> for every ordinary load) is a
    /// pure OBSERVER of the JSON replay: it records which commit versions this reconstruction actually read
    /// and the <c>metaData</c> actions they expressed, so a caller that must ALSO inspect those commits'
    /// metadata (the #671 change-feed pre-range column-mapping identity gate) can reuse this pass instead of
    /// reading the same commit objects a second time (#691). It cannot influence reconstruction.</para></summary>
    private async Task<Snapshot> ReconstructAsync(
        LogListing listing, long target, long startTimestamp, ReplayedMetadataLog? replayObserver,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CheckpointSelection> checkpoints =
            await SelectCheckpointsAsync(listing, target, cancellationToken).ConfigureAwait(false);

        var state = new SnapshotState();
        long? checkpointVersion = null;
        foreach (CheckpointSelection candidate in checkpoints)
        {
            long? seeded = await TrySeedFromCheckpointAsync(state, candidate, cancellationToken).ConfigureAwait(false);
            if (seeded is not null)
            {
                checkpointVersion = seeded;
                break;
            }

            // Corrupt/partial checkpoint: discard any partial seed and try the next-older complete
            // checkpoint before falling all the way back to JSON replay from version 0. This keeps a
            // log-cleaned table (early *.json VACUUMed) readable when only the newest checkpoint is corrupt.
            state = new SnapshotState();
        }

        long replayStart = checkpointVersion is { } c ? c + 1 : 0;
        int replayed = await ReplayContiguousAsync(
            state, replayStart, target, listing.Commits, replayObserver, cancellationToken).ConfigureAwait(false);

        var metrics = new SnapshotLoadMetrics(
            CheckpointVersion: checkpointVersion,
            ReplayedCommitCount: replayed,
            ActiveFileCount: 0,
            LoadDuration: Stopwatch.GetElapsedTime(startTimestamp));

        Snapshot snapshot = state.ToSnapshot(target, metrics);

        // Protocol negotiation (§2.10.5): fail closed on an unsupported reader version/feature BEFORE the
        // snapshot is served to a scan — never read past a feature this build does not implement.
        ProtocolSupport.EnsureReadable(snapshot.Protocol);

        // Column-mapping mode gate (§2.12.3; STORY-05.4.3 AC4). The protocol feature gate above opens for a
        // column-mapped (name OR id) table; this build serves BOTH 'name' and 'id' mode reads AND writes
        // (id-mode read/write resolve columns by Parquet field_id — #523/#572) but rejects a mode declared
        // without protocol support (protocol-upgrade error). This is the single read-side choke point, so
        // EVERY load (including time travel) of a column-mapping table declared without protocol support is
        // rejected before any data column is resolved — never a positional/name misread.
        ColumnMappingMode mode = ColumnMapping.ResolveMode(snapshot.Metadata.Configuration);
        ColumnMapping.EnsureModeGate(mode, snapshot.Protocol);

        // Name-mode resolution invariant (STORY-05.4.3 / #191 HIGH). Reject a poisoned/malformed name-mode
        // table (duplicate physicalName across data+partition fields, duplicate/missing/out-of-range id) —
        // and (#572 deltaspec N3/R4) a nested (non-leaf) mapped column or a non-positive id — fail-closed at
        // this single choke point: a duplicate physical name would otherwise let one column's value be served
        // under another column's logical name with NO exception (a silent misread).
        //
        // NOTE (#572 deltaspec N3/R4 finding #3, partitionColumns ⊆ schema): the all-mode partition-existence
        // invariant is enforced at the COMMITTER (DeltaCommitter.CommitCoreAsync) rather than here — validating
        // it at load is too broad, because a large corpus of hand-authored log/checkpoint fixtures uses a stub
        // schema that deliberately omits partition columns (they exercise log mechanics only). The committer
        // guarantees no NEW bad-partition metaData is published; a pre-existing raw-authored table still loads.
        ColumnMapping.ValidateColumnMappingSchema(mode, snapshot.Schema, snapshot.Metadata.Configuration);
        return snapshot;
    }

    /// <summary>The highest JSON-commit version currently visible in <c>_delta_log</c> (ignoring
    /// checkpoints), or <see langword="null"/> if the table has no commits. Used by the commit engine to
    /// find the latest committed version <c>M</c> after a lost put-if-absent race (design §2.11.2).</summary>
    internal async Task<long?> GetLatestCommitVersionAsync(CancellationToken cancellationToken)
    {
        long? latest = null;
        await foreach (StorageObjectInfo info in _backend.ListAsync(LogPrefix, cancellationToken).ConfigureAwait(false))
        {
            DeltaLogFile file = DeltaLogFiles.Classify(DeltaLogFiles.FileName(info.Path));
            if (file.Kind == DeltaLogFileKind.Commit)
            {
                latest = Max(latest, file.Version);
            }
        }

        return latest;
    }

    /// <summary>Whether the JSON commit file for <paramref name="version"/> exists (the existence probe used
    /// to walk the winning commits <c>(R, M]</c> and to re-resolve an ambiguous commit put, §2.11.3).</summary>
    internal async Task<bool> CommitExistsAsync(long version, CancellationToken cancellationToken) =>
        await _backend.HeadAsync(DeltaLogFiles.CommitPath(version), cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>Reads and parses the actions of the single JSON commit at <paramref name="version"/>. Used by
    /// the commit engine to classify a lost race and to identify its own commit during ambiguous-ack
    /// recovery (design §2.11.2/§2.11.3).</summary>
    /// <exception cref="DeltaStorageException">The commit object does not exist.</exception>
    /// <exception cref="DeltaProtocolException">The commit is malformed or exceeds the read ceiling.</exception>
    internal async Task<IReadOnlyList<DeltaAction>> ReadCommitActionsAsync(long version, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> content = await ReadAllAsync(DeltaLogFiles.CommitPath(version), cancellationToken).ConfigureAwait(false);
        return DeltaLogActionReader.ParseCommit(content, version);
    }

    /// <summary>
    /// Enumerates the table-root-relative paths of every <c>_change_data/</c> file referenced by an
    /// <see cref="AddCdcFileAction"/> in a <b>retained, in-window</b> commit JSON — a commit whose object
    /// modification time is at or after <paramref name="logRetentionCutoffMillis"/> (epoch millis), i.e.
    /// within <c>delta.logRetentionDuration</c>. This is the ONLY source for these paths: snapshot replay
    /// ignores <c>cdc</c> actions (§2.3, §3.3 INV C1) and checkpoints do not retain them, so the snapshot
    /// (active/checkpoint state) never knows a <c>cdc</c> file's path. VACUUM consumes this set to protect an
    /// in-window change file from reclamation (#489). A <c>cdc</c> file referenced only by a commit that has
    /// aged past log retention (below the cutoff — and therefore itself cleanable) is correctly absent here
    /// and remains reclaimable, so the protection is window-bounded, never unbounded. Fail-safe: a commit
    /// whose modification time is unknown is treated as in-window (protected), never dropped.
    /// <para>The caller passes the <paramref name="listing"/> that its snapshot was reconstructed from
    /// (<see cref="LoadSnapshotWithListingAsync"/>), so the scan operates on the <b>same</b> view of
    /// <c>_delta_log</c> as the snapshot — never a second, independently-listed view that could diverge and
    /// silently drop an in-window commit's <c>cdc</c> paths, under-protecting a live change file (#489).</para>
    /// </summary>
    /// <exception cref="DeltaProtocolException">A retained commit is malformed or exceeds the read ceiling.</exception>
    internal async Task<IReadOnlyCollection<string>> CollectInWindowChangeDataPathsAsync(
        LogListing listing, long logRetentionCutoffMillis, CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (long version in listing.Commits)
        {
            if (listing.CommitTimestamps.TryGetValue(version, out DateTime modified)
                && DeltaTimestamps.ToEpochMillis(modified) < logRetentionCutoffMillis)
            {
                continue; // known-aged past log retention → its cdc files are reclaimable, not protected.
            }

            foreach (DeltaAction action in await ReadCommitActionsAsync(version, cancellationToken).ConfigureAwait(false))
            {
                if (action is AddCdcFileAction cdc)
                {
                    paths.Add(cdc.Path);
                }
            }
        }

        return paths;
    }

    /// <summary>
    /// Loads the snapshot at a change-feed range's <paramref name="rangeStartVersion"/> AND — in the SAME
    /// log pass — validates the #671 pre-range column-mapping IDENTITY-immutability gate over
    /// <c>[earliest, rangeStartVersion - 1]</c>. Both were previously independent operations that each listed
    /// <c>_delta_log</c> and each read the SAME pre-range commit objects; fusing them makes the pre-range gate
    /// <b>listing-free</b> (it runs entirely off the caller's <paramref name="listing"/> — the one the END
    /// snapshot was reconstructed from) and collapses the double read of every pre-range commit to a single
    /// read (#691).
    /// <para><b>Coverage is unchanged</b> (see
    /// <see cref="ValidateColumnMappingIdentityStableBeforeAsync"/> for the proof): the start-snapshot
    /// reconstruction reports every commit it replays to a <see cref="ReplayedMetadataLog"/>, and the gate
    /// consumes those observations for exactly the versions the replay covered, explicitly READING every other
    /// retained pre-range commit — including a stray surviving strictly below a compacting checkpoint floor,
    /// which the replay never touches. Validation order is unchanged (baseline first, then ascending
    /// versions), so the fail-closed message still names the same version.</para>
    /// <para><b>Log-view coupling.</b> Because the gate runs off the listing carried by
    /// <paramref name="endView"/>, the pre-range window it validates is provably co-extensive with the log
    /// view the END identity it compares against came from — the <see cref="ChangeFeedEndView"/> type makes
    /// that a structural property rather than a caller convention. That is COVERAGE-SAFER (log cleanup only
    /// deletes, so the earlier listing's commit set is a SUPERSET of any later one's, and the gate can only
    /// validate more), but it is AVAILABILITY-COSTLIER: the listing is now consumed over one long window
    /// instead of three short ones, so a commit that concurrent cleanup deletes between the listing and the
    /// gate's read of it surfaces as a fail-closed read error rather than being silently absent from a fresh
    /// listing. That trade is deliberate — the gate exists to fail closed on anything it cannot verify.</para>
    /// <para><b>Fail ORDER changed (#691).</b> The start-snapshot reconstruction now runs BEFORE the pre-range
    /// gate (it is what feeds it). On a log that is BOTH protocol-illegal at the start version and forged
    /// earlier in history, the surfaced error is now the protocol/feature error rather than the identity
    /// error, so the "names the offending version" contract of the identity gate does not apply on that path.
    /// Both are fail-closed and both are path-free; only which one wins changed.</para>
    /// </summary>
    /// <exception cref="DeltaProtocolException">The start version is out of range / no longer retained, the
    /// log has a gap, a retained commit is malformed, the reconstruction did not land on the requested start
    /// version, or a retained version before the range declares a different (or unrecognized) column-mapping
    /// identity.</exception>
    internal async Task<Snapshot> LoadChangeFeedStartSnapshotAsync(
        ChangeFeedEndView endView, long rangeStartVersion, CancellationToken cancellationToken)
    {
        // Only PRE-range versions are observed: [start, end] is the reader's per-version gate, and bounding the
        // observation keeps the retained metadata proportional to the pre-range history's metadata revisions.
        long startTimestamp = Stopwatch.GetTimestamp();
        var replayed = new ReplayedMetadataLog(rangeStartVersion);
        Snapshot startSnapshot = await LoadSnapshotFromListingAsync(
            endView.Listing, rangeStartVersion, startTimestamp, replayed, cancellationToken)
            .ConfigureAwait(false);

        // The observer's exclusive upper bound is the REQUESTED start version, while the replay stops at the
        // version target resolution landed on. They are equal for a valid explicit version (the resolver is
        // the identity there — only the timestamp path clamps), but that equality is an assumption, and if it
        // ever drifted the failure mode would be a silent COVERAGE hole, not a compile error. Assert it.
        if (startSnapshot.Version != rangeStartVersion)
        {
            throw DeltaProtocolException.Inconsistent(string.Create(
                CultureInfo.InvariantCulture,
                $"A change-feed start-snapshot load for version {rangeStartVersion} reconstructed version "
                + $"{startSnapshot.Version} instead; the pre-range validation window can no longer be proven "
                + $"to match the range, so the read fails closed."));
        }

        await ValidateColumnMappingIdentityStableBeforeAsync(
            endView.Listing, rangeStartVersion, endView.EndMetadata, replayed, cancellationToken)
            .ConfigureAwait(false);
        return startSnapshot;
    }

    /// <summary>Loads the change-feed range's END snapshot together with the single <c>_delta_log</c> listing
    /// it was reconstructed from, minting the <see cref="ChangeFeedEndView"/> that
    /// <see cref="LoadChangeFeedStartSnapshotAsync"/> consumes. The view exists so "this end metadata and this
    /// listing came from the SAME snapshot load" is enforced by the type system rather than by a caller
    /// convention over two unrelated parameters.</summary>
    internal async Task<(Snapshot End, ChangeFeedEndView View)> LoadChangeFeedEndViewAsync(
        long? version, CancellationToken cancellationToken)
    {
        (Snapshot Snapshot, LogListing Listing) load =
            await LoadSnapshotWithListingAsync(version, cancellationToken).ConfigureAwait(false);
        return (load.Snapshot, ChangeFeedEndView.From(load));
    }

    /// <summary>
    /// Validates that the column-mapping IDENTITY — mode, per-column <c>delta.columnMapping.id</c> (field id),
    /// physical name, and the partition-column set — is immutable across the RETAINED history strictly BEFORE
    /// <paramref name="rangeStartVersion"/>, matching the end-of-range identity in
    /// <paramref name="endMetadata"/> (#671, scope broadened by the maintainer from mode-only to full identity).
    /// A change-feed read interprets every file it touches — including a file a <c>remove</c> in the range
    /// references but that was AUTHORED at any prior retained version — through the END snapshot's identity.
    /// Delta column-mapping identity is <b>immutable</b> (mode is sticky/creation-only; field ids and physical
    /// names are assigned once), so a historical version whose identity differs is a forged/illegal
    /// <c>_delta_log</c> — reading its file through the end identity would surface MISMAPPED change rows (a mode
    /// flip, a field-id reassignment, or a physical-name change). Scans the baseline identity at the earliest
    /// reconstructable version (compacted from a checkpoint when the creation commit has aged out) plus every
    /// retained commit's <c>metaData</c> before the range — including a transient change-and-change-back that
    /// reverted before <c>start</c> — and fails closed on any difference. Legitimate schema evolution (ADDING a
    /// column) is allowed: only a column present in BOTH a historical version and the end is identity-compared.
    /// The in-range <c>[start, end]</c> window is validated per-version by the change-feed reader, so this
    /// closes the pre-<c>start</c> gap the per-version check cannot see.
    /// <para><b>Scan source (#691).</b> <paramref name="alreadyReplayed"/> carries the commits the caller's
    /// start-snapshot reconstruction ALREADY read on this same <paramref name="listing"/>; a pre-range version
    /// it covers is validated from those observations instead of being re-read (the replay parsed the whole
    /// commit, so its <c>metaData</c> actions are exactly what a re-read would yield). Every pre-range commit
    /// the replay did NOT cover — always including a stray surviving strictly below the reconstructable floor,
    /// which no replay can reach — is still READ here. The validated set is therefore identical to a full
    /// re-scan: <c>{earliest}</c> (baseline snapshot) ∪ <c>{v ∈ listing.Commits : v &lt; start, v ≠ earliest}</c>,
    /// visited in ascending version order either way.</para>
    /// <para><b>Why an observation is never trusted on its silence (council R1).</b> This gate enforces a
    /// SECURITY property, so it must not infer "this version changed no identity" from an observer that merely
    /// SAYS nothing — that would make the gate vacuous if a future replay change under-reported. An observation
    /// is consumed only when the replay PROVABLY covered the version (contiguous covered window) and the record
    /// is corroborated by the reconstruction's own metadata lineage (state metadata reference changes across a
    /// version iff that version carried a <c>metaData</c>); see <see cref="ReplayedMetadataLog"/>. Everything
    /// else either falls back to the disk read or fails closed. Consequently the union of (proven, corroborated
    /// observations) and (disk reads) equals the full validation set BY CONSTRUCTION, and any defect in the
    /// observing seam can only cost performance or availability — never coverage.</para>
    /// <para><b>Residual (inherent).</b> An identity change recorded ONLY in commit JSON that has been DELETED
    /// (aged out) below the earliest reconstructable version is physically unreadable; a still-referenced
    /// below-floor file is then read under the uniform retained identity, which is equivalent to ordinary
    /// data-content forgery — no capability beyond the issue's already-stipulated <c>_delta_log</c>-write
    /// threat model. Every commit whose JSON SURVIVES — including a stray persisting strictly below a
    /// compacting checkpoint floor — IS scanned here (we skip only the exact baseline version); only truly
    /// deleted commit JSON is unvalidatable.</para>
    /// The fail-closed message is path-free (#653): it names ONLY the offending version.
    /// </summary>
    /// <exception cref="DeltaProtocolException">A retained version before the range declares a different (or
    /// unrecognized) column-mapping identity, or a retained commit is malformed / exceeds the read ceiling.</exception>
    private async Task ValidateColumnMappingIdentityStableBeforeAsync(
        LogListing listing, long rangeStartVersion, MetadataAction endMetadata,
        ReplayedMetadataLog alreadyReplayed, CancellationToken cancellationToken)
    {
        long earliest = EarliestReconstructableVersion(listing);
        if (earliest >= rangeStartVersion)
        {
            return; // No retained history before the range; the reader's per-version check covers [start, end].
        }

        ColumnMappingIdentity endIdentity = BuildIdentity(endMetadata);

        // Baseline: the identity as of the earliest reconstructable version — compacted from a checkpoint when
        // the creation commit has aged out, so a checkpoint-baked identity that no surviving commit re-expresses
        // is still caught. Reconstructed from the SAME listing (no second LIST, #691).
        Snapshot earliestSnapshot = await LoadSnapshotFromListingAsync(
            listing, earliest, Stopwatch.GetTimestamp(), null, cancellationToken).ConfigureAwait(false);
        ValidateHistoricalIdentity(earliest, earliestSnapshot.Metadata, endIdentity);

        // Every retained commit's metaData REPLACES the metadata (Delta semantics); a differing identity at any
        // version before the range — including a change-and-change-back that reverted before start, or a
        // SURVIVING commit whose JSON persists strictly below the earliest reconstructable floor (e.g. below a
        // compacting checkpoint) — is forged. We skip ONLY the exact baseline version (== earliest, already
        // validated above via the earliest snapshot); a below-floor stray (version < earliest) with a surviving
        // JSON is still read and validated, so a transient identity forged there cannot hide beneath a
        // checkpoint that bakes the reverted identity.
        foreach (long version in listing.Commits)
        {
            if (version == earliest || version >= rangeStartVersion)
            {
                continue; // == earliest folded into the baseline; >= start is the reader's per-version check.
            }

            cancellationToken.ThrowIfCancellationRequested();

            // #691: the start-snapshot reconstruction on THIS listing already read (and fully parsed) this
            // commit; reuse its metaData actions rather than issuing a second GET for the same immutable
            // object. The observation is consumable ONLY when the replay PROVABLY covered this version and its
            // record is corroborated by the reconstruction's own metadata lineage (see ReplayedMetadataLog);
            // anything else — a version the replay never reached (always the case for a sub-floor stray or a
            // commit below the seeding checkpoint) — falls back to the disk read below. An observer defect can
            // therefore cost a fail-closed read or an extra GET, but can NEVER shrink this validation set.
            IReadOnlyList<MetadataAction> versionMetadata =
                alreadyReplayed.TryGetProvenObservation(version, out IReadOnlyList<MetadataAction> observed)
                    ? observed
                    : ReplayedMetadataLog.MetadataActionsOf(
                        await ReadCommitActionsAsync(version, cancellationToken).ConfigureAwait(false));

            foreach (MetadataAction metadata in versionMetadata)
            {
                ValidateHistoricalIdentity(version, metadata, endIdentity);
            }
        }
    }

    // Fails closed if a historical version's column-mapping identity differs from the end's (mode,
    // partition-column set, or any COMMON column's field id / physical name). Path-free (#653): names only the
    // version. An unparseable schemaString is itself a forged/inconsistent log → fail closed.
    private static void ValidateHistoricalIdentity(
        long version, MetadataAction metadata, in ColumnMappingIdentity endIdentity)
    {
        if (!endIdentity.IsImmutableFrom(BuildIdentity(metadata)))
        {
            throw ColumnMappingIdentityNotImmutable(version);
        }
    }

    private static ColumnMappingIdentity BuildIdentity(MetadataAction metadata)
    {
        try
        {
            return ColumnMappingIdentity.FromMetadata(metadata);
        }
        catch (SchemaValidationException ex)
        {
            throw DeltaProtocolException.Malformed(
                "A change-feed range's metadata schemaString is unparseable or not a struct; the commit log "
                + "is inconsistent, so the read fails closed.", ex);
        }
    }

    private static DeltaProtocolException ColumnMappingIdentityNotImmutable(long version) =>
        DeltaProtocolException.Unsupported(string.Create(
            CultureInfo.InvariantCulture,
            $"The table's column-mapping identity (mode, field ids, physical names, or partition columns) is "
            + $"not immutable across retained history (version {version} differs from the end of the requested "
            + $"change-feed range); such a transition is protocol-illegal, so the change-feed read fails closed "
            + $"rather than risk emitting mismapped change data."));

    /// <summary>Seeds <paramref name="state"/> from the selected checkpoint's parts, returning its version,
    /// or <see langword="null"/> if the checkpoint is corrupt/partial (the caller then replays from 0).</summary>
    private async Task<long?> TrySeedFromCheckpointAsync(
        SnapshotState state, CheckpointSelection checkpoint, CancellationToken cancellationToken)
    {
        try
        {
            foreach (string partPath in checkpoint.PartPaths)
            {
                Stream stream = await _backend.OpenReadAsync(partPath, cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    state.ApplyAll(await DeltaCheckpointReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false));
                }
            }

            return checkpoint.Version;
        }
        catch (DeltaProtocolException)
        {
            // The checkpoint is non-authoritative (design §2.10.3): any decode failure falls back to JSON
            // replay rather than propagating, and never publishes half-built state.
            return null;
        }
    }

    /// <summary>Replays JSON commits <c>[start, target]</c> in ascending order into <paramref name="state"/>,
    /// requiring a contiguous chain (a missing version is a gap → fail closed). Each replayed version is
    /// reported to <paramref name="replayObserver"/> (when supplied) with its parsed actions AND the state's
    /// prevailing metadata immediately before/after applying them, so an observing caller sees exactly the
    /// commits this replay read and can corroborate every observation against the state it produced.</summary>
    private async Task<int> ReplayContiguousAsync(
        SnapshotState state, long start, long target, IReadOnlySet<long> commits,
        ReplayedMetadataLog? replayObserver, CancellationToken cancellationToken)
    {
        int replayed = 0;
        for (long v = start; v <= target; v++)
        {
            if (!commits.Contains(v))
            {
                throw DeltaProtocolException.Inconsistent(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Delta log is missing commit version {v} required to reconstruct version {target}; the log has a gap."));
            }

            string path = LogPrefix + FormatVersion(v) + ".json";
            ReadOnlyMemory<byte> content = await ReadAllAsync(path, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<DeltaAction> actions = DeltaLogActionReader.ParseCommit(content, v);
            MetadataAction? prevailingBefore = state.Metadata;
            state.ApplyAll(actions);
            replayObserver?.Observe(v, actions, prevailingBefore, state.Metadata);
            replayed++;
        }

        return replayed;
    }

    /// <summary>The usable classic checkpoints at version ≤ <paramref name="target"/>, ordered newest-first,
    /// so the caller seeds from the newest and — if it is corrupt — falls back to the next-older complete
    /// checkpoint before full JSON replay. The validated <c>_last_checkpoint</c> hint (when it names a
    /// complete checkpoint) is tried first; the rest follow in descending version order. Empty ⇒ full replay.</summary>
    private async Task<IReadOnlyList<CheckpointSelection>> SelectCheckpointsAsync(
        LogListing listing, long target, CancellationToken cancellationToken)
    {
        // All complete checkpoints ≤ target, newest first.
        var candidates = new List<CheckpointSelection>();
        foreach (long version in listing.Checkpoints.Keys.Where(v => v <= target).OrderByDescending(v => v))
        {
            CheckpointGroup group = listing.Checkpoints[version];
            if (group.IsComplete)
            {
                candidates.Add(new CheckpointSelection(version, group.OrderedPartPaths()));
            }
        }

        // Hint preference: if the (validated) hint names a complete checkpoint ≤ target, try it first.
        if (listing.HasHint
            && await ReadHintAsync(cancellationToken).ConfigureAwait(false) is { } hint
            && hint.Version <= target)
        {
            int hintIndex = candidates.FindIndex(c => c.Version == hint.Version);
            if (hintIndex > 0)
            {
                CheckpointSelection hinted = candidates[hintIndex];
                candidates.RemoveAt(hintIndex);
                candidates.Insert(0, hinted);
            }
        }

        return candidates;
    }

    private async Task<LastCheckpointHint?> ReadHintAsync(CancellationToken cancellationToken)
    {
        try
        {
            ReadOnlyMemory<byte> content = await ReadAllAsync(LastCheckpointHint.Path, cancellationToken).ConfigureAwait(false);
            return LastCheckpointHint.TryParse(content.Span);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The hint is advisory; any read/parse failure degrades to listing-based selection.
            return null;
        }
    }

    private async Task<LogListing> ListLogAsync(CancellationToken cancellationToken)
    {
        var commits = new SortedSet<long>();
        var commitTimestamps = new Dictionary<long, DateTime>();
        var checkpoints = new Dictionary<long, CheckpointGroup>();
        long? latest = null;
        bool hasHint = false;

        await foreach (StorageObjectInfo info in _backend.ListAsync(LogPrefix, cancellationToken).ConfigureAwait(false))
        {
            string name = DeltaLogFiles.FileName(info.Path);
            if (string.Equals(name, "_last_checkpoint", StringComparison.Ordinal))
            {
                hasHint = true;
                continue;
            }

            DeltaLogFile file = DeltaLogFiles.Classify(name);
            switch (file.Kind)
            {
                case DeltaLogFileKind.Commit:
                    commits.Add(file.Version);
                    // The <N>.json object modification time is the commit-timestamp source for timestamp
                    // time travel (design §2.12.1); capture it here where the listing is the single I/O pass.
                    commitTimestamps[file.Version] = info.LastModifiedUtc;
                    break;

                case DeltaLogFileKind.ClassicCheckpoint:
                    if (!checkpoints.TryGetValue(file.Version, out CheckpointGroup? group))
                    {
                        group = new CheckpointGroup(file.Parts);
                        checkpoints[file.Version] = group;
                    }

                    group.Add(file.Part, file.Parts, info.Path);
                    break;

                case DeltaLogFileKind.V2Checkpoint:
                    // Skipped: V2/UUID checkpoints are accepted only under the v2Checkpoint reader feature,
                    // which protocol negotiation rejects for a v1-baseline reader (§2.10.3/§2.10.5).
                    break;

                case DeltaLogFileKind.Other:
                default:
                    break;
            }

            // LatestVersion counts exactly the version-establishing artifacts (commits + classic checkpoints).
            // VACUUM's tail-truncation guard reuses this same DeltaLogFile.CountsTowardLatestVersion predicate
            // (and DeltaLogFiles.FileName) so the candidate pass's max version and this resolved latest are
            // computed identically — no asymmetry that could fail open (guard misses a version the snapshot
            // sees) or false-abort (guard counts one the snapshot skips).
            if (file.CountsTowardLatestVersion)
            {
                latest = Max(latest, file.Version);
            }
        }

        return new LogListing(commits, commitTimestamps, checkpoints, latest, hasHint);
    }

    private static long? Max(long? current, long candidate) =>
        current is { } value ? Math.Max(value, candidate) : candidate;

    private async Task<ReadOnlyMemory<byte>> ReadAllAsync(string path, CancellationToken cancellationToken)
    {
        Stream stream = await _backend.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                // A commit file / _last_checkpoint hint is untrusted input (design §5.4 C-DECODE); bound the
                // buffered read so an oversized/corrupt object fails closed rather than driving an unbounded
                // allocation, mirroring the checkpoint part cap.
                if (buffer.Length + read > _maxLogObjectBytes)
                {
                    throw DeltaProtocolException.Inconsistent(string.Create(
                        CultureInfo.InvariantCulture,
                        // #667 message hygiene: the log-object path is structural (_delta_log/<version>), but
                        // it is dropped for a uniform no-path posture; the bounded byte ceiling is the diagnosis.
                        $"A Delta log object exceeds the {_maxLogObjectBytes}-byte read ceiling."));
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private static string FormatVersion(long version) =>
        version.ToString(CultureInfo.InvariantCulture).PadLeft(VersionDigits, '0');

    /// <summary>The classic-checkpoint parts discovered for a single version, tracking completeness.</summary>
    internal sealed class CheckpointGroup
    {
        private readonly Dictionary<int, string> _partPaths = new();
        private int _parts;

        public CheckpointGroup(int parts) => _parts = parts;

        /// <summary>True once every declared part (1..N) has been seen.</summary>
        public bool IsComplete => _parts >= 1 && _partPaths.Count == _parts && AllPartsPresent();

        public void Add(int part, int parts, string path)
        {
            // Trust the largest declared part count seen (all parts declare the same N in a valid set).
            _parts = Math.Max(_parts, parts);
            _partPaths[part] = path;
        }

        public IReadOnlyList<string> OrderedPartPaths()
        {
            var ordered = new string[_parts];
            for (int p = 1; p <= _parts; p++)
            {
                ordered[p - 1] = _partPaths[p];
            }

            return ordered;
        }

        private bool AllPartsPresent()
        {
            for (int p = 1; p <= _parts; p++)
            {
                if (!_partPaths.ContainsKey(p))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>A resolved checkpoint to seed from: its <see cref="Version"/> and ordered part paths.</summary>
    private sealed record CheckpointSelection(long Version, IReadOnlyList<string> PartPaths);

    /// <summary>
    /// The END-of-range view a change-feed read validates against: the end snapshot's <c>metaData</c> AND the
    /// single <c>_delta_log</c> listing that snapshot was reconstructed from, bound together (#691, council
    /// R1 item 6). Only <see cref="LoadChangeFeedEndViewAsync"/> can mint one, so a caller cannot accidentally
    /// pair an end identity with a listing from a DIFFERENT log view — the "provably co-extensive" claim the
    /// pre-range gate's coverage argument rests on is structural, not a convention.
    /// </summary>
    internal readonly struct ChangeFeedEndView
    {
        private ChangeFeedEndView(MetadataAction endMetadata, LogListing listing)
        {
            EndMetadata = endMetadata;
            Listing = listing;
        }

        /// <summary>The end snapshot's <c>metaData</c> — the identity every earlier version is compared to.</summary>
        internal MetadataAction EndMetadata { get; }

        /// <summary>The log listing that same end snapshot was reconstructed from.</summary>
        internal LogListing Listing { get; }

        /// <summary>The ONLY way to build one: the end metadata is DERIVED from the snapshot in the same
        /// <see cref="LoadSnapshotWithListingAsync"/> result the listing came from, so the pair cannot be
        /// assembled from two unrelated log views. The constructor itself is private.</summary>
        internal static ChangeFeedEndView From((Snapshot Snapshot, LogListing Listing) load) =>
            new(load.Snapshot.Metadata, load.Listing);
    }

    /// <summary>The discovered <c>_delta_log</c> contents: commit versions, each commit object's modification
    /// time (the timestamp-time-travel source, design §2.12.1), classic checkpoint groups, the latest
    /// reconstructable version, and whether a <c>_last_checkpoint</c> hint is present.</summary>
    internal sealed record LogListing(
        SortedSet<long> Commits,
        IReadOnlyDictionary<long, DateTime> CommitTimestamps,
        Dictionary<long, CheckpointGroup> Checkpoints,
        long? LatestVersion,
        bool HasHint);

    /// <summary>The reconstructable commit versions (ascending) and each one's <b>effective</b> commit
    /// timestamp (epoch millis) after the strictly-monotonic <c>&lt;N&gt;.json</c>-mtime adjustment; the
    /// parallel arrays share indices. Built by <see cref="BuildEffectiveCommitTimeline"/>.</summary>
    private readonly record struct EffectiveCommitTimeline(long[] Versions, long[] EffectiveMillis)
    {
        public int Count => Versions.Length;
    }
}

/// <summary>The single-listing view a Change Data Feed range read resolves against
/// (<see cref="DeltaLog.LoadChangeFeedLogAsync"/>): the latest committed version (default range end), the
/// earliest reconstructable version (CDF-readable-window floor), and the reconstructable commit versions
/// (ascending) with their effective commit timestamps — as parallel lists <see cref="CommitVersions"/> /
/// <see cref="EffectiveMillis"/> plus the <see cref="EffectiveMillisByVersion"/> lookup used to stamp each
/// replayed version's <c>_commit_timestamp</c>. All timestamps come from
/// <c>BuildEffectiveCommitTimeline</c>, the same policy <c>timestampAsOf</c> uses.</summary>
internal sealed record ChangeFeedLog(
    long LatestVersion,
    long EarliestReconstructableVersion,
    IReadOnlyList<long> CommitVersions,
    IReadOnlyList<long> EffectiveMillis,
    IReadOnlyDictionary<long, long> EffectiveMillisByVersion);
