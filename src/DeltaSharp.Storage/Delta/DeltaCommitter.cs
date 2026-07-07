using System.Collections.Immutable;
using System.Security.Cryptography;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Delta;

/// <summary>The outcome of a successful commit: the <see cref="Version"/> that became visible and the
/// number of put-if-absent <see cref="Attempts"/> it took (1 ⇒ won on the first try; &gt;1 ⇒ the writer
/// observed a retryable conflict, rebased onto a newer version, and retried).</summary>
internal readonly record struct DeltaCommitResult(long Version, int Attempts);

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
/// cannot be resolved.</para>
/// </summary>
internal sealed class DeltaCommitter
{
    /// <summary>The <c>commitInfo</c> key that carries this attempt's idempotency nonce (Delta's
    /// <c>txnId</c>) so an ambiguous-ack re-GET can recognize this writer's own commit.</summary>
    internal const string CommitNonceKey = "txnId";

    /// <summary>A generous bound on rebase-retries; reaching it implies sustained contention (or a bug) and
    /// fails closed rather than spinning forever.</summary>
    internal const int DefaultMaxAttempts = 64;

    private readonly IStorageBackend _backend;
    private readonly DeltaLog _log;
    private readonly int _maxAttempts;
    private readonly Func<string> _nonceFactory;

    /// <summary>Test seam (null/inert in production): awaited immediately before each put-if-absent with
    /// <c>(attemptIndex, targetVersion)</c>, so a test can deterministically interleave a racing writer.</summary>
    internal volatile Func<int, long, CancellationToken, Task>? BeforePutProbe;

    public DeltaCommitter(IStorageBackend backend)
        : this(backend, DefaultMaxAttempts, nonceFactory: null)
    {
    }

    internal DeltaCommitter(IStorageBackend backend, int maxAttempts, Func<string>? nonceFactory)
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
    }

    /// <summary>The production idempotency-nonce source: 128 bits from a cryptographic RNG, hex-encoded.
    /// Uses <see cref="RandomNumberGenerator"/> (not the banned <c>Guid.NewGuid</c>/<c>System.Random</c>) so
    /// nonces are collision-resistant, while a deterministic factory can be injected in tests.</summary>
    internal static string DefaultNonceFactory() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Commits <paramref name="actions"/> against <paramref name="readSnapshot"/> under
    /// <paramref name="readScope"/>, returning the version that became visible.
    /// </summary>
    /// <exception cref="DeltaProtocolException">The table's writer protocol is unsupported (fail closed).</exception>
    /// <exception cref="DeltaConcurrentModificationException">A concurrent commit logically conflicts with
    /// this one (aborted, not rebased).</exception>
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

        // Writer protocol negotiation: fail closed before any write if the table requires a writer
        // version/feature this build does not enforce (design §2.11 / §2.14 P3).
        ProtocolSupport.EnsureWritable(readSnapshot.Protocol);

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
                won = await _backend.PutIfAbsentAsync(path, bytes, cancellationToken).ConfigureAwait(false);
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

            // Definite conflict: read the winners over (baseVersion, M], classify, then rebase or abort.
            (long latest, IReadOnlyList<DeltaAction> winners) =
                await ReadWinnersAsync(baseVersion, cancellationToken).ConfigureAwait(false);
            DeltaConflictChecker.Check(actions, readScope, winners); // throws on a logical conflict
            baseVersion = latest; // safe: rebase onto M and retry with the same nonce-stable bytes.
        }

        throw new DeltaCommitUnknownStateException(
            baseVersion + 1,
            $"The commit did not converge within {_maxAttempts} attempts under sustained concurrent writers.");
    }

    /// <summary>Reads and concatenates the actions of every commit over <c>(afterExclusive, M]</c> — the
    /// winners since the read snapshot — returning the latest version <c>M</c> and their actions.</summary>
    private async Task<(long Latest, IReadOnlyList<DeltaAction> Winners)> ReadWinnersAsync(
        long afterExclusive, CancellationToken cancellationToken)
    {
        var winners = new List<DeltaAction>();
        long version = afterExclusive + 1;
        while (await _log.CommitExistsAsync(version, cancellationToken).ConfigureAwait(false))
        {
            winners.AddRange(await _log.ReadCommitActionsAsync(version, cancellationToken).ConfigureAwait(false));
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

        return (latest, winners);
    }

    /// <summary>Re-resolves an ambiguous put-if-absent (design §2.11.3): re-GET <c>&lt;version&gt;.json</c>
    /// and decide whether this writer's own commit landed (nonce match), the slot is still free, or another
    /// writer won it.</summary>
    private async Task<AmbiguousResolution> ResolveAmbiguousAsync(
        long version, string nonce, CancellationToken cancellationToken)
    {
        bool exists;
        try
        {
            exists = await _log.CommitExistsAsync(version, cancellationToken).ConfigureAwait(false);
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
            committed = await _log.ReadCommitActionsAsync(version, cancellationToken).ConfigureAwait(false);
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
