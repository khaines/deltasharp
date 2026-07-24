using System.Collections.Immutable;
using System.Linq;
using DeltaSharp.Storage.Delta.DeletionVectors;

namespace DeltaSharp.Storage.Delta;

/// <summary>A file the caller discovered under the table directory that is a candidate for reclamation,
/// with the modification time (epoch millis) used to protect a just-staged file within the retention
/// window.</summary>
internal readonly record struct OrphanCandidate(string Path, long ModificationTimeMillis);

/// <summary>The bounded reason the <see cref="OrphanCleanup"/> contract assigned to a discovered candidate —
/// the single source of truth for both the deletion decision and the audit annotation. Only
/// <see cref="Deletable"/> candidates are ever reclaimed; the other three are fail-safe protections.</summary>
internal enum OrphanClassification
{
    /// <summary>Retention-expired and unreferenced by the log: safe to reclaim.</summary>
    Deletable,

    /// <summary>Referenced by an active <c>add</c> in the current snapshot — never an orphan.</summary>
    Active,

    /// <summary>Referenced by a tombstone removed within the retention window (or with an unknown deletion
    /// time) — a stale reader pinned to an older snapshot may still read it.</summary>
    RetentionProtectedTombstone,

    /// <summary>Modified within the retention window (<c>mtime &gt;= cutoff</c>) — it may belong to an
    /// in-flight commit, so it is protected against listing lag / a torn view.</summary>
    RecentlyStaged,

    /// <summary>Referenced by a <c>cdc</c> action (an <see cref="AddCdcFileAction"/>) in a retained, in-window
    /// commit JSON — a Change-Data-Feed <c>_change_data/</c> file. It is never an active file (INV C1) yet
    /// must not be reclaimed while a commit within <c>delta.logRetentionDuration</c> still references it
    /// (#489). Referenced only via a non-<c>add.path</c> field, so it is protected by an explicit set that
    /// VACUUM collects from the retained commit range, not from the snapshot's active/checkpoint state.</summary>
    ReferencedChangeData,
}

/// <summary>A single candidate's classification: the object <see cref="Path"/> (the raw, unencoded disk key
/// as listed) and the fail-safe <see cref="Classification"/> the contract assigned it.</summary>
internal readonly record struct OrphanDecision(string Path, OrphanClassification Classification);

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
///
/// <para><b>Path-encoding robustness (data-loss critical).</b> Delta's protocol URI-encodes the file paths
/// stored in the log (<c>add.path</c>/<c>remove.path</c>): a file literally named <c>a b.parquet</c> on disk
/// is recorded as <c>a%20b.parquet</c> in the log. DeltaSharp writes unencoded paths (its own tables
/// round-trip), but a Spark-written / Delta-protocol table has encoded log paths, while a directory listing
/// always yields the raw disk key. Matching a raw candidate against only the raw log path would classify the
/// still-active <c>a b.parquet</c> as an orphan and delete it. The protected sets are therefore the
/// <b>union</b> of each log path and its <see cref="Uri.UnescapeDataString(string)"/> decoding, and the raw
/// candidate key is tested against that union. This protects both DeltaSharp-unencoded and Spark-encoded
/// tables; it can only ever <i>over</i>-protect (a rare filename containing a literal <c>%</c> sequence that
/// happens to decode to another candidate's name), never over-delete.</para>
///
/// <para><b>Referenced-path assumption (non-<c>add.path</c> references).</b> The active/tombstone protected
/// sets are built from <c>add.path</c> and <c>remove.path</c>. Two classes of file are referenced by
/// <b>other</b> fields and so must be protected explicitly or VACUUM would wrongly reclaim a still-referenced
/// file: (1) deletion-vector sidecars (<c>.bin</c>, referenced by <c>add.deletionVector</c>) — protected here
/// from the snapshot's active/tombstone DVs; and (2) Change-Data-Feed files (under <c>_change_data/</c>,
/// referenced by a <c>cdc</c> action) — because a <c>cdc</c> action is ignored by snapshot replay (INV C1)
/// and not retained in checkpoints, the snapshot cannot know their paths, so VACUUM enumerates them from the
/// retained, in-window commit JSONs (bounded by <c>delta.logRetentionDuration</c>) and passes them in as
/// an explicit protected set (#489). A <c>_change_data/</c> file referenced by no retained
/// <c>cdc</c> action stays reclaimable; one referenced only by a commit aged past log retention is correctly
/// unprotected (window-bounded).</para>
/// </summary>
internal static class OrphanCleanup
{
    /// <summary>
    /// Returns the subset of <paramref name="candidates"/> that is safe to delete given
    /// <paramref name="snapshot"/> and a <paramref name="retentionCutoffMillis"/> (epoch millis; a file
    /// protected iff it was removed/modified at or after the cutoff). Active files and retention-protected
    /// files are always excluded. This is the deletable projection of <see cref="Classify"/> — the single
    /// source of truth — so the deletion set never diverges from the audit classification.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> or <paramref name="candidates"/> is null.</exception>
    public static IReadOnlyList<string> SelectDeletable(
        Snapshot snapshot,
        IEnumerable<OrphanCandidate> candidates,
        long retentionCutoffMillis,
        IReadOnlyCollection<string>? protectedReferencedPaths = null)
    {
        var deletable = new List<string>();
        foreach (OrphanDecision decision in Classify(snapshot, candidates, retentionCutoffMillis, protectedReferencedPaths))
        {
            if (decision.Classification == OrphanClassification.Deletable)
            {
                deletable.Add(decision.Path);
            }
        }

        return deletable;
    }

