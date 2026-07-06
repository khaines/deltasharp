using System.Diagnostics;
using System.Globalization;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Reads a Delta table's <c>_delta_log</c> from an <see cref="IStorageBackend"/> (rooted at the table
/// directory) and reconstructs an immutable <see cref="Snapshot"/> (design §2.4, §2.10.4). The active
/// file set comes only from committed log actions — never a data-directory listing.
///
/// <para><b>Checkpoint use (STORY-05.2.2 optimization) is layered on separately:</b> this reader replays
/// the JSON commit chain from version 0. That is exactly the "missing/partial/corrupt checkpoint →
/// fall back safely to JSON replay" behavior the design mandates (§2.10.3, STORY-05.2.2 AC2); the
/// checkpoint-Parquet fast path (replay only newer commits) plugs into <see cref="ResolveSegmentAsync"/>
/// once the nested-checkpoint reader lands, without changing the replay contract.</para>
/// </summary>
internal sealed class DeltaLog
{
    private const string LogPrefix = "_delta_log/";
    private const int VersionDigits = 20;

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
        LogSegment segment = await ResolveSegmentAsync(version, cancellationToken).ConfigureAwait(false);

        var state = new SnapshotState();
        int replayed = 0;
        foreach (long commitVersion in segment.CommitVersions)
        {
            string path = LogPrefix + FormatVersion(commitVersion) + ".json";
            ReadOnlyMemory<byte> content = await ReadAllAsync(path, cancellationToken).ConfigureAwait(false);
            state.ApplyAll(DeltaLogActionReader.ParseCommit(content, commitVersion));
            replayed++;
        }

        var metrics = new SnapshotLoadMetrics(
            CheckpointVersion: segment.CheckpointVersion,
            ReplayedCommitCount: replayed,
            ActiveFileCount: 0,
            LoadDuration: Stopwatch.GetElapsedTime(start));

        return state.ToSnapshot(segment.TargetVersion, metrics);
    }

    /// <summary>
    /// Determines the ordered set of log versions to replay for a target version. Lists <c>_delta_log</c>,
    /// validates the JSON commit chain is contiguous from 0 through the target, and (today) selects no
    /// checkpoint — the checkpoint fast path plugs in here.
    /// </summary>
    private async Task<LogSegment> ResolveSegmentAsync(long? requested, CancellationToken cancellationToken)
    {
        var commitVersions = new SortedSet<long>();
        await foreach (StorageObjectInfo info in _backend.ListAsync(LogPrefix, cancellationToken).ConfigureAwait(false))
        {
            if (TryParseCommitVersion(FileName(info.Path), out long commitVersion))
            {
                commitVersions.Add(commitVersion);
            }
        }

        if (commitVersions.Count == 0)
        {
            throw DeltaProtocolException.Inconsistent(
                "No Delta commit files were found under _delta_log; the path is not a Delta table.");
        }

        long latest = commitVersions.Max;
        long target = requested ?? latest;
        if (requested is { } r && (r < 0 || r > latest))
        {
            throw DeltaProtocolException.Inconsistent(string.Create(
                CultureInfo.InvariantCulture,
                $"Requested Delta version {r} does not exist; the table has versions 0 through {latest}."));
        }

        // A snapshot at V requires a contiguous chain 0..V (no gap) when replaying without a checkpoint —
        // a missing intermediate commit means the reconstructed state would be wrong, so fail closed.
        var chain = new List<long>((int)Math.Min(target + 1, int.MaxValue));
        for (long v = 0; v <= target; v++)
        {
            if (!commitVersions.Contains(v))
            {
                throw DeltaProtocolException.Inconsistent(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Delta log is missing commit version {v} required to reconstruct version {target}; the log has a gap."));
            }

            chain.Add(v);
        }

        return new LogSegment(target, CheckpointVersion: null, chain);
    }

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

    /// <summary>True iff <paramref name="fileName"/> is a Delta JSON commit file
    /// (<c>&lt;20-digit-version&gt;.json</c>), yielding its <paramref name="version"/>.</summary>
    private static bool TryParseCommitVersion(string fileName, out long version)
    {
        version = 0;
        const string suffix = ".json";
        if (fileName.Length != VersionDigits + suffix.Length
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> digits = fileName.AsSpan(0, VersionDigits);
        foreach (char c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    private static string FormatVersion(long version) =>
        version.ToString(CultureInfo.InvariantCulture).PadLeft(VersionDigits, '0');

    /// <summary>The resolved log versions to replay for a target snapshot version, plus the checkpoint
    /// (if any) the initial state was seeded from.</summary>
    private sealed record LogSegment(long TargetVersion, long? CheckpointVersion, IReadOnlyList<long> CommitVersions);
}
