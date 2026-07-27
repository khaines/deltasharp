using System.Globalization;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// A pure OBSERVER of a snapshot reconstruction's JSON replay (#691) that lets the change-feed PRE-RANGE
/// column-mapping identity gate (<c>DeltaLog.ValidateColumnMappingIdentityStableBeforeAsync</c>) be driven off
/// the commits the start-snapshot reconstruction ALREADY read, instead of issuing a second GET per commit for
/// the same immutable object. A commit's JSON is write-once (put-if-absent, never rewritten), so a replayed
/// commit's parsed actions are exactly what a re-read would yield.
///
/// <para><b>Why the gate may trust an observation.</b> The gate enforces a SECURITY property, so it must never
/// infer "no identity change at this version" from an observation that merely <i>says nothing</i> — otherwise
/// a future defect in the replay path (a short-circuit, a cache, a checkpoint fast-path) could silently make
/// the gate vacuous with no test failing. Three structural rules make silence unfalsifiable rather than
/// trusted:</para>
/// <list type="number">
/// <item><description><b>Proven coverage is an INTERVAL, not a population.</b> The replay is contiguous by
/// construction, so the covered set is exactly <c>[CoveredFromInclusive, CoveredToExclusive)</c> — two longs,
/// maintained by <see cref="Record"/>, which fails closed if it is ever handed a non-contiguous version.
/// Coverage is therefore checkable BY INSPECTION rather than by reasoning about which dictionary keys happened
/// to get populated. <see cref="TryGetProvenObservation"/> reports anything outside that interval as NOT
/// covered, so the caller reads it from disk — always including a stray commit surviving strictly below the
/// reconstructable floor, and every commit below the checkpoint the replay seeded from.</description></item>
/// <item><description><b>Corroborated silence (per version).</b> A version is recorded whenever it carried a
/// <c>metaData</c> <b>or</b> the reconstruction's prevailing metadata moved across it — a Delta <c>metaData</c>
/// REPLACES the prevailing metadata (<c>SnapshotState.Apply</c>), so those are the same condition observed
/// from two independent places. A recorded entry that disagrees with the state the snapshot was actually built
/// from (silence where the state moved — the exact defect that would make the gate vacuous — or a claimed
/// <c>metaData</c> where it did not) is rejected fail-closed at CONSUMPTION time by
/// <see cref="EnsureObservationMatchesReplayedState"/>. Absence of a record therefore means "neither signal
/// fired", not "the observer chose to say nothing".</description></item>
/// <item><description><b>Corroborated lineage (whole window).</b> Independently of any single version, the
/// prevailing metadata at the START of the covered interval and at its END moved if and only if some covered
/// version carried a <c>metaData</c>, and a lineage that moved must be accounted for by a recorded
/// <c>metaData</c> whose applied result IS the metadata the window ends on
/// (<see cref="EnsureLineageIsAccountedFor"/>). This catches a WHOLESALE failure to record, which leaves no
/// per-version entry to contradict.</description></item>
/// </list>
///
/// <para><b>Single extraction site.</b> <see cref="MetadataActionsOf"/> is the ONE place a commit's
/// <c>metaData</c> actions are picked out of its parsed actions; the gate's disk-fallback path calls the same
/// helper, so the observed and re-read paths cannot drift apart, and a defect in the extraction breaks BOTH
/// (the disk-path regression tests go red).</para>
///
/// <para><b>Memory bound.</b> The covered set costs two <see langword="long"/>s regardless of history length;
/// only a version that carried a <c>metaData</c> — or whose silence would contradict the replayed state —
/// occupies a dictionary entry. Retention is therefore O(METADATA REVISIONS in the observed window), one entry
/// for a normal table, NOT O(commits); and never the file actions, which dominate a commit's size.</para>
///
/// <para>An observer defect can consequently cost AVAILABILITY (a fail-closed read) or PERFORMANCE (a disk
/// fallback) — never COVERAGE. Fail-closed messages are path-free (#653): they name ONLY versions.</para>
/// </summary>
internal sealed class ReplayedMetadataLog
{
    private static readonly IReadOnlyList<MetadataAction> None = Array.Empty<MetadataAction>();

    // Sparse by design (see the memory bound): ONLY versions that carried a metaData, or across which the
    // replayed metadata moved — the same set unless the observer is defective, which is precisely what the
    // consumption-time cross-checks must catch.
    private readonly Dictionary<long, ObservedCommit> _recorded = new();
    private readonly long _exclusiveUpperBound;

    private MetadataAction? _lineageAtWindowStart;
    private MetadataAction? _lineageAtWindowEnd;
    private bool _lineageChecked;

    internal ReplayedMetadataLog(long exclusiveUpperBound) => _exclusiveUpperBound = exclusiveUpperBound;

    /// <summary>Whether the observed replay covered any version below the exclusive upper bound at all.</summary>
    internal bool HasCoverage { get; private set; }

    /// <summary>The first version the observed replay covered (inclusive); meaningless when
    /// <see cref="HasCoverage"/> is <see langword="false"/>.</summary>
    internal long CoveredFromInclusive { get; private set; }

