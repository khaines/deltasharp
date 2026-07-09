using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;

namespace DeltaSharp.Storage.Tests.Delta.Simulation;

/// <summary>
/// A Jepsen-style commit-history checker (design §3.3.5, oracle d). Given the recorded
/// <see cref="HistoryEvent"/> log and the reconstructed <c>_delta_log</c> (reloaded via
/// <see cref="DeltaLog.LoadSnapshotAsync"/>), it validates the safety invariants the issue enumerates —
/// <b>I1</b> version monotonicity, <b>I2</b> single-winner, <b>I4</b> snapshot isolation, <b>I5</b>
/// read-your-writes, <b>I6</b> idempotent publication, and <b>I8</b> absence of illegal anomalies — plus
/// snapshot isolation explicitly. DeltaSharp targets snapshot isolation (not serializability), so the
/// legal-history predicate encodes SI, not global serial order.
///
/// <para><b>It trusts nothing the SUT self-reports.</b> Per §3.3.5 the blind-append property is
/// <b>recomputed</b> from the manifest's read-file-set (<see cref="ActionManifest.IsBlindAppend"/>), never
/// read from a flag; any <see cref="ActionManifest.SutReportedBlindAppend"/> is cross-checked against the
/// computed value. Ownership of each committed version is verified against the version's actual committed
/// content, so a writer that claims a version it did not durably own is caught.</para>
/// </summary>
internal static class JepsenHistoryChecker
{
    public static async Task<HistoryCheckResult> CheckAsync(
        ImmutableArray<HistoryEvent> history,
        IStorageBackend backend,
        ISet<long> backgroundVersions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(backgroundVersions);

        var violations = new List<HistoryViolation>();
        var log = new DeltaLog(backend);

        Snapshot final = await log.LoadSnapshotAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        long latest = final.Version;

        // Read every committed version's actions once (0 is the seed protocol/metadata commit).
        var commits = new Dictionary<long, CommittedVersion>();
        for (long v = 0; v <= latest; v++)
        {
            if (!await log.CommitExistsAsync(v, cancellationToken).ConfigureAwait(false))
            {
                violations.Add(new HistoryViolation("I1", $"version chain has a gap: commit {v} is missing (latest={latest})."));
                continue;
            }

            IReadOnlyList<DeltaAction> actions = await log.ReadCommitActionsAsync(v, cancellationToken).ConfigureAwait(false);
            commits[v] = CommittedVersion.From(v, actions);
        }

        CheckMonotonicity(commits, latest, violations);
        CheckSingleWinner(history, commits, backgroundVersions, latest, violations);
        await CheckSnapshotIsolationAsync(history, log, violations, cancellationToken).ConfigureAwait(false);
        await CheckReadYourWritesAsync(history, log, final, violations, cancellationToken).ConfigureAwait(false);
        CheckIdempotentPublication(commits, latest, violations);
        CheckNoAnomalies(commits, final, latest, violations);
        CheckConflictClassification(history, commits, backgroundVersions, latest, violations);

        return new HistoryCheckResult(violations.ToImmutableArray());
    }

    // ---- I1: contiguous versions 0..latest, one commit object per version ----
    private static void CheckMonotonicity(
        IReadOnlyDictionary<long, CommittedVersion> commits, long latest, List<HistoryViolation> violations)
    {
        for (long v = 0; v <= latest; v++)
        {
            if (!commits.ContainsKey(v))
            {
                violations.Add(new HistoryViolation("I1", $"missing commit object for version {v}; versions must be contiguous 0..{latest}."));
            }
        }
    }

