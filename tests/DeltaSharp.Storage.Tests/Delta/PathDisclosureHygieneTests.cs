using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Writing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Regressions for the Hive-path DISCLOSURE posture (council round 3, maintainer ruling).
/// <para>DeltaSharp lays data out Hive-style, so a table-relative object path such as
/// <c>email=alice.taylor%40example.com/region=EU/part-DD73….parquet</c> embeds partition VALUES — which are
/// COLUMN VALUES, i.e. table data and potentially PII. Sanitizing does NOT address that: an email address
/// contains no control character and is well under the 128-char cap, so it survives
/// <see cref="DiagnosticText.Sanitize"/> verbatim and <c>Uri.UnescapeDataString</c> recovers the original from
/// the percent-encoding. The ruling is therefore: <b>keep partition CONTEXT, never partition VALUES</b> —
/// every path echo renders through <see cref="DiagnosticText.DescribePath"/>, which keeps the sanitized file
/// name plus the sanitized partition COLUMN NAMES and drops the values, while the raw path stays on a typed
/// property the table owner can read deliberately.</para>
/// <para>Each test drives ONE site so an individual revert turns exactly one test red.</para>
/// </summary>
// Two tests here drive the PROCESS-GLOBAL LocalFileSystemBackend.IoFaultHook / DirectoryFsync.FsyncHook
// seams, so this class joins the existing non-parallel fault-injection collection. Without it an injected
// fault leaks into whatever else xUnit happens to be running -- observed as five unrelated failures across
// VACUUM, checkpoint and optimize suites the first time these tests ran.
// THE ORDERING RULE FOR EVERY GUARD IN THIS FILE, STATED ONCE HERE AND EXECUTED BELOW BY
// EveryFailureCollectingGuard_AssertsItsCollectionIsEmpty_BeforeAnyAdequacyAssertion:
//
//     Assert a check BEFORE the behavioural assertion only if its failure makes that result WRONG.
//     If it merely leaves the result UNMEASURED, assert it AFTER.
//
// It is deliberately NOT "preconditions go first", and that misreading is what produced this defect five
// times in this file. A corpus-size or non-vacuity check placed first fires on a corpus that merely moved,
// and the regression it was guarding is never printed -- that has cost three reproductions on the census
// members alone, and once inside the totality guard added to stop a DIFFERENT non-travelling fix.
//
// The discriminator is WRONG vs UNMEASURED and nothing else. A surgery-staleness check belongs FIRST:
// surgery against a pattern that no longer matches makes every row it prints a fiction. A corpus size
// belongs LAST: a corpus that grew leaves the finding unmeasured, not false.
//
// Both sides are executed rather than remembered. The guard reports any assertion running ahead of a
// collected-failures-are-empty assertion, and an exemption is honoured only if the site itself carries the
// marker WRONG-not-UNMEASURED -- so the discriminator has to be CLAIMED where the next author reads it,
// not merely recorded in a list at the top of the guard.
//
// THE RULE HAS A THIRD FORM, AND IT IS ABOUT ADEQUACY CHECKS AGAINST EACH OTHER RATHER THAN AGAINST
// BEHAVIOUR. Ordering says which of two checks runs first; it cannot say anything useful when ONE edit
// invalidates SEVERAL of them, because an ordering reports exactly one and silently drops the rest:
//
//     Where one edit can invalidate several adequacy conditions, COLLECT them and report them TOGETHER.
//     Ordering only chooses which of the true findings the maintainer is allowed to see.
//
// A reviewer found this by trimming the `roots` axis of the flagship member to one element. Two adequacy
// conditions became false -- the R1 attribution, which names the KEY-CLASS axis, and the subject-
// distinguishing count, which names the ROOTS axis just edited. Only the first ran, so the run was RED for
// a true reason while pointing at the wrong edit. That is not a false green, but it costs the next
// maintainer a reproduction, which is the same currency the first two forms of this rule are paid in.
//
// Executed by AdequacyFindings_AreCollected_NotAssertedOneAtATime below: once a member starts collecting
// adequacy findings, a bare adequacy assertion may not be added back beside them.
//
// AND A RULE ABOUT THE GUARDS THEMSELVES, SINCE SEVERAL OF THEM READ THIS FILE'S OWN SOURCE:
//
//     A predicate's SUBJECT decides which text it reads. Subject is CODE -- read the line with literals
//     and comments stripped. Subject is PROSE or a LITERAL -- read the raw line. Say which, at the site.
//
// A code-subject predicate left on raw text is opted out of by QUOTING ITS OWN SUBJECT in a message or a
// comment, and in this file, writing about the thing being guarded is the house style: someone explaining
// WHY the collector matters would have disabled it. Found by a reviewer on the collector guard and
// reproduced on its sibling, where the escape was live rather than latent -- an assertion whose message
// mentioned the emptiness check was counted AS the emptiness check, and every violation ahead of it went
// unreported. Both were three lines from a stripping helper this file already had and already used.

// THE SECOND RULE FOR EVERY GUARD IN THIS FILE, AND IT IS ABOUT CORPORA RATHER THAN ASSERTIONS:
//
//     Every corpus axis must carry a PIN -- it is a slice of a shared constant, or a chokepoint
//     guarantees it is reached, or the test asserts a bounded reachability counter for it.
//
// An axis with none of the three can be trimmed to a single element while the suite stays green, which
// means the part of the claim it supports has stopped being executed and nothing says so. That is the
// distinction between green because a test RAN and green because it STOPPED running, and it is the one
// this file is least able to see from the inside.
//
// It is stated as a rule because it was learned as a non-travelling fix, and the worst one so far: the
// commit that repaired the `proses` axis by slicing it from GuardPrefixes wrote `trailings` ten lines
// below it as a fresh literal list with no pin at all -- the axis that makes the central claim
// DIRECTIONAL. A reviewer trimmed it to `[string.Empty]` and the test passed. The fix did not travel
// across ten lines of its own diff, which says the pin was being applied as a response to a finding
// rather than as a property every axis has to have.
//
// So the obligation is checked mechanically rather than remembered: `tools/testing/axis-trim-sweep.py`
// trims every axis in this file to one element and reports which trims stay green. MEASURED-AT the
// commit carrying this sentence, by RUNNING the tool rather than by recalling an earlier run: 38 axes
// were trimmed, RED 37, GREEN 1. The single GREEN is the tool's own documented blind spot (a leading
// `.. GuardPrefixes` spread survives a first-element trim, so the `proses` axis of
// Redact_AlwaysRedactsOnRightwardEvidence_AndOverRedactsOnlyWithAStatedCause still has its remaining
// elements) and was verified pinned by hand. The previous sentence here said 36 trimmed / 35 RED and
// was NOT re-measured for two subsequent axis additions; one of the two, `SweptSources`, had by then
// gone GREEN -- trimming it silently dropped LocalFileSystemBackend.cs from the swept corpus. It is now
// pinned per-source (see NoCommentStatesATestOutcomeCount_WithoutSayingWhenItWasMeasured), which is why
// this count is 38/37 and not 38/36. Run the tool after adding an axis, and re-date this sentence when
// you do. It is slow by construction -- one rebuild per axis -- because the property it measures is
// only visible from outside the test.
//
// THE THIRD RULE, AND IT IS ABOUT SENTENCES RATHER THAN ASSERTIONS OR CORPORA:
//
//     For every claim, name the subject the ENGLISH uses and the subject the CODE computes, and make sure
//     the corpus can generate a cell where they DISAGREE. If it cannot, the claim is unfalsifiable by
//     construction: it will read as verified forever, however many cells are added, because the cell that
//     would contradict it is not in the space.
//
// This file has had FOUR rounds of "the stated subject is not the executed one", and every individual
// repair was correct. They recurred because each round fixed the EXECUTED subject and left the English
// describing a different one, and no corpus contained the witness that would have shown the gap. The last
// of them: the claim said "evidence at or to the RIGHT of the key" while the code scanned
// `pathPortion + trailing`, whose pathPortion STARTS WITH THE ROOT -- left of the key. `/tbl/email=SENT`
// redacts with no evidence right of the key at all. The behaviour was right in every round; the sentence
// was false in every round.
//
// The remedy adopted is to name the subject ONCE IN CODE and have the English refer to the name, so the
// two cannot drift independently -- HasEvidenceInKeysPathRegion -- and then to count the distinguishing
// cells and refuse to run without them (`subjectDistinguishingCells`, bounded on both sides).
//
// Where a claim's two candidate subjects genuinely cannot disagree, that is allowed, but the REASON has to
// be executed rather than assumed, because the reason is always a property of the corpus and corpora move:
//
//   Redact_AlwaysRedactsOnRightwardEvidence_AndOverRedactsOnlyWithAStatedCause
//       English: the key's path region and after it.  Code: HasEvidenceInKeysPathRegion.
//       Witness: YES -- subjectDistinguishingCells, counted and bounded on both sides.
//
//   Redact_SeparatorEvidenceOutsideTheKeysPathRegion_IsNotEvidence
//       English: evidence outside that region.  Code: the message against the region.
//       Witness: YES -- a rooted row cannot be smuggled in, because the theory asserts the key is not
//       opened by a separator.
//
//   R4Classifier_DivergesFromTheEnumerationItReplaced_OnceABranchIsWeakened
//   Redact_SiblingRecognizerUnderTheSameTailAxis_DeclinesOnlyIntoFiledResiduals
//   BothRecognizers_OverTheWholeCorpus_DivergeOnlyInsideAFiledResidual
//       English: the PATH.  Code: the whole MESSAGE.
//       Witness: NO -- and the reason is that the corpus prose carries no separator, which is asserted
//       once at BothRecognizerProse rather than assumed three times.
//
//   Redact_MonotonicityMatrix_EveryCellOutsideTheAllowListStaysRedacted, its R4 clause
//       English: evidence ANYWHERE.  Code: value + close.
//       Witness: NO -- and any axis that ever puts a separator in front of the value now fails, naming
//       the cell.
//
//   SurvivalRows_WithSeparatorEvidence_AllHaveKeysInAFiledResidual
//       English: the ROW.  Code: the ROW.
//       Witness: they agree, and 5 of its 11 rows distinguish the row from the key-onward reading, so
//       the wider subject is REACHED rather than merely chosen.
//
// The three MESSAGE-vs-PATH claims are why that constant exists -- and naming it was only HALF the fix.
// Four sites went on spelling the literal by hand, so mutating the constant reached ONE of the three and
// a reviewer measured the other two going red only when the RAW COPIES were mutated: disjoint sets, and
// a remark in this file claiming otherwise. Every site now takes the decision from the constant, and
// TheCorpusProse_IsSpelledOnce_SoTheChokepointReachesEveryClaimThatDependsOnIt keeps it that way, and it
// derives its own spelling from the constant rather than repeating it.
//
// NO NUMBER IS WRITTEN HERE ON PURPOSE, AND THE MISSING NUMBER IS THE POINT. This sentence used to end
// "one edit to the constant turns six tests red instead of two". A reviewer measured five, and was right:
// the six was a relayed count that nobody re-ran. A count of how many tests a mutation reddens is the
// worst kind of prose this file can carry -- it cannot be checked by any guard here, it changes whenever
// a test is added or split, and unlike a count of members it rots INVISIBLY, because nothing goes red
// when it becomes wrong. The checkable claim is the one above it: every site takes the decision from the
// constant, and a guard fails if any site stops doing so.
//
// The rule that follows, since this file does carry other red counts: a red count describing the CURRENT
// tree is unwritable here, because nothing re-runs it. A red count recording a PAST measurement is
// allowed only where the prose says WHEN -- the branch-6 masking narrative on
// SegmentGuard_PerSiteProfile_IsMeasuredAtThisHeadRatherThanDescribed is that form, and its live half is
// executed by the test rather than asserted by the paragraph.
//
// AND THE RULE IS NOW A MECHANISM, BECAUSE STATING IT WAS NOT ENOUGH. The rule above was written and
// applied to exactly one site, and a reviewer then measured a violation of it that had shipped in the
// same commit that declared it: a paragraph in this file claiming a red count "at THIS head" that no
// longer matched the tree, and nothing failed when it stopped matching, which is the rule's own argument
// turned on the rule. NoCommentStatesATestOutcomeCount_WithoutSayingWhenItWasMeasured now enforces it
// over BOTH files, so a red count must either lose its number or carry the MEASURED-AT marker naming when
// it was taken. The generalisation, and this file has now paid for it three times: WHEN YOU DERIVE A
// RULE, SWEEP FOR ITS VIOLATIONS IN THE SAME COMMIT, and prefer a mechanism that makes the violation
// impossible over a rule that asks the next author to remember. A rule applied to the site that produced
// it is indistinguishable from a rule not held at all.
//
// THE RULE IS WIDER THAN ITS MECHANISM, AND THE GAP IS MEASURED RATHER THAN ESTIMATED. A red count is
// only the visible half; the general form is that ANY PROSE ASSERTION ABOUT THE CURRENT TREE WHICH
// NOTHING EXECUTES IS UNWRITABLE HERE. The count-shaped half is mechanised above. The verdict-shaped half
// -- "reverting this leaves that GREEN", "the suite stays green" -- is NOT, and the reason is a
// measurement taken before deciding: over both swept files, a vocabulary scan for GREEN/RED and their
// phrasings yields 26 comment lines, of which roughly six are claims about this tree and the rest are
// DISCUSSION OF THE FAILURE MODE ITSELF ("a 0-RED survivor", "rows stating a RED delta rot invisibly").
// Restricting the scan to verdicts used as a predicate rather than a noun phrase improves precision and
// makes recall worse, missing claims whose verdict follows an object. Both forms are a hand-list, which
// is the defect three consecutive rounds have been spent removing from the guard below; a wider hand-list
// is not a derivation. So the verdict half is DECLINED as a mechanism, on the record, rather than shipped
// as a low-precision guard that would read as coverage.
//
// AND THE RULE THAT CAME OUT OF FIXING THAT GUARD SIX TIMES, WHICH IS NOT THE RULE THE SIXTH FIX
// FOLLOWED. Each repair replaced a hand-spelled INSTANCE with the CLASS it stood for -- declaration
// position for the member, the name `Adequate` for the collector, the token `if (!` for negation, a
// binary answer for uncertainty, the token `.Add(` for append -- and each was correct and incomplete in
// the same way. The rule written here after the fifth was: WHEN YOU SPLIT A BINARY INTO A THIRD OUTCOME,
// APPLY IT TO EVERY AXIS THE CLASSIFIER READS, because a collapsed axis becomes the next hole and it
// will be the axis nobody thought of as an axis. That rule is right, and IT DID NOT SAVE THE CODE BESIDE
// IT: this paragraph said the classifier reads three axes -- span, certainty, append -- while it in fact
// also decided (`Assert.`) and identified its sink (`List<string>`), both still binary, and the DECIDE
// one failing OPEN. A reviewer walked out of a collector through `throw` and was told it was clean.
//
// SO THE SIXTH REPAIR DOES NOT ADD A THIRD ANSWER TO THE TWO MISSING AXES, because the count of axes is
// itself a hand-list -- enumerate them and the next defect is the axis left off the enumeration, one
// generation on. The classifier is INVERTED instead. It no longer asks a growing set of questions with
// enumerated bad answers; it asks ONE question per statement -- CAN I READ THIS AS ACCUMULATION? -- over
// a permissive whitelist of statements a collector may contain, and anything outside it is reported. The
// axis-multiplication is dissolved rather than counted, and the direction of ignorance is reversed: a
// spelling missing from a hand-list used to cost a whole member's silence, whereas a spelling missing
// from the whitelist costs one false finding, loudly, at the site. The sink is derived the same way --
// from the emptiness assertion the member already makes and from the arguments its own call sites pass,
// not from a declared collection type -- so "which container is a findings list" and "what is it called
// inside the collector" both stop being asked. Widening the sink pattern to `ICollection<string>` was
// the obvious repair and was declined for the reason above: a wider hand-list of type spellings is not
// a derivation, and the parameter's type was never what hid it -- its NAME was.
//
// AND THE SEVENTH GENERATION, WHICH THE INVERSION DID NOT PREVENT: the classifier read LINES while
// claiming something about STATEMENTS, so a whitelisted `if (c) { list.Add(x); throw ...; }` was admitted
// on its opening and its tail never looked at -- the same escape, spelled with a line break removed. A
// LINE IS AN INSTANCE OF A STATEMENT'S SPELLING, NOT THE STATEMENT, which makes this the same
// instance-for-class substitution as every generation before it. So: A SCANNER'S UNIT MUST MATCH THE UNIT
// ITS CLAIM IS ABOUT. The offered repair -- re-scan the tail of the line past its first brace -- would
// have closed the reported example and left the unit as the line; the body is cut at semicolons and
// braces at bracket depth zero instead, so four statements on one line are four and one statement across
// four lines is one. Both directions are pinned by probe.
//
// AND THE UNIT RULE HAS A SECOND EDGE THE SEVENTH GENERATION DID NOT SHOW. Two generations later the
// same substitution reappeared one level down: a rule about a STATEMENT read only the statement's FIRST
// PARENTHESISED GROUP, so `findings.Where(p).ToList().ForEach(f => throw ...)` put its effect past the
// group the scan had chosen. A parenthesis pair is as much a sub-unit of a statement as a line is a
// spelling of one, so the rule generalises: CHOOSING ANY UNIT SMALLER THAN THE CLAIM IS THE SAME DEFECT,
// whether the small unit is a line, a token or a bracket pair. It also generalises in the direction
// nobody had tested. A recognizer repaired nine times by "a body that used to pass now fails" has only
// ever been measured firing, and a recognizer that answered UNREADABLE to everything would have
// satisfied all nine demonstrations -- which is exactly the tenth defect found here, an ordinary
// expression-bodied collector reported as unreadable because the `=>` was left on its first statement.
// The first FALSE POSITIVE in a file whose every earlier defect was a silence. SO A RECOGNIZER MUST BE
// SHOWN TO STOP, NOT ONLY TO FIRE, and the classifier is now handed bodies directly with the cases that
// must stay clean carried beside the ones that must be reported.
//
// AND THE UNIT RULE HAS AN INSIDE. Correcting line -> statement stopped AT the statement boundary, so a
// statement's INTERIOR stayed permission-granted: each branch of the classifier consumed some region and
// handed on the rest, and every region it consumed without reading was an escape. Two were found -- an
// argument list behind a sink receiver, and a header CONDITION, which is jumped over to reach the
// remainder and carries no receiver to look suspicious. Patching regions one at a time is the generation
// engine itself, so the question was moved instead of the patch: EFFECTS ARE ASKED OF THE WHOLE
// STATEMENT; STRUCTURE IS ASKED OF ITS PARTS. A collector accumulates, so leaving the method disqualifies
// it wherever that is written, and there is no region left for the next reviewer to find because there
// are no regions in the question. The decide axis already worked this way; the two effect questions now
// agree instead of one of them being region-scoped.
//
// AND THE EFFECT QUESTION ITSELF WAS AN INSTANCE. Moving the question from a region to the whole
// statement fixed the SCOPE and left the PREDICATE where it was: `throw` is not the effect question, it
// is one answer to it. The question is DOES THIS STATEMENT STOP A FINDING REACHING THE ASSERTION, and
// destroying the sink answers it too -- `findings.RemoveAll(...)` produces the same silence as a throw
// and a worse one, since it suppresses every finding rather than those after the first, while leaving
// the member looking like an ordinary collector. Note the symmetry with what was already right here:
// LEAVING THE METHOD is a collector idiom and stays readable (`return`, `break`, `continue`), LEAVING THE
// RUN is not, and DESTROYING THE SINK belongs with leaving the run rather than with either.
//
// The repair is an inversion, not an extension: blacklisting `Clear`/`RemoveRange` fails OPEN on the
// first spelling nobody thought of, which is this file's defect eight times over, so the APPEND spellings
// are whitelisted and every other call on the sink is reported. That makes it the one whitelist here
// whose growth direction is UNSAFE, which is stated at its site rather than left for a reader to notice.
//
// AND A MUTANT THAT CHANGES TWO THINGS MEASURES LESS THAN ONE THAT CHANGES ONE. The whole-statement
// effect check was reported here as load-bearing for a single region, because the mutant demonstrating it
// ALSO restored the region-scoped check it had replaced -- and that second edit silently caught one of
// the two cases, so the fix was credited with half its actual strength. A reviewer re-ran it as a single
// edit and it names three. UNDERSTATING A GUARD IS STILL MIS-STATING IT: the direction is safer, but the
// next author reads the weaker claim and keeps a redundancy the file does not need, or removes the check
// believing it covers less than it does. Mutants are scoped to one edit here for the same reason findings
// are collected rather than asserted one at a time -- so that what fires names what caused it.
//
// AND A CLASS FIX IS RECOGNISABLE BY WHAT IT CLOSES WITHOUT BEING TOLD. Inverting the sink question --
// appends readable, every other call reported -- was written against `RemoveAll`, and it also closed
// `Clear`, reassignment behind the receiver, and `Environment.Exit` inside a lambda, the last of which a
// reviewer raised as a counter-example the following round and found already shut. An instance fix closes
// what it was shown; this is the difference, and it is the only reliable evidence of the difference.
//
// AND AN ARGUMENT CAN BE SOUND ABOUT THE USE IT WAS CHECKED FOR AND FALSE ABOUT THE ONE IT WAS NOT.
// "Any call on the sink is an append -- widening can only put MORE members under the guard" was examined
// and endorsed four times, by four different readers. It is true of MEMBERSHIP and false of ACCUMULATION,
// and one predicate was answering both, so `findings.Clear()` was read as accumulating. That is a sharper
// form of this file's recurring defect than any of the nine before it: not an unexamined claim, an
// EXAMINED one silently carried to a second use. So the two questions now have two predicates -- one
// deliberately wide, because a member cannot escape the population by being mentioned more; one derived
// positively from what an append IS, and SHARED with the readability answer because those two are the
// same question asked twice. Where a predicate serves two purposes, the argument for it must be made
// twice or it has only been made once.
//
// WHAT THAT SPLIT DOES AND DOES NOT CLOSE, MEASURED RATHER THAN CLAIMED. The attack that exposed it --
// a destructive call beside an honest append -- was already reported at the commit it was raised
// against, by the readability answer, so the split is defence in depth there rather than the closure.
// What it closes on its own is a local void that only READS its sink being classified a clean collector
// though it never collects. Both are pinned; the distinction between them is stated because a fix
// credited with the wrong closure is the mis-stated claim two rounds up, in the other direction.
//
// AND A NINTH GENERATION ON AN AXIS NONE OF THE EIGHT BEFORE IT TOUCHED. Every earlier repair here was
// about WHERE the scan looks (line, statement, region, argument, header) or WHAT it looks for (`.Add(`,
// `throw`, `Assert.`). This one is about WHETHER THE TEXT BEING SCANNED IS THE TEXT THAT IS THERE.
// `WithoutLiteralsOrComments` keyed the escape rule on the QUOTE CHARACTER rather than on the LITERAL
// KIND, so a char literal did not honour its own escapes and a verbatim string honoured escapes it does
// not have. Either one swallows THE REST OF ITS LINE, code included, and the same function feeds member
// tracking, sink derivation, assertion extraction and the ordering guard -- so one such literal blinds
// every source-reading guard on that line, and hides `Assert.` exactly as well as `throw`, both being
// asked of the already-filtered text. It is the smallest fix in this file and the widest blast radius,
// which is the ordering rule it earns: WHEN A PRE-PASS AND A CLASSIFIER ARE BOTH SUSPECT, FIX THE
// PRE-PASS FIRST, because every measurement of the classifier is taken through it.
//
// AND WHEN A REVIEWER REPORTS ONE HALF OF A MIS-KEYING, THE OTHER HALF IS ALREADY THERE. Only the char
// literal was reported. Asking what else the wrong key decided found the verbatim mirror unreported and
// live, and asking what the RIGHT key decides found a third kind -- a raw string -- open in the same
// direction, measured `Collector` for `"""a"b"""` because an odd quote count left the reader inside a
// literal. Fixing the reported half alone would have been the PARTIAL this file forbids. What makes
// covering all four a derivation rather than the hand-list this file keeps retiring is that THE KINDS
// ARE ENUMERATED BY THE LANGUAGE, NOT BY THE AUTHOR: C# has exactly four, and each answers the same two
// questions -- does it honour `\` escapes, and what closes it.
//
// AND THE ARGUMENT STOPPED HALFWAY THROUGH THE FUNCTION'S OWN NAME. The closed-grammar defence covered
// the LITERALS half and left the COMMENTS half exactly the shape it had just retired: C# enumerates the
// comment kinds as closely as the literal kinds -- there are two -- and only `//` was handled. The same
// sentence that justified doing all four literals demanded both comments, and it was not noticed because
// the fix had been checked against the reported defect rather than against its own reasoning. WHEN AN
// ARGUMENT IS ACCEPTED, RUN IT OVER EVERYTHING IT COVERS, NOT ONLY OVER WHAT PROMPTED IT.
//
// AND A DIRECTION MEASURED OVER TWO SPELLINGS WAS STATED AS A PROPERTY OF THE LIMIT. The round before
// pinned the line-scoped residual as failing CLOSED, on two measurements. A third spelling had the
// opposite direction: a QUOTE inside an unhandled block comment opens a literal that eats the rest of
// the line, and the reader goes BLIND rather than over-reporting. That is rule 22 -- a claim asserted
// over a space wider than the one measured -- for the third time in this file, and the first two times
// it was someone else's claim. The residual is now pinned only where it is measured, and the DIRECTION
// is a per-case pin rather than a sentence about the limit.
//
// AND A REVIEWER'S SPELLING IS THE START OF THE SEARCH, NOT THE END OF IT. The reported blind spelling
// put the comment BETWEEN statements, where the `/*` debris was itself unreadable, so the body still
// reported and the hole looked theoretical. Moving it INSIDE THE APPEND'S OWN ARGUMENT LIST makes the
// debris land in a statement that IS readable -- `findings.Add(why /* " */); throw ...` was a clean
// `Collector`. The pre-pass finding was real at the pre-pass; the guard-level exploit had to be built,
// and reporting "still reports" without building it would have understated a live hole as a near miss.
//
// AND THE PRE-PASS RESIDUAL IS EXECUTED, NOT ASSERTED. What remains is that the reader is LINE-SCOPED,
// so a construct spanning lines is mis-read: a multi-line verbatim string's continuation is read as
// code, which over-reports. That is pinned as a case rather than described, and it is stated as the one
// measurement it is rather than as a property of everything line-scoping touches.
//
// AND A DERIVED TAXONOMY READ THROUGH A HAND-LISTED RECOGNISER IS STILL A HAND-LIST. Covering all four
// literal KINDS was a genuine derivation -- a reviewer attacked it with eight shapes and it held -- and
// it was still one level short, because the code that RECOGNISED one of those kinds asked for `@`
// immediately before a quote, and the interpolated-verbatim kind has two legal spellings. So the taxonomy
// was closed and the opener was a list of one, and the failure was the same shape and the same fail-OPEN
// direction as the rows the derivation had just fixed. WHEN A CLASSIFICATION IS DERIVED, ASK WHETHER THE
// RECOGNISER OF EACH CLASS IS DERIVED TOO; a closed set of kinds recognised by an open-ended guess is
// the file's recurring defect hiding one level below where it was just evicted.
//
// AND "ATTACKED AND HELD" IS A CLAIM ABOUT THE ATTACK, NOT ABOUT THE CODE. The eight-shape attack that
// confirmed the kinds tested `$@"` and did not test `@$"`; the claim held against eight shapes and failed
// on the ninth, and it had already been written down as settled -- by the reviewer who ran it and by this
// file. Surviving an attack is evidence proportional to the attack, and recording it as a property of the
// code is the same over-claim as stating a direction measured over two spellings as a property of a
// limit, one round earlier. Both are now stated as the measurements they are.
//
// AND THE QUESTION WAS ASKED OF EVERY OPENER, NOT ONLY THE REPORTED ONE. `$@"`, `@$"`, a raw fence behind
// doubled interpolation modifiers, and the `u8` SUFFIX -- which is a suffix and not an opener -- are all
// pinned, and the two spellings of the interpolated-verbatim opener are pinned as a PAIR, because either
// alone passes against a reader that handles one order. That is how this survived the round that claimed
// to have closed it.
//
// AND COVERED IS NOT ATTRIBUTED. The predicate split shipped a round earlier was verified only by the
// fact that destructive calls were reported -- but a destructive call on some OTHER list is reported
// too, by generic unreadability, so no mutant could name the split and reverting it would have gone
// unnoticed by outcome. A reviewer measured that and named the consequence exactly: not distinguishable
// from generic unreadability by outcome, therefore not independently pinned. So the case has its own
// outcome now, and a mutant that drops it names six pinned cases at once. A CLAIM MUST BE
// DISTINGUISHABLE FROM THE THING IT EXCLUDES, OR THE TEST PASSING IS NOT EVIDENCE FOR IT.
//
// AND AN OUTCOME IS NAMED AFTER ITS PREDICATE, NOT AFTER THE ATTACK THAT MOTIVATED IT. The first name
// was `SinkMutatedNotAppended`, which fit `Clear` and lied about `findings.ForEach(f => Exit(1))` -- a
// call on the sink that mutates nothing. That is how a general check acquires a specific-sounding claim
// it cannot keep, which is the same defect this whole file is a record of, arriving through the name.
//
// AND A PIN THAT CANNOT DISTINGUISH THE WRONG ANSWER IS NOT A PIN. Non-nesting of block comments was
// first pinned on a line with ONE `*/`, so a mutant scanning to the LAST `*/` -- the precise shape of a
// plausible wrong fix -- PASSED against it. Two comments on one line is what actually pins it. Twice now
// a case here has looked like evidence and held none, and the mutant is the only thing that revealed it
// either time. This is why every claim in this file is mutated rather than inspected.
//
// AND A DISCLOSURE THAT CANNOT STOP BEING TRUE QUIETLY. Five rounds running, a reviewer re-measured the
// residual paragraph and found it wrong in one direction or the other -- each correction true when
// written, each stale within two commits, because an ENUMERATION OF SCENARIOS ROTS AGAINST CODE IT DOES
// NOT DERIVE FROM. That is the pin-table argument this file made and won, arriving late at its own
// disclosure. The sink residual is now stated as THE COMPLEMENT of the derivations beside it, so adding
// one shrinks it automatically; the classification residual is not prose at all but a CASE ASSERTED
// STILL OPEN, which turns RED the day it closes and forces the comment to be rewritten.
//
// AND NOTHING ELSE IN THE REPO CATCHES THIS CLASS, which is worth stating because it sets how much the
// guard is carrying. A reviewer confirmed that `dotnet format --verify-no-changes` exits 0 with the
// one-lined collector in the tree: the escape is idiomatic C#, the formatter has no opinion on it, the
// analyzers have none either, and CI is green. There is no downstream net under this one.
//
// WHAT WAS DONE INSTEAD, AND WHAT IT FOUND. The verdict claims were audited by MUTATION rather than by
// inspection, because presence is inspectable and correctness is not: a marker says when a claim was
// taken, never that it was true, and a claim can be pinned by a test asserting the wrong thing. Two of
// the candidates were re-run by performing the change they describe. BOTH WERE STALE -- the R4 predicate
// paragraph claimed a pin of one test where three now fire, and the tail-axis paragraph described a gap
// that has since closed. Both are corrected at their sites and dated. The remaining candidates are not
// re-run here and that is stated rather than implied; the residual is filed so it is a task rather than
// an intention.
//
// The names above are not decoration and they are not transcribed on trust:
// EveryMemberNamedInAComment_ResolvesToAMemberThatExists resolves every member-shaped token in every
// comment in this file against the members that actually exist. It found this table's own abbreviated
// names on its first run, which is the point -- an abbreviated name is exactly as ungreppable as a stale
// one, and five stale names had already shipped.
//
// The instruction that produced those two guards was "use nameof for every member named in prose here",
// and it splits in two because the language does:
//
//   in a COMMENT, nameof cannot be written, and see-cref is unchecked unless the project emits an XML
//   documentation file, which this one does not. So the reference is resolved by reflection instead --
//   derived, not transcribed, which is the same argument used to refuse a transcribed per-axis table.
//
//   in a STRING LITERAL, nameof CAN be written, so it must be, and
//   NoMemberNameIsTranscribedIntoAStringLiteral_BecauseNameofCanBeWrittenThere says so. Five names were
//   transcribed when that guard was written, four of them inside guards that READ THIS FILE BY NAME --
//   a rename would have left them scanning for a string that no longer occurs, silently.
//
// Note what is deliberately NOT written here: a transcribed per-axis table of which pin each axis
// carries. That is the artifact the second rule exists to replace -- a prose copy of a machine-checkable
// fact, which rots silently the first time an axis moves. The per-axis answer is the trim sweep's output,
// and it is regenerable in one command.
[Collection(DeltaSharp.Storage.Tests.BackendFaultInjectionCollection.Name)]
public sealed partial class PathDisclosureHygieneTests : IDisposable
{
    // A PII-shaped partition value, Hive/percent-encoded exactly as the writer emits it. The assertions check
    // BOTH the encoded and the decoded form, because an attacker or an auditor reading the log will apply
    // Uri.UnescapeDataString.
    private const string EncodedValue = "alice.taylor%40example.com";
    private const string DecodedValue = "alice.taylor@example.com";
    private const string PartitionedPath =
        "email=" + EncodedValue + "/region=EU/part-DD73B2610EAF39BB5D3E26FBEDD83A69.parquet";

    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public PathDisclosureHygieneTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "path-disclosure-" + Path.GetRandomFileName());
        _backend = new LocalFileSystemBackend(_root);
    }

    public void Dispose()
    {
        _backend.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>
    /// THE SEGMENT GUARD IS SUBSUMED BY THE ROOTLESS BRANCHES -- AND THIS ASSERTS BOTH HALVES OF THAT, so
    /// that "the guard's mutants no longer die" stops being an unexplained 0-RED and becomes a stated,
    /// executed relationship between two parts of the alternation.
    /// </summary>
    /// <remarks>
    /// <para>THE FINDING THIS REPLACES. MEASURED-AT the commit that added branch 6 and the two before
    /// it; not re-run since. Three rounds ago, dropping either scan origin from the guard
    /// (<c>^</c> or <c>/</c>) killed 6 and 5 tests respectively. At the commit that added branch 6, both
    /// dropped to ZERO. That is not the guard becoming safe to remove: the paired mutant -- drop the origin
    /// AND delete branch 6 -- kills 12, so the guard is still load-bearing. Branch 6 MASKS it.</para>
    /// <para>THIS IS THE SIXTH CAUSE OF A 0-RED SURVIVOR, RUNNING BACKWARDS, and it arrived in the same
    /// commit that named the sixth cause. Recorded there as "an equivalence certified before its masker
    /// existed"; this is the mirror image -- A NEW BRANCH SILENTLY UN-PINNING AN EXISTING GUARD. It is the
    /// more dangerous orientation, because the certification was correct when made AND the guard is still
    /// load-bearing; only its PIN is gone, so nothing fails and nothing tells you. The rule that follows:
    /// ANY COMMIT THAT ADDS A BRANCH TO THE ALTERNATION MUST RE-RUN EVERY GUARD-SITE MUTATION, because a
    /// new branch can absorb another guard's failures without touching it. This test is that re-run, made
    /// automatic.</para>
    /// <para>WHY THE SUBSUMPTION HOLDS, structurally rather than by census. The guard exists to stop a
    /// branch starting at a SYNTHETIC sub-key inside a value. Such a sub-key exists only when the value
    /// contains a separator -- and a value containing <c>/</c> satisfies branch 5's right anchor, while a
    /// value containing <c>\</c> with content after it satisfies branch 6's. Either way the REAL key
    /// matches, and the real key is strictly to the LEFT of any sub-key inside its own value, so the regex
    /// engine's leftmost rule gives it the match. The guard can only ever have blocked positions that lose
    /// anyway.</para>
    /// <para>The instrument is the SHIPPED pattern text, not a re-declared copy and not a build-time
    /// mutation: the guard is deleted from the compiled pattern string at runtime and both variants are run
    /// over the same corpus. That removes the two failure modes this file keeps finding in mutation
    /// evidence -- a mutant that is not the edit someone else ran, and a duplicated pattern that drifts.</para>
    /// </remarks>
    [Fact]
    public void SegmentGuard_IsSubsumedByTheRootlessBranches_ButOnlyWhileTheyExist()
    {
        string shipped = LocalFileSystemBackend.HivePartitionValue().ToString();

        // THE SITE COUNT, EXECUTED. It is spelled 5 times in source -- branches 1, 4, 5 and 6 directly, plus
        // once inside QuotedPathPrefix -- and QuotedPathPrefix is used by branches 2 and 3, so the compiled
        // pattern carries 6. The prose table beside the guard stated FOUR for two rounds after that stopped
        // being true. Counts of the code's own shape are the prose most likely to rot, so this one is a
        // number the build checks rather than a number someone maintains.
        Assert.Equal(6, shipped.Split(SegmentGuardPattern).Length - 1);

        // Branch 6 is the last alternative and the only one ending in RootlessBackslashPathValue.
        int lastBranch = shipped.LastIndexOf("|(?<![^/", StringComparison.Ordinal);
        Assert.True(lastBranch > 0, "branch 6 was not found; this test's surgery is stale.");
        string withoutBranch6 = string.Concat(
            shipped.AsSpan(0, lastBranch), shipped.AsSpan(shipped.Length - 1));

        Regex asShipped = Build(shipped);
        Regex shippedNoGuard = Build(shipped.Replace(SegmentGuardPattern, string.Empty, StringComparison.Ordinal));
        Regex noBranch6 = Build(withoutBranch6);
        Regex noBranch6NoGuard = Build(withoutBranch6.Replace(SegmentGuardPattern, string.Empty, StringComparison.Ordinal));

        int cells = 0;
        int subsumedDifferences = 0;
        int loadBearingDifferences = 0;
        int loadBearingKeyEchoes = 0;
        string? firstSubsumedDifference = null;
        string? firstKeyEcho = null;

        foreach (string prefix in GuardPrefixes)
        {
            foreach (string root in GuardRoots)
            {
                foreach (string key in GuardKeys)
                {
                    foreach (string separator in GuardSeparators)
                    {
                        foreach (string value in GuardValues)
                        {
                            foreach (string tail in GuardTails)
                            {
                                string message = prefix + root + key + separator + value + tail;
                                cells++;

                                string a = asShipped.Replace(message, "${key}=<value>");
                                string b = shippedNoGuard.Replace(message, "${key}=<value>");
                                if (!string.Equals(a, b, StringComparison.Ordinal))
                                {
                                    subsumedDifferences++;
                                    firstSubsumedDifference ??= message + "  ->  " + a + "  |  " + b;
                                }

                                string c = noBranch6.Replace(message, "${key}=<value>");
                                string d = noBranch6NoGuard.Replace(message, "${key}=<value>");
                                if (string.Equals(c, d, StringComparison.Ordinal))
                                {
                                    continue;
                                }

                                loadBearingDifferences++;

                                // The guard's actual job: without it, a run INSIDE the value is harvested as
                                // ${key} and echoed verbatim beside the marker.
                                if (d.Contains(Sentinel + "=<value>", StringComparison.Ordinal))
                                {
                                    loadBearingKeyEchoes++;
                                    firstKeyEcho ??= message + "  ->  " + d;
                                }
                            }
                        }
                    }
                }
            }
        }

        Assert.Equal(
            GuardPrefixes.Length * GuardRoots.Length * GuardKeys.Length * GuardSeparators.Length
            * GuardValues.Length * GuardTails.Length,
            cells);

        // HALF ONE: while branches 5 and 6 exist, the guard changes NOTHING. Deleting it from all six
        // compiled sites is output-identical on every cell. This is the assertion that explains the 0-RED,
        // and it is the one that will FAIL the day some future change makes the guard load-bearing again --
        // which is the moment the next reader needs to be told that it has regained a role and needs a pin
        // of its own. A comment saying "currently subsumed" cannot do that.
        Assert.True(
            subsumedDifferences == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"the segment guard changed {subsumedDifferences} of {cells} cells; it is no longer "
                + $"subsumed and needs an individual pin again. First: {firstSubsumedDifference}"));

        // HALF TWO, AND IT IS WHAT STOPS HALF ONE FROM READING AS "DELETE THE GUARD". Remove branch 6 and
        // the guard immediately regains its teeth, in the disclosure direction: a sub-key harvested from
        // inside the value and echoed through ${key}. Masking, not equivalence -- exactly what the paired
        // mutant showed, asserted here so it cannot quietly stop being true either.
        Assert.True(
            loadBearingDifferences > 0,
            "the guard is inert even without branch 6; half one would then be vacuous.");
        Assert.True(
            loadBearingKeyEchoes > 0,
            "no cell showed the guard preventing a sub-key echo; the corpus cannot see what it guards.");
        Assert.NotNull(firstKeyEcho);
    }

    /// <summary>
    /// The guard-site table, EXECUTED -- one measured number per site per branch set.
    /// </summary>
    /// <remarks>
    /// <para>MEASURED-AT the commit that wrote the row quoted here; not re-run since. The prose table
    /// beside the guard carried a row saying "delete branch 5 as well and branch 1's guard becomes
    /// load-bearing immediately: RED 5, and the partials are the BACKSLASH class". That row's
    /// CONCLUSION is still true -- this test measures branch 1's site going from 0 to 1,152 differing cells
    /// the moment branch 5 is removed -- but its stated EVIDENCE, a suite RED delta, stopped reproducing.
    /// Deleting branch 5 now fails a large set of tests, so the guard's own increment lands inside an
    /// instrument that is already saturated, and the increment reads as zero.</para>
    /// <para>That is cause TWO of a 0-RED survivor (a saturated corpus), not cause FOUR (masking). The
    /// distinction is worth the precision because the two have opposite remedies: masking is repaired by
    /// re-pinning against the masker, saturation by measuring something finer-grained than a pass/fail
    /// count. A reviewer reached the masking diagnosis from the suite delta alone and named branch 6 as the
    /// masker; branch 6 is measurably NOT the masker here -- see the set-equality assertion at the end,
    /// which shows the differing CELLS are byte-identical with branch 6 present and absent.</para>
    /// <para>The general lesson, and the reason this is a test rather than a corrected sentence: a row whose
    /// evidence is "N tests go red" is only as good as the corpus behind N, and it rots silently when
    /// anything else in the file starts failing first. Rows stating a count of the code's own shape rot
    /// visibly; rows stating a RED delta rot INVISIBLY, while still reading as plausible. Both tables in
    /// this recognizer make claims of that kind, the every-site sweep was built for one of them and not run
    /// on the other in the same commit, and this is the sweep for the other one.</para>
    /// </remarks>
    [Fact]
    public void SegmentGuard_PerSiteProfile_IsMeasuredAtThisHeadRatherThanDescribed()
    {
        string shipped = LocalFileSystemBackend.HivePartitionValue().ToString();
        Assert.Equal(6, TopLevelBranches(shipped).Count);

        string withoutBranch6 = WithoutBranches(shipped, 5);
        string withoutBranch5 = WithoutBranches(shipped, 4);
        string withoutBoth = WithoutBranches(shipped, 4, 5);

        List<string> corpus = ReducedGuardCorpus();
        Assert.Equal(
            4 * GuardRoots.Length * GuardKeys.Length * 2 * GuardValues.Length * 6,
            corpus.Count);

        // Sites 0..3 denote the same four positions in every row below -- branch 1, the two compiled copies
        // of QuotedPathPrefix, and branch 4 -- because branches 5 and 6 are the LAST two alternatives, so
        // removing them never renumbers the sites in front of them.
        (string Set, string Pattern, string[] Sites, int[] Differences)[] profile =
        {
            ("as shipped", shipped,
                ["branch 1", "quoted a", "quoted b", "branch 4", "branch 5", "branch 6"],
                [0, 0, 0, 0, 0, 0]),
            ("without branch 6", withoutBranch6,
                ["branch 1", "quoted a", "quoted b", "branch 4", "branch 5"],
                [0, 480, 480, 3840, 0]),
            ("without branch 5", withoutBranch5,
                ["branch 1", "quoted a", "quoted b", "branch 4", "branch 6"],
                [1152, 0, 0, 0, 0]),
            ("without branches 5 and 6", withoutBoth,
                ["branch 1", "quoted a", "quoted b", "branch 4"],
                [1152, 480, 480, 3840]),
        };

        // AND THE TABLE IS AN AXIS TOO. Trimmed to its first row -- "as shipped", all zeros -- this test
        // passed, because every remaining assertion is about a guard that changes nothing. The three rows
        // carrying NON-ZERO expectations are the entire measurement: they are where the guard is shown to be
        // load-bearing once a branch is removed. Bounded on both sides, because each end is a different way
        // for the table to stop measuring: with no non-zero row the profile says the guard is inert
        // everywhere, and with no all-zero row it loses the shipped configuration the other rows are
        // contrasted against.
        int rowsWithLoadBearingSites = profile.Count(row => row.Differences.Any(d => d != 0));
        Assert.True(
            rowsWithLoadBearingSites > 0 && rowsWithLoadBearingSites < profile.Length,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{rowsWithLoadBearingSites} of {profile.Length} profile rows expect a guard site to make a "
                + $"difference. At 0 the table only measures configurations where the guard is inert; at "
                + $"{profile.Length} the as-shipped row that proves subsumption has been dropped."));

        foreach ((string set, string pattern, string[] sites, int[] expected) in profile)
        {
            Assert.Equal(sites.Length, expected.Length);
            Assert.Equal(sites.Length, pattern.Split(SegmentGuardPattern).Length - 1);

            Regex baseline = Build(pattern);
            for (int site = 0; site < sites.Length; site++)
            {
                Regex mutant = Build(WithoutGuardOccurrence(pattern, site));
                int differences = 0;
                int subKeyEchoes = 0;
                foreach (string message in corpus)
                {
                    string with = baseline.Replace(message, "${key}=<value>");
                    string without = mutant.Replace(message, "${key}=<value>");
                    if (string.Equals(with, without, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    differences++;
                    if (without.Contains(Sentinel + "=<value>", StringComparison.Ordinal))
                    {
                        subKeyEchoes++;
                    }
                }

                Assert.True(
                    expected[site] == differences,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"guard site '{sites[site]}' {set}: expected {expected[site]} differing cells, "
                        + $"measured {differences}. The per-site profile has changed, which means some "
                        + $"branch now masks or unmasks this site. Re-derive the row, do not edit the "
                        + $"number."));

                // Every difference this guard makes is a SUB-KEY ECHO: without it, a run from inside the
                // value is harvested into ${key} and printed beside the marker. Asserting the class as well
                // as the count is what stops a future change from keeping the number while changing what
                // the guard is for.
                Assert.Equal(differences, subKeyEchoes);
            }
        }

        // BRANCH 6 IS NOT THE MASKER FOR BRANCH 1'S SITE. Counts being equal would be weak evidence; these
        // are the differing CELLS themselves, and the two sets are identical, so branch 6 has no bearing on
        // this site at all. The suite-level increment of zero that suggested otherwise is saturation.
        HashSet<string> withBranch6 = GuardSiteDifferences(withoutBranch5, 0, corpus);
        HashSet<string> withoutBranch6Too = GuardSiteDifferences(withoutBoth, 0, corpus);
        Assert.True(
            withBranch6.SetEquals(withoutBranch6Too),
            "branch 6 changes which cells branch 1's guard covers; the masking diagnosis would then stand.");
        Assert.Equal(1152, withBranch6.Count);
    }

    private static HashSet<string> GuardSiteDifferences(string pattern, int site, List<string> corpus)
    {
        Regex baseline = Build(pattern);
        Regex mutant = Build(WithoutGuardOccurrence(pattern, site));
        HashSet<string> differences = [];
        foreach (string message in corpus)
        {
            if (!string.Equals(
                    baseline.Replace(message, "${key}=<value>"),
                    mutant.Replace(message, "${key}=<value>"),
                    StringComparison.Ordinal))
            {
                differences.Add(message);
            }
        }

        return differences;
    }

    private static List<string> ReducedGuardCorpus()
    {
        List<string> corpus = [];
        foreach (string prefix in GuardPrefixes.Take(4))
        {
            foreach (string root in GuardRoots)
            {
                foreach (string key in GuardKeys)
                {
                    foreach (string separator in GuardSeparators.Take(2))
                    {
                        foreach (string value in GuardValues)
                        {
                            foreach (string tail in GuardTails.Take(6))
                            {
                                corpus.Add(prefix + root + key + separator + value + tail);
                            }
                        }
                    }
                }
            }
        }

        return corpus;
    }

    private static string WithoutGuardOccurrence(string pattern, int index)
    {
        int at = -1;
        for (int i = 0; i <= index; i++)
        {
            at = pattern.IndexOf(SegmentGuardPattern, at + 1, StringComparison.Ordinal);
        }

        Assert.True(at >= 0, "guard occurrence " + index.ToString(CultureInfo.InvariantCulture) + " is absent.");
        return pattern.Remove(at, SegmentGuardPattern.Length);
    }

    private static string WithoutBranches(string pattern, params int[] drop)
        => "(?:" + string.Join("|", TopLevelBranches(pattern).Where((_, i) => !drop.Contains(i))) + ")";

    // Splitting the alternation needs a depth- and class-aware scan: the separator alphabet itself is
    // spelled "(?:=|%3[Dd])", so a naive search for '|' lands INSIDE a branch rather than between two.
    private static List<string> TopLevelBranches(string pattern)
    {
        string body = pattern.Substring(3, pattern.Length - 4);
        List<string> branches = [];
        int depth = 0;
        bool inClass = false;
        int start = 0;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '\\')
            {
                i++;
            }
            else if (inClass)
            {
                inClass = c != ']';
            }
            else if (c == '[')
            {
                inClass = true;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == '|' && depth == 0)
            {
                branches.Add(body[start..i]);
                start = i + 1;
            }
        }

        branches.Add(body[start..]);
        return branches;
    }

    /// <summary>
    /// The folded R4 classifier is NOT equivalent to the enumeration it replaced, measured under mutation.
    /// </summary>
    /// <remarks>
    /// <para>When the enumerating form was folded onto the single helper, reverting the fold left the whole
    /// suite green, and that was recorded as an unpinned refactor made on principle. That disposition was
    /// too modest: the two classifiers agree at HEAD only because every cell they disagree on is currently
    /// REDACTED, so no decline exists for either to bucket. Delete branch 6 and the disagreement is 140
    /// cells wide -- the enumeration excuses 140 leaks that the corrected predicate flags.</para>
    /// <para>This is the pin, and it is built by runtime pattern surgery rather than by adding a corpus
    /// cell, because a cell invented to make a refactor look pinned measures the test. The corpus is the
    /// shipped one; only the recognizer is weakened.</para>
    /// </remarks>
    [Fact]
    public void R4Classifier_DivergesFromTheEnumerationItReplaced_OnceABranchIsWeakened()
    {
        const string Sentinel = "QQZZQQ";

        string shipped = LocalFileSystemBackend.HivePartitionValue().ToString();
        string withoutBranch6 = WithoutBranches(shipped, 5);
        Regex weakened = Build(withoutBranch6);

        int leaks = 0;
        int enumerationSays = 0;
        int correctedSays = 0;
        bool sawLoneTrailingBackslash = false;

        foreach ((string message, string key) in BothRecognizerCorpus())
        {
            string actual = weakened.Replace(message, "${key}=<value>");
            if (!actual.Contains(Sentinel, StringComparison.Ordinal))
            {
                continue;
            }

            leaks++;
            bool r1OrR2 = key.Any(char.IsWhiteSpace);

            // THE SUPERSEDED FORM. IT IS THE SPECIMEN, NOT THE INSTRUMENT -- nothing branches on it except
            // the comparison below, whose entire purpose is to show how far it is from the live classifier.
            // A reviewer flagged the risk directly: an enumeration sitting a few lines from the helper that
            // replaced it is the shape a future reader mistakes for the live one, and this file has already
            // lost five reviews to a copy nobody noticed was still wired up.
            //
            // So the separation is not left to this comment. TheSupersededEnumeration_SurvivesOnlyWhereItIsMeasured
            // reads this source file and FAILS if a containment test against a separator-only literal
            // appears in any member but three, each named with a reason. Copy these three lines anywhere
            // else in this file and the build goes red. That is the difference between a warning and a
            // guarantee, and it is the reason the specimen is safe to keep: the history is worth having,
            // and it is now inert by construction rather than by everyone remembering what it is.
            bool enumerationCallsItR4 = !message.Contains('/', StringComparison.Ordinal)
                && !message.Contains(":\\", StringComparison.Ordinal)
                && !message.Contains("\\\\", StringComparison.Ordinal);

            if (!enumerationCallsItR4 && !r1OrR2)
            {
                enumerationSays++;
            }

            if (HasSeparatorEvidence(message) && !r1OrR2)
            {
                correctedSays++;
                if (message.EndsWith("=" + Sentinel + "\\", StringComparison.Ordinal))
                {
                    sawLoneTrailingBackslash = true;
                }
            }
        }

        // 140 leaks the enumeration would have waved through. "They agree today" was a statement about the
        // corpus reaching the disagreement, not about the predicates. This is the decisive quantity, so it
        // is asserted BEFORE its components and before the corpus size -- written the other way round, a
        // corpus that moved would report a leak-count mismatch and never reach the divergence.
        Assert.Equal(140, correctedSays - enumerationSays);

        // AND THE CELL THAT CORRECTS THE RECORD. An earlier note in this file named `email=QQZZQQ\` as the
        // cell proving the tail axis reaches the class without the value-prefix axis. The measurement was
        // right and the cell was wrong: the run that produced it fired on the TWO-backslash spelling, which
        // the enumeration already flagged because it contains the `\\` it tests for. The ONE-backslash
        // spelling was excused by the enumeration and became visible only when the fold landed. A correct
        // conclusion resting on a misattributed cause -- inside the paragraph documenting that failure
        // mode -- so the cell is asserted here rather than described anywhere.
        Assert.True(
            sawLoneTrailingBackslash,
            "the single-trailing-backslash cell is absent; this test no longer pins what it claims to.");

        Assert.Equal(16, enumerationSays);
        Assert.Equal(156, correctedSays);

        // Adequacy last; see the note on the whole-corpus comparison.
        Assert.Equal(672, leaks);
    }

    /// <summary>
    /// The corpus prose is spelled ONCE in code, so the chokepoint that asserts it reaches every claim that
    /// depends on it.
    /// </summary>
    /// <remarks>
    /// <para>Naming the constant was not the fix. Four sites went on spelling the literal by hand, and a
    /// reviewer measured what that meant: mutating the CONSTANT and mutating the RAW COPIES turn disjoint
    /// pairs of tests red. So the guard that was supposed to cure a non-travelling fix did not itself
    /// travel, and the remark claiming three claims depended on the constant was true of one of them.</para>
    /// <para>A shared constant is not a shared decision unless every site takes the decision FROM it. That
    /// is checkable and therefore checked: the spelling may appear on exactly one non-comment line in this
    /// file, the line that declares it. A hand-written copy fails here, naming its line, instead of quietly
    /// removing a claim from the chokepoint's reach.</para>
    /// </remarks>
    [Fact]
    public void TheCorpusProse_IsSpelledOnce_SoTheChokepointReachesEveryClaimThatDependsOnIt()
    {
        // DERIVED, NOT TRANSCRIBED. The guard's own copy of the literal would be the second spelling this
        // test exists to forbid, and it would make an unrelated change to the constant fail here for a
        // reason that has nothing to do with the property.
        string spelling = "\"" + BothRecognizerProse + "\"";

        List<string> copies = [];
        int declarations = 0;
        string[] lines = EmbeddedSourceLines();

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || !trimmed.Contains(spelling, StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("private const string " + nameof(BothRecognizerProse), StringComparison.Ordinal))
            {
                declarations++;
                continue;
            }

            copies.Add(string.Create(CultureInfo.InvariantCulture, $"{EmbeddedSourceName}:{i + 1}  {trimmed}"));
        }

        Assert.True(
            copies.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{copies.Count} hand-written copies of the corpus prose remain, so mutating " +
                $"{nameof(BothRecognizerProse)} cannot reach the claims they build. " +
                $"Take the decision from the constant:{Environment.NewLine}" +
                $"{string.Join(Environment.NewLine, copies)}"));

        // Adequacy after the behavioural result: a renamed constant shows up as a copy above, so this cannot
        // be the assertion that hides one.
        Assert.Equal(1, declarations);
    }

    /// <summary>
    /// The both-recognizer corpus's prose carries no separator, so a claim over the MESSAGE and a claim over
    /// the key's PATH REGION are the same claim on this corpus.
    /// </summary>
    /// <remarks>
    /// <para>Three claims here take the whole message as their evidence subject while their English names
    /// the path. Every previous round of this file fixed such a gap at the site; this one closes it at the
    /// reason instead, because the reason is shared and a per-site fix has failed to travel seven times.</para>
    /// <para>The anti-vacuity half matters as much as the behavioural half: if every cell's path region bore
    /// evidence, or none did, the two subjects would agree for a reason that has nothing to do with the
    /// prose, and this guard would pass while guarding nothing.</para>
    /// </remarks>
    [Fact]
    public void BothRecognizerCorpus_CarriesNoSeparatorInItsProse_SoTheMessageIsItsOwnPathRegion()
    {
        List<string> strays = [];
        int cells = 0;
        int regionsWithEvidence = 0;

        foreach ((string message, string key) in BothRecognizerCorpus())
        {
            cells++;

            if (!message.StartsWith(BothRecognizerProse, StringComparison.Ordinal))
            {
                strays.Add("not led by the named prose: " + message);
                continue;
            }

            // Everything after the prose IS the path region: the corpus builds root + key + "=" + value + tail
            // with nothing else in between, and the root is part of the region (it is left of the key, and a
            // key's path region starts at its root -- the correction this round made in the flagship claim).
            string pathRegion = message[BothRecognizerProse.Length..];
            if (HasSeparatorEvidence(pathRegion))
            {
                regionsWithEvidence++;
            }

            if (HasSeparatorEvidence(message) != HasSeparatorEvidence(pathRegion))
            {
                strays.Add("message and path region disagree: " + message);
            }
        }

        Assert.True(
            strays.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{strays.Count} cell(s) make `the message` and `the key's path region` different subjects, " +
                $"so the three claims that say PATH while measuring MESSAGE are no longer the same claim. " +
                $"First: {strays.FirstOrDefault()}"));

        // Adequacy after the behavioural result, per the ordering rule at the top of this file: a corpus that
        // shrank leaves the result UNMEASURED, it does not make it WRONG.
        Assert.Equal(2760, cells);

        Assert.True(
            regionsWithEvidence > 0 && regionsWithEvidence < cells,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{regionsWithEvidence} of {cells} path regions carry evidence. At 0 or at all of them the " +
                $"two subjects agree for a reason unrelated to the prose and this guard proves nothing."));
    }

    /// <summary>
    /// Every member named in a comment in this file resolves to a member that exists.
    /// </summary>
    /// <remarks>
    /// <para>This file argues its own design in prose, and the prose names members constantly. A reviewer
    /// found the FIFTH stale name in it: a remark citing a member that has never existed, sitting inside
    /// the paragraph explaining why a claim needed checking. Nothing checked the citation, because a name
    /// in a comment is not a name to the compiler.</para>
    /// <para>The obvious remedy -- <c>nameof</c> -- cannot be written inside a comment, and the other
    /// candidate is not available either: <c>see cref</c> is only validated when the project emits an XML
    /// documentation file, which this one does not, and the dominant form here is the plain line comment
    /// where a cref cannot appear at all. So the index is DERIVED rather than transcribed: every
    /// member-shaped token in a comment is resolved by reflection against the members that exist. That is
    /// the same argument used to refuse a transcribed per-axis table, applied to the class of fact that
    /// produced the finding.</para>
    /// <para>The token shape is deliberately narrow -- an initial capital and at least one underscore, the
    /// shape of a test member name in this repository. Names without an underscore are not indexed, which
    /// is a stated gap rather than an oversight: widening it pulls in ordinary prose and a guard nobody can
    /// keep green is a guard everybody learns to suppress. At the commit that added it the shape matches 27
    /// tokens across this file, and its first run failed on EIGHT of them -- five in a table written one
    /// commit earlier, whose names had been abbreviated to fit a column. An abbreviated name is exactly as
    /// ungreppable as a stale one, which is a thing prose review does not notice and this does.</para>
    /// </remarks>
    [Fact]
    public void EveryMemberNamedInAComment_ResolvesToAMemberThatExists()
    {
        const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        HashSet<string> known = new(StringComparer.Ordinal);
        foreach (Type type in typeof(PathDisclosureHygieneTests).Assembly.GetTypes())
        {
            known.Add(type.Name);
            foreach (MemberInfo member in type.GetMembers(Everything))
            {
                known.Add(member.Name);
            }
        }

        List<string> unresolved = [];
        int examined = 0;
        int inThisClass = 0;
        string[] lines = EmbeddedSourceLines();

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in MemberShapedToken().Matches(trimmed))
            {
                examined++;
                if (!known.Contains(match.Value))
                {
                    unresolved.Add(string.Create(
                        CultureInfo.InvariantCulture, $"{EmbeddedSourceName}:{i + 1}  {match.Value}"));
                }
                else if (typeof(PathDisclosureHygieneTests).GetMember(match.Value, Everything).Length > 0)
                {
                    inThisClass++;
                }
            }
        }

        Assert.True(
            unresolved.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{unresolved.Count} member name(s) in comments resolve to nothing in this assembly. A name " +
                $"is what the next reader greps for, so a stale one costs more than a stale sentence:" +
                $"{Environment.NewLine}{string.Join(Environment.NewLine, unresolved)}"));

        // Adequacy after the behavioural result, per the ordering rule at the top of this file.
        Assert.True(
            examined > 20,
            string.Create(
                CultureInfo.InvariantCulture,
                $"only {examined} member-shaped tokens were found in comments. The token shape or the " +
                $"comment detection has stopped matching this file and the scan is reporting on nothing."));

        Assert.True(
            inThisClass > 10,
            string.Create(
                CultureInfo.InvariantCulture,
                $"only {inThisClass} of {examined} tokens name members of THIS class. Resolution is " +
                $"assembly-wide so that a cross-file citation is not a false positive; if almost nothing " +
                $"resolves here, the scan has drifted onto some other vocabulary."));
    }

    /// <summary>
    /// No member name is TRANSCRIBED into a string literal, because <c>nameof</c> exists there.
    /// </summary>
    /// <remarks>
    /// <para>The companion to the comment index, and the half of the instruction that can actually be
    /// obeyed literally: where <c>nameof</c> CAN be written, it must be. A member name copied into a
    /// literal is a reference the rename tooling cannot see, and several guards here read their own
    /// source by name -- a rename would leave them scanning for a string that no longer occurs. HOW MANY
    /// IS NOT WRITTEN HERE: this sentence said "four" and a reviewer measured otherwise, so the number
    /// lives in a constant that
    /// <see cref="StructuralCountsThisFileReliesOn_AreExecutedRatherThanWrittenIntoProse"/> executes.</para>
    /// <para>The inside-a-literal test is a heuristic over quote parity, with interpolation holes excluded
    /// so that <c>$"{SomeMember}"</c> counts as code rather than text. Its adequacy is not asserted from
    /// the heuristic's own reasoning: it reports zero here only after four real transcriptions were
    /// converted, and a planted one is reported by line.</para>
    /// <para>The length floor exists for the same reason the comment index has a shape floor. Short member
    /// names collide with ordinary words and with BCL members named in prose, and a guard that cries wolf
    /// gets suppressed rather than obeyed.</para>
    /// </remarks>
    [Fact]
    public void NoMemberNameIsTranscribedIntoAStringLiteral_BecauseNameofCanBeWrittenThere()
    {
        const int ShortestIndexedName = 8;
        const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        HashSet<string> ours = new(StringComparer.Ordinal);
        foreach (MemberInfo member in typeof(PathDisclosureHygieneTests).GetMembers(Everything))
        {
            if (member.Name.Length >= ShortestIndexedName)
            {
                ours.Add(member.Name);
            }
        }

        List<string> transcriptions = [];
        int examined = 0;
        string[] lines = EmbeddedSourceLines();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in LongMemberShapedToken().Matches(line))
            {
                if (!ours.Contains(match.Value))
                {
                    continue;
                }

                examined++;
                if (IsInsideStringLiteral(line, match.Index))
                {
                    transcriptions.Add(string.Create(
                        CultureInfo.InvariantCulture, $"{EmbeddedSourceName}:{i + 1}  {match.Value}"));
                }
            }
        }

        Assert.True(
            transcriptions.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{transcriptions.Count} member name(s) are transcribed into string literals where " +
                $"nameof would be checked by the compiler:{Environment.NewLine}" +
                $"{string.Join(Environment.NewLine, transcriptions)}"));

        // Adequacy after the behavioural result, per the ordering rule at the top of this file.
        Assert.True(
            examined > 100,
            string.Create(
                CultureInfo.InvariantCulture,
                $"only {examined} occurrences of a member name of this class were seen in code. The token " +
                $"shape has stopped matching this file and the scan is reporting on nothing."));
    }

    /// <summary>
    /// True when <paramref name="index"/> falls inside a string literal on <paramref name="line"/> and not
    /// inside an interpolation hole within one.
    /// </summary>
    private static bool IsInsideStringLiteral(string line, int index)
    {
        bool inString = false;
        bool inHole = false;

        for (int i = 0; i < index && i < line.Length; i++)
        {
            char c = line[i];

            if (inString && c == '\\')
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                inHole = false;
            }
            else if (inString && c == '{')
            {
                inHole = true;
            }
            else if (inString && c == '}')
            {
                inHole = false;
            }
        }

        return inString && !inHole;
    }

    /// <summary>
    /// A member name long enough not to collide with ordinary words: an initial capital and at least eight
    /// characters.
    /// </summary>
    [GeneratedRegex(@"[A-Z][A-Za-z0-9_]{7,}")]
    private static partial Regex LongMemberShapedToken();

    /// <summary>
    /// The shape of a member name as this repository writes them: an initial capital and at least one
    /// underscore.
    /// </summary>
    [GeneratedRegex(@"[A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+")]
    private static partial Regex MemberShapedToken();

    /// <summary>
    /// The one prose the both-recognizer corpus puts in FRONT of its path region.
    /// </summary>
    /// <remarks>
    /// <para>Three claims over this corpus take the WHOLE MESSAGE as their evidence subject
    /// (<c>R4Classifier_DivergesFromTheEnumerationItReplaced_OnceABranchIsWeakened</c>,
    /// <c>Redact_SiblingRecognizerUnderTheSameTailAxis_DeclinesOnlyIntoFiledResiduals</c> and
    /// <c>BothRecognizers_OverTheWholeCorpus_DivergeOnlyInsideAFiledResidual</c>) while their English says
    /// "the PATH". Those are the same subject here for exactly one reason, and it is not that the corpus
    /// has no prose -- every one of its 2760 cells has some. It is that this prose carries no separator, so
    /// evidence in the message and evidence in the key's path region cannot disagree.</para>
    /// <para>That is a property of a string literal, which is the kind of thing that changes without anyone
    /// noticing they have changed three claims. Naming it here was HALF the fix and was reported as the
    /// whole of it: four sites went on spelling the literal by hand, so mutating this constant reached one
    /// of the three claims and a reviewer measured the other two going red only when the RAW COPIES were
    /// mutated -- disjoint sets. Every site now takes the decision from here, and
    /// <c>TheCorpusProse_IsSpelledOnce_SoTheChokepointReachesEveryClaimThatDependsOnIt</c> keeps it that
    /// way; the claim that this constant reaches all three is checked by
    /// <c>BothRecognizerCorpus_CarriesNoSeparatorInItsProse_SoTheMessageIsItsOwnPathRegion</c>.</para>
    /// </remarks>
    private const string BothRecognizerProse = "Could not delete file ";

    private static IEnumerable<(string Message, string Key)> BothRecognizerCorpus()
    {
        const string Sentinel = "QQZZQQ";
        string[] roots = { "/tbl/", string.Empty, "C:\\tbl\\", "\\\\srv\\s\\", "a/b/" };
        string[] keys = { "email", "K", "my col", "o'brien", "o'brien y", "a.b" };
        string[] tailShapes =
        {
            string.Empty, "S", "SS", "Spart-0.parquet", "Spart 0.parquet", "Sa b", "Sa bSc",
            "S..", "S.", " and more", "'.", "Sp q", "/S", "S/",
        };
        string[] tails = tailShapes
            .SelectMany(shape => shape.Contains('S', StringComparison.Ordinal)
                ? new[] { "/", "\\" }.Select(sep => shape.Replace("S", sep, StringComparison.Ordinal))
                : new[] { shape })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] valueShapes = { Sentinel, Sentinel + "%40x", "x\\" + Sentinel, Sentinel + "'s" };

        foreach (string root in roots)
        {
            foreach (string key in keys)
            {
                foreach (string value in valueShapes)
                {
                    foreach (string tail in tails)
                    {
                        yield return (BothRecognizerProse + root + key + "=" + value + tail, key);
                    }
                }
            }
        }
    }

    /// <summary>
    /// No row asserting a message SURVIVES may carry separator evidence with a clean key.
    /// </summary>
    /// <remarks>
    /// <para>A row that pins "this string passes through unchanged" is half a pin. It is the right half for
    /// prose and the wrong half for a path, and the difference is invisible at the row -- which is how
    /// <c>st_mode=0100644\</c> came to sit in the survival corpus asserting exactly the behaviour that was
    /// the leak, and how a reviewer then cited it as protection while withdrawing a real finding.</para>
    /// <para>A sweep found no unfiled half-pin left. That is a statement about today: nothing stops a
    /// maintainer adding one, and the row would look entirely reasonable. So the class is closed by
    /// construction instead. Every survival row carrying separator evidence must have a key candidate that
    /// lands in filed R1/R2 -- whitespace or a quote in the segment before the separator. A row with a
    /// CLEAN key and separator evidence is a path shape asserted to survive, and fails here.</para>
    /// <para>The rows are read off the theory by reflection rather than re-listed, because a guard over a
    /// transcribed copy of a corpus guards the copy.</para>
    /// </remarks>
    [Fact]
    public void SurvivalRows_WithSeparatorEvidence_AllHaveKeysInAFiledResidual()
    {
        IEnumerable<InlineDataAttribute> rows = typeof(PathDisclosureHygieneTests)
            .GetMethod(nameof(Redact_LeavesOperationalDiagnosticsVerbatim))!
            .GetCustomAttributes<InlineDataAttribute>();

        int examined = 0;
        int withEvidence = 0;
        List<string> unfiled = [];

        foreach (InlineDataAttribute row in rows)
        {
            string detail = (string)row.GetData(null!).Single()[0]!;
            examined++;
            if (!HasSeparatorEvidence(detail))
            {
                continue;
            }

            withEvidence++;

            int separator = detail.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            string beforeSeparator = detail[..separator];
            int lastSeparator = beforeSeparator.LastIndexOfAny(['/', '\\']);
            string keyCandidate = beforeSeparator[(lastSeparator + 1)..];

            bool filed = keyCandidate.Any(char.IsWhiteSpace)
                || keyCandidate.Contains('\'', StringComparison.Ordinal)
                || keyCandidate.Contains('"', StringComparison.Ordinal);

            if (!filed)
            {
                unfiled.Add(detail + "   key candidate: '" + keyCandidate + "'");
            }
        }

        Assert.True(
            unfiled.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{unfiled.Count} survival row(s) assert a path-shaped message passes through with a CLEAN "
                + $"key. That is a half-pin: it pins the over-redaction direction of a string that should "
                + $"redact, and pins nothing about disclosure. Either the row belongs in the accepted "
                + $"over-redaction corpus with its rendered text, or it is a leak."
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, unfiled)}"));

        // Behaviour first, adequacy last. These are corpus-adequacy checks: a corpus that read no rows
        // leaves the finding above UNMEASURED, not wrong, so by the rule at the top of this file they go
        // after it -- unlike the two exempted preconditions, whose failure would make it a fiction.
        Assert.True(examined > 0, "no rows were read; the reflection is stale.");
        Assert.True(
            withEvidence > 0,
            "no survival row carries separator evidence, so this guard is vacuous.");
    }

    /// <summary>
    /// The recognizer ALWAYS redacts when separator evidence sits in the key's PATH REGION or after it, and
    /// over-redacts only where the prose independently redacts -- one direction without exception, one
    /// false with exactly one stated cause. It is deliberately NOT a biconditional; see the remarks for
    /// the three rounds of narrowing that retired that shape.
    /// </summary>
    /// <remarks>
    /// <para>THIS IS THE FIRST CHARACTERISATION OF THIS RECOGNIZER RATHER THAN AN ENUMERATION OF IT, and a
    /// reviewer supplied it. Every prior statement was a list -- of branches, of mechanisms, of residuals,
    /// of shapes -- and every list in this file has at some point been short by one. A biconditional cannot
    /// be short by one: it is falsified by a single cell in either direction, so the corpus is free to
    /// range over anything at all.</para>
    /// <para>WHICH IS WHY IT IS SCOPED. The claim as offered was "behaviour is now exactly redact-iff-
    /// separator-evidence", measured over clean keys. It is FALSE for a whitespace key: cells here carry
    /// evidence and are not redacted, every one of them residual R1 (filed, #704/#708), where the key class
    /// excludes whitespace and the decline has nothing to do with evidence. A claim asserted over a space
    /// wider than the one measured is this PR's recurring defect, and it is worth noting that it recurred
    /// in the observation that finally characterised the thing.</para>
    /// <para>AND THEN IT RECURRED AGAIN, ONE CONJUNCT IN, WHICH IS WHY THE SUBJECT IS NOW RIGHT RATHER THAN
    /// THE SCOPE NARROWER. The first version asked <c>HasSeparatorEvidence</c> of the WHOLE MESSAGE while
    /// holding the prose prefix fixed at a separator-free <c>"Could not delete file "</c>. A reviewer varied
    /// the axis nobody had varied and the claim fell over: <c>open /proc/self/fd failed: k=QQZZQQ</c> carries
    /// a slash in its PROSE and is correctly declined, so a per-MESSAGE predicate was being used to
    /// characterise a per-TOKEN property. Measured over a varied prose axis, that costs 108 breaches.</para>
    /// <para>The remedy is not a third qualifier. A qualifier can always be one conjunct short -- that is
    /// twice now -- so the subject is corrected instead: evidence is asked of the PATH PORTION, and the
    /// prose prefix becomes an executed axis. Over the same corpus that produces 108 breaches against the
    /// message, the path portion produces ZERO. No region-finder is written to achieve this and none may be:
    /// the test CONSTRUCTS the path portion, so it never has to FIND it, and a second thing that locates
    /// path regions is exactly the two-classifiers-for-one-concept precondition every drift here has
    /// shared.</para>
    /// <para>The error direction was safe throughout -- all 108 were DECLINES, never disclosure -- because
    /// an over-wide evidence predicate makes a cell look unexplained rather than excused. Loud, not
    /// silent.</para>
    /// <para>THE SUBJECT WAS STILL ONE SIDE SHORT, AND A SECOND REVIEWER FOUND THE OTHER ONE. Correcting the
    /// subject to the path portion fixed the prose-in-FRONT case by construction, but the corpus could then
    /// only ever place evidence adjacent to the value, so a third class stayed unreachable: evidence sitting
    /// in prose to the LEFT of the key. <c>Could not delete file /tbl/a.txt while handling email=SENT</c> is
    /// a clean key, carries a slash, and is correctly DECLINED -- a breach of the claim as written, in the
    /// disclosure direction, and unattributed.</para>
    /// <para>What made this blocking was not the behaviour, which is right, but the CLAIM. "Redact iff
    /// separator evidence exists" tells the next reviewer that a value in a message containing a slash is
    /// necessarily redacted -- which is precisely the hunt that produced opt-outs 1 through 3. A false
    /// safety property is a more dangerous artifact than a behaviour gap, because it retires a search.</para>
    /// <para>So the claim is stated DIRECTIONALLY, and the corpus is widened on BOTH sides to execute it:</para>
    /// <para><b>The recognizer redacts if and only if separator evidence exists in the key's PATH REGION or
    /// after it</b> -- the region being what <see cref="HasEvidenceInKeysPathRegion"/> is asked of, which is
    /// where that phrase is defined rather than described.</para>
    /// <para>Both halves of that direction are measured, and they fail in opposite ways if either side is
    /// dropped. Evidence to the LEFT does not count and must not: it is behind the match position and the
    /// recognizer cannot reach it -- asking the whole message costs 108 breaches. Evidence to the RIGHT does
    /// count and must: the value run is <c>[^/]*</c> and extends rightward to the next separator, so a slash
    /// in trailing prose really does pull the run across it -- asking only the path portion costs 324, the
    /// already-accepted over-redaction class pinned on the delimiter-adjacent-prose rows. Widening the
    /// corpus on both sides and asking <c>pathPortion + trailing</c> yields ZERO breaches in either
    /// direction.</para>
    /// <para>That is three subjects in three rounds, each one conjunct short of the last, and the pattern is
    /// worth naming: a scope word is cheaper than a corpus axis, which is why it keeps being the thing that
    /// is wrong. Each fix here made the qualifier EXECUTED rather than implied.</para>
    /// <para>AND THEN THE OBSERVABLE TURNED OUT TO HAVE A SUBJECT TOO, WHICH IS THE PART EVERY EARLIER ROUND
    /// MISSED. Round 39 corrected the subject of the PREDICATE -- what evidence is asked of -- and left the
    /// subject of the OBSERVABLE whole-message: <c>changed</c> still meant "the message differs", not "the
    /// value was redacted". When the prose itself contains a Hive segment, the message changes for a reason
    /// that has nothing to do with the path portion, because the prose's own value run -- <c>[^/]*</c>,
    /// greedy -- starts inside the prose and reaches rightward ACROSS the key:</para>
    /// <code>a=1/b=2 email=SENTINEL%2F   -&gt;   a=&lt;value&gt;/b=&lt;value&gt;</code>
    /// <para>Branch 4 anchors on the slash in the PROSE, takes <c>b</c> as its key, and its <c>[^/]*$</c> run
    /// swallows the rest of the line including the value. So evidence to the left CAN reach the value after
    /// all -- not as evidence, but as the tail of somebody else's match -- and the directional claim was
    /// itself one conjunct short. Third time.</para>
    /// <para>The falsifying prose was not exotic. It is <c>"a=1/b=2 "</c>, and it had been sitting in this
    /// file's own <c>GuardPrefixes</c> constant the whole time, in a list whose own comment states the rule
    /// the prose axis was breaking: a shared constant is not a shared decision unless the slice is taken from
    /// the constant itself. The axis is now that slice.</para>
    /// <para>Note that drawing from the constant does not make the falsifier unreachable -- it makes it
    /// REACHABLE, which is the point. Adding the string turns the old assertion RED at 216 cells. Exposure is
    /// the service a shared corpus performs; it is not the fix.</para>
    /// <para>THE FIX IS TO STOP CLAIMING A BICONDITIONAL, because the recognizer does not have one, and three
    /// rounds of narrowing were three attempts to keep a false shape by qualifying it. What it has is one
    /// direction that holds without exception and one that fails with exactly one cause:</para>
    /// <list type="number">
    /// <item><b>Evidence in the key's path region or after it always redacts.</b> This is the safety half. Zero breaches,
    /// and it is stated with no qualifier at all.</item>
    /// <item><b>The converse is false, and every counterexample is an independently-redacting prose.</b>
    /// Asserted as a CAUSE -- the prose is handed to the recognizer alone -- so a cell excused here must be
    /// excused by a mechanism already known. A second mechanism cannot hide behind the first one's name,
    /// which is what an enumeration of prose shapes would have permitted.</item>
    /// </list>
    /// <para>Both counterexample directions are over-redaction, never disclosure, and that asymmetry is why
    /// the artifact is stated this way round: the half that can leak is the half with no exceptions.</para>
    /// <para>The remaining three statements below are unchanged in substance, each with its own scope:</para>
    /// <list type="number">
    /// <item>For a CLEAN key both directions hold exactly -- 0 breaches either way, once the
    /// independently-redacting prose is attributed. This is the strongest scope, and it is a scope, not
    /// the general claim.</item>
    /// <item>The safe direction NEVER breaks, for any key class: nothing is redacted without evidence, so
    /// the over-redaction posture is not traded away to obtain (1).</item>
    /// <item>Every breach of (1) under a non-clean key is a whitespace key, i.e. R1. A quote-only key does
    /// not decline anywhere in this corpus.</item>
    /// </list>
    /// <para>The evidence axis deliberately includes tokens that LOOK like separators and are not --
    /// <c>%2F</c>, <c>%5C</c>, <c>:</c>, <c>|</c>, <c>..</c>, U+2215 DIVISION SLASH, TAB/CR/LF/NUL -- because
    /// the failure this recognizer has actually had, five times, is treating a near-separator as one or a
    /// real separator as not one.</para>
    /// </remarks>
    /// <summary>
    /// The five cells that falsified the undirected claim, kept as rows so the direction cannot quietly
    /// become undirected again.
    /// </summary>
    /// <remarks>
    /// <para>Every row is a CLEAN key -- no whitespace, no quote, so not R1 and not R2 -- in a message that
    /// carries unmistakable separator evidence, and every row is correctly DECLINED. The evidence is outside
    /// the key's own path region: BEHIND the key is necessary but not sufficient, because a rooted key has
    /// its evidence behind it too and redacts. The recognizer scans forward from a path-region start and cannot see it, and should not:
    /// treating prose that merely MENTIONS a path as licence to redact a later token is how a recognizer
    /// starts redacting <c>errno=13</c>.</para>
    /// <para>These rows do NOT assert survival for its own sake -- a test asserting a string survives is half
    /// a pin. Each asserts the message is byte-identical, which is the FULL-or-DECLINE property: no partial
    /// rewrite, and the sentinel is a constructed value, so its presence is a decline rather than a leak of
    /// anything real. They are here to make the word RIGHTWARD load-bearing: delete it from the claim and
    /// these five become the counterexamples.</para>
    /// </remarks>
    [Theory]
    [InlineData("Could not find a part of the path '/tbl/data.parquet'. email=QZSENTINELZQ")]
    [InlineData("Access to the path 'C:\\tbl\\data.parquet' is denied. email=QZSENTINELZQ")]
    [InlineData("Could not delete file /tbl/a.txt while handling email=QZSENTINELZQ")]
    [InlineData("unlinkat /tbl/x failed; owner=QZSENTINELZQ")]
    [InlineData("Could not delete file /tbl/x.parquet (email=QZSENTINELZQ)")]
    public void Redact_SeparatorEvidenceOutsideTheKeysPathRegion_IsNotEvidence(string message)
    {
        Regex recognizer = LocalFileSystemBackend.HivePartitionValue();

        string redacted = recognizer.Replace(message, "${key}=<value>");

        Assert.Equal(message, redacted);
        Assert.True(
            HasSeparatorEvidence(message),
            "Row is pointless unless the message really does carry separator evidence somewhere.");
        Assert.False(
            HasSeparatorEvidence(message[message.LastIndexOf('=')..]),
            "...and pointless unless that evidence sits before the key, which is half the claim.");

        // AND THE OTHER HALF, WHICH THE NAME USED TO ASSERT AND THE BODY DID NOT. "Left of the key" is not
        // the class: `/tbl/email=SENT` has all its evidence left of the key and redacts, because the
        // separator OPENS the key's own path region. The class is evidence outside that region, and the
        // cheapest exact test for "inside" is the one the recognizer itself uses -- the single character in
        // front of the key. Asked of HasSeparatorEvidence, so this stays the file's one separator decision
        // rather than becoming a second region-finder.
        //
        // Without this the five rows are all rootless by accident and the corpus cannot reach a rooted key,
        // which is precisely the unfalsifiable-by-construction shape a reviewer named: a scope that holds
        // because no cell can test it. Now a rooted row cannot be added without turning this red.
        int keyStart = message.LastIndexOf('=') - 1;
        while (keyStart > 0 && char.IsLetterOrDigit(message[keyStart - 1]))
        {
            keyStart--;
        }

        Assert.False(
            keyStart > 0 && HasSeparatorEvidence(message[(keyStart - 1)..keyStart]),
            "...and pointless unless the key is NOT opened by a separator: a rooted key belongs to the class "
            + "that REDACTS, and a row like `/tbl/email=SENT` smuggled in here would be asserting the "
            + "opposite of the behaviour.");
    }

    /// <summary>
    /// THE SUBJECT every evidence claim in this file is asked of, named once so that no English sentence has
    /// to describe it a second time.
    /// </summary>
    /// <remarks>
    /// <para>It is the PATH REGION THE KEY SITS IN, plus everything after that region -- not the key, and not
    /// the whole message. The root belongs to the subject even though it sits to the LEFT of the key, because
    /// the separator that opens the segment is exactly what branch 1 anchors on: <c>/tbl/email=SENT</c>
    /// redacts, and its only evidence is the slash in front of the key.</para>
    /// <para>This exists because the English and the code had drifted apart for four rounds. Each round
    /// corrected the subject the CODE computes and then described it in a sentence naming a different one --
    /// most recently "at or to the RIGHT of the key", while the expression being evaluated began with the
    /// root. A reviewer measured the gap on the most ordinary shape in the system: <c>/tbl/email=SENT</c> and
    /// <c>C:\tbl\email=SENT</c> both redact with no evidence at or right of the key at all. The behaviour was
    /// right in every round; the sentence was false in every round.</para>
    /// <para>A sentence cannot drift from a name the way it drifts from an expression, so the claims now say
    /// "the key's path region" and this method is what that phrase means.</para>
    /// </remarks>
    private static bool HasEvidenceInKeysPathRegion(string pathRegion, string afterTheRegion) =>
        HasSeparatorEvidence(pathRegion + afterTheRegion);

    [Fact]
    public void Redact_AlwaysRedactsOnRightwardEvidence_AndOverRedactsOnlyWithAStatedCause()
    {
        Regex recognizer = LocalFileSystemBackend.HivePartitionValue();

        // The axis whose absence made the first version of this claim one conjunct short. It must contain
        // prose bearing a separator, a quote and a colon, since those are what the recognizer's anchors and
        // the quoted branches key on -- a prose axis of separator-free strings would re-create the defect
        // while appearing to fix it.
        // AND THE AXIS IS A SLICE OF THE CONSTANT, NOT A SECOND HAND-WRITTEN LIST. It used to be the latter,
        // and that is precisely how a hive-shaped prefix -- `a=1/b=2 `, sitting in `GuardPrefixes` all along --
        // stayed unreachable by the corpus that most needed it. `GuardPrefixes`' own comment already states the
        // rule this violated: a shared constant is not a shared decision unless the slice is taken from the
        // constant itself. The extras below are additions to that slice, never a replacement for it.
        string[] proses =
        [
            .. GuardPrefixes,
            "open /proc/self/fd failed: ",
            "stat '/usr/lib' -> ",
            "write failed: errno=13, ",
            "C:\\Windows\\System32 refused ",
            "\"quoted prose\" ",
            "path: ",
        ];

        // AND THE OBLIGATION THIS AXIS CARRIES IS EXECUTED RATHER THAN WRITTEN DOWN. A prose string that ENDS
        // in a separator is not prose, it is a ROOT: every cell it generates is a mis-split, and the failure
        // surfaces as 288 unattributed over-redactions -- loud, but pointing at the recognizer when the fault
        // is in the corpus. A reviewer measured that shape and asked for a comment beside the axis; a comment
        // is the same artifact as a prose completeness table, so the obligation is asserted instead and names
        // its own repair.
        // Asked of HasSeparatorEvidence rather than of a fresh separator literal. This file has ONE separator
        // decision, and a second spelling introduced next to the first is the defect it has had five times --
        // including once in the guard written to prevent it.
        // WRONG-not-UNMEASURED. This precedes the behavioural assertions, which the ordering rule otherwise
        // forbids. The exemption is that a malformed axis does not make the behavioural report inadequate, it
        // makes it WRONG: the cells are spurious, so reporting them first would be reporting a fiction. Same
        // standing as the surgery-staleness guard and the input precondition already exempt in this file.
        // The marker above is not decoration -- the ordering guard reads it, and an exemption without it is
        // reported as a violation.
        foreach (string prose in proses.Where(p => p.Length > 0))
        {
            Assert.False(
                HasSeparatorEvidence(prose[^1..]),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"prose axis entry \"{prose}\" ends in a separator, so it is a ROOT and belongs in the "
                    + $"`roots` axis. Left in the prose axis it mis-splits every cell it generates, and the "
                    + $"breach is then reported against the recognizer rather than against the corpus."));
        }
        // AND THE AXIS ON THE OTHER SIDE, WHICH IS WHAT MADE THE CLAIM DIRECTIONAL. Prose AFTER the value
        // is not decoration: the value run is `[^/]*` and extends RIGHTWARD to the next separator, so a
        // slash in trailing prose pulls the run across the intervening words. Known and accepted since
        // `retries=5 then check C:\\tbl\\name=...` -> `retries=<value>`, and unreachable by a corpus whose
        // prose only ever sits in front.
        string[] trailings =
        [
            string.Empty, ")", " while reading /var/log/app", "; see C:\\logs\\a.txt", " -> errno=13",
        ];
        string[] roots = ["/tbl/", string.Empty, "C:\\tbl\\", "a/b/"];
        (string Key, bool Clean)[] keys =
        [
            ("email", true), ("k", true), ("a.b", true),
            ("my col", false), ("o'brien", false), ("o brien'", false),
        ];

        // Separator-bearing tokens, then tokens that resemble one without being one.
        string[] tokens =
        [
            "/", "\\", "\\.", "\\ ", "\\\"", "\\'", "\\/", "/\\", "\\\\?\\",
            "%2F", "%5C", ":", "|", ".", "..", "\u2215", "\t", "\r", "\n", "\0", string.Empty,
        ];
        string[] suffixes = [string.Empty, "part-0.parquet", "/part-0.parquet"];

        int cleanCells = 0;
        int cleanRedacted = 0;
        List<string> cleanUnderRedactions = [];
        List<string> redactedWithoutEvidence = [];
        int attributedToHiveShapedProse = 0;
        int prosesThatRedactAlone = 0;
        int subjectDistinguishingCells = 0;
        int tokenBorneRedactions = 0;
        int suffixBorneRedactions = 0;
        int trailingBorneRedactions = 0;
        List<string> nonCleanBreachesWithoutWhitespace = [];
        int r1Breaches = 0;

        foreach (string prose in proses)
        {
            // THE CAUSE, ASKED OF THE PROSE ALONE, AND ASKED OF THE RECOGNIZER RATHER THAN OF ITS SHAPE. If the
            // prose independently contains a Hive segment, its own value run -- `[^/]*`, greedy -- starts inside
            // the prose and reaches rightward ACROSS the key, redacting the value for a reason that has nothing
            // to do with the path portion this loop built. Stating the cause instead of listing the prose that
            // exhibits it is the difference between an attribution and an excuse: a NEW cause cannot hide here,
            // because a breach whose prose does not independently redact is reported.
            bool proseRedactsAlone = !string.Equals(
                recognizer.Replace(prose, "${key}=<value>"), prose, StringComparison.Ordinal);
            if (proseRedactsAlone)
            {
                prosesThatRedactAlone++;
            }

            foreach (string root in roots)
            {
                foreach ((string key, bool clean) in keys)
                {
                    foreach (string token in tokens)
                    {
                        foreach (string suffix in suffixes)
                        {
                            foreach (string trailing in trailings)
                            {
                                // Constructed, therefore never searched for. The subject of the evidence test is
                                // everything from the key RIGHTWARD -- what this loop built, plus the prose after
                                // it -- and never the prose in FRONT, which the recognizer cannot reach.
                                string beforeToken = root + key + "=" + Sentinel;
                                string withoutRoot = key + "=" + Sentinel + token + suffix;
                                string withToken = beforeToken + token;
                                string pathPortion = withToken + suffix;
                                string message = prose + pathPortion + trailing;
                                string redacted = recognizer.Replace(message, "${key}=<value>");
                                bool changed = !string.Equals(redacted, message, StringComparison.Ordinal);
                                bool evidence = HasEvidenceInKeysPathRegion(pathPortion, trailing);
                                string rendered = message.Replace("\0", "<NUL>", StringComparison.Ordinal);

                                // (2) The over-redaction direction, asserted for EVERY key class, and attributed
                                // by CAUSE. This is where the biconditional stopped being one: it is false, with
                                // exactly one cause, and saying so is stronger than narrowing the claim again.
                                if (changed && !evidence)
                                {
                                    if (proseRedactsAlone)
                                    {
                                        attributedToHiveShapedProse++;
                                    }
                                    else
                                    {
                                        redactedWithoutEvidence.Add(rendered + " -> " + redacted);
                                    }
                                }

                                if (clean)
                                {
                                    cleanCells++;

                                    // THE CELLS THAT TELL THE TWO CANDIDATE SUBJECTS APART, COUNTED. The
                                    // claim says "the key's path region"; the sentence it replaced said
                                    // "right of the key". These are the cells where those two answers
                                    // differ -- a rooted key whose only evidence is the separator opening
                                    // its own segment. Counting them is what stops the sentence drifting
                                    // back: a claim naming the key rather than the region is false for
                                    // every cell counted here, and the corpus is asserted to contain some.
                                    //
                                    // COUNTED INSIDE THE CLEAN CLASS, WHICH IS WHERE ITS BOUND LIVES. It
                                    // stood outside, so the numerator ranged over all six key classes while
                                    // the denominator ranged over three -- roughly twice the slack, which
                                    // means the UPPER half could not fire for its stated cause. Harmless in
                                    // effect, because the drift it guards is caught by the lower half, and
                                    // recorded anyway: a bound wider than its measurement is a claim wider
                                    // than its measurement, which is the defect this file exists to catch.
                                    if (evidence != HasSeparatorEvidence(withoutRoot + trailing))
                                    {
                                        subjectDistinguishingCells++;
                                    }

                                    // AND THE UPPER HALF OF ITS BOUND IS COVERED RATHER THAN LOAD-BEARING,
                                    // WHICH THREE REVIEWERS FOUND INDEPENDENTLY AND IS RECORDED HERE RATHER
                                    // THAN LEFT TO BE FOUND A FOURTH TIME. Driving this count to every
                                    // clean cell means taking the rightward evidence out of the corpus, and
                                    // the assertion that catches that is named rather than guessed: strip
                                    // the separators from `trailings` and `suffixes` and delete the rootless
                                    // root, and what fails is the sibling adequacy assertion
                                    // `cleanRedacted < cleanCells` -- "every clean cell redacts, so the
                                    // over-redaction direction is vacuous". So `< cleanCells` states an
                                    // intent a DIFFERENT assertion enforces. It is kept because it costs
                                    // nothing and a bound removed is a bound nobody reinstates -- but it is
                                    // not evidence, and calling it evidence would be this file's own defect
                                    // one level up.
                                    //
                                    // AND THE MUTANT THAT APPEARED TO PIN IT WAS PINNING THE ARITHMETIC.
                                    // While this counter sat outside the clean class its numerator ranged
                                    // over 98280 cells and its denominator over 49140, so inverting the
                                    // comparison scored 92664 and went RED -- which reads exactly like a
                                    // pin. At the corrected scope the same mutant is GREEN. A mutation that
                                    // goes red for the wrong reason is indistinguishable from a mutation
                                    // that goes red for the right one, and this is the only instance in this
                                    // file where that was measured rather than argued.

                                    if (changed)
                                    {
                                        cleanRedacted++;

                                        // EVERY RIGHTWARD EVIDENCE SOURCE, COUNTED WHERE IT IS THE ONLY
                                        // EXPLANATION. The subject of the claim is built in four pieces and
                                        // three of them sit right of the key, so each gets its own
                                        // reachability counter: a cell that redacts because THIS piece
                                        // introduced the evidence, and would not have without it. Counting
                                        // cells where a piece merely agrees with evidence already present
                                        // would count almost everything and pin nothing -- it is the
                                        // DIFFERENCE each piece makes that is load-bearing, so the difference
                                        // is what is counted.
                                        if (!HasSeparatorEvidence(beforeToken) && HasSeparatorEvidence(withToken))
                                        {
                                            tokenBorneRedactions++;
                                        }

                                        if (!HasSeparatorEvidence(withToken) && HasSeparatorEvidence(pathPortion))
                                        {
                                            suffixBorneRedactions++;
                                        }

                                        if (!HasSeparatorEvidence(pathPortion) && evidence)
                                        {
                                            trailingBorneRedactions++;
                                        }
                                    }

                                    // (1) THE SAFETY HALF, and the only half stated without exception.
                                    if (evidence && !changed)
                                    {
                                        cleanUnderRedactions.Add(rendered + " -> " + redacted);
                                    }

                                    continue;
                                }

                                if (evidence && !changed)
                                {
                                    r1Breaches++;

                                    // (3) Attribution: a breach under a non-clean key must be the WHITESPACE key,
                                    // not the quote. Stated as a cause, so a new decline cannot hide among these.
                                    if (!key.Any(char.IsWhiteSpace))
                                    {
                                        nonCleanBreachesWithoutWhitespace.Add(rendered);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // BEHAVIOUR FIRST, ADEQUACY LAST, AND THAT ORDER IS LOAD-BEARING. Written the other way round this
        // test was caught by its own kind of bug: an over-redaction mutant widened the recognizer until
        // every clean cell redacted, and the VACUITY guard fired first, reporting "the negative half is
        // vacuous" -- true, and not the finding. A corpus-adequacy assertion placed ahead of a behavioural
        // one converts a real failure into a complaint about the corpus. Same defect already annotated on
        // the whole-corpus comparison; found here by mutating rather than by remembering.
        Assert.True(
            cleanUnderRedactions.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{cleanUnderRedactions.Count} clean-key cell(s) carry separator evidence in the key's path "
                + $"region or after it and are NOT redacted. This is the safety half, and it is the one stated without "
                + $"exception: a separator was not recognised as one, which is the shape of every drift and "
                + $"every opt-out this recognizer has had."
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, cleanUnderRedactions)}"));

        Assert.True(
            redactedWithoutEvidence.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{redactedWithoutEvidence.Count} cell(s) redact with no separator evidence in the key's path "
                + $"region or after it AND no independently-redacting prose to explain it. Over-redaction is tolerated; an "
                + $"over-redaction with no stated cause is a second mechanism wearing the first one's name."
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, redactedWithoutEvidence)}"));

        Assert.True(
            nonCleanBreachesWithoutWhitespace.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{nonCleanBreachesWithoutWhitespace.Count} evidence-bearing decline(s) have a key with no "
                + $"whitespace, so they are not residual R1 and are unattributed."
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, nonCleanBreachesWithoutWhitespace)}"));

        // Adequacy of the corpus, asserted after the behaviour it is adequate FOR -- and COLLECTED rather
        // than asserted one at a time, because a reviewer showed that these mask each other. Trimming the
        // `roots` axis to `[string.Empty]` fired the R1-attribution check, which names the KEY-CLASS axis,
        // while the check that names the roots axis never ran. The failure was honest and the diagnosis
        // pointed at the wrong edit.
        //
        // Reordering cannot fix that, because ONE edit invalidates SEVERAL of these at once and an ordering
        // reports exactly one. Measured: trimming `roots` fails TWO of them -- the R1 vacuity above, which
        // names the key-class axis, and the subject-distinguishing count below, which names the roots axis
        // that was actually edited. Whichever is written first, the other is hidden, and which axis the
        // maintainer just touched is not knowable from inside the test. So they are reported TOGETHER and
        // the reader sees every axis the edit implicated.
        //
        // Recorded because it bounds the claim: the opposite-direction masking was LOOKED FOR and NOT
        // found. Trimming `keys` to its clean rows fails only the R1 check, so a reorder would have
        // diagnosed that one edit correctly. The case for collecting rests on SIMULTANEITY -- one edit,
        // several invalidated conditions -- not on a second masking that was never measured.
        List<string> inadequacies = [];
        void Adequate(bool condition, string finding)
        {
            if (!condition)
            {
                inadequacies.Add(finding);
            }
        }

        Adequate(cleanCells > 0 && cleanRedacted > 0, "the clean-key corpus is empty or never redacts.");
        Adequate(
            cleanRedacted < cleanCells,
            "every clean cell redacts, so the over-redaction direction is vacuous here. That direction is "
            + "no longer half of a biconditional -- the claim was retired -- but its corpus still has to "
            + "contain a cell that does NOT redact for the attribution below to mean anything.");
        Adequate(r1Breaches > 0, "no non-clean breach was seen, so the R1 attribution is vacuous.");
        // AND THE EXCUSE IS BOUNDED IN BOTH DIRECTIONS, WHICH IS WHAT KEEPS IT AN ATTRIBUTION. A cause that
        // never fires is dead and the corpus has drifted off the constant; a cause that fires for EVERY prose
        // excuses the whole over-redaction direction and the assertion above becomes unfalsifiable. Neither
        // failure is visible from the assertion it protects, so it is asserted here rather than trusted.
        Adequate(
            prosesThatRedactAlone > 0 && prosesThatRedactAlone < proses.Length,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{prosesThatRedactAlone} of {proses.Length} prose strings independently redact. At 0 the "
                + $"attribution is dead; at {proses.Length} it excuses everything and stops being one."));

        Adequate(
            attributedToHiveShapedProse > 0,
            $"no cell was excused by hive-shaped prose, so that attribution is vacuous and the prose slice "
            + $"has drifted away from {nameof(GuardPrefixes)}.");

        // AND THE SAME BOUND ON THE AXIS THAT MAKES THE CLAIM DIRECTIONAL, BECAUSE THE PIN DID NOT TRAVEL TEN
        // LINES. The commit that sliced `proses` from GuardPrefixes wrote `trailings` beside it as a fresh
        // literal list carrying none of the three permitted pins -- not a slice of a shared constant, no
        // reachability counter, and the corpus obligation loop above iterates `proses` only. A reviewer trimmed
        // it to `[string.Empty]` and the test still passed, which means the RIGHTWARD half of the directional
        // claim could stop being measured while the suite stayed green. Green because it stopped running is
        // the failure this file exists to distinguish from green because it ran, and it arrived one axis over
        // in the same diff.
        //
        // Bounded on BOTH sides for the same reason `prosesThatRedactAlone` is. At zero the rightward half is
        // unreached and the word RIGHTWARD in the claim is decoration. At `cleanRedacted` every redaction is
        // trailing-borne, which means the ADJACENT-evidence axis has gone dead and the tokens list is doing
        // nothing -- the opposite drift, invisible from the same assertion.
        (string Piece, int Reached)[] rightwardSources =
        [
            ("tokens", tokenBorneRedactions),
            ("suffixes", suffixBorneRedactions),
            ("trailings", trailingBorneRedactions),
        ];

        // AND THE CORPUS IS ASSERTED TO CONTAIN THE WITNESS THAT TELLS THE CLAIM'S SUBJECT FROM THE ONE IT
        // KEEPS BEING MISDESCRIBED AS. A claim whose stated subject and executed subject cannot disagree on
        // any cell in the corpus is unfalsifiable by construction: it reads as verified forever, however many
        // cells are added, because the disagreeing cell is not in the space. That is the root cause a reviewer
        // identified behind four rounds of this same defect, and it is cheap to close -- count the cells where
        // the two answers differ and refuse to run without them. Bounded above as well, because if EVERY cell
        // distinguished them the rootless half of the corpus would have gone missing.
        //
        // WHAT IS AND IS NOT KNOWN ABOUT THE UPPER HALF, since three seats have now looked at it. It IS
        // live: forcing disagreement on every clean cell reports `49140 of 49140` and goes red. What no
        // seat has produced is an INDEPENDENT killer -- a realistic edit, not a hand-forced identity,
        // that trips this half and nothing else. So it is live and unkilled rather than settled, and the
        // difference is worth writing down: an upper bound whose only witness is a mutant written to hit
        // it is pinned by its own author.
        Adequate(
            subjectDistinguishingCells > 0 && subjectDistinguishingCells < cleanCells,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{subjectDistinguishingCells} of {cleanCells} clean cells distinguish `the key's path "
                + $"region` from `right of the key`. At 0 the corpus cannot witness the difference and any "
                + $"sentence naming either subject would read as verified; the rooted keys have gone. At "
                + $"{cleanCells} nothing in the corpus AGREES, so the two subjects are no longer being told "
                + $"apart either -- both halves are the same failure seen from opposite ends."));

        // The table itself is an axis, and an axis with no pin is how this whole round started. Trimmed to one
        // row, two of the three pins above simply stop running. The count rots VISIBLY: add a fourth rightward
        // piece to the subject and forget its row, and this fires rather than silently covering three of four.
        Adequate(
            rightwardSources.Length == 3,
            string.Create(
                CultureInfo.InvariantCulture,
                $"the rightward-source table has {rightwardSources.Length} rows, not 3. Add a fourth piece "
                + $"to the subject and forget its row, and two of the three pins below stop running."));

        foreach ((string piece, int reached) in rightwardSources)
        {
            Adequate(
                reached > 0 && reached < cleanRedacted,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{reached} of {cleanRedacted} clean redactions are explained ONLY by evidence the "
                    + $"`{piece}` axis introduced. At 0 that axis carries no separator and the part of the "
                    + $"claim it measures is unmeasured; at {cleanRedacted} it is the ONLY thing driving "
                    + $"redaction and every other rightward axis has gone dead."));
        }

        Assert.True(
            inadequacies.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{inadequacies.Count} adequacy condition(s) failed, reported together because they mask "
                + $"one another when asserted separately and the masked one is often the axis that was "
                + $"actually edited:{Environment.NewLine}"
                + $"{string.Join(Environment.NewLine, inadequacies)}"));
    }

    /// <summary>
    /// The ordering rule, expressed over the guards instead of applied to them one at a time.
    /// </summary>
    /// <remarks>
    /// <para>Every guard in this file that COLLECTS failures into a list must assert that list is empty
    /// before it asserts anything about how big its corpus was. Written the other way round, a corpus-size
    /// or non-vacuity assertion fires first and the actual regression is never named.</para>
    /// <para>This is not a hypothetical. It cost two reproductions on the whole-corpus comparison, was
    /// fixed there, recurred on the divergence classifier one commit after being fixed, was fixed again,
    /// and then recurred a third time in the totality guard added to prevent a DIFFERENT non-travelling
    /// fix. A reviewer demonstrated the cost with a double mutant -- the superseded specimen tidied down
    /// to two conjuncts while a slash-only R4 predicate was hand-rolled back into an unpermitted member --
    /// and the run failed on the missing bookkeeping entry while NEVER NAMING THE STRAY.</para>
    /// <para>Five guards in this change set have now contained the defect they exist to prevent. The
    /// reason this one kept coming back is structural: the rule lived as "fix these three call sites",
    /// which is a fact about the past, and a new guard is not a call site. So it is stated as a property
    /// of the file and executed.</para>
    /// <para>Measured in both directions before being trusted. Against the source as it stood when the
    /// reviewer filed, this reports SIX violations across the two members named. Against the source with
    /// the ordering corrected, ZERO.</para>
    /// <para>Two assertions may precede, each because it is a PRECONDITION rather than an adequacy
    /// check -- if it fails, the behavioural report would not be incomplete, it would be a FICTION:</para>
    /// <list type="bullet">
    /// <item><c>sentinelCollisions == 0</c> -- a sentinel outside the value makes every survival reading
    /// in that census unsound, so the rows it would print are meaningless.</item>
    /// <item><c>HasSeparatorEvidence(prose[^1..])</c> -- a prose axis entry that is really a root
    /// mis-splits every cell it generates, so the breaches reported would be spurious.</item>
    /// </list>
    /// <para>The exemption is keyed on the ASSERTION, never on the member. Keyed on the member it would
    /// have excused the very regression that prompted this, since that member legitimately opens with a
    /// precondition. And each permitted entry must be OBSERVED, so an exemption cannot outlive the
    /// assertion it was written for.</para>
    /// <para>IT MUST ALSO BE CLAIMED AT ITS SITE. A maintainer, reconciling two reviewers who had reached
    /// OPPOSITE correct answers about two different assertions, named the discriminator: assert a check
    /// first only if its failure makes the result WRONG; if it merely leaves the result UNMEASURED, assert
    /// it after. Both reviewers were right, about different sites. The request was to cite that rule at
    /// each site so the next author can tell them apart -- and a citation nobody executes is the same
    /// artifact as the prose completeness tables this PR has spent ten rounds replacing. So the guard reads
    /// it: an entry in the list is honoured only if the marker appears in a comment within
    /// <see cref="RuleCitationWindow"/> lines above the assertion. Listed but unclaimed is reported as a
    /// violation, which is the fail-closed direction.</para>
    /// <para>ITS SCOPE IS THE LIST-COLLECTING FAMILY, AND THAT IS A MEASUREMENT RATHER THAN A CONVENIENCE.
    /// Ten members collect failures into a <c>List&lt;string&gt;</c> and assert it empty; over those, the
    /// rule is exact -- every flag is genuine and every non-flag is correct.</para>
    /// <para>THE UN-AUTOMATED REMAINDER IS NOW MEASURED ON THE RIGHT POPULATION, WHICH CHANGED THE NUMBER
    /// BUT NOT THE ANSWER. The broad heuristic that produced "3 genuine of 17" was indexed on LINES, and a
    /// reviewer pointed out that a line-indexed sweep structurally cannot see this file's dominant
    /// multi-line assertion form -- so the 17 was an undercount of the population, not a measurement of
    /// noise. Re-run statement-joined the true denominator is 281 assert statements across 85 members, and
    /// the broad heuristic flags EIGHTEEN statements in seven members, of which SEVEN in three members were
    /// genuine and are now fixed: a pinned per-site count ahead of the masking diagnosis it would mask, a
    /// component/corpus triple ahead of the cell that corrects the record, and the census totals ahead of
    /// the residual attribution.</para>
    /// <para>What remains is the argument against automating the broad form, and it is stronger stated
    /// this way round: with those seven fixed, the heuristic still flags ELEVEN statements in six members
    /// and NONE of them is genuine. Two are surgery-staleness preconditions, two are input preconditions,
    /// three are <c>VacuumCandidateLog</c> probe counts that ARE its behaviour, and the rest are the
    /// behavioural assertions themselves, matched because they happen to compare against a literal. A
    /// guard whose steady-state precision is zero teaches people to suppress it; that is a measurement of
    /// the heuristic, not a preference. The narrow form stays because over ITS family the precision is
    /// one.</para>
    /// <para>So the other families stay a stated obligation rather than an executed one, and this
    /// paragraph plus the rule at the top of the file is the statement. That is a real limit, not a
    /// covered case: every live site outside this family was found by hand-reading a sweep, and the next
    /// one will have to be too.</para>
    /// <para>The scanner joins statements before classifying them. A line-indexed version of this sweep
    /// was written first and reported zero, because this file's dominant form is a multi-line
    /// <c>Assert.True(cond, "...")</c> and the condition is never on the <c>Assert.</c> line. A sweep is
    /// only as wide as the axis it is indexed on -- the same defect, one level up, in the instrument built
    /// to detect it.</para>
    /// </remarks>
    [Fact]
    public void EveryFailureCollectingGuard_AssertsItsCollectionIsEmpty_BeforeAnyAdequacyAssertion()
    {
        FrozenSet<string> preconditionsMayPrecede = PermittedPreconditions;

        string[] lines = EmbeddedSourceLines();
        Dictionary<string, HashSet<string>> collections = [];
        Dictionary<string, List<(int Line, string Text, string Code)>> asserts = [];
        HashSet<string> exemptionsObserved = new(StringComparer.Ordinal);
        string member = "<file>";
        List<string> pending = [];
        List<string> pendingCode = [];
        int pendingLine = 0;
        int depth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            // EVERY predicate below reads the STRIPPED line, never the raw one. A guard that matches its
            // own subject inside a message or a comment can be opted out of by writing about the thing it
            // guards -- and in this file, writing about the thing being guarded is the house style. The
            // raw text is kept for the REPORT only, where a reader wants the message back.
            string code = WithoutLiteralsOrComments(line);

            if (pending.Count == 0)
            {
                Match declaration = MemberDeclaration().Match(code);
                if (declaration.Success)
                {
                    member = declaration.Groups["name"].Value;
                }

                Match collection = FailureCollection().Match(code);
                if (collection.Success)
                {
                    collections.TryAdd(member, new HashSet<string>(StringComparer.Ordinal));
                    collections[member].Add(collection.Groups["name"].Value);
                }

                if (!code.AsSpan().TrimStart().StartsWith("Assert."))
                {
                    continue;
                }

                pendingLine = i + 1;
            }

            pending.Add(line.Trim());
            pendingCode.Add(code.Trim());
            depth += code.Count(c => c == '(') - code.Count(c => c == ')');
            if (depth > 0 || !code.AsSpan().TrimEnd().EndsWith(";"))
            {
                continue;
            }

            asserts.TryAdd(member, []);
            asserts[member].Add((pendingLine, string.Join(' ', pending), string.Join(' ', pendingCode)));
            pending.Clear();
            pendingCode.Clear();
            depth = 0;
        }

        List<string> outOfOrder = [];
        int collecting = 0;
        foreach ((string owner, HashSet<string> names) in collections)
        {
            if (!asserts.TryGetValue(owner, out List<(int Line, string Text, string Code)>? statements))
            {
                continue;
            }

            int empty = statements.FindIndex(
                s => names.Any(n => s.Code.Contains(n + ".Count == 0", StringComparison.Ordinal)));
            if (empty < 0)
            {
                continue;
            }

            collecting++;
            foreach ((int line, string text, string code) in statements.Take(empty))
            {
                string? exemption = preconditionsMayPrecede
                    .FirstOrDefault(t => code.Contains(t, StringComparison.Ordinal));
                if (exemption is not null && !CitesTheOrderingRule(lines, line))
                {
                    // Listed but not CLAIMED. The list at the top of this method is a decision taken
                    // once, somewhere else; the marker is the decision taken here, where the next author
                    // reads it. Without it an exemption is indistinguishable from the defect.
                    exemption = null;
                }

                if (exemption is null)
                {
                    outOfOrder.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{owner}:{line} -- {text[..Math.Min(90, text.Length)]}"));
                }
                else
                {
                    exemptionsObserved.Add(exemption);
                }
            }
        }

        Assert.True(
            outOfOrder.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{outOfOrder.Count} assertion(s) about corpus size or non-vacuity run BEFORE the guard "
                + $"asserts its collected failures are empty. Written that way, a real regression is "
                + $"reported as a bookkeeping complaint and the failing rows are never printed -- which "
                + $"has already cost three reproductions in this file. Move the emptiness assertion first, "
                + $"or add the assertion to preconditionsMayPrecede AND write \"{RuleCitation}\" in a "
                + $"comment within {RuleCitationWindow} lines above it, saying why its failure makes the "
                + $"behavioural report a FICTION rather than merely incomplete."
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, outOfOrder)}"));

        Assert.True(
            collecting > 4,
            string.Create(
                CultureInfo.InvariantCulture,
                $"only {collecting} failure-collecting guard(s) were found, so the statement joiner has "
                + $"stopped seeing this file's dominant multi-line assertion form."));

        Assert.Equal(preconditionsMayPrecede.Order(StringComparer.Ordinal), exemptionsObserved.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The third form of the ordering rule, executed: once a member COLLECTS its adequacy findings, a bare
    /// adequacy assertion may not be written back beside them.
    /// </summary>
    /// <remarks>
    /// <para>Ordering decides which of two checks runs first. It has nothing to say when ONE edit
    /// invalidates SEVERAL of them, because an ordering reports exactly one and drops the rest in silence.
    /// A reviewer trimmed the flagship member's roots axis to a single element: two adequacy conditions
    /// became false, the run was RED for a true reason, and the one that named the axis just edited was
    /// the one that did not run.</para>
    /// <para>The remedy is to collect, and the risk in the remedy is that collecting decays -- the next
    /// condition gets added as a plain assertion beside the collector, and the masking returns for exactly
    /// one condition. That is the shape every non-travelling fix in this change set has had, so it is
    /// executed rather than remembered.</para>
    /// <para>Bounded below as well: if no member is collecting, the rule has been deleted rather than
    /// obeyed, and a guard over an empty population reads as verified forever.</para>
    /// <para>AND THE SCOREBOARD, HONESTLY. This guard shipped with two mutants, and both probed the
    /// POLICING step. Two seats then found two further holes, independently, and both were in the
    /// CLASSIFICATION step: which span is scanned, and how the collector is recognised. A third hole,
    /// in which TEXT the predicate reads, came from a third seat. Three of the four holes this guard has
    /// had were therefore invisible to the mutants it was shipped with, because those mutants all
    /// assumed the classification was correct and varied only what came after it. Mutate the step that
    /// decides WHAT IS IN SCOPE, not only the step that decides what is WRONG.</para>
    /// </remarks>
    /// <summary>
    /// The structural counts this file's reasoning depends on are ASSERTED, not transcribed into prose.
    /// </summary>
    /// <remarks>
    /// <para>Two sentences here carried counts of the code's own shape and both had rotted: "four guards
    /// read their own source by name" when the assignments number otherwise, and "the five branches' own
    /// key and value classes" when the recognizer's alternation has more arms than that. Neither could
    /// fail. A count of the code's own shape is the prose most likely to rot, and unlike a red count it
    /// is DERIVABLE -- so the rule is not "do not write it" but "do not write it in prose".</para>
    /// <para>The constants are not documentation. Changing either shape fails here and makes the author
    /// look at the sentences that depend on it -- a new recognizer branch brings new inline separator
    /// classes that the totality table cannot see, which is a prose obligation the totality test states
    /// and cannot check.</para>
    /// </remarks>
    [Fact]
    public void StructuralCountsThisFileReliesOn_AreExecutedRatherThanWrittenIntoProse()
    {
        string[] own = EmbeddedSourceLines();
        // Stripped, per the text-subject rule at the top of this file: the subject is CODE, and the
        // predicate below is spelled in this very file, so a raw scan counts the scanner.
        // And the token is BUILT from nameof rather than typed, because this file's own guard caught the
        // transcription the moment it was written -- which is the guard working, on its author.
        string readerCall = "= " + nameof(EmbeddedSourceLines) + "(";
        int reading = own.Count(l =>
            WithoutLiteralsOrComments(l).Contains(readerCall, StringComparison.Ordinal));
        Assert.Equal(SourceReadingSites, reading);

        string[] recognizer = EmbeddedSourceLines(RecognizerSourceName);
        int open = Array.FindIndex(recognizer, l => l.Trim().Equals("@\"(?:\"", StringComparison.Ordinal));
        int close = Array.FindIndex(recognizer, l => l.Trim().Equals("+ @\")\")]", StringComparison.Ordinal));
        Assert.True(
            open >= 0 && close > open,
            "the segment recognizer's alternation could not be located in the embedded recognizer source, "
            + "so the branch count below would be measured over nothing.");

        int branches = 1 + recognizer[open..close]
            .Count(l => l.TrimStart().StartsWith("+ @\"|", StringComparison.Ordinal));
        Assert.Equal(RecognizerBranches, branches);
    }

    // Counts of this file's and the recognizer's own shape, held in code because prose cannot fail.
    // Both were stale sentences before they were constants.
    private const int SourceReadingSites = 9;
    private const int RecognizerBranches = 6;

    // THE CLASSIFIER'S NEGATIVE CONTROL, BESIDE ITS POSITIVE ONES. This classifier has been repaired
    // nine times and every repair was demonstrated the same way: a body that USED to pass now fails. That
    // only ever measures one direction. A recognizer that answered `StatementNotRead` to everything would
    // have satisfied all nine demonstrations, and the tenth defect found here was exactly that shape --
    // an ordinary expression-bodied collector reported as unreadable, the first FALSE POSITIVE this scan
    // has produced. So the classifier is now handed bodies directly and asked for both answers, with the
    // cases that must stay CLEAN carried in the same list as the ones that must be REPORTED. Directly,
    // rather than as members of this file, because a body that must be reported cannot also live here as
    // a member without failing the guard that reports it.
    [Fact]
    public void TheCollectorClassifier_AnswersBothWays_OnBodiesItIsGivenDirectly()
    {
        (string Shape, string[] Body, LocalVoidShape Expected)[] cases =
        [
            (
                "an expression-bodied collector",
                ["void Collect(bool ok, string why) => findings.Add(ok ? string.Empty : why);"],
                LocalVoidShape.Collector),
            (
                "a braced collector",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    if (!ok)",
                    "    {",
                    "        findings.Add(why);",
                    "    }",
                    "}",
                ],
                LocalVoidShape.Collector),
            (
                "a control transfer inside an argument",
                ["void Collect(bool ok, string why) => findings.Add(ok ? string.Empty : throw new InvalidOperationException(why));"],
                LocalVoidShape.StatementNotRead),
            (
                "a control transfer past the first argument list",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why);",
                    "    findings.Where(f => f.Length > 0).ToList().ForEach(f => throw new InvalidOperationException(f));",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                "a control transfer riding a one-lined guard clause",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    if (!ok) { findings.Add(why); throw new InvalidOperationException(why); }",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                "an accumulator that also decides",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why);",
                    "    " + nameof(Assert) + ".True(ok, why);",
                    "}",
                ],
                LocalVoidShape.AccumulatesAndDecides),
            (
                "a body that outruns the window",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    .. Enumerable.Repeat("    if (ok)", CollectorBodyWindow + 4),
                ],
                LocalVoidShape.BodyNotRead),
            (
                "an assertion inside an argument",
                ["void Collect(bool ok, string why) => findings.Add(" + nameof(Assert) + ".IsType<string>(why));"],
                LocalVoidShape.AccumulatesAndDecides),
            (
                "a control transfer under nested headers",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    if (ok) if (findings.Count > 99) throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                "SEC3: control transfer inside an argument list",
                ["void Collect(bool ok, string why) => findings.Add(why.Length >= 0 ? throw new InvalidOperationException(why) : why);"],
                LocalVoidShape.StatementNotRead),
            (
                "SEC4: control transfer inside a header condition",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    if (findings.Count > 0 ? throw new InvalidOperationException(why) : false) { return; }",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                // THE DISCLOSED RESIDUAL, EXECUTED. A callee cannot be followed by a scan that reads one
                // member's source, so an effect behind a call is unread wherever it sits. Asserting that
                // it is STILL unread makes this the one disclosure in the file that cannot quietly stop
                // being true: close the route and this case goes RED and demands the prose be rewritten.
                "KNOWN OPEN: an effect reached through a callee",
                ["void Collect(bool ok, string why) => findings.Add(Bail(why));"],
                LocalVoidShape.Collector),
            (
                "a sink emptied by a call behind the receiver",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why);",
                    "    findings.RemoveRange(0, findings.Count);",
                    "}",
                ],
                LocalVoidShape.SinkCallIsNotAnAppend),
            (
                "a sink emptied by the realistic spelling",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why);",
                    "    findings.RemoveAll(f => f.Length >= 0);",
                    "}",
                ],
                LocalVoidShape.SinkCallIsNotAnAppend),
            (
                "a sink overwritten through its indexer",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why);",
                    "    findings[0] = why;",
                    "}",
                ],
                LocalVoidShape.SinkCallIsNotAnAppend),
            (
                // NOT A NEGATIVE CONTROL, AND SAYING WHY MATTERS. A reviewer asked for a legitimate
                // `RemoveAll` off the sink to stay clean; it does not, and it never did. Any call this
                // scan cannot read is reported whether or not the sink is involved, which is the
                // fail-closed floor the whole classifier stands on rather than a regression from the
                // change beside it. Pinned so the distinction is not rediscovered as a defect.
                "a destructive call on something that is not the sink",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why);",
                    "    other.RemoveAll(f => f.Length >= 0);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                // A CALL THAT ENDS THE RUN, WHICH THE CONTROL-TRANSFER QUESTION DOES NOT SEE AND DOES NOT
                // NEED TO. Reported because it is not an append, not because it is recognised as fatal.
                "a run ended by a call behind the receiver",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why);",
                    "    findings.ForEach(f => Environment.Exit(1));",
                    "}",
                ],
                LocalVoidShape.SinkCallIsNotAnAppend),
            (
                "a sink cleared",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why);",
                    "    findings.Clear();",
                    "}",
                ],
                LocalVoidShape.SinkCallIsNotAnAppend),
            (
                "a sink reassigned",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why);",
                    "    findings = [];",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                // A LOCAL VOID THAT ONLY READS ITS SINK WAS CLASSIFIED A COLLECTOR, because "mentions the
                // sink with a dot after it" answered the ACCUMULATION question. It never accumulates, so
                // the honest answer is the third one on that axis, and this is what the predicate split
                // observably changes.
                "a body that only reads its sink",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    if (findings.Count > 3) { return; }",
                    "}",
                ],
                LocalVoidShape.TouchesTheSinkUnreadably),
            (
                "a sink emptied BEFORE the append, which is the composite a reviewer built",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Clear();",
                    "    findings.Add(why);",
                    "}",
                ],
                LocalVoidShape.SinkCallIsNotAnAppend),
            (
                "a single finding dropped from the sink",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why);",
                    "    findings.RemoveAt(0);",
                    "}",
                ],
                LocalVoidShape.SinkCallIsNotAnAppend),
            (
                "a raw string with an odd number of quotes",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(\"\"\"a\"b\"\"\"); throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                // THE PRE-PASS RESIDUAL, EXECUTED. The literal reader is LINE-SCOPED, so a construct that
                // spans lines is mis-read. Both spellings are pinned rather than described, because the
                // claim that matters is the DIRECTION: each makes a body report MORE than it should, so
                // the limit is fail-closed. If either ever flips to a clean Collector these go RED.
                "a verbatim string continued onto the next line, whose tail is read as code",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(@\"start",
                    "    throw new InvalidOperationException(why); end\");",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                // THE SPELLING THAT WAS ACTUALLY LIVE, AND IT IS NOT THE ONE REPORTED. A quote inside a
                // block comment opened a literal that ate the rest of the line. Placed between statements
                // the `/*` debris was itself unreadable, so the body still reported and the hole looked
                // theoretical; placed INSIDE THE APPEND'S OWN ARGUMENT LIST the debris lands in a
                // statement that IS readable, and this measured a clean `Collector` before the fix.
                // Pinned as the case, because a reviewer's spelling is the start of the search, not it.
                "a quote in a block comment inside the append's own argument list",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why /* \" */); throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                "a quote in a block comment between statements",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why); /* the \" budget */ throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                // C# BLOCK COMMENTS DO NOT NEST. The inner `/*` is comment text and the first `*/` ends
                // it, so the throw after it is code and is read. Non-nesting is the language's rule, and
                // pinning it stops a later reader "fixing" it into a nesting counter.
                "a block comment containing a second opener, which does not nest",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why); /* a /* b */ throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                // TWO COMMENTS ON ONE LINE, WHICH IS WHAT ACTUALLY PINS NON-NESTING. The first pin for it
                // had only ONE `*/` on the line, so a mutant that scanned to the LAST `*/` -- the exact
                // shape of a plausible wrong "fix" that makes comments nest -- passed against it. A pin
                // that cannot distinguish the wrong answer is not a pin, and this file has now been
                // caught doing that twice; the mutant is the only thing that ever reveals it.
                "two block comments on one line, so the code between them is read",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why); /* a */ throw new InvalidOperationException(why); /* b */",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                "a line-comment marker inside a string, which is not a comment",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(\"//\"); throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                "a block-comment opener inside a string, which is not a comment",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(\"/*\"); throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                // THE NEGATIVE CONTROL FOR THE ATTRIBUTION. A destructive call on some OTHER list is
                // still reported -- an unreadable statement is reported whether or not it touches the
                // sink -- but it is reported as generic unreadability, NOT as the sink outcome. That
                // difference is what makes the destroy predicate independently pinned rather than merely
                // covered, and a reviewer was right that without it no mutant could name it.
                "a destructive call on a list that is not the sink",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why);",
                    "    other.RemoveAll(x => true);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                // A BLOCK COMMENT IS NOW STRIPPED, so this body is the clean collector it looks like.
                // It was pinned the other way one round ago, as evidence that the line-scoped limit
                // failed closed -- which was a property claimed of the LIMIT from two measured
                // spellings, and a third spelling had the opposite direction. Rule 22 again.
                "a block comment, which is stripped like the line comment beside it",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why); /* throw new InvalidOperationException(why); */",
                    "}",
                ],
                LocalVoidShape.Collector),
            (
                // BOTH SPELLINGS OF THE INTERPOLATED-VERBATIM OPENER, PINNED TOGETHER. Only `$@"` was
                // recognised; `@$"` fell through to the regular-string path, where the backslash escaped
                // the closing quote and ate the line. Pinning the pair is the point -- either alone
                // passes against a reader that handles one order, which is exactly how this survived.
                "an interpolated verbatim string, dollar first",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add($@\"a\\\"); throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                "an interpolated verbatim string, at first",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(@$\"a\\\"); throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                // A RAW FENCE WINS OVER ANY PREFIX, so the interpolation modifiers cannot smuggle a
                // literal past the fence rule by preceding it.
                "a raw interpolated string with doubled dollars",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add($$\"\"\"a\"b\"\"\"); throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                // A UTF-8 SUFFIX IS A SUFFIX, not an opener -- checked because the question asked of the
                // `@` opener has to be asked of every opener, not only the one reported.
                "a utf-8 string literal, whose suffix is not an opener",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(\"a\"u8); throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                "a char literal containing a quote",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(why.Replace('\\'', '~')); throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                "a verbatim string ending in a backslash",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add(@\"C:\\\"); throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                "an interpolated string with a brace",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    findings.Add($\"{why}\"); throw new InvalidOperationException(why);",
                    "}",
                ],
                LocalVoidShape.StatementNotRead),
            (
                "a local void that never touches the sink",
                [
                    "void Collect(bool ok, string why)",
                    "{",
                    "    " + nameof(Assert) + ".True(ok, why);",
                    "}",
                ],
                LocalVoidShape.NotACollector),
        ];

        List<string> inadequacies = [];
        void Note(bool ok, string why)
        {
            if (!ok)
            {
                inadequacies.Add(why);
            }
        }

        foreach ((string shape, string[] body, LocalVoidShape expected) in cases)
        {
            LocalVoidShape actual = ClassifyLocalVoid(body, 0, ["findings"]);
            Note(
                actual == expected,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{shape} was classified {actual}, but this file relies on {expected}."));
        }

        // THE BALANCE IS EXECUTED, NOT PROMISED. A list that drifted into positive controls only would
        // still pass every case above while proving nothing about the direction that was actually broken.
        Note(
            cases.Any(c => c.Expected == LocalVoidShape.Collector)
                && cases.Any(c => c.Expected != LocalVoidShape.Collector),
            "the case list must hold at least one body that must stay clean and one that must be "
                + "reported; positive controls alone show only that the recognizer fires, never that it "
                + "stops.");

        Assert.True(
            inadequacies.Count == 0,
            string.Join(Environment.NewLine, inadequacies));
    }

    [Fact]
    public void AdequacyFindings_AreCollected_NotAssertedOneAtATime()
    {
        string[] lines = EmbeddedSourceLines();
        List<string> strays = [];

        // POLICED BY MEMBER, NOT BY POSITION. The first cut turned `collecting` on when the scanner
        // reached the collector's declaration, so an assertion written ABOVE it was invisible -- and C#
        // convention puts local functions at the END of a method, which makes the blind spot the
        // IDIOMATIC place for the next author to add the next condition. A reviewer showed the
        // second-order cost: with one bare assertion moved above the declaration, trimming the `roots`
        // axis reports only the finding that names the WRONG axis, and this guard stays green. The
        // masking it exists to prevent, restored through the guard itself, with nothing red.
        //
        // So the member is classified first and every assertion in it is judged, wherever it sits.
        // THE SINKS EACH MEMBER DECLARES, IN A PRE-PASS, because APPEND is the third axis this
        // classifier reads and it was still a one-token hand-list: `.Add(`. A reviewer walked out through
        // `AddRange`, which changes no behaviour. The question is not "does this call a method named Add"
        // -- that is an instance of appending standing in for the class, the fifth time this guard has
        // made that substitution -- it is "does this body touch the findings list the member declares".
        // The sink name is taken from the member's own declaration, from the SHAPE of any generic
        // collection it declares, from the emptiness assertion it later makes, and from the parameters its
        // own call sites pass it to -- so any spelling of the append, any container type, any spelling of
        // the emptiness assertion, any extension method and any alias assignment still mentions it.
        //
        // WHAT IS STILL NOT SEEN -- STATED AS A COMPLEMENT, NOT AS A LIST OF ROUTES. Five rounds running,
        // a reviewer re-measured this paragraph and found it wrong in one direction or the other: it
        // named a relay that is caught twice over, it omitted a route that was open, it claimed a field
        // route open that turned out to need a third conjunct, and it was read as claiming argument-borne
        // effects were open when they are not. Every one of those corrections was true when written, and
        // the paragraph still went stale, because AN ENUMERATION OF SCENARIOS ROTS AGAINST CODE IT DOES
        // NOT DERIVE FROM -- the same argument that retired the pin table two hundred lines up, finally
        // applied to the disclosure that kept demanding it.
        //
        // So the sink residual is now exactly the COMPLEMENT of the four derivations above, and it is
        // written as one: a sink is unnamed here when it is declared outside the member AND is not the
        // subject of the member's own emptiness assertion AND is not passed at any of the collector's
        // call sites AND is not touched with `.Add(`. Any name failing all four is invisible; a name
        // failing three of four is REPORTED. This sentence cannot go stale in the way the list did,
        // because adding a derivation shrinks it automatically and removing one widens it -- it says the
        // same thing the code beside it says rather than a snapshot of what that code happened to catch.
        // Both directions were re-run at this commit: field scope with `.AddRange(` and no emptiness
        // assertion is silent; the same member with `.Add(` is reported.
        //
        // The CLASSIFICATION residual is not written here at all. It is a case in the classifier's own
        // both-ways test, marked as open and asserted to still be open, so that CLOSING it turns that
        // test RED and forces this comment to be revisited. A disclosure that fails when it stops being
        // true is the only kind that has survived review here.
        Dictionary<string, HashSet<string>> sinksByMember = [];
        string scanned = "<file>";
        foreach (string raw in lines)
        {
            string scan = WithoutLiteralsOrComments(raw);
            Match declared = MemberDeclaration().Match(scan);
            if (declared.Success)
            {
                scanned = declared.Groups["name"].Value;
            }

            // AND FROM THE EMPTINESS ASSERTION, NOT ONLY FROM THE DECLARED TYPE. FailureCollection()
            // spells `List<string>`, which made the sink axis a hand-list of one type: a member
            // collecting into a SortedSet had no sink at all and dropped out of the population silently.
            // What makes a local a findings sink is not how it is declared, it is that the member later
            // asserts it is EMPTY -- so the name is taken from the assertion the guard already reads.
            Match asserted = EmptinessAssertion().Match(scan);
            if (asserted.Success)
            {
                if (!sinksByMember.TryGetValue(scanned, out HashSet<string>? fromAssert))
                {
                    fromAssert = new HashSet<string>(StringComparer.Ordinal);
                    sinksByMember[scanned] = fromAssert;
                }

                fromAssert.Add(asserted.Groups["name"].Value);
            }

            // AND FROM ANY COLLECTION THE MEMBER DECLARES, BY SHAPE. A member that asserts emptiness in
            // a spelling other than `x.Count == 0` -- `Assert.Empty(x)` is the obvious one -- names no
            // sink through the assertion route, and adding that literal would be a second spelling of an
            // assertion rather than a derivation of one. What IS derivable without naming a type or an
            // assertion is the declaration's shape: a generic type applied to a name with an initializer.
            // Type-agnostic, so `List`, `HashSet`, `SortedSet` and anything else arrive together.
            Match declaredCollection = CollectionDeclaration().Match(scan);
            if (declaredCollection.Success)
            {
                if (!sinksByMember.TryGetValue(scanned, out HashSet<string>? fromShape))
                {
                    fromShape = new HashSet<string>(StringComparer.Ordinal);
                    sinksByMember[scanned] = fromShape;
                }

                fromShape.Add(declaredCollection.Groups["name"].Value);
            }

            Match sink = FailureCollection().Match(scan);
            if (sink.Success)
            {
                if (!sinksByMember.TryGetValue(scanned, out HashSet<string>? names))
                {
                    names = new HashSet<string>(StringComparer.Ordinal);
                    sinksByMember[scanned] = names;
                }

                names.Add(sink.Groups["name"].Value);
            }
        }

        Dictionary<string, SortedSet<string>> collectingMembers = [];
        Dictionary<string, int> collectorCalls = new(StringComparer.Ordinal);
        List<string> unrecognised = [];
        Dictionary<string, List<(int Line, string Text, string Code)>> asserts = [];
        string member = "<file>";
        List<string> pending = [];
        List<string> pendingCode = [];
        int pendingLine = 0;
        int depth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            // Stripped, for the reason given on the sibling guard above: a predicate over raw text is
            // opted out of by quoting its own subject in a message or a comment, and the assertion this
            // guard exists to catch is exactly the one whose author is most likely to be writing about
            // `.Count == 0`. The raw line is kept for the report.
            string code = WithoutLiteralsOrComments(line);
            ReadOnlySpan<char> trimmed = code.AsSpan().Trim();

            if (pending.Count == 0)
            {
                Match declaration = MemberDeclaration().Match(code);
                if (declaration.Success)
                {
                    member = declaration.Groups["name"].Value;
                }

                // DERIVED BY SHAPE, NOT BY NAME. Keyed on the literal name of the one collector that
                // existed, this guard policed an INSTANCE rather than a CLASS: a member whose local
                // function was called anything else went unpoliced, and the rot was invisible because
                // the reachability bound stayed satisfied by the member that did use the name. A literal
                // name is a hand-list -- the same argument that retired the enumerated separator shapes,
                // the four hand-spelled prose copies and the transcribed name index in this file.
                Match collector = LocalVoidDeclaration().Match(code);
                if (collector.Success)
                {
                    LocalVoidShape shape = ClassifyLocalVoid(
                        lines,
                        i,
                        WithParameterSinks(
                            lines,
                            i,
                            collector.Groups["name"].Value,
                            sinksByMember.GetValueOrDefault(member) ?? []));
                    if (shape == LocalVoidShape.Collector)
                    {
                        if (!collectingMembers.TryGetValue(member, out SortedSet<string>? named))
                        {
                            named = new SortedSet<string>(StringComparer.Ordinal);
                            collectingMembers[member] = named;
                        }

                        named.Add(collector.Groups["name"].Value);
                        continue;
                    }

                    // FAILS CLOSED ON A SHAPE IT CANNOT READ. A local void that accumulates but is not a
                    // clean collector -- it decides as well, or its body runs past what this scan reads --
                    // leaves its member unclassified, and an unclassified member's assertions are not
                    // policed at all. Every previous hole in this guard was exactly that outcome reached
                    // by a different route, and each time the guard stayed green because some OTHER member
                    // still collected. So an accumulator that cannot be recognised is a finding here
                    // rather than a silence, whatever the rest of the file looks like.
                    if (shape != LocalVoidShape.NotACollector)
                    {
                        unrecognised.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{member}:{i + 1} -- local void `{collector.Groups["name"].Value}` "
                            + $"accumulates but is not a readable collector ({shape}). Split the deciding "
                            + $"half out, or shorten the body, so the member can be classified."));
                    }
                }

                Match call = StatementLevelCall().Match(code);
                if (call.Success)
                {
                    string key = member + ":" + call.Groups["name"].Value;
                    collectorCalls[key] = collectorCalls.GetValueOrDefault(key) + 1;
                }

                if (!trimmed.StartsWith("Assert."))
                {
                    continue;
                }

                pendingLine = i + 1;
            }

            pending.Add(line.Trim());
            pendingCode.Add(code.Trim());
            depth += code.Count(c => c == '(') - code.Count(c => c == ')');
            if (depth > 0 || !code.AsSpan().TrimEnd().EndsWith(";"))
            {
                continue;
            }

            asserts.TryAdd(member, []);
            asserts[member].Add((pendingLine, string.Join(' ', pending), string.Join(' ', pendingCode)));
            pending.Clear();
            pendingCode.Clear();
            depth = 0;
        }

        foreach ((string owner, SortedSet<string> named) in collectingMembers)
        {
            if (!asserts.TryGetValue(owner, out List<(int Line, string Text, string Code)>? statements))
            {
                continue;
            }

            foreach ((int line, string text, string code) in statements)
            {
                if (code.Contains(".Count == 0", StringComparison.Ordinal))
                {
                    continue;
                }

                // A PRECONDITION may stand alone, and must: collecting it would let the run continue on a
                // corpus whose report is a fiction. Same list and same marker the ordering guard uses --
                // the discriminator is WRONG vs UNMEASURED here too, applied to a different question.
                if (PermittedPreconditions.Any(t => code.Contains(t, StringComparison.Ordinal))
                    && CitesTheOrderingRule(lines, line))
                {
                    continue;
                }

                strays.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{owner}:{line} (collects via {string.Join(", ", named)}) -- "
                    + $"{text[..Math.Min(90, text.Length)]}"));
            }
        }

        // AND THIS GUARD OBEYS ITS OWN RULE, WHICH IT DID NOT UNTIL A PROBE OF ITS OWN MADE IT OBVIOUS.
        // It used to end in five separate assertions over one population -- strays, unrecognised locals,
        // the population floor, the named anchor, the per-member call bound -- so a tree that broke two
        // of them reported one and hid the other. That was measured, not supposed: a run carrying two
        // unreadable collectors AND two stray assertions printed only the strays. The rule this member
        // exists to enforce is that one edit can invalidate several conditions at once, and it was the
        // clearest instance of its own subject in the file. Collected here, reported together.
        List<string> inadequacies = [];
        void Note(bool condition, string finding)
        {
            if (!condition)
            {
                inadequacies.Add(finding);
            }
        }

        Note(
            strays.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{strays.Count} assertion(s) sit beside an adequacy COLLECTOR instead of joining it. One "
                + $"edit can invalidate several adequacy conditions at once; a separate assertion reports "
                + $"its own and hides every other, and the hidden one is as likely as not the one naming "
                + $"the axis that was actually edited. Route the condition through the collector its "
                + $"member already declares, named beside each finding below."
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, strays)}"));

        Note(
            unrecognised.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{unrecognised.Count} local void(s) accumulate into a findings list without being "
                + $"recognisable as a collector. Their members are classified as non-collecting, so no "
                + $"assertion in them is policed by this guard -- and that silence survives however many "
                + $"other members collect."
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, unrecognised)}"));

        Note(
            collectingMembers.Count > 0,
            "no member collects its adequacy findings, so this guard is checking an empty population and "
            + "would read as verified however far the rule had rotted.");

        // AND THE KNOWN COLLECTOR BY NAME, BECAUSE AN EXISTENTIAL CANNOT SEE THE LOSS OF A SPECIFIC
        // MEMBER. The floor above is satisfied by ANY collecting member, so converting the flagship back
        // to bare assertions removes it from the population and the floor stays green on the strength of
        // some other member. This is the third appearance of that shape in two change sets, after two
        // sibling NotEmpty bounds that could not see a per-item loss.
        //
        // A NAME HERE IS NOT THE HAND-LIST THIS GUARD SPENT THREE ROUNDS REMOVING, and the distinction is
        // worth stating because the two look identical. A RECOGNIZER keyed on a name polices one INSTANCE
        // while claiming to police a CLASS -- that was the defect. An ANCHOR names one known artifact and
        // claims only that it still exists, which is what a regression test is. `nameof` keeps it from
        // rotting into a string the rename tooling cannot see.
        Note(
            collectingMembers.ContainsKey(
                nameof(Redact_AlwaysRedactsOnRightwardEvidence_AndOverRedactsOnlyWithAStatedCause)),
            "the flagship adequacy member is no longer in the collecting population, so its findings are "
            + "reported one at a time again and the floor above stays satisfied by some other member.");

        // AND BOUNDED PER MEMBER, NOT ONLY OVER THE FILE. A reviewer showed why: with two collectors in
        // the tree, a global non-emptiness bound is satisfied by the OTHER one while the member under
        // edit falls silently out of the population. That is the same defect as a suite-wide NotEmpty
        // that cannot see a per-item loss, and it is the second time in two change sets that a global
        // floor has been the thing keeping a hole invisible.
        //
        // What this CAN check per member is that the collector is actually used: a collector called once
        // is a bare assertion with extra steps, and a collector called never means the member's findings
        // are going somewhere else. What it CANNOT check is a member that has left the population
        // entirely. THIS PARAGRAPH USED TO CLAIM THAT THE CLASSIFIER CLOSED THAT -- "the only way out of
        // the population is to stop appending, which is to stop being a collector" -- which is circular:
        // stopping appending IS the escape, and a reviewer walked out through it. It is closed by the
        // anchor above, for exactly one member, and remains open for any other. Stated rather than
        // argued away, because a false safety claim is worse than the gap it hides.
        foreach ((string owner, SortedSet<string> named) in collectingMembers)
        {
            foreach (string collector in named)
            {
                int calls = collectorCalls.GetValueOrDefault(owner + ":" + collector);
                Note(
                    calls > 1,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{owner} declares the collector `{collector}` but calls it {calls} time(s). One "
                        + $"call is a bare assertion with extra steps and none means the findings are "
                        + $"going somewhere this guard is not looking."));
            }
        }

        Assert.True(
            inadequacies.Count == 0,
            string.Join(Environment.NewLine, inadequacies));
    }

    // WHAT A COLLECTOR IS, stated by EFFECT so the guard polices the class and not one spelling of it.
    // A LOCAL void -- no access modifier, which is what makes it local -- whose body RECORDS and does not
    // DECIDE: it appends to a collection and asserts nothing.
    //
    // The first cut said the same thing with the token `if (!`, which made an IDIOMATIC GUARD CLAUSE --
    // `if (condition) { return; } list.Add(finding);` -- unrecognised: identical semantics, no behaviour
    // change, member silently unpoliced. That was a hand-list wearing a shape's clothes, and it is the
    // same defect as the literal name it replaced, moved one level down. So the branch is not mentioned
    // at all. What a collector does is APPEND; what it must not do is ASSERT, because an assertion inside
    // the collector is a decision and a decision stops the run before the other findings are gathered.
    [GeneratedRegex(@"^\s+(?:static\s+)?void\s+(?<name>\w+)\s*\(", RegexOptions.ExplicitCapture)]
    private static partial Regex LocalVoidDeclaration();

    // A call that IS the whole statement -- how a void collector is invoked, and not how a value-returning
    // helper is used. Deliberately blind to `x = f(...)` and to calls nested in expressions.
    [GeneratedRegex(@"^\s+(?<name>[a-zA-Z_]\w*)\(", RegexOptions.ExplicitCapture)]
    private static partial Regex StatementLevelCall();

    // How far into a local function the body may run. Small on purpose: a body longer than this is
    // deciding something, which is exactly what a collector must not do.
    private const int CollectorBodyWindow = 12;

    // What a local void turned out to be. Three outcomes rather than two, because the two-valued form
    // answered "is this a collector?" with NO for both "it is something else" and "it accumulates in a
    // shape I cannot read" -- and only the first of those is safe to ignore.
    private enum LocalVoidShape
    {
        NotACollector,
        Collector,
        AccumulatesAndDecides,
        BodyNotRead,
        TouchesTheSinkUnreadably,
        SinkCallIsNotAnAppend,
        StatementNotRead,
    }

    // A SINK CAN ARRIVE AS A PARAMETER, AND A PARAMETER HAS ITS OWN NAME. The pre-pass learns what a
    // member's findings list is called at the point the member declares it; inside a local void that
    // takes the list as an argument, that name is gone and the body touches `sink` instead. Two seats
    // reached the same escape from opposite directions -- `void Note(List<string> sink, ...)` appending
    // by any spelling other than `.Add(` was outside the population entirely.
    //
    // IT IS CLOSED BY DERIVATION, NOT BY WIDENING THE TYPE. The obvious repair was to make the sink
    // pattern match any `ICollection<string>`, and that is a hand-list of type spellings -- the same
    // substitution retired for the collector's name, its branch, its append and its decision, wearing an
    // interface's clothes. It also would not have helped: the parameter's TYPE was never the problem,
    // its NAME was. So the local void's parameters are matched POSITIONALLY against its own call sites,
    // and a parameter that is passed the member's findings list IS the member's findings list under
    // another name. Widening only ever adds names, so it can move a member into the policed population
    // and never out of it -- the same soundness argument the retained `.Add(` fallback rests on.
    private static HashSet<string> WithParameterSinks(
        string[] source,
        int declaration,
        string name,
        HashSet<string> sinks)
    {
        string[] parameters = ArgumentList(WithoutLiteralsOrComments(source[declaration]));
        if (parameters.Length == 0 || sinks.Count == 0)
        {
            return sinks;
        }

        HashSet<string> widened = new(sinks, StringComparer.Ordinal);
        for (int i = declaration + 1; i < source.Length; i++)
        {
            string code = WithoutLiteralsOrComments(source[i]);
            if (MemberDeclaration().IsMatch(code))
            {
                break;
            }

            if (!code.TrimStart().StartsWith(name + "(", StringComparison.Ordinal))
            {
                continue;
            }

            // BY NAME WHERE THE CALLER GAVE ONE, BY POSITION OTHERWISE. C# lets a caller write
            // `Note(into: findings, condition: true)`, which reorders the arguments and left positional
            // matching pairing the sink with the wrong parameter -- a byte-identical collector body,
            // silently outside the population. A named argument states the parameter it binds to, so the
            // name is taken from the argument rather than guessed from where it sits.
            string[] arguments = ArgumentList(code);
            for (int a = 0; a < arguments.Length; a++)
            {
                Match named = NamedArgument().Match(arguments[a]);
                if (named.Success)
                {
                    if (sinks.Contains(named.Groups["value"].Value))
                    {
                        widened.Add(named.Groups["parameter"].Value);
                    }
                }
                else if (a < parameters.Length && sinks.Contains(arguments[a]))
                {
                    widened.Add(LastWord(parameters[a]));
                }
            }
        }

        return widened;
    }

    // The top-level comma-separated contents of the first bracketed group on a line -- a parameter list
    // read from a declaration, an argument list read from a call. Depth-tracked so a generic argument or
    // a nested call does not split a member in two.
    private static string[] ArgumentList(string code)
    {
        int open = code.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return [];
        }

        List<string> parts = [];
        StringBuilder current = new();
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            char c = code[i];
            if (c is '(' or '[' or '<')
            {
                depth++;
                if (depth == 1)
                {
                    continue;
                }
            }
            else if (c is ')' or ']' or '>')
            {
                depth--;
                if (depth == 0)
                {
                    parts.Add(current.ToString().Trim());
                    return [.. parts];
                }
            }
            else if (c == ',' && depth == 1)
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString().Trim());
        return [.. parts];
    }

    // The declared name out of a parameter, which is its last word once the type is in front of it.
    private static string LastWord(string parameter)
    {
        int space = parameter.LastIndexOf(' ');
        return space < 0 ? parameter : parameter[(space + 1)..];
    }

    private static LocalVoidShape ClassifyLocalVoid(
        string[] source,
        int declaration,
        HashSet<string> sinks)
    {
        bool closed = false;
        int depth = 0;
        bool opened = false;
        StringBuilder body = new();

        // FROM THE DECLARATION LINE, NOT THE ONE AFTER IT. C# permits the whole body on the declaration
        // line, and the scan that started below it read no body at all -- so it reported NOT A COLLECTOR
        // where the honest answer was BODY NOT READ. The span axis and the certainty axis are the same
        // defect met twice: a scan that cannot see something must say so rather than answer no.
        for (int i = declaration; i < Math.Min(source.Length, declaration + 1 + CollectorBodyWindow); i++)
        {
            string code = WithoutLiteralsOrComments(source[i]);
            if (MemberDeclaration().IsMatch(code))
            {
                closed = true;
                break;
            }

            // THE LINE LOOP NOW ONLY ANSWERS THE SPAN QUESTION -- how far the body reaches -- and hands
            // the TEXT on to be cut into statements below. Reading line by line while reasoning about
            // statements was the seventh generation of one defect: a line is an INSTANCE OF A STATEMENT'S
            // SPELLING, not the statement, so `if (c) { list.Add(x); throw new Exception(x); }` matched a
            // whitelisted opening and its tail was never looked at. A scanner's unit must match the unit
            // its claim is about.
            body.Append(i == declaration ? BodyAfterSignature(code) : code).Append('\n');

            depth += code.Count(c => c == '{') - code.Count(c => c == '}');
            opened |= code.Contains('{', StringComparison.Ordinal);
            if (opened && depth <= 0)
            {
                closed = true;
                break;
            }

            // An expression-bodied local function has no braces to close.
            if (!opened && code.Contains("=>", StringComparison.Ordinal)
                && code.AsSpan().TrimEnd().EndsWith(";"))
            {
                closed = true;
                break;
            }
        }

        bool appends = false;
        bool mentions = false;
        bool decides = false;
        bool references = false;
        string unreadable = string.Empty;
        bool mutatesSink = false;
        foreach (string statement in Statements(body.ToString()))
        {
            if (statement.Contains("Assert.", StringComparison.Ordinal))
            {
                decides = true;
            }

            // TWO QUESTIONS, TWO PREDICATES -- AND THEY WERE ONE. "Any call on the member's own findings
            // list is an append, however it is spelled" was defended four times, by four seats including
            // me, with the argument that widening can only put MORE members under the guard. That
            // argument is sound FOR MEMBERSHIP and false FOR ACCUMULATION, and the same predicate was
            // answering both: `inadequacies.Clear()` was read as accumulation, so a destructive call made
            // a member look like a collector. The reasoning was correct about the use we examined and
            // silently wrong about the use we did not, which is the sharpest form this file's recurring
            // defect has taken -- not an unexamined claim, an examined one applied somewhere else.
            //
            // MEMBERSHIP stays as wide as it can be. Any mention of a sink, plus `.Add(` on anything at
            // all, puts the member in the population; the widening is safe here because a member cannot
            // leave the policed set by being mentioned more.
            bool touchesSink = sinks.Any(n => statement.Contains(n, StringComparison.Ordinal))
                || statement.Contains(".Add(", StringComparison.Ordinal);
            references |= touchesSink;

            // ACCUMULATION is DERIVED POSITIVELY and shares its predicate with the readability answer,
            // because they are the same concept asked twice: an append is the one thing a collector may
            // do to its sink. Everything else it might do -- empty it, drop from it, overwrite it -- is
            // not accumulation and is not readable, and neither fact is now inferred from the other.
            if (AccumulatesIntoSink(statement, sinks))
            {
                appends = true;
            }
            else if (touchesSink)
            {
                mentions = true;
            }

            // EVERY STATEMENT IS READ, AND TOUCHING THE SINK IS ONE READABLE FORM RATHER THAN AN EXEMPTION
            // FROM BEING READ. This was an `else if`, so any statement that so much as mentioned the
            // findings list skipped the readability question entirely -- and a collector that throws will
            // NATURALLY name the findings list in its message, so the most idiomatic spelling of the
            // escape was also the one that disabled the check. The three doors four seats found here were
            // one error wearing three coats: a structure that GRANTS PERMISSION instead of REQUIRING A
            // READING. Permission is now something a statement earns by being classifiable.
            if (SinkIsTheReceiver(statement, sinks)
                && !AccumulatesIntoSink(statement, sinks)
                && !ControlTransfer().IsMatch(statement))
            {
                // A DESTRUCTIVE CALL ON THE SINK IS NOT MERELY UNREADABLE, AND SAYING SO IS THE POINT. A
                // reviewer measured a legitimate `RemoveAll` on some OTHER list and found it reported
                // too -- correctly, since an unreadable statement is reported whether or not it touches
                // the sink. But the consequence was that the predicate split shipped the round before was
                // COVERED AND NOT ATTRIBUTED: no mutant could name it, because its outcome was
                // indistinguishable from generic unreadability. By this file's own standard a claim must
                // be distinguishable from the thing it excludes, so the destroy case now has its own
                // outcome and its own mutant, and `other.RemoveAll(...)` still reports as StatementNotRead.
                //
                // AND IT IS NAMED AFTER THE PREDICATE, NOT AFTER THE ATTACK THAT MOTIVATED IT. The first
                // spelling of this was `SinkMutatedNotAppended`, which was a fair name for `Clear` and a
                // wrong one for `findings.ForEach(f => Environment.Exit(1))` -- a call on the sink that
                // mutates nothing. Naming an outcome after the attack is how a general check acquires a
                // specific-sounding claim it cannot keep, which is this file's recurring defect wearing
                // yet another coat. Control transfer keeps precedence because it is the more specific
                // diagnosis and is the first question the readability answer asks.
                mutatesSink = true;
            }

            if (!ReadableInACollector(statement, sinks))
            {
                // THE DECIDE AXIS IS INVERTED RATHER THAN ENUMERATED, WHICH IS WHY IT NO LONGER HAS A
                // LIST TO ESCAPE FROM. It used to ask "does this body contain `Assert.`" -- a one-token
                // hand-list for the class DECIDES, the same substitution that had already been retired
                // for the collector's name, its branch and its append. A reviewer walked out through
                // `throw`, which stops the run exactly as an assertion does and restored the masking.
                // Enumerating the ways to leave a method early is the same losing game, so the question
                // is turned around: a collector's body may contain the statements this scan can READ --
                // control flow, and anything touching the member's own sink -- and ANYTHING ELSE is an
                // effect it cannot account for. Unknown means reported, not clean.
                unreadable = statement;
            }
        }

        if (!closed)
        {
            // The body outran the window, so "it does not append" is not something this scan established.
            return appends ? LocalVoidShape.AccumulatesAndDecides : LocalVoidShape.BodyNotRead;
        }

        if (!references)
        {
            // Never touches a findings sink, so whatever else it does is not this guard's business. This
            // is the ONLY clean NO the classifier gives, and it is given on a positive observation.
            return LocalVoidShape.NotACollector;
        }

        if (decides)
        {
            return LocalVoidShape.AccumulatesAndDecides;
        }

        if (mutatesSink)
        {
            return LocalVoidShape.SinkCallIsNotAnAppend;
        }

        if (unreadable.Length > 0)
        {
            return LocalVoidShape.StatementNotRead;
        }

        if (!appends)
        {
            // Touching the findings list without a readable call on it is the honest third answer on the
            // APPEND axis, the same shape as BodyNotRead on the span axis.
            return mentions ? LocalVoidShape.TouchesTheSinkUnreadably : LocalVoidShape.NotACollector;
        }

        return LocalVoidShape.Collector;
    }

    // THE UNIT IS THE STATEMENT, AND SAYING SO IS THE POINT. Cut at semicolons and braces that sit at
    // bracket depth zero, so a statement spanning four lines is one statement and four statements sharing
    // a line are four. The alternative offered was to re-scan the tail of a whitelisted line past its
    // first brace, which would have worked on the reported example and left the UNIT as the line -- and
    // the unit is what was wrong. A guarantee about what a collector's body may CONTAIN is a claim about
    // statements; a scanner whose unit is the line is making that claim about a spelling of them.
    //
    // A `for (a; b; c)` header keeps its semicolons because they are inside the brackets, which is the
    // same depth rule doing the same work in the other direction.
    private static List<string> Statements(string body)
    {
        List<string> statements = [];
        StringBuilder current = new();
        int depth = 0;
        foreach (char c in body)
        {
            if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth--;
            }
            else if (depth <= 0 && c is ';' or '{' or '}')
            {
                statements.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        statements.Add(current.ToString().Trim());
        return statements;
    }

    // A local function's body is what follows its parameter list, so the signature is not mistaken for a
    // statement. This is why `void ` no longer has to be whitelisted as an opening -- and it should not
    // be, because an opening that admits a whole line would admit `void N(bool c) => throw new X();`.
    private static string BodyAfterSignature(string declaration)
    {
        int open = declaration.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return declaration;
        }

        int depth = 0;
        for (int i = open; i < declaration.Length; i++)
        {
            if (declaration[i] == '(')
            {
                depth++;
            }
            else if (declaration[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    // THE `=>` GOES WITH THE SIGNATURE, and leaving it on made the first FALSE POSITIVE
                    // this scan has produced: `void Note(bool c, string w) => findings.Add(...)` cut to
                    // the statement `=> findings.Add(...)`, which starts with neither the sink nor a
                    // control-flow opening, so a legitimate collector was reported as unreadable. Every
                    // other defect here was permissive and cost a silence; this one cost a false alarm on
                    // ordinary code, which is why the negative control beside the positive ones exists.
                    string body = declaration[(i + 1)..].TrimStart();
                    return body.StartsWith("=>", StringComparison.Ordinal) ? body[2..] : body;
                }
            }
        }

        return string.Empty;
    }

    // The statements a collector's body may contain besides a touch of its own sink. A permissive list
    // whose GROWTH DIRECTION IS SAFE: anything missing from it is reported rather than waved through, so
    // forgetting a spelling costs a false finding and never a silence. That is the opposite of the
    // `Assert.`/`.Add(`/`if (!` lists it replaces, where forgetting a spelling cost the whole member.
    // It shrank when the unit became the statement: braces and `case` labels are DELIMITERS now rather
    // than things to recognise, and a shorter hand-list is a smaller surface to be wrong about.
    //
    // A CONTROL-FLOW KEYWORD IS NOT A PERMISSION FOR WHAT FOLLOWS IT. Matching the opening and returning
    // true made a whitelist entry a permission for the whole STATEMENT rather than for the branch header
    // -- so a braceless `if (!c) throw new InvalidOperationException(w);` is one statement, matched its
    // opening, and was never read past it. The header is consumed and THE REMAINDER IS CLASSIFIED, which
    // is the same correction as the unit change one level in: read the thing, do not recognise a prefix
    // of it. A branch with braces has an empty remainder and is readable for the honest reason.
    private static bool ReadableInACollector(string code, HashSet<string> sinks)
    {
        string t = code.Trim();

        // THE WAY TO GUARANTEE NO REGION IS EXEMPT IS NOT TO HAVE REGIONS. The unit correction went
        // line -> statement and then STOPPED AT THE STATEMENT BOUNDARY, so a statement's INTERIOR stayed
        // permission-granted: every branch below consumes some part of the text and hands on the rest,
        // and each part it consumed without reading was an escape. Two were found that way -- an argument
        // list behind a sink receiver, and a HEADER CONDITION, which is jumped over to reach the
        // remainder and has no receiver to make it look suspicious. Patching them one at a time would
        // have left the next region to be found by the next reviewer, which is the eighth generation of
        // exactly one mistake.
        //
        // So the EFFECT question is asked of the WHOLE STATEMENT before any of it is consumed, and only
        // STRUCTURE is decomposed below. A collector accumulates; leaving the method is disqualifying
        // wherever it is written -- in a condition, in an argument, behind a receiver, or alone. This is
        // the same shape the DECIDE axis already had (`Assert.` is looked for across the whole statement,
        // arguments included), so the two effect questions now agree instead of one being region-scoped.
        // EFFECTS ARE ASKED OF THE WHOLE STATEMENT; STRUCTURE IS ASKED OF ITS PARTS.
        if (ControlTransfer().IsMatch(t))
        {
            return false;
        }

        if (t.Length == 0 || t is "else" or "return" or "break" or "continue")
        {
            return true;
        }

        // The sink as the RECEIVER of the statement, which is what appending to it looks like. Merely
        // MENTIONING it -- `throw new InvalidOperationException(findings[^1])` -- is not this, and that
        // distinction is the whole of the second door.
        //
        // THIS COMMENT USED TO CLAIM THAT `throw` IS THE ONLY FORM THAT TRANSFERS CONTROL OUT, AND THAT
        // THERE WAS "NO SECOND SPELLING TO HAVE MISSED". That was false as written, and a reviewer said
        // so with `findings.ForEach(f => Environment.Exit(1))` -- control leaving the run, visible in the
        // statement's own text, no callee body required. The accurate statement is narrower: C# has one
        // KEYWORD that transfers control out of an expression, and everything else that ends a run is a
        // CALL. Calls are not covered by the control-transfer question at all; they are covered by the
        // fail-closed floor, which reports every statement this scan cannot read. That is why the file
        // survived the counter-example -- but surviving it is not the same as having claimed correctly,
        // and the sentence is corrected rather than defended.
        if (SinkIsTheReceiver(t, sinks))
        {
            // AND THE EFFECT QUESTION IS NOT "DOES IT THROW", IT IS "DOES THIS STATEMENT STOP A FINDING
            // REACHING THE ASSERTION". Leaving the run is ONE answer. DESTROYING THE SINK is another, and
            // it is the worse one: `findings.RemoveRange(0, findings.Count)` has the same outcome as a
            // throw -- nothing reaches the final assertion -- except that it suppresses ALL findings
            // rather than the ones after the first, and it leaves the member looking like an ordinary
            // collector. Reassignment (`findings = []`) was already reported, because it is not a call
            // and so matches nothing; the receiver form was not.
            //
            // THE LIST BELOW IS THE ONE WHITELIST IN THIS FILE WHOSE GROWTH DIRECTION IS UNSAFE, AND THAT
            // IS WHY IT IS SHORT AND WHY THIS SENTENCE EXISTS. Blacklisting `Clear`/`RemoveRange` would
            // fail OPEN on the first spelling nobody thought of, which is the mistake this file has now
            // made eight times; a whitelist of the APPEND spellings fails closed, so `UnionWith`, `Push`
            // and anything else cost a false finding rather than a silence. But adding an entry here
            // widens what is waved through, so it is a decision rather than a convenience: it holds the
            // two spellings this file actually uses, and a third should be argued for, not appended.
            return AccumulatesIntoSink(t, sinks);
        }

        if (t.StartsWith("else ", StringComparison.Ordinal))
        {
            return ReadableInACollector(t[5..], sinks);
        }

        foreach (string opening in (string[])["if", "while", "for", "foreach", "switch"])
        {
            if (!t.StartsWith(opening + " (", StringComparison.Ordinal))
            {
                continue;
            }

            int close = MatchingParenthesis(t, t.IndexOf('(', StringComparison.Ordinal));
            return close >= 0 && ReadableInACollector(t[(close + 1)..], sinks);
        }

        return false;
    }

    // THE ONE THING A COLLECTOR MAY DO TO ITS SINK, asked once and used by both the readability answer
    // and the append axis. Splitting those two uses onto one predicate is what let a destructive call be
    // read as accumulation; sharing ONE predicate between them is the opposite arrangement, and the
    // difference is that this one is derived from what accumulation IS rather than from what touching
    // the sink looks like.
    //
    // THIS IS THE ONE WHITELIST IN THIS FILE WHOSE GROWTH DIRECTION IS UNSAFE, and it is short for that
    // reason. Blacklisting `Clear`/`RemoveAll`/`RemoveAt` would fail OPEN on the first spelling nobody
    // thought of -- this file's defect nine times over -- so the append spellings are named and every
    // other call on the sink is reported, which costs a false finding for `UnionWith` or `Push` and never
    // a silence. Adding an entry widens what is waved through, so it is a decision to argue rather than a
    // convenience to reach for.
    private static bool AccumulatesIntoSink(string statement, HashSet<string> sinks)
        => sinks.Any(n => statement.Contains(n + ".Add(", StringComparison.Ordinal)
            || statement.Contains(n + ".AddRange(", StringComparison.Ordinal));

    // The index of the parenthesis closing the one at `open`, or -1 if the line does not close it.
    private static int MatchingParenthesis(string code, int open)
    {
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '(')
            {
                depth++;
            }
            else if (code[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    // The marker a permitted precondition must carry AT ITS SITE, and how far above the assertion the
    // guard will look for it. Ten lines is the length of the longest exempted site's comment block plus
    // room to grow; a window rather than an exact line so the marker can lead a paragraph rather than
    // having to be jammed against the Assert.
    // ONE list for BOTH guards that need it, because the thing it names is one concept: an assertion
    // whose failure makes the behavioural report a FICTION rather than merely incomplete. The ordering
    // guard uses it to allow such an assertion to run FIRST; the collector guard uses it to allow such an
    // assertion to stand ALONE, since a precondition that is collected with adequacy findings is a
    // precondition that no longer stops the run. Either way the site must CLAIM the exemption with the
    // marker below -- a central list is a decision taken somewhere else, and the marker is the decision
    // taken where the next author reads it. Exact consumption is asserted by the ordering guard, over the
    // scope where every entry is reachable.
    private static readonly FrozenSet<string> PermittedPreconditions = new[]
    {
        "sentinelCollisions == 0",
        nameof(HasSeparatorEvidence) + "(prose[^1..])",
    }.ToFrozenSet(StringComparer.Ordinal);

    private const string RuleCitation = "WRONG-not-UNMEASURED";
    private const int RuleCitationWindow = 10;

    private static bool CitesTheOrderingRule(string[] source, int assertLine)
    {
        int first = Math.Max(0, assertLine - 1 - RuleCitationWindow);
        for (int i = first; i < assertLine - 1; i++)
        {
            if (source[i].Contains(RuleCitation, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // THE RESOURCE NAME IS THE PROJECT-RELATIVE PATH, AND IT IS DERIVED RATHER THAN WRITTEN. The csproj
    // sets LogicalName="%(Identity)", so the manifest name IS the Include path: failures read
    // `Delta/PathDisclosureHygieneTests.cs:984`, which a terminal or an IDE can resolve from the test
    // project directory, instead of the opaque `PathDisclosureHygieneTests.source`.
    //
    // A reviewer asked whether both could be emitted -- the resource name AND a clickable path. Emitting a
    // hand-written path alongside would be a LABEL the guard never verified, and one that rots silently
    // toward pointing at a file that no longer exists; this file has spent ten rounds replacing exactly
    // that artifact. Deriving the name from the item that supplies the BYTES gives the same navigability
    // with no second decision to drift: move or rename the file and the Include moves the name with it,
    // while the constant below stops matching and the guard fails loudly rather than reading a stale copy.
    /// <summary>
    /// No comment may state how many tests a change reddens without saying WHEN it was measured.
    /// </summary>
    /// <remarks>
    /// <para>A count of members rots VISIBLY -- a guard fails when it becomes wrong. A count of test
    /// OUTCOMES rots INVISIBLY: nothing re-runs it, it changes whenever a test is added or split, and
    /// the tree stays green while the sentence becomes false. This file carried one such sentence for
    /// several rounds and a reviewer measured it wrong; the round after the rule was stated, the same
    /// reviewer found the rule already violated at another site -- and at a third, in the recognizer's
    /// own source. Stating a rule is not applying it, so this applies it, over BOTH files.</para>
    /// <para>The escape is not deletion but DATING: a count that says when it was measured is a fact
    /// about a commit, which cannot rot. A count with no WHEN is a claim about the tree the reader is
    /// looking at, and this file is not able to check it.</para>
    /// <para>PARTIAL BY CONSTRUCTION, AND THE BLIND SPOT IS STATED. A test-outcome count has no single
    /// shape; the sweep recognises the spellings this codebase actually uses, and a count spelled some
    /// other way will pass. That is why the population is bounded below -- if the sweep stops seeing the
    /// counts it does recognise, it says so rather than reading as verified.</para>
    /// <para>THE SOURCE AXIS IS PINNED TWICE, AND THAT IS THE ROUND-11 REPAIR. `SweptSources` was a fresh
    /// literal list with no pin: `axis-trim-sweep.py` trimmed it to its first element -- dropping
    /// `LocalFileSystemBackend.cs`, the recognizer's own source and the reason this guard says "over BOTH
    /// files" -- and the suite stayed GREEN, because an aggregate `counted` bound is satisfied by whichever
    /// source is left. So the axis is now bounded on both sides: it must COVER every `.cs` source embedded
    /// in the test assembly (the chokepoint -- dropping a source is a set mismatch, not a smaller number),
    /// and EVERY swept source must contribute at least one recognised count (per-source, so a source that
    /// stops carrying the shape can no longer hide behind its sibling's eight).</para>
    /// </remarks>
    [Fact]
    public void NoCommentStatesATestOutcomeCount_WithoutSayingWhenItWasMeasured()
    {
        List<string> undated = [];
        Dictionary<string, int> countedBySource = [];

        foreach (string source in SweptSources)
        {
            string[] lines = EmbeddedSourceLines(source);
            countedBySource[source] = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith("//", StringComparison.Ordinal)
                    || !TestOutcomeCount().IsMatch(trimmed))
                {
                    continue;
                }

                countedBySource[source]++;
                bool dated = false;
                for (int j = Math.Max(0, i - MeasurementCitationWindow); j <= i; j++)
                {
                    dated |= lines[j].Contains(MeasurementCitation, StringComparison.Ordinal);
                }

                if (!dated)
                {
                    undated.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{source}:{i + 1} -- {trimmed[..Math.Min(96, trimmed.Length)]}"));
                }
            }
        }

        Assert.True(
            undated.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{undated.Count} comment(s) state a test-outcome count with no WHEN. Such a count is a "
                + $"claim about the tree the reader is looking at, nothing re-runs it, and it goes wrong "
                + $"in silence. Delete the number and keep the checkable claim, or write "
                + $"\"{MeasurementCitation} <commit>\" within {MeasurementCitationWindow} lines above it."
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, undated)}"));

        // EVERY adequacy condition below leaves the finding UNMEASURED rather than WRONG, so all of them run
        // after the behavioural assertion -- this file's ordering rule, applied.
        int counted = countedBySource.Values.Sum();

        Assert.True(
            counted > 4,
            string.Create(
                CultureInfo.InvariantCulture,
                $"only {counted} test-outcome count(s) were recognised across {SweptSources.Length} "
                + $"sources, so the sweep has stopped seeing the spellings it was built for and would "
                + $"read as verified over a file full of them."));

        // PER SOURCE, because the aggregate above is satisfied by whichever source is left. That is exactly
        // how the source axis went unpinned: trimming SweptSources to its first element dropped
        // LocalFileSystemBackend.cs -- the recognizer's own source, and the whole point of "over BOTH files"
        // -- while `counted` stayed at 8 and the suite stayed green.
        string[] silent = countedBySource
            .Where(entry => entry.Value == 0)
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            silent.Length == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{silent.Length} swept source(s) carried no recognised test-outcome count at all "
                + $"({string.Join(", ", silent)}), so this guard is reading them as verified while "
                + $"measuring nothing in them. Either the source stopped carrying the shape, or the "
                + $"recognizer stopped seeing it."));

        // AND THE AXIS ITSELF IS BOUNDED THE OTHER WAY, against a population it does not get to choose: every
        // `.cs` source EMBEDDED in the test assembly must be swept. Dropping a source is then a set mismatch
        // rather than a smaller number, which is the difference between an axis that can be trimmed in
        // silence and one that cannot.
        string[] embedded = typeof(PathDisclosureHygieneTests).Assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] swept = SweptSources.Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            embedded.SequenceEqual(swept, StringComparer.Ordinal),
            string.Create(
                CultureInfo.InvariantCulture,
                $"the swept sources [{string.Join(", ", swept)}] are not the sources embedded in the test "
                + $"assembly [{string.Join(", ", embedded)}]. A source embedded for a guard to read but "
                + $"absent from this sweep is unmeasured; a source swept but not embedded cannot be read "
                + $"at all."));
    }

    // The spellings of a test-outcome count this codebase actually uses. Partial on purpose and stated
    // as such above: there is no single shape for "how many tests went red", and enumerating shapes is
    // this file's own named failure mode. The population bound is what keeps the partiality honest.
    [GeneratedRegex(
        @"\b(?:\d+\s+(?:more\s+)?tests?\b|RED\s+\d+\b|\d+\s+RED\b|kill(?:s|ed)\s+\d+\b|[Ff]ail(?:s|ed)\s+\d+\b)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex TestOutcomeCount();

    private const string EmbeddedSourceName = "Delta/PathDisclosureHygieneTests.cs";

    // The recognizer's own file, embedded for the same reason this one is: a rule that holds in the test
    // file and not in the source it guards is a rule applied to one site, which is how three rules in
    // this change set have already rotted.
    private const string RecognizerSourceName = "LocalFileSystemBackend.cs";

    private static readonly string[] SweptSources = [EmbeddedSourceName, RecognizerSourceName];

    // The token a comment must carry to be allowed to state a test-outcome count, and how far above the
    // count the sweep will look for it.
    private const string MeasurementCitation = "MEASURED-AT";
    private const int MeasurementCitationWindow = 14;

    // Both source-reading guards share ONE read path. Two reads of the same resource would be two
    // decisions about where the source comes from, and this file's longest-running defect is exactly that
    // shape: one concept, two implementations, drifting apart.
    private static string[] EmbeddedSourceLines() => EmbeddedSourceLines(EmbeddedSourceName);

    private static string[] EmbeddedSourceLines(string name)
    {
        using Stream? embedded = typeof(PathDisclosureHygieneTests).Assembly
            .GetManifestResourceStream(name);

        Assert.True(
            embedded is not null,
            "the source is not embedded in the test assembly, so this guard cannot run. It fails rather "
                + "than skips: an opted-out totality guard is the defect it exists to catch.");

        using StreamReader reader = new(embedded!);
        return reader.ReadToEnd().Split('\n');
    }

    // Paren counting must not read a bracket inside a string literal or a comment. The assertions this
    // scans are full of both, and every one of them carries parentheses.
    // ONE QUESTION, ONE PREDICATE, SO THE ATTRIBUTION CANNOT DRIFT FROM THE BRANCH IT ATTRIBUTES. The
    // readability answer asks "is this statement's receiver the sink" and so does the outcome that names
    // a destructive call, and those are not two similar questions -- they are one. Written twice they
    // would drift, and the round that split `AccumulatesIntoSink` off a shared predicate is the reason to
    // care: it is the same defect in the other direction.
    private static bool SinkIsTheReceiver(string statement, HashSet<string> sinks)
    {
        string t = statement.Trim();
        return sinks.Any(n => t.StartsWith(n + ".", StringComparison.Ordinal)
            || t.StartsWith(n + "[", StringComparison.Ordinal));
    }

    private static int QuoteRun(string line, int start)
    {
        int end = start;
        while (end < line.Length && line[end] == '"')
        {
            end++;
        }

        return end - start;
    }

    private static string WithoutLiteralsOrComments(string line)
    {
        StringBuilder code = new(line.Length);
        char quote = '\0';
        bool verbatim = false;
        int fence = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (fence > 0)
                {
                    // A RAW STRING CLOSES ON A RUN OF QUOTES AT LEAST AS LONG AS THE ONE THAT OPENED IT,
                    // and honours no escapes at all. Read one quote at a time it fails exactly as the two
                    // above did: `"""a"b"""` has an ODD number of quotes, so the reader was left inside
                    // a literal and swallowed the rest of the line -- measured `Collector` before this.
                    if (c == '"')
                    {
                        int run = QuoteRun(line, i);
                        i += run - 1;
                        if (run >= fence)
                        {
                            quote = '\0';
                            verbatim = false;
                            fence = 0;
                        }
                    }

                    continue;
                }

                // AN ESCAPE IS A PROPERTY OF THE LITERAL'S KIND, NOT OF ITS QUOTE CHARACTER. This read
                // `quote == '"'`, so a CHAR literal did not honour its own escapes: `'\''` closed on the
                // escaped quote, re-opened on the next apostrophe, and swallowed the REST OF THE LINE --
                // code included. The mirror was live too and went unreported: a VERBATIM string honoured
                // backslash escapes it does not have, so `@"C:\"` never closed and swallowed the rest of
                // its line the same way. Both are the same mis-keying, and fixing only the reported half
                // would have left the other for the next reviewer.
                //
                // IT OUTRANKS ITS SIZE BECAUSE OF WHAT IS DOWNSTREAM. This function feeds member
                // tracking, sink derivation, assertion extraction and the ordering guard, so one such
                // literal blinds EVERY source-reading guard on that line, and it hides `Assert.` exactly
                // as well as `throw` -- both questions are asked of the already-filtered text. It is a
                // ninth generation on an axis none of the others touched: not WHERE the scan looks or
                // WHAT it looks for, but WHETHER THE TEXT IT IS LOOKING AT IS THE TEXT THAT IS THERE.
                if (c == '\\' && !verbatim)
                {
                    i++;
                    continue;
                }

                if (c == quote)
                {
                    quote = '\0';
                    verbatim = false;
                }

                continue;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                break;
            }

            // THE NAME OF THIS FUNCTION HAS TWO HALVES AND THE ARGUMENT HAD ONLY COVERED ONE. Completing
            // the LITERAL kinds was defended as a derivation because C# enumerates them; a reviewer then
            // pointed out that C# enumerates the COMMENT kinds just as closely -- there are two -- and
            // only `//` was handled. So `/* */` was left exactly the shape the literal fix had retired,
            // inside the very function whose name promises both.
            //
            // AND IT WAS NOT MERELY AN OMISSION, IT WAS A THIRD FAIL DIRECTION. An unhandled `/*` is not
            // skipped harmlessly: a QUOTE inside it opens a literal that swallows the rest of the line.
            // The reported spelling still reported, because the `/*` debris was itself an unreadable
            // statement -- but that is luck, not a limit, and moving the comment INSIDE the append's own
            // argument list makes the debris land in a statement that IS readable:
            // `findings.Add(why /* " */); throw ...` was measured a clean `Collector`. Non-nesting is
            // deliberate and is the language's rule, not a simplification: `/* a /* b */` ends at the
            // first `*/`.
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                int close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    // Runs past the end of the line. Line-scoped, so the rest of THIS line is comment.
                    break;
                }

                i = close + 1;
                continue;
            }

            // DERIVE THE OPENER THE WAY THE KINDS WERE DERIVED, WHICH IS THE HALF THAT WAS STILL A LIST.
            // Covering all four literal KINDS was a real derivation and survived an attack; it does not
            // help if one kind's OPENER is misrecognised, and this read `@` immediately followed by a
            // quote. The interpolated-verbatim kind has TWO legal spellings -- `$@"` and `@$"`, both
            // legal since C# 8 -- so `@$"a\"` was read as a regular string, the backslash escaped the
            // closing quote, and the rest of the line was swallowed: the identical failure, in the
            // identical fail-OPEN direction, as the char and verbatim rows fixed one commit earlier.
            // An INSTANCE standing in for the CLASS, on the axis that had just been claimed derived.
            //
            // THE PREFIX IS A SET, NOT A SEQUENCE. That is the derivation rather than adding the second
            // spelling: C# lets the modifiers appear in either order because they are unordered
            // modifiers, so read the RUN of them and let the kind follow from WHICH are present. `$` and
            // doubled `$$` do not change how a literal is delimited or escaped, so only `@` is asked
            // about here; a raw fence still wins over both, because a raw string is raw whatever
            // interpolation modifiers precede it.
            if (c is '$' or '@')
            {
                int j = i;
                bool at = false;
                while (j < line.Length && line[j] is '$' or '@')
                {
                    at |= line[j] == '@';
                    j++;
                }

                if (j < line.Length && line[j] == '"')
                {
                    int prefixed = QuoteRun(line, j);
                    quote = '"';
                    if (prefixed >= 3)
                    {
                        verbatim = false;
                        fence = prefixed;
                        i = j + prefixed - 1;
                    }
                    else
                    {
                        verbatim = at;
                        i = j;
                    }

                    continue;
                }
            }

            if (c is '"' or '\'')
            {
                // THE KINDS ARE ENUMERATED BY THE LANGUAGE, NOT BY ME. That is what makes completing them
                // a derivation rather than the hand-list this file keeps retiring: C# has exactly four
                // literal kinds, and each answers two questions -- does it honour `\` escapes, and what
                // closes it. Fixing only the two kinds a reviewer reported would have left the third
                // (measured live, and open in the same direction) for the next round.
                if (c == '"')
                {
                    int run = QuoteRun(line, i);
                    if (run >= 3)
                    {
                        quote = '"';
                        verbatim = false;
                        fence = run;
                        i += run - 1;
                        continue;
                    }
                }

                quote = c;
                verbatim = false;
                continue;
            }

            code.Append(c);
        }

        return code.ToString();
    }

    // BOTH SPELLINGS OF THE SAME DECLARATION. Keyed on the explicit type alone, a collection declared
    // `var x = new List<string>()` dropped its whole member out of the policed population. That was
    // caught -- the reachability bound below fell to its floor -- but caught for the WRONG REASON, as a
    // complaint about how many guards were seen rather than about the assertion that had moved. A
    // mutation that goes red for the wrong reason is indistinguishable from one that goes red for the
    // right one, which this file has already paid for once.
    [GeneratedRegex(
        @"\b(?:List<\s*string\s*>\s+(?<name>\w+)\s*=|var\s+(?<name>\w+)\s*=\s*new\s+List<\s*string\s*>)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex FailureCollection();

    // A generic collection declared with an initializer, whatever its type argument and however it is
    // constructed. Used only to widen the sink population, which can move a member INTO the policed set
    // and never out of it -- the soundness direction two seats have now checked on the `.Add(` fallback.
    [GeneratedRegex(@"\b\w+<[^;=<>]*(?:<[^;=<>]*>)?[^;=<>]*>\s+(?<name>\w+)\s*=\s*(?:\[|new\b)",
        RegexOptions.ExplicitCapture)]
    private static partial Regex CollectionDeclaration();

    // The one expression form in C# that transfers control out of the method the scan can see.
    [GeneratedRegex(@"\bthrow\b", RegexOptions.ExplicitCapture)]
    private static partial Regex ControlTransfer();

    // An argument that states the parameter it binds to. Anchored at both ends so a ternary's colon and
    // a namespace qualification cannot be read as one.
    [GeneratedRegex(@"^(?<parameter>\w+)\s*:\s*(?<value>[^:]+)$", RegexOptions.ExplicitCapture)]
    private static partial Regex NamedArgument();

    // A findings sink named by the assertion that it is empty. Type-agnostic on purpose: the sink axis
    // was a hand-list of one declared type until a reviewer collected into a SortedSet.
    [GeneratedRegex(@"\b(?<name>\w+)\.Count\s*==\s*0", RegexOptions.ExplicitCapture)]
    private static partial Regex EmptinessAssertion();

    private static Regex Build(string pattern)
        => new(pattern, RegexOptions.None, TimeSpan.FromSeconds(5));

    // ONE CORPUS, TWO DENSITIES. Both guard tests range over the same axes; the per-site matrix takes a
    // PREFIX of each axis because it compiles ~20 pattern variants rather than 4. The axes are ordered so
    // that the prefix IS the reduced density -- two corpora written separately would be two decisions, and
    // a shared constant is not a shared decision unless the slice is taken from the constant itself.
    // THE SECOND ENTRY IS THE CONSTANT, DELIBERATELY, AND THE DECISION IS STATED RATHER THAN INFERRED.
    // It stood here as a fifth hand-written copy of the same words. A reviewer asked the right question --
    // is this MEANT to be the corpus prose, or an independent spelling that happens to coincide? -- and the
    // answer is that it is meant to be the same prose: it is here to give the flagship corpus a
    // separator-free prefix, which is exactly the property the constant is asserted to have. An independent
    // spelling would have to say so, and would then need its own assertion of that property. The other
    // entries are deliberately NOT the constant: each carries a separator, a quote or a hive shape, and
    // exists to attack the recognizer from a direction the corpus prose cannot.
    private static readonly string[] GuardPrefixes =
    {
        string.Empty, BothRecognizerProse, "a=1/b=2 ", "Error opening \"", "retries=5 ",
        "path /var/log: ", "x=y ",
    };

    private static readonly string[] GuardRoots =
    {
        string.Empty, "/root/", @"C:\w\", @"C:\\w\\", @"\\srv\s\", @"\\\\srv\\s\\", "a/b/", "./",
        "'", "\"",
    };

    private static readonly string[] GuardKeys =
    {
        "email", "my col", "o'brien", "o'brien y", "K", string.Empty, "k%3D", "a.b",
    };

    private static readonly string[] GuardSeparators = { "=", "%3D", "%3d" };

    private static readonly string[] GuardValues =
    {
        Sentinel, "Legal\\" + Sentinel, "Legal/" + Sentinel, "Legal\\" + Sentinel + "=EU",
        "Legal/" + Sentinel + "=EU", "Legal\\" + Sentinel + "=EU\\p", "Legal/" + Sentinel + "=EU/p",
        "Legal\\\\" + Sentinel + "%3DEU", "Legal\\" + Sentinel + "%3DEU\\p-0.parquet",
        Sentinel + "=" + Sentinel + "=" + Sentinel,
    };

    private static readonly string[] GuardTails =
    {
        string.Empty, "\\", "/p-0.parquet", "\\p-0.parquet", "'", "/a b", "/", " and more", "'.", "\"",
    };

    private const string Sentinel = "QQSENTINELQQ";

    // DERIVED, NOT TRANSCRIBED. An earlier draft spelled this guard out as a literal here, and a mutation
    // that dropped the guard's `^` origin then failed BOTH guard tests on the site count -- "expected 6,
    // actual 0" -- which reports that the transcription drifted and says nothing about whether behaviour
    // changed. That is the same defect these tests exist to catch, committed inside the test. Reading the
    // constant off the type under test makes a guard edit change the shipped pattern and the expectation
    // TOGETHER, so the site count stays 6 and the mutant has to be killed on a measured NUMBER instead.
    private static readonly string SegmentGuardPattern =
        (string)typeof(LocalFileSystemBackend)
            .GetField("NoSeparatorEarlierInSegment", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;


    // SEPARATOR EVIDENCE, SPELLED ONCE FOR EVERY CLASSIFIER IN THIS FILE, AND STATED AS THE QUESTION
    // RATHER THAN AS A LIST OF SHAPES. Residual R4 is defined by INDISTINGUISHABILITY from prose, so the
    // only question a classifier may ask is "does this string carry separator evidence?".
    //
    // THIS PREDICATE HAS NOW BEEN WRONG TWICE, EACH TIME BY ENUMERATING. It first read
    // `!contains('/') && !contains(":\\") && !contains("\\\\")`, which made a lone backslash invisible and
    // bucketed a live disclosure into R4 across three corpora and five reviews. It was then corrected to
    // "a backslash with content after it" -- narrower, still an enumeration, and it excused the very next
    // defect, a value ending AT its backslash. Two corrections, both partial, both in the direction of
    // listing the shapes that count.
    //
    // So it is no longer a list. A separator is evidence WHEREVER IT SITS: enumerating positions is how a
    // predicate becomes wider than the residual it models, and a predicate wider than its residual converts
    // a finding into an excuse silently. If some genuinely prose-only string is ever misclassified by this,
    // the fix is a filed residual, not another clause here.
    // WHAT PINS THIS PREDICATE, MEASURED-AT f74f9b4 BY PERFORMING THE REVERT RATHER THAN BY DESCRIBING
    // IT. This paragraph used to say "pinned by exactly one test, and that is the correct number rather
    // than a gap". The tail-axis half of that is still true -- reverting to the enumerating form leaves
    // Redact_SiblingRecognizerUnderTheSameTailAxis_DeclinesOnlyIntoFiledResiduals green, for the reason
    // below -- but the count was wrong: the monotonicity matrix reds, and so do
    // R4Classifier_DivergesFromTheEnumerationItReplaced_OnceABranchIsWeakened and
    // Redact_AlwaysRedactsOnRightwardEvidence_AndOverRedactsOnlyWithAStatedCause. The pin widened as the
    // corpus grew and the sentence did not, which is the failure this file keeps finding in its own prose.
    // The tail axis does
    // range over the shape -- its tail alphabet is a hole filled from {/, \\}, so a lone terminal backslash
    // is in the corpus -- but a classifier predicate can only be wrong about a cell that DECLINES, and
    // closing the terminal-backslash opt-out moved every one of those cells to FULL. A predicate has no
    // opportunity to excuse a decline that no longer happens.
    //
    // A REVIEWER STOOD DOWN ON THIS PREDICATE, WHICH IS THE COST STATED AS AN EVENT RATHER THAN A RULE.
    // A seat's own separator-agnostic sweep surfaced the terminal-backslash shape in its drift set,
    // consulted this classifier, was told "R4, filed", and withdrew the candidate. The instrument found the
    // defect and the classifier explained it away. That is the whole mechanism by which a predicate wider
    // than its residual converts a finding into an excuse -- not a hypothetical, and not a failure of the
    // corpus, which had the cell.
    //
    // Which is the same distinction this file has had to draw twice before, in the other direction: an
    // assertion can discriminate perfectly under mutation and still never be shown the input that breaks
    // it. Here the corpus HAS the input and the fix removed the only outcome the predicate governs. Adding
    // cells would not change that, so the corpus is deliberately not widened to manufacture a second kill.
    /// <summary>
    /// The superseded enumerating predicate may exist in exactly one member: the one that measures it.
    /// </summary>
    /// <remarks>
    /// <para>"Separator evidence is spelled once for every classifier in this file" is a TOTALITY claim, and
    /// totality claims are the class this PR has now had wrong four times -- the separator-alphabet table
    /// went stale three times, the guard-site table once. The claim was true when written and false one
    /// commit later, when a third live copy of the enumeration was found after the form had been declared
    /// gone. It was then re-checked by grep, which is the same instrument as memory with extra steps.</para>
    /// <para>So it is checked here instead, and checked as an ABSENCE rather than as a count: counts of the
    /// code's own shape are the prose most likely to rot, and a count cannot detect a missing row. The
    /// enumeration is permitted in exactly one member -- the divergence test, which must reconstruct it to
    /// measure what it excuses -- and nowhere else. Non-vacuity is a property, not a number: the helper must
    /// have more than one caller, or "spelled once for every classifier" has no content.</para>
    /// <para>THE FIRST VERSION OF THIS GUARD WAS THE DEFECT IT GUARDS AGAINST, WHICH IS WHY IT IS SPELLED
    /// BY CAUSE. It searched for the two ESCAPED SPELLINGS the enumeration happened to be written in. The
    /// historical predicate spells its second literal with FOUR source backslashes and the search term had
    /// two, so the guard could not see the real thing at all -- and the mutant that "proved" it was
    /// hand-written, by the same author, in the one spelling it covered. A guard narrower than the form it
    /// models, killed by a mutant narrower than the real one, passing for exactly the reason it was
    /// worthless. Seventh instance of "enumerating is the failure mode", and the first inside the
    /// construct built to stop it.</para>
    /// <para>So no escaped text is matched. The argument literal is PARSED OUT and UNESCAPED, and the
    /// question asked of it is the residual's own: is this nothing but separator characters? Verified in
    /// three independent spellings -- <c>":\\"</c>, <c>"\\\\"</c> and the char form -- because the failure
    /// mode was being right about one of them.</para>
    /// <para>This reads its own source through <see cref="CallerFilePathAttribute"/>. That is a real
    /// dependency on the sources being present beside the build, and it is taken deliberately: the
    /// alternative is a prose claim, which is the artifact that failed. A missing file FAILS rather than
    /// skips, because a totality guard that silently opts out is the defect it exists to catch.</para>
    /// </remarks>
    [Fact]
    public void TheSupersededEnumeration_SurvivesOnlyWhereItIsMeasured()
        => AssertTheSupersededEnumerationIsConfined();

    // THE SOURCE IS READ FROM THE ASSEMBLY, NOT FROM DISK, AND THAT IS A CI FINDING RATHER THAN A
    // PREFERENCE. This used to take the path from [CallerFilePath] and read the file. It passed on every
    // developer machine and failed on every pipeline run: deterministic builds rewrite source paths to
    // `/_/tests/...`, so the file is not beside the build and the guard's own fail-rather-than-skip branch
    // fired. Green locally, red in CI, and invisible to a local gate -- the exact shape this file spends
    // 4,000 lines guarding against, arriving in the guard itself.
    // The remedy is not to special-case CI, and not to skip when the source is absent -- skipping IS the
    // opt-out this guard exists to catch. The source is an EmbeddedResource, so the two environments are
    // identical, a missing resource is still a hard failure, and the copy is pinned to the build under
    // test rather than to whatever is on disk beside it.
    // The local gate now runs with -p:ContinuousIntegrationBuild=true, which reproduces the CI path
    // mapping. A gate that cannot see a failure mode is not a gate for it.
    private static void AssertTheSupersededEnumerationIsConfined()
    {
        const string SourceName = EmbeddedSourceName;
        const string Measuring = nameof(
            R4Classifier_DivergesFromTheEnumerationItReplaced_OnceABranchIsWeakened);

        // THREE MEMBERS MAY SPELL A SEPARATOR, EACH FOR A STATED REASON, AND THE LIST FAILS CLOSED.
        // Enumerating MEMBERS is not the defect this guards: the claim being checked is precisely "which
        // members spell it", so the list IS the claim, and anything absent from it is flagged by default.
        //   HasSeparatorEvidence                          -- defines the question; must spell it once.
        //   R4Classifier_DivergesFromTheEnumerationItReplaced_OnceABranchIsWeakened
        //                                                 -- reconstructs the superseded form to measure
        //                                                    what it excuses; the thing under test.
        //   SeparatorBearingConstants_AllHaveARowInTheTotalityTable
        //                                                 -- asks a DIFFERENT question of a different
        //                                                    subject: does a CONSTANT spell a separator,
        //                                                    not does a MESSAGE carry evidence. Deliberately
        //                                                    not folded; see its own remarks.
        FrozenSet<string> PermittedToSpellASeparator = new[]
        {
            Measuring,
            nameof(HasSeparatorEvidence),
            nameof(SeparatorBearingConstants_AllHaveARowInTheTotalityTable),
        }.ToFrozenSet(StringComparer.Ordinal);


        string[] lines = EmbeddedSourceLines();
        string member = "<file scope>";
        List<string> strays = [];
        HashSet<string> spellings = new(StringComparer.Ordinal);
        int helperCallers = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            // Prose may discuss the enumeration freely; that is the whole point of keeping its history.
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("///", StringComparison.Ordinal))
            {
                continue;
            }

            Match declaration = MemberDeclaration().Match(line);
            if (declaration.Success)
            {
                member = declaration.Groups["name"].Value;
            }

            // Stripped, because this counts CALLERS. Counted over raw text, a comment mentioning the
            // helper by name satisfies the adequacy bound below and the scanner is free to have gone
            // blind -- an adequacy counter a comment can satisfy is not a counter. The literal scan that
            // follows deliberately reads the RAW line, because its subject IS the literal.
            if (WithoutLiteralsOrComments(line).Contains(
                    nameof(HasSeparatorEvidence) + "(", StringComparison.Ordinal))
            {
                helperCallers++;
            }

            // STATED BY CAUSE, AFTER THE FIRST ATTEMPT WAS THE DEFECT IT GUARDS AGAINST. That attempt
            // searched for the two ESCAPED SPELLINGS the enumeration was written in. The historical
            // predicate spells its second literal with FOUR source backslashes; the search term had two;
            // and the mutant that "proved" the guard happened to be hand-written in the covered spelling.
            // A guard narrower than the form it models, killed by a mutant narrower than the real one.
            //
            // So the escaped text is never matched. The literal is PARSED OUT and UNESCAPED, and the
            // question asked of it is the residual's own: is this argument nothing but separator
            // characters? A containment test against such a literal IS a hand-rolled separator classifier,
            // however it happens to be spelled -- verbatim, interpolated, char or string.
            foreach (Match argument in ContainmentArgument().Matches(line))
            {
                // An ASSERTION is not a classifier. Assert.Contains(expected, collection) reads the same
                // to a scanner and decides nothing, including the two below that prove this scanner sees.
                if (argument.Index >= 7
                    && line.AsSpan(argument.Index - 7, 7).SequenceEqual("Assert."))
                {
                    continue;
                }

                // AND THE BOUNDARY OF THAT QUESTION, MEASURED RATHER THAN ASSUMED. A probe smuggling
                // `Contains(":\\")` into an unpermitted member does NOT trip this, because `:\` is not
                // separator-ONLY -- it is a drive-shape test, which decides something other than "is this
                // a separator". The superseded enumeration is still caught, by cause and not by luck: two
                // of its three conjuncts (`'/'` and `\\`) are separator-only, and one flagged literal
                // condemns the member. Stated because the probe's silence would otherwise read as a hole,
                // and an unexplained silence is how this file has mislabelled findings before.
                string literal = Unescape(argument.Groups["arg"].Value);
                if (!IsSeparatorOnly(literal))
                {
                    continue;
                }

                if (!PermittedToSpellASeparator.Contains(member))
                {
                    strays.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{SourceName}:{i + 1} in {member}: {trimmed}"));
                }

                spellings.Add(literal);
            }
        }

        // BEHAVIOUR FIRST. This guard shipped the other way round, and a reviewer showed what that costs:
        // under a double mutant -- the specimen tidied down to two conjuncts while a slash-only R4 predicate
        // was hand-rolled back into an unpermitted member -- the run failed on the missing bookkeeping entry
        // and NEVER NAMED THE STRAY. A live disclosure regression, masked by an accounting complaint.
        // Fifth guard in this change set to contain the defect it exists to prevent, and the first to
        // reintroduce one already fixed three times in this same file. The fix did not travel because it
        // had been recorded as three call sites rather than as a property; see the rule at the top of the
        // file, which is where it is recorded now.
        // The precondition exemption does not reach this. That exemption is for an axis so malformed the
        // behavioural report would be a FICTION. Here the scanner was not blind -- it saw both the bare
        // slash and the bare backslash -- so the report would have been true and merely incomplete.
        // Incomplete is adequacy. Adequacy last.
        Assert.True(
            strays.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"the superseded shape-enumerating R4 predicate is live in {strays.Count} place(s) outside "
                + $"the test that measures it. That form made a lone backslash invisible and bucketed a real "
                + $"disclosure into R4 across three corpora and five reviews; it must ask the residual's own "
                + $"question via {nameof(HasSeparatorEvidence)} instead."
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, strays)}"));

        // NON-VACUITY, IN THE DIMENSION THAT FAILED. The first guard could not see the historical spelling
        // at all and still passed, because nothing it was blind to happened to exist. So it is not enough
        // that no stray is found: the scanner must demonstrably RECOGNISE a separator literal, and the two
        // permitted members between them spell both a bare backslash and a bare slash.
        Assert.Contains("\\\\", spellings, StringComparer.Ordinal);
        Assert.Contains(":\\", spellings, StringComparer.Ordinal);
        Assert.Contains("/", spellings, StringComparer.Ordinal);

        Assert.True(
            helperCallers > 1,
            $"{nameof(HasSeparatorEvidence)} has at most one caller, so \"spelled once for every "
                + $"classifier\" has no content and this guard is vacuous.");
    }

    [GeneratedRegex(
        @"^\s*(?:\[[^\]]*\]\s*)?(?:public|private|internal|protected)[^(]*?\b(?<name>\w+)\s*\(",
        RegexOptions.ExplicitCapture)]
    private static partial Regex MemberDeclaration();

    // A containment test and its literal argument, string or char, verbatim or escaped.
    [GeneratedRegex(
        @"Contains\s*\(\s*@?(?:""(?<arg>(?:[^""\\]|\\.)*)""|'(?<arg>\\.|[^'])')",
        RegexOptions.ExplicitCapture)]
    private static partial Regex ContainmentArgument();

    private static string Unescape(string literal)
    {
        System.Text.StringBuilder builder = new(literal.Length);
        for (int i = 0; i < literal.Length; i++)
        {
            if (literal[i] == '\\' && i + 1 < literal.Length)
            {
                i++;
                builder.Append(literal[i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '0' => '\0',
                    _ => literal[i],
                });
                continue;
            }

            builder.Append(literal[i]);
        }

        return builder.ToString();
    }

    // The residual's own question, asked of a CLASSIFIER'S ARGUMENT rather than of a message: a literal
    // built only from path separators (a drive colon is permitted, since ":\\" was one of the two shapes
    // the superseded predicate enumerated) is not prose -- it is a separator test.
    private static bool IsSeparatorOnly(string literal)
        => literal.Length > 0
            && literal.All(c => c == '/' || c == '\\' || c == ':')
            && literal.Any(c => c == '/' || c == '\\');

    private static bool HasSeparatorEvidence(string text)
        => text.Contains('/', StringComparison.Ordinal) || text.Contains('\\', StringComparison.Ordinal);

    /// <summary>
    /// THE TOTALITY TABLE, EXECUTED. Every private constant in
    /// <see cref="LocalFileSystemBackend"/> that spells a path separator must have a row in the in-source
    /// table headed "EVERY SITE THE SEPARATOR ALPHABET IS SPELLED".
    /// </summary>
    /// <remarks>
    /// <para>The table went stale THREE TIMES in three rounds. Rows 8 and 9 were added after two reviewers
    /// independently found branch 5's anchors missing; the gate then found <c>NoSeparatorEarlierInSegment</c>
    /// -- which spells <c>/</c> three times -- had never had a row at all. The artifact whose stated purpose
    /// is "the rule was applied where a defect was measured rather than at every position the property has
    /// to hold" failed its own test three times.</para>
    /// <para>A COUNT-CHECK CANNOT DETECT A MISSING ROW. A reviewer swept the table for stale claims and
    /// reported "7-site table = SEVEN", which is true and says nothing: the observable is identical whether
    /// the table is complete or not. That is the same indistinguishable-check shape this file has been
    /// finding all along, and the remedy is the one already applied to a sibling PR's equivalent table --
    /// a prose table asserting completeness is a test that never runs.</para>
    /// <para>WHAT THIS TEST DOES NOT COVER, STATED RATHER THAN IMPLIED, because a completeness check that
    /// quietly is not one is the defect being fixed. It sees NAMED CONSTANTS only. Separator characters
    /// written inline in the <c>[GeneratedRegex]</c> attribute -- the recognizer branches' own key and
    /// value classes -- are invisible to it, as is <c>DiagnosticText.PathSeparators</c> in the sibling. Those
    /// remain prose obligations. The gain is that the sites which have historically gone missing, the
    /// extracted anchors, can no longer go missing silently.</para>
    /// </remarks>
    [Fact]
    public void SeparatorBearingConstants_AllHaveARowInTheTotalityTable()
    {
        // The table's rows, transcribed. A name here with no constant is as much a defect as a constant
        // with no row: the first means the table describes code that no longer exists, which is how R3
        // came to be documented after it was closed.
        string[] tabled =
        {
            "ClosingQuoteValue",
            "RootlessPathValue",
            "RootlessBackslashPathValue",
            "PathRegionStart",
            "NoSeparatorEarlierInSegment",
            "QuotedPathPrefix",
        };

        string[] actual = typeof(LocalFileSystemBackend)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Where(f => f.GetRawConstantValue() is string v
                && (v.Contains('/', StringComparison.Ordinal)
                    || v.Contains('\\', StringComparison.Ordinal)))
            .Select(f => f.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] expected = tabled.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            string.Join(", ", expected),
            string.Join(", ", actual));

        // ANTI-VACUITY. A reflection query that silently matches nothing would pass an empty-vs-empty
        // comparison forever, which is precisely the failure this test replaces.
        Assert.True(actual.Length >= 6, "the reflection query found no separator-bearing constants.");
    }

    // Asserts the ruling on any rendered text: no partition VALUE in either encoding, but the partition
    // COLUMN NAMES and the file name are still there for the operator.
    private static void AssertKeepsContextDropsValues(string rendered)
    {
        Assert.DoesNotContain(EncodedValue, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DecodedValue, Uri.UnescapeDataString(rendered), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EU", rendered, StringComparison.Ordinal);

        // Context survives: the partition COLUMN names and the data file name.
        Assert.Contains("email", rendered, StringComparison.Ordinal);
        Assert.Contains("region", rendered, StringComparison.Ordinal);
        Assert.Contains("part-DD73B2610EAF39BB5D3E26FBEDD83A69.parquet", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribePath_HiveEncodedPath_KeepsColumnNames_DropsPartitionValues()
    {
        string rendered = DiagnosticText.DescribePath(PartitionedPath);

        AssertKeepsContextDropsValues(rendered);
        Assert.Equal("'part-DD73B2610EAF39BB5D3E26FBEDD83A69.parquet' (partitioned by: email, region)", rendered);
    }

    [Fact]
    public void DescribePath_PoisonedPartitionColumnName_IsNeutralized()
    {
        // A partition COLUMN name is the #683/#665 foreign-schema-name class on a hostile table, so keeping it
        // is only safe because it is sanitized. A guard that dropped the value but echoed the key raw would
        // still be a log-injection vector.
        string rendered = DiagnosticText.DescribePath("e\r\nmail\u2028x=secret/part-0.parquet");

        Assert.DoesNotContain('\r', rendered);
        Assert.DoesNotContain('\n', rendered);
        Assert.DoesNotContain('\u2028', rendered);
        Assert.DoesNotContain("secret", rendered, StringComparison.Ordinal);
    }

    [Theory]
    // The path IS a partition directory: the terminal segment is `key=value`, not a file name.
    [InlineData("email=" + EncodedValue)]
    // ...with a trailing separator, which RemoveEmptyEntries collapses to the same shape.
    [InlineData("email=" + EncodedValue + "/")]
    // ...nested under another partition, so the earlier key is harvested but the LAST one decided the render.
    [InlineData("region=EU/email=" + EncodedValue + "/")]
    // The same four shapes over a BACKSLASH separator: DescribePath accepts both because a poisoned add.path
    // can use either, and a separator the scanner does not recognize collapses the whole path into one
    // "segment" -- which for these shapes means the partition value is rendered as if it were a file name.
    [InlineData("email=" + EncodedValue + "\\")]
    public void DescribePath_TerminalSegmentIsPartitionDirectory_StillDropsTheValue(string path)
    {
        // Round-4 finding (found independently by two seats): the original loop scanned segments
        // 0..Length-2 for `key=` and then rendered the LAST segment unconditionally as "the file name". When
        // the path ends at a partition DIRECTORY, that pushed the partition VALUE through Sanitize — which,
        // by the very argument this helper exists for, cannot remove an email address. Not reachable from
        // DeltaSharp's own call graph today, but IStorageBackend is an extensible seam and a poisoned
        // add.path takes these shapes trivially.
        string rendered = DiagnosticText.DescribePath(path);

        Assert.DoesNotContain(EncodedValue, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(rendered), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("email", rendered, StringComparison.Ordinal);

        // No file name exists, so the helper says so rather than rendering an empty pair of quotes.
        Assert.Contains("(directory)", rendered, StringComparison.Ordinal);
    }

    // These two rows used to live in the theory above, asserting that `email` IS named. That expectation
    // encoded the WINDOWS reading of `region=EU\email=...` -- two partition directories, the second of which
    // has a real column name. On POSIX the identical string is ONE directory whose name happens to contain a
    // backslash, and `email` is then value content that the caller supplied. The platform is not knowable
    // from the string, and the rule this file now applies everywhere is that an unknowable separator must not
    // be resolved toward disclosure. So the deeper key is no longer named.
    //
    // The cost is real and bounded: on a genuinely Windows-shaped path, partition column names below the
    // first are withheld. That is schema information, and the traversal/omission counts still report the
    // shape. The alternative is echoing whatever follows a backslash inside a value as though it were a
    // column name, which is how `owner=Legal\alice.taylor%40example.com` came to be rendered as
    // "partitioned by: alice.taylor%40example.com" -- a PII disclosure under a label claiming redaction.
    [Theory]
    [InlineData("region=EU\\email=" + EncodedValue)]
    [InlineData("region=EU\\email=" + EncodedValue + "\\")]
    [InlineData("owner=Legal\\email=" + EncodedValue + "\\part-0.parquet")]
    public void DescribePath_KeyNestedBehindABackslash_IsNotNamedAsAColumn(string path)
    {
        string rendered = DiagnosticText.DescribePath(path);

        Assert.DoesNotContain(EncodedValue, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(rendered), StringComparison.OrdinalIgnoreCase);

        // The first key is above the ambiguous separator, so it is still real and still named.
        Assert.Contains(path.StartsWith("region", StringComparison.Ordinal) ? "region" : "owner",
            rendered, StringComparison.Ordinal);

        // The nested one is not.
        Assert.DoesNotContain("email", rendered, StringComparison.Ordinal);
    }

    [Theory]
    // Bare, with and without a trailing separator (the latter is unambiguously a directory and STILL rendered
    // as a file name before the fix).
    [InlineData("=" + EncodedValue)]
    [InlineData("=" + EncodedValue + "/")]
    [InlineData("=" + EncodedValue + "\\")]
    // A doubled '=' -- key empty, value begins with '='.
    [InlineData("==" + EncodedValue)]
    // Nested behind a well-formed partition, so the interior key is harvested and the terminal one decides.
    [InlineData("region=EU/=" + EncodedValue)]
    // Nested behind ordinary directories, which are dropped -- so the terminal segment is the ONLY thing left.
    [InlineData("a/b/c/=" + EncodedValue)]
    public void DescribePath_TerminalSegmentWithAnEmptyHiveKey_StillDropsTheValue(string path)
    {
        // Round-8 (Privacy, MEDIUM): the terminal-segment recognizer tested `IndexOf('=') > 0`, so a segment
        // whose Hive key was EMPTY was not recognized -- and the terminal DEFAULT is to echo the segment as a
        // file name. This is the sibling of the empty-key fail-open closed in Redact the round before, and the
        // more dangerous half: an unrecognized INTERIOR segment is dropped (fails closed), while the terminal
        // one is echoed (fails open). Reachable through the ordinary public backend surface -- an OpenReadAsync
        // on a foreign add.path -- with no fault injection at all.
        string rendered = DiagnosticText.DescribePath(path);

        Assert.DoesNotContain(EncodedValue, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(rendered), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(directory)", rendered, StringComparison.Ordinal);
    }

    // R6, THE BARE TRAILING BACKSLASH, AND THE DOOR THAT KEEPS IT LATENT. `k=v\` is the one decline class a
    // separator-agnostic hunt over 22 candidate evidence tokens found that is neither R4 nor R1: the sibling
    // suppresses it, `k=v/`, `k=v\\`, `k=v\x` and `k=v\/` are all FULL, and only the bare trailing form
    // declines. It is not a live disclosure, and the reason is NOT a property of the recognizer -- it is
    // that the caller always presents the path ROOTED, so the table root supplies the separator evidence the
    // string itself lacks.
    //
    // That is a coupling between this recognizer's residual set and how paths are PRESENTED one layer out,
    // and couplings of that shape are invisible until something moves. A refactor that surfaces a relative
    // path, or trims the root for brevity, exposes R6 without touching the recognizer at all. So the door is
    // asserted rather than the residual: this test fails if the presentation ever stops supplying the root,
    // which is the only event that turns R6 from latent into live.
    [Fact]
    public async Task Redact_BareTrailingBackslashValue_IsRedactedByTheRecognizerAndByTheDoor()
    {
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => _backend.OpenReadAsync("k=" + EncodedValue + "\\", CancellationToken.None).AsTask());

        Assert.DoesNotContain(EncodedValue, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(error.Message), StringComparison.OrdinalIgnoreCase);

        // The entitled caller still gets the raw path on the typed property.
        Assert.Equal("k=" + EncodedValue + "\\", error.Path);

        // AND THE RECOGNIZER ITSELF, HANDED THE BARE STRING WITH NO ROOT. This assertion used to run the
        // other way: it asserted a DECLINE, and existed to say that the clean result above was the DOOR's
        // doing rather than the recognizer's. That was an honest description of a residual and a bad place
        // to leave a disclosure path, because it made the safety of the message depend on a caller that
        // always happens to root the path. Two reviewers disagreed about whether any caller could present
        // the bare shape; the fix was one character wide, so the disagreement was worth less than the round
        // it would have taken to settle, and it is recorded as DISPUTED AND MOOT rather than resolved.
        //
        // Both layers now hold independently, which is the only arrangement under which the dispute stays
        // moot: the door roots the path, and the recognizer would redact it even if the door stopped.
        string bare = LocalFileSystemBackend.HivePartitionValue()
            .Replace("Could not delete file k=" + EncodedValue + "\\", "${key}=<value>");

        Assert.Equal("Could not delete file k=<value>", bare);
    }

    [Fact]
    public async Task DescribePath_EmptyHiveKeyTerminal_DoesNotLeakThroughThePublicBackendSurface()
    {
        // The same shape end-to-end, through OpenReadAsync on a NotFound path -- no fault hook, no test seam.
        // Pins that the helper fix actually reaches the surfaced message rather than only the unit render.
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => _backend.OpenReadAsync("=" + EncodedValue, CancellationToken.None).AsTask());

        Assert.DoesNotContain(EncodedValue, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(error.Message), StringComparison.OrdinalIgnoreCase);

        // The entitled caller still gets the raw path on the typed property.
        Assert.Equal("=" + EncodedValue, error.Path);
    }

    [Theory]
    [InlineData('/')]
    [InlineData('\\')]
    public void DescribePath_NonHivePath_DropsIntermediateDirectoryNames(char separator)
    {
        // Non-Hive intermediate directories are dropped outright, not sanitized: a directory name is
        // operator-chosen free text that routinely carries a data subject ("customer-alice-taylor"), a tenant,
        // or a case number, and -- like a partition value -- none of that is a control character or over the
        // 128-char cap, so sanitizing would leave it intact. Only the file name and the Hive KEYS survive.
        //
        // The '/' arm is the control that fixes the expected shape; the '\\' arm is what pins the backslash
        // half of PathSeparators. Drop it and the scanner sees ONE segment with no '=', so the entire path --
        // intermediate directories included -- is rendered verbatim as "the file name".
        string rendered = DiagnosticText.DescribePath(
            string.Join(separator, "data", "customer-alice-taylor", "part-1.parquet"));

        // The directory NAMES are gone but their COUNT is kept: a count cannot carry table data, and
        // erasing the shape entirely is what made a traversal indistinguishable from an innocent file.
        Assert.Equal("'part-1.parquet' (2 directories omitted)", rendered);
        Assert.DoesNotContain("customer-alice-taylor", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(separator, rendered);
    }

    [Theory]
    [InlineData('/')]
    [InlineData('\\')]
    public void DescribePath_AbsoluteTenantScopedPath_DropsRootAndTenant_KeepsPartitionKey(char separator)
    {
        // A backend rooted at an absolute, tenant-scoped prefix is the worst case for the separator bug: with
        // the backslash half of PathSeparators removed the whole prefix becomes one segment, and because that
        // segment CONTAINS an '=' (from the Hive directory further down) the terminal-segment branch promotes
        // it to a partition KEY -- so the drive root and the tenant identity are rendered to the operator
        // under "partitioned by:", where they read as schema column names. Losing a segment would be a bug;
        // relabelling a tenant identifier as a column name is a disclosure.
        string rendered = DiagnosticText.DescribePath(
            string.Join(
                separator, "C:", "warehouse", "tenant-acme-corp", "tbl", "email=" + EncodedValue, "part-1.parquet"));

        // 4 omitted = the drive/root segment, "warehouse", "tenant-acme-corp", "tbl". None is named; all
        // four are counted, and "rooted" is what tells the operator this was never a table-relative path.
        //
        // THE FILE NAME IS WITHHELD ON THE BACKSLASH ARM, and this is a deliberate, measured trade rather
        // than a shrug. `part-1.parquet` sits after `email=<value>\`, and a backslash is an ordinary POSIX
        // filename character -- so under the POSIX reading this terminal is the TAIL OF THE PARTITION VALUE,
        // not a file name, and echoing it is exactly the leak Architect reached through the not-found door:
        //   email=al\ice.taylor@example.com  ->  'ice.taylor@example.com' (partitioned by: email)
        // The recognizer cannot know which platform produced a foreign add.path, so it fails closed and
        // declines to NAME anything whose segment boundary is platform-dependent. The cost is confined to
        // Windows-shaped paths that also contain a partition segment, and it costs a file name -- which
        // DeltaSharp itself generates -- rather than a value, which the attacker supplies. Every disclosure
        // assertion below is unchanged on both arms; only the naming differs.
        string expectedName = separator == '/' ? "'part-1.parquet'" : "'(directory)'";
        Assert.Equal(
            expectedName + " (rooted; 4 directories omitted; partitioned by: email)", rendered);
        Assert.DoesNotContain("tenant-acme-corp", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("warehouse", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("C:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(EncodedValue, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(rendered), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // THE THIRD DRIFT BETWEEN Redact AND DescribePath, on the axis this round introduced. Redact stopped
    // treating `\` as a delimiter because a backslash is an ordinary POSIX filename character; DescribePath
    // kept splitting on it, so the tail of a partition value landed in the terminal position where the
    // default is to ECHO it as the file name. Reached through the ordinary not-found door -- no fault hook,
    // no crafted detail -- which is the door the residual block calls always-reachable.
    //
    // The first row is the one that matters most: BOTH halves render into the SAME message, so Redact
    // redacted the value while DescribePath printed it three words earlier.
    [InlineData(@"email=al\ice.taylor@example.com")]
    [InlineData(@"email=alice.taylor@example.com\x")]
    [InlineData(@"a\b\c\email=alice.taylor@example.com")]
    [InlineData(@"tbl/email=al\ice.taylor@example.com")]
    [InlineData(@"email=al\ice.taylor@example.com\part-0.parquet")]
    // THE LATCH. The run flag must STAY set once a Hive separator has been seen, not be recomputed from
    // the immediately preceding segment: here the sensitive tail is two backslash-segments deep, so a
    // non-latching flag is reset by the innocuous middle segment and the third one is echoed. Added
    // because the mutant that removes the latch was GREEN without it -- a corpus gap, not a dead mutant.
    [InlineData(@"email=v\middle\alice.taylor@example.com")]
    [InlineData(@"email=v\a\b\taylor.alice@example.com")]
    public void DescribePath_BackslashInsideAPartitionValue_IsNeverEchoedAsAFileName(string path)
    {
        string rendered = DiagnosticText.DescribePath(path);
        string decoded = Uri.UnescapeDataString(rendered);

        foreach (string fragment in new[] { "alice", "taylor", "example.com", "ice." })
        {
            Assert.DoesNotContain(fragment, decoded, StringComparison.OrdinalIgnoreCase);
        }

        // The partition KEY still survives -- withholding the name must not cost the schema fact that makes
        // the message actionable.
        Assert.Contains("email", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SurfaceFailure_FrameworkDetail_DropsHivePartitionValues()
    {
        // Round-5 (Architect): the Hive-PII guarantee was defeated INSIDE THE SAME MESSAGE. Redact stripped
        // only the absolute root, so the framework exception's path survived as a table-RELATIVE Hive path
        // and landed in {detail} immediately after DescribePath had dropped it:
        //
        //   Deleting 'part-x.parquet' (partitioned by: email, region) failed:
        //   IOException: Could not delete file '<table-root>/email=alice%40example.com/region=EU/part-x.parquet'
        //
        // {detail} was correctly out of scope for #664 (inner-exception/ToString rendering); the Hive-path
        // ruling is newer and turns it into a PII channel in Message itself.
        await _backend.PutIfAbsentAsync(PartitionedPath, new byte[] { 1 }, CancellationToken.None);

        string absolute = Path.Combine(_root, PartitionedPath.Replace('/', Path.DirectorySeparatorChar));
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException($"Could not delete file '{absolute}'.")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync(PartitionedPath, CancellationToken.None).AsTask());

            // The value is gone from the message AND from everything ToString() renders...
            AssertKeepsContextDropsValues(error.Message);
            Assert.DoesNotContain(
                DecodedValue, Uri.UnescapeDataString(error.ToString()), StringComparison.OrdinalIgnoreCase);

            // ...but the KEY survives inside {detail} too, so the operator still sees the partitioning.
            Assert.Contains("email=<value>", error.Message, StringComparison.Ordinal);
            Assert.Contains("region=<value>", error.Message, StringComparison.Ordinal);

            // The raw path stays on the typed property for a caller entitled to it.
            Assert.Equal(PartitionedPath, error.Path);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    public async Task Redact_LeavesNonPathEqualsSignsAlone()
    {
        // The value-stripping rewrite must not eat ordinary diagnostic text: it only fires after a path
        // separator, so "errno=13" and "mode=0644" -- which carry no table data and do help an operator --
        // are untouched. Pins the bound on a redaction that would otherwise be tempting to widen.
        string root = Path.Combine(Path.GetTempPath(), "redact-" + Path.GetRandomFileName());
        using var backend = new LocalFileSystemBackend(root);

        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException("write failed: errno=13, mode=0644")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            Assert.Contains("errno=13", error.Message, StringComparison.Ordinal);
            Assert.Contains("mode=0644", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    // The KEY length. 128 was the old group cap and 129 was the bypass: a key one character over the cap
    // made the group unmatchable, and because the lookbehind requires the match to start immediately after a
    // separator -- every later position inside the run is preceded by a key character, so there is no
    // alternative start -- the ENTIRE key=value survived unredacted.
    [InlineData(128, 16)]
    [InlineData(129, 16)]
    [InlineData(4_096, 16)]
    // The VALUE length. 512 was the old cap; one character over left the tail behind.
    [InlineData(8, 512)]
    [InlineData(8, 513)]
    [InlineData(8, 8_192)]
    public async Task Redact_HivePartitionValue_IsStrippedAtAnyKeyOrValueLength(int keyLength, int padLength)
    {
        // Round-6 (Balanced, HIGH): the recognizer's quantifiers were capped, so an attacker who chose a
        // long enough key or value simply opted out of the redaction -- in the exact channel this PR added
        // the redaction to close. The lengths here straddle the OLD boundaries deliberately: mutating a
        // bound to an obviously-wrong value (512 -> 2) exercises the test, not the boundary, which is how
        // this survived a round of review.
        string key = new('k', keyLength);
        string value = EncodedValue + new string('z', padLength);
        string absolute = Path.Combine(_root, key + "=" + value, "part-x.parquet");

        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException($"Could not delete file '{absolute}'.")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            // No partition value survives, in either encoding, at any length...
            Assert.DoesNotContain(EncodedValue, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                DecodedValue, Uri.UnescapeDataString(error.Message), StringComparison.OrdinalIgnoreCase);

            // ...nor does the value's tail, which is what a capped value quantifier left behind.
            Assert.DoesNotContain(new string('z', 8), error.Message, StringComparison.Ordinal);

            // The redaction did fire (rather than the whole segment being dropped some other way) -- OR the
            // message was cut before the marker, which is strictly stronger (the whole tail, marker
            // included, is gone). Asserting the marker unconditionally would make the test length-fragile
            // without adding privacy value, so pin the disjunction explicitly.
            //
            // There are now TWO ways to be cut, and both are stronger than the marker. DetailMaxLength
            // caps the rendered result; RedactScanLimit caps what the recognizer ever sees, and backs its
            // cut off to a path separator so a half-segment is never handed to the regex. At these lengths
            // the back-off is what fires, and it removes the key as well as the value -- so "the whole run
            // is gone" is a third accepted outcome, not a failure.
            bool detailWasCapped = error.Message.EndsWith('…');
            bool wholeRunRemoved = !error.Message.Contains(new string('k', 8), StringComparison.Ordinal);
            Assert.True(
                detailWasCapped
                    || wholeRunRemoved
                    || error.Message.Contains("=<value>", StringComparison.Ordinal),
                $"expected the '=<value>' marker, a capped detail, or the whole key=value run to be cut; "
                + $"got: {error.Message}");
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Theory]
    // Control: an ordinary key.
    [InlineData("k")]
    // Round-7: the recognizer required a NON-EMPTY key, so a segment whose key was empty matched NOTHING and
    // the value survived in full -- the same fail-open class as the 129-character key, found by probing the
    // recognizer for shapes it DECLINES rather than shapes it mis-handles. An empty or '='-leading Hive key
    // is not something DeltaSharp writes, which is exactly why it must be covered: `add.path` is FOREIGN.
    [InlineData("")]
    [InlineData("=")]
    // Round-8: the key class excluded whitespace and quotes, so any of these made the group unable to span to
    // the '=' and -- the lookbehind admitting exactly ONE start position per segment -- the recognizer matched
    // NOTHING. Unlike every other row here this is not a foreign-table shape: DeltaWriteTarget percent-encodes
    // the partition VALUE and writes the column NAME raw, and nothing validates a partition column name, so a
    // table partitioned by a legal Delta column named `my col` or `o'brien` emits these on the happy path.
    [InlineData("my col")]
    [InlineData("my\tcol")]
    [InlineData("my\rcol")]
    [InlineData("my\u2028col")]
    [InlineData("my\u00a0col")]
    [InlineData("o'brien")]
    [InlineData("o\"brien")]
    // A 129-character key AND a space: the two independent fail-opens composed.
    [InlineData("my col" + "kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk")]
    public async Task Redact_HiveKeyOfAnyShape_StillStripsTheValue(string key)
    {
        string absolute = Path.Combine(_root, key + "=" + EncodedValue, "part-x.parquet");

        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException($"Could not delete file '{absolute}'.")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            Assert.DoesNotContain(EncodedValue, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                DecodedValue, Uri.UnescapeDataString(error.Message), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("=<value>", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    public async Task Redact_PercentEncodedHiveSeparator_IsStillRecognized()
    {
        // A foreign writer may percent-encode the '=' itself. "email%3Dalice%40example.com" is a Hive
        // directory that the literal-'='-only recognizer declined -- fail-open on a shape the attacker
        // selects, which is the whole class this PR closes.
        string absolute = Path.Combine(_root, "email%3D" + EncodedValue, "part-x.parquet");

        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException($"Could not delete file '{absolute}'.")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            Assert.DoesNotContain(EncodedValue, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                DecodedValue, Uri.UnescapeDataString(error.Message), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("=<value>", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Theory]
    // The confirmed-holding over-redaction corpus. Widening the key class is only safe if EVERY one of these
    // still passes through verbatim: a diagnostic that loses "errno=13" has traded a real operational cost
    // for no privacy gain. The permissive branch requires a separator or quote to the RIGHT of the value,
    // which is what keeps prose out of the match.
    [InlineData("Permission denied (errno=13)")]
    [InlineData("open /proc/self/fd failed: errno=13")]
    [InlineData("The parameter mode=0644")]
    [InlineData("check /var/log then set retries=5")]
    [InlineData("write failed: errno=13, mode=0644")]
    [InlineData("no equals sign anywhere in this message")]
    // Delimiter-adjacent rows. None of these places a separator or quote immediately after the value, which
    // is what branch 2's right anchor requires, so they must still pass verbatim. The rows that DO place one
    // are covered by Redact_DelimiterAdjacentProse_IsAKnownAcceptedOverRedaction.
    [InlineData("path /var/log/app: retries=5 attempts")]
    [InlineData("stat '/usr/lib' -> st_mode=0100644")]
    // A QUOTED path in front of the token. The old branch 2 key class permitted quote, colon and space, so
    // after the last '/' it took `file": errno` as a key and `13` as a value, with the trailing quote
    // satisfying the anchor — over-redacting the exact token the right anchor was introduced to preserve.
    // Branch 3 accepts a quote-bearing key only when a real PATH SEPARATOR follows, because a quote to the
    // right of a quote-bearing key is the signature of a quoted path in prose, not a Hive segment.
    [InlineData("Error opening \"/tmp/file\": errno=13\"")]
    // THE one cell of the {key content} x {right delimiter} matrix that must stay declined (residual R2).
    // The key candidate here is `file": errno` -- a quote AND a space -- delimited by a quote. Branch 2
    // admits quotes but not whitespace, branch 3 admits whitespace but not quotes, and keeping those two
    // relaxations SEPARATE rather than merging them into one permissive class is the entire reason this row
    // survives while "o'brien=v'" and "my col=v'" are both redacted.
    [InlineData("Could not find file '/tmp/x/y.parquet'.")]
    [InlineData("Access to the path '/var/lib/x' is denied.")]

    // ROOTLESS key=N/M PROSE USED TO BE PINNED HERE and has MOVED to the accepted-over-redaction corpus.
    // The rule that kept it verbatim was "prose resumes with a space, a path segment does not" -- and that
    // is false about paths. `part 0.parquet` is an ordinary file name, so the whitespace class that
    // protected these four rows was also an ATTACKER-SELECTABLE OPT-OUT: one space in a downstream segment
    // and a rootless partition value was echoed in full. Fail closed; the rows are pinned in the direction
    // they now behave, one theory down.
    public async Task Redact_LeavesOperationalDiagnosticsVerbatim(string detail)
    {
        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete" ? new IOException(detail) : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            Assert.Contains(detail, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    public async Task Redact_NeutralizesControlCharactersInTheFrameworkDetail()
    {
        // Redact did root-stripping and Hive-value stripping and never called Sanitize, so the channel this
        // PR moved IN scope for PII was still wide open for INJECTION. The {detail} carve-out cited #664,
        // but #664 was about inner-exception/ToString() RENDERING; defending one injection class and not the
        // other inside the same string is incoherent.
        string absolute = Path.Combine(_root, "sub\r\n[CRITICAL] forged", "part-x.parquet");

        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException($"Could not delete file '{absolute}'.")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            Assert.DoesNotContain('\r', error.Message);
            Assert.DoesNotContain('\n', error.Message);
            Assert.DoesNotContain('\u2028', error.Message);
            // Still diagnosable: the operation and the framework's own wording survive.
            Assert.Contains("Could not delete file", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    public async Task Redact_OversizedFrameworkDetail_IsBounded()
    {
        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException(new string('d', 40_000))
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            Assert.True(error.Message.Length < 1_000, $"message was {error.Message.Length} characters");
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Theory]
    // The INTERIOR sibling of the terminal empty-key fail-open. Interior non-recognition falls through to
    // droppedDirectories, so it was safe only by LUCK -- the value never reached the message either way. But
    // a recognizer that reports "1 directory omitted" where the segment is demonstrably a partition directory
    // is under-reporting the shape, and the two halves must not answer the same question differently.
    [InlineData("=", "'x.parquet' (partitioned by: (empty))")]
    [InlineData("==", "'x.parquet' (partitioned by: (empty))")]
    [InlineData("region=EU/=", "'x.parquet' (partitioned by: region, (empty))")]
    public void DescribePath_InteriorSegmentWithAnEmptyHiveKey_IsClassifiedAsAPartitionDirectory(
        string prefix, string expected)
    {
        Assert.Equal(expected, DiagnosticText.DescribePath(prefix + EncodedValue + "/x.parquet"));
        Assert.DoesNotContain(
            DecodedValue,
            Uri.UnescapeDataString(DiagnosticText.DescribePath(prefix + EncodedValue + "/x.parquet")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribePath_InteriorEmptyKey_StillCountsTowardTheElisionMarker()
    {
        // The old `equals <= 0` guard returned WITHOUT incrementing `total`, so an empty-key segment vanished
        // from every tally -- neither a partition nor a dropped directory -- and `total` desynchronized from
        // the materialized list, which is what the "(+N more)" marker is computed from.
        string path = string.Join("/", Enumerable.Range(0, 20).Select(_ => "=v")) + "/x.parquet";
        string described = DiagnosticText.DescribePath(path);

        Assert.Contains("(+4 more)", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE agreement test. DeltaSharp has two recognizers that independently answer "is this a Hive
    /// <c>key=value</c> segment?" — <see cref="DiagnosticText.DescribePath"/>, which drops the partition
    /// VALUE from a described path, and <c>LocalFileSystemBackend.Redact</c>, which strips it out of an
    /// echoed framework message. For three consecutive review rounds a shape was found in one half and
    /// fixed only there, producing a single message in which one half redacted the value and the other
    /// echoed it. This corpus is the union of EVERY shape found across those rounds, and it is asserted
    /// against BOTH halves, so a shape that is fixed in one and not the other fails here regardless of
    /// which one someone remembered.
    /// </summary>
    [Theory]
    // Round 0 — the ordinary shape.
    [InlineData("email")]
    // Round 6 — the quantifier bypass: a key one character past the old {1,128} cap.
    [InlineData("kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk")]
    // Round 7 — the empty and '='-leading keys.
    [InlineData("")]
    [InlineData("=")]
    // Round 8 — keys the strict character class could not span: whitespace, quotes, separators-as-data.
    [InlineData("my col")]
    [InlineData("my\tcol")]
    [InlineData("my\u2028col")]
    [InlineData("o'brien")]
    [InlineData("o\"brien")]
    public async Task HiveRecognizers_AgreeOnEverySeparatorSpelling(string key)
    {
        foreach (string separator in new[] { "=", "%3D", "%3d" })
        {
            string segment = key + separator + EncodedValue;

            // Half 1: DescribePath must classify the segment as a partition directory and drop its value —
            // both when it is INTERIOR (fail-closed by luck: the fallthrough is a drop) and when it is
            // TERMINAL (fail-OPEN: the fallthrough is "echo as a file name").
            foreach (string described in new[]
            {
                DiagnosticText.DescribePath(segment + "/part-x.parquet"),
                DiagnosticText.DescribePath(segment),
            })
            {
                Assert.Contains("partitioned by:", described, StringComparison.Ordinal);
                Assert.DoesNotContain(EncodedValue, described, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    DecodedValue, Uri.UnescapeDataString(described), StringComparison.OrdinalIgnoreCase);
            }

            // Half 2: Redact must strip the value out of the framework detail for the SAME segment.
            string absolute = Path.Combine(_root, segment, "part-x.parquet");
            LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
                ? new IOException($"Could not delete file '{absolute}'.")
                : null;
            try
            {
                await _backend.PutIfAbsentAsync("agree.bin", new byte[] { 1 }, CancellationToken.None);
                DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                    () => _backend.DeleteAsync("agree.bin", CancellationToken.None).AsTask());

                Assert.Contains("=<value>", error.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(EncodedValue, error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    DecodedValue, Uri.UnescapeDataString(error.Message), StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                LocalFileSystemBackend.IoFaultHook = null;
                File.Delete(Path.Combine(_root, "agree.bin"));
            }
        }
    }

    [Fact]
    public async Task DescribePath_PercentEncodedHiveSeparator_DoesNotLeakThroughThePublicBackendSurface()
    {
        // The divergence was visible INSIDE ONE MESSAGE: DescribePath echoed the raw segment as a file name
        // while Redact, in the very same string, redacted the value out of the framework detail. No fault
        // injection — an ordinary OpenReadAsync on a foreign add.path.
        string relative = "email%3D" + EncodedValue + "/part-x.parquet";

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => _backend.OpenReadAsync(relative, CancellationToken.None).AsTask());

        Assert.DoesNotContain(EncodedValue, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(error.Message), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("partitioned by: email", error.Message, StringComparison.Ordinal);
        Assert.Equal(relative, error.Path); // raw, on the typed property
    }

    [Theory]
    // The percent-encoded separator, in the shapes Privacy enumerated: bare, nested behind an ordinary
    // partition directory, lowercase, and with a trailing separator (unambiguously a directory).
    [InlineData("email%3D", "'(directory)' (partitioned by: email)")]
    [InlineData("email%3d", "'(directory)' (partitioned by: email)")]
    [InlineData("email%3D", "'(directory)' (partitioned by: email)", "/")]
    public void DescribePath_PercentEncodedSeparator_IsAPartitionDirectoryNotAFileName(
        string prefix, string expected, string suffix = "")
    {
        Assert.Equal(expected, DiagnosticText.DescribePath(prefix + EncodedValue + suffix));
    }

    [Fact]
    public void DescribePath_PercentEncodedSeparator_NestedBehindAnOrdinaryPartition()
    {
        Assert.Equal(
            "'part-x.parquet' (partitioned by: region, email)",
            DiagnosticText.DescribePath("region=EU/email%3D" + EncodedValue + "/part-x.parquet"));
    }

    [Theory]
    // The shared predicate's own contract. A '%' that is NOT the start of a %3D escape must not be mistaken
    // for a separator, and the FIRST separator wins so the key cannot greedily span a later one.
    [InlineData("plain.parquet", -1, 0)]
    [InlineData("100%25done", -1, 0)]
    [InlineData("pct%3", -1, 0)]
    [InlineData("a=b", 1, 1)]
    [InlineData("a%3Db", 1, 3)]
    [InlineData("a%3db", 1, 3)]
    [InlineData("a%3Db=c", 1, 3)]
    [InlineData("a=b%3Dc", 1, 1)]
    public void HiveSeparatorIndex_RecognizesEverySpellingAndPrefersTheFirst(
        string segment, int expectedIndex, int expectedLength)
    {
        int index = DiagnosticText.HiveSeparatorIndex(segment, out int length);

        Assert.Equal(expectedIndex, index);
        Assert.Equal(expectedLength, length);
    }

    /// <summary>
    /// Pins the recognizer's UNBOUNDED-key property BELOW the detail cap, where it is actually observable.
    /// </summary>
    /// <remarks>
    /// Every other regression for this property asserts on <c>DeltaStorageException.Message</c>, and
    /// <c>Redact</c> now terminates with a 512-character <c>Sanitize</c>. Past that length the cap truncates
    /// the message and MASKS whether the recognizer matched at all — so the marker assertion, which was
    /// never a privacy assertion but the ANTI-VACUITY guard, stopped doing its job on the 4,096-character
    /// row. Measured: with the key quantifier re-bounded to <c>{0,512}</c> on both branches the entire suite
    /// stayed green, while a <c>{0,128}</c> control turned red. The suite pinned the property to
    /// ~135 characters and then went blind — on the exact parameter that has failed open twice in this PR.
    /// That safety was a coincidence of <c>DetailMaxLength</c> happening to sit near the old bound, an
    /// undocumented coupling that a plausible future widening of the cap would silently break.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(4_096)]
    [InlineData(65_536)]
    public void Recognizer_StripsTheValueAtAnyKeyLength_BelowTheDetailCap(int keyLength)
    {
        string key = new('k', keyLength);
        string subject = $"Could not delete file '/table/{key}={EncodedValue}/part-x.parquet'.";

        string redacted = LocalFileSystemBackend.HivePartitionValue()
            .Replace(subject, static m => m.Groups["key"].Value + "=<value>");

        Assert.Contains(key + "=<value>", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(EncodedValue, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(redacted), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // The VALUE side of the same blind spot: a value long enough to push the marker past the cap.
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(4_096)]
    [InlineData(65_536)]
    public void Recognizer_StripsTheValueAtAnyValueLength_BelowTheDetailCap(int padLength)
    {
        string subject =
            $"Could not delete file '/table/email={EncodedValue}{new string('z', padLength)}/part-x.parquet'.";

        string redacted = LocalFileSystemBackend.HivePartitionValue()
            .Replace(subject, static m => m.Groups["key"].Value + "=<value>");

        Assert.Contains("email=<value>", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(EncodedValue, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("zzzzzzzz", redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins the REACHABILITY premise for the one shape the two branches deliberately do not cover: a
    /// non-strict key (whitespace- or quote-bearing) with no right delimiter after the value.
    /// </summary>
    /// <remarks>
    /// <para>That intersection is not closable in the recognizer — adding <c>|$</c> to branch 2's anchor
    /// reopens <c>errno=13</c>, the exact case that justified the two-branch split — so what makes it safe
    /// is a property of the messages this backend actually surfaces, and that property was UNWRITTEN. This
    /// test makes it executable.</para>
    /// <para>The premise turns out to be stronger than "the runtime quotes paths". Driving every genuine
    /// failure door on this backend — no fault injection — <b>none of them surfaces a runtime path at all</b>:
    /// the confinement and not-found guards pre-empt with their own DescribePath-rendered message, and the
    /// syscall wrappers synthesize a path-free errno detail (<c>"unlinkat failed (errno 1)"</c>). The
    /// unquoted-terminal shape therefore has no door to arrive through. The regression this guards against
    /// is a future door — an object-store adapter, or someone "improving" an errno message by appending the
    /// path — starting to emit one; that mutation turns this test red and names the assumption instead of
    /// leaking silently.</para>
    /// </remarks>
    [Fact]
    public async Task Redact_NoGenuineDoorOnThisBackendSurfacesARuntimePath()
    {
        string segment = "my col=" + EncodedValue; // the exact class branch 2 exists for; #708 writes it
        Directory.CreateDirectory(Path.Combine(_root, segment));
        await File.WriteAllBytesAsync(Path.Combine(_root, segment, "x.parquet"), [1], CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(_root, "f" + segment), [1], CancellationToken.None);

        var doors = new List<Func<Task>>
        {
            // Not found.
            () => _backend.OpenReadAsync(segment + "/missing.parquet", CancellationToken.None).AsTask(),
            // A genuine syscall failure: unlinkat on a non-empty directory.
            () => _backend.DeleteAsync(segment, CancellationToken.None).AsTask(),
            // A regular file used as a path component (ENOTDIR), on both the read and the write side.
            () => _backend.OpenReadAsync("f" + segment + "/x.parquet", CancellationToken.None).AsTask(),
            () => _backend.OpenWriteAsync("f" + segment + "/y.parquet", CancellationToken.None).AsTask(),
        };

        foreach (Func<Task> door in doors)
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(door);

            // No absolute path, and therefore no undelimited Hive segment, in any of them.
            Assert.DoesNotContain(_root, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(EncodedValue, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                DecodedValue, Uri.UnescapeDataString(error.Message), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    // The BOUNDARY of the over-redaction corpus, recorded as KNOWN rather than left to be discovered. Every
    // pinned prose row places nothing after the value; these place the one thing branch 2 requires -- a
    // separator or quote -- so they are eaten. The direction is safe (redaction, not disclosure) and the
    // realism is low for .NET IO messages, but an undocumented boundary is how the last four fail-opens got
    // in, so it is pinned in the direction it actually behaves.
    [InlineData("path /var/log/app: retries=5/6 attempts", "retries=<value>/6 attempts")]
    [InlineData("mount /dev/sda1 opts=rw,noatime/etc", "opts=<value>/etc")]
    // Newly eaten by the delimiter-strength ladder, and recorded rather than quietly dropped. Branch 1 pairs
    // a fully permissive key with a REAL PATH SEPARATOR anchor, which is what buys "o'brien y=v/" -- a legal
    // Delta column name carrying both a quote and a space. These rows are the price, and it is the safe
    // direction: a lost errno in prose that happens to precede a later path, never a disclosed value. The
    // realistic .NET IO corpus in Redact_LeavesOperationalDiagnosticsVerbatim is unaffected.
    //
    // The backslash-terminated spelling of this row USED to be here and has moved to the verbatim corpus:
    // once `\` stopped counting as a right delimiter, that prose stopped being eaten. The over-redaction
    // corpus shrinking is a real gain and is pinned on the other side rather than deleted.
    [InlineData("Access to the path '/var/lib' is denied; mode=644/tmp", "mode=<value>/tmp")]
    [InlineData("stat /var/lib/x then set retries=5 for /tmp/y", "retries=<value>/tmp/y")]
    // Quote-delimited, key carries a quote but NO whitespace, so branch 2 takes it. This is the same cell
    // that makes "'<root>/o'brien=alice%40example.com'." redact, which is a leak we are required to close;
    // declining it here would mean declining that.
    [InlineData("Error opening \"/tmp/file\":errno=13\"", "errno=<value>\"")]
    // ROOTLESS key=N/M PROSE, moved here from the verbatim corpus when the downstream-whitespace class was
    // removed from branch 5's right anchor. These four rows were the discriminating rows for mechanism 7's
    // first fix and they are still discriminating -- they just discriminate the other way now, and pinning
    // the exact rendered text is what makes the cost VISIBLE rather than a sentence claiming it is small.
    //
    // What was traded, stated as a comparison rather than as a preference: a numeric ratio in a rootless
    // framework message loses its numerator, and the key, the denominator and the surrounding prose all
    // survive -- against a partition value echoed in full whenever a file name contains a space. The second
    // is a disclosure the attacker selects by naming a file, which is the shape this file's fail-closed rule
    // exists for: a shape the recognizer declines is a redaction the attacker opted out of by choosing it.
    [InlineData("compression ratio=1/2 rejected", "ratio=<value>/2 rejected")]
    [InlineData("quota=80/100 exceeded", "quota=<value>/100 exceeded")]
    [InlineData("invalid range=0/0 in header", "range=<value>/0 in header")]
    [InlineData("scale=3/4 is out of bounds", "scale=<value>/4 is out of bounds")]
    // PROSE FOLLOWED BY A ROOTLESS-REACHABLE WINDOWS PATH, moved here from a test that asserted the
    // opposite. It used to DECLINE, and the test that pinned the decline was named
    // `..._DeclinesRatherThanInventingAKey` -- but the decline echoed `alice%40example.com` in full. A
    // decline that leaks is not the safe direction; it is the unsafe one wearing the safe one's name.
    //
    // Branch 6 now reaches it, and the cost is exactly what the old name warned about: the key is harvested
    // from PROSE (`retries`), so the rendered message asserts a partition column that does not exist, and
    // the intervening prose is eaten. That is a diagnostics-fidelity cost of the same kind as every other
    // row in this corpus, and it is paid for closing a value echo.
    //
    // THE NARROWER BRANCH THAT WOULD SPARE THIS ROW WAS REJECTED ON PURPOSE. Requiring the backslash to be
    // reachable from the value's start WITHOUT CROSSING WHITESPACE keeps this row verbatim and passes every
    // other test in this file -- and it hands the attacker a switch, because the whitespace it keys on is
    // INSIDE THE VALUE: `k=a b\c.parquet` would decline and echo. That is the round-24 defect verbatim, one
    // constant to the left. The rule is older than either row: a class that narrows a recognizer on evidence
    // the attacker supplies is a switch the attacker owns.
    [InlineData(@"retries=5 then check C:\tbl\name=alice%40example.com", "retries=<value>")]

    // PROSE ENDING IN A BACKSLASH, WHICH THIS CORPUS HAS NOW GAINED AND LOST AND GAINED AGAIN. It began
    // here, moved to the verbatim corpus when branch 6's evidence class was written to require content
    // AFTER the separator, and has come back because that requirement was the third attacker-selectable
    // opt-out: a value ending AT its backslash declined and echoed in full.
    //
    // The row is the entire measured cost of closing it. Flipping the one character breaks five tests, and
    // the other four are expectations that encode the old behaviour -- one theory row, two classifier
    // models and a residual. This is the only DIAGNOSTIC that changes, and what it loses is the trailing
    // separator on a mode bitmask. Set against a value going out in full, the trade is not close, and it is
    // the same trade already taken one round earlier when the whitespace-reachability narrowing was
    // rejected: pay in over-redaction rather than leave the attacker a switch.
    //
    // AND THIS ROW WAS CITED AS THE REASON THE DEFECT WAS NOT A DEFECT, which is worth recording because
    // the citation was made in good faith and was wrong in a way that is easy to repeat. A reviewer found
    // `k=SEN\` independently, and withdrew it as "explicitly in R4 and BIDIRECTIONALLY PINNED by
    // st_mode=0100644\". The row it named lived in Redact_LeavesOperationalDiagnosticsVerbatim and asserted
    // `Assert.Contains(detail, error.Message)` -- that the string passes through UNCHANGED. That is one
    // direction, and it is the over-redaction one. Against a value-shaped input it asserted precisely the
    // behaviour that WAS the leak.
    //
    // So the shape had two artifacts pointing at it and both pointed the wrong way: the classifier said
    // "R4, filed, fine", and the pin said "protected", while one was excusing the echo and the other was
    // pinning it. A test that asserts a string survives is not a bidirectional pin on a recognizer; it is
    // half of one, and the missing half is the half that matters. Both halves are now present -- this row
    // pins the over-redaction, and Redact_RootlessBackslashPath_RedactsTheValueAndLeavesBareProseAlone
    // pins the redaction of the same shape.
    [InlineData(@"stat '/usr/lib' -> st_mode=0100644\", "st_mode=<value>")]
    public async Task Redact_DelimiterAdjacentProse_IsAKnownAcceptedOverRedaction(string detail, string expect)
    {
        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete" ? new IOException(detail) : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            Assert.Contains(expect, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Theory]
    // BOTH blocking findings of the re-confirmation round, end-to-end through a real DeleteAsync fault --
    // the monotonicity matrix pins them at the recognizer, these pin them at the message.
    //
    // Every column name here is a LEGAL Delta column name, and DeltaWriteTarget emits the name RAW into
    // add.path (only the VALUE is percent-encoded), so all of these are reachable on DeltaSharp's own write
    // path. They are not foreign-table hypotheticals.
    //
    // (1) Quality: key bearing a quote AND whitespace. The three-branch form excluded quotes in one branch
    //     and whitespace in the other, so their INTERSECTION matched nothing and the value was echoed.
    //     These carry BOTH properties, so the quote-delimited form is residual R2 and is NOT covered --
    //     see the third argument. It is irreducible: `o'brien y=v'` and `file": errno=13"` are the SAME
    //     STRING SHAPE, so no recognizer can redact one and preserve the other. In practice the exposure is
    //     confined to a message naming a partition DIRECTORY, because a data file always appends
    //     "/part-<guid>.parquet" and so always supplies the path separator that branch 1 needs.
    [InlineData("o'brien y", false)]
    [InlineData("my col\"x", false)]
    [InlineData("a b'c", false)]
    // (2) Security: quote-bearing key delimited by a QUOTE rather than a path separator. This was a
    //     REGRESSION -- it redacted correctly at 9220b66 and leaked at 0df0d1f, because narrowing a key
    //     class to protect `errno=13"` silently withdrew coverage that already existed. Anchoring by
    //     delimiter STRENGTH rather than by key content is what lets both hold at once.
    [InlineData("o'brien", true)]
    [InlineData("my col", true)]
    [InlineData("co\"l", true)]
    public async Task Redact_NonStrictColumnName_StillStripsTheValue_UnderEitherDelimiter(
        string column, bool quoteDelimiterCovered)
    {
        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);

        // A real path separator on the right, and a QUOTE on the right (the shape .NET itself emits when it
        // quotes a path). Both must strip.
        List<string> subjects =
        [
            $"Could not delete file '{_root}/{column}={EncodedValue}/part-x.parquet'.",
        ];

        if (quoteDelimiterCovered)
        {
            subjects.Add($"Could not delete file '{_root}/{column}={EncodedValue}'.");
        }

        foreach (string subject in subjects)
        {
            LocalFileSystemBackend.IoFaultHook = tag => tag == "delete" ? new IOException(subject) : null;
            try
            {
                DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                    () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

                // Unescape first: percent-encoding is not redaction, and the whole point of the ruling is
                // that Uri.UnescapeDataString recovers the original from an echoed encoded value.
                Assert.DoesNotContain(
                    DecodedValue, Uri.UnescapeDataString(error.Message), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(EncodedValue, error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                LocalFileSystemBackend.IoFaultHook = null;
            }
        }
    }

    [Theory]
    // THE PARTIAL-MATCH CLASS, end-to-end. A partial match emits "<value>" over a value the recognizer only
    // half-consumed, so the message asserts a removal that did not happen -- strictly worse than declining,
    // because a decline at least looks like what it is. All four rows below were reported as partial matches
    // at 930072b; the mechanism is the VALUE class, not the key class and not branch order.
    //
    // (a) A quote INSIDE a value satisfied the quote-delimiter lookahead early. The lookahead could not tell
    //     an interior quote from the closing quote of a quoted path, so it stopped at the first one.
    [InlineData("'/root/email=Aaaa'Bbbb%40example.com'", "Bbbb")]
    [InlineData("/root/o'brien=Aaaa'Bbbb%40corp.com'", "Bbbb")]
    // (b) The terminal branch was unanchored, so it stopped the value at whitespace and emitted anyway.
    [InlineData("/root/email=Aaaa Bbbb%40example.com", "Bbbb")]
    [InlineData("/root/k=Aaaa Bbbb", "Bbbb")]
    public async Task Redact_ValueBearingQuoteOrSpace_IsNeverPartiallyMatched(string subject, string tail)
    {
        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException($"Could not delete file '{subject}'.")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            // The invariant, stated as an implication rather than as two independent facts: it is fine for
            // the recognizer to DECLINE (no marker, whole segment echoed and then sanitized), and fine for
            // it to consume the value FULLY. What it may never do is claim a removal and leave the tail.
            if (error.Message.Contains("<value>", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(tail, error.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("Aaaa", error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Theory]
    // SPARK'S escapePathName DOES NOT ESCAPE AN APOSTROPHE, and add.path is foreign, so a quote inside a
    // partition VALUE is a real shape rather than a constructed one. These rows carry two assertions each,
    // and they pin two different halves of ClosingQuoteValue:
    //
    //   * no fragment of the value survives -- pins the value run being PERMISSIVE. A quote-excluding value
    //     run stops at the apostrophe, and the quote-delimited branches then decline; only the
    //     end-of-message branch is left, so the tail after the closing quote is consumed as value.
    //   * the trailing framework prose survives -- pins the CLOSING-QUOTE anchor doing the terminating,
    //     rather than the end-of-message branch swallowing the rest of the sentence. This is the
    //     diagnosability the permissive value run buys back, and it is invisible to a leak-only oracle.
    //
    // Row 3 is the discriminator against the literal form four reviewers prescribed (permissive value, bare
    // `(?=['"])` lookahead): with no closing quote anywhere, greedy backtracking settles on the INTERIOR
    // apostrophe and emits a marker over "O" alone, leaving "'Brien Household" standing.
    [InlineData(
        "Access to the path '/tbl/name=O'Brien Household' is denied.",
        "is denied.")]
    [InlineData(
        "Could not find a part of the path '/tbl/name=\"Ace\" Taylor'.",
        "Could not find a part of the path")]
    [InlineData(
        "Could not find a part of the path /tbl/name=O'Brien Household",
        "Could not find a part of the path")]
    public async Task Redact_ApostropheBearingPartitionValue_IsFullyStrippedAndKeepsTheProse(
        string detail,
        string retained)
    {
        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete" ? new IOException(detail) : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            Assert.Contains("<value>", error.Message, StringComparison.Ordinal);
            foreach (string fragment in new[] { "Brien", "Household", "Ace", "Taylor" })
            {
                Assert.DoesNotContain(fragment, error.Message, StringComparison.Ordinal);
            }

            Assert.Contains(retained, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Theory]
    // A BACKSLASH INSIDE A PARTITION VALUE, end-to-end. `\` is an ordinary POSIX filename character: it is
    // legal in a directory name, so `dept=Legal\Compliance` is ONE component whose value contains a
    // backslash. The recognizer nevertheless excluded `\` from every value class while accepting it in
    // every right anchor, so the value run stopped at the backslash and that same backslash satisfied the
    // lookahead -- emitting the marker over a value it had only half consumed. add.path is foreign, so
    // nothing upstream prevents this.
    //
    // This was NOT reachable only through an exotic delimiter: it fired in every envelope, quoted and
    // unquoted, with and without a trailing path.
    [InlineData("Could not delete file /tbl/dept={0}/part-0.parquet")]
    [InlineData("Could not delete file '/tbl/dept={0}/part-0.parquet'")]
    [InlineData("Could not find a part of the path '/tbl/dept={0}'")]
    [InlineData("Access to the path '/tbl/dept={0}' is denied.")]
    [InlineData("Could not find a part of the path /tbl/dept={0}")]
    [InlineData(@"Could not delete file \tbl\dept={0}\part-0.parquet")]
    public async Task Redact_BackslashInsidePartitionValue_IsNotMistakenForASegmentBoundary(string envelope)
    {
        const string Value = @"Legal\alice.taylor%40example.com";
        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException(string.Format(CultureInfo.InvariantCulture, envelope, Value))
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            string decoded = Uri.UnescapeDataString(error.Message);
            foreach (string fragment in new[] { "Legal", "alice", "taylor", "example.com" })
            {
                Assert.DoesNotContain(fragment, decoded, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Theory]
    // THE FOLLOWER AXIS, end-to-end. What comes immediately AFTER a quote inside a partition value decides
    // whether a boundary-shaped lookahead is satisfied by it, so the follower -- not merely the presence of
    // a quote -- is the axis that has to be ranged over. Every one of these rows partial-matched at
    // 4de2591 except the LETTER row, which is the one the corpus happened to contain.
    [InlineData("Boys' Club.taylor%40example.com", "space")]
    [InlineData("O'Brien Household", "letter")]
    [InlineData("Boys'.Club", "period")]
    [InlineData("Boys',Club", "comma")]
    [InlineData("Boys':Club", "colon")]
    [InlineData("Boys'\tClub", "tab")]
    public async Task Redact_QuoteInsideValue_IsFullyStrippedWhateverFollowsIt(string value, string follower)
    {
        _ = follower;
        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException("Could not find a part of the path /tbl/email=" + value + " while locked")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            Assert.Contains("<value>", error.Message, StringComparison.Ordinal);
            foreach (string fragment in new[] { "Boys", "Club", "Brien", "Household", "taylor" })
            {
                Assert.DoesNotContain(fragment, error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Theory]
    // POSSESSIVE PARTITION VALUES, ACROSS EVERY SPELLING A REAL PRODUCER EMITS. Reported independently by
    // two reviewers with these exact values; kept under their names because the rows are the evidence.
    // Spark's escapePathName leaves an apostrophe raw and add.path is foreign, so `Teachers' Union` is an
    // ordinary partition value, not a crafted one.
    //
    // The axis that matters here is the MESSAGE ENVELOPE, not the value: the same value is reported by
    // .NET inside single quotes, inside double quotes, with trailing prose, and bare. A reviewer probing
    // the recognizer directly with a hand-built `'/root/dept=Teachers' Union` -- an opening quote and no
    // closing quote anywhere -- is testing a string no producer emits, and the one shape on which an
    // interior quote is provably indistinguishable from a closing one. That shape has exactly one source,
    // Redact's own scan-limit cut, and it is closed there rather than here; see
    // Redact_ScanLimitCut_LandsOnASegmentBoundaryAndNeverHalfARun.
    [InlineData("Could not find a part of the path '<P>'.", "Teachers' Union")]
    [InlineData("Could not find a part of the path '<P>'.", "Players' Association")]
    [InlineData("Could not find a part of the path '<P>'.", "Riders' Club")]
    [InlineData("Could not find a part of the path '<P>'.", "Ace\" Taylor")]
    [InlineData("Access to the path '<P>' is denied.", "Teachers' Union")]
    [InlineData("Could not find file \"<P>\".", "Teachers' Union")]
    [InlineData("Could not find file '<P>/part-0.parquet'.", "Teachers' Union")]
    [InlineData("stat failed on <P>", "Riders' Club")]
    [InlineData("<P>", "Players' Association")]
    public async Task Redact_PossessivePartitionValue_IsFullyStrippedInEveryMessageEnvelope(
        string envelope, string value)
    {
        string detail = envelope.Replace("<P>", "/root/dept=" + value, StringComparison.Ordinal);

        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete" ? new IOException(detail) : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            Assert.Contains("<value>", error.Message, StringComparison.Ordinal);

            // NOT merely "the value is absent" -- every WORD of it, because the failure being pinned is a
            // partial match that removes the head and leaves the tail standing next to the marker.
            foreach (string word in value.Split([' ', '\'', '"'], StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.DoesNotContain(word, error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Theory]
    // AN INTERIOR QUOTE OF THE OTHER KIND IS NOT A CLOSING QUOTE. QuotedPathPrefix captures the quote that
    // OPENED the path, and ClosingQuoteValue's anchor is a BACKREFERENCE to it -- not a literal ['"] class.
    // The distinction is invisible while the path closes normally, because the value run is greedy and a
    // literal class can then only ever select a position further right (more over-redaction, never a leak).
    // It becomes a disclosure exactly when the path does NOT close: then the other-kind quote is the only
    // quote-shaped thing left, a literal class stops on it, and the rest of the value survives beside a
    // <value> marker. These rows are the ones that separate the two forms.
    [InlineData("'", "\"")]
    [InlineData("\"", "'")]
    public async Task Redact_InteriorQuoteOfTheOtherKind_IsNotMistakenForTheClosingQuote(
        string opening, string interior)
    {
        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException("Could not find file " + opening + "/root/email=Aaaa" + interior + " Bbbbcccc")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            Assert.DoesNotContain("Aaaa", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Bbbbcccc", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    // THE OTHER HALF OF THE SCAN LIMIT. Its COST is pinned (the ReDoS test above); its BENEFIT was not,
    // and an unpinned justification is how a constant drifts. RedactScanLimit is 2x DetailMaxLength rather
    // than equal to it because REDACTION SHORTENS TEXT: a segment costing 136 raw characters costs 17 once
    // its value is replaced, so prose sitting beyond the 512-character output cap in the RAW message can
    // still fit under it in the REDACTED one. Cutting the input at the output cap would throw that prose
    // away before the recognizer ever had the chance to make room for it -- strictly a diagnosability loss,
    // never a disclosure one, which is exactly why it needs a test rather than an argument. (2x, not the
    // former 8x: at 8x the backward lookbehind was a ~0.5s ReDoS on the I/O failure path; 2x bounds the
    // quadratic term to ~1 KB while still buying the headroom this test pins.)
    //
    // 6 segments of 136 raw characters collapse to 17 apiece: 816 raw becomes 102. The marker therefore
    // sits past raw offset 816 -- well beyond the 512 output cap -- and still lands around offset 130 in the
    // result. With the limit at 1x it would be cut before redaction ever ran.
    public async Task Redact_ScanLimitHeadroom_LetsRedactionPullLaterProseUnderTheDetailCap()
    {
        const string Marker = "ZzMarkerZz";
        string leading = string.Concat(
            Enumerable.Repeat("/" + new string('k', 8) + "=" + new string('z', 126), 6));
        string detail = "Could not find file '" + leading + "/" + Marker + ".parquet'.";

        // The property under test only exists because the marker is far past the output cap in the raw
        // message. If this ever stops holding, the test has stopped testing anything.
        Assert.True(
            detail.IndexOf(Marker, StringComparison.Ordinal) > 512,
            "the marker must sit beyond DetailMaxLength in the RAW message for this test to mean anything");
        Assert.True(detail.Length < 1_024, "the cell must fit inside RedactScanLimit so no cut occurs");

        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete" ? new IOException(detail) : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            // The headroom paid off: prose from beyond raw offset 512 survived into the capped result.
            Assert.Contains(Marker, error.Message, StringComparison.Ordinal);

            // ...and it did so because the values were redacted, not because the cap is loose.
            Assert.Contains("=<value>", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('z', 8), error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    // THE SCAN LIMIT MUST NOT MANUFACTURE THE ONE SHAPE THE RECOGNIZER CANNOT PARSE. Cutting the input at
    // RedactScanLimit is safe in content terms -- it only removes text -- but a BLIND cut also invents a
    // quoted path whose closing quote was cut off, and that is the single input on which an interior quote
    // is indistinguishable from a closing one. The recognizer then stops at the apostrophe and the rest of
    // the value survives next to a <value> marker. No regex change can fix it, because the truncated string
    // is byte-identical to a legitimately closed one; the cut itself has to land on a segment boundary.
    //
    // THE ARITHMETIC, because this channel is only observable when it is set up deliberately. Redact caps
    // its OUTPUT at DetailMaxLength (512) and its INPUT at RedactScanLimit (1024), so text near the cut
    // normally never reaches Message at all -- it is past the 512 cap. It reaches Message only when the
    // segments BEFORE it redact away hard enough to pull it under the cap, which is the whole reason the
    // scan limit is 2x the detail cap. So: 7 leading segments of 136 chars each (952 chars) collapse to
    // 17 chars apiece (119), and the poisoned segment that straddles the 1,024 cut then lands around offset
    // 130 -- comfortably inside the surviving 512.
    public async Task Redact_ScanLimitCut_LandsOnASegmentBoundaryAndNeverHalfARun()
    {
        const string Head = "Could not find file '";
        string leading = string.Concat(
            Enumerable.Repeat("/" + new string('k', 8) + "=" + new string('z', 126), 7));

        // Straddles the 1,024-char scan limit: the value opens with an apostrophe-terminated run and then
        // continues, so a blind cut leaves "email=Aaaa' BBBB..." with no closing quote anywhere.
        string poisoned = "/email=Aaaa' " + new string('B', 700);
        string detail = Head + leading + poisoned;
        Assert.True(detail.Length > 1_024, "the cell must straddle RedactScanLimit to exercise the cut");
        Assert.True(
            (Head + leading).Length < 1_024 && detail.Length > 1_024,
            "the POISONED segment (not just the message) must straddle the cut, or the backoff is untested");

        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete" ? new IOException(detail) : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            // Nothing of the straddling value survives -- neither the head before the apostrophe nor the
            // tail after it, which is what a blind cut left behind beside the marker.
            Assert.DoesNotContain("Aaaa", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('B', 8), error.Message, StringComparison.Ordinal);

            // ...and the test is not passing merely because the message came back empty: the leading
            // segments still had to be recognised and stripped.
            Assert.Contains("=<value>", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('z', 8), error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    // REDOS REGRESSION. QuotedPathPrefix scans BACKWARD for the quote that opened the path, and on a
    // message with no quote at all that scan reaches position 0 from every start position -- quadratic.
    // Measured on the recognizer directly, unbounded, on this exact input shape:
    //
    //     segments    chars    time
    //        5,000   48,928    0.65 s
    //       10,000   98,928    2.22 s
    //       20,000  208,928    3.54 s
    //       40,000  428,928   15.40 s
    //       80,000  868,928   63.34 s
    //
    // Redact bounds the INPUT rather than the recognizer, so the cost ceiling is ~0.85 ms and CONSTANT.
    //
    // THE MARGIN IS THE POINT, AND IT WAS WRONG. This started at 40,000 segments against a 15 s ceiling --
    // which the table above shows costs 15.40 s, a 3% margin. It duly passed under load in one mutation
    // run and failed in another, i.e. it was a coin flip, not a regression test; a wall-clock assertion
    // whose verdict depends on machine load asserts nothing. 80,000 segments costs 63 s against the same
    // ceiling, a 4x margin measured on a machine running four parallel builds, so the quiet-machine margin
    // is wider still. The green path is unaffected: bounded, this input is ~1 ms whatever its length, so
    // the test is instant when correct and unambiguously slow when broken.
    public async Task Redact_QuoteFreeMegabyteMessage_IsBoundedNotQuadratic()
    {
        string adversarial = "/root" + string.Concat(
            Enumerable.Range(0, 80_000).Select(i => "/k a b" + i.ToString(CultureInfo.InvariantCulture)));

        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException(adversarial + "/email=Aaaa%40Bbbb/part-0.parquet")
            : null;
        try
        {
            long start = Stopwatch.GetTimestamp();
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            Assert.True(
                elapsed < TimeSpan.FromSeconds(15),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Redact took {elapsed.TotalSeconds:F1}s on a {adversarial.Length}-char quote-free "
                    + $"message; the backward scan is unbounded again."));

            // The bound must not become a disclosure channel: whatever survives is still capped.
            Assert.True(error.Message.Length < 6_000);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    // A message that OPENS a quote and never closes it is NOT a residual, and an earlier corpus row saying
    // otherwise was contradicting itself: it placed an opening quote before the path and then declared the
    // value to extend past the only closing quote. The message's own delimiters settle it -- the quoted
    // region ends at the next occurrence of the same quote character, so "Bbbb" below is prose, not value.
    // Redacting the quoted region and leaving the prose is therefore FULL, not PARTIAL, and this test
    // pins that reading so the next corpus cannot re-derive the wrong oracle.
    public async Task Redact_UnclosedOpeningQuote_RedactsTheQuotedRegionAndNothingElse()
    {
        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException("Could not find a part of the path '/tbl/email=Aaaa' Bbbb")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            // Inside the quoted region: gone. Outside it: prose, and preserved.
            Assert.Contains("<value>", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Aaaa", error.Message, StringComparison.Ordinal);
            Assert.Contains("Bbbb", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    /// <summary>
    /// THE MONOTONICITY MATRIX. Generated COMBINATORIALLY, not by hand, and that is the point of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three consecutive rounds of this PR fixed one shape and silently withdrew coverage from another,
    /// because every round tested the shapes it happened to be thinking about. Hand-written rows cannot
    /// catch that: the cell nobody names is the cell nobody pins. This test enumerates the FULL
    /// {key content} × {value content} × {right delimiter} × {separator spelling} product and asserts that
    /// every cell outside a short, explicitly-commented allow-list is redacted — so any future narrowing of
    /// a character class fails here by construction rather than being discovered by a reviewer.
    /// </para>
    /// <para>
    /// The oracle is deliberately strict: it is not enough that the message contain a redaction marker. NO
    /// FRAGMENT of the value may survive. That distinction is load-bearing — an unanchored branch tried too
    /// early produces "name=&lt;value&gt; Taylor", which carries the marker and the tail at once, and a
    /// marker-presence oracle would score it a pass.
    /// </para>
    /// </remarks>
    [Fact]
    public void Redact_MonotonicityMatrix_EveryCellOutsideTheAllowListStaysRedacted()
    {
        // Column names spanning the key-content lattice. All are LEGAL Delta column names, and
        // DeltaWriteTarget emits the name RAW into add.path (only the value is percent-encoded), so every
        // one of these is reachable on DeltaSharp's own write path -- not a foreign-table hypothetical.
        (string Key, bool Space, bool Quote)[] keys =
        [
            ("email", false, false),
            ("my col", true, false),
            ("o'brien", false, true),
            ("co\"l", false, true),
            ("o'brien y", true, true),
            ("my col\"x", true, true),
        ];

        // WHY THIS LATTICE KEEPS GROWING, stated once so the next person does not have to rediscover it.
        // A reviewer named the pattern exactly: a verification mechanism built from the same mental model
        // as the code it verifies will CONFIRM that model rather than test it. This matrix is the
        // anti-vacuity guard for the residual list, and it twice inherited the blind spot of the thing it
        // guarded -- it had no quote-bearing value while the recognizer was being reasoned about in terms
        // of quotes, then no quote-follower variety while the anchor discriminated on exactly that.
        //
        // The evidence that the corrective works is direct and is not a hypothetical: adding the follower
        // and quoting axes below reported 189 PARTIAL cells against a HEAD believed to have none. Nobody
        // predicted those 189; the lattice found them. So the rule for extending this corpus is to add the
        // axis FIRST and let it report what the recognizer does, rather than deciding what the recognizer
        // should do and picking rows that agree. An axis added to confirm a fix will confirm it.
        //
        // VALUE CONTENT IS ITS OWN AXIS, AND SO IS THE FOLLOWER. Adding value content last round was
        // right and still not enough: every row happened to place a LETTER after the interior quote, which
        // is the single follower class the closing-quote boundary set excludes. `Boys' Club` -- apostrophe
        // then SPACE -- was outside the corpus, and it partial-matched. So the axis is not "does the value
        // contain a quote" but "what comes immediately after it", and the rows below range over the whole
        // boundary set. add.path is FOREIGN, and Spark's escapePathName leaves an apostrophe raw, so none
        // of these depends on what DeltaSharp itself writes.
        // THE VALUE LATTICE IS GENERATED FROM A STRUCTURAL ALPHABET, NOT ENUMERATED BY HAND, AND THE
        // PAYLOAD IS A SENTINEL RATHER THAN PROSE. The previous rows were readable strings
        // (`Aaaa'Bbbb`) scored by splitting on a hard-coded character set. That set enumerated the
        // mechanisms already known when it was written, so a value whose leak used any OTHER character
        // produced a single fragment, and a survival of the tail scored FULL. The oracle was structurally
        // incapable of observing a mechanism it had not already been told about -- which is why each round
        // closed one leak and the next round found another on a character nobody had added yet.
        //
        // So the payload is now two private sentinel runs, Q... and Z..., separated by the structural
        // character under test. Survival is detected by the PRESENCE OF A SENTINEL CHARACTER, with no
        // splitting, no fragment length threshold, and no alphabet to guess: a single surviving character
        // is a leak and is scored as one. The structural character is a loop variable rather than a
        // literal, so adding a boundary character to the recognizer adds rows here automatically.
        const string Head = "QQQQ";
        const string Tail = "ZZZZ";
        (char Ch, string Name)[] structurals =
        [
            ('\0', "none"),
            (' ', "space"),
            ('\'', "apostrophe"),
            ('"', "double quote"),

            // BACKSLASH AND SLASH ARE LEGAL POSIX FILENAME CHARACTERS. They are excluded from every value
            // class in the recognizer yet accepted by every right anchor, so the value run stops at one and
            // that same character satisfies the lookahead. add.path is foreign; nothing prevents it.
            ('\\', "backslash"),
            ('/', "slash"),

            ('%', "percent"),
            ('.', "period"),
            (',', "comma"),
            (':', "colon"),
            (';', "semicolon"),
            (')', "close paren"),
            (']', "close bracket"),
        ];

        List<(string Value, string Follower)> values = [];
        foreach ((char ch, string name) in structurals)
        {
            if (ch == '\0')
            {
                values.Add((Head + Tail, name));
                continue;
            }

            // INTERIOR and VALUE-TERMINAL are different followers: what comes after the structural
            // character is the only thing a boundary lookahead discriminates on, so a lattice carrying
            // only one of them cannot test the discriminator.
            values.Add((Head + ch + Tail, name + ", interior"));
            values.Add((Head + ch, name + ", value-terminal"));

            // A THIRD FOLLOWER, AND THE ONE THE PREVIOUS TWO COULD NOT EXPRESS. The follower axis was
            // added because the recognizer discriminates on what comes AFTER a structural character, but
            // both rows above follow it with ordinary sentinel letters. Neither can ask what happens when
            // the value carries a SECOND Hive separator further along -- and that is the shape in which a
            // structural character stops being a follower question and becomes a LEFT-ANCHOR question,
            // because the run after it now looks exactly like a key. A left anchor that trusts the
            // structural character will start a segment inside the value and echo the synthetic sub-key
            // through ${key}. Both separator spellings, because the recognizer accepts both and a lattice
            // that only writes the literal one has repeatedly been the thing that hid a defect here.
            foreach (string spelling in new[] { "=", "%3D" })
            {
                values.Add((Head + ch + Tail + spelling + "EU", name + ", then " + spelling));
            }
        }

        // QUOTING IS AN AXIS TOO. The recognizer now treats a quote as a delimiter only when something
        // OPENED it, so a corpus that never writes an opening quote cannot tell a closing quote from an
        // interior one -- which is precisely how the follower gap stayed invisible. Each row is
        // {opening quote} x {trailing prose}, and the last two are unquoted paths that merely END in a
        // quote, where no quote is a delimiter at all.
        (string Open, string Close, string Kind)[] delimiters =
        [
            (string.Empty, "/part-0000.parquet", "path"),
            (string.Empty, "\\part-0000.parquet", "path"),
            ("'", "'", "quote"),
            ("\"", "\"", "quote"),
            ("'", "' is denied.", "quote"),
            ("'", "'.", "quote"),
            (string.Empty, string.Empty, "none"),
            (string.Empty, "'", "none"),

            // CLOSURE IS A SUB-DIMENSION OF QUOTING, and leaving it out is the same mistake one level
            // down. The rows above are {opened AND closed} or {never opened}; neither can express a
            // quote that was OPENED and never closed. That shape is not exotic -- Redact truncates its
            // input at RedactScanLimit, so a path long enough to be cut loses its closing quote. Our own
            // ReDoS bound MANUFACTURES this cell. It is where an interior quote of the OTHER kind is the
            // only quote-shaped thing left, and where a literal ['"] boundary set (rather than the
            // \k<oq> backreference) stops mid-value and leaks the tail.
            ("'", string.Empty, "unclosed"),
            ("\"", string.Empty, "unclosed"),
        ];

        string[] separators = ["=", "%3D", "%3d"];

        // THE ROOT IS AN AXIS, AND ITS ABSENCE INVALIDATED AN EQUIVALENCE CLAIM I MADE LAST ROUND. Every
        // cell above this line was rooted at "/root/", so every cell contained a forward slash. The left
        // anchor's segment guard scans back to `(?:^|/)`, and with a slash always present the `^`
        // alternative could never be the thing that made it match -- so a mutant deleting `^` changed
        // nothing measurable and I reported it as an equivalent mutant. It is not: on a Windows-shaped
        // message there is no forward slash at all, `^` is the only scan origin, and without it the guard
        // silently stops guarding exactly the shape the Windows COST argument made load-bearing.
        //
        // THE GENERAL RULE, because this is the second time a claim outlived its corpus: INTRODUCING A NEW
        // CONSIDERATION INVALIDATES EVERY PRIOR EQUIVALENCE MEASUREMENT WHOSE CORPUS DOES NOT RANGE OVER
        // IT. The commit that added the Windows cost argument is the commit that made the slash-free root
        // relevant, and the equivalence was measured in the same commit on a corpus that predated the
        // argument. A 0-RED result carried across a change of argument is measuring the old world. The
        // corpus and the argument have to move together.
        string[] roots =
        [
            "/root/",
            @"C:\\warehouse\\",

            // UNC, because "Windows-shaped" is not one shape. A UNC path opens with two backslashes
            // and has no drive letter, so a guard keyed on either would miss it.
            @"\\\\server\\share\\",

            // ROOTLESS, and this is the row class that discriminates hardest. add.path is RELATIVE by
            // the Delta protocol, so a message can carry a bare `key=value` with nothing before it. The
            // real key then has no left anchor at all -- not because it is whitespace-bearing, but
            // because there is no preceding separator for any branch to start after -- and the
            // backslash-opened sub-key inside the VALUE becomes the only match in the string. Both
            // rooted spellings hide this: they always supply a left anchor for the real key.
            string.Empty,
        ];

        List<string> failures = [];
        int cells = 0;
        int full = 0;
        int declined = 0;
        int declinedR1 = 0;
        int declinedR2 = 0;
        int declinedR4 = 0;
        int sentinelCollisions = 0;
        SortedDictionary<string, int> partialByFollower = [];

        foreach ((string key, bool keySpace, bool keyQuote) in keys)
        {
            foreach ((string value, string follower) in values)
            {
                foreach ((string open, string close, string kind) in delimiters)
                {
                    foreach (string separator in separators)
                    {
                        foreach (string root in roots)
                        {
                            cells++;

                            // THE ORACLE VERIFIES ITS OWN SOUNDNESS. Everything except the value -- the
                            // quoting, the root, the key, the separator, the trailing prose -- must be free of
                            // sentinel characters, or a "survival" could be something this loop itself wrote.
                            // Asserted, because "I checked that Q is not in any key" is exactly the kind of
                            // by-inspection claim this file keeps having to retract.
                            if ((open + root + key + separator + close).AsSpan().IndexOfAny('Q', 'Z') >= 0)
                            {
                                sentinelCollisions++;
                            }

                            string subject = open + root + key + separator + value + close;
                            string actual = LocalFileSystemBackend.HivePartitionValue()
                                .Replace(subject, "${key}=<value>");

                            // THE INVARIANT: the outcome must be exactly FULL or DECLINE. A PARTIAL match -- a
                            // marker emitted over a value the recognizer only half-consumed -- is strictly worse
                            // than declining, because the marker tells the reader the value was handled. This is
                            // asserted for EVERY cell; the allow-list below governs only FULL vs DECLINE.
                            bool marked = actual.Contains("<value>", StringComparison.Ordinal);

                            // THE ORACLE READS THE MESSAGE'S OWN DELIMITERS, NOT THE CONSTRUCTOR'S INTENT.
                            // This loop builds a cell by concatenating {open}{path}{value}{close}, so it
                            // "knows" the whole of `value` is value. The rendered string does not: if the
                            // value itself contains a quote of the opening kind, then by the ordinary reading
                            // of a quoted token the quoted region ENDS there and everything after it is prose.
                            // Scoring against the constructor's intent asserts a fiat the string contradicts,
                            // and that is exactly how the previous corpus row here had to be withdrawn. So the
                            // protected region is derived the same way a reader would derive it -- opening
                            // quote to the FIRST quote of that kind. Deriving it from the recognizer's own
                            // greedy last-match rule instead would make the oracle circular; taking the first
                            // is independent of the regex and never accuses it of over-redacting.
                            // A CLOSING QUOTE IS A QUOTE THAT ENDS SOMETHING. `Aaaa'Bbbb` -- apostrophe then
                            // a LETTER -- is nobody's idea of a closed quotation, and reading it as one put
                            // three cells in the wrong clause. So the closer is the first quote of the opening
                            // kind that is itself followed by whitespace, a path separator, terminal
                            // punctuation, or end of string. That is the ordinary prose reading of quoting and
                            // it is derivable without reference to the regex.
                            // A FORWARD SLASH IS A PATH SEPARATOR ON EVERY PLATFORM, so text after one is a
                            // new path COMPONENT, not value content -- `/root/email=A/B` is a directory `B`
                            // inside a directory `email=A`, and a directory name is not a partition value.
                            // Conforming writers agree: Spark's escapePathName percent-encodes `/` precisely
                            // because it cannot survive in a component. So the protected region ends there.
                            // This is the same rule the quote logic below applies -- read the string's own
                            // delimiters, never the constructor's intent -- and it is derived without
                            // reference to the regex, so it cannot excuse the regex.
                            //
                            // A BACKSLASH IS NOT THAT. On POSIX it is an ordinary filename character, so
                            // `email=A\B` is ONE component and `B` is value content. The recognizer cannot
                            // know which platform produced a foreign add.path, and the two errors are not
                            // symmetric: treating `\` as a separator LEAKS on POSIX, while treating it as
                            // content merely over-redacts a Windows directory name. Fail closed, so `\` does
                            // not truncate the protected region here.
                            string protectedValue = value;
                            int slash = protectedValue.IndexOf('/', StringComparison.Ordinal);
                            if (slash >= 0)
                            {
                                protectedValue = protectedValue[..slash];
                            }

                            bool valueClosesTheQuote = false;
                            if (open.Length > 0)
                            {
                                string scan = protectedValue;
                                for (int i = 0; i < scan.Length; i++)
                                {
                                    if (scan[i] != open[0])
                                    {
                                        continue;
                                    }

                                    bool endsSomething = i == scan.Length - 1
                                        || char.IsWhiteSpace(scan[i + 1])
                                        || "/\\.,:;)]".Contains(scan[i + 1], StringComparison.Ordinal);
                                    if (endsSomething)
                                    {
                                        protectedValue = scan[..i];
                                        valueClosesTheQuote = true;
                                        break;
                                    }
                                }
                            }

                            // CLASSIFY THE RENDERED STRING, NOT THE ROW LABEL. "unclosed" describes how the
                            // cell was BUILT; what governs the recognizer is whether the rendered text has a
                            // right delimiter. A value carrying a quote of the opening kind supplies one --
                            // that quote closes the region whether or not the row appended a closer -- so such
                            // a cell is an ordinary quote-delimited cell. Only when no closer exists anywhere
                            // is the cell genuinely delimiter-free, and then it is R1. Labelling by intent
                            // instead put 15 cells in the wrong clause, all of them cells where the recognizer
                            // did BETTER than the residual list predicted.
                            // A BACKSLASH IS NOT A RIGHT DELIMITER, so a path that continues with one is, for
                            // classification purposes, a path with no delimiter at all. This is the same
                            // delimiter-strength reading applied elsewhere in this oracle: `/` and a matched
                            // closing quote END something on every platform, while `\` only ends something on
                            // Windows and is an ordinary filename character on POSIX. A delimiter that holds
                            // on one platform is not a delimiter; it is a guess.
                            string effectiveKind = kind == "unclosed"
                                ? (valueClosesTheQuote ? "quote" : "none")
                                : kind;
                            if (effectiveKind == "path" && close.StartsWith('\\'))
                            {
                                effectiveKind = "none";
                            }

                            // A VALUE CARRYING ITS OWN SLASH SUPPLIES ITS OWN RIGHT DELIMITER. The protected
                            // region ends at that slash, and the slash is a real one, so the cell has a strong
                            // delimiter regardless of what the row appended afterwards. Reading it any other
                            // way accuses the recognizer of declining on cells where it in fact redacts -- the
                            // same error, in the opposite direction, as the one the quote logic already fixes.
                            if (slash >= 0)
                            {
                                effectiveKind = "path";
                            }

                            // SURVIVAL IS A SENTINEL QUERY, NOT A FRAGMENT SEARCH. Only the sentinels inside
                            // the protected region count: if the value itself closed the quote, the text after
                            // the closer is prose and keeping it is correct. Q and Z occur nowhere else in any
                            // cell, which is asserted below rather than assumed, so a single surviving sentinel
                            // character is a leak no matter which character the leak travelled through.
                            bool survived =
                                (protectedValue.Contains('Q') && actual.Contains('Q'))
                                || (protectedValue.Contains('Z') && actual.Contains('Z'));

                            string outcome = (marked, survived) switch
                            {
                                (true, false) => "FULL",
                                (true, true) => "PARTIAL",
                                _ => "DECLINE",
                            };

                            if (outcome == "FULL")
                            {
                                full++;
                            }
                            else if (outcome == "DECLINE")
                            {
                                declined++;
                            }

                            // THIS SWITCH IS THE RESIDUAL LIST. The comment block above HivePartitionValue
                            // names exactly these two clauses and nothing else, so the documentation cannot
                            // drift from the code: a decline outside the predicate fails here, and a clause
                            // with no decline left to explain fails the anti-vacuity assertions below.
                            //
                            // R1  whitespace-bearing key with NO right delimiter -- which now includes an
                            //     unquoted path that merely ENDS in a quote, since an unopened quote is not a
                            //     delimiter. Not closable: an end-of-string anchor on a whitespace-bearing key
                            //     reopens "open /proc/self/fd failed: errno=13".
                            // R2  key bearing BOTH a quote and whitespace, at a QUOTED path. Irreducible --
                            //     `o'brien y=v'` and `file": errno=13"` are the same string shape. Tracked as a
                            //     known monotonicity regression by #714.
                            //
                            // NOT here, in either direction: value content. R3 -- a quote INSIDE the value --
                            // was a documented residual and is CLOSED; the follower axis above exists to keep
                            // it closed, and every one of those cells is asserted FULL rather than excused.
                            // An unclosed quote with no closer anywhere collapses into R1 rather than earning
                            // a residual of its own: it is not a right delimiter, which is the same reason an
                            // unquoted path merely ENDING in a quote is R1. Same cause, same clause.
                            // R4: a rootless path whose string carries NO SEPARATOR EVIDENCE AT ALL. The
                            // residual's own words are "a bare key=value with no slash at all ...
                            // indistinguishable from `errno=13` by any means available in the string", and
                            // the operative word is INDISTINGUISHABLE. This predicate used to read "no
                            // FORWARD slash", which is wider than the residual it claims to model, and that
                            // gap is what hid sibling-drift #5 from five reviewers and three corpora: a
                            // rootless `k=v\\part.parquet` was bucketed R4 and excused, when a backslash is
                            // very much a means available in the string -- `errno=13` has none, `DescribePath`
                            // reaches FULL on it, and `Redact` itself accepted it as evidence when ROOTED.
                            // Read the residual, not the corpus: evidence is a separator in either
                            // alphabet, ANYWHERE. The "with something after it" qualifier that stood here
                            // was itself an enumeration and excused a third opt-out; see HasSeparatorEvidence.
                            bool rootless = root.Length == 0;
                            string evidenceScan = value + close;

                            // R1 IS NARROWER THAN IT WAS. A whitespace-bearing key with no right delimiter
                            // declines because an end-of-string value run on such a key would reopen
                            // `open /proc/self/fd failed: errno=13`. That argument is about EVIDENCE, and the
                            // rootless-backslash branch supplies some: its end-of-string run is gated on a
                            // separator, which `errno=13` does not have at all. So the subset
                            // of R1 that carries backslash evidence is now redacted rather than excused, and
                            // the model has to say so or the fix reads as a regression. R2 narrows for the
                            // same reason and by the same clause: `o'brien y=v'` and `file": errno=13"` are
                            // the same shape only while neither carries a separator, and a backslash breaks
                            // that tie in exactly the direction R2's own rationale says it cannot be broken
                            // WITHOUT one.
                            // ONE NAME, BECAUSE IT IS ONE PREDICATE. Two locals stood here --
                            // "slash" and "backslash" evidence -- holding the identical expression, left
                            // over from the alphabet split the fold retired. Two names for one concept is
                            // the precondition every subject-drift in this file has shared: the next author
                            // narrows one and the other keeps the old meaning silently. So the R4 clause
                            // below and the R1/R2 clauses above now read the same local, and a narrowing has
                            // to be written once, where both can see it.
                            bool evidence = HasSeparatorEvidence(evidenceScan);

                            // AND THE SCAN'S SUBJECT IS NARROWER THAN THE SENTENCE ABOVE IT. The residual
                            // says "no separator evidence AT ALL" and "ANYWHERE"; this scans `value + close`
                            // and omits the opener, the key and the separator spelling. On today's axes the
                            // two subjects cannot disagree -- none of those three carries a separator -- but
                            // that is a property of the axes, not of the claim, and this file's recurring
                            // defect is exactly a sentence whose subject the corpus can never contradict.
                            // So the coincidence is EXECUTED: an axis that ever puts a separator in front of
                            // the value fails here, naming the cell, rather than quietly re-scoping R4.
                            if (rootless && HasSeparatorEvidence(open + key + separator))
                            {
                                failures.Add(
                                    "the R4 scan omits separator evidence in front of the value: " + subject);
                            }

                            bool expectFull = effectiveKind switch
                            {
                                "none" => !keySpace || evidence,
                                "quote" => !(keySpace && keyQuote) || evidence,
                                _ => true,
                            };

                            if (rootless && !evidence)
                            {
                                expectFull = false;
                            }

                            if (outcome == "DECLINE")
                            {
                                if (rootless && !evidence)
                                {
                                    declinedR4++;
                                }
                                else if (effectiveKind == "none" && keySpace)
                                {
                                    declinedR1++;
                                }
                                else if (effectiveKind == "quote" && keySpace && keyQuote)
                                {
                                    declinedR2++;
                                }
                            }

                            if (outcome == "PARTIAL")
                            {
                                partialByFollower[follower] =
                                    partialByFollower.GetValueOrDefault(follower) + 1;
                            }

                            if (outcome == "PARTIAL" || outcome != (expectFull ? "FULL" : "DECLINE"))
                            {
                                failures.Add(string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"key='{key}' value='{value}' follower={follower} "
                                    + $"open='{open}' close='{close}' sep='{separator}' "
                                    + $"expected={(expectFull ? "FULL" : "DECLINE")} got={outcome} "
                                    + $"rendered='{actual}'"));
                            }
                        }
                    }
                }
            }
        }

        // THE ORACLE IS CHECKED BEFORE THE THING IT MEASURES. WRONG-not-UNMEASURED: a sentinel appearing
        // outside the value would make every survival reading unreliable -- not incomplete, unsound -- so
        // this fails first and separately.
        Assert.True(
            sentinelCollisions == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{sentinelCollisions} cells put a sentinel character outside the value; "
                + $"survival readings would be unsound."));

        // Per-cell detail first: a census mismatch is far less useful than the row that caused it. The
        // census rides along in the message because the count is the first thing worth knowing when a
        // corpus change moves cells between buckets.
        Assert.True(
            failures.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{failures.Count} of {cells} monotonicity cells regressed "
                + $"[FULL={full} DECLINE={declined} R1={declinedR1} R2={declinedR2} R4={declinedR4}] "
                + $"partials by follower: "
                + $"{string.Join(", ", partialByFollower.Select(kv => kv.Key + "=" + kv.Value))}:"
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(6))}"));

        // Census pinned so the corpus cannot silently shrink and so a change in the FULL/DECLINE split is
        // visible as a NUMBER, not only as a pass/fail. full + declined == cells is the invariant restated
        // arithmetically: there is no third bucket, because PARTIAL is not an outcome this recognizer may
        // produce. At 930072b this same sweep produced 82 partial matches.
        //
        // Closing the terminal-backslash opt-out moved 135 cells from DECLINE to FULL (13,401 -> 13,266),
        // out of R1 (4,455 -> 4,374) and R4 (5,958 -> 5,904). No cell moved the other way, which is the
        // shape a NARROWING of the residual set has and a change of behaviour does not.
        // ATTRIBUTION BEFORE THE PINNED CENSUS. Every decline must be attributable to R1, R2 or R4;
        // a corpus that moves makes the pinned totals fire, and written the other way round that count
        // masks an undocumented residual -- the same ordering defect three other guards here have had.
        Assert.Equal(declined, declinedR1 + declinedR2 + declinedR4);
        Assert.Equal(cells, full + declined);

        Assert.Equal(35280, cells);
        Assert.Equal(22014, full);
        Assert.Equal(13266, declined);

        // ANTI-VACUITY ON THE RESIDUAL LIST ITSELF. Every decline must be attributable to R1 or R2, and
        // each clause must still have work to do. The first assertion catches a residual the comment does
        // not document; the second and third catch a residual the comment documents but the code no longer
        // produces -- which is how R3 came to be described in source after it had been closed. A residual
        // list that is only checked in one direction drifts in the other.
        Assert.True(declinedR1 > 0, "R1 is documented but no cell exercises it.");
        Assert.True(declinedR2 > 0, "R2 is documented but no cell exercises it.");
        Assert.True(declinedR4 > 0, "R4 is documented but no cell exercises it.");
    }

    /// <summary>
    /// THE equivalence assertion: for a shared corpus, <c>Redact</c>'s recognizer and
    /// <see cref="DiagnosticText.HiveSeparatorIndex"/> must resolve the SAME SUBSTRING as the key — not
    /// merely both avoid leaking.
    /// </summary>
    /// <remarks>
    /// The shared separator constant made the two halves accept the same ALPHABET, and five reviewers
    /// verified exactly that and were right. What it did not make them is EQUIVALENT, because they used
    /// different SEARCH STRATEGIES: <c>DescribePath</c> scans for the FIRST separator, while a greedy regex
    /// key reaches the LAST one it can. On <c>k%3DSECRET=value</c> the greedy form took
    /// <c>k%3DSECRET</c> as the key and redacted only <c>value</c>, echoing <c>SECRET</c> — a partition
    /// value — while <c>DescribePath</c> correctly reported the key as <c>k</c>. A shared alphabet is not a
    /// shared decision procedure, and only this assertion distinguishes the two.
    /// </remarks>
    [Theory]
    [InlineData("email=v", "email")]
    // The gate's HIGH #1: several separators of DIFFERENT spellings in one segment. The key is whatever
    // precedes the FIRST of them, so everything after it — including a second key-looking token — is value.
    [InlineData("k%3DSECRET=value", "k")]
    [InlineData("k=SECRET%3Dvalue", "k")]
    [InlineData("a=1=2=3", "a")]
    [InlineData("a%3D1%3D2", "a")]
    // The gate's HIGH #2: an unencoded space on the VALUE side.
    [InlineData("k y=val ue", "k y")]
    // Keys the strict class cannot span.
    [InlineData("my col=v", "my col")]
    [InlineData("my\tcol=v", "my\tcol")]
    [InlineData("my\u2028col=v", "my\u2028col")]
    [InlineData("o'brien=v", "o'brien")]
    [InlineData("o\"brien=v", "o\"brien")]
    [InlineData("o'brien%3Dv", "o'brien")]
    // Empty and '='-leading keys.
    [InlineData("=v", "")]
    [InlineData("==v", "")]
    [InlineData("%3Dv", "")]
    public void Redact_AndDescribePath_ResolveTheSameKey(string segment, string expectedKey)
    {
        int index = DiagnosticText.HiveSeparatorIndex(segment, out _);
        Assert.True(index >= 0, "the corpus must consist of recognized Hive segments");
        string describePathKey = segment[..index];

        Match match = LocalFileSystemBackend.HivePartitionValue().Match("/" + segment + "/");
        Assert.True(match.Success, $"the recognizer declined '{segment}'");
        string redactKey = match.Groups["key"].Value;

        Assert.Equal(expectedKey, describePathKey);
        Assert.Equal(expectedKey, redactKey);
    }


    /// <summary>
    /// The equivalence assertion extended from WHICH SUBSTRING IS THE KEY to WHAT ENDS A SEGMENT — ranged
    /// over the character alphabet rather than over a hand-picked corpus.
    /// </summary>
    /// <remarks>
    /// These two halves have now drifted three times, on a different axis each time: the separator ALPHABET
    /// (fixed by sharing HiveSeparatorPattern), the search STRATEGY (greedy versus first-index, fixed by
    /// lazy keys and pinned by Redact_AndDescribePath_ResolveTheSameKey), and the separator alphabet AGAIN
    /// on `\`, where Redact stopped treating a backslash as a delimiter and DescribePath kept splitting on
    /// it — echoing the partition value as the file name through the ordinary not-found door.
    ///
    /// A shared CONSTANT made them share an alphabet once and they diverged twice anyway, so what is
    /// asserted here is a shared DECISION: for every character, both halves must reach the same verdict on
    /// whether a value survives past it. The test does not encode which characters those are, so a change
    /// to either half's separator handling fails here without anyone remembering to add a row.
    /// </remarks>
    [Fact]
    public void Redact_AndDescribePath_AgreeOnWhichCharactersEndASegment()
    {
        List<string> disagreements = [];
        List<char> survivesInBoth = [];

        foreach (char c in Enumerable.Range(1, 126).Select(i => (char)i))
        {
            string value = "AAA" + c + "BBB";

            string described = DiagnosticText.DescribePath("tbl/key=" + value);
            bool describeKeeps = described.Contains("BBB", StringComparison.Ordinal);

            string redacted = LocalFileSystemBackend.HivePartitionValue()
                .Replace("/tbl/key=" + value + "/part-0.parquet", "${key}=<value>");
            bool redactKeeps = redacted.Contains("BBB", StringComparison.Ordinal);

            if (describeKeeps != redactKeeps)
            {
                disagreements.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"U+{(int)c:X4}: DescribePath keeps={describeKeeps} Redact keeps={redactKeeps} "
                    + $"described='{described}' redacted='{redacted}'"));
            }

            if (describeKeeps && redactKeeps)
            {
                survivesInBoth.Add(c);
            }
        }

        Assert.True(
            disagreements.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{disagreements.Count} characters end a segment for one half and not the other:"
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, disagreements.Take(10))}"));

        // AGREEMENT ALONE IS NOT ENOUGH — two halves that both leak agree perfectly. `/` is the only
        // character after which a value may survive, because `/` genuinely separates components on every
        // platform and what follows one is a directory name rather than value content. Anything else in
        // this list would be a leak both halves share.
        Assert.Equal(new[] { '/' }, survivesInBoth);
    }

    // THE COMPANION HALF OF THE EQUIVALENCE, AND THE ONE THAT WAS MISSING. The test above ranges over
    // which characters END a segment. That is only half of "the two recognizers share a decision
    // procedure": a delimiter alphabet is used at BOTH ends, and a character that must not be trusted to
    // close a value must also not be trusted to OPEN a key. Applying the platform rule to the right anchor
    // and the value classes but not to the left anchor is exactly how the backslash defect survived the
    // commit whose subject line was the backslash rule -- a rule applied where the defect was measured
    // rather than everywhere the property has to hold.
    //
    // The probe is the shape a left anchor gets wrong: a value that carries a SECOND separator further
    // along. If a half trusts `c` to start a segment, the run after it looks like a key, and the half
    // echoes SUB as a column name -- disclosing value content under a label asserting it was removed.
    [Fact]
    public void Redact_AndDescribePath_AgreeOnWhichCharactersMayStartAKey()
    {
        List<string> disagreements = [];
        List<char> startsAKeyInBoth = [];

        foreach (char c in Enumerable.Range(1, 126).Select(i => (char)i))
        {
            string value = "AAA" + c + "SUB=BBB";

            string described = DiagnosticText.DescribePath("tbl/key=" + value);
            bool describeStarts = described.Contains("SUB", StringComparison.Ordinal);

            string redacted = LocalFileSystemBackend.HivePartitionValue()
                .Replace("/tbl/key=" + value + "/part-0.parquet", "${key}=<value>");
            bool redactStarts = redacted.Contains("SUB", StringComparison.Ordinal);

            if (describeStarts != redactStarts)
            {
                disagreements.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"U+{(int)c:X4}: DescribePath starts={describeStarts} Redact starts={redactStarts} "
                    + $"described='{described}' redacted='{redacted}'"));
            }

            if (describeStarts && redactStarts)
            {
                startsAKeyInBoth.Add(c);
            }
        }

        Assert.True(
            disagreements.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{disagreements.Count} characters start a key for one half and not the other:"
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, disagreements.Take(10))}"));

        // Same reasoning as the sibling test: agreement alone is satisfied by two halves that both leak.
        // `/` is the only character that may introduce a real key, because it is the only one that is a
        // separator on every platform rather than a filename character on one of them.
        Assert.Equal(new[] { '/' }, startsAKeyInBoth);
    }

    // The left-anchor mechanism, named so a reader meets the shape rather than only a cell count. A
    // backslash inside a value starts a synthetic segment; the sub-key after it is strict, so it satisfies
    // the branch that the REAL key (whitespace-bearing) could not, and the recognizer redacts only the tail
    // while echoing the surviving head under a marker. Worse than a decline: the marker asserts a removal
    // that did not happen, and the residual block classified this exact class as DECLINE while it was
    // PARTIAL in fact.
    [Theory]
    [InlineData(@"Could not delete file /tmp/tbl/owner name=Legal\alice.taylor%40example.com=EU")]
    [InlineData(@"Could not delete file /tmp/tbl/owner name=Legal\alice.taylor%40example.com%3DEU")]
    [InlineData(@"'/tmp/tbl/o'brien col=x\alice.taylor%40example.com=EU'")]
    [InlineData(@"Could not delete file /tmp/t/my col=A\B\alice.taylor%40example.com=EU")]
    public void Redact_BackslashThenASecondSeparator_DoesNotInventAKeyInsideTheValue(string detail)
    {
        string redacted = LocalFileSystemBackend.HivePartitionValue()
            .Replace(detail, "${key}=<value>");

        // FULL or DECLINE, never PARTIAL: if the marker is present the value must be gone.
        if (redacted.Contains("<value>", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("alice.taylor", redacted, StringComparison.Ordinal);
            Assert.DoesNotContain("example.com", redacted, StringComparison.Ordinal);
        }
    }

    // THE COST SIDE, PINNED. The uniform-looking remedy -- drop the backslash from the left anchors as it
    // was dropped from the right ones -- is wrong, and this test is what says so. A Windows-shaped message
    // contains no forward slash to anchor on, so `/`-only left anchors decline it outright and surrender
    // every partition value on a Windows or PVC host. Anyone who "completes" the backslash rule by
    // symmetry will turn these red, which is the intended warning.
    [Theory]
    [InlineData(@"Could not delete file C:\warehouse\tbl\email=alice%40example.com\part-1.parquet")]
    [InlineData(@"Could not find a part of the path '\tbl\name=alice%40example.com'")]
    public void Redact_WindowsShapedPathWithNoForwardSlash_IsStillRedacted(string detail)
    {
        string redacted = LocalFileSystemBackend.HivePartitionValue()
            .Replace(detail, "${key}=<value>");

        Assert.Contains("<value>", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", redacted, StringComparison.Ordinal);
    }

    // THE OVER-DECLINE OF THE SEGMENT GUARD, RESTATED AFTER IT STOPPED BEING A DECLINE. The guard asks
    // whether a separator appeared earlier in the current FORWARD-SLASH segment; prose has no slashes, so an
    // equals sign in the prose sits in the same segment as a following Windows path and suppresses the
    // backslash anchor at the real key. That is still true -- `name` is not the key in the rendered output.
    // What changed is that branch 6 now matches at the PROSE key instead of declining, so the cell is an
    // over-redaction rather than an echo. It is pinned with its exact rendered text in
    // Redact_DelimiterAdjacentProse_IsAKnownAcceptedOverRedaction; this comment stays because the guard's
    // scope is still the thing that decides which key is harvested, and a future change to it will move
    // that row rather than this one.
    // The cell that falsified last round's equivalence claim. A Windows-shaped message contains no forward
    // slash, so the segment guard's `^` alternative is its only scan origin; delete `^` and the guard stops
    // seeing the earlier separator, the backslash opens a synthetic segment inside the value, and the
    // sub-key is echoed under a marker. The whole matrix was rooted at "/root/" when I called that mutation
    // equivalent, so no cell could distinguish the two -- this one can, and it is asserted here by name as
    // well as by census so that deleting `^` fails with a readable reason rather than a count.
    [Theory]
    [InlineData(@"C:\\warehouse\\my col=Legal\\alice.taylor%40example.com=EU")]
    [InlineData(@"C:\\warehouse\\my col=Legal\\alice.taylor%40example.com=EU\\part-0.parquet")]
    [InlineData(@"'C:\\warehouse\\o'brien col=Legal\\alice.taylor%40example.com=EU'")]
    [InlineData(@"\\\\server\\share\\my col=Legal\\alice.taylor%40example.com=EU")]
    public void Redact_WindowsRootWithTwoSeparators_DoesNotInventAKeyInsideTheValue(string detail)
    {
        string redacted = LocalFileSystemBackend.HivePartitionValue()
            .Replace(detail, "${key}=<value>");

        if (redacted.Contains("<value>", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("alice.taylor", redacted, StringComparison.Ordinal);
            Assert.DoesNotContain("example.com", redacted, StringComparison.Ordinal);
        }
    }

    // MECHANISM 7, found by adding a ROOTLESS root to the census and live at the previous HEAD. add.path is
    // RELATIVE by the Delta protocol, so a message can carry a partition path with nothing before the first
    // key. The first segment then has no left delimiter and could never match, while the SECOND segment
    // still did -- printing a marker over an unredacted head, which is the worst reading of all: the reader
    // is told a value was removed, and one was, just not that one.
    //
    //   email=alice%40example.com/region=<value>/part-0.parquet
    //
    // Every previous cell of the census was rooted, and a root supplies the left delimiter the first key was
    // missing, so 35,280 cells could not express a shape the ordinary not-found door produces unprompted.
    [Theory]
    [InlineData("email=" + EncodedValue + "/region=EU/part-0.parquet")]
    [InlineData("email=" + EncodedValue + "/part-0.parquet")]
    [InlineData("Could not find file 'email=" + EncodedValue + "/region=EU/part-0.parquet'")]
    [InlineData("Could not find file \"email=" + EncodedValue + "/part-0.parquet\"")]
    public void Redact_RootlessRelativePath_RedactsTheFirstSegmentToo(string detail)
    {
        string redacted = LocalFileSystemBackend.HivePartitionValue()
            .Replace(detail, "${key}=<value>");

        Assert.Contains("<value>", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(EncodedValue, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(redacted), StringComparison.OrdinalIgnoreCase);
    }

    // The other side of mechanism 7's fix, and the reason branch 4 did NOT get the weaker anchor. A bare
    // key=value with no slash anywhere is what an operator diagnostic looks like, and nothing in the string
    // distinguishes it from a rootless partition segment. The recognizer declines -- residual R4 -- rather
    // than redacting a diagnostic. If someone later gives branch 4 the same anchor branch 1 has, these turn
    // red before the prose corpus does.
    [Theory]
    [InlineData("path /var/log/app: retries=5 attempts")]
    [InlineData("open /proc/self/fd failed: errno=13")]
    [InlineData("check /var/log then set retries=5")]
    public void Redact_BareKeyValueWithNoPathEvidence_IsLeftAlone(string detail)
    {
        Assert.Equal(
            detail,
            LocalFileSystemBackend.HivePartitionValue().Replace(detail, "${key}=<value>"));
    }

    // R5, CHARACTERIZED RATHER THAN CLAIMED CLOSED. A partition value containing an apostrophe inside a
    // single-quoted framework message is byte-identical to a message truncated at that apostrophe. The
    // recognizer implements the reader's parse -- opening quote to the first quote of that kind -- so the
    // tail after the interior quote survives, under a marker. That is a PARTIAL, it is irreducible, and it
    // is asserted here so the residual is a fact about the code rather than a sentence about it. If someone
    // later closes it, this test fails and the residual entry must be revisited; if someone widens it, the
    // second assertion fails.
    [Theory]
    [InlineData("stat failed on '/tbl/name=Boys' Club Holdings", "Club Holdings")]
    [InlineData("Could not open '/tbl/owner=Dells' Farm Road", "Farm Road")]
    public void Redact_InteriorQuoteInAValue_IsAnIrreduciblePartialResidual(
        string detail, string survivingTail)
    {
        string redacted = LocalFileSystemBackend.HivePartitionValue()
            .Replace(detail, "${key}=<value>");

        // The marker is emitted...
        Assert.Contains("<value>", redacted, StringComparison.Ordinal);

        // ...and under the alternate reading, part of the value survives it. Recorded, not excused.
        Assert.Contains(survivingTail, redacted, StringComparison.Ordinal);

        // The head of the value is gone either way, so this is bounded: only the run after the interior
        // quote survives, never the whole value.
        Assert.DoesNotContain("Boys", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Dells", redacted, StringComparison.Ordinal);

        // AND THE SCOPE OF R5 IS NARROWER THAN IT LOOKS, which is worth pinning too: when the message
        // CLOSES the quote it opened, the backreference finds a closing quote with a real boundary after
        // it, the value class spans the interior quote, and the redaction is FULL. So R5 is confined to
        // genuinely unbalanced shapes -- not to every value containing an apostrophe.
        Assert.Equal(
            "Access to the path '/tbl/owner=<value>' is denied.",
            LocalFileSystemBackend.HivePartitionValue()
                .Replace("Access to the path '/tbl/owner=Dells' Farm Road' is denied.", "${key}=<value>"));
    }

    [Theory]
    // HIGH #1 — a second, differently-spelled separator inside the segment. The greedy key consumed past
    // the first one, so everything between them was echoed as if it were part of the column NAME.
    [InlineData("k%3DSECRET=")]
    [InlineData("k=SECRET%3D")]
    // HIGH #2 — an unencoded space in the VALUE. The value class stopped at the space, so the right anchor
    // landed on the space instead of the separator and the match was declined ENTIRELY. Distinct from the
    // disclosed residual: here a right delimiter exists, and the mechanism is the value class, not the key.
    [InlineData("k y=lead ")]
    [InlineData("my col=lead ")]
    public async Task Redact_SecondSeparatorOrUnencodedValueSpace_StillStripsTheValue(string prefix)
    {
        string absolute = Path.Combine(_root, prefix + DecodedValue, "part-x.parquet");

        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException($"Could not delete file '{absolute}'.")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            Assert.DoesNotContain("SECRET", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(DecodedValue, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("=<value>", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Fact]
    public void DescribePath_SeparatorsOnly_IsDistinguishableFromAMissingPath()
    {
        // A path that is all separators has no nameable segment, but the caller DID supply one. Rendering it
        // as "(null)" made a real request indistinguishable from a missing one -- a diagnosability loss with
        // no privacy benefit, since neither render carries data-derived content.
        Assert.Equal("(no segments)", DiagnosticText.DescribePath("///////"));
        Assert.Equal("(no segments)", DiagnosticText.DescribePath(@"\\\\"));
        Assert.Equal("(null)", DiagnosticText.DescribePath(null));
        Assert.Equal("(null)", DiagnosticText.DescribePath(string.Empty));
    }

    [Fact]
    public async Task Redact_UnencodedWhitespaceInAValue_IsFullyStrippedWhenDelimited()
    {
        // This test previously PINNED A LEAK as an accepted bound: the value run stopped at whitespace, so
        // "name=Alice Taylor" rendered as "name=<value> Taylor" -- a redaction marker sitting next to the
        // half of the value that survived it. The delimiter-strength ladder closes it. Because add.path is
        // FOREIGN, a value may arrive unencoded no matter what DeltaSharp writes, so this was never really
        // off the reachable path, and the old rationale ("DeltaSharp percent-encodes") did not cover it.
        //
        // The residual is now strictly narrower and is R1: whitespace in a value under-redacts only where
        // NO right delimiter follows at all, pinned separately in the monotonicity matrix.
        await _backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
        LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
            ? new IOException($"Could not delete file '{Path.Combine(_root, "name=Alice Taylor", "part-x.parquet")}'.")
            : null;
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                () => _backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            Assert.Contains("name=<value>", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Taylor", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Alice", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            LocalFileSystemBackend.IoFaultHook = null;
        }
    }

    [Theory]
    [InlineData("email=" + EncodedValue + "/region=EU/../../../../etc/passwd")]
    [InlineData("email=" + EncodedValue + "/../../../../etc/passwd")]
    public async Task ConfinementRejection_KeepsAttackShape_ButStillDropsPartitionValues(string escape)
    {
        // Round-5 (Architect, HIGH): a confinement rejection's entire diagnostic content is the SHAPE of the
        // rejected path, and dropping every non-Hive directory made "../../../../etc/passwd", "/etc/passwd"
        // and an innocent in-root "passwd" render identically. The seat proposed a second, shape-preserving
        // renderer for "control" paths. This test is the counter-example to that split: a poisoned add.path
        // reaches the confinement guard carrying a partition VALUE, so a "control paths may echo raw" branch
        // would reopen the exact disclosure the helper exists to close. Keeping the structural FACTS --
        // counts and fixed literals, which cannot carry table data -- restores the diagnosis without it.
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => _backend.OpenReadAsync(escape, CancellationToken.None).AsTask());

        Assert.Equal(StorageErrorKind.PathNotConfined, error.Kind);

        // The attack is legible: traversal count, and the leaf that was aimed at.
        Assert.Contains("parent traversals", error.Message, StringComparison.Ordinal);
        Assert.Contains("'passwd'", error.Message, StringComparison.Ordinal);

        // ...and the partition value is still gone, in both encodings.
        Assert.DoesNotContain(EncodedValue, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(error.Message), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // The three shapes an operator must be able to tell apart on a confinement rejection.
    [InlineData("../../../../etc/passwd", "'passwd' (4 parent traversals; 1 directory omitted)")]
    [InlineData("/etc/passwd", "'passwd' (rooted; 1 directory omitted)")]
    [InlineData("passwd", "'passwd'")]
    // A control directory is a DeltaSharp-generated fixed literal, so naming it discloses nothing and tells
    // the operator a LOG object was involved rather than a data file.
    [InlineData("_delta_log/00000000000000000001.json", "'00000000000000000001.json' (under _delta_log)")]
    [InlineData("_change_data/cdc-abc.parquet", "'cdc-abc.parquet' (under _change_data)")]
    public void DescribePath_KeepsPathShape_WithoutNamingAnyDirectory(string path, string expected) =>
        Assert.Equal(expected, DiagnosticText.DescribePath(path));

    [Fact]
    public async Task StagedWriteDurabilityFailure_KeepsRawPathOnTypedProperty()
    {
        // Round-5 (Architect, LOW): four of the six staged-write throws passed the raw path to the typed
        // property and two did not, so on those two the operator got NEITHER the value-bearing path in the
        // message (correctly) NOR the raw path on the property (incorrectly) -- the "raw retained for a
        // caller entitled to it" half of the ruling silently did not hold for the same stream's own messages.
        if (OperatingSystem.IsWindows())
        {
            return; // Sync() short-circuits to true on Windows; there is no durability arm to drive.
        }

        Stream stream = await _backend.OpenWriteAsync(PartitionedPath, CancellationToken.None);
        await using var staged = stream;
        await stream.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);

        DirectoryFsync.FsyncHook = _ => 5; // EIO at the directory fsync: publication happens in CompleteAsync.
        try
        {
            DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
                async () => await ((ICompletableWriteStream)staged).CompleteAsync(CancellationToken.None));

            Assert.Equal(StorageErrorKind.RetryUnsafeAmbiguous, error.Kind);
            Assert.Contains("could not be made durable", error.Message, StringComparison.Ordinal);

            // The message still drops the values...
            AssertKeepsContextDropsValues(error.Message);

            // ...and the raw path is on the typed property, which is the half that was missing.
            Assert.Equal(PartitionedPath, error.Path);
        }
        finally
        {
            DirectoryFsync.FsyncHook = null;
        }
    }

    [Fact]
    public void DescribePath_ParsesAPathTheWriterActuallyProduced()
    {
        // Round-5 (Architect, LOW): the Hive `key=value` truth lives in DeltaWriteTarget and DescribePath
        // re-encodes it. If the layout ever changed, the two would drift silently and the PRIVACY guarantee
        // would break with nothing failing. So assert against a path the WRITER produced, not a literal.
        string produced = DeltaWriteTarget.DataFilePath(
            ["email", "region"],
            ImmutableSortedDictionary<string, string?>.Empty
                .Add("email", DecodedValue)
                .Add("region", "EU"),
            "TOKEN");

        // Sanity: the writer really did percent-encode a PII-shaped value into the path, so this can fail.
        Assert.Contains(EncodedValue, produced, StringComparison.OrdinalIgnoreCase);

        string rendered = DiagnosticText.DescribePath(produced);

        Assert.Equal("'part-TOKEN.parquet' (partitioned by: email, region)", rendered);
        Assert.DoesNotContain(EncodedValue, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(rendered), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EU", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribePath_DeepPath_DoesNotMaterializeEverySegment()
    {
        // The OUTPUT was already bounded, but Split materialized every segment first: a 100,000-deep path
        // allocated ~9.5 MB to render ~93 chars (~24x amplification). The span scan collects at most
        // MaxEchoedListItems keys and only COUNTS the rest.
        var path = new System.Text.StringBuilder();
        for (int i = 0; i < 100_000; i++)
        {
            path.Append(CultureInfo.InvariantCulture, $"c{i}=v{i}/");
        }

        path.Append("part-0.parquet");

        // Materialize the INPUT before the measurement window: we are measuring what DescribePath allocates,
        // not what it costs to build a 1.2 MB test string.
        string deep = path.ToString();

        long before = GC.GetAllocatedBytesForCurrentThread();
        string rendered = DiagnosticText.DescribePath(deep);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("… (+99984 more)", rendered, StringComparison.Ordinal);

        // Generous: the point is that allocation is a function of what is RENDERED, not of path depth. The
        // Split implementation allocated ~9.5 MB here.
        Assert.True(allocated < 64 * 1024, $"allocated {allocated} bytes to render {rendered.Length} chars");
    }

    [Fact]
    public void DescribePath_ManyPartitionColumns_IsCountBounded()
    {
        string path = string.Join('/', Enumerable.Range(0, 500).Select(
            i => string.Create(CultureInfo.InvariantCulture, $"c{i}=v{i}"))) + "/part-0.parquet";

        string rendered = DiagnosticText.DescribePath(path);

        Assert.Contains("… (+484 more)", rendered, StringComparison.Ordinal);
        Assert.True(rendered.Length < 512, $"unbounded render: {rendered.Length} chars");
    }

    [Fact]
    public void DescribePath_OversizedFileName_IsLengthCapped()
    {
        string rendered = DiagnosticText.DescribePath(new string('f', 100_000) + ".parquet");

        Assert.True(rendered.Length <= DiagnosticText.DefaultMaxLength + 8, $"uncapped: {rendered.Length}");
    }

    [Fact]
    public async Task ConfinementWalkFailure_PartitionedPath_DropsValues_ButKeepsRawOnTypedProperty()
    {
        // Drive a REAL IO fault on a Hive-partitioned relative path: the surfaced Transient message must carry
        // the partition COLUMN names only, while ex.Path keeps the exact key the table owner needs to act.
        // An unreadable-mode file exists (so the NotFound gate passes) but open() fails with EACCES, which is
        // the framework IOException SurfaceFailure wraps.
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX mode bits drive this fault; the Windows arm is covered by the NotFound test.
        }

        await _backend.PutIfAbsentAsync(PartitionedPath, new byte[] { 1, 2, 3 }, CancellationToken.None);
        string full = Path.Combine(_root, PartitionedPath.Replace('/', Path.DirectorySeparatorChar));
        File.SetUnixFileMode(full, UnixFileMode.None);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => _backend.OpenReadAsync(PartitionedPath, CancellationToken.None).AsTask());

        // EACCES on the confined openat walk surfaces through MapWalkError's Transient echo.
        Assert.Contains("Resolving", error.Message, StringComparison.Ordinal);
        AssertKeepsContextDropsValues(error.Message);
        Assert.Equal(PartitionedPath, error.Path); // raw, on the typed property

        File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task NotFound_PartitionedPath_DropsValues_ButKeepsRawOnTypedProperty()
    {
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => _backend.OpenReadAsync(PartitionedPath, CancellationToken.None).AsTask());

        Assert.Equal(StorageErrorKind.NotFound, error.Kind);
        AssertKeepsContextDropsValues(error.Message);
        Assert.Equal(PartitionedPath, error.Path);
    }

    [Fact]
    public async Task SurfaceFailure_PartitionedPath_DropsValues_ButKeepsRawOnTypedProperty()
    {
        // SurfaceFailure is the generic framework-IO wrapper on every backend operation. A read-only
        // partition directory makes staging the conditional-create temp fail with a genuine
        // UnauthorizedAccessException, which is exactly the fault it exists to surface.
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX mode bits drive this fault.
        }

        string dir = Path.Combine(_root, "email=" + EncodedValue, "region=EU");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => _backend.PutIfAbsentAsync(PartitionedPath, new byte[] { 1 }, CancellationToken.None).AsTask());

        AssertKeepsContextDropsValues(error.Message);
        Assert.Equal(PartitionedPath, error.Path);

        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task StagedPublish_AlreadyExists_PartitionedPath_DropsValues()
    {
        // The staged-write stream renders its display path once at construction; the publish-collision message
        // is the reachable door to it.
        await _backend.PutIfAbsentAsync(PartitionedPath, new byte[] { 1 }, CancellationToken.None);

        await using Stream staged = await _backend.OpenWriteAsync(PartitionedPath, CancellationToken.None);
        await staged.WriteAsync(new byte[] { 2 }, CancellationToken.None);

        var completable = (ICompletableWriteStream)staged;
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => completable.CompleteAsync(CancellationToken.None).AsTask());

        Assert.Equal(StorageErrorKind.AlreadyExists, error.Kind);
        AssertKeepsContextDropsValues(error.Message);
        Assert.Equal(PartitionedPath, error.Path);
    }

    // A logger with Debug DISABLED — the production default — that records whether anything was ever asked
    // of it. If the candidate line's argument were evaluated unconditionally, DescribePath would run per
    // candidate for output nobody sees.
    private sealed class LevelGatedLogger(LogLevel minimum) : ILogger<DeltaVacuum>
    {
        internal int LoggedCount { get; private set; }

        /// <summary>Counts every <see cref="IsEnabled"/> probe for <see cref="LogLevel.Debug"/>.</summary>
        internal int DebugEnabledProbes { get; private set; }

        /// <summary>Counts emitted VACUUM candidate-decision lines (event 4102).</summary>
        internal int DebugCandidateLines { get; private set; }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel)
        {
            if (logLevel == LogLevel.Debug)
            {
                DebugEnabledProbes++;
            }

            return logLevel >= minimum;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= minimum)
            {
                LoggedCount++;

                if (eventId.Id == 4102)
                {
                    DebugCandidateLines++;
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    [Fact]
    public async Task VacuumCandidateLog_IsGatedOnIsEnabled_SoDescribePathIsNotEvaluatedWhenSuppressed()
    {
        // Round-4 (Balanced): [LoggerMessage] puts its IsEnabled check INSIDE the generated method, so C#
        // evaluates the arguments at the call site regardless of level. This is the only per-candidate (not
        // per-fault) DescribePath in the codebase, so an ungated call site pays for every candidate whether
        // or not anything consumes the line. The explicit outer gate is what stops it.
        await SeedOneExpiredCandidateAsync();

        // With Debug ENABLED the call site probes IsEnabled itself and the generated method probes again:
        // two probes per candidate is the observable signature of an explicit outer gate. Remove the gate and
        // only the generated method probes, so the count halves -- that is what pins this fix.
        var verbose = new LevelGatedLogger(LogLevel.Debug);
        await NewVacuum(verbose).VacuumAsync(TimeSpan.FromHours(168), dryRun: true);

        Assert.Equal(2, verbose.DebugEnabledProbes);
        Assert.Equal(1, verbose.DebugCandidateLines);

        // With Debug DISABLED the outer gate short-circuits: DescribePath is never invoked and nothing is
        // logged. At ~1.4 KB per candidate (measured) an ungated 1M-file VACUUM would burn ~1.4 GB of Gen0
        // building strings for output that is thrown away.
        var quiet = new LevelGatedLogger(LogLevel.Information);
        await NewVacuum(quiet).VacuumAsync(TimeSpan.FromHours(168), dryRun: true);

        Assert.Equal(1, quiet.DebugEnabledProbes);
        Assert.Equal(0, quiet.DebugCandidateLines);
    }

    private DeltaVacuum NewVacuum(ILogger<DeltaVacuum> logger) =>
        new(
            _backend,
            policy: null,
            logger: logger,
            telemetry: null,
            timeProvider: new FixedTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    private async Task SeedOneExpiredCandidateAsync()
    {
        await _backend.PutIfAbsentAsync(
            "_delta_log/00000000000000000000.json",
            System.Text.Encoding.UTF8.GetBytes(
                DeltaTestHarness.Protocol() + "\n" + DeltaTestHarness.Metadata() + "\n"),
            CancellationToken.None);
        await _backend.PutIfAbsentAsync(PartitionedPath, new byte[] { 1, 2, 3 }, CancellationToken.None);
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, PartitionedPath.Replace('/', Path.DirectorySeparatorChar)),
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task VacuumCandidateLog_PartitionedPath_DropsValues_ButAuditKeepsRaw()
    {
        // The VACUUM audit line is a [LoggerMessage] that fires UNCONDITIONALLY from inside DeltaSharp, so it
        // bypasses the whole exception-Message sweep: one candidate under a PII-valued partition was enough to
        // put the value (and, with a poisoned path, a forged log line) on a text sink.
        var logger = new RecordingLogger<DeltaVacuum>();
        var vacuum = new DeltaVacuum(
            _backend,
            policy: null,
            logger: logger,
            telemetry: null,
            timeProvider: new FixedTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        await _backend.PutIfAbsentAsync(
            "_delta_log/00000000000000000000.json",
            System.Text.Encoding.UTF8.GetBytes(
                DeltaTestHarness.Protocol() + "\n" + DeltaTestHarness.Metadata() + "\n"),
            CancellationToken.None);
        await _backend.PutIfAbsentAsync(PartitionedPath, new byte[] { 1, 2, 3 }, CancellationToken.None);
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, PartitionedPath.Replace('/', Path.DirectorySeparatorChar)),
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        VacuumResult result = await vacuum.VacuumAsync(TimeSpan.FromHours(168), dryRun: true);

        RecordingLogger<DeltaVacuum>.Entry candidate =
            Assert.Single(logger.Entries, e => e.EventId.Name == "DeltaVacuumCandidateDecision");

        AssertKeepsContextDropsValues(candidate.Message);

        // The typed audit entry the CALLER receives still carries the exact raw key.
        Assert.Contains(result.Audit, a => string.Equals(a.Path, PartitionedPath, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------
    // Round 22 (Quality, BLOCKING): the THIRD echo path, and it is the guard itself.
    //
    // `hiveInBackslashRun` is what suppresses both of the echoes closed in earlier rounds -- the file-name
    // echo and the column-name echo. It is set when a segment carrying a Hive separator is followed by a
    // backslash, and CLEARED by a `/`, because a `/` really does end a component everywhere. The terminal
    // check read the LIVE latch, so a `/` arriving after the last named segment cleared the guard before
    // it was ever consulted, and the tail of the partition value went out as the file name:
    //
    //     email=x\alice.taylor%40example.com/   ->   'alice.taylor%40example.com' (partitioned by: email)
    //
    // Reachable through the ordinary public backend surface with no fault injection -- an OpenReadAsync on
    // a poisoned add.path -- and the trailing slash is exactly what a listing prefix looks like.
    //
    // Two prior seats hunted for a third echo path and found none, because neither corpus varied the TAIL:
    // the identical path WITHOUT the trailing separator redacts correctly. That is why the theory below is
    // a product over a tail axis rather than a list of rows -- the axis is the finding.
    //
    // The fix reads `lastSegmentInsideHiveRun` (the latch as of BEFORE the terminal segment) instead. It is
    // a statement about the SEGMENT rather than about the end of the string, and it strictly dominates:
    // after the terminal segment is recorded the latch can only be cleared, never set, so consulting the
    // live one could only ever lose suppression.
    [Theory]
    // The tail axis. "" is the shape both earlier corpora carried, and it was already correct.
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("/.")]
    [InlineData("\\")]
    [InlineData("/\\")]
    public void DescribePath_ValueTailFollowedByATrailingSlash_IsStillNotNamed(string tail)
    {
        // The value sits after a backslash inside a run whose Hive separator appeared earlier, so under the
        // POSIX reading every character of it is partition-value content.
        foreach (string head in new[] { string.Empty, "region=EU/", "a/b/", "/", "C:\\warehouse\\" })
        {
            foreach (string key in new[] { "email", "owner name", "o'brien" })
            {
                string path = head + key + "=x\\" + EncodedValue + tail;

                string rendered = DiagnosticText.DescribePath(path);

                Assert.DoesNotContain(EncodedValue, rendered, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    DecodedValue, Uri.UnescapeDataString(rendered), StringComparison.OrdinalIgnoreCase);

                // Not merely absent from the file-name slot -- absent from the column list too, which is the
                // second echo this same latch closes.
                Assert.DoesNotContain("taylor", rendered, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // The other half of the fix, and the reason it is not simply "never name a terminal segment after a
    // backslash": a `/` genuinely does end the value run, so a real file name BELOW one is still named.
    // Without this the fix would trade a disclosure for a diagnosability loss on every Windows-shaped path.
    [Theory]
    [InlineData("email=x\\" + EncodedValue + "/part-0.parquet", "part-0.parquet")]
    [InlineData("region=EU/email=x\\" + EncodedValue + "/part-0.parquet", "part-0.parquet")]
    [InlineData("a/b/email=x\\" + EncodedValue + "//part-0.parquet", "part-0.parquet")]
    public void DescribePath_RealFileNameBelowAClearedValueRun_IsStillNamed(string path, string expectedName)
    {
        string rendered = DiagnosticText.DescribePath(path);

        Assert.Contains(expectedName, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(EncodedValue, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(rendered), StringComparison.OrdinalIgnoreCase);
    }

    // A census rather than a corpus, because the finding was an AXIS and a corpus cannot demonstrate the
    // absence of one. Sentinel-in-value-only: the protected run is the only place either sentinel appears,
    // so any survivor in the output is a leak regardless of which mechanism carried it -- the same
    // alphabet-independent oracle the Redact matrix uses, for the same reason (a fragment alphabet that
    // enumerates known mechanisms can never detect an unknown one).
    [Fact]
    public void DescribePath_ValueRunCensus_LeaksNoSentinelOnAnyTail()
    {
        const string Head = "QQQQ";
        const string Tail = "ZZZZ";
        string[] roots = { string.Empty, "/", "a/", "a/b/", "region=EU/", "C:\\warehouse\\", "\\\\srv\\s\\" };
        string[] keys = { "email", "owner name", "o'brien", string.Empty, "a.b", "k%3D", "K" };
        string[] separators = { "=", "%3D", "%3d" };
        // GENERATED OVER THE SEPARATOR ALPHABET; see the tail axis for why this is a parameter and not
        // a longer list. `S` is the hole; separator-free shapes are emitted once.
        string[] tailShapes =
        {
            string.Empty, "S", "SS", "Spart-0.parquet", "Spart 0.parquet", "Sa b", "Sa bSc",
            "S..", "S.", " and more", "'.", "Sp q", "/S", "S/",
        };

        string[] tails = tailShapes
            .SelectMany(shape => shape.Contains('S', StringComparison.Ordinal)
                ? new[] { "/", "\\" }.Select(sep => shape.Replace("S", sep, StringComparison.Ordinal))
                : new[] { shape })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        int cells = 0;
        int leaked = 0;
        string? firstLeak = null;

        foreach (string root in roots)
        {
            foreach (string key in keys)
            {
                foreach (string separator in separators)
                {
                    foreach (string tail in tails)
                    {
                        // The value -- and ONLY the value -- carries the sentinels.
                        string path = root + key + separator + "x\\" + Head + "alice" + Tail + tail;

                        string rendered = DiagnosticText.DescribePath(path);

                        cells++;
                        if (rendered.Contains(Head, StringComparison.Ordinal)
                            || rendered.Contains(Tail, StringComparison.Ordinal))
                        {
                            leaked++;
                            firstLeak ??= path + "  ->  " + rendered;
                        }
                    }
                }
            }
        }

        Assert.Equal(0, leaked);
        Assert.Null(firstLeak);

        // Adequacy last; see the note on the whole-corpus comparison.
        Assert.Equal(3381, cells);
    }

    // The sibling recognizer under the SAME tail axis, because a lifetime bug in one guard is a reason to
    // ask whether the other is tail-sensitive too. It is not -- PARTIAL is 0 on every cell. What this test
    // exists to pin is the DECLINE classification, and it exists because the first time I measured it I
    // wrote down a claim wider than my corpus.
    //
    // I reported "the only declines are R4" from a key set that contained no whitespace key. Three review
    // seats ran the same axis over three different key sets and got three different survivor sets; the
    // union is R4 AND R1, and the difference was entirely whether the corpus carried `my col`. The claim
    // was true of what I measured and false of what I said, which is the defect this PR has spent the most
    // rounds on: A CLAIM ASSERTED OVER A SPACE WIDER THAN THE ONE THAT WAS MEASURED.
    //
    // So the classification is asserted by the code rather than described in prose, and `outsideBoth` is
    // asserted to be zero -- that assertion is the part that cannot silently widen, because a new decline
    // class shows up as a failure rather than as a sentence nobody re-checks.
    [Fact]
    public void Redact_SiblingRecognizerUnderTheSameTailAxis_DeclinesOnlyIntoFiledResiduals()
    {
        const string Head = "QQQQ";
        const string Tail = "ZZZZ";

        // Rooted and rootless, POSIX and Windows and UNC. Rootlessness is R4's predicate, so it has to vary.
        string[] roots = { "/tbl/", string.Empty, "C:\\tbl\\", "\\\\srv\\s\\", "a/b/" };

        // R1 IS ABOUT WHITESPACE, NOT ABOUT U+0020. The first version of this key set carried only `my col`,
        // and two seats found the predicate wrong in OPPOSITE directions from it: too narrow, because a
        // Delta column name may carry a tab, an NBSP or a line separator and R1 covers all of them; and too
        // broad, because `o'brien y` bears a quote AS WELL as whitespace and that is R2, a different clause
        // with a different irreducibility argument. Both are the same error as the R4 predicate before it --
        // stating what the corpus's cells LOOK LIKE instead of what the residual is ABOUT.
        string[] keys =
        {
            "email", "owner", "K",
            "my col", "my\tcol", "my\u00a0col", "my\u2028col",
            "o'brien", "o'brien y",
        };

        // The axis itself: what follows the value. A `/` is a true right delimiter everywhere, a `\` is one
        // only on Windows and so counts as none, and the prose tails carry no delimiter at all.
        //
        // WIDEN THIS AXIS BY STRUCTURE, NOT BY CHARACTER. A reviewer attacked the predicate by widening the
        // corpus 3.5x -- an empty key, a quote-bearing key, non-ASCII, and seven more tail characters
        // INCLUDING A TAB -- reached 800 cells, saw outsideBoth still 0, and concluded the partition was a
        // property of the recognizer. The gate reached the missing class with ONE tail, `/part 0.parquet`.
        //
        // The difference is not effort, it is dimension. That reviewer varied WHICH CHARACTERS the tail
        // contains; the gate varied WHERE IN THE SEGMENT STRUCTURE the whitespace sits. Branch 5's anchor
        // reads (?=/[^/]*(?:[/'"]|$)), so the question it asks is not "does the tail contain whitespace"
        // but "does the segment AFTER THE NEXT SLASH contain whitespace before its terminator" -- and a tab
        // appended to the end of the current segment never reaches that question. 800 cells and five
        // reviewers went past it.
        //
        // So the axis to add here is SEGMENT-RELATIVE POSITION, not more whitespace characters. Stated
        // explicitly because the next person to widen this corpus will reach for characters too: that is
        // the cheap move, it feels like coverage, and it is measured in cells rather than in questions.
        //
        // THE LAST THREE SHAPES ARE THE ROW CLASS THIS CORPUS COULD NOT EXPRESS. Every shape that carried
        // path evidence was whitespace-free downstream, so `outsideBoth == 0` was sound in form and vacuous
        // in exactly the dimension a live leak lived in: branch 5's right anchor declined whenever the
        // segment AFTER the value contained a space. Adding these three took outsideBoth from 0 to 4 before
        // the fix. An assertion is only as complete as the axis it ranges over, and the axis is the part
        // that has to be argued.
        //
        // THE TAILS ARE GENERATED, NOT LISTED, AND THE SEPARATOR IS A PARAMETER. This is the third
        // structural correction to this axis and the most important one, because it is the correction that
        // three independent opt-out hunts needed and none of them made:
        //
        //   a hunt over 780 position cells plus a 31-character prefix sweep -- swept FORWARD-SLASH positions
        //   a hunt over 7,800 cells                                         -- FORWARD-SLASH structure
        //   a hunt over 594 + 198 cells, whose axis was "which segment AFTER THE VALUE carries the hazard,
        //     and where inside that segment" -- which PRESUPPOSES a `/` after the value to create segments
        //
        // The third of those included a backslash in its hazard-character set and a rootless root in its
        // root set, and it still could not reach `k=SENTINEL\part.parquet`, because the defect has no
        // forward slash anywhere: the separator after the value IS the backslash. None of the three hunts
        // was under-powered. All three encoded "a path is delimited by forward slashes" into the SHAPE of
        // the corpus, where widening cannot reach it.
        //
        // So the fix is not another hazard character. It is that a tail is written as a SHAPE with the
        // separator left as a hole, and the hole is filled from the separator alphabet -- the same alphabet
        // the recognizer accepts. Adding `\` to a list of characters is the cheap move that feels like
        // coverage and leaves the presupposition in place; parameterising the separator removes it. This is
        // the corpus-level statement of the same defect the R4 predicate had one layer in: the residual said
        // "no slash at all", the classifiers said "no FORWARD slash", and the generators said "segments
        // delimited by forward slashes". All three had to move.
        string[] separatorAlphabet = { "/", "\\" };

        // S is the hole. A shape with no S is separator-free and is emitted once; every other shape is
        // emitted once per separator, so the axis ranges over the recognizer's whole alphabet by
        // construction rather than by anyone remembering to add a row.
        string[] tailShapes =
        {
            string.Empty, "S", "SS", "S.", "S..", "Spart-0.parquet", " and more", "'.",
            "Spart 0.parquet", "Sa b", "Sa bSc",
        };

        string[] tails = tailShapes
            .SelectMany(shape => shape.Contains('S', StringComparison.Ordinal)
                ? separatorAlphabet.Select(sep => shape.Replace("S", sep, StringComparison.Ordinal))
                : new[] { shape })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // A SECOND STRUCTURAL AXIS, ADDED FOR THE SAME REASON THE LAST THREE TAILS WERE. Every cell above
        // used to hard-code a value of `x\SENTINEL`, so every cell carried a value-internal backslash. That
        // was invisible while no branch keyed on one; the moment branch 6 did, the whole corpus moved into
        // FULL together and R1 emptied -- an anti-vacuity assertion firing not because a residual closed but
        // because the corpus could only express one side of a distinction the code had started making.
        //
        // The axis is therefore WHETHER THE VALUE CARRIES ITS OWN SEPARATOR EVIDENCE, which is a structural
        // property of the cell and not a character in it. It is what keeps R1 and R2 observable now that
        // backslash evidence lifts a cell out of them.
        string[] valuePrefixes = { @"x\", string.Empty };

        int cells = 0;
        int full = 0;
        int declined = 0;
        int partial = 0;
        int r1 = 0;
        int r2 = 0;
        int r4 = 0;
        int outsideBoth = 0;
        string? firstPartial = null;
        string? firstOutside = null;

        foreach (string root in roots)
        {
            foreach (string key in keys)
            {
                foreach (string tail in tails)
                {
                    foreach (string valuePrefix in valuePrefixes)
                    {
                        string message = BothRecognizerProse + root + key + "=" + valuePrefix
                            + Head + "alice" + Tail + tail;
                        string actual = LocalFileSystemBackend.HivePartitionValue().Replace(message, "<value>");

                        bool marked = actual.Contains("<value>", StringComparison.Ordinal);
                        bool survived = actual.Contains(Head, StringComparison.Ordinal)
                            || actual.Contains(Tail, StringComparison.Ordinal);

                        cells++;
                        if (marked && !survived)
                        {
                            full++;
                            continue;
                        }

                        if (marked)
                        {
                            partial++;
                            firstPartial ??= message + "  ->  " + actual;
                            continue;
                        }

                        declined++;

                        // R4: NOTHING IN THE MESSAGE PROVES THIS IS A PATH. Not merely "no forward slash" --
                        // that predicate was my first draft and it misclassified 8 cells, because a Windows
                        // root is path evidence too and a `C:\tbl\my col=...` decline is caused by the KEY,
                        // not by rootlessness. Getting the residual predicates to partition correctly required
                        // stating what each residual is ABOUT rather than what its cells happen to look like.
                        // A cell with no evidence at all is indistinguishable from `errno=13`-shaped prose,
                        // which is why R4 is not closable.
                        //
                        // AND NOT MERELY "NO FORWARD SLASH" EITHER, which is the error one layer out and the one
                        // that cost a live disclosure. Five reviewers and three corpora all wrote the predicate
                        // as "no forward slash, no drive root, no UNC root" -- WIDER than the residual it models,
                        // because a rootless `k=v\part.parquet` has none of those and still carries a backslash
                        // with content after it. Every such cell was bucketed R4 and excused. `errno=13` has no
                        // backslash; `DescribePath` reached FULL on these strings using nothing else; and
                        // `Redact` itself already accepted a backslash as evidence when the path was ROOTED.
                        // The predicate now asks the residual's own question.
                        //
                        // THE CORRECTION USED TO BE UNPINNED HERE, AND THAT SENTENCE WENT STALE WITHOUT A
                        // NUMBER IN IT. It said the forward-slash-only revert leaves this test GREEN and reds
                        // only the monotonicity model. MEASURED-AT f74f9b4, by performing the revert: this
                        // test itself goes RED, and so does
                        // TheSupersededEnumeration_SurvivesOnlyWhereItIsMeasured, which names the site. The
                        // recorded gap has CLOSED and the prose still described it as open -- a verdict claim
                        // about the current tree that nothing executed, carrying no count to make its rot
                        // visible. It is kept rather than deleted because the reasoning under it is still the
                        // reason the predicate is written this way: once branch 6 ships, no cell in this
                        // corpus both DECLINES and
                        // carries backslash-only evidence, so the wide predicate and the correct one agree on
                        // every decline that still exists. The class the wide predicate used to excuse is
                        // empty. That is exactly why the predicate had to be read against the residual's TEXT
                        // instead of measured against the corpus -- measuring it here, today, proves nothing,
                        // and measuring it here yesterday would have reported the same 0 while a value was
                        // going out in full.
                        bool hasPathEvidence = HasSeparatorEvidence(message);

                        // R2: a key bearing BOTH a quote and whitespace, delimited by something that is not a
                        // real path separator. Tested FIRST, because R2's cells all satisfy R1's predicate too
                        // and the clauses are not interchangeable -- R2's irreducibility argument is that
                        // `o'brien y=v'` and `file": errno=13"` are the same string shape, which says nothing
                        // about R1.
                        bool r2Shape = key.Any(char.IsWhiteSpace) && key.Contains('\'', StringComparison.Ordinal);

                        // R1: a WHITESPACE-bearing key with no right delimiter after the value. Any Unicode
                        // whitespace -- a Delta column name is not restricted to U+0020, and the branch-4 key
                        // class excludes \s, which is what R1 is about. `\` is not a delimiter (it only ends a
                        // segment on Windows), so it does not lift a cell out of R1.
                        bool r1Shape = key.Any(char.IsWhiteSpace);

                        if (!hasPathEvidence)
                        {
                            r4++;
                        }
                        else if (r2Shape)
                        {
                            r2++;
                        }
                        else if (r1Shape)
                        {
                            r1++;
                        }
                        else
                        {
                            outsideBoth++;
                            firstOutside ??= message + "  ->  " + actual;
                        }
                    }
                }
            }
        }

        Assert.Null(firstPartial);

        // Behaviour first, adequacy last. Found by a statement-joined sweep after a line-indexed one
        // reported zero: the condition of this file's dominant assertion form never sits on the
        // `Assert.` line, so a line-indexed sweep structurally cannot see it.
        Assert.Equal(roots.Length * keys.Length * tails.Length * valuePrefixes.Length, cells);

        // INVARIANT 1 on this axis: no marker is ever emitted over a surviving value.
        Assert.Equal(0, partial);

        // Every decline lands in a residual that is filed, argued irreducible and cross-referenced.
        Assert.Equal(0, outsideBoth);
        Assert.Null(firstOutside);
        Assert.Equal(declined, r1 + r2 + r4);

        // Every class is non-empty, so no branch of the classification is vacuous -- the failure mode that
        // produced the over-wide claim in the first place was a class nobody had a cell for.
        //
        // TWO DIFFERENT THINGS GET CALLED "THIS ASSERTION IS NOT VACUOUS", AND ONLY ONE OF THEM WAS TRUE
        // HERE. A reviewer mutated branch 4's terminal value run and watched outsideBoth go 0 -> 64, which
        // proves the assertion DISCRIMINATES WHEN THE CODE CHANGES. In the same round the gate added one
        // tail -- `/part 0.parquet` -- and watched outsideBoth go 0 -> 4 against UNMUTATED, SHIPPED code,
        // which proves the CORPUS WAS MISSING AN AXIS THE SHIPPED CODE ALREADY VARIED OVER. Both results
        // are correct and they are not about the same property.
        //
        // A guard can discriminate perfectly against every mutant anyone writes and still never be shown
        // the input that breaks it, because mutants are drawn from the same mental model as the code and
        // the corpus is drawn from the same mental model as the assertion. Non-vacuity under mutation says
        // the assertion has teeth; only an argument about the AXES says it has been pointed at the right
        // input. This note is here because a future reader who finds the 0 -> 64 result will reasonably
        // conclude the assertion is sound and stop -- and stopping there is what left the leak in place.
        Assert.True(r1 > 0, "R1 (whitespace key, no right delimiter) has no cell; the classification is vacuous.");
        Assert.True(r2 > 0, "R2 (quote AND whitespace key) has no cell; the classification is vacuous.");
        Assert.True(r4 > 0, "R4 (rootless, no slash evidence) has no cell; the classification is vacuous.");

        // 1710 = 5 roots x 9 keys x 19 generated tails x 2 value prefixes. The tail count is 19 rather
        // than 22 because three shapes carry no separator hole and are emitted once.
        //
        // MEASURED, NOT ASSUMED -- AND THE HEADLINE NUMBER WAS RELAYED AS ONE RESULT WHEN IT IS THREE.
        // MEASURED-AT the commits named in each row; the last row re-run at 236dbc2.
        // Four reviewers reconstructed "delete branch 6 and see what the pre-fix corpus catches" and got
        // 8 RED, 9 RED, 10 RED and 0 RED. All four are correct; they are four different mutants:
        //
        //   whole pre-fix test FILE restored, branch 6 deleted     Failed 0, Passed 2094   TOTALLY BLIND
        //   pre-fix TAILS only, current value shapes kept          10 RED, whole-corpus comparison FIRES
        //   generated tails, branch 6 deleted (at that HEAD)        9 RED
        //   at 236dbc2, branch 6 deleted                          15 RED
        //
        // THE 0 AND THE 10 DO NOT CONFLICT, AND THE DIFFERENCE NAMES THE SECOND AXIS. `valueShapes` below
        // carries its own backslash (`x\` + Sentinel), independent of the tail alphabet, so neutering the
        // tail generator ALONE cannot blind the whole-corpus comparison. Only the wholesale file swap
        // removes both axes at once, which is why it alone reports zero. So the accurate claim is not "the
        // generated tails closed it": the pre-fix corpus was blind IN ITS ENTIRETY, and the tail generator
        // is one of two axes that reach the class.
        //
        // WHICH MAKES THE VALUE-PREFIX AXIS LOAD-BEARING RATHER THAN MERELY PRINCIPLED. Three reviewers
        // called the decision to leave `valuePrefixes` as `{ "x\", "" }` a sound one on argument. It is
        // also doing measurable work: it is the axis that keeps the whole-corpus comparison awake when the
        // tail alphabet is neutered. An argued non-change and a measured one are not the same evidence, and
        // this is now the second.
        //
        // AND THE TAIL AXIS REACHES IT INDEPENDENTLY: deleting branch 6 AND removing the `x\` value shape
        // still fires the whole-corpus comparison, on a value ended by a generated TAIL. That result stands.
        //
        // THE CAUSE FIRST GIVEN FOR IT DID NOT. This paragraph named the firing cell as `email=QQZZQQ\` and
        // credited the predicate fix for its visibility. Both halves were wrong, and a reviewer caught it:
        // the run fired on the TWO-backslash spelling, which the superseded enumeration already flagged
        // because it contains the `\\` that enumeration tests for. The ONE-backslash spelling was excused,
        // and became visible only when the fold landed a commit later. The measurement was sound, the
        // conclusion was sound, and the mechanism offered for it was not the mechanism.
        //
        // AND THE MECHANISM, SUPPLIED BY A THIRD REVIEWER, IS WORSE THAN "THE CELL WAS MISNAMED": the
        // superseded predicate counted `\\` and not `\`, and this corpus happens to contain a UNC-shaped
        // DOUBLE backslash. So the comparison passed on an accident of the corpus, not on the axis anyone
        // believed -- 90% blind on the lone-backslash axis, and green because of a tail nobody chose for
        // that purpose. An accidental pass in the artifact built to prevent accidental passes.
        //
        // Which is this chain's fifth correct conclusion resting on a wrong stated cause, and the first to
        // occur INSIDE the paragraph documenting that failure mode. So the cell and its classifier are
        // asserted in R4Classifier_DivergesFromTheEnumerationItReplaced_OnceABranchIsWeakened rather than
        // described here. The corpus was never blind on that axis; the PREDICATE was, by 140 cells.
        //
        // THE DECLINE COUNT DID NOT MOVE WHEN THE BACKSLASH SPELLINGS WERE ADDED: 116 before and after,
        // with all 630 new cells landing in FULL. That is the fix being total rather than lucky -- every
        // tail shape that redacts through a forward slash redacts through a backslash too. Had this axis
        // been generated from the start, the fifth drift would have been a failing cell on the day branch 5
        // shipped instead of a gate finding three rounds later.
        //
        // AND IT MOVED, 116 -> 87, WHEN THE TERMINAL-BACKSLASH OPT-OUT WAS CLOSED. Every one of the 29
        // cells moved from DECLINE to FULL, and all three residuals shrank rather than trading against each
        // other: R4 36 -> 27, R1 64 -> 48, R2 16 -> 12. R2 shrinking is the part worth noting, because R2's
        // irreducibility argument is that a quote-and-whitespace key is indistinguishable from quoted prose
        // -- true only while neither carries a separator, so a class that gains separator evidence leaves
        // R2 by R2's own definition rather than by exception.
        Assert.Equal(1623, full);
        Assert.Equal(87, declined);
        Assert.Equal(27, r4);
        Assert.Equal(12, r2);
        Assert.Equal(48, r1);
    }

    // THE FOURTH DRIFT BETWEEN THE TWO RECOGNIZERS, and the first one that was a live disclosure with no
    // fault injection anywhere. Branch 5's right anchor read `(?=/[^/\s]*(?:[/'"]|$))` -- it required the
    // segment AFTER the value to be whitespace-free. A Parquet file name may contain a space, so:
    //
    //     k=SENTINEL/part-0.parquet   ->  k=<value>/part-0.parquet     redacted
    //     k=SENTINEL/part 0.parquet   ->  k=SENTINEL/part 0.parquet    echoed in full
    //
    // One space, one segment DOWNSTREAM of the value, and a full redaction becomes a full leak. DescribePath
    // got both right, because it splits rather than pattern-matches.
    //
    // The class was not a residual, it was an ATTACKER-SELECTABLE OPT-OUT: add.path is foreign, so the
    // author of a poisoned path decides whether the recognizer runs by choosing a file name. The rule this
    // file states -- a shape the recognizer declines is a redaction the attacker opted out of by choosing
    // it -- has no exception for shapes that are expensive to close, and this one cost four prose rows.
    [Theory]
    [InlineData("k=" + EncodedValue + "/part 0.parquet")]
    [InlineData("k=" + EncodedValue + "/a b")]
    [InlineData("k=" + EncodedValue + "/a b/c")]
    [InlineData("k=" + EncodedValue + "/a\tb")]
    [InlineData("'k=" + EncodedValue + "/part 0.parquet'")]
    [InlineData("region=EU/k=" + EncodedValue + "/part 0.parquet")]
    // The whitespace-free counterparts, which were ALREADY correct. Kept beside the leaking rows because the
    // finding is the DIFFERENCE between them: a corpus carrying only these could not see the defect, and
    // that is exactly what every corpus in this file carried until the gate ran.
    [InlineData("k=" + EncodedValue + "/part-0.parquet")]
    [InlineData("k=" + EncodedValue + "/a/b")]
    public void Redact_DownstreamSegmentContainingWhitespace_StillRedactsTheValue(string path)
    {
        string actual = LocalFileSystemBackend.HivePartitionValue()
            .Replace(BothRecognizerProse + path, "${key}=<value>");

        Assert.DoesNotContain(EncodedValue, actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(actual), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<value>", actual, StringComparison.Ordinal);

        // The sibling has to agree, on the identical string. Four drifts in, agreement is asserted rather
        // than assumed every time either recognizer is touched.
        string rendered = DiagnosticText.DescribePath(path);
        Assert.DoesNotContain(EncodedValue, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(rendered), StringComparison.OrdinalIgnoreCase);
    }

    // THE FIFTH DRIFT, and the second live disclosure found by the gate rather than by a reviewer. A
    // ROOTLESS path whose only separator is a BACKSLASH echoed its value in full:
    //
    //     k=SENTINEL\part.parquet      Redact   -> k=SENTINEL\part.parquet     ECHOED
    //                                  Describe -> '(directory)' (partitioned by: k)   FULL
    //     C:\t\k=SENTINEL\part.parquet Redact   -> C:\t\k=<value>              redacted
    //
    // WHY THIS WAS NOT R4, IN R4's OWN WORDS. R4 declines "a bare key=value with no slash at all ...
    // indistinguishable from `errno=13` by any means available in the string". The operative word is
    // INDISTINGUISHABLE, and a backslash IS a means available in the string: `errno=13` has none,
    // `DescribePath` reaches FULL on these using nothing else, and `Redact` itself already accepted a
    // backslash as evidence when the path was ROOTED. Accepting the same separator rooted and rejecting it
    // rootless is an internal inconsistency, not a principled decline.
    //
    // HOW IT HID FROM FIVE REVIEWERS AND THREE CORPORA: the in-source residual says "no slash at all", and
    // every classifier written against it said "no FORWARD slash". The predicate was WIDER than the residual
    // it modelled, so every one of these cells was bucketed R4 and excused -- including by the
    // `outsideBoth == 0` assertion added one round earlier specifically to stop over-wide claims. A
    // classification is only as sound as the predicate that names its buckets, and a predicate wider than
    // its residual converts findings into excuses silently.
    [Theory]
    [InlineData("k=" + EncodedValue + "\\part.parquet", true)]
    [InlineData("k=" + EncodedValue + "\\a\\b", true)]
    [InlineData("k=" + EncodedValue + "\\a b\\c", true)]
    [InlineData("region=EU\\k=" + EncodedValue + "\\part.parquet", true)]
    [InlineData("my col=" + EncodedValue + "\\part.parquet", true)]
    // A TRAILING BACKSLASH IS EVIDENCE TOO, AND THIS ROW USED TO SAY OTHERWISE. The branch first required
    // content AFTER the separator, which spared the prose row `st_mode=0100644\` -- and handed the attacker
    // a third opt-out one character wide: a value ending AT its backslash declined and echoed in full. The
    // rule that settles it is the same one that forces this branch to consume its tail: on POSIX a `\` is an
    // ordinary filename character, so a value may legitimately end with one. The rule that forbids `\` from
    // TERMINATING the value run therefore also forbids it from being the thing that denies evidence -- which
    // is why the terminal case was a bug and not a second, symmetric asymmetry.
    [InlineData("k=" + EncodedValue + "\\", true)]

    // GENUINE R4, ASSERTED TO STAY DECLINED. Without a separator these are `errno=13` with a longer value,
    // and redacting them would destroy the diagnostics this recognizer exists to preserve. These rows are
    // what makes the fix a narrowing of R4 rather than its deletion.
    [InlineData("k=" + EncodedValue, false)]
    [InlineData("k=" + EncodedValue + ".parquet", false)]
    public void Redact_RootlessBackslashPath_RedactsTheValueAndLeavesBareProseAlone(string path, bool expectRedacted)
    {
        string actual = LocalFileSystemBackend.HivePartitionValue()
            .Replace(BothRecognizerProse + path, "${key}=<value>");

        if (!expectRedacted)
        {
            Assert.DoesNotContain("<value>", actual, StringComparison.Ordinal);
            Assert.EndsWith(path, actual, StringComparison.Ordinal);
            return;
        }

        Assert.DoesNotContain(EncodedValue, actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(actual), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<value>", actual, StringComparison.Ordinal);

        // The sibling on the identical string. It was already correct on every row here, which is what made
        // this a DRIFT rather than a shared blind spot -- and what made the pairwise assertion the only
        // instrument able to see it.
        string sibling = DiagnosticText.DescribePath(path);
        Assert.DoesNotContain(EncodedValue, sibling, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            DecodedValue, Uri.UnescapeDataString(sibling), StringComparison.OrdinalIgnoreCase);
    }

    // A CELL-BY-CELL COMPARISON OF THE TWO RECOGNIZERS OVER THE WHOLE CORPUS, rather than at the positions
    // where a defect was last measured. Four drifts have been found -- the fragment alphabet, the search
    // strategy, the separator alphabet, and the downstream whitespace class -- and every one was found by a
    // reviewer's corpus rather than by reasoning. The pattern in all four is the same: a class that narrows
    // one recognizer on evidence the ATTACKER SUPPLIES is a switch the attacker owns, and the two
    // recognizers acquired those switches independently.
    //
    // What is asserted:
    //   * DescribePath NEVER leaks. It splits rather than pattern-matches, so it has no declining shape and
    //     no residual; any leak from it is a bug, full stop.
    //   * Redact leaks ONLY where a filed residual holds. Every other divergence is a new drift and fails
    //     here rather than in the next review.
    [Fact]
    public void BothRecognizers_OverTheWholeCorpus_DivergeOnlyInsideAFiledResidual()
    {
        const string Sentinel = "QQZZQQ";
        string[] roots = { "/tbl/", string.Empty, "C:\\tbl\\", "\\\\srv\\s\\", "a/b/" };
        string[] keys = { "email", "K", "my col", "o'brien", "o'brien y", "a.b" };
        // GENERATED OVER THE SEPARATOR ALPHABET, for the reason spelled out at length on the tail axis:
        // listing backslash spellings by hand is adding a character, and what hid the fifth drift was a
        // corpus SHAPED around forward slashes. `S` is the hole; separator-free shapes are emitted once.
        string[] tailShapes =
        {
            string.Empty, "S", "SS", "Spart-0.parquet", "Spart 0.parquet", "Sa b", "Sa bSc",
            "S..", "S.", " and more", "'.", "Sp q", "/S", "S/",
        };

        string[] tails = tailShapes
            .SelectMany(shape => shape.Contains('S', StringComparison.Ordinal)
                ? new[] { "/", "\\" }.Select(sep => shape.Replace("S", sep, StringComparison.Ordinal))
                : new[] { shape })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] valueShapes = { Sentinel, Sentinel + "%40x", "x\\" + Sentinel, Sentinel + "'s" };

        int cells = 0;
        int describeLeaks = 0;
        int redactLeaks = 0;
        int unexplained = 0;
        string? firstDescribeLeak = null;
        string? firstUnexplained = null;

        foreach (string root in roots)
        {
            foreach (string key in keys)
            {
                foreach (string tail in tails)
                {
                    foreach (string value in valueShapes)
                    {
                        string path = root + key + "=" + value + tail;
                        cells++;

                        string rendered = DiagnosticText.DescribePath(path);
                        if (rendered.Contains(Sentinel, StringComparison.Ordinal))
                        {
                            describeLeaks++;
                            firstDescribeLeak ??= path + "  ->  " + rendered;
                        }

                        string message = BothRecognizerProse + path;
                        string actual = LocalFileSystemBackend.HivePartitionValue()
                            .Replace(message, "${key}=<value>");
                        if (!actual.Contains(Sentinel, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        redactLeaks++;

                        // R4: nothing in the message proves this is a path. R1/R2: the key carries
                        // whitespace, so branch 4's key class cannot reach it. Stated by CAUSE; a cell that
                        // satisfies neither is a class nobody has filed.
                        //
                        // THIS WAS THE THIRD COPY OF THE ENUMERATING PREDICATE, AND IT SURVIVED THE SWEEP
                        // THAT DELETED THE OTHER TWO. It spelled R4 as
                        // `!contains('/') && !contains(":\\") && !contains("\\\\")` -- the exact form that
                        // excused a live disclosure across three corpora and five reviews -- and it was
                        // still here one commit after that form was declared gone. Corrected one site,
                        // missed the sibling, in the commit that named the defect: the same shape as the
                        // guard table, the separator alphabet, and the two recognizers.
                        //
                        // It is folded onto the single helper NOT because the two disagreed -- measured
                        // over all 2,760 cells they agree on every one, and a reviewer confirmed it
                        // independently -- but because two classifiers for one residual, written on two
                        // different principles, is the PRECONDITION every drift in this file has shared.
                        // The recognizers drifted five times, the latch was read two ways, the guard table
                        // went stale three times, and R4's predicate excused a real leak twice. Not one of
                        // those was a disagreement when it was written. Waiting for a measured difference
                        // is waiting for the disclosure.
                        //
                        // AND IT IS PINNED, WHICH THE FIRST ATTEMPT AT THIS NOTE DENIED. Reverting the fold
                        // leaves the suite green, so it was recorded as an unpinned refactor made on
                        // principle. That was too modest, and modest in the direction that loses evidence:
                        // the two forms agree at HEAD only because every cell they disagree on is currently
                        // REDACTED, so no decline exists for either to bucket. Weaken the recognizer and the
                        // disagreement is 140 cells wide -- 140 leaks the enumeration waves through. See
                        // R4Classifier_DivergesFromTheEnumerationItReplaced_OnceABranchIsWeakened, which
                        // measures it by runtime pattern surgery rather than by adding a corpus cell, since
                        // a cell invented to make a refactor look pinned measures the test.
                        //
                        // "The two classifiers agree" was a statement about the corpus reaching the
                        // disagreement, not about the classifiers -- the same confusion as "zero red means
                        // equivalent", one level up.
                        //
                        // AND TWICE TOO MODEST, NOT ONCE. The pinning above is stated under a SYNTHETIC
                        // weakening -- a whole branch deleted. A reviewer supplied the real one: reopen the
                        // terminal-backslash opt-out, the one-character regression that actually shipped and
                        // was actually fixed. At the commit before the fold that mutant leaves THIS test
                        // GREEN; at this HEAD the same mutant reds it, and the increment is exactly the
                        // test you are reading -- no suite count, because nothing re-runs one. So the fold is
                        // pinned by the historical bug, not merely by a mutant built for the purpose, and
                        // the increment is exactly the fold. Recorded because both understatements had the
                        // same cause: only the mutants already built were tried.
                        bool r4 = !HasSeparatorEvidence(message);
                        bool r1OrR2 = key.Any(char.IsWhiteSpace);

                        if (!r4 && !r1OrR2)
                        {
                            unexplained++;
                            firstUnexplained ??= path + "  ->  " + actual;
                        }
                    }
                }
            }
        }

        // The splitter has no residuals. This is the assertion that would have caught drifts 1, 3 and 4 by
        // showing one recognizer suppressing where the other did not.
        Assert.Null(firstDescribeLeak);
        Assert.Equal(0, describeLeaks);

        // Every Redact leak is inside a filed, argued residual: #704/#708 (R1), #714 (R2), R4 beside
        // branch 5. A fifth drift arrives as a failure here.
        Assert.Null(firstUnexplained);
        Assert.Equal(0, unexplained);

        // Non-vacuous in the other direction: the corpus DOES contain residual cells, so "0 unexplained" is
        // not "0 leaks and nothing to explain".
        Assert.True(redactLeaks > 0, "No Redact leak in the corpus at all; the residual check is vacuous.");

        // ADEQUACY LAST, AND THIS GUARD IS WHY THE RULE EXISTS. It used to run FIRST, and change any corpus
        // axis and the cell count moves -- so it failed before the divergence assertions could speak,
        // reporting an arithmetic mismatch when the question was whether a drift is detected. It cost two
        // separate reproductions of the terminal-backslash class, both of which had to relax the number
        // before this test could say anything.
        //
        // IT WAS ANNOTATED RATHER THAN MOVED, and that is the part worth recording: a comment saying "this
        // fires first and is not the answer" leaves the trap armed and asks every future reader to read the
        // comment first. The same defect was then rediscovered in a test written in the same round and
        // fixed THERE, in place, without travelling back here -- the non-travelling fix, in the file that
        // has now had five of them. The guard is kept, because a corpus that silently shrinks is a real
        // failure; it is kept LAST, because it is a statement about the corpus and everything above it is a
        // statement about the code.
        Assert.Equal(2760, cells);
    }
}
