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

    // #653 hygiene: a fail-closed message names ONLY a version — never a path, column name, or physical name.
    private static void AssertPathFree(string message)
    {
        Assert.DoesNotContain("_delta_log", message, StringComparison.Ordinal);
        Assert.DoesNotContain(".json", message, StringComparison.Ordinal);
        Assert.DoesNotContain("/", message, StringComparison.Ordinal);
        Assert.DoesNotContain("col-", message, StringComparison.Ordinal);
    }
}
