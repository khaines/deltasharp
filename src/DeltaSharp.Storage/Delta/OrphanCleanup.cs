using System.Collections.Immutable;
using System.Linq;

namespace DeltaSharp.Storage.Delta;

/// <summary>A file the caller discovered under the table directory that is a candidate for reclamation,
/// with the modification time (epoch millis) used to protect a just-staged file within the retention
/// window.</summary>
internal readonly record struct OrphanCandidate(string Path, long ModificationTimeMillis);

/// <summary>
/// The <b>orphan-file cleanup contract</b> (design §2.11.5, STORY-05.3.2 AC2/AC4). Files staged but never
/// committed — a crash before commit, or a lost commit later rebased away — are orphans: not referenced by
/// any committed <c>add</c>, therefore invisible to readers (the log is truth), and never treated as
/// committed data. This type answers the single safety-critical question VACUUM (§2.14 / #196) asks: which
/// discovered candidate files may be deleted <b>without</b> removing an active file or a retention-protected
/// one. It performs no I/O and never deletes — reclamation is VACUUM's job, never an eager delete on the
/// commit path (§2.11.5).
///
/// <para>A candidate is <b>deletable</b> only when it is <b>all</b> of: (a) not in the snapshot's active
/// file set; (b) not a retention-protected tombstone (a file removed at or after the retention cutoff — a
/// stale reader pinned to an older snapshot may still read it); and (c) not itself modified at or after the
/// cutoff (a just-staged file may belong to an in-flight commit). Everything else is retained — the contract
/// is <b>fail-safe</b>: an unknown deletion time or a boundary case is treated as protected, never deleted.</para>
/// </summary>
internal static class OrphanCleanup
{
    /// <summary>
    /// Returns the subset of <paramref name="candidates"/> that is safe to delete given
    /// <paramref name="snapshot"/> and a <paramref name="retentionCutoffMillis"/> (epoch millis; a file
    /// protected iff it was removed/modified at or after the cutoff). Active files and retention-protected
    /// files are always excluded.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> or <paramref name="candidates"/> is null.</exception>
    public static IReadOnlyList<string> SelectDeletable(
        Snapshot snapshot, IEnumerable<OrphanCandidate> candidates, long retentionCutoffMillis)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidates);

        ImmutableHashSet<string> active = snapshot.ActiveFiles
            .Select(add => add.Path)
            .ToImmutableHashSet(StringComparer.Ordinal);

        // A tombstone is retention-protected while a stale reader might still need it: removed at/after the
        // cutoff, OR with an unknown deletion time (fail safe — never delete on missing provenance).
        ImmutableHashSet<string> protectedTombstones = snapshot.Tombstones
            .Where(remove => (remove.DeletionTimestamp ?? long.MaxValue) >= retentionCutoffMillis)
            .Select(remove => remove.Path)
            .ToImmutableHashSet(StringComparer.Ordinal);

        var deletable = new List<string>();
        foreach (OrphanCandidate candidate in candidates)
        {
            if (active.Contains(candidate.Path))
            {
                continue; // an active data file is never an orphan.
            }

            if (protectedTombstones.Contains(candidate.Path))
            {
                continue; // removed within the retention window — a stale reader may still read it.
            }

            if (candidate.ModificationTimeMillis >= retentionCutoffMillis)
            {
                continue; // staged within the retention window — may belong to an in-flight commit.
            }

            deletable.Add(candidate.Path);
        }

        return deletable;
    }
}
