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
        Assert.False(log.TryGetProvenObservation(1, out IReadOnlyList<MetadataAction> observed));
        Assert.Empty(observed);
    }

    [Fact]
    public void VersionAboveTheReplayWindow_IsReportedNotCovered()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta("t");
        log.Record(21, None, m, m);

        Assert.False(log.TryGetProvenObservation(22, out _));
    }

    [Fact]
    public void AnEmptyObserver_ReportsEverythingNotCovered_SoTheGateDegradesToAFullDiskScan()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);

        Assert.False(log.TryGetProvenObservation(0, out _));
        Assert.False(log.TryGetProvenObservation(50, out _));
        Assert.False(log.TryGetProvenObservation(long.MaxValue, out _));
    }

    [Fact]
    public void AtOrAboveTheExclusiveUpperBound_IsNotRecorded_KeepingRetentionBoundedToThePreRange()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 40);
        MetadataAction m = Meta("t");
        log.Record(39, None, m, m);
        log.Record(40, None, m, m);
        log.Record(41, None, m, m);

        Assert.True(log.TryGetProvenObservation(39, out _));
        Assert.False(log.TryGetProvenObservation(40, out _));
        Assert.False(log.TryGetProvenObservation(41, out _));
    }

    [Fact]
    public void AHoleInsideTheClaimedWindow_FailsClosed_RatherThanSkipTheVersion()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta("t");
        log.Record(10, None, m, m);
        log.Record(12, None, m, m);

        // The replay is contiguous by construction, so a missing 11 inside [10, 12] is a broken invariant in
        // the observing seam. Skipping it would silently shrink the validation set.
        DeltaProtocolException error =
            Assert.Throws<DeltaProtocolException>(() => log.TryGetProvenObservation(11, out _));

        Assert.Contains("11", error.Message, StringComparison.Ordinal);
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
            Assert.Throws<DeltaProtocolException>(() => log.TryGetProvenObservation(20, out _));

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
            Assert.Throws<DeltaProtocolException>(() => log.TryGetProvenObservation(20, out _));

        Assert.Contains("20", error.Message, StringComparison.Ordinal);
        AssertPathFree(error.Message);
    }

    [Fact]
    public void CorroboratedSilence_IsAccepted_SoTheOptimizationStillApplies()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta("t");
        log.Record(20, None, m, m); // no metaData observed AND the replayed metadata did not move.

        Assert.True(log.TryGetProvenObservation(20, out IReadOnlyList<MetadataAction> observed));
        Assert.Empty(observed);
    }

    [Fact]
    public void ACorroboratedMetadataObservation_IsYieldedInLogOrderForValidation()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction first = Meta("first");
        MetadataAction second = Meta("second");
        log.Record(20, new[] { first, second }, Meta("before"), second);

        Assert.True(log.TryGetProvenObservation(20, out IReadOnlyList<MetadataAction> observed));
        Assert.Equal(new[] { first, second }, observed);
    }

    [Fact]
    public void TheCreationCommit_IsCorroboratedByMetadataAppearingFromNothing()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta("t");
        log.Record(0, new[] { m }, null, m);

        Assert.True(log.TryGetProvenObservation(0, out IReadOnlyList<MetadataAction> observed));
        Assert.Single(observed);
    }

    [Fact]
    public void SilenceClaimedWhereMetadataAppearedFromNothing_FailsClosed()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        log.Record(0, None, null, Meta("t"));

        Assert.Throws<DeltaProtocolException>(() => log.TryGetProvenObservation(0, out _));
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

        Assert.True(log.TryGetProvenObservation(7, out IReadOnlyList<MetadataAction> observed));
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
