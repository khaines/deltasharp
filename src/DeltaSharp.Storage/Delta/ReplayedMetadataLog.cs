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
/// <para>Each layer therefore has its OWN discriminating test rather than sharing an end-behaviour one.</para>
///
/// <para><b>How that claim is audited, and why the METHOD is written down instead of a number.</b> Five
/// independent audits of this type counted 12, 18, 19, 21 and 26 "guards" and found 1, 2, 2, 2 and 3
/// survivors respectively. Every finer audit found more. That is the signature of a set nobody DERIVED:
/// each reviewer hand-listed, and their granularity decided their count, so "every guard in this type" was
/// unverifiable by construction — which is why it was falsified three times by three different people. The
/// enumeration is therefore no longer hand-listed. A mutation point in this type is defined as, and can be
/// re-derived mechanically from the source by:</para>
/// <list type="number">
/// <item><description>each top-level boolean operand (split on <c>&amp;&amp;</c> / <c>||</c>) of every
/// condition, and</description></item>
/// <item><description>each <c>return</c>, <c>continue</c> and <c>break</c>, and</description></item>
/// <item><description>each STATE WRITE WHOSE VALUE PARTICIPATES IN A VALIDATION DECISION — NOT merely the
/// boolean-valued ones, and INCLUDING declarations with initialisers, which are writes and were the rule's
/// second demonstrated blind spot: a script implementing it matched only assignments and so missed
/// <c>cursor</c>'s seed entirely, while a hand audit caught it. The narrower boolean phrasing was this
/// rule's own FIRST defect (council R3, balanced seat): it is narrower than the audit actually performed,
/// and it would mislead the next person extending it. The interval bounds, the lineage endpoints, the
/// witness order, <c>cursor</c>'s seed and advance, and
/// the recorded-version list are all non-boolean and all load-bearing; each was audited separately and every
/// one was killed, so the narrow rule concealed no gap at this HEAD — but it did understate its own scope,
/// and</description></item>
/// <item><description>each ORDERING constraint between a state write and a validation call.</description>
/// </item>
/// </list>
/// <para><b>No total is quoted for this rule, deliberately.</b> A count has been wrong in this doc three
/// times — first as a hand-listed "every guard", then as a spurious corroboration, then as an accounting
/// carried over from the narrow rule after the rule had been broadened. What is durable is the METHOD; the
/// number is a derived quantity that goes stale the moment the rule or the file moves, and it has never once
/// been the thing that caught a defect. Re-derive from the four categories above when you need a set, and
/// compare SETS rather than totals.</para>
/// <para><b>Why that instruction is emphatic.</b> An earlier draft claimed the rule was corroborated because
/// a mechanical count matched a hand audit's total. That was RETRACTED — equal totals over different sets.
/// The retraction then shipped with a worked example intended to demonstrate set-wise comparison, and THAT
/// example did not compare set-wise either: it credited a difference the hand list did not have, and its
/// arithmetic did not close. Both the claim and its correction failed the same way, one level apart. The
/// mechanism is worth naming, because it is not carelessness: <b>a correction is written at the moment of
/// greatest confidence, immediately after the insight, which is exactly when its own arithmetic is least
/// examined</b>. Treat a freshly-written correction as the least-audited text in the file, not the most.</para>
/// <para><b>And when you retract a claim, search for its CONTENT, not its location.</b> The equivalence
/// retraction above was applied to this type doc and NOT to the inline comment at the guard itself, 290 lines
/// away, which went on asserting the retracted sentence — and asserting it in the place a maintainer standing
/// at that guard actually reads, where "NOT load-bearing" is a standing invitation to delete a fail-closed
/// layer. The correction was right and INCOMPLETE, and the second site was found by a reviewer rather than by
/// the corrector; that has now happened three times across this change's siblings, always in that same shape.
/// So the discipline is mechanical: grep the assertion's WORDING across <c>src/</c> and <c>tests/</c> —
/// "load-bearing", "no test can kill", "equivalent", "dead code", "redundant" — and fix every site in the
/// same commit as the retraction.</para>
/// <para><b>The one worked set-wise comparison, shown as SETS because its totals were disputed twice.</b>
/// A script implementing the rule above produced 13 non-boolean state writes; an independent hand audit
/// produced 11. Neither total is evidence of anything; the membership is:</para>
/// <list type="bullet">
/// <item><description><b>Script but not hand (4):</b> <c>_sealed = true</c>, <c>metadata = None</c>,
/// <c>_recorded[version] = …</c>, <c>chained = false</c>.</description></item>
/// <item><description><b>Hand but not script (2):</b> the <c>ObservedCommit</c> witness ARGUMENT ORDER, which
/// is not a write at all; and <c>cursor</c>'s SEED.</description></item>
/// <item><description><b>Closing:</b> 13 − 4 + 2 = 11, which closes only once all six differences are
/// named.</description></item>
/// </list>
/// <para>Three points about that, because two seats disagreed and a third split the difference. First, an
/// earlier draft named only THREE differences, which netted to 11 rather than 13 — the objection that the
/// arithmetic did not close was CORRECT. Second, that draft credited "the rule splits <c>cursor</c>'s seed
/// from its advance" as a difference; it is not one, and it is backwards — the hand audit had both, and the
/// script had only the advance. Third, the reason is a real blind spot rather than a slip: <c>cursor</c>'s
/// seed is a DECLARATION with an initialiser, which the script's write-detector did not recognise. Note the
/// cause is NOT simply "it matched only assignments" — it also matched collection-mutating calls, and
/// <c>_recordedVersions.Add</c> must be inside its 13 for the arithmetic above to close. A write-detector
/// needs at least four shapes an assignment-matcher misses: declarations with initialisers, expression-bodied
/// member writes, collection-mutating calls, and <c>??=</c> in expression position. All four were probed and
/// all were killed, so the hole cost no coverage — but the cause has to be stated correctly, because the
/// accounting depends on which shapes the script actually saw. <b>The mechanical rule missed a site the hand
/// audit caught.</b> Two later reviews affirmed the illustration after verifying that
/// the seed and the advance are two distinct SITES — which is true, and is a different proposition from their
/// being a DIFFERENCE between the two sets. That is the whole reason this is written as sets: agreement about
/// a total, or about a neighbouring true statement, is not agreement about membership.</para>
///
/// <para>The rule does, however, explain the historical misses. Categories 3 and 4 are not <c>if</c>
/// statements, so a guard-shaped enumeration cannot see them — and those sites are EXACTLY the fail-opens
/// found by review rather than by this file's own audit: the chain-closure <c>ReferenceEquals</c> (write),
/// the <c>endMetadataAccounted</c> <c>ReferenceEquals</c> (write), the chain walk's latching write, and
/// <see cref="Seal"/>'s ordering. The lesson generalises past this file: an audit that enumerates conditions
/// will systematically miss validation logic that lives in an assignment.</para>
///
/// <para><b>Result: every derived point was individually neutered against the full suite. All but three are
/// killed, and every point whose neutering fails OPEN is caught by at least one test.</b> State that
/// carefully: an earlier revision asserted "no point fails open at this HEAD" while the chain walk's latching
/// write was still unpinned, so <b>the file's own headline safety claim was FALSE for three commits</b> and
/// was corrected only after three separate seats each found it with a different probe. It is the same failure
/// as every other in this list — a safety property asserted over a set, at the moment of greatest confidence,
/// without executing the members. Attribution is stated as
/// two SEPARATE properties, because asserting the stronger one over the whole file was wrong three times:</para>
/// <list type="bullet">
/// <item><description><b>Disjoint in identity — the four conjuncts of
/// <see cref="EnsureLineageIsAccountedFor"/> only.</b> Each names a different oracle:
/// chain-link → <c>APartiallyOmittedNonFinalRevision</c> +
/// <c>AnOmittedRevisionWhoseBreakPointLandsOnTheWindowEnd</c>; chain-closure →
/// <c>ATrailingSilentVersionWhoseWitnessContradictsTheChain</c>; end-accounting →
/// <c>AWholesaleFailureToRecordMetadata</c> + <c>AMovedButSilentTrailingRevision</c>; un-moved branch →
/// <c>AMetadataRecordedAcrossAnUnMovedLineage</c>. The four red sets are PAIRWISE DISJOINT, but this is
/// disjointness in IDENTITY only — it is no longer "exactly one red each", since chain-link and end-accounting
/// now have two oracles apiece.</description></item>
/// <item><description><b>Everything else: individually discriminating, NOT disjoint.</b> Measured overlaps —
/// the exclusive-window bound's single red is a strict SUBSET of the upper coverage bound's; the two coverage
/// bounds share <c>TheCoveredSetIsExactlyTheContiguousIntervalObserved</c>; and the empty-entry skip, its
/// <c>continue</c>, and the <c>endMetadataAccounted</c> conjunct all die with the SAME two-element set. Every
/// point still has an oracle that dies; "non-overlapping" as a blanket adjective is simply
/// false.</description></item>
/// </list>
///
/// <para><b>The survivors, classified — and the classification matters more than the count.</b> Two are
/// survivors, and PAIRED treatment refined rather than confirmed their labels: council R5 swept the
/// complete survivor lattice — every single, pair and triple over the four then-surviving points — and
/// <c>!HasCoverage</c> and <see cref="Seal"/>'s early return stayed 0-red in every combination excluding
/// themselves. That sweep was measured BEFORE the chain walk's write was pinned, so it was re-checked at this
/// HEAD rather than carried across trees: both are still 0-red here, and the write is now killed. Citing a
/// measurement taken on a different tree is the same error as comparing totals over different sets.</para>
/// <list type="number">
/// <item><description><b>CONTINGENT redundancy, guarded by the upper bound — NOT equivalent (1).</b> The
/// <c>!HasCoverage</c> disjunct in <see cref="TryGetProvenObservation"/>. It survives single-point (0 red)
/// and was four times called provably equivalent, including by this doc. That was wrong in the benign
/// direction. It is redundant only WHILE the bound beside it is intact: remove the upper bound alone and 3
/// tests fail; remove the upper bound AND this disjunct and 6 do, the extra three being an empty observer and
/// two surviving-sub-floor CDF cases. Against the LOWER bound it really is unobservable (4 red either way,
/// identical sets). So it is a defence-in-depth layer that no single-point test can kill, not dead code.
/// <b>Why four falsifications missed it: every one swept the INPUT space with the rest of the code intact.
/// The dimension that distinguishes this guard is the CODE STATE of a neighbouring guard.</b> An equivalence
/// claim must range over both, which is the same lesson as the prior-call dimension and the fixture
/// dimension, in a third disguise. Two seats reached apparently opposite verdicts here and BOTH are right
/// over the space each swept: composed against the other SURVIVORS it is behaviour-identical, because none of
/// them touches the interval; composed against the KILLED upper bound it is observable. A survivor lattice is
/// not a sufficient space for an equivalence claim — redundancy must also be tested against the guards the
/// claim says it is redundant WITH, and those are usually not survivors.</description></item>
/// <item><description><b>Equivalent as a CONSEQUENCE of the ordering fix (1).</b> <see cref="Seal"/>'s
/// idempotent early return. Because the flag is now set only after the check PASSES, and <see cref="Record"/>
/// refuses to mutate a sealed window, a repeated <see cref="Seal"/> re-runs a deterministic check over frozen
/// state and reaches the same verdict — pass/pass or throw/throw. It is a memoisation, not a guard. It was
/// NOT equivalent before that fix; see below.</description></item>
/// <item><description><b>Genuine equivalent (1).</b> The <c>break</c> after <c>chained = false</c>. Once the
/// write has latched <c>false</c> nothing restores it, and the loop's only other effect is advancing
/// <c>cursor</c>, which is consumed solely by a conjunction already false — so continuing the walk cannot
/// change the verdict. Unlike the earlier ARGUED form of this claim, it is now backed by execution: council
/// R7 composed it with the other two survivors and the result is behaviour-identical, so the three are
/// equivalent singly AND jointly. Note the argument is valid only GIVEN the write beside it, which is
/// separately load-bearing and separately pinned — the <c>break</c> was briefly mistaken for the load-bearing
/// half of that pair, when the truth was the reverse.</description></item>
/// </list>
///
/// <para><b>The write beside that <c>break</c> is load-bearing, and getting there took three wrong
/// classifications — read this before classifying anything here.</b> <c>chained = false</c> is pinned
/// single-point by <c>AnOmittedRevisionWhoseBreakPointLandsOnTheWindowEnd</c>: flip it to <c>true</c>, leave
/// the <c>break</c>, and a window with a silently omitted revision is ACCEPTED. An earlier revision of this
/// doc called that write merely half of a mutually-masking pair, because on the probe then available the
/// break left <c>cursor</c> stale and the chain-CLOSURE conjunct re-rejected. The distinguishing probe is one
/// whose break point leaves <c>cursor</c> on EXACTLY the window end, where closure is satisfied and cannot
/// re-reject. TWO structurally different probes now do this — a two-version window, and a three-version one
/// whose successor RESTORES an earlier link's applied result — and the write alone fails open on both. The
/// masking was therefore a property of the FIXTURE, not of the code: the older test happened to choose a
/// value that left <c>cursor</c> stale. <b>"Masked on the fixture I had" is not "maskable"</b>, and the
/// masking-pair explanation is withdrawn for this site: the true cause was simply an unpinned
/// guard.</para>
/// <para>That was the third classification on this type to resolve toward benign — <see cref="Seal"/>'s
/// idempotence was twice called provably equivalent and was a latent fail-open; this <c>break</c> was called
/// a genuine equivalent and was not the whole story; this write was called half a pair and is independently
/// fail-open. Each was reached honestly, and each time acquiring an EXPLANATION is what ended the search.
/// So: <b>a survivor's classification is the LAST thing to establish, not the first, and the moment a
/// mechanism is found is the moment to ask whether it is the ONLY one</b> — because that is precisely when
/// there is a reason to stop looking. Concretely, "this mutant is masked" must be demonstrated over a probe
/// set chosen to break the masking, not over the probes that happened to be at hand.</para>
/// <para><b>Safer-direction survivors: a third category, distinct from equivalent.</b> A mutant that makes
/// the code fail closed MORE cannot be killed by a suite whose assertions are "must throw" — it is invisible
/// to the method, not absent from the code. There are none now, but before the ordering fix, deleting
/// <see cref="Seal"/>'s early return made a retried seal re-throw instead of returning silently: strictly
/// safer, therefore unkillable, and it CONCEALED the real fail-open underneath it. Any future survivor must
/// be excluded from this bucket BEFORE it may be called equivalent.</para>
///
/// <para><b>Mutant quality is part of the audit.</b> Three of this file's own mutants were initially DEFECTIVE
/// — they weakened a guard only at <c>long.MinValue</c>, so they were near-equivalent by construction and
/// "survived" for a reason that said nothing about the test suite. Corrected to always-false forms, all three
/// died (1, 26 and 2 red). A survivor is evidence about the tests ONLY once the mutant is shown to change
/// behaviour on a reachable input; otherwise it is evidence about the mutant.</para>
///
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
            // DO NOT DELETE `!HasCoverage`. It is a fail-closed defence-in-depth layer, NOT dead code.
            // It looks redundant — with no coverage the interval is [0, 0), so the two bounds beside it
            // already reject every version — and no SINGLE-POINT mutant of it can be killed. An earlier
            // version of this comment concluded from that it was "NOT load-bearing, which is why no test can
            // kill a mutant of it". That was FALSE and is retracted: tests DO kill it, once the guard it is
            // redundant WITH is also broken. Measured (council R8/R9): remove the upper bound alone -> 3 red;
            // remove the upper bound AND this disjunct -> 6, the extra three being an empty observer and two
            // surviving-sub-floor CDF cases. Against the LOWER bound it is genuinely unobservable (4 red
            // either way, identical sets).
            //
            // The earlier claim's falsification space was inputs only ({MIN,-1,0,1,99,100,MAX} x four
            // observer states). That is the insufficiency this type's doc now forbids: an equivalence claim
            // must also range over the CODE STATE of the guards it claims redundancy with. See the mutation
            // audit on the type for the full rule.
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
