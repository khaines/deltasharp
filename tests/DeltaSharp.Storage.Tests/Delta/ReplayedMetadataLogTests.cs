using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Unit tests for <see cref="ReplayedMetadataLog"/> (#691) — the seam that lets the change-feed PRE-RANGE
/// column-mapping identity gate reuse the commits the start-snapshot reconstruction already read.
///
/// <para>The gate enforces a SECURITY property, so the dangerous failure mode is not "the observer is wrong"
/// but "the observer is SILENT and the gate believes it". These tests pin the two structural rules that make
/// silence unfalsifiable — proven coverage and corroborated silence — so that a future defect in the (entirely
/// non-security) snapshot-replay path can only cost a fail-closed read or an extra GET, never coverage.</para>
///
/// <para>They drive the seam DIRECTLY because an under-reporting observer cannot be produced through the
/// production call path (<see cref="ReplayedMetadataLog.Observe"/> extracts the metaData itself); the
/// <see cref="ReplayedMetadataLog.Record"/> primitive it delegates to models exactly the mutation the review
/// council used to demonstrate the fail-open.</para>
/// </summary>
public sealed class ReplayedMetadataLogTests
{
    private const string Schema =
        "{\"type\":\"struct\",\"fields\":[{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":{}}]}";

    private static MetadataAction Meta(string id) =>
        new(
            Id: id,
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
            SchemaString: Schema,
            PartitionColumns: ImmutableArray<string>.Empty,
            Configuration: ImmutableSortedDictionary<string, string>.Empty,
            CreatedTime: null);

    private static IReadOnlyList<MetadataAction> None => Array.Empty<MetadataAction>();

    // Observation and consumption are separate PHASES (council R2): the whole-window cross-checks run once, at
    // seal time, over the complete window. Every consumption below goes through this so the phase order the
    // production caller uses is the phase order the tests exercise.
    private static ReplayedMetadataLog Sealed(ReplayedMetadataLog log)
    {
        log.Seal();
        return log;
    }

    // ---------------------------------------------------------------------------------------------------
    // Rule 1 — proven coverage: an observation is consumable ONLY inside the contiguous window the replay
    // actually recorded. "Not covered" must be reported as such (caller reads disk), never as "nothing here".
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void VersionBelowTheReplayWindow_IsReportedNotCovered_SoTheCallerReadsItFromDisk()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta("t");
        log.Record(21, None, m, m);
        log.Record(22, None, m, m);

        // Version 1 survives below a compacting checkpoint the replay seeded from: the replay never touched it.
        Assert.False(Sealed(log).TryGetProvenObservation(1, out IReadOnlyList<MetadataAction> observed));
        Assert.Empty(observed);
    }

    [Fact]
    public void VersionAboveTheReplayWindow_IsReportedNotCovered()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta("t");
        log.Record(21, None, m, m);

        Assert.False(Sealed(log).TryGetProvenObservation(22, out _));
    }

    [Fact]
    public void AnEmptyObserver_ReportsEverythingNotCovered_SoTheGateDegradesToAFullDiskScan()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);

        Assert.False(Sealed(log).TryGetProvenObservation(0, out _));
        Assert.False(Sealed(log).TryGetProvenObservation(50, out _));
        Assert.False(Sealed(log).TryGetProvenObservation(long.MaxValue, out _));
    }

    [Fact]
    public void AtOrAboveTheExclusiveUpperBound_IsNotRecorded_KeepingRetentionBoundedToThePreRange()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 40);
        MetadataAction m = Meta("t");
        log.Record(39, None, m, m);
        log.Record(40, None, m, m);
        log.Record(41, None, m, m);

        Assert.True(Sealed(log).TryGetProvenObservation(39, out _));
        Assert.False(Sealed(log).TryGetProvenObservation(40, out _));
        Assert.False(Sealed(log).TryGetProvenObservation(41, out _));
    }

    [Fact]
    public void ANonContiguousObservation_FailsClosed_SoTheCoveredIntervalIsNeverWiderThanWhatWasSeen()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta("t");
        log.Record(10, None, m, m);

        // Coverage is stated as an INTERVAL. If the observer ever skipped a version, the interval would claim
        // coverage of a version nothing read — the exact shape of a silently-narrowed validation set. Reject
        // it at RECORD time, where it is unambiguous, rather than reasoning about it at consumption.
        DeltaProtocolException error =
            Assert.Throws<DeltaProtocolException>(() => log.Record(12, None, m, m));

        Assert.Contains("12", error.Message, StringComparison.Ordinal);
        Assert.Contains("11", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    [Fact]
    public void TheCoveredSetIsExactlyTheContiguousIntervalObserved()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta("t");
        Assert.False(log.HasCoverage);

        log.Record(31, None, m, m);
        log.Record(32, None, m, m);
        log.Record(33, None, m, m);

        Assert.True(log.HasCoverage);
        Assert.Equal(31, log.CoveredFromInclusive);
        Assert.Equal(34, log.CoveredToExclusive);
        Assert.False(Sealed(log).TryGetProvenObservation(30, out _));
        Assert.True(Sealed(log).TryGetProvenObservation(31, out _));
        Assert.True(Sealed(log).TryGetProvenObservation(33, out _));
        Assert.False(Sealed(log).TryGetProvenObservation(34, out _));
    }

    [Fact]
    public void SilentVersionsCostNoDictionaryEntry_SoRetentionIsOMetadataRevisionsNotOCommits()
    {
        // Council R1 (architect seat, low): storing one entry per replayed version would be ~48 B x history
        // length per concurrent change-feed read. Coverage is an interval, so silence is free.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100_000);
        MetadataAction m = Meta("t");
        for (long v = 0; v < 10_000; v++)
        {
            log.Record(v, None, m, m);
        }

        Assert.Equal(0, log.RecordedObservationCount);
        Assert.Equal(0, log.CoveredFromInclusive);
        Assert.Equal(10_000, log.CoveredToExclusive);

        MetadataAction next = Meta("next");
        log.Record(10_000, new[] { next }, m, next);
        Assert.Equal(1, log.RecordedObservationCount);   // one entry per METADATA REVISION, not per commit.
    }

    [Fact]
    public void AWholesaleFailureToRecordMetadata_IsCaughtByTheWindowLineageCheck()
    {
        // The per-version check needs an entry to contradict. This models an observer that produced NO entry
        // for the version that actually carried the metaData, so only the whole-window lineage cross-check can
        // see it: the metadata the reconstruction ended on is not accounted for by anything recorded.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction before = Meta("before");
        MetadataAction after = Meta("after");
        log.Record(20, None, before, before);   // silent, no entry
        log.Record(21, None, before, after);    // the metaData the observer failed to report

        DeltaProtocolException error =
            Assert.Throws<DeltaProtocolException>(() => Sealed(log).TryGetProvenObservation(20, out _));

        Assert.Contains("20", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    [Fact]
    public void AnOmittedRevisionWhoseSuccessorRestoresAnEarlierLink_IsCaughtOnlyByTheChainedFalseWrite()
    {
        // Council R6, architect seat — a SECOND, structurally different input that unmasks the same write.
        // Here the window is three versions with a silent one in the middle, and the omission at version 12 is
        // followed by a witness whose applied result RESTORES version 10's `PrevailingAfter`. That restoration
        // is what leaves `cursor` equal to the window end at the break point, so the chain-CLOSURE conjunct is
        // satisfied and only the latched `false` rejects the window.
        //
        // Two distinct probes now unmask the write (this one and the two-version shape above). That matters
        // more than either test alone: it is what turns "masked on the fixture I had" into "not maskable", and
        // it is the evidence the earlier half-of-a-pair classification lacked.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction seed = Meta("seed");
        MetadataAction restored = Meta("restored");
        MetadataAction omitted = Meta("omitted");
        MetadataAction successor = Meta("successor");
        log.Record(10, new[] { restored }, seed, restored);
        log.Record(11, None, restored, restored);              // silent, unmoved — records nothing
        log.Record(12, new[] { successor }, omitted, restored); // arrives from a link the walk never saw

        DeltaProtocolException error =
            Assert.Throws<DeltaProtocolException>(() => Sealed(log).TryGetProvenObservation(10, out _));

        Assert.Contains("10", error.Message, StringComparison.Ordinal);
        Assert.Contains("12", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    [Fact]
    public void AnOmittedRevisionWhoseBreakPointLandsOnTheWindowEnd_IsCaughtOnlyByTheChainedFalseWrite()
    {
        // Council R5, quality seat. The `chained = false` write in the chain walk is load-bearing ON ITS OWN,
        // and this is the shape that proves it. Version 1's witness says the reconstruction was on `b` before
        // applying it, but the walk arrived carrying `a` — a revision was omitted. The walk breaks there, and
        // the break point leaves `cursor` on `a`, which IS the window's end metadata. So the chain-CLOSURE
        // conjunct is SATISFIED and cannot re-reject: the only thing that rejects this window is the write
        // that latched `chained` to false.
        //
        // An earlier revision of this file classified that write as merely half of a masking pair with the
        // `break` beside it, on the strength of a different probe where the closure DID re-reject. That was
        // "masked on the probe I had" mistaken for "maskable". Flipping the write alone, with the `break`
        // retained, accepts this window — a silently omitted revision — and turned ZERO tests red before this
        // test existed.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction arrivedOn = Meta("arrived-on");
        MetadataAction claimed = Meta("claimed");
        log.Record(0, new[] { arrivedOn }, null, arrivedOn);
        log.Record(1, new[] { claimed }, claimed, arrivedOn);   // witness disagrees with the walk's cursor

        DeltaProtocolException error =
            Assert.Throws<DeltaProtocolException>(() => Sealed(log).TryGetProvenObservation(0, out _));

        Assert.Contains("0", error.Message, StringComparison.Ordinal);
        Assert.Contains("1", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    [Fact]
    public void AMovedButSilentTrailingRevision_IsRejectedOnlyByTheEndAccountingReferenceEquals()
    {
        // Council R3, balanced seat. Every other part of the lineage check PASSES on this shape: the chain is
        // unbroken, it closes on the window end, the lineage moved, and a metaData WAS recorded. The one thing
        // that is false is the thing `endMetadataAccounted` is computed from — no recorded metaData's applied
        // result is the metadata the reconstruction actually ended on. Version 1 moved the lineage silently,
        // so the window's end metadata is corroborated by nothing.
        //
        // This is the oracle for the `ReferenceEquals` in the `endMetadataAccounted |= ...` accumulation.
        // Weakening only that half (to an always-true predicate) turned ZERO tests red before this test
        // existed: `AWholesaleFailureToRecordMetadata_...` cannot reach it, because there EVERY entry has an
        // empty metadata list and the accumulation loop `continue`s past all of them.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction recorded = Meta("recorded");
        MetadataAction endedOn = Meta("ended-on");
        log.Record(0, new[] { recorded }, null, recorded);   // a real metaData, but not the window's end
        log.Record(1, None, recorded, endedOn);              // lineage moves with nothing recorded to explain it

        DeltaProtocolException error =
            Assert.Throws<DeltaProtocolException>(() => Sealed(log).TryGetProvenObservation(0, out _));

        Assert.Contains("0", error.Message, StringComparison.Ordinal);
        Assert.Contains("1", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    // ---------------------------------------------------------------------------------------------------
    // Rule 2 — corroborated silence: "this version expressed no metaData" is CHECKED against the
    // reconstruction's own metadata lineage, not believed. This is the exact council-R1 fail-open.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AnUnderReportingObservation_FailsClosed_InsteadOfLettingTheVersionSkipValidation()
    {
        // The council's mutation: an observer that stores NO metaData for a version that genuinely carried one
        // (here witnessed by the replayed state's metadata changing across the version). Before this check the
        // gate believed the silence and skipped the version — a forged pre-range identity passed. Now the
        // contradiction between the observation and the snapshot the reconstruction actually built is fatal.
        //
        // NOTE (council R2, quality seat): this pins the BEHAVIOUR, not the LAYER. The window here holds a
        // single record, so the whole-window lineage check rejects it too — deleting
        // EnsureObservationMatchesReplayedState leaves this test GREEN. The layer's own oracle is
        // AnUnderReportAtANonFinalRevision_IsCaughtByThePerVersionGuard_WhichNoWholeWindowCheckSubsumes below.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        log.Record(20, None, Meta("before"), Meta("after"));

        DeltaProtocolException error =
            Assert.Throws<DeltaProtocolException>(() => Sealed(log).TryGetProvenObservation(20, out _));

        Assert.Contains("20", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    [Fact]
    public void AnOverReportingObservation_AlsoFailsClosed_SoTheCheckHoldsInBothDirections()
    {
        // As above, this pins the behaviour rather than the layer — see
        // AnOverReportAtANonFinalRevision_IsCaughtByThePerVersionGuard_SoBothDirectionsHaveTheirOwnOracle.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta("t");
        log.Record(20, new[] { Meta("phantom") }, m, m);

        DeltaProtocolException error =
            Assert.Throws<DeltaProtocolException>(() => Sealed(log).TryGetProvenObservation(20, out _));

        Assert.Contains("20", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    [Fact]
    public void CorroboratedSilence_IsAccepted_SoTheOptimizationStillApplies()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta("t");
        log.Record(20, None, m, m); // no metaData observed AND the replayed metadata did not move.

        Assert.True(Sealed(log).TryGetProvenObservation(20, out IReadOnlyList<MetadataAction> observed));
        Assert.Empty(observed);
    }

    [Fact]
    public void ACorroboratedMetadataObservation_IsYieldedInLogOrderForValidation()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction first = Meta("first");
        MetadataAction second = Meta("second");
        log.Record(20, new[] { first, second }, Meta("before"), second);

        Assert.True(Sealed(log).TryGetProvenObservation(20, out IReadOnlyList<MetadataAction> observed));
        Assert.Equal(new[] { first, second }, observed);
    }

    [Fact]
    public void TheCreationCommit_IsCorroboratedByMetadataAppearingFromNothing()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta("t");
        log.Record(0, new[] { m }, null, m);

        Assert.True(Sealed(log).TryGetProvenObservation(0, out IReadOnlyList<MetadataAction> observed));
        Assert.Single(observed);
    }

    [Fact]
    public void SilenceClaimedWhereMetadataAppearedFromNothing_FailsClosed()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        log.Record(0, None, null, Meta("t"));

        Assert.Throws<DeltaProtocolException>(() => Sealed(log).TryGetProvenObservation(0, out _));
    }

    // ---------------------------------------------------------------------------------------------------
    // Rule 2, DISCRIMINATING cases (council R2, quality seat). The three tests above are honest end-behaviour
    // tests, but each records a single version, so the WHOLE-WINDOW lineage check rejects them as well:
    // deleting EnsureObservationMatchesReplayedState outright left the entire suite green. A test that passes
    // because SOME layer caught the defect does not defend THIS layer, and the per-version guard is the fix
    // for the council-R1 fail-open — the layer that most needs an oracle had none.
    //
    // The shape that isolates it is an under-report at a NON-FINAL metadata revision. The whole-window checks
    // are satisfied — the lineage moved, the recorded links chain unbroken from the window's opening metadata
    // to its closing metadata, and the final revision's applied result IS the window's end metadata — yet an
    // earlier version silently swallowed a real metaData. Only the per-version corroboration can see it.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AnUnderReportAtANonFinalRevision_IsCaughtByThePerVersionGuard_WhichNoWholeWindowCheckSubsumes()
    {
        // v10 truly applied `forged` but is reported silent; v11 reverts to `reverted`, which IS the metadata
        // the window ends on. Whole-window verdict: lineage moved AND is accounted for AND the links chain —
        // it PASSES. Without the per-version guard the forged identity at v10 would be validated away.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction original = Meta("original");
        MetadataAction forged = Meta("forged");
        MetadataAction reverted = Meta("reverted");
        log.Record(10, None, original, forged);                       // under-reported, NON-final revision
        log.Record(11, new[] { reverted }, forged, reverted);         // final revision, correctly reported

        ReplayedMetadataLog sealedLog = Sealed(log); // the whole-window checks accept this window.

        DeltaProtocolException error =
            Assert.Throws<DeltaProtocolException>(() => sealedLog.TryGetProvenObservation(10, out _));

        Assert.Contains("10", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    [Fact]
    public void AnOverReportAtANonFinalRevision_IsCaughtByThePerVersionGuard_SoBothDirectionsHaveTheirOwnOracle()
    {
        // The mirror image: v10 claims a metaData the reconstruction never applied, while v11 carries the real
        // final revision. The whole-window verdict is again satisfied, so only the per-version guard fires.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction original = Meta("original");
        MetadataAction latest = Meta("latest");
        log.Record(10, new[] { Meta("phantom") }, original, original); // over-reported, NON-final version
        log.Record(11, new[] { latest }, original, latest);            // final revision, correctly reported

        ReplayedMetadataLog sealedLog = Sealed(log);

        DeltaProtocolException error =
            Assert.Throws<DeltaProtocolException>(() => sealedLog.TryGetProvenObservation(10, out _));

        Assert.Contains("10", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    [Fact]
    public void AnUnderReportedCreationCommit_IsCaughtByThePerVersionGuard_WhenALaterRevisionClosesTheLineage()
    {
        // The creation commit (metadata appearing from nothing) at a NON-final position: v0 is reported silent
        // though it created the table's metadata, and v1 replaces it with the metadata the window ends on.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction created = Meta("created");
        MetadataAction replaced = Meta("replaced");
        log.Record(0, None, null, created);                      // under-reported creation, NON-final
        log.Record(1, new[] { replaced }, created, replaced);    // final revision, correctly reported

        ReplayedMetadataLog sealedLog = Sealed(log);

        DeltaProtocolException error =
            Assert.Throws<DeltaProtocolException>(() => sealedLog.TryGetProvenObservation(0, out _));

        Assert.Contains("0", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    [Fact]
    public void APartiallyOmittedNonFinalRevision_IsCaughtByTheLineageCHAINCheck()
    {
        // Council R2 (architect seat) — the BLOCKING fail-open, isolated. A version that genuinely applied a
        // metaData but was omitted from the sparse dictionary AND had its before/after witness equalised is
        // reported covered-and-silent, and NO per-version check can fire because there is no entry to
        // contradict. The existential "some record explains the window's end metadata" predicate is also
        // satisfied here (v12 does explain it), so only the CHAIN conjunct sees the break: v12's
        // PrevailingBefore is m1, but the chain from the window's opening metadata has only reached m0.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m0 = Meta("m0");
        MetadataAction m1 = Meta("m1");   // the revision the defective observer dropped
        MetadataAction m2 = Meta("m2");
        log.Record(10, None, m0, m0);
        log.Record(11, None, m0, m0);           // truly applied m1; dropped, witness equalised
        log.Record(12, new[] { m2 }, m1, m2);

        DeltaProtocolException error = Assert.Throws<DeltaProtocolException>(log.Seal);

        Assert.Contains("10", error.Message, StringComparison.Ordinal);
        Assert.Contains("12", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    [Fact]
    public void ATrailingSilentVersionWhoseWitnessContradictsTheChain_IsCaughtOnlyByTheChainCLOSURE()
    {
        // Council R2 (security seat), mutant M3. The chain conjunct has TWO parts: each recorded link must
        // continue from the previous one, and the chain must CLOSE on the metadata the window ends on. The
        // closure half was load-bearing but pinned by nothing — neutering it alone left the whole suite green.
        //
        // The shape it uniquely rejects: every RECORDED link chains perfectly, but a trailing version that is
        // silent AND state-unchanged (so it earns no dictionary entry, and no per-version check can fire)
        // carries a witness contradicting where the recorded chain actually ended. Here the links run
        // null -> a -> b, yet v2 witnesses the prevailing metadata as `a`, so the window ends on `a` while the
        // chain ends on `b`. Only the closure comparison sees the discrepancy; without it v2 is reported
        // covered-and-silent and the gate SKIPS it — a fail-open.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction a = Meta("a");
        MetadataAction b = Meta("b");
        log.Record(0, new[] { a }, null, a);
        log.Record(1, new[] { b }, a, b);
        log.Record(2, None, a, a); // silent, unmoved, unrecorded — but `a` is not where the chain ended.

        DeltaProtocolException error = Assert.Throws<DeltaProtocolException>(log.Seal);

        Assert.Contains("0", error.Message, StringComparison.Ordinal);
        Assert.Contains("2", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    [Fact]
    public void AMetadataRecordedAcrossAnUnMovedLineage_IsCaughtOnlyByTheUnmovedBranchOfTheLineageCheck()
    {
        // The fourth conjunct, found while re-verifying the disjointness claim per-conjunct rather than taking
        // it on trust: when the window's opening and closing metadata are the SAME INSTANCE, no metaData may
        // have been recorded anywhere in it. A Delta metaData REPLACES the prevailing metadata with the newly
        // parsed action instance, so a genuine revision — even a change-and-change-back — always leaves the
        // window ending on a DIFFERENT instance than it opened on. A window that both recorded revisions and
        // ended on the very instance it started with therefore cannot have come from a real replay.
        //
        // Every other conjunct is satisfied here: the links chain (a -> b -> a) and the chain closes on the
        // window's end. Only `!anyMetadataRecorded` rejects it.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction a = Meta("a");
        MetadataAction b = Meta("b");
        log.Record(10, new[] { b }, a, b);
        log.Record(11, new[] { a }, b, a); // reverts to the SAME instance the window opened on.

        DeltaProtocolException error = Assert.Throws<DeltaProtocolException>(log.Seal);

        Assert.Contains("10", error.Message, StringComparison.Ordinal);
        Assert.Contains("11", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    [Fact]
    public void AnUnbrokenChainOfSeveralRevisions_IsAccepted_SoTheChainCheckIsNotAFalsePositive()
    {
        // The chain conjunct must not reject a legitimate history that changes metadata repeatedly — including
        // a change-and-change-back, where each revert is a NEWLY parsed instance.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m0 = Meta("m0");
        MetadataAction m1 = Meta("m1");
        MetadataAction m2 = Meta("m2");
        log.Record(10, None, m0, m0);
        log.Record(11, new[] { m1 }, m0, m1);
        log.Record(12, None, m1, m1);
        log.Record(13, new[] { m2 }, m1, m2);
        log.Record(14, None, m2, m2);
        log.Seal();

        Assert.Equal(2, log.RecordedObservationCount);
        Assert.True(log.TryGetProvenObservation(12, out IReadOnlyList<MetadataAction> silent));
        Assert.Empty(silent);
        Assert.True(log.TryGetProvenObservation(13, out IReadOnlyList<MetadataAction> revision));
        Assert.Equal(new[] { m2 }, revision);
    }

    // ---------------------------------------------------------------------------------------------------
    // Observation and consumption are separate PHASES, enforced by the type (council R2).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ConsumingBeforeSealing_FailsClosed_BecauseTheWholeWindowChecksHaveNotRunOverTheWholeWindow()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta("t");
        log.Record(10, None, m, m);

        Assert.Throws<DeltaProtocolException>(() => log.TryGetProvenObservation(10, out _));
    }

    [Fact]
    public void AFailedSealDoesNotPresentAsSealed_SoAThrownVerdictIsNeverMistakenForAPassedOne()
    {
        // Council R2 (security seat, follow-up). Seal() marks the window sealed only AFTER the whole-window
        // cross-checks pass. With the flag set first, a FAILED seal left the window looking sealed: a retried
        // Seal returned silently instead of re-throwing, and TryGetProvenObservation's pre-seal guard admitted
        // observations from a window whose lineage check had THROWN — a gate whose failure path marks itself
        // validated, which is the exact defect class the sealing primitive exists to prevent.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m0 = Meta("m0");
        MetadataAction m1 = Meta("m1");
        MetadataAction m5 = Meta("m5");
        MetadataAction m6 = Meta("m6");
        MetadataAction m7 = Meta("m7");

        // The window is deliberately made to violate the chain-LINK, chain-CLOSURE and end-ACCOUNTING
        // conjuncts simultaneously, so that neutering any ONE of them still leaves the seal failing. Without
        // that, this oracle would ride along with whichever single conjunct its window happened to trip and
        // would show up as a second RED in that conjunct's mutant, blunting the per-conjunct disjointness the
        // audit above asserts. What this test pins is the ORDERING, and only the ordering.
        log.Record(10, new[] { Meta("x") }, m0, m1);
        log.Record(11, new[] { Meta("y") }, m5, m6); // link break: m5 is not where the chain had reached
        log.Record(12, None, m7, m7);                // silent + unmoved: no entry, but moves the window end

        Assert.Throws<DeltaProtocolException>(log.Seal);

        // A retry must re-run the check and fail closed again, NOT return silently. This assertion is what
        // the ordering uniquely controls.
        Assert.Throws<DeltaProtocolException>(log.Seal);

        // And consumption must still be refused, because the window was never successfully sealed. This one
        // deliberately overlaps the pre-seal guard (defence in depth): it is the security consequence of the
        // ordering defect, so it is asserted here even though ConsumingBeforeSealing_… also covers the guard.
        Assert.Throws<DeltaProtocolException>(() => log.TryGetProvenObservation(11, out _));
    }

    [Fact]
    public void ObservingAfterSealing_FailsClosed_SoAWholeWindowVerdictIsNeverReusedOverAWiderWindow()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta("t");
        log.Record(10, None, m, m);
        log.Seal();

        DeltaProtocolException error =
            Assert.Throws<DeltaProtocolException>(() => log.Record(11, None, m, m));

        Assert.Contains("11", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    // ---------------------------------------------------------------------------------------------------
    // The single extraction site both the observed and the disk-read paths go through.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void MetadataActionsOf_ExtractsEveryMetadataInLogOrder_AndNothingElse()
    {
        MetadataAction first = Meta("first");
        MetadataAction second = Meta("second");
        var actions = new DeltaAction[]
        {
            new ProtocolAction(1, 2, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty),
            first,
            new CommitInfoAction(ImmutableSortedDictionary<string, string>.Empty),
            second,
        };

        Assert.Equal(new[] { first, second }, ReplayedMetadataLog.MetadataActionsOf(actions));
        Assert.Empty(ReplayedMetadataLog.MetadataActionsOf(Array.Empty<DeltaAction>()));
    }

    [Fact]
    public void Observe_ExtractsTheSameMetadataTheDiskPathWouldSee()
    {
        MetadataAction m = Meta("t");
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        log.Observe(7, new DeltaAction[] { new ProtocolAction(1, 2, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty), m }, Meta("before"), m);

        Assert.True(Sealed(log).TryGetProvenObservation(7, out IReadOnlyList<MetadataAction> observed));
        Assert.Equal(new[] { m }, observed);
    }

    // ---------------------------------------------------------------------------------------------------
    // IDENTITY-vs-VALUE probes (council R11). MetadataAction is a `sealed record`, so `==` is VALUE equality
    // and compiles wherever ReferenceEquals does — every identity check in the lineage cross-check therefore
    // has a STRICTNESS mutation point distinct from its subject, and all six were 0-red. The whole suite used
    // one instance per distinct metadata, so identity and value never disagreed. These probes make them
    // disagree: `Dup()` returns a SECOND instance structurally equal to the first. Each is single-point
    // against the identity check named in its title, and each fails OPEN under `==` (accepts a history the
    // identity check rejects), which is why they are defects rather than residuals.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>A second, DISTINCT <see cref="MetadataAction"/> instance that is structurally equal to
    /// <see cref="Meta"/>'s — identical under <c>==</c>, different under <c>ReferenceEquals</c>. The
    /// reconstruction produces exactly this whenever two commits carry byte-identical <c>metaData</c>.</summary>
    private static MetadataAction Dup(string id) => Meta(id);

    /// <summary>Chain CLOSURE (<c>chained &amp;&amp; ReferenceEquals(cursor, _lineageAtWindowEnd)</c>). A
    /// trailing SILENT record whose witness is unmoved is not stored, so it advances the window end without
    /// advancing the cursor — and here the cursor lands on a value-equal TWIN of the end, not the end.</summary>
    [Fact]
    public void ATrailingSilentVersionWhoseWitnessIsAValueEqualTwin_StillBreaksTheChainCLOSURE()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction x = Meta("dup");
        MetadataAction twin = Dup("dup");
        log.Record(0, new[] { x }, null, x);
        log.Record(1, new[] { twin }, x, twin);
        log.Record(2, None, x, x);              // silent + unmoved: not stored, but moves the window end to x

        DeltaProtocolException error = Assert.Throws<DeltaProtocolException>(log.Seal);
        AssertPathFree(error.Message);
    }

    /// <summary>Chain LINK (<c>!ReferenceEquals(link.PrevailingBefore, cursor)</c>). The omitted revision's
    /// successor witnesses a value-equal TWIN of the cursor rather than the cursor itself.</summary>
    [Fact]
    public void ARevisionWitnessingAValueEqualTwinOfTheCursor_StillBreaksTheChainLINK()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction a = Meta("dup");
        MetadataAction twin = Dup("dup");
        log.Record(0, new[] { a }, null, a);
        log.Record(1, new[] { Meta("b") }, twin, Meta("b"));

        DeltaProtocolException error = Assert.Throws<DeltaProtocolException>(log.Seal);
        AssertPathFree(error.Message);
    }

    /// <summary>END ACCOUNTING (<c>ReferenceEquals(entry.PrevailingAfter, _lineageAtWindowEnd)</c>). The only
    /// metadata record explains a value-equal TWIN of the window end, not the window end.</summary>
    [Fact]
    public void AMetadataRecordExplainingOnlyAValueEqualTwinOfTheEnd_DoesNotACCOUNTForIt()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction a = Meta("dup");
        log.Record(0, new[] { a }, null, a);
        log.Record(1, None, a, Dup("dup"));     // silent, but the witness moves to a twin

        DeltaProtocolException error = Assert.Throws<DeltaProtocolException>(log.Seal);
        AssertPathFree(error.Message);
    }

    /// <summary>LINEAGE MOVED (<c>!ReferenceEquals(_lineageAtWindowStart, _lineageAtWindowEnd)</c>). The
    /// lineage moves to a value-equal TWIN, which selects the accounting branch, not the silent branch.</summary>
    [Fact]
    public void ALineageThatMovesToAValueEqualTwin_StillCountsAsHavingMOVED()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        log.Record(0, None, Meta("dup"), Dup("dup"));

        DeltaProtocolException error = Assert.Throws<DeltaProtocolException>(log.Seal);
        AssertPathFree(error.Message);
    }

    /// <summary>Per-version CORROBORATION (<c>!ReferenceEquals(prevailingBefore, prevailingAfter)</c>). The
    /// under-reporting observer this guard exists to catch, hiding behind a value-equal twin: the
    /// reconstruction replaced the instance, the observer recorded no <c>metaData</c>.</summary>
    [Fact]
    public void AnObserverSilentWhileTheStateMovedToAValueEqualTwin_StillContradictsTheReplayedState()
    {
        MetadataAction before = Meta("dup");

        DeltaProtocolException error = Assert.Throws<DeltaProtocolException>(
            () => ReplayedMetadataLog.EnsureObservationMatchesReplayedState(7, None, before, Dup("dup")));

        Assert.Contains("7", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    /// <summary>STORAGE rule (<c>metadata.Count > 0 || !ReferenceEquals(prevailingBefore, prevailingAfter)</c>)
    /// — the sparse dictionary's admission test. A silent record whose witness moves to a value-equal TWIN is
    /// still a state move and must still be STORED, because it is a link the chain walk needs. Under value
    /// equality it is dropped, the chain loses that link, and the next record's witness no longer matches the
    /// cursor. Unlike the other five this mutation fails CLOSED, so it is pinned by a history that must be
    /// ACCEPTED rather than one that must be rejected.</summary>
    [Fact]
    public void ASilentRecordWhoseWitnessMovesToAValueEqualTwin_IsStillSTOREDAsAChainLink()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction a = Meta("dup");
        MetadataAction twin = Dup("dup");
        MetadataAction b = Meta("b");
        MetadataAction c = Meta("c");
        log.Record(0, new[] { a }, null, a);
        log.Record(1, None, a, twin);        // silent, witness moves to a twin — dropped if `==` is used
        log.Record(2, None, twin, b);        // its `before` is the twin, so the chain needs v1's link
        log.Record(3, new[] { c }, b, c);

        log.Seal();                          // must be ACCEPTED: the chain is unbroken and closes on c

        // v1 itself is deliberately a silent-while-moved record, so CONSUMING it is a per-version
        // contradiction by design; the storage rule is observed through the chain it keeps intact.
        Assert.True(log.TryGetProvenObservation(3, out IReadOnlyList<MetadataAction> observed));
        Assert.Equal(new[] { c }, observed.ToArray());
    }

    /// <summary>
    /// Pins the END-ACCOUNTING conjunct's ACCUMULATION (<c>|=</c>), which is a DIFFERENT mutation point from
    /// its <c>ReferenceEquals</c> predicate — the predicate was already pinned at 1 red, and the operator was
    /// not (<c>|=</c> -> <c>=</c> was 0 red; council R10 gate).
    ///
    /// <para>The probe has to make the window-end match NOT the last metadata-carrying record, which the
    /// CHAIN CLOSURE conjunct otherwise forces: closure requires the walk to end on the window's end
    /// metadata, so under a closing chain the final link normally IS the match. The way through is a trailing
    /// SILENT record whose witness returns the lineage to an instance an EARLIER record produced — legal to
    /// the existential predicate ("some record explains the end metadata" — v0 does), legal to the chain
    /// (every link's before matches the cursor, and the walk closes on the end), and fatal to <c>=</c>, which
    /// keeps only the last metadata record's verdict and so discards v0's match.</para>
    /// </summary>
    [Fact]
    public void AnEarlierRecordExplainsTheWindowEnd_SoTheEndAccountingMustACCUMULATE_NotOverwrite()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction end = Meta("end");    // produced at v0, and prevailing again at the window's end
        MetadataAction mid = Meta("mid");    // produced at v1 — the LAST metadata record, and not the match
        log.Record(0, new[] { end }, null, end);
        log.Record(1, new[] { mid }, end, mid);
        log.Record(2, None, mid, end);       // silent, but the lineage returns to what v0 produced

        log.Seal();

        // Sealed, so the window is consumable: the accumulation accepted a history the overwrite rejects.
        Assert.True(log.TryGetProvenObservation(0, out IReadOnlyList<MetadataAction> observed));
        Assert.Equal(new[] { end }, observed);
    }

    /// <summary>
    /// Audit for the <c>DO NOT DELETE `!HasCoverage`</c> block in <see cref="ReplayedMetadataLog"/>. That block
    /// justifies keeping a redundant-looking disjunct by citing measured kill sets, and names the three tests
    /// that a mutant of it kills once the upper bound is broken too. Those numbers are PROSE and cannot execute
    /// — but the test NAMES can, and a rename or deletion is the likeliest way the block goes quietly stale.
    ///
    /// <para>This pins the names only. It does NOT re-measure the counts: applying the mutations requires
    /// editing source, which a test cannot do. So the block also carries a re-measured-at marker, and this
    /// audit narrows the silent-drift surface to the counts alone rather than closing it.</para>
    /// </summary>
    [Fact]
    public void TheHasCoverageAuditBlockNamesThreeTestsThatAllStillExist()
    {
        (Type Type, string Method)[] cited =
        {
            (typeof(ReplayedMetadataLogTests),
                nameof(AnEmptyObserver_ReportsEverythingNotCovered_SoTheGateDegradesToAFullDiskScan)),
            (typeof(Reading.ChangeFeedReadIoBudgetTests),
                "Cdf_PreRangeIdentityGate_StillReadsASurvivingSubFloorCommitTheReplayCannotReach"),
            (typeof(Reading.ChangeFeedReadTests),
                "Cdf_SurvivingSubFloorCommitIdentityDiffers_FailsClosed_NamesSubFloorVersion"),
        };

        foreach ((Type type, string method) in cited)
        {
            Assert.True(
                type.GetMethod(method) is not null,
                $"The !HasCoverage audit block cites {type.Name}.{method}, which no longer exists. "
                + "Re-measure the block's kill sets and update it — do not just fix this name.");
        }
    }

    // #653 hygiene: a fail-closed message names ONLY a version — never a path, column name, or physical name.
    private static void AssertPathFree(string message)
    {
        Assert.DoesNotContain("_delta_log", message, StringComparison.Ordinal);
        Assert.DoesNotContain(".json", message, StringComparison.Ordinal);
        Assert.DoesNotContain("/", message, StringComparison.Ordinal);
        Assert.DoesNotContain("col-", message, StringComparison.Ordinal);
    }
}
