using System.Collections.Immutable;
using System.Linq;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The <b>read scope</b> a commit was computed against — the input that makes Delta conflict detection
/// asymmetric and read-scope-driven (design §2.11.2). A commit conflicts with a concurrent winner only
/// where its read scope overlaps what the winner changed, so a writer that read nothing (a blind append)
/// can rebase past concurrent appends, while a writer that read files (a delete/overwrite/merge) cannot.
/// Each scope owns its <see cref="CheckDataConflict"/> rule (the metadata/protocol/txn conflicts that apply
/// to every scope are handled by <see cref="DeltaConflictChecker"/> before it is consulted).
///
/// <para><b>v1 scope.</b> Three shapes are modeled: <see cref="BlindAppend"/> (empty read set),
/// <see cref="WholeTable"/> (depends on every active file — a full overwrite or unpartitioned delete), and
/// <see cref="ReadFiles"/> (depends on a named file set — a targeted delete). <see cref="BlindAppend"/> and
/// <see cref="WholeTable"/> are <b>no less conservative</b> than Delta for their cells, so they never miss a
/// real conflict. <see cref="ReadFiles"/> flags a concurrent <c>remove</c>/re-<c>add</c> of an <b>exact read
/// path</b> only; it does <b>not</b> flag a concurrent append of a <i>new</i> file that would match a
/// read <i>predicate</i> — so it is sound for a snapshot-isolated <c>DELETE</c> (which acts on its snapshot),
/// but is <b>not</b> sufficient for a <c>MERGE</c> that must observe predicate-matching inserts. Scope
/// <see cref="ReadFiles"/> to snapshot-isolated deletes until predicate/partition-precise scopes (design
/// §2.11.2 conditional cells) land as a tracked refinement.</para>
/// </summary>
internal abstract record DeltaReadScope
{
    private DeltaReadScope()
    {
    }

    /// <summary>An empty read set — a streaming or plain <c>append</c> that read no existing data, so it
    /// conflicts <b>only</b> with a concurrent metadata/protocol change (the load-bearing fast path).
    /// <b>Precondition:</b> a <see cref="BlindAppend"/> commit must be append-only (contain no
    /// <c>remove</c>), because it performs no data-conflict detection; a commit that removes files must use
    /// <see cref="WholeTable"/> or <see cref="ReadFiles"/> (enforced by the committer).</summary>
    public static DeltaReadScope BlindAppend { get; } = new BlindAppendScope();

    /// <summary>A commit that depends on the entire active file set (a full overwrite or an unpartitioned
    /// delete): it conflicts with any concurrent <c>add</c> or <c>remove</c>.</summary>
    public static DeltaReadScope WholeTable { get; } = new WholeTableScope();

    /// <summary>A commit that read a specific set of files (a snapshot-isolated targeted delete): it
    /// conflicts with a concurrent <c>remove</c> or re-<c>add</c> of any file in <paramref name="paths"/>.
    /// It does not observe predicate-matching inserts, so it is not sufficient for a <c>MERGE</c> (see the
    /// type remarks).</summary>
    public static DeltaReadScope ReadFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new ReadFilesScope(paths.ToImmutableHashSet(StringComparer.Ordinal));
    }

    /// <summary>Throws a <see cref="DeltaConcurrentModificationException"/> if a winner's <c>add</c>/
    /// <c>remove</c> overlaps this scope; returns when the writer may safely rebase. Called only after the
    /// scope-independent metadata/protocol/txn conflicts have been ruled out.</summary>
    internal abstract void CheckDataConflict(IReadOnlyList<DeltaAction> winnerActions);

    internal sealed record BlindAppendScope : DeltaReadScope
    {
        internal override void CheckDataConflict(IReadOnlyList<DeltaAction> winnerActions)
        {
            // Empty read set: no data conflict is possible — only the metadata/protocol/txn cases apply.
        }
    }

    internal sealed record WholeTableScope : DeltaReadScope
    {
        internal override void CheckDataConflict(IReadOnlyList<DeltaAction> winnerActions)
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
    }

    internal sealed record ReadFilesScope(ImmutableHashSet<string> Paths) : DeltaReadScope
    {
        internal override void CheckDataConflict(IReadOnlyList<DeltaAction> winnerActions)
        {
            // A targeted read (delete/merge over a named file set) conflicts when a winner removed a file it
            // read (read-precedence ⇒ ConcurrentDeleteRead) or re-added one of those exact paths.
            foreach (DeltaAction action in winnerActions)
            {
                switch (action)
                {
                    case RemoveFileAction remove when Paths.Contains(remove.Path):
                        throw new ConcurrentDeleteReadException(
                            $"A concurrent commit removed file '{remove.Path}', which this writer read, since its read snapshot; the commit was aborted.");

                    case AddFileAction add when Paths.Contains(add.Path):
                        throw new ConcurrentAppendException(
                            $"A concurrent commit re-added file '{add.Path}', which this writer read, since its read snapshot; the commit was aborted.");

                    default:
                        break;
                }
            }
        }
    }
}
