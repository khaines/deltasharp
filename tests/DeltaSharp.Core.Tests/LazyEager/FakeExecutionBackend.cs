using DeltaSharp.Diagnostics;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Core.Tests.LazyEager;

/// <summary>
/// A test double for the execution backend bridge (#173/#174) that records the analyzer → planner →
/// backend invocation path through the ambient <see cref="IExecutionAudit"/> sink. It is invoked by
/// nothing until an <b>action</b> asks it to <see cref="Execute"/>: building a logical plan never
/// reaches it, which is what the AC2 lazy test asserts. <see cref="Execute"/> drives the exact
/// substrate a real action (#173) will run — analyze, plan, scan the source, invoke the backend — so
/// AC3 can assert the observed path.
/// </summary>
internal sealed class FakeExecutionBackend
{
    /// <summary>
    /// Runs the eager pipeline for <paramref name="plan"/> against <paramref name="source"/>, recording
    /// each milestone. This is the only method that notifies the audit sink; a transformation never
    /// calls it.
    /// </summary>
    /// <param name="plan">The logical plan to execute (the root of the transformation tree).</param>
    /// <param name="source">The source whose eager scan feeds execution.</param>
    /// <returns>The number of rows produced by the scan.</returns>
    public long Execute(LogicalPlan plan, FakeSource source)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);

        ExecutionAudit.StageEntered(ExecutionStage.Analyzer);
        ExecutionAudit.StageEntered(ExecutionStage.Planner);
        long rows = source.Read();
        ExecutionAudit.StageEntered(ExecutionStage.Backend);
        return rows;
    }
}
