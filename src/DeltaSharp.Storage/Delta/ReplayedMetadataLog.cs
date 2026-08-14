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
/// <para><b>Where that grep stops.</b> Two seats re-ran the sweep with ~19 and ~12 further terms
/// (<c>unkillable</c>, <c>equivalent mutant</c>, <c>vacuous</c>, <c>has no effect</c>, <c>safe to remove</c>,
/// <c>never fires</c>, <c>vestigial</c>, <c>tautolog</c>, …) and found no fourth site, but they did surface
/// three NEAR-MISSES worth naming so the next reader does not re-litigate them. The other <c>not
/// load-bearing</c> hits are <c>commitInfo</c> provenance — a different claim class, about protocol
/// semantics rather than test coverage. <c>NullMaskTier</c> / <c>KernelTier</c>'s <c>vacuously "green"</c> is
/// the INVERSE shape: it justifies a testability affordance instead of excusing an untested guard.
/// <c>ChangeFeedReader</c>'s <c>UNREACHABLE via the read path</c> is the closest miss and is CORRECT — and it
/// is correct for the reason the rule turns on: it names its upstream reason and RETAINS the guard. Naming
/// why something is unreachable and keeping it is sound; concluding it is therefore deletable is not. That,
/// not the vocabulary, is the line.</para>
/// <para><b>One mutation point is not one line.</b> The end-accounting conjunct carried TWO — the
/// <c>ReferenceEquals</c> predicate and the <c>|=</c> that accumulates it — and pinning the first was
/// repeatedly reported as pinning "G21". It was not: the predicate was 1-red from R8 and the accumulation was
/// still 0-red at R10. Category 3 of the derivation rule says "each state write whose value participates in a
/// validation decision", and a compound assignment is TWO such points, since the operator and the operand can
/// be mutated independently. The lesson is not about this line: <b>when a claim is attached to a name rather
/// than to a point ("G21 is pinned"), check that the name denotes exactly one point.</b></para>
/// <para>Its disposition also shows the third disguise of the equivalence insufficiency. The proposed reason
/// was iteration order; the operative constraint was a NEIGHBOURING CONJUNCT (chain closure). Both would have
/// predicted 0 red, so the measurement could not distinguish them — but they differ in what they license,
/// because an unspecified enumeration contract cannot be probed while a conjunct can. <b>Two reasons that
/// predict the same measurement are not the same claim</b>, and the one to keep is the one that yields a
/// probe.</para>
/// <para><b>And an EXPRESSION is not one point either.</b> The rule above was adopted after the
/// <c>endMetadataAccounted</c> line was found to carry two points under one name, and it was still one level
/// too shallow: every <c>ReferenceEquals</c> here also carries a STRICTNESS point, because
/// <see cref="MetadataAction"/> is a <c>sealed record</c> and <c>==</c> therefore compiles wherever
/// <c>ReferenceEquals</c> does while meaning VALUE equality. All six identity checks in this type were 0-red
/// against that mutation, and four seats had audited the busiest of them and reported it pinned — because
/// each of them mutated the ACCUMULATION and none mutated the PREDICATE'S STRICTNESS. So the audit unit is
/// the sub-expression: a comparison has a subject, an operator and a strictness; a compound assignment has an
/// accumulation and a predicate; each is mutable alone.</para>
/// <para>The masking cause was again a fixture value choice, and a systematic one: the suite used ONE
/// instance per distinct metadata, so identity and value never disagreed anywhere. That is not an exotic
/// input — the reconstruction produces a fresh instance per commit, so any two commits carrying identical
/// <c>metaData</c> are exactly this. Five of the six mutations fail OPEN (they accept histories the identity
/// checks reject); the storage rule's fails CLOSED, so it is pinned by a history that must be ACCEPTED rather
/// than rejected. The probes are the <c>ValueEqualTwin</c> tests.</para>
/// <para>NOTE for whoever extends this: identity-vs-value is a REPO-WIDE shape wherever
/// <c>ReferenceEquals</c> is applied to a record type, and it is used deliberately elsewhere (plan-tree
/// rewriters return <c>this</c> when a child is unchanged BY IDENTITY). Those sites are outside this
/// change's scope and are not audited here; do not assume this file's sweep covered them. Tracked by
/// issue #733, which carries the site enumeration and the reason value equality would be a behaviour
/// change at the plan-tree sites rather than a fix.</para>
/// <para><b>Why five auditors missed it, which is worth more than the rule.</b> The round before had found a
/// second mutation point on that exact line, so every seat had reason to examine it more carefully than any
/// other line in the file — and all five mutated the ACCUMULATION, the point the previous finding had made
/// salient. <b>The fix created the attention and simultaneously bounded it: the correction propagated as a
/// PATTERN TO MATCH rather than a METHOD TO APPLY.</b> That is the failure mode to watch for after any
/// finding, because the natural response to "we missed X here" is to look for X, and the line is now the
/// least likely place to find Y. Re-derive the points from the rule at a corrected site; do not scan it for
/// the shape of the last defect.</para>
/// <para><b>Re-run a single-observation RED before banking it.</b> The first strictness sweep reported 1 red
/// at the corroboration check, in an unrelated projection test; it did not reproduce. Reporting it would have
/// claimed that guard PINNED when it was not. Flaky failures are habitually re-run and flaky results that
/// make a guard look COVERED habitually are not — and the second kind is the dangerous one, because it
/// terminates the search. A red that would let you stop looking deserves the same second run as a red that
/// blocks a merge.</para>
/// <para><b>An ACCEPT assertion is a weaker oracle than a REJECT assertion</b>, and needs a discriminating
/// control. Where a mutation fails CLOSED the only way to pin it is a history that must be accepted — but
/// "does not throw" is also satisfied by a gate that rejects NOTHING. Measured here: neutering the whole
/// lineage cross-check turns 12 tests red, and the accept-only version of the storage-rule test was not among
/// them. Pairing it with the minimal near-miss that must still be REJECTED puts it in both sets (13 red under
/// the neutered gate, and still a single-point kill of its own mutation).</para>
/// <para><b>A <c>!=</c> or <c>==</c> guard rejects in TWO directions, so one relational mutant is half a
/// test.</b> Mutate to BOTH <c>&lt;</c> and <c>&gt;</c> — one mutant per rejected direction. The contiguity
/// guard was reported PINNED by a seat that measured <c>!=</c> -> <c>&lt;</c> at 1 red; the other direction,
/// <c>!=</c> -> <c>&gt;</c>, was 0 red, and a REPEATED or BACKWARD hand-over was accepted as a proven window.
/// That is worse than a duplicate: <c>CoveredToExclusive = version + 1</c> runs unconditionally below the
/// guard, so a backward hand-over SHRINKS the covered interval, and <c>_recordedVersions</c> is a
/// <c>List&lt;long&gt;</c>, so a repeat duplicates a link in the chain walk this guard is what lets the doc
/// call strictly ascending.</para>
/// <para>Two seats mutated the SAME operator and reached OPPOSITE conclusions — neither carelessly. This is
/// the relational form of the strictness axis that the <c>ReferenceEquals</c> sweep found, and that sweep
/// structurally could not have caught it, because it was scoped to a method name rather than to a property of
/// comparisons. Both-direction sweep of every <c>==</c>/<c>!=</c> in this type: contiguity 1 red each way
/// (disjoint); the per-version corroboration 3 red one way and 1 red the other (disjoint); the metadata-count
/// skip 77 red when inverted. The one exception is <c>entry.Metadata.Count == 0</c> -> <c>&lt;= 0</c> at
/// 0 red, which is a PROVABLE equivalent rather than a hole: <c>Count</c> is non-negative by the
/// <see cref="IReadOnlyList{T}"/> contract, so the two agree on every reachable value.</para>
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
/// for a normal table, NOT O(commits); and never the file actions, which dominate a commit's size.
/// <b>The number of metadata revisions is written by the FOREIGN log author</b>, however, so an adversarial or
/// merely pathological pre-range history (a <c>metaData</c> on every commit across a wide checkpoint interval)
/// can make "revisions in the observed window" equal the whole pre-range commit count — an
/// O(pre-range commits) memory-amplification vector per concurrent change-feed read. That worst case is
/// therefore CAPPED at <see cref="MaxRetainedObservations"/> entries (#712): past the cap the observer goes
/// <see cref="IsInert">inert</see>, releasing everything retained and reporting nothing covered, so the gate
/// falls back to the pre-#697 full disk scan of the window — O(1) retention, O(pre-range commits) disk reads,
/// fail-closed-safe. So the bound an adversarial log actually gets is O(min(revisions, cap)) = O(1). Note the
/// cap bounds the retained observation COUNT (<see cref="MaxRetainedObservations"/> = 4096 entries), NOT bytes:
/// each retained entry still holds that revision's <c>SchemaString</c>, which is separately bounded by
/// <see cref="DeltaLog.MaxLogObjectBytes"/> (256 MiB per log object). The true residual is therefore
/// O(cap × per-commit metadata size), not literally O(1) bytes — the "O(1)" above is a constant ENTRY COUNT
/// independent of history length, which is the property that defeats the amplification vector.</para>
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

    // #712: retention cap. `_recorded` is O(metadata revisions in the observed window), which is normally
    // small — but the number of revisions is written by the FOREIGN log author, so a hostile or merely
    // pathological pre-range history (a metaData on every commit over a wide checkpoint interval) can drive it
    // to O(pre-range commits), a memory-amplification vector per concurrent change-feed read. Once the count
    // would exceed MaxRetainedObservations the observer goes INERT: it releases every retained observation and
    // reports nothing covered, so the pre-range gate falls back to reading each commit from disk over the
    // whole window. That is exactly the pre-#697 behavior — O(1) retention, O(commits) disk reads — and is
    // fail-closed-safe: the gate validates every commit it reads, and consuming NO observation means no
    // whole-window predicate is evaluated over a partial window (Seal skips the lineage check when inert,
    // because nothing corroborates against it). The cap bounds only MEMORY; it never weakens coverage.
    private bool _inert;

    /// <summary>The maximum number of recorded observations retained before the observer goes inert and the
    /// pre-range gate degrades to a full disk scan (#712). Generous for any legitimate table (metadata rarely
    /// changes), while bounding an adversarial/pathological log's retention to O(1) rather than
    /// O(pre-range commits).</summary>
    internal const int MaxRetainedObservations = 4096;

    internal ReplayedMetadataLog(long exclusiveUpperBound) => _exclusiveUpperBound = exclusiveUpperBound;

    /// <summary>Whether the observed replay covered any version below the exclusive upper bound at all.</summary>
    internal bool HasCoverage { get; private set; }

    /// <summary>The first version the observed replay covered (inclusive); meaningless when
    /// <see cref="HasCoverage"/> is <see langword="false"/>.</summary>
    internal long CoveredFromInclusive { get; private set; }

    /// <summary>One past the last version the observed replay covered.</summary>
    internal long CoveredToExclusive { get; private set; }

    /// <summary>Whether the observer went INERT because its retained-observation count reached
    /// <see cref="MaxRetainedObservations"/> (#712): it then retains nothing and reports every version NOT
    /// covered, so the pre-range gate reads the whole window from disk (fail-closed-safe). Observable so a
    /// retention regression is a test failure rather than a silent O(pre-range commits) allocation.</summary>
    internal bool IsInert => _inert;

    /// <summary>How many covered versions occupy a dictionary entry — the memory bound made observable, so a
    /// regression that reverted to one entry per replayed version is a test failure rather than a silent
    /// O(commits) allocation per concurrent change-feed read. Bounded by <see cref="MaxRetainedObservations"/>
    /// (#712): past the cap the observer goes inert and this returns 0.</summary>
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
            // #712 retention cap. Once already inert, retain nothing more; once the retained count would
            // exceed the cap, go inert NOW — releasing everything retained so far — rather than grow to
            // O(pre-range commits). The interval/lineage endpoints above still advance (they are O(1) longs),
            // but no observation is retained and none will be served: TryGetProvenObservation reports every
            // version NOT covered while inert, so the gate reads the whole window from disk (fail-closed-safe).
            if (_inert)
            {
                return;
            }

            if (_recorded.Count >= MaxRetainedObservations)
            {
                EnterInertMode();
                return;
            }

            _recorded[version] = new ObservedCommit(metadata, prevailingBefore, prevailingAfter);
            _recordedVersions.Add(version);
        }
    }

    // Releases every retained observation and latches the observer inert (#712): from here it retains nothing
    // and TryGetProvenObservation reports every version NOT covered, so the pre-range gate degrades to a full
    // disk scan of the window. Fail-closed-safe — coverage is unchanged; only retention is bounded.
    private void EnterInertMode()
    {
        _inert = true;
        _recorded.Clear();
        _recordedVersions.Clear();
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
        //
        // #712: when INERT the observer retained nothing and TryGetProvenObservation serves nothing, so there
        // is no observation for the gate to trust — every pre-range version is read from disk and validated
        // independently. The whole-window lineage check exists ONLY to make a defective observer's silence
        // unfalsifiable; with no observation consumed there is nothing to corroborate, so the check is vacuous
        // and is skipped. Skipping it is fail-closed-safe (coverage comes entirely from the disk scan) and
        // necessary — it is computed over `_recorded`, which inert mode has cleared.
        if (!_inert)
        {
            EnsureLineageIsAccountedFor();
        }

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

        if (_inert)
        {
            // #712: the observer went inert (retention cap) and retained nothing, so it can prove coverage of
            // NO version — the gate MUST read every version from disk. Returning false here (rather than
            // falling through to the cleared `_recorded` lookup, which would report a covered-and-SILENT
            // version and let the gate skip the disk read) is the fail-closed direction: inert means "read it
            // yourself", never "there was nothing to see".
            return false;
        }

        if (!HasCoverage || version < CoveredFromInclusive || version >= CoveredToExclusive)
        {
            // DO NOT DELETE `!HasCoverage`. It is a fail-closed defence-in-depth layer, NOT dead code.
            // It looks redundant — with no coverage the interval is [0, 0), so the two bounds beside it
            // already reject every version — and no SINGLE-POINT mutant of it can be killed. An earlier
            // version of this comment concluded from that it was "NOT load-bearing, which is why no test can
            // kill a mutant of it". That was FALSE and is retracted: tests DO kill it, once the guard it is
            // redundant WITH is also broken.
            //
            // Figures below re-measured at c3dfd6c, on the condition as written one line above. They are
            // PROSE and do not execute, so treat them as stale if that line has changed since. The three
            // test names ARE pinned executably, by ReplayedMetadataLogTests
            // .TheHasCoverageAuditBlockNamesThreeTestsThatAllStillExist. The mutations are given as their
            // literal edited condition, because a kill count is meaningless without the exact mutant:
            //
            //   Uhi     `if (!HasCoverage || version < CoveredFromInclusive)`                       -> 3 red
            //   Uhi+S1  `if (version < CoveredFromInclusive)`                                       -> 6 red
            //   Ulo     `if (!HasCoverage || version >= CoveredToExclusive)`                        -> 4 red
            //   Ulo+S1  `if (version >= CoveredToExclusive)`                                        -> 4 red
            //
            // Uhi+S1's extra three over Uhi are AnEmptyObserver_ReportsEverythingNotCovered_SoTheGate
            // DegradesToAFullDiskScan, Cdf_PreRangeIdentityGate_StillReadsASurvivingSubFloorCommitThe
            // ReplayCannotReach, and Cdf_SurvivingSubFloorCommitIdentityDiffers_FailsClosed_NamesSubFloor
            // Version. Against the LOWER bound this disjunct is genuinely unobservable — 4 red either way,
            // and compared SET-WISE those two are the same four tests name for name, not merely the same
            // count.
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

            // `|=`, NOT `=`. This is a SEPARATE mutation point from the ReferenceEquals beside it: the
            // predicate has been pinned since R8, and the ACCUMULATION was still 0-red at R10 -- "G21 is
            // pinned" was true of one and false of the other. Now pinned by AnEarlierRecordExplainsTheWindow
            // End_SoTheEndAccountingMustACCUMULATE_NotOverwrite, at 1 red, and the mutant fails CLOSED
            // (DeltaProtocolException), which is why it was a residual and not a defect.
            //
            // The R10 gate proposed to retire it as equivalent because "dictionary iteration order
            // structurally guarantees the absolute end state is evaluated last". That reason is WRONG twice
            // over. Dictionary<,> enumeration order is UNSPECIFIED in .NET -- insertion order is an
            // implementation detail, not a contract -- so it guarantees nothing structurally. And it is not
            // the operative constraint anyway: what made `=` look equivalent is CHAIN CLOSURE below, which
            // requires the walk to end on _lineageAtWindowEnd and so normally makes the final link the match.
            // Note the two loops do not even share an order -- this one walks _recorded.Values (dictionary
            // order), the chain walks _recordedVersions (proven ascending). A trailing SILENT record whose
            // witness returns the lineage to an instance an EARLIER record produced satisfies closure with
            // the match NOT last, and that is the probe.
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
