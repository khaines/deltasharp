using System.Linq;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Classifies a lost optimistic-concurrency race (design §2.11.2). Given the actions this writer intends to
/// commit, its <see cref="DeltaReadScope"/>, and the concatenated actions of every commit that landed since
/// its read snapshot (the winners over versions <c>(R, M]</c>), it either <b>returns</b> — the writer may
/// safely rebase onto <c>M+1</c> and retry — or <b>throws</b> a Delta-parity
/// <see cref="DeltaConcurrentModificationException"/> naming the conflict.
///
/// <para>Detection is asymmetric and read-scope-driven: it keys the <i>loser's</i> read scope against each
/// <i>winner's</i> <c>add</c>/<c>remove</c>/<c>metaData</c>/<c>protocol</c>/<c>txn</c> set. The
/// load-bearing case is the <b>blind append</b> (<see cref="DeltaReadScope.BlindAppend"/>): it registers no
/// read predicate, so it conflicts <b>only</b> with a concurrent metadata/protocol change and rebases past
/// concurrent appends/overwrites/deletes/compaction. The exception <b>type</b> is winner-driven — the
/// metadata/protocol cases name what the <i>winner</i> changed (any loser conflicts with a concurrent
/// metadata/protocol change).</para>
/// </summary>
internal static class DeltaConflictChecker
{
    /// <summary>
    /// Throws a <see cref="DeltaConcurrentModificationException"/> if <paramref name="loserActions"/> under
    /// <paramref name="readScope"/> logically conflicts with <paramref name="winnerActions"/>; returns
    /// normally when the loser may rebase-and-retry.
    /// </summary>
    public static void Check(
        IReadOnlyList<DeltaAction> loserActions,
        DeltaReadScope readScope,
        IReadOnlyList<DeltaAction> winnerActions)
    {
        ArgumentNullException.ThrowIfNull(loserActions);
        ArgumentNullException.ThrowIfNull(readScope);
        ArgumentNullException.ThrowIfNull(winnerActions);

        // (1) Winner-driven exclusivity: any concurrent protocol/metadata change aborts every loser
        // (conflict matrix Metadata/Protocol columns). Protocol is checked first as the most significant.
        if (winnerActions.Any(a => a is ProtocolAction))
        {
            throw new ProtocolChangedException(
                "The table protocol was changed by a concurrent commit since this writer's read snapshot; the commit was aborted.");
        }

        if (winnerActions.Any(a => a is MetadataAction))
        {
            throw new MetadataChangedException(
                "The table metadata was changed by a concurrent commit since this writer's read snapshot; the commit was aborted.");
        }

        // (2) Loser exclusivity (conflict matrix rows 5–6, a deliberate stricter-than-Delta posture): a
        // commit that itself changes protocol/metadata takes the table exclusively, so any concurrent winner
        // (which here changed neither protocol nor metadata — those are handled above) aborts it rather than
        // risk rebasing a schema/protocol change over concurrent data.
        if (loserActions.Any(a => a is ProtocolAction))
        {
            throw new ProtocolChangedException(
                "This commit changes the table protocol, which requires exclusive access, but a concurrent commit landed since its read snapshot; retry against the latest version.");
        }

        if (loserActions.Any(a => a is MetadataAction))
        {
            throw new MetadataChangedException(
                "This commit changes the table metadata, which requires exclusive access, but a concurrent commit landed since its read snapshot; retry against the latest version.");
        }

        // (3) Concurrent same-appId transaction — two writers sharing an application id raced (idempotent
        // skip of an already-applied txn is STORY-05.3.2; here a genuine concurrent same-appId commit is a
        // hard conflict, checked before any rebase so shared-appId blind appends never both commit).
        CheckConcurrentTransaction(loserActions, winnerActions);

        // (3.5) Deletion-vector exclusivity (STORY-05.5.1 AC2). A merge-on-read DELETE commits an add that
        // carries a deletionVector for a file whose DV it just rewrote (plus a remove of that file's prior
        // add). If a concurrent winner already added OR removed that EXACT file — its own DELETE, OPTIMIZE,
        // or overwrite touched the same physical file — then rebasing this DV over the winner would either
        // silently DROP the winner's deletes (union computed against a stale physical layout) or resurrect
        // already-deleted rows, so the loser MUST abort rather than rebase. This is a scope-independent
        // safety net narrowly gated on a DV-bearing add, so a normal append (no DV) is never disturbed even
        // when it shares no read scope. The DELETE also commits under ReadFiles, which catches the same race;
        // this rule guarantees the invariant "a concurrent DV update to the same file never loses a delete"
        // holds regardless of the scope a future caller supplies.
        CheckDeletionVectorConflict(loserActions, winnerActions);

        // (4) Read-scope-driven data conflicts — each scope owns its overlap rule (§2.11.2).
        readScope.CheckDataConflict(winnerActions);
    }

    // Aborts the loser when it attaches/updates a deletion vector on a file a concurrent winner also added or
    // removed. Only a loser add carrying a non-null deletionVector participates (a plain append is ignored),
    // so this never fires on ordinary concurrent appends.
    private static void CheckDeletionVectorConflict(
        IReadOnlyList<DeltaAction> loserActions, IReadOnlyList<DeltaAction> winnerActions)
    {
        HashSet<string>? deletionVectorPaths = null;
        foreach (DeltaAction action in loserActions)
        {
            if (action is AddFileAction add && add.DeletionVector is not null)
            {
                (deletionVectorPaths ??= new HashSet<string>(StringComparer.Ordinal)).Add(add.Path);
            }
        }

        if (deletionVectorPaths is null)
        {
            return;
        }

        foreach (DeltaAction action in winnerActions)
        {
            string? path = action switch
            {
                AddFileAction winnerAdd when deletionVectorPaths.Contains(winnerAdd.Path) => winnerAdd.Path,
                RemoveFileAction winnerRemove when deletionVectorPaths.Contains(winnerRemove.Path) => winnerRemove.Path,
                _ => null,
            };

            if (path is not null)
            {
                throw new ConcurrentDeleteReadException(
                    $"A concurrent commit changed file '{path}', for which this writer is committing a deletion "
                    + "vector, since its read snapshot; the commit was aborted to avoid losing a concurrent delete.");
            }
        }
    }

    private static void CheckConcurrentTransaction(
        IReadOnlyList<DeltaAction> loserActions, IReadOnlyList<DeltaAction> winnerActions)
    {
        HashSet<string>? loserAppIds = null;
        foreach (DeltaAction action in loserActions)
        {
            if (action is TxnAction txn)
            {
                (loserAppIds ??= new HashSet<string>(StringComparer.Ordinal)).Add(txn.AppId);
            }
        }

        if (loserAppIds is null)
        {
            return;
        }

        foreach (DeltaAction action in winnerActions)
        {
            if (action is TxnAction winnerTxn && loserAppIds.Contains(winnerTxn.AppId))
            {
                throw new ConcurrentTransactionException(
                    $"A concurrent commit recorded a transaction for appId '{winnerTxn.AppId}' since this writer's read snapshot; the commit was aborted to preserve idempotency.");
            }
        }
    }
}
