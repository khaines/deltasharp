using System.Diagnostics;
using System.Globalization;
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
/// </summary>
internal sealed class DeltaLog
{
    private const string LogPrefix = "_delta_log/";
    private const int VersionDigits = DeltaLogFiles.VersionDigits;

    private readonly IStorageBackend _backend;

    /// <summary>Creates a reader over <paramref name="backend"/>, which must be rooted at the Delta table
    /// directory (so <c>_delta_log/…</c> is reachable).</summary>
    public DeltaLog(IStorageBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
    }

    /// <summary>
    /// Loads the snapshot at <paramref name="version"/> (default: the latest committed version).
    /// </summary>
    /// <exception cref="DeltaProtocolException">The log is empty (not a Delta table), the requested
    /// version does not exist, the version chain has a gap, a commit is malformed, or the reconstructed
    /// state is missing a protocol/metaData action.</exception>
    public async Task<Snapshot> LoadSnapshotAsync(long? version = null, CancellationToken cancellationToken = default)
    {
        long start = Stopwatch.GetTimestamp();
        LogListing listing = await ListLogAsync(cancellationToken).ConfigureAwait(false);

        if (listing.LatestVersion is not { } latest)
        {
            throw DeltaProtocolException.Inconsistent(
                "No Delta commit files or checkpoints were found under _delta_log; the path is not a Delta table.");
        }

        long target = version ?? latest;
        if (version is { } requested && (requested < 0 || requested > latest))
        {
            throw DeltaProtocolException.Inconsistent(string.Create(
                CultureInfo.InvariantCulture,
                $"Requested Delta version {requested} does not exist; the table has versions 0 through {latest}."));
        }

        CheckpointSelection? checkpoint = await SelectCheckpointAsync(listing, target, cancellationToken).ConfigureAwait(false);

        var state = new SnapshotState();
        long? checkpointVersion = null;
        if (checkpoint is not null)
        {
            checkpointVersion = await TrySeedFromCheckpointAsync(state, checkpoint, cancellationToken).ConfigureAwait(false);
            if (checkpointVersion is null)
            {
                // Corrupt/partial checkpoint: discard any partial seed and replay from version 0.
                state = new SnapshotState();
            }
        }

        long replayStart = checkpointVersion is { } c ? c + 1 : 0;
        int replayed = await ReplayContiguousAsync(state, replayStart, target, listing.Commits, cancellationToken)
            .ConfigureAwait(false);

        var metrics = new SnapshotLoadMetrics(
            CheckpointVersion: checkpointVersion,
            ReplayedCommitCount: replayed,
            ActiveFileCount: 0,
            LoadDuration: Stopwatch.GetElapsedTime(start));

        return state.ToSnapshot(target, metrics);
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

    /// <summary>Chooses the newest usable classic checkpoint at version ≤ <paramref name="target"/>:
    /// preferring the validated <c>_last_checkpoint</c> hint, else the newest complete checkpoint found by
    /// listing. Returns null when no complete checkpoint applies (→ full replay).</summary>
    private async Task<CheckpointSelection?> SelectCheckpointAsync(
        LogListing listing, long target, CancellationToken cancellationToken)
    {
        LastCheckpointHint? hint = listing.HasHint
            ? await ReadHintAsync(cancellationToken).ConfigureAwait(false)
            : null;

        if (hint is { } h && h.Version <= target
            && listing.Checkpoints.TryGetValue(h.Version, out CheckpointGroup? hinted) && hinted.IsComplete)
        {
            return new CheckpointSelection(h.Version, hinted.OrderedPartPaths());
        }

        long best = -1;
        CheckpointGroup? bestGroup = null;
        foreach ((long checkpointVersion, CheckpointGroup group) in listing.Checkpoints)
        {
            if (checkpointVersion <= target && checkpointVersion > best && group.IsComplete)
            {
                best = checkpointVersion;
                bestGroup = group;
            }
        }

        return bestGroup is null ? null : new CheckpointSelection(best, bestGroup.OrderedPartPaths());
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

        return new LogListing(commits, checkpoints, latest, hasHint);
    }

    private static long? Max(long? current, long candidate) =>
        current is { } value ? Math.Max(value, candidate) : candidate;

    private async Task<ReadOnlyMemory<byte>> ReadAllAsync(string path, CancellationToken cancellationToken)
    {
        Stream stream = await _backend.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The discovered <c>_delta_log</c> contents: commit versions, classic checkpoint groups, the
    /// latest reconstructable version, and whether a <c>_last_checkpoint</c> hint is present.</summary>
    private sealed record LogListing(
        SortedSet<long> Commits,
        Dictionary<long, CheckpointGroup> Checkpoints,
        long? LatestVersion,
        bool HasHint);
}
