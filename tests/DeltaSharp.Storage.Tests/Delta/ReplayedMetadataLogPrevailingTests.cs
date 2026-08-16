using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// #809: unit tests for <see cref="ReplayedMetadataLog.TryGetProvenPrevailing"/>, the derive-prevailing
/// step-function accessor VACUUM's cdc-scan skip predicate relies on. These pin the fail-closed paths (unsealed
/// / outside coverage / inert) and the seal-degrade backstop DIRECTLY, because they are defense-in-depth guards
/// that a valid on-disk reconstruction cannot reach end-to-end (every listed commit gets a timestamp, and a
/// valid replay always produces an accountable lineage). They also pin the step-function correctness — an
/// inheriting version must report the CDF state it inherited, not "off".
/// </summary>
public sealed class ReplayedMetadataLogPrevailingTests
{
    private const string Schema =
        "{\"type\":\"struct\",\"fields\":[{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":{}}]}";

    private static MetadataAction Meta(bool cdfOn) =>
        new(
            Id: "t",
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
            SchemaString: Schema,
            PartitionColumns: ImmutableArray<string>.Empty,
            Configuration: ImmutableSortedDictionary<string, string>.Empty.Add(
                "delta.enableChangeDataFeed", cdfOn ? "true" : "false"),
            CreatedTime: null);

    private static readonly IReadOnlyList<MetadataAction> None = Array.Empty<MetadataAction>();

    private static bool CdfOn(MetadataAction? m) => m is not null && ChangeDataFeedFeature.IsEnabled(m.Configuration);

    [Fact]
    public void Unsealed_Throws()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta(cdfOn: false);
        log.Record(0, new[] { m }, null, m);

        Assert.Throws<DeltaProtocolException>(
            () => log.TryGetProvenPrevailing(0, out _, out _));
    }

    [Fact]
    public void OutsideCoverage_ReturnsFalse_NeverOff()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction m = Meta(cdfOn: true);
        log.Record(20, new[] { m }, null, m);
        log.Record(21, None, m, m);
        log.Seal();

        // Below and above the covered interval [20, 22) → not covered → false (fail-closed, NOT "off").
        Assert.False(log.TryGetProvenPrevailing(19, out _, out _));
        Assert.False(log.TryGetProvenPrevailing(22, out _, out _));
    }

    [Fact]
    public void Inert_ReturnsFalse()
    {
        var log = new ReplayedMetadataLog(exclusiveUpperBound: long.MaxValue);
        MetadataAction on = Meta(cdfOn: true);
        MetadataAction off = Meta(cdfOn: false);
        // Drive state-moving records past MaxRetainedObservations so the observer latches inert (#712).
        for (int v = 0; v <= ReplayedMetadataLog.MaxRetainedObservations; v++)
        {
            MetadataAction before = v == 0 ? off : (v % 2 == 1 ? off : on);
            MetadataAction after = v % 2 == 0 ? on : off;
            log.Record(v, new[] { after }, before, after);
        }

        Assert.True(log.IsInert);
        log.Seal();
        Assert.False(log.TryGetProvenPrevailing(0, out _, out _)); // inert → un-proven, never a stale pair.
    }

    [Fact]
    public void DerivesInheritedCdfOn_ForVersionsWithNoMetadata()
    {
        // CDF enabled at v10 (a recorded transition); v11..v13 inherit (no metaData, state unchanged) and are
        // NOT in _recorded. The accessor must carry v10's CDF-on prevailing forward to every inheriting version.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction on = Meta(cdfOn: true);
        log.Record(10, new[] { on }, null, on); // transition null → on
        log.Record(11, None, on, on);
        log.Record(12, None, on, on);
        log.Record(13, None, on, on);
        log.Seal();

        // v10 is the enabling transition: before = off (null seed), after = on.
        Assert.True(log.TryGetProvenPrevailing(10, out MetadataAction? b10, out MetadataAction? a10));
        Assert.False(CdfOn(b10));
        Assert.True(CdfOn(a10));

        // v11..v13 INHERIT CDF-on (no metaData): both boundaries must derive CDF-on, carried forward.
        foreach (long v in new long[] { 11, 12, 13 })
        {
            Assert.True(log.TryGetProvenPrevailing(v, out MetadataAction? before, out MetadataAction? after));
            Assert.True(CdfOn(before), $"prevailingBefore at v{v} should be CDF-on (inherited)");
            Assert.True(CdfOn(after), $"prevailingAfter at v{v} should be CDF-on (inherited)");
        }
    }

    [Fact]
    public void DerivesOff_BelowTheFirstRecordedEnable()
    {
        // Enable happens at v12; v10..v11 (below the enabling transition, covered) must derive CDF-OFF.
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction off = Meta(cdfOn: false);
        MetadataAction on = Meta(cdfOn: true);
        log.Record(10, new[] { off }, null, off); // seed off
        log.Record(11, None, off, off);
        log.Record(12, new[] { on }, off, on);    // enable
        log.Seal();

        Assert.True(log.TryGetProvenPrevailing(11, out MetadataAction? before11, out MetadataAction? after11));
        Assert.False(CdfOn(before11));
        Assert.False(CdfOn(after11));

        Assert.True(log.TryGetProvenPrevailing(12, out MetadataAction? before12, out MetadataAction? after12));
        Assert.False(CdfOn(before12)); // before the enable
        Assert.True(CdfOn(after12));   // after the enable
    }

    [Fact]
    public void Seal_UnaccountableLineage_Throws_TheScanDegradeBackstop()
    {
        // A broken lineage chain (a recorded version whose prevailing-before does not continue the running
        // cursor) is unaccountable → Seal throws DeltaProtocolException. This is exactly the fault DeltaVacuum's
        // predicate try/catch maps to a fail-closed SCAN (ScanSealDegraded).
        var log = new ReplayedMetadataLog(exclusiveUpperBound: 100);
        MetadataAction a = Meta(cdfOn: false);
        MetadataAction b = Meta(cdfOn: true);
        MetadataAction c = Meta(cdfOn: false);
        log.Record(10, new[] { b }, a, b); // cursor: a → b
        log.Record(11, new[] { c }, a, c); // prevailing-before `a` != cursor `b` → chain broken

        Assert.Throws<DeltaProtocolException>(() => log.Seal());
    }
}
