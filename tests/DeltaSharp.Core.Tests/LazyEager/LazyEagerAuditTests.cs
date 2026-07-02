using DeltaSharp.Core.Tests.Plans;
using DeltaSharp.Diagnostics;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.LazyEager;

/// <summary>
/// Lazy/eager regression tests for STORY-04.4.3 (#169): they prove DeltaSharp's central invariant —
/// <b>transformations are lazy, actions are eager</b> — by observing the internal
/// <see cref="IExecutionAudit"/> seam.
/// </summary>
/// <remarks>
/// <para>
/// These tests exercise what exists <b>today</b>: constructing the immutable logical IR
/// (<see cref="UnresolvedRelation"/> / <see cref="Project"/> / <see cref="Filter"/>) and wrapping it
/// in a <see cref="DataFrame"/>. That is the plan-construction path every future transformation
/// (<c>Select</c>/<c>Filter</c>/…, STORY-04.1.x / #160) must follow: build plan nodes, notify no
/// audit sink. AC1 and AC2 are therefore <b>fully realized now</b> and stand as the standing guard
/// that #160's transformations stay lazy.
/// </para>
/// <para>
/// The action path (an action such as <c>Collect</c>/<c>count</c>, STORY-04.2.x / #173) does not exist
/// yet, so AC3 is delivered as the observability <b>substrate</b> plus a contract test: it drives the
/// audit seam directly (and through <see cref="FakeExecutionBackend"/>) to prove the expected
/// analyzer → planner → backend path is recordable. When #173 lands, the real action wires into this
/// exact seam (<see cref="ExecutionAudit"/>) and the AC3 contract test is upgraded to drive the real
/// action end-to-end. AC4's regression guard is the non-vacuous complement: it proves that if a
/// transformation ever touched the seam, these zero-counter assertions would fail.
/// </para>
/// </remarks>
public sealed class LazyEagerAuditTests
{
    // ----- AC1: fake source records opens/reads; transformations-only leaves counters at zero -----

    [Fact]
    public void TransformationsOnly_LeaveFileAndRowCountersAtZero()
    {
        var recording = new RecordingAudit();
        var source = new FakeSource("people", rowCount: 42);

        using (ExecutionAudit.BeginScope(recording))
        {
            // Everything below is plan construction — the lazy half of the invariant. A real reader
            // (#158) only touches the audit seam from its eager scan (FakeSource.Read), never from
            // Describe(); future DataFrame transformations (#160) build these same nodes.
            UnresolvedRelation relation = source.Describe();
            var filtered = new Filter(
                new UnresolvedFunction(">", new Expression[]
                {
                    new UnresolvedAttribute("age"),
                    new UnresolvedAttribute("21"),
                }),
                relation);
            var projected = new Project(
                new Expression[] { new UnresolvedAttribute("name") },
                filtered);
            var frame = new DataFrame(projected);

            // Touch the plan the way construction-time code does; still no execution.
            Assert.Same(projected, frame.Plan);
        }

        Assert.Equal(0, recording.FilesOpened);
        Assert.Equal(0, recording.RowsRead);
        // Defense-in-depth: the strongest all-three oracle (files, rows, and stage path) holds on the
        // FakeSource construction path — nothing eager was observed.
        Assert.True(recording.ObservedNoExecution);
    }

    // ----- AC2: fake execution backend is never invoked while only transformations run -----

    [Fact]
    public void TransformationsOnly_DoNotInvokeAnyBackendStage()
    {
        var recording = new RecordingAudit();
        var source = new FakeSource("events", rowCount: 7);
        // The backend double exists but must not be reached by plan construction.
        _ = new FakeExecutionBackend();

        using (ExecutionAudit.BeginScope(recording))
        {
            LogicalPlan plan = new Project(
                new Expression[] { new UnresolvedAttribute("a"), new UnresolvedAttribute("b") },
                source.Describe());
            _ = new DataFrame(plan);
        }

        Assert.Empty(recording.StagePath);
        Assert.True(recording.ObservedNoExecution);
    }

    [Fact]
    public void TransformationsOnly_OverExistingPlanFixture_ObservesNoExecution()
    {
        var recording = new RecordingAudit();

        using (ExecutionAudit.BeginScope(recording))
        {
            // Reuse the shared Project-over-Filter-over-Relation fixture: still pure construction.
            LogicalPlan plan = PlanFixtures.SamplePlan();
            _ = new DataFrame(plan);
        }

        Assert.True(recording.ObservedNoExecution);
    }

    // ----- AC3 (substrate + contract): an action drives the expected analyzer/planner/backend path --

