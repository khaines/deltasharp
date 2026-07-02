using DeltaSharp.Diagnostics;
using Xunit;

namespace DeltaSharp.Core.Tests.LazyEager;

/// <summary>
/// The marquee lazy proof for STORY-04.2.1 (#160): chaining the real <see cref="DataFrame"/>
/// transformations (<c>Select</c>/<c>Filter</c>/<c>Where</c>/<c>WithColumn</c>) over a source whose
/// scan is booby-trapped triggers <b>no</b> read and <b>no</b> backend work. It complements the
/// existing #169 <see cref="LazyEagerAuditTests"/> audit-seam guards by exercising the actual public
/// transformation API rather than raw plan-node construction, proving DeltaSharp's central invariant
/// — transformations are lazy, actions are eager (ADR-0001) — on the real surface this story ships.
/// </summary>
public sealed class DataFrameLazyTransformationTests
{
    [Fact]
    public void ChainedTransformations_OverThrowOnReadSource_NeverRead()
    {
        var source = new ThrowOnReadSource("people");
        var df = new DataFrame(source.Describe());

        // The full transformation chain — none of these may scan the source.
        DataFrame result = df
            .Select("id", "name", "age")
            .Filter(Functions.Col("age"))
            .Where(Functions.Col("id"))
            .Select(Functions.Col("name").As("who"))
            .WithColumn("flag", Functions.Lit(true))
            .WithColumn("name", Functions.Col("who"));

        Assert.NotNull(result.Plan);
        // The booby-trapped scan was never entered.
        Assert.Equal(0, source.ReadCount);
    }

    [Fact]
    public void ChainedTransformations_TouchNoAuditSeam()
    {
        var recording = new RecordingAudit();
        var source = new FakeSource("events", rowCount: 99);

        using (ExecutionAudit.BeginScope(recording))
        {
            var df = new DataFrame(source.Describe());
            _ = df
                .Filter(Functions.Col("age"))
                .Select("name")
                .WithColumn("doubled", Functions.Col("age"));
        }

        // No file opened, no rows read, no analyzer/planner/backend stage entered.
        Assert.True(recording.ObservedNoExecution);
        Assert.Equal(0, recording.FilesOpened);
        Assert.Equal(0, recording.RowsRead);
        Assert.Empty(recording.StagePath);
    }
}
