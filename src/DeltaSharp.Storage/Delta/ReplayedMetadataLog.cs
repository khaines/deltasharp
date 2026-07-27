using System.Globalization;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// A pure OBSERVER of a snapshot reconstruction's JSON replay (#691) that records, for each replayed commit
/// version <b>below an exclusive upper bound</b>, the <c>metaData</c> actions it expressed AND the
/// reconstruction's own prevailing-metadata references immediately BEFORE and AFTER that version was applied.
/// It exists so the change-feed pre-range column-mapping identity gate
/// (<c>DeltaLog.ValidateColumnMappingIdentityStableBeforeAsync</c>) can be driven off the commits the
/// start-snapshot reconstruction ALREADY read, instead of issuing a second GET per commit for the same
/// immutable object. A commit's JSON is write-once (put-if-absent, never rewritten), so a replayed commit's
/// parsed actions are exactly what a re-read would yield.
///
/// <para><b>Why the gate may trust an observation (council R1, quality seat).</b> The gate enforces a
/// SECURITY property, so it must never infer "no identity change at this version" from an observation that
/// merely <i>says nothing</i> — otherwise a future defect in the replay path (a short-circuit, a cache, a
/// checkpoint fast-path) could silently make the gate vacuous with no test failing. Two structural rules make
/// silence unfalsifiable rather than trusted:</para>
/// <list type="number">
/// <item><description><b>Proven coverage.</b> An observation is consumable only for a version inside the
/// CONTIGUOUS window the replay actually recorded (<see cref="TryGetProvenObservation"/>). Anything outside it
/// — always including a stray commit surviving strictly below the reconstructable floor, and every commit
/// below a checkpoint the replay seeded from — is reported as NOT covered, so the caller reads it from disk.
/// A version INSIDE the window with no recorded entry is an internal contradiction (the replay is contiguous
/// by construction) and fails closed.</description></item>
/// <item><description><b>Corroborated silence.</b> "This version expressed no <c>metaData</c>" is checked at
/// CONSUMPTION time against the reconstruction's own metadata lineage: a Delta <c>metaData</c> action REPLACES
/// the prevailing metadata (<c>SnapshotState.Apply</c>), so the state's metadata reference changes across a
/// version if and only if that version carried at least one <c>metaData</c>. An observation that disagrees
/// with the state the snapshot itself was built from is rejected fail-closed
/// (<see cref="EnsureObservationMatchesReplayedState"/>). An under-reporting observer therefore costs
/// AVAILABILITY (a fail-closed read) or PERFORMANCE (a disk fallback) — never COVERAGE.</description></item>
/// </list>
///
/// <para><b>Single extraction site.</b> <see cref="MetadataActionsOf"/> is the ONE place a commit's
/// <c>metaData</c> actions are picked out of its parsed actions; the gate's disk-fallback path calls the same
/// helper, so the observed and re-read paths cannot drift apart, and a defect in the extraction breaks BOTH
/// (the disk-path regression tests go red).</para>
///
/// <para><b>Retention bound.</b> Only versions strictly below the exclusive upper bound (the range start) are
/// recorded, and only <c>metaData</c> actions plus two metadata references are retained — never the file
/// actions, which dominate a commit's size. The retained set is therefore proportional to the number of
/// METADATA REVISIONS in the pre-range history (one for a normal table), not to its file count.</para>
///
/// <para>Fail-closed messages are path-free (#653): they name ONLY a version.</para>
/// </summary>
internal sealed class ReplayedMetadataLog
{
    private static readonly IReadOnlyList<MetadataAction> None = Array.Empty<MetadataAction>();

    private readonly Dictionary<long, ObservedCommit> _observed = new();
    private readonly long _exclusiveUpperBound;
    private long _firstObserved = long.MaxValue;
    private long _lastObserved = long.MinValue;

    internal ReplayedMetadataLog(long exclusiveUpperBound) => _exclusiveUpperBound = exclusiveUpperBound;

    /// <summary>The ONE place a commit's <c>metaData</c> actions are extracted from its parsed actions — used
    /// by both this observer and the gate's disk-fallback read, so the two sources can never disagree about
    /// what "the metaData actions of version N" means.</summary>
    internal static IReadOnlyList<MetadataAction> MetadataActionsOf(IReadOnlyList<DeltaAction> actions)
    {
        List<MetadataAction>? metadata = null;
        foreach (DeltaAction action in actions)
        {
            if (action is MetadataAction metadataAction)
            {
                (metadata ??= new List<MetadataAction>()).Add(metadataAction);
            }
        }

        return metadata ?? None;
    }

