using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// Determinism: the structural FNV-1a hash is process-independent, so a fixed plan hashes to a
/// fixed, pinned value across runs/machines/processes; and deep trees (within the depth limit)
/// transform, hash, and compare correctly.
/// </summary>
public sealed class DeterminismTests
{
    // Golden hash of PlanFixtures.SamplePlan(). FNV-1a is not process-randomized, so this is a
    // cross-process/golden assertion: a change here means the hashing contract changed.
    private const int SamplePlanGoldenHash = 1066055212;

    [Fact]
    public void SamplePlanHashMatchesPinnedGolden()
    {
        Assert.Equal(SamplePlanGoldenHash, PlanFixtures.SamplePlan().GetHashCode());
    }

    [Fact]
    public void DeepChainTransformsHashesAndComparesWithinDepthLimit()
    {
        const int Depth = 900;
        LogicalPlan a = BuildChain(Depth);
        LogicalPlan b = BuildChain(Depth);

        Assert.Equal(Depth, a.Depth);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // A no-op deep transform returns the same root reference (structural sharing).
        Assert.Same(a, a.TransformUp(n => n));

        // A leaf rewrite rebuilds only the spine and changes equality.
        LogicalPlan rewritten = a.TransformUp(n =>
            n is UnresolvedRelation ? new UnresolvedRelation(new[] { "renamed" }) : n);
        Assert.NotEqual(a, rewritten);
        Assert.Equal(Depth, rewritten.Depth);
    }

    private static LogicalPlan BuildChain(int depth)
    {
        LogicalPlan plan = PlanFixtures.Relation("t");
        for (int i = 1; i < depth; i++)
        {
            plan = new Limit(i, plan);
        }

        return plan;
    }
}
