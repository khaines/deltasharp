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

    internal LogicalPlan? LastPlan { get; private set; }

    public IReadOnlyList<Row> Collect(LogicalPlan analyzedPlan)
    {
        CollectCallCount++;
        LastPlan = analyzedPlan;
        DriveBackend();
        return _rows;
    }

    public long Count(LogicalPlan analyzedPlan)
    {
        CountCallCount++;
        LastPlan = analyzedPlan;
        DriveBackend();
        return _countOverride ?? _rows.Count;
    }

    private static void DriveBackend()
    {
        ExecutionAudit.StageEntered(ExecutionStage.Planner);
        ExecutionAudit.StageEntered(ExecutionStage.Backend);
    }
}
