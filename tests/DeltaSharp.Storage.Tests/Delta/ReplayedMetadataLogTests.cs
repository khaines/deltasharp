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