    // ---- I2: each contended version has exactly one visible winner whose content is that winner's ----
    private static void CheckSingleWinner(
        ImmutableArray<HistoryEvent> history,
        IReadOnlyDictionary<long, CommittedVersion> commits,
        ISet<long> backgroundVersions,
        long latest,
        List<HistoryViolation> violations)
    {
        // No two acknowledged commits may claim the same version.
        var claimants = new Dictionary<long, List<int>>();
        foreach (HistoryEvent e in history.Where(e => e.Outcome == CommitOutcome.Committed && e.CommittedVersion is not null))
        {
            long version = e.CommittedVersion!.Value;
            if (!claimants.TryGetValue(version, out List<int>? list))
            {
                list = new List<int>();
                claimants[version] = list;
            }

            list.Add(e.ProcessId);
        }

        foreach ((long version, List<int> writers) in claimants)
        {
            if (writers.Count > 1)
            {
                violations.Add(new HistoryViolation(
                    "I2", $"version {version} was claimed as committed by {writers.Count} writers [{string.Join(", ", writers.Select(w => "w" + w))}] — single-winner violated."));
            }

            // The acknowledged winner's own declared adds must actually be the committed content of N (else
            // its ack was honored but its bytes were lost/overwritten — a single-winner/lost-update break).
            HistoryEvent owner = history.First(e =>
                e.Outcome == CommitOutcome.Committed && e.CommittedVersion == version && e.ProcessId == writers[0]);
            if (commits.TryGetValue(version, out CommittedVersion committed) && owner.Manifest is { } manifest)
            {
                foreach (ManifestFile add in manifest.Adds)
                {
                    if (!committed.AddPaths.Contains(add.Path))
                    {
                        violations.Add(new HistoryViolation(
                            "I2", $"writer w{owner.ProcessId} acknowledged a commit at version {version} but the committed content does not contain its file '{add.Path}' — its win was overwritten (single-winner violated)."));
                    }
                }
            }
        }

        // Every fresh version (not pre-seeded background) must have exactly one committing writer in history.
        for (long v = 0; v <= latest; v++)
        {
            if (backgroundVersions.Contains(v))
            {
                continue;
            }

            int owners = claimants.TryGetValue(v, out List<int>? w) ? w.Count : 0;
            if (owners == 0)
            {
                violations.Add(new HistoryViolation(
                    "I2", $"version {v} exists in the log but no writer acknowledged committing it — an unattributed (phantom) commit."));
            }
        }
    }

    // ---- I4 / snapshot isolation: a read pinned at R reflects exactly the state at R, nothing from >R ----
    private static async Task CheckSnapshotIsolationAsync(
        ImmutableArray<HistoryEvent> history, DeltaLog log, List<HistoryViolation> violations, CancellationToken cancellationToken)
    {
        foreach (HistoryEvent read in history.Where(e => e.Outcome is null))
        {
            long r = read.SnapshotReadVersion;
            Snapshot pinned = await log.LoadSnapshotAsync(r, cancellationToken).ConfigureAwait(false);
            var expected = pinned.ActiveFiles.Select(a => a.Path).OrderBy(p => p, StringComparer.Ordinal).ToImmutableArray();

            if (!expected.SequenceEqual(read.ObservedReadFiles, StringComparer.Ordinal))
            {
                violations.Add(new HistoryViolation(
                    "I4", $"writer w{read.ProcessId} read pinned@{r} observed [{string.Join(", ", read.ObservedReadFiles)}] but the reconstructed snapshot at {r} is [{string.Join(", ", expected)}] — a read leaked/omitted actions across the pin (snapshot isolation violated)."));
            }
        }
    }

    // ---- I5: after an acknowledged commit at N, a reader at >= N sees the writer's rows ----
    private static async Task CheckReadYourWritesAsync(
        ImmutableArray<HistoryEvent> history,
        DeltaLog log,
        Snapshot final,
        List<HistoryViolation> violations,
        CancellationToken cancellationToken)
    {
        var finalActive = final.ActiveFiles.Select(a => a.Path).ToImmutableHashSet(StringComparer.Ordinal);

        foreach (HistoryEvent e in history.Where(e => e.Outcome == CommitOutcome.Committed && e.CommittedVersion is not null && e.Manifest is not null))
        {
            long n = e.CommittedVersion!.Value;
            Snapshot atN = await log.LoadSnapshotAsync(n, cancellationToken).ConfigureAwait(false);
            var activeAtN = atN.ActiveFiles.Select(a => a.Path).ToImmutableHashSet(StringComparer.Ordinal);

            foreach (ManifestFile add in e.Manifest!.Adds)
            {
                if (!activeAtN.Contains(add.Path))
                {
                    violations.Add(new HistoryViolation(
                        "I5", $"writer w{e.ProcessId} committed '{add.Path}' at version {n} but a reader at {n} does not see it (read-your-writes violated)."));
                }

                // No later remove in this append-only model ⇒ it must also survive to the final snapshot.
                bool removedLater = e.Manifest.Removes.Any(r => string.Equals(r.Path, add.Path, StringComparison.Ordinal));
                if (!removedLater && !finalActive.Contains(add.Path))
                {
                    violations.Add(new HistoryViolation(
                        "I8", $"writer w{e.ProcessId}'s acknowledged file '{add.Path}' (committed at {n}) is absent from the final snapshot — a lost update."));
                }
            }
        }
    }

