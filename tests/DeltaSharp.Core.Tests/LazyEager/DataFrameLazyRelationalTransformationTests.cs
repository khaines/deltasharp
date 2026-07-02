using DeltaSharp.Diagnostics;
using Xunit;

namespace DeltaSharp.Core.Tests.LazyEager;

/// <summary>
/// The marquee lazy proof for STORY-04.2.3 (#162): chaining the relational <see cref="DataFrame"/>
/// transformations (<c>Join</c>/<c>OrderBy</c>/<c>Sort</c>/<c>Limit</c>/<c>Distinct</c>/<c>Union</c>)
/// over sources whose scans are booby-trapped triggers <b>no</b> read and <b>no</b> backend work —
/// including a <c>Join</c> with a booby-trapped source on <b>both</b> sides. It proves DeltaSharp's
/// central invariant — transformations are lazy, actions are eager (ADR-0001) — on the real relational
/// surface this story ships.
/// </summary>
public sealed class DataFrameLazyRelationalTransformationTests
{
    [Fact]
    public void JoinAndChainedRelationalOps_OverThrowOnReadSources_NeverRead()
    {
        var leftSource = new ThrowOnReadSource("left");
        var rightSource = new ThrowOnReadSource("right");
        var left = new DataFrame(leftSource.Describe());
        var right = new DataFrame(rightSource.Describe());

        // A join with a booby-trapped scan on BOTH sides, plus every relational transform on top —
        // none of these may scan either source.
        DataFrame result = left
            .Join(right, Functions.Col("id"), "left_outer")
            .Where(Functions.Col("age"))
            .OrderBy(Functions.Col("age").Desc())
            .Distinct()
            .Union(left.Limit(3))
            .Limit(5);

        Assert.NotNull(result.Plan);
        Assert.Equal(0, leftSource.ReadCount);
        Assert.Equal(0, rightSource.ReadCount);
    }

    [Fact]
    public void ChainedRelationalTransformations_TouchNoAuditSeam()
    {
        var recording = new RecordingAudit();
        var leftSource = new FakeSource("left", rowCount: 42);
        var rightSource = new FakeSource("right", rowCount: 7);

        using (ExecutionAudit.BeginScope(recording))
        {
            var left = new DataFrame(leftSource.Describe());
            var right = new DataFrame(rightSource.Describe());
            _ = left
                .Join(right, Functions.Col("id"))
                .OrderBy("id")
                .Distinct()
                .Limit(10)
                .Union(right);
        }

        // No file opened, no rows read, no analyzer/planner/backend stage entered.
        Assert.True(recording.ObservedNoExecution);
        Assert.Equal(0, recording.FilesOpened);
        Assert.Equal(0, recording.RowsRead);
        Assert.Empty(recording.StagePath);
    }
}