    /// <summary>
    /// Classifies <b>every</b> candidate with the single fail-safe reason (active / retention-protected
    /// tombstone / recently staged / deletable) VACUUM consumes for both its deletion set (the
    /// <see cref="OrphanClassification.Deletable"/> subset) and its per-candidate audit. Centralizing the
    /// reason here means the encoding-robust matching and the exclusion order live in exactly one place.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> or <paramref name="candidates"/> is null.</exception>
    public static IReadOnlyList<OrphanDecision> Classify(
        Snapshot snapshot,
        IEnumerable<OrphanCandidate> candidates,
        long retentionCutoffMillis,
        IReadOnlyCollection<string>? protectedReferencedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidates);

        // Encoding-robust protected sets: a raw disk key matches a log path under raw-equality OR
        // URI-decoded-equality, so both DeltaSharp-unencoded and Spark-encoded (e.g. "a%20b.parquet")
        // tables protect the same real file. Building the log side as {raw} ∪ {UnescapeDataString(raw)}
        // and comparing the raw candidate key against it can only over-protect, never over-delete.
        //
        // STORY-05.5.1: a deletion vector's on-disk `.bin` sidecar (a `'u'` DV) is referenced by
        // add.deletionVector, NOT add.path, so it must be protected explicitly or VACUUM would reclaim a live
        // DV file and resurrect deleted rows. Both active-file DVs and retention-protected-tombstone DVs are
        // protected (a stale reader of a within-window tombstone still needs its DV).
        ImmutableHashSet<string> active = BuildEncodingRobustSet(
            snapshot.ActiveFiles.Select(add => add.Path)
                .Concat(DeletionVectorSidecarPaths(snapshot.ActiveFiles.Select(add => add.DeletionVector))));

        // A tombstone is retention-protected while a stale reader might still need it: removed at/after the
        // cutoff, OR with an unknown deletion time (fail safe — never delete on missing provenance).
        IEnumerable<RemoveFileAction> protectedRemoves = snapshot.Tombstones
            .Where(remove => (remove.DeletionTimestamp ?? long.MaxValue) >= retentionCutoffMillis);
        ImmutableHashSet<string> protectedTombstones = BuildEncodingRobustSet(
            protectedRemoves.Select(remove => remove.Path)
                .Concat(DeletionVectorSidecarPaths(protectedRemoves.Select(remove => remove.DeletionVector))));

