using System.Diagnostics;
using System.Globalization;
using System.Linq;
using DeltaSharp.Storage.Backends;

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
        long start = Stopwatch.GetTimestamp();
        LogListing listing = await ListLogAsync(cancellationToken).ConfigureAwait(false);
        long latest = RequireLatest(listing);
        long target = ResolveExplicitVersionTarget(listing, latest, version);
        return await ReconstructAsync(listing, target, start, cancellationToken).ConfigureAwait(false);
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
    /// state; an <paramref name="asOf"/> at or after the latest commit resolves to the latest version.</para>
    /// </summary>
    /// <exception cref="DeltaProtocolException">The log is empty (not a Delta table); the timestamp predates
    /// the earliest retained commit (<see cref="DeltaProtocolErrorKind.RetentionGap"/>, AC3); or the resolved
    /// version cannot be reconstructed (malformed/gap/missing protocol — <see cref="DeltaProtocolErrorKind.InconsistentLog"/>).</exception>
    public async Task<TimeTravelResult> LoadSnapshotAsOfTimestampAsync(
        DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        long start = Stopwatch.GetTimestamp();
        LogListing listing = await ListLogAsync(cancellationToken).ConfigureAwait(false);
        _ = RequireLatest(listing);
        long resolved = ResolveTimestampTarget(listing, asOf);
        Snapshot snapshot = await ReconstructAsync(listing, resolved, start, cancellationToken).ConfigureAwait(false);
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
    /// an out-of-range error; a version <b>below</b> the earliest retained version is a retention gap (AC3).</summary>
    private static long ResolveExplicitVersionTarget(LogListing listing, long latest, long? version)
    {
        if (version is not { } requested)
        {
            return latest;
        }

        if (requested < 0 || requested > latest)
        {
            throw DeltaProtocolException.Inconsistent(string.Create(
                CultureInfo.InvariantCulture,
                $"Requested Delta version {requested} does not exist; the table has versions 0 through {latest}."));
        }

        long earliest = EarliestReconstructableVersion(listing);
        if (requested < earliest)
        {
            throw DeltaProtocolException.VersionNoLongerRetained(requested, earliest);
        }

        return requested;
    }

    /// <summary>Resolves a <c>timestampAsOf</c> request to the latest version whose <b>effective</b> commit
    /// timestamp (monotonic-adjusted <c>&lt;N&gt;.json</c> mtime) is at or before <paramref name="asOf"/>.
    /// Candidates are the retained commit files at or above the earliest reconstructable version, so the
    /// resolved version is always reconstructable. A timestamp before the earliest candidate fails closed
    /// with a retention gap (AC3); the effective timestamps are strictly increasing so the last qualifying
    /// version is the answer.</summary>
    private static long ResolveTimestampTarget(LogListing listing, DateTimeOffset asOf)
    {
        long floor = EarliestReconstructableVersion(listing);
        long[] candidates = listing.Commits.Where(v => v >= floor).OrderBy(v => v).ToArray();
        if (candidates.Length == 0)
        {
            throw DeltaProtocolException.RetentionGap(
                "No retained Delta commit files carry a timestamp; timestamp time travel is unavailable "
                + "(the JSON commits required to resolve a timestamp were removed by log cleanup).");
        }

        long asOfMillis = asOf.ToUnixTimeMilliseconds();
        long resolved = -1;
        long effectivePrevious = long.MinValue;
        long earliestEffective = 0;
        for (int i = 0; i < candidates.Length; i++)
        {
            long v = candidates[i];
            long mtime = ToEpochMillis(listing.CommitTimestamps[v]);
            // Delta monotonicity: force each commit strictly later than its predecessor so equal / out-of-order
            // file mtimes still yield a deterministic, strictly-increasing timeline.
            long effective = i == 0 ? mtime : Math.Max(mtime, effectivePrevious + 1);
            if (i == 0)
            {
                earliestEffective = effective;
            }

            if (effective > asOfMillis)
            {
                // Effective timestamps are strictly increasing, so no later version can qualify either.
                break;
            }

            resolved = v;
            effectivePrevious = effective;
        }

        if (resolved < 0)
        {
            throw DeltaProtocolException.TimestampBeforeRetention(
                asOf, candidates[0], DateTimeOffset.FromUnixTimeMilliseconds(earliestEffective));
        }

        return resolved;
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
    /// materialize, and fail closed on an unsupported protocol before serving.</summary>
    private async Task<Snapshot> ReconstructAsync(
        LogListing listing, long target, long startTimestamp, CancellationToken cancellationToken)
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
        int replayed = await ReplayContiguousAsync(state, replayStart, target, listing.Commits, cancellationToken)
            .ConfigureAwait(false);

        var metrics = new SnapshotLoadMetrics(
            CheckpointVersion: checkpointVersion,
            ReplayedCommitCount: replayed,
            ActiveFileCount: 0,
            LoadDuration: Stopwatch.GetElapsedTime(startTimestamp));

        Snapshot snapshot = state.ToSnapshot(target, metrics);

        // Protocol negotiation (§2.10.5): fail closed on an unsupported reader version/feature BEFORE the
        // snapshot is served to a scan — never read past a feature this build does not implement.
        ProtocolSupport.EnsureReadable(snapshot.Protocol);
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
            DeltaLogFile file = DeltaLogFiles.Classify(FileName(info.Path));
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
    /// requiring a contiguous chain (a missing version is a gap → fail closed).</summary>
    private async Task<int> ReplayContiguousAsync(
        SnapshotState state, long start, long target, IReadOnlySet<long> commits, CancellationToken cancellationToken)
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
            state.ApplyAll(DeltaLogActionReader.ParseCommit(content, v));
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
            string name = FileName(info.Path);
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
                    latest = Max(latest, file.Version);
                    break;

                case DeltaLogFileKind.ClassicCheckpoint:
                    if (!checkpoints.TryGetValue(file.Version, out CheckpointGroup? group))
                    {
                        group = new CheckpointGroup(file.Parts);
                        checkpoints[file.Version] = group;
                    }

                    group.Add(file.Part, file.Parts, info.Path);
                    latest = Max(latest, file.Version);
                    break;

                case DeltaLogFileKind.V2Checkpoint:
                    // Skipped: V2/UUID checkpoints are accepted only under the v2Checkpoint reader feature,
                    // which protocol negotiation rejects for a v1-baseline reader (§2.10.3/§2.10.5).
                    break;

                case DeltaLogFileKind.Other:
                default:
                    break;
            }
        }

        return new LogListing(commits, commitTimestamps, checkpoints, latest, hasHint);
    }

    private static long? Max(long? current, long candidate) =>
        current is { } value ? Math.Max(value, candidate) : candidate;

    /// <summary>The UTC modification time of a <c>&lt;N&gt;.json</c> object as Delta epoch milliseconds — the
    /// unit timestamp resolution and the monotonicity adjustment work in (mirrors VACUUM's stamping).</summary>
    private static long ToEpochMillis(DateTime lastModifiedUtc) =>
        new DateTimeOffset(DateTime.SpecifyKind(lastModifiedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

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
                        $"Delta log object '{path}' exceeds the {_maxLogObjectBytes}-byte read ceiling."));
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private static string FileName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string FormatVersion(long version) =>
        version.ToString(CultureInfo.InvariantCulture).PadLeft(VersionDigits, '0');

    /// <summary>The classic-checkpoint parts discovered for a single version, tracking completeness.</summary>
    private sealed class CheckpointGroup
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

    /// <summary>The discovered <c>_delta_log</c> contents: commit versions, each commit object's modification
    /// time (the timestamp-time-travel source, design §2.12.1), classic checkpoint groups, the latest
    /// reconstructable version, and whether a <c>_last_checkpoint</c> hint is present.</summary>
    private sealed record LogListing(
        SortedSet<long> Commits,
        IReadOnlyDictionary<long, DateTime> CommitTimestamps,
        Dictionary<long, CheckpointGroup> Checkpoints,
        long? LatestVersion,
        bool HasHint);
}
