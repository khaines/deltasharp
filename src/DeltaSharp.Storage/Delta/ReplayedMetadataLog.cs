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
/// <item><description><b>Corroborated lineage (whole window).</b> Independently of any single version, and
/// checked once when the window is <see cref="Seal">sealed</see>: the prevailing metadata at the START of the
/// covered interval and at its END moved if and only if some covered version carried a <c>metaData</c>; a
/// lineage that moved must be accounted for by a recorded <c>metaData</c> whose applied result IS the metadata
/// the window ends on; AND the recorded observations must form an unbroken CHAIN from the window's opening
/// metadata to its closing metadata (<see cref="EnsureLineageIsAccountedFor"/>). The chain conjunct is what
/// makes silence safe under a SPARSE dictionary: because an unrecorded covered version is by definition one
/// that neither carried a <c>metaData</c> nor moved the state, an omitted revision necessarily shows up as a
/// break between two recorded links — even when the omission is compounded by an equalised before/after
/// witness, which defeats every per-version check. Together these catch a WHOLESALE failure to record and a
/// PARTIAL omission of a non-final revision, neither of which leaves a per-version entry to
/// contradict.</description></item>
/// </list>
///
/// <para><b>The three rules are INDEPENDENT layers; none subsumes another.</b> Worth stating explicitly,
/// because two of the conjuncts above are individually far weaker than the property the coverage argument
/// needs, and the gap between them is exactly where a fail-open lives:</para>
/// <list type="bullet">
/// <item><description>The end-accounting predicate ("a moved lineage is explained by a recorded
/// <c>metaData</c> whose applied result is the window's end metadata") is satisfied by recording ONLY the LAST
/// revision in the window, which would fail open for every earlier one. The CHAIN conjunct is what closes
/// that; end-accounting alone is not a coverage argument.</description></item>
/// <item><description>Conversely the whole-window checks cannot see an under-report at a NON-FINAL revision
/// whose successor restores the window's end metadata: the lineage moved, is accounted for, and the recorded
/// links chain unbroken, yet an earlier version silently swallowed a real <c>metaData</c>. Only
/// <see cref="EnsureObservationMatchesReplayedState"/> catches that shape.</description></item>
/// </list>
/// <para>Each layer therefore has its OWN discriminating test rather than sharing an end-behaviour one. The
/// precise, audited form of that claim (council R2 — the earlier unqualified wording was falsified by a
/// surviving mutant, so it is stated here only to the extent it has actually been checked):
/// <b>every mutation point in this type was individually neutered and the whole suite run</b>. FOURTEEN in
/// total — thirteen guards plus one ORDERING constraint (<see cref="Seal"/>'s, which is not an <c>if</c> but
/// is a mutation point with its own oracle, and is counted separately precisely because it was the thing a
/// guard was concealing). Three categories; the third is the one that matters, because it is the class a
/// fail-closed suite is STRUCTURALLY INCAPABLE of detecting:</para>
/// <list type="number">
/// <item><description><b>Killed (12).</b> Two DIFFERENT properties hold here and they must be stated
/// separately, because asserting the stronger one over both groups was wrong. (a) The four conjuncts of
/// <see cref="EnsureLineageIsAccountedFor"/> are disjoint in COUNT AND IDENTITY: exactly one red each, four
/// different tests. (b) The interval guards (the two coverage bounds and the exclusive window bound) are each
/// individually DISCRIMINATING but NOT disjoint — their red sets are pairwise distinct yet overlapping, and
/// the window bound's single red is a strict SUBSET of the upper coverage bound's. Every observable guard
/// still has a discriminating oracle; "non-overlapping" as a blanket adjective was simply
/// false.</description></item>
/// <item><description><b>Provably equivalent (2), each falsified BY EXECUTION against a recorded state space
/// rather than argued from the shape of the code.</b> An equivalence claim is only as good as the space it
/// survived, so the space is recorded here, not just the verdict. (a) The <c>!HasCoverage</c> disjunct in
/// <see cref="TryGetProvenObservation"/>: with no coverage the interval is <c>[0, 0)</c>, so the bounds
/// beside it already reject every version — THREE independent falsification attempts, over
/// <c>{long.MinValue, -1, 0, 1, 99, 100, long.MaxValue}</c> against four observer states, over
/// <c>{MinValue, -1, 0, 1, MaxValue}</c> including the <c>version = -1</c> overflow edge, plus the proof that
/// <c>CoveredToExclusive != 0</c> implies <see cref="HasCoverage"/>. None could distinguish it; it holds.
/// (b) <see cref="Seal"/>'s idempotent early return, but ONLY as a consequence of the ordering fix below:
/// because the flag is now set only after the check PASSES, and <see cref="Record"/> refuses to mutate a
/// sealed window, a repeated <see cref="Seal"/> re-runs a deterministic check over frozen state and reaches
/// the same verdict — pass/pass or throw/throw. It is a memoisation, not a guard.</description></item>
/// <item><description><b>Safer-direction survivors (0 now; 1 before the ordering fix).</b> A mutant that makes
/// the code fail closed MORE cannot be killed by a suite whose assertions are "must throw" — it is invisible
/// to the method, not absent from the code. Before the fix, deleting <see cref="Seal"/>'s early return made a
/// retried seal re-throw instead of returning silently: strictly safer, therefore unkillable. Any future
/// survivor here must be excluded from this bucket BEFORE it may be called equivalent.</description></item>
/// </list>
/// <para><b>Why that last rule is stated so bluntly.</b> <see cref="Seal"/>'s early return was independently
/// assessed as unkillable by two reviewers using two DIFFERENT arguments — one swept four observer states
/// against three <see cref="Seal"/> calls, the other reasoned that <see cref="Record"/> throws after sealing
/// so the check is pure over frozen state — and BOTH were wrong, because the distinguishing input requires a
/// PRIOR FAILED CALL, a dimension neither ranged over. What the survivor concealed was a real fail-open: the
/// ordering underneath it. Three people have now re-derived that wrong conclusion, so it is written down: an
/// equivalence claim here must be falsified by EXECUTION over a state space that includes prior-call
/// OUTCOMES, never argued from the shape of the code.</para>
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
/// <para><b>Scope of the guarantee.</b> An observer defect costs AVAILABILITY (a fail-closed read) or
/// PERFORMANCE (a disk fallback), not COVERAGE, for any defect that leaves the reconstruction's own metadata
/// lineage intact — which includes every single-point defect at the observation site (under-reporting,
/// over-reporting, wholesale non-recording, a non-contiguous or post-seal hand-over, and a partial omission of
/// any revision including a non-final one). It is NOT an unconditional claim: an observer that fabricated a
/// self-consistent lineage — omitting a revision AND rewriting the before/after witnesses of every surrounding
/// record to close the chain over it — would defeat these cross-checks, because at that point the observation
/// no longer describes the snapshot the reconstruction built. That is a different failure class from a
/// regression in the replay path, and the disk-fallback default (anything outside the proven interval) is
/// unaffected by it.</para>
/// <para>Fail-closed messages are path-free (#653): they name ONLY versions.</para>
/// </summary>
internal sealed class ReplayedMetadataLog
{
    private static readonly IReadOnlyList<MetadataAction> None = Array.Empty<MetadataAction>();