        // #489: cdc `_change_data/` files are referenced by a `cdc` action, NOT add.path/remove.path, and a
        // cdc action is ignored by snapshot replay (INV C1) and not retained in checkpoints — so the snapshot
        // above cannot know them. VACUUM enumerates them from the retained, in-window commit JSONs (bounded
        // by delta.logRetentionDuration) and passes them here; they are protected under the same
        // encoding-robust matching so a Spark-encoded cdc path protects the raw disk key.
        ImmutableHashSet<string> referenced = protectedReferencedPaths is { Count: > 0 }
            ? BuildEncodingRobustSet(protectedReferencedPaths)
            : ImmutableHashSet<string>.Empty;

        var decisions = new List<OrphanDecision>();
        foreach (OrphanCandidate candidate in candidates)
        {
            decisions.Add(new OrphanDecision(candidate.Path, ClassifyOne(
                candidate, active, protectedTombstones, referenced, retentionCutoffMillis)));
        }

        return decisions;
    }

    private static OrphanClassification ClassifyOne(
        OrphanCandidate candidate,
        ImmutableHashSet<string> active,
        ImmutableHashSet<string> protectedTombstones,
        ImmutableHashSet<string> referenced,
        long retentionCutoffMillis)
    {
        if (active.Contains(candidate.Path))
        {
            return OrphanClassification.Active; // an active data file is never an orphan.
        }

        if (protectedTombstones.Contains(candidate.Path))
        {
            // removed within the retention window — a stale reader may still read it.
            return OrphanClassification.RetentionProtectedTombstone;
        }

        if (referenced.Contains(candidate.Path))
        {
            // a _change_data/ file referenced by an in-window cdc action (#489) — never an active file
            // (INV C1), but a CDF read within the log-retention window still needs it.
            return OrphanClassification.ReferencedChangeData;
        }

        if (candidate.ModificationTimeMillis >= retentionCutoffMillis)
        {
            // staged within the retention window — may belong to an in-flight commit.
            return OrphanClassification.RecentlyStaged;
        }

        return OrphanClassification.Deletable;
    }

    // The table-root-relative `.bin` sidecar path of every on-disk relative-path ('u') deletion vector in the
    // sequence (inline 'i' DVs have no sidecar; absolute 'p' DVs are out of scope for VACUUM protection and
    // fall to the tracked-deferral note). A malformed descriptor is skipped here (it fails the READ path
    // elsewhere) rather than aborting VACUUM's protected-set construction.
    private static IEnumerable<string> DeletionVectorSidecarPaths(IEnumerable<DeletionVectorDescriptor?> descriptors)
    {
        foreach (DeletionVectorDescriptor? descriptor in descriptors)
        {
            if (descriptor is null || descriptor.StorageType != DeletionVectorDescriptor.StorageTypeUuidRelative)
            {
                continue;
            }

            string? path = null;
            try
            {
                path = descriptor.ResolveRelativePath();
            }
            catch (DeltaStorageException)
            {
                // A corrupt DV descriptor: skip protection here (the read path fails closed on it separately).
            }

            if (path is not null)
            {
                yield return path;
            }
        }
    }

    /// <summary>Builds the protection set as the union of each log path and its URI-decoded form, so a raw
    /// (unencoded) disk key is matched whether the log stored the path encoded (Spark/Delta protocol) or
    /// unencoded (DeltaSharp). <see cref="StringComparer.Ordinal"/> is preserved (paths are byte-exact keys,
    /// never culture-folded).</summary>
    private static ImmutableHashSet<string> BuildEncodingRobustSet(IEnumerable<string> logPaths)
    {
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (string logPath in logPaths)
        {
            builder.Add(logPath);
            string decoded = Uri.UnescapeDataString(logPath);
            if (!string.Equals(decoded, logPath, StringComparison.Ordinal))
            {
                builder.Add(decoded);
            }
        }

        return builder.ToImmutable();
    }
}