    /// <summary>One past the last version the observed replay covered.</summary>
    internal long CoveredToExclusive { get; private set; }

    /// <summary>How many covered versions occupy a dictionary entry — the memory bound made observable, so a
    /// regression that reverted to one entry per replayed version is a test failure rather than a silent
    /// O(commits) allocation per concurrent change-feed read.</summary>
    internal int RecordedObservationCount => _recorded.Count;

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
    /// <exception cref="DeltaProtocolException">The versions are not handed over contiguously, so the covered
    /// INTERVAL would not be the set actually observed.</exception>
    internal void Record(
        long version,
        IReadOnlyList<MetadataAction> metadata,
        MetadataAction? prevailingBefore,
        MetadataAction? prevailingAfter)
    {
        if (version >= _exclusiveUpperBound)
        {
            return; // [start, end] is the reader's per-version gate; bounding the window bounds the retention.
        }

        if (!HasCoverage)
        {
            HasCoverage = true;
            CoveredFromInclusive = version;
            _lineageAtWindowStart = prevailingBefore;
        }
        else if (version != CoveredToExclusive)
        {
            // The covered set is claimed to be an INTERVAL; a non-contiguous hand-over would make that claim
            // false and would silently widen coverage over versions the replay never read.
            throw DeltaProtocolException.Inconsistent(string.Create(
                CultureInfo.InvariantCulture,
                $"A change-feed pre-range validation observation jumped to version {version} instead of "
                + $"continuing at version {CoveredToExclusive}; the covered range is no longer contiguous, so "
                + $"the read fails closed rather than claim coverage it cannot prove."));
        }

        CoveredToExclusive = version + 1;
        _lineageAtWindowEnd = prevailingAfter;

        if (metadata.Count > 0 || !ReferenceEquals(prevailingBefore, prevailingAfter))
        {
            _recorded[version] = new ObservedCommit(metadata, prevailingBefore, prevailingAfter);
        }
    }

    /// <summary>
    /// Reports whether the replay PROVABLY covered <paramref name="version"/> and, if so, yields the
    /// <c>metaData</c> actions it expressed. <see langword="false"/> means "not covered — read the commit
    /// yourself"; it NEVER means "there was nothing to see".
    /// </summary>
    /// <exception cref="DeltaProtocolException">The observation contradicts the reconstruction it came from —
    /// a record disagrees with the replayed state (an under- or over-reporting observer), or the window's
    /// metadata lineage is unaccounted for. Fails closed rather than skip.</exception>
    internal bool TryGetProvenObservation(long version, out IReadOnlyList<MetadataAction> metadata)
    {
        metadata = None;
        if (!HasCoverage || version < CoveredFromInclusive || version >= CoveredToExclusive)
        {
            return false; // Outside the proven interval — the caller MUST read the commit itself.
        }

        if (_recorded.TryGetValue(version, out ObservedCommit observed))
        {
            EnsureObservationMatchesReplayedState(
                version, observed.Metadata, observed.PrevailingBefore, observed.PrevailingAfter);
            metadata = observed.Metadata;
        }

        EnsureLineageIsAccountedFor();
        return true;
    }

    /// <summary>
    /// Whole-window cross-check, run once. Because a <c>metaData</c> always REPLACES the prevailing metadata
    /// with the newly parsed instance, the lineage across the covered interval moved if and only if some
    /// covered version carried a <c>metaData</c> — and the last one must be the metadata the window ends on.
    /// Catches a WHOLESALE failure to record, which leaves no per-version entry to contradict.
    /// </summary>
    private void EnsureLineageIsAccountedFor()
    {
        if (_lineageChecked)
        {
            return;
        }

        _lineageChecked = true;
        bool lineageMoved = !ReferenceEquals(_lineageAtWindowStart, _lineageAtWindowEnd);
        bool anyMetadataRecorded = false;
        bool endMetadataAccounted = false;
        foreach (ObservedCommit entry in _recorded.Values)
        {
            if (entry.Metadata.Count == 0)
            {
                continue;
            }

            anyMetadataRecorded = true;
            endMetadataAccounted |= ReferenceEquals(entry.PrevailingAfter, _lineageAtWindowEnd);
        }

        if (lineageMoved ? endMetadataAccounted : !anyMetadataRecorded)
        {
            return;
        }

        throw DeltaProtocolException.Inconsistent(string.Create(
            CultureInfo.InvariantCulture,
            $"The change-feed pre-range validation observations over versions {CoveredFromInclusive} through "
            + $"{CoveredToExclusive - 1} do not account for the table metadata the snapshot reconstruction "
            + $"ended on; the read fails closed rather than validate that range from observations it cannot "
            + $"corroborate."));
    }

    // One recorded version: the metaData actions extracted from it, plus the reconstruction's prevailing
    // metadata immediately before/after applying it (the corroboration witness).
    private readonly record struct ObservedCommit(
        IReadOnlyList<MetadataAction> Metadata,
        MetadataAction? PrevailingBefore,
        MetadataAction? PrevailingAfter);
}