    // Sparse by design (see the memory bound): ONLY versions that carried a metaData, or across which the
    // replayed metadata moved — the same set unless the observer is defective, which is precisely what the
    // consumption-time cross-checks must catch.
    private readonly Dictionary<long, ObservedCommit> _recorded = new();

    // The recorded versions in the order they were handed over, which Record proves is strictly ascending and
    // contiguous-within-the-window. The lineage CHAIN check walks this — a chain is order-sensitive, and
    // deriving the order structurally (append order) rather than by sorting keys at read time means a future
    // edit cannot quietly drop the ordering the check depends on.
    private readonly List<long> _recordedVersions = new();
    private readonly long _exclusiveUpperBound;

    private MetadataAction? _lineageAtWindowStart;
    private MetadataAction? _lineageAtWindowEnd;
    private bool _sealed;

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
        if (_sealed)
        {
            throw DeltaProtocolException.Inconsistent(string.Create(
                CultureInfo.InvariantCulture,
                $"A change-feed pre-range validation observation arrived for version {version} after the "
                + $"observed window was sealed; the window's whole-window cross-checks were computed over a "
                + $"different set, so the read fails closed."));
        }

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
            _recordedVersions.Add(version);
        }
    }

    /// <summary>
    /// Closes the observation phase and runs the whole-window cross-checks ONCE, over the COMPLETE window.
    /// The caller (<c>DeltaLog.LoadChangeFeedStartSnapshotAsync</c>) seals as soon as the reconstruction it
    /// observed has returned. Sealing is what makes the phase separation structural rather than a convention:
    /// <see cref="Record"/> fails closed after it and <see cref="TryGetProvenObservation"/> fails closed
    /// before it, so a future change that interleaved observation with consumption — streaming the gate
    /// alongside the replay, say — could not silently evaluate a whole-window predicate over a PARTIAL window
    /// and reuse the answer.
    /// </summary>
    /// <exception cref="DeltaProtocolException">The observed window's metadata lineage is not accounted for by
    /// the recorded observations.</exception>
    internal void Seal()
    {
        if (_sealed)
        {
            return;
        }

        // ORDER IS LOAD-BEARING (council R2 — found independently by three parties, rated blocking).
        // Assigning `_sealed` BEFORE the check gave the field two meanings: Seal wrote it to mean "seal
        // ATTEMPTED", while TryGetProvenObservation's `!_sealed` gate reads it to mean "seal VALIDATED". The
        // disagreement is exactly a FAILED verdict being consumable as a passed one — a retried Seal returned
        // silently instead of re-throwing, and observations from a window whose lineage check had THROWN were
        // served. A gate whose failure path marks itself validated contradicts this type's own
        // phase-separation argument, so the flag is set only once the cross-checks have PASSED.
        EnsureLineageIsAccountedFor();
        _sealed = true;
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
        if (!_sealed)
        {
            throw DeltaProtocolException.Inconsistent(
                "A change-feed pre-range validation observation was consumed before the observed window was "
                + "sealed, so its whole-window cross-checks have not run over the complete window; the read "
                + "fails closed.");
        }

        if (!HasCoverage || version < CoveredFromInclusive || version >= CoveredToExclusive)
        {
            // `!HasCoverage` is redundant, deliberately: with no coverage the interval is [0, 0), so the two
            // bounds already reject every version. Kept for readability; it is NOT load-bearing, which is why
            // no test can kill a mutant of it. Falsified against {MIN,-1,0,1,99,100,MAX} x four observer
            // states, plus the proof that CoveredToExclusive != 0 implies HasCoverage (council R2).
            return false; // Outside the proven interval — the caller MUST read the commit itself.
        }

        if (_recorded.TryGetValue(version, out ObservedCommit observed))
        {
            EnsureObservationMatchesReplayedState(
                version, observed.Metadata, observed.PrevailingBefore, observed.PrevailingAfter);
            metadata = observed.Metadata;
        }

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

        // CHAIN conjunct. The two predicates above are existential — they prove that SOME record explains the
        // metadata the window ends on. They do NOT prove the records run UNBROKEN from the metadata the window
        // began on, and a sparse dictionary makes "absent" mean "silent and state-unchanged", not "not
        // covered". So a version that genuinely applied a metaData, but was omitted from the dictionary AND
        // had its before/after witness equalised, would be reported covered-and-silent with nothing to
        // contradict it — a fail-OPEN (council R2, architect seat). Because every UNRECORDED covered version
        // is by definition one that neither carried a metaData nor moved the state, the recorded links must
        // form an unbroken chain from the window's opening metadata to its closing metadata; a gap in that
        // chain is exactly an omitted revision. Walk it in observation order (Record proves that order is
        // strictly ascending), and require the chain to close on the window's end.
        MetadataAction? cursor = _lineageAtWindowStart;
        bool chained = true;
        foreach (long recordedVersion in _recordedVersions)
        {
            ObservedCommit link = _recorded[recordedVersion];
            if (!ReferenceEquals(link.PrevailingBefore, cursor))
            {
                chained = false;
                break;
            }

            cursor = link.PrevailingAfter;
        }

        chained = chained && ReferenceEquals(cursor, _lineageAtWindowEnd);
        if (chained && (lineageMoved ? endMetadataAccounted : !anyMetadataRecorded))
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
