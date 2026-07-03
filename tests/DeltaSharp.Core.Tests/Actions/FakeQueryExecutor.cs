using System.Collections.Generic;
using DeltaSharp.Diagnostics;
using DeltaSharp.Execution;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Core.Tests.Actions;

/// <summary>
/// A test double for the executor lane's <see cref="IQueryExecutor"/> (STORY-04.6.2 / #174). It
/// returns <b>canned</b> rows / count without doing physical planning, so <c>DeltaSharp.Core</c>'s
/// action pipeline (analyze → optimize → execute → materialize/format) can be exercised in isolation.
/// It records how many times each method was called and the plan it received, and drives the
/// <see cref="ExecutionAudit"/> seam's <see cref="ExecutionStage.Planner"/>/<see cref="ExecutionStage.Backend"/>
/// milestones exactly as the real backend bridge will — so the lazy/eager tests can assert the full
/// analyzer → planner → backend path an action produces.
/// </summary>
internal sealed class FakeQueryExecutor : IQueryExecutor
{
    private readonly IReadOnlyList<Row> _rows;
    private readonly long? _countOverride;

    internal FakeQueryExecutor(IReadOnlyList<Row> rows, long? countOverride = null)
    {
        _rows = rows;
        _countOverride = countOverride;
    }

    internal int CollectCallCount { get; private set; }

    internal int CountCallCount { get; private set; }

    internal int ExplainPhysicalCallCount { get; private set; }

    internal LogicalPlan? LastPlan { get; private set; }

    /// <summary>The <see cref="ExecutionOptions"/> the most recent action threaded across the seam.</summary>
    internal ExecutionOptions? LastOptions { get; private set; }

    /// <summary>When set, published into <c>options.Metrics</c> so the out-metrics overloads can be exercised.</summary>
    internal ExecutionMetrics? MetricsToPublish { get; set; }

    public IReadOnlyList<Row> Collect(LogicalPlan analyzedPlan, ExecutionOptions options)
    {
        CollectCallCount++;
        LastPlan = analyzedPlan;
        LastOptions = options;
        options.CancellationToken.ThrowIfCancellationRequested();
        if (MetricsToPublish is not null)
        {
            options.Metrics = MetricsToPublish;
        }

        DriveBackend();
        return _rows;
    }

    public long Count(LogicalPlan analyzedPlan, ExecutionOptions options)
    {
        CountCallCount++;
        LastPlan = analyzedPlan;
        LastOptions = options;
        options.CancellationToken.ThrowIfCancellationRequested();
        if (MetricsToPublish is not null)
        {
            options.Metrics = MetricsToPublish;
        }

        DriveBackend();
        return _countOverride ?? _rows.Count;
    }

    /// <summary>
    /// Returns a canned physical-plan string for <c>DataFrame.Explain</c>'s physical section, recording
    /// the call and the plan. It deliberately does <b>not</b> drive the backend audit stages — EXPLAIN
    /// plans but never executes (STORY-04.7.3, lazy/eager), so the lazy/eager tests can assert that
    /// Explain touched neither <see cref="Collect"/> nor <see cref="Count"/> and left the audit empty.
    /// </summary>
    public string ExplainPhysical(LogicalPlan analyzedPlan)
    {
        ExplainPhysicalCallCount++;
        LastPlan = analyzedPlan;
        return FakePhysicalPlanText;
    }

    /// <summary>The stub physical-plan text this fake renders so Core tests can assert the physical
    /// section is present without a real physical planner (which lives in DeltaSharp.Executor).</summary>
    internal const string FakePhysicalPlanText = "FakePhysicalPlan [stub]";

    private static void DriveBackend()
    {
        ExecutionAudit.StageEntered(ExecutionStage.Planner);
        ExecutionAudit.StageEntered(ExecutionStage.Backend);
    }
}