    [Fact]
    public void AuditSeam_DirectlyDriven_RecordsAnalyzerPlannerBackendPath()
    {
        // Pure-substrate contract: the seam records the ordered pipeline path an action will drive.
        // This is independent of any test double, so it is the durable contract #173 must satisfy.
        var recording = new RecordingAudit();

        using (ExecutionAudit.BeginScope(recording))
        {
            ExecutionAudit.StageEntered(ExecutionStage.Analyzer);
            ExecutionAudit.StageEntered(ExecutionStage.Planner);
            ExecutionAudit.StageEntered(ExecutionStage.Backend);
        }

        Assert.Equal(
            new[] { ExecutionStage.Analyzer, ExecutionStage.Planner, ExecutionStage.Backend },
            recording.StagePath);
    }

    [Fact]
    public void Action_ThroughFakeBackend_ObservesExpectedPathAndSourceReads()
    {
        // The FakeExecutionBackend stands in for the #173 action + #174 backend bridge: it analyzes,
        // plans, scans the source, and invokes the backend, all through the same ExecutionAudit seam
        // the real action will use. When #173 lands, replace the FakeExecutionBackend.Execute call
        // with the real action (frame.Collect()/frame.Count()) and this assertion holds unchanged.
        var recording = new RecordingAudit();
        var source = new FakeSource("orders", rowCount: 128);
        var backend = new FakeExecutionBackend();

        long rows;
        using (ExecutionAudit.BeginScope(recording))
        {
            LogicalPlan plan = new Project(
                new Expression[] { new UnresolvedAttribute("total") },
                source.Describe());

            // Nothing observed yet — the plan is built but no action has run.
            Assert.True(recording.ObservedNoExecution);

            rows = backend.Execute(plan, source);
        }

        Assert.Equal(128, rows);
        Assert.Equal(
            new[] { ExecutionStage.Analyzer, ExecutionStage.Planner, ExecutionStage.Backend },
            recording.StagePath);
        Assert.Equal(1, recording.FilesOpened);
        Assert.Equal(128, recording.RowsRead);
    }

    // ----- AC4: the regression guard genuinely fails if a transformation touches the seam -----

    [Fact]
    public void RegressionGuard_FailsWhenATransformationAccidentallyExecutes()
    {
        // Non-vacuity proof for AC1/AC2: if plan construction ever reached the audit seam (a lazy/eager
        // regression), the zero-counter assertions the other tests make would fail. Here a deliberately
        // buggy "transformation" touches the seam, and we prove the guard fires — so the passing
        // zero-counter tests above are meaningful, not vacuous.
        var recording = new RecordingAudit();
        var source = new FakeSource("leaky", rowCount: 1);

        using (ExecutionAudit.BeginScope(recording))
        {
            UnresolvedRelation relation = source.Describe();
            _ = new DataFrame(relation);

            // Simulate the regression: a transformation that wrongly performs eager work.
            AccidentallyEagerTransformation(source);
        }

        // The guard the AC1/AC2 tests rely on: any eager work during construction is caught.
        Assert.False(recording.ObservedNoExecution);
        Assert.True(recording.FilesOpened > 0);
        Assert.True(recording.RowsRead > 0);
    }

    // ----- seam hygiene: forwarders are safe no-ops when no sink is installed -----

    [Fact]
    public void Forwarders_WithNoSinkInstalled_AreNoOps()
    {
        // Outside a scope there is no current sink; the production default is zero-overhead and safe.
        Assert.Null(ExecutionAudit.Current);
        ExecutionAudit.FileOpened("none");
        ExecutionAudit.RowsRead(100);
        ExecutionAudit.StageEntered(ExecutionStage.Backend);
        Assert.Null(ExecutionAudit.Current);
    }

    [Fact]
    public void BeginScope_RestoresPreviousSinkOnDispose()
    {
        var outer = new RecordingAudit();
        var inner = new RecordingAudit();

        using (ExecutionAudit.BeginScope(outer))
        {
            Assert.Same(outer, ExecutionAudit.Current);
            using (ExecutionAudit.BeginScope(inner))
            {
                Assert.Same(inner, ExecutionAudit.Current);
            }

            Assert.Same(outer, ExecutionAudit.Current);
        }

        Assert.Null(ExecutionAudit.Current);
    }

    /// <summary>
    /// A deliberately incorrect "transformation" that performs eager source I/O. It exists only to
    /// demonstrate AC4's regression guard is non-vacuous; no production transformation may do this.
    /// </summary>
    private static void AccidentallyEagerTransformation(FakeSource source) => source.Read();
}
