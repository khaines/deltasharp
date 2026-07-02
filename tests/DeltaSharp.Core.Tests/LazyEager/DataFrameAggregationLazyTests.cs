using DeltaSharp.Diagnostics;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.LazyEager;

/// <summary>
/// The marquee lazy proof for STORY-04.2.2 (#161): building the real <c>GroupBy(...).Agg(...)</c> /
/// <c>GroupBy(...).Count()</c> / global <c>Agg(...)</c> chains over a source whose scan is
/// booby-trapped triggers <b>no</b> read, <b>no</b> analyzer stage, and <b>no</b> backend work.
/// Aggregations are transformations: they only extend the immutable logical plan (ADR-0001). This
/// complements <see cref="DataFrameLazyTransformationTests"/> by exercising the aggregation surface.
/// </summary>
public sealed class DataFrameAggregationLazyTests
{
    [Fact]
    public void GroupByAgg_OverThrowOnReadSource_NeverReads()
    {
        var source = new ThrowOnReadSource("people");
        var df = new DataFrame(source.Describe());

        DataFrame result = df
            .Filter(Functions.Col("active"))
            .GroupBy("dept", "team")
            .Agg(
                Functions.Sum(Functions.Col("salary")).As("total"),
                Functions.Max(Functions.Col("salary")).As("top"));

        Assert.NotNull(result.Plan);
        Assert.Equal(0, source.ReadCount);
    }

    [Fact]
    public void GroupByCount_AndGlobalAgg_OverThrowOnReadSource_NeverRead()
    {
        var source = new ThrowOnReadSource("events");
        var df = new DataFrame(source.Describe());

        DataFrame perGroup = df.GroupBy("kind").Count();
        DataFrame global = df.Agg(Functions.Count(Functions.Col("id")).As("n"));

        Assert.NotNull(perGroup.Plan);
        Assert.NotNull(global.Plan);
        Assert.Equal(0, source.ReadCount);
    }

    [Fact]
    public void GroupByAgg_TouchesNoAuditSeam()
    {
        var recording = new RecordingAudit();
        var source = new FakeSource("people", rowCount: 42);

        using (ExecutionAudit.BeginScope(recording))
        {
            var df = new DataFrame(source.Describe());
            _ = df
                .GroupBy("dept")
                .Agg(Functions.Sum(Functions.Col("salary")).As("total"));
        }

        // No file opened, no rows read, no analyzer/planner/backend stage entered.
        Assert.True(recording.ObservedNoExecution);
        Assert.Equal(0, recording.FilesOpened);
        Assert.Equal(0, recording.RowsRead);
        Assert.Empty(recording.StagePath);
    }
}
