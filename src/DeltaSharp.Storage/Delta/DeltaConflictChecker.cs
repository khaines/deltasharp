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

        // (4) Read-scope-driven data conflicts.
        switch (readScope)
        {
            case DeltaReadScope.BlindAppendScope:
                // Empty read set: no data conflict is possible (only the metadata/protocol/txn cases above).
                return;

            case DeltaReadScope.WholeTableScope:
                CheckWholeTable(winnerActions);
                return;

            case DeltaReadScope.ReadFilesScope readFiles:
                CheckReadFiles(readFiles, winnerActions);
                return;

            default:
                // Defensive: an unmodeled scope must fail closed rather than silently allow a rebase.
                throw new ConcurrentAppendException(
                    $"Commit read scope '{readScope.GetType().Name}' is not recognized; failing closed on a concurrent commit.");
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

    private static void CheckWholeTable(IReadOnlyList<DeltaAction> winnerActions)
    {
        // A whole-table read (overwrite / unpartitioned delete) depends on every active file, so any
        // concurrent add lands in scope and any concurrent remove deleted a file it read.
        if (winnerActions.Any(a => a is AddFileAction))
        {
            throw new ConcurrentAppendException(
                "A concurrent commit added files to the table since this overwrite/whole-table writer's read snapshot; the commit was aborted.");
        }

        if (winnerActions.Any(a => a is RemoveFileAction))
        {
            throw new ConcurrentDeleteReadException(
                "A concurrent commit removed files this overwrite/whole-table writer read since its read snapshot; the commit was aborted.");
        }
    }

    private static void CheckReadFiles(DeltaReadScope.ReadFilesScope readFiles, IReadOnlyList<DeltaAction> winnerActions)
    {
        // A targeted read (delete/merge over a named file set) conflicts when a winner removed a file it
        // read (read-precedence ⇒ ConcurrentDeleteRead) or re-added one of those exact paths.
        foreach (DeltaAction action in winnerActions)
        {
            switch (action)
            {
                case RemoveFileAction remove when readFiles.Paths.Contains(remove.Path):
                    throw new ConcurrentDeleteReadException(
                        $"A concurrent commit removed file '{remove.Path}', which this writer read, since its read snapshot; the commit was aborted.");

                case AddFileAction add when readFiles.Paths.Contains(add.Path):
                    throw new ConcurrentAppendException(
                        $"A concurrent commit re-added file '{add.Path}', which this writer read, since its read snapshot; the commit was aborted.");

                default:
                    break;
            }
        }
    }
}
