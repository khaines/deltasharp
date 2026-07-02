using DeltaSharp.Analysis;
using DeltaSharp.Diagnostics;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
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

    [Fact]
    public void ChainedTransformations_NeverEnterTheAnalyzerStage()
    {
        // The guarantee "transformations don't ANALYZE" (not merely "don't READ"): with the #169
        // audit seam now emitted at Analyzer.Resolve's entry, building the Select/Filter/Where/
        // WithColumn chain must never record the Analyzer stage. An eager-analyze mutation of any
        // transform (e.g. Select calling Analyzer.Resolve) would emit ExecutionStage.Analyzer here and
        // redden this test — see EagerAnalyzeMutation_IsRecordedByTheSeam for the non-vacuity proof.
        var recording = new RecordingAudit();
        var source = new FakeSource("people", rowCount: 7);

        using (ExecutionAudit.BeginScope(recording))
        {
            var df = new DataFrame(source.Describe());
            _ = df
                .Select("id", "name", "age")
                .Filter(Functions.Col("age"))
                .Where(Functions.Col("id"))
                .Select(Functions.Col("name").As("who"))
                .WithColumn("flag", Functions.Lit(true))
                .WithColumn("name", Functions.Col("who"));
        }

        Assert.DoesNotContain(ExecutionStage.Analyzer, recording.StagePath);
        Assert.Empty(recording.StagePath);
    }

    [Fact]
    public void EagerAnalyzeMutation_IsRecordedByTheSeam()
    {
        // Non-vacuity proof for the lazy-analysis guard above: if a transform ever eagerly analyzed
        // (transform → Analyzer.Resolve), the seam WOULD observe the Analyzer stage. Here we simulate
        // that regression by invoking the real analyzer inside the audit scope and assert the stage is
        // recorded — so the passing "never enters the Analyzer stage" assertions are meaningful, not
        // vacuous.
        var recording = new RecordingAudit();
        var catalog = new LocalCatalog();
        catalog.Register("people", new StructType(new[]
        {
            new StructField("id", LongType.Instance, nullable: false),
            new StructField("name", StringType.Instance, nullable: true),
        }));
        var analyzer = new Analyzer(catalog);

        using (ExecutionAudit.BeginScope(recording))
        {
            var df = new DataFrame(new UnresolvedRelation(new[] { "people" }));
            // The forbidden move: a "transformation" that eagerly resolves. Real transforms never do
            // this; doing it must be observable so the lazy-analysis guard cannot be a false green.
            _ = analyzer.Resolve(df.Select("name").Plan);
        }

        Assert.Contains(ExecutionStage.Analyzer, recording.StagePath);
        Assert.False(recording.ObservedNoExecution);
    }
}