    // ---- I6: no committed file appears in two versions (a retry never duplicates rows/files) ----
    private static void CheckIdempotentPublication(
        IReadOnlyDictionary<long, CommittedVersion> commits, long latest, List<HistoryViolation> violations)
    {
        var seen = new Dictionary<string, long>(StringComparer.Ordinal);
        for (long v = 1; v <= latest; v++)
        {
            if (!commits.TryGetValue(v, out CommittedVersion c))
            {
                continue;
            }

            foreach (string path in c.AddPaths)
            {
                if (seen.TryGetValue(path, out long first))
                {
                    violations.Add(new HistoryViolation(
                        "I6", $"file '{path}' was published at both version {first} and version {v} — duplicated commit (idempotent publication violated)."));
                }
                else
                {
                    seen[path] = v;
                }
            }
        }
    }

    // ---- I8: every active file traces to a committed add; no phantom active files ----
    private static void CheckNoAnomalies(
        IReadOnlyDictionary<long, CommittedVersion> commits, Snapshot final, long latest, List<HistoryViolation> violations)
    {
        var everAdded = new HashSet<string>(StringComparer.Ordinal);
        for (long v = 1; v <= latest; v++)
        {
            if (commits.TryGetValue(v, out CommittedVersion c))
            {
                foreach (string path in c.AddPaths)
                {
                    everAdded.Add(path);
                }
            }
        }

        foreach (AddFileAction active in final.ActiveFiles)
        {
            if (!everAdded.Contains(active.Path))
            {
                violations.Add(new HistoryViolation(
                    "I8", $"active file '{active.Path}' in the final snapshot was never committed via an add — a phantom active file."));
            }
        }
    }

