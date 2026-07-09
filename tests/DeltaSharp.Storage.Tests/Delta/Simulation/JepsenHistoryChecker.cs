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
/// read-your-writes, <b>I6</b> idempotent publication, and <b>I8</b> absence of illegal anomalies (a lost
/// update or a phantom/dangling active file) — plus snapshot isolation explicitly. DeltaSharp targets
/// snapshot isolation (not serializability), so the legal-history predicate encodes SI, not global serial
/// order.
///
/// <para><b>It trusts nothing the SUT self-reports.</b> Per §3.3.5 the blind-append property is
/// <b>recomputed</b> from the manifest's read-file-set (<see cref="ActionManifest.IsBlindAppend"/>), never
/// read from a flag; any <see cref="ActionManifest.SutReportedBlindAppend"/> is cross-checked against the
/// computed value. Ownership of each committed version is verified against the version's actual committed
/// content, so a writer that claims a version it did not durably own is caught.</para>
///
/// <para><b>Enforced invariants (each is falsifiable — a matching fault knob + efficacy test proves teeth):</b>
/// <list type="bullet">
/// <item><b>I1</b> (<see cref="CheckMonotonicity"/>): versions are contiguous <c>0..latest</c>. Contiguity is
/// probed BEFORE reconstruction so a version-chain gap is reported as I1 rather than crashing the loader.</item>
/// <item><b>I2</b> (<see cref="CheckSingleWinner"/>): exactly one writer owns each version and its bytes are
/// the committed content.</item>
/// <item><b>I4</b> (<see cref="CheckSnapshotIsolationAsync"/>): every real read — a pure read AND a non-blind
/// committing writer's read — observed exactly the snapshot at its pinned version.</item>
/// <item><b>I5</b> (<see cref="CheckReadYourWritesAsync"/>): an acknowledged file is visible at its commit
/// version.</item>
/// <item><b>I6</b> (<see cref="CheckIdempotentPublication"/>): no file — and no idempotency nonce — is
/// published in two versions.</item>
/// <item><b>I8</b> (<see cref="CheckReadYourWritesAsync"/> + <see cref="CheckNoAnomalies"/>): no acknowledged
/// file is lost (absent from the final snapshot without a later committed remove by ANY writer), and every
/// active file has a backing data object (no torn/dangling reference).</item>
/// <item><b>SI</b> (<see cref="CheckConflictClassificationAsync"/>): the §2.11.2 conflict class is re-derived
/// from the winners a writer actually raced <c>(R, M]</c> and cross-checked against what it reported. The
/// computed-vs-reported blind-append cross-check is tagged <b>SI</b>; a mis-derived conflict <i>class</i>
/// (e.g. a blind append aborting on a concurrent remove, or a remove-only race reported as an append) is
/// tagged <b>CC</b> so it is never confused with an I8 lost-update/phantom.</item>
/// </list></para>
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

        // ---- I1 (part 1): probe the version chain for a gap BEFORE any full reconstruction ----------------
        // GetLatestCommitVersionAsync lists commit objects and never throws on a gap, unlike
        // LoadSnapshotAsync (which raises DeltaProtocolException on a hole). We therefore determine the latest
        // and verify 0..latest are all present FIRST, so a gap is reported as a first-class I1 violation and
        // reconstruction — which cannot proceed past a hole — is skipped rather than crashing the checker.
        long? latestOpt = await log.GetLatestCommitVersionAsync(cancellationToken).ConfigureAwait(false);
        if (latestOpt is not { } latest)
        {
            violations.Add(new HistoryViolation("I1", "the table has no commit objects at all — version 0 is missing."));
            return new HistoryCheckResult(violations.ToImmutableArray());
        }

        var present = new HashSet<long>();
        for (long v = 0; v <= latest; v++)
        {
            if (await log.CommitExistsAsync(v, cancellationToken).ConfigureAwait(false))
            {
                present.Add(v);
            }
        }

        CheckMonotonicity(present, latest, violations);
        if (present.Count != latest + 1)
        {
            // A hole exists: the snapshot cannot be reconstructed past it, so stop after reporting I1 rather
            // than letting LoadSnapshotAsync throw an unhandled DeltaProtocolException.
            return new HistoryCheckResult(violations.ToImmutableArray());
        }

        Snapshot final = await log.LoadSnapshotAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Read every committed version's actions once (0 is the seed protocol/metadata commit).
        var commits = new Dictionary<long, CommittedVersion>();
        for (long v = 0; v <= latest; v++)
        {
            IReadOnlyList<DeltaAction> actions = await log.ReadCommitActionsAsync(v, cancellationToken).ConfigureAwait(false);
            commits[v] = CommittedVersion.From(v, actions);
        }

        var snapshots = new SnapshotCache(log);

        CheckSingleWinner(history, commits, backgroundVersions, latest, violations);
        await CheckSnapshotIsolationAsync(history, snapshots, commits, latest, violations, cancellationToken).ConfigureAwait(false);
        await CheckReadYourWritesAsync(history, snapshots, commits, final, latest, violations, cancellationToken).ConfigureAwait(false);
        CheckIdempotentPublication(commits, latest, violations);
        CheckNoAnomalies(final, backend, violations);
        await CheckConflictClassificationAsync(history, commits, latest, snapshots, violations, cancellationToken).ConfigureAwait(false);

        return new HistoryCheckResult(violations.ToImmutableArray());
    }

    // ---- I1: contiguous versions 0..latest, one commit object per version ----
    private static void CheckMonotonicity(ISet<long> present, long latest, List<HistoryViolation> violations)
    {
        for (long v = 0; v <= latest; v++)
        {
            if (!present.Contains(v))
            {
                violations.Add(new HistoryViolation(
                    "I1", $"version chain has a gap: commit object for version {v} is missing; versions must be contiguous 0..{latest}."));
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
        ImmutableArray<HistoryEvent> history,
        SnapshotCache snapshots,
        IReadOnlyDictionary<long, CommittedVersion> commits,
        long latest,
        List<HistoryViolation> violations,
        CancellationToken cancellationToken)
    {
        // The first version each path was ever added, across ALL commits — the raw material for the
        // partial-read "no leaked-from-the-future file" check. A file whose first add is at a version > R
        // cannot legally appear in a read pinned at R.
        var firstAddedAt = new Dictionary<string, long>(StringComparer.Ordinal);
        for (long v = 0; v <= latest; v++)
        {
            if (!commits.TryGetValue(v, out CommittedVersion c))
            {
                continue;
            }

            foreach (string path in c.AddPaths)
            {
                if (!firstAddedAt.ContainsKey(path))
                {
                    firstAddedAt[path] = v;
                }
            }
        }

        foreach (HistoryEvent e in history)
        {
            // Validate every event that performed a REAL read: a pure read (Outcome null) AND a committing
            // writer whose read-set is non-empty (a non-blind commit). The blind-append flag is COMPUTED from
            // the read-set (never the SUT flag): a blind append reads nothing by design, so there is nothing
            // to validate — comparing its empty read-set to the snapshot would false-positive.
            ImmutableArray<string> observed;
            bool wholeTableRead;
            if (e.Outcome is null)
            {
                // A pure read observes the ENTIRE active file set at its pin, so it is a whole-table read and
                // must equal the snapshot exactly.
                observed = e.ObservedReadFiles;
                wholeTableRead = true;
            }
            else if (e.Manifest is { } m && !m.IsBlindAppend)
            {
                observed = m.ReadFileSet.OrderBy(p => p, StringComparer.Ordinal).ToImmutableArray();
                wholeTableRead = m.ReadScope != ManifestReadScope.ReadFiles;
            }
            else
            {
                continue;
            }

            long r = e.SnapshotReadVersion;
            Snapshot pinned = await snapshots.GetAsync(r, cancellationToken).ConfigureAwait(false);
            var activeAtR = pinned.ActiveFiles.Select(a => a.Path).ToImmutableHashSet(StringComparer.Ordinal);
            var expected = activeAtR.OrderBy(p => p, StringComparer.Ordinal).ToImmutableArray();
            string kind = e.Outcome is null ? "read" : "committing writer's read";

            if (wholeTableRead)
            {
                // A whole-table read depends on every active file, so a reader that missed (or invented) any
                // active file across the pin IS a snapshot-isolation violation — require exact equality.
                if (!expected.SequenceEqual(observed, StringComparer.Ordinal))
                {
                    violations.Add(new HistoryViolation(
                        "I4", $"writer w{e.ProcessId} whole-table {kind} pinned@{r} observed [{string.Join(", ", observed)}] but the reconstructed snapshot at {r} is [{string.Join(", ", expected)}] — a read leaked/omitted actions across the pin (snapshot isolation violated)."));
                }
            }
            else
            {
                // A read-files (partial) read depends only on a NAMED subset, so a strict subset of the pinned
                // snapshot is legal. It is a violation only if it observed a file that is NOT in the snapshot
                // at R (a ghost) or that was first added at a version > R (a file leaked from the future).
                var leaked = observed.Where(p => !activeAtR.Contains(p)).ToList();
                var fromFuture = observed
                    .Where(p => firstAddedAt.TryGetValue(p, out long added) && added > r)
                    .ToList();
                if (leaked.Count > 0 || fromFuture.Count > 0)
                {
                    var offenders = leaked.Concat(fromFuture).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal);
                    violations.Add(new HistoryViolation(
                        "I4", $"writer w{e.ProcessId} read-files {kind} pinned@{r} observed [{string.Join(", ", observed)}] which is not a subset of the reconstructed snapshot at {r} = [{string.Join(", ", expected)}]: [{string.Join(", ", offenders)}] is a ghost or leaked-from-the-future file (snapshot isolation violated)."));
                }
            }
        }
    }

    // ---- I5 / I8: an acknowledged commit at N is visible at N and is not lost from the final snapshot ----
    private static async Task CheckReadYourWritesAsync(
        ImmutableArray<HistoryEvent> history,
        SnapshotCache snapshots,
        IReadOnlyDictionary<long, CommittedVersion> commits,
        Snapshot final,
        long latest,
        List<HistoryViolation> violations,
        CancellationToken cancellationToken)
    {
        var finalActive = final.ActiveFiles.Select(a => a.Path).ToImmutableHashSet(StringComparer.Ordinal);

        // Build a GLOBAL path -> versions-removed map from every commit's removes (across ALL writers), so a
        // legal delete of a file by a later, different writer is not misread as a lost update (§2.11.2).
        var removedIn = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        for (long v = 1; v <= latest; v++)
        {
            if (!commits.TryGetValue(v, out CommittedVersion c))
            {
                continue;
            }

            foreach (string path in c.RemovePaths)
            {
                if (!removedIn.TryGetValue(path, out List<long>? versions))
                {
                    versions = new List<long>();
                    removedIn[path] = versions;
                }

                versions.Add(v);
            }
        }

        foreach (HistoryEvent e in history.Where(e => e.Outcome == CommitOutcome.Committed && e.CommittedVersion is not null && e.Manifest is not null))
        {
            long n = e.CommittedVersion!.Value;
            Snapshot atN = await snapshots.GetAsync(n, cancellationToken).ConfigureAwait(false);
            var activeAtN = atN.ActiveFiles.Select(a => a.Path).ToImmutableHashSet(StringComparer.Ordinal);

            foreach (ManifestFile add in e.Manifest!.Adds)
            {
                if (!activeAtN.Contains(add.Path))
                {
                    violations.Add(new HistoryViolation(
                        "I5", $"writer w{e.ProcessId} committed '{add.Path}' at version {n} but a reader at {n} does not see it (read-your-writes violated)."));
                }

                // A file is "lost" only if it is absent from the final snapshot AND no committed remove for
                // that path exists in any version after N (in ANY writer's commit). A later legal delete is
                // NOT a lost update.
                if (!finalActive.Contains(add.Path))
                {
                    bool removedLater = removedIn.TryGetValue(add.Path, out List<long>? removeVersions)
                        && removeVersions.Any(rv => rv > n);
                    if (!removedLater)
                    {
                        violations.Add(new HistoryViolation(
                            "I8", $"writer w{e.ProcessId}'s acknowledged file '{add.Path}' (committed at {n}) is absent from the final snapshot and was never removed by a later commit — a lost update."));
                    }
                }
            }
        }
    }

    // ---- I6: no committed file — and no idempotency nonce — appears in two versions (a retry never dupes) ----
    private static void CheckIdempotentPublication(
        IReadOnlyDictionary<long, CommittedVersion> commits, long latest, List<HistoryViolation> violations)
    {
        var seenFile = new Dictionary<string, long>(StringComparer.Ordinal);
        var seenNonce = new Dictionary<string, long>(StringComparer.Ordinal);
        for (long v = 1; v <= latest; v++)
        {
            if (!commits.TryGetValue(v, out CommittedVersion c))
            {
                continue;
            }

            foreach (string path in c.AddPaths)
            {
                if (seenFile.TryGetValue(path, out long first))
                {
                    violations.Add(new HistoryViolation(
                        "I6", $"file '{path}' was published at both version {first} and version {v} — duplicated commit (idempotent publication violated)."));
                }
                else
                {
                    seenFile[path] = v;
                }
            }

            // A commit nonce (Delta txnId) is the writer's exactly-once token; the same nonce landing in two
            // versions is a true double-publish (a retry that committed twice), so nonces must be unique.
            if (c.Nonce is { } nonce)
            {
                if (seenNonce.TryGetValue(nonce, out long firstNonce))
                {
                    violations.Add(new HistoryViolation(
                        "I6", $"commit nonce '{nonce}' was published at both version {firstNonce} and version {v} — the same commit landed twice (idempotent publication violated)."));
                }
                else
                {
                    seenNonce[nonce] = v;
                }
            }
        }
    }

    // ---- I8: every active file has a backing data object; none is a phantom/dangling reference ----
    private static void CheckNoAnomalies(Snapshot final, IStorageBackend backend, List<HistoryViolation> violations)
    {
        // A phantom active file is one the final snapshot lists as active but whose backing data object was
        // never durably written (a torn write: the commit's add references data that does not exist). The
        // in-memory backend stages a backing object for every add at write time, so a missing object here is
        // a genuine dangling reference. (Only enforced against the simulation's own backend, which records
        // staged data objects.)
        if (backend is not InMemoryStorageBackend sim)
        {
            return;
        }

        foreach (AddFileAction active in final.ActiveFiles)
        {
            if (!sim.HasObject(active.Path))
            {
                violations.Add(new HistoryViolation(
                    "I8", $"active file '{active.Path}' in the final snapshot has no backing data object — a phantom/dangling reference (torn write)."));
            }
        }
    }

    // ---- §3.3.5: recompute isBlindAppend from the read-set; re-derive the expected conflict class ----
    private static async Task CheckConflictClassificationAsync(
        ImmutableArray<HistoryEvent> history,
        IReadOnlyDictionary<long, CommittedVersion> commits,
        long latest,
        SnapshotCache snapshots,
        List<HistoryViolation> violations,
        CancellationToken cancellationToken)
    {
        foreach (HistoryEvent e in history.Where(e => e.Manifest is not null && e.Outcome is not null))
        {
            ActionManifest manifest = e.Manifest!;
            bool computedBlind = manifest.IsBlindAppend;

            // Cross-check any SUT-reported flag against the computed value (never trust it — §3.3.5). A
            // whole-table read over an EMPTY table has an empty read-set too, so computedBlind is spuriously
            // true there; suppress the mismatch when the table was empty at the read version (there is no way
            // to distinguish a true blind append from a whole-table scan of nothing).
            if (manifest.SutReportedBlindAppend is { } reported && reported != computedBlind)
            {
                Snapshot pinned = await snapshots.GetAsync(e.SnapshotReadVersion, cancellationToken).ConfigureAwait(false);
                bool emptyAtRead = pinned.ActiveFiles.IsEmpty;
                if (!(computedBlind && emptyAtRead))
                {
                    violations.Add(new HistoryViolation(
                        "SI", $"writer w{e.ProcessId} reported isBlindAppend={reported} but the read-file-set implies {computedBlind} — the checker uses the computed value."));
                }
            }

            // The winners this writer raced: committed versions in (R, M] it does not own, where M is the
            // writer's observed latest-at-abort (design §2.11.2). Bounding to M rather than the log's final
            // latest avoids attributing versions committed AFTER this writer aborted as ones it raced.
            long own = e.Outcome == CommitOutcome.Committed && e.CommittedVersion is { } committed ? committed : -1;
            long m = e.ObservedLatestVersion ?? latest;
            long upper = own >= 0 ? own - 1 : m;

            // Re-derive each concurrent-change class SEPARATELY (never bundled): the real DeltaConflictChecker
            // ranks winner protocol > winner metadata > loser protocol > loser metadata > same-appId txn >
            // read-scope data conflict (add/remove). Bundling remove with metadata (as an earlier revision
            // did) both let a blind append illegitimately abort on a concurrent remove AND masked a
            // remove-only race misreported as the wrong class.
            var readSet = manifest.ReadFileSet.ToImmutableHashSet(StringComparer.Ordinal);
            bool winnerAdd = false;
            bool winnerRemove = false;
            bool winnerMeta = false;
            bool winnerProtocol = false;
            bool concurrentSameTxn = false;
            bool readPathRemoved = false;
            bool readPathReadded = false;
            for (long v = e.SnapshotReadVersion + 1; v <= upper; v++)
            {
                if (v == own || !commits.TryGetValue(v, out CommittedVersion c))
                {
                    continue;
                }

                winnerAdd |= c.AddPaths.Count > 0;
                winnerRemove |= c.RemovePaths.Count > 0;
                winnerMeta |= c.HasMetadata;
                winnerProtocol |= c.HasProtocol;
                readPathRemoved |= c.RemovePaths.Overlaps(readSet);
                readPathReadded |= c.AddPaths.Overlaps(readSet);
                if (manifest.Txn is { } txn && c.AppIds.Contains(txn.AppId))
                {
                    concurrentSameTxn = true;
                }
            }

            if (e.Outcome != CommitOutcome.Conflict)
            {
                continue;
            }

            // The set of conflict-class tokens the real committer could legitimately have raised for this
            // race, following the precedence above. An EMPTY set means no legitimate conflict exists — the
            // writer should have rebased, so an abort here is spurious.
            var acceptable = new List<string>();
            if (winnerProtocol || manifest.HasProtocolChange)
            {
                acceptable.Add("ProtocolChanged");
            }
            else if (winnerMeta || manifest.HasMetadataChange)
            {
                acceptable.Add("MetadataChanged");
            }
            else if (concurrentSameTxn)
            {
                acceptable.Add("ConcurrentTransaction");
            }
            else if (!computedBlind)
            {
                // A non-blind data conflict: a whole-table read conflicts with any concurrent add (checked
                // first by the real scope) else any concurrent remove; a read-files read conflicts only where
                // a winner touched one of its READ paths (a concurrent add/remove of an unread file is not a
                // conflict for it — it must rebase).
                if (manifest.ReadScope == ManifestReadScope.WholeTable)
                {
                    if (winnerAdd)
                    {
                        acceptable.Add("ConcurrentAppend");
                    }
                    else if (winnerRemove)
                    {
                        acceptable.Add("ConcurrentDeleteRead");
                    }
                }
                else
                {
                    if (readPathRemoved)
                    {
                        acceptable.Add("ConcurrentDeleteRead");
                    }

                    if (readPathReadded)
                    {
                        acceptable.Add("ConcurrentAppend");
                    }
                }
            }

            if (acceptable.Count == 0)
            {
                // No legitimate concurrent conflict: the writer must have rebased-and-retried, not aborted.
                if (computedBlind)
                {
                    violations.Add(new HistoryViolation(
                        "CC", $"writer w{e.ProcessId} is a blind append (empty read-set) yet aborted with conflict '{e.ConflictClass}' — a blind append must rebase past a concurrent append/remove (only a metadata/protocol change or a same-appId txn may abort it)."));
                }
                else
                {
                    violations.Add(new HistoryViolation(
                        "CC", $"writer w{e.ProcessId} aborted with '{e.ConflictClass}' but no legitimate concurrent conflict exists for its read scope ({manifest.ReadScope}) in (version {e.SnapshotReadVersion}, {m}] — a spurious conflict (it should have rebased)."));
                }
            }
            else if (!acceptable.Any(token => e.ConflictClass?.Contains(token, StringComparison.Ordinal) == true))
            {
                violations.Add(new HistoryViolation(
                    "CC", $"writer w{e.ProcessId} aborted reporting conflict class '{e.ConflictClass}', but the race it lost in (version {e.SnapshotReadVersion}, {m}] re-derives to [{string.Join(" | ", acceptable)}] (expected exception name to contain one of those)."));
            }
        }
    }

    /// <summary>A small memoizing loader so each version's pinned snapshot is reconstructed at most once
    /// across the SI and conflict-class checks.</summary>
    private sealed class SnapshotCache
    {
        private readonly DeltaLog _log;
        private readonly Dictionary<long, Snapshot> _cache = new();

        public SnapshotCache(DeltaLog log) => _log = log;

        public async Task<Snapshot> GetAsync(long version, CancellationToken cancellationToken)
        {
            if (!_cache.TryGetValue(version, out Snapshot? snapshot))
            {
                snapshot = await _log.LoadSnapshotAsync(version, cancellationToken).ConfigureAwait(false);
                _cache[version] = snapshot;
            }

            return snapshot;
        }
    }

    private readonly record struct CommittedVersion(
        long Version,
        ImmutableHashSet<string> AddPaths,
        ImmutableHashSet<string> RemovePaths,
        ImmutableHashSet<string> AppIds,
        bool HasMetadata,
        bool HasProtocol,
        string? Nonce)
    {
        public static CommittedVersion From(long version, IReadOnlyList<DeltaAction> actions)
        {
            var adds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            var removes = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            var appIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
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
                    case TxnAction txn:
                        appIds.Add(txn.AppId);
                        break;
                    case CommitInfoAction info when info.Entries.TryGetValue(DeltaCommitter.CommitNonceKey, out string? value):
                        nonce = value;
                        break;
                    default:
                        break;
                }
            }

            return new CommittedVersion(version, adds.ToImmutable(), removes.ToImmutable(), appIds.ToImmutable(), hasMetadata, hasProtocol, nonce);
        }
    }
}

/// <summary>A single violated invariant discovered by the checker: its catalogue <see cref="Invariant"/>
/// id (I1/I2/I4/I5/I6/I8/SI/CC) and a human-readable <see cref="Detail"/>.</summary>
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
