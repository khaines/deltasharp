using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using DeltaSharp.Storage.Backends;

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

    /// <summary>Test seam (null/inert in production): awaited immediately before each put-if-absent with
    /// <c>(attemptIndex, targetVersion)</c>, so a test can deterministically interleave a racing writer.</summary>
    internal volatile Func<int, long, CancellationToken, Task>? BeforePutProbe;

    public DeltaCommitter(IStorageBackend backend)
        : this(backend, DefaultMaxAttempts, nonceFactory: null, transientBackoff: null)
    {
    }

    internal DeltaCommitter(
        IStorageBackend backend,
        int maxAttempts,
        Func<string>? nonceFactory,
        Func<int, CancellationToken, Task>? transientBackoff = null)
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
    /// <exception cref="DeltaProtocolException">The table's writer protocol is unsupported (fail closed).</exception>
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

        // Idempotency via txn (§2.11.4): if this commit records application transactions whose versions the
        // read snapshot already reflects (snapshot.txn[appId] >= version), the batch already committed —
        // report success without re-writing, so a streaming/micro-batch retry that re-reads a fresh snapshot
        // (or a cross-restart retry) never duplicates rows. Idempotency is all-or-nothing for the atomic
        // batch: skip only if EVERY txn is covered; fail closed on a partial overlap (some covered, some not)
        // rather than silently dropping the uncommitted transactions and their data.
        switch (ClassifyTxnCoverage(actions, readSnapshot.Transactions, out int coveredUpFront, out int totalUpFront))
        {
            case TxnCoverage.All:
                return new DeltaCommitResult(readSnapshot.Version, Attempts: 0, Skipped: true);
            case TxnCoverage.Partial:
                throw PartialTxn(coveredUpFront, totalUpFront);
        }

        (IReadOnlyList<DeltaAction> payload, string nonce) = BuildPayload(actions, _nonceFactory());
        byte[] bytes = DeltaLogActionWriter.SerializeCommit(payload);

        long baseVersion = readSnapshot.Version; // R — rebased forward on each safe retry.
        for (int attempt = 0; attempt < _maxAttempts; attempt++)
        {
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
                        return new DeltaCommitResult(target, attempt + 1); // ack was lost but the write landed.
                    case AmbiguousResolution.SlotFree:
                        continue; // our put did not land; the slot is unclaimed — retry the same version.
                    default: // LostToOther: <N>.json exists but is not ours → resolve as a definite conflict.
                        won = false;
                        break;
                }
            }

            if (won)
            {
                return new DeltaCommitResult(target, attempt + 1);
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
                return new DeltaCommitResult(ourVersion, attempt + 1);
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
            switch (ClassifyTxnCoverage(actions, TxnStateOf(winners), out int coveredOnConflict, out int totalOnConflict))
            {
                case TxnCoverage.All:
                    return new DeltaCommitResult(latest, attempt + 1, Skipped: true);
                case TxnCoverage.Partial:
                    throw PartialTxn(coveredOnConflict, totalOnConflict);
            }

            DeltaConflictChecker.Check(actions, readScope, winners); // throws on a logical conflict
            baseVersion = latest; // safe: rebase onto M and retry with the same nonce-stable bytes.
        }

        // Budget exhausted under sustained contention: the commit provably did NOT land (every attempt ended
        // in a lost race or a safe rebase), so this is a known, retryable outcome — distinct from the genuine
        // unknown-state paths (§2.11.3).
        throw new DeltaCommitContentionException(
            baseVersion + 1,
            _maxAttempts,
            $"The commit did not converge within {_maxAttempts} attempts under sustained concurrent writers; it did not land — retry from a fresh snapshot.");
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
    /// idempotency <paramref name="nonce"/> (merged over any caller-supplied <c>commitInfo</c>), followed by
    /// the caller's non-<c>commitInfo</c> actions in order.</summary>
    private static (IReadOnlyList<DeltaAction> Payload, string Nonce) BuildPayload(
        IReadOnlyList<DeltaAction> actions, string nonce)
    {
        var entries = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var rest = new List<DeltaAction>(actions.Count);
        foreach (DeltaAction action in actions)
        {
            if (action is CommitInfoAction commitInfo)
            {
                foreach (KeyValuePair<string, string> entry in commitInfo.Entries)
                {
                    entries[entry.Key] = entry.Value;
                }
            }
            else
            {
                rest.Add(action);
            }
        }

        // The engine owns the idempotency nonce: overwrite any caller-supplied commitInfo["txnId"] so the
        // nonce is authoritative for ambiguous-ack recognition (a caller cannot forge/override it).
        entries[CommitNonceKey] = nonce;

        var payload = new List<DeltaAction>(rest.Count + 1) { new CommitInfoAction(entries.ToImmutable()) };
        payload.AddRange(rest);
        return (payload, nonce);
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
        IReadOnlyList<DeltaAction> actions, ImmutableSortedDictionary<string, long> txnState,
        out int covered, out int total)
    {
        total = 0;
        covered = 0;
        foreach (DeltaAction action in actions)
        {
            if (action is TxnAction txn)
            {
                total++;
                if (txnState.TryGetValue(txn.AppId, out long committedVersion) && committedVersion >= txn.Version)
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

    private static PartialTransactionException PartialTxn(int covered, int total) =>
        new($"This commit carries {total} application transactions of which {covered} are already committed; "
            + "idempotency for an atomic batch is all-or-nothing, so a partially-committed batch is failed "
            + "closed rather than skipped (which would drop the uncommitted transactions and their data). "
            + "Do not bundle unrelated or non-monotonic application transactions into a single commit (§2.11.4).");

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