    // ---- §3.3.5: recompute isBlindAppend from the read-set; re-derive the expected conflict class ----
    private static void CheckConflictClassification(
        ImmutableArray<HistoryEvent> history,
        IReadOnlyDictionary<long, CommittedVersion> commits,
        ISet<long> backgroundVersions,
        long latest,
        List<HistoryViolation> violations)
    {
        foreach (HistoryEvent e in history.Where(e => e.Manifest is not null && e.Outcome is not null))
        {
            ActionManifest manifest = e.Manifest!;
            bool computedBlind = manifest.IsBlindAppend;

            // Cross-check any SUT-reported flag against the computed value (never trust it — §3.3.5).
            if (manifest.SutReportedBlindAppend is { } reported && reported != computedBlind)
            {
                violations.Add(new HistoryViolation(
                    "SI", $"writer w{e.ProcessId} reported isBlindAppend={reported} but the read-file-set implies {computedBlind} — the checker uses the computed value."));
            }

            // The winners this writer raced: committed versions after its read it does not own.
            long upper = e.Outcome == CommitOutcome.Committed && e.CommittedVersion is { } own ? own - 1 : latest;
            bool concurrentAdd = false;
            bool concurrentRemoveOrMeta = false;
            for (long v = e.SnapshotReadVersion + 1; v <= upper; v++)
            {
                if (e.CommittedVersion == v || !commits.TryGetValue(v, out CommittedVersion c))
                {
                    continue;
                }

                concurrentAdd |= c.AddPaths.Count > 0;
                concurrentRemoveOrMeta |= c.RemovePaths.Count > 0 || c.HasMetadata || c.HasProtocol;
            }

            if (computedBlind)
            {
                // A blind append reads nothing, so a concurrent append can never conflict with it — it must
                // rebase and succeed (Committed) or idempotently skip, never abort with a data conflict.
                if (e.Outcome == CommitOutcome.Conflict)
                {
                    violations.Add(new HistoryViolation(
                        "I2", $"writer w{e.ProcessId} is a blind append (empty read-set) yet was aborted with conflict '{e.ConflictClass}' — a blind append must rebase past concurrent appends, not conflict."));
                }
            }
            else if (e.Outcome == CommitOutcome.Conflict)
            {
                // A non-blind writer that conflicted must have raced a real concurrent change, and the class
                // must match: a concurrent add ⇒ ConcurrentAppend; a concurrent remove/meta ⇒ delete/read.
                if (!concurrentAdd && !concurrentRemoveOrMeta)
                {
                    violations.Add(new HistoryViolation(
                        "I8", $"writer w{e.ProcessId} aborted with '{e.ConflictClass}' but no concurrent change is present in the log after version {e.SnapshotReadVersion} — a spurious conflict."));
                }
                else if (concurrentAdd && e.ConflictClass?.Contains("ConcurrentAppend", StringComparison.Ordinal) != true)
                {
                    violations.Add(new HistoryViolation(
                        "I8", $"writer w{e.ProcessId} raced a concurrent append but reported conflict class '{e.ConflictClass}' (expected ConcurrentAppendException)."));
                }
            }
        }
    }

    private readonly record struct CommittedVersion(
        long Version,
        ImmutableHashSet<string> AddPaths,
        ImmutableHashSet<string> RemovePaths,
        bool HasMetadata,
        bool HasProtocol,
        string? Nonce)
    {
        public static CommittedVersion From(long version, IReadOnlyList<DeltaAction> actions)
        {
            var adds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            var removes = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            bool hasMetadata = false;
            bool hasProtocol = false;
            string? nonce = null;

            foreach (DeltaAction action in actions)
            {
                switch (action)
                {
                    case AddFileAction add:
                        adds.Add(add.Path);
                        break;
                    case RemoveFileAction remove:
                        removes.Add(remove.Path);
                        break;
                    case MetadataAction:
                        hasMetadata = true;
                        break;
                    case ProtocolAction:
                        hasProtocol = true;
                        break;
                    case CommitInfoAction info when info.Entries.TryGetValue(DeltaCommitter.CommitNonceKey, out string? value):
                        nonce = value;
                        break;
                    default:
                        break;
                }
            }

            return new CommittedVersion(version, adds.ToImmutable(), removes.ToImmutable(), hasMetadata, hasProtocol, nonce);
        }
    }
}

/// <summary>A single violated invariant discovered by the checker: its catalogue <see cref="Invariant"/>
/// id (I1/I2/I4/I5/I6/I8/SI) and a human-readable <see cref="Detail"/>.</summary>
internal readonly record struct HistoryViolation(string Invariant, string Detail)
{
    public override string ToString() =>
        "[" + Invariant + "] " + Detail;
}

/// <summary>The result of a Jepsen history check: legal iff <see cref="Violations"/> is empty.</summary>
internal sealed record HistoryCheckResult(ImmutableArray<HistoryViolation> Violations)
{
    /// <summary>Whether the history satisfies every checked invariant.</summary>
    public bool IsLegal => Violations.IsEmpty;

    /// <summary>The distinct invariant ids that were violated (e.g. <c>I2, I5</c>).</summary>
    public string ViolatedInvariants =>
        string.Join(", ", Violations.Select(v => v.Invariant).Distinct(StringComparer.Ordinal));

    /// <summary>A multi-line description of every violation, for an assertion message.</summary>
    public string Describe() =>
        Violations.IsEmpty
            ? "history is legal (all invariants hold)"
            : string.Join(Environment.NewLine, Violations.Select(v => v.ToString()));
}