    /// <summary>Records one replayed version: its parsed <paramref name="actions"/> plus the reconstruction's
    /// prevailing metadata immediately BEFORE and AFTER they were applied (the corroboration witness).</summary>
    internal void Observe(
        long version,
        IReadOnlyList<DeltaAction> actions,
        MetadataAction? prevailingBefore,
        MetadataAction? prevailingAfter) =>
        Record(version, MetadataActionsOf(actions), prevailingBefore, prevailingAfter);

    /// <summary>The recording primitive <see cref="Observe"/> is built on, taking the already-extracted
    /// <paramref name="metadata"/>. Kept separate so a test can model an UNDER-REPORTING observer (recording
    /// fewer <c>metaData</c> actions than the version truly carried) and prove that
    /// <see cref="TryGetProvenObservation"/> rejects it instead of silently skipping the version.</summary>
    internal void Record(
        long version,
        IReadOnlyList<MetadataAction> metadata,
        MetadataAction? prevailingBefore,
        MetadataAction? prevailingAfter)
    {
        if (version >= _exclusiveUpperBound)
        {
            return; // [start, end] is the reader's per-version gate; bounding the record bounds the retention.
        }

        _observed[version] = new ObservedCommit(metadata, prevailingBefore, prevailingAfter);
        _firstObserved = Math.Min(_firstObserved, version);
        _lastObserved = Math.Max(_lastObserved, version);
    }

    /// <summary>
    /// Reports whether the replay PROVABLY covered <paramref name="version"/> and, if so, yields the
    /// <c>metaData</c> actions it expressed. <see langword="false"/> means "not covered — read the commit
    /// yourself"; it NEVER means "there was nothing to see".
    /// </summary>
    /// <exception cref="DeltaProtocolException">The observation contradicts the reconstruction it came from —
    /// either a version inside the contiguous replay window has no record, or a record disagrees with the
    /// replayed state's metadata lineage (an under-reporting observer). Fails closed rather than skip.</exception>
    internal bool TryGetProvenObservation(long version, out IReadOnlyList<MetadataAction> metadata)
    {
        if (version < _firstObserved || version > _lastObserved)
        {
            // Outside the proven window (no replay reaches here) — the caller MUST read the commit itself.
            metadata = None;
            return false;
        }

        if (!_observed.TryGetValue(version, out ObservedCommit observed))
        {
            // The replay is contiguous by construction, so a hole inside its own window is a broken invariant
            // in the observing seam. Never treat it as "nothing to validate".
            throw DeltaProtocolException.Inconsistent(string.Create(
                CultureInfo.InvariantCulture,
                $"A change-feed pre-range validation observation is missing version {version} from inside the "
                + $"contiguous range it claims to cover; the read fails closed rather than skip a version."));
        }

        EnsureObservationMatchesReplayedState(
            version, observed.Metadata, observed.PrevailingBefore, observed.PrevailingAfter);
        metadata = observed.Metadata;
        return true;
    }

    /// <summary>
    /// Rejects an observation that disagrees with the reconstruction's own metadata lineage. A Delta
    /// <c>metaData</c> REPLACES the prevailing metadata, so the replayed state's metadata reference changes
    /// across a version <b>if and only if</b> that version carried at least one <c>metaData</c> action. An
    /// observation claiming silence where the state moved (an UNDER-REPORTING observer — the exact defect that
    /// would make the pre-range gate vacuous) or claiming a <c>metaData</c> where the state did not move is a
    /// contradiction between the observer and the snapshot the reconstruction actually built, so the read
    /// fails closed. Path-free (#653): names only the version.
    /// </summary>
    internal static void EnsureObservationMatchesReplayedState(
        long version,
        IReadOnlyList<MetadataAction> metadata,
        MetadataAction? prevailingBefore,
        MetadataAction? prevailingAfter)
    {
        bool stateReplacedMetadata = !ReferenceEquals(prevailingBefore, prevailingAfter);
        if (stateReplacedMetadata == (metadata.Count > 0))
        {
            return;
        }

        throw DeltaProtocolException.Inconsistent(string.Create(
            CultureInfo.InvariantCulture,
            $"A change-feed pre-range validation observation for version {version} disagrees with the "
            + $"metadata the snapshot reconstruction applied at that version; the read fails closed rather "
            + $"than treat the observation as evidence that the version changed no column-mapping identity."));
    }

    // One replayed version's record: the metaData actions extracted from it, plus the reconstruction's
    // prevailing metadata immediately before/after applying it (the corroboration witness for "no metaData").
    private readonly record struct ObservedCommit(
        IReadOnlyList<MetadataAction> Metadata,
        MetadataAction? PrevailingBefore,
        MetadataAction? PrevailingAfter);
}
