using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// Security/Architect: the construction-time depth guard rejects trees deeper than
/// <see cref="TreeNode{TNode}.MaxDepth"/> with a typed, fail-fast exception, bounding the
/// otherwise unbounded recursion in equality, hashing, transforms, and rendering.
/// </summary>
public sealed class DepthGuardTests
{
    private static LogicalPlan BuildChain(int depth)
    {
        // depth == number of nodes from root to leaf inclusive. Leaf relation is depth 1.
        LogicalPlan plan = PlanFixtures.Relation("t");
        for (int i = 1; i < depth; i++)
        {
            plan = new Limit(i, plan);
        }

        return plan;
    }

    [Fact]
    public void ChainAtMaxDepthSucceeds()
    {
        LogicalPlan plan = BuildChain(TreeNode<LogicalPlan>.MaxDepth);
        Assert.Equal(TreeNode<LogicalPlan>.MaxDepth, plan.Depth);
    }

    [Fact]
    public void ChainPastMaxDepthThrowsTypedException()
    {
        LogicalPlan atLimit = BuildChain(TreeNode<LogicalPlan>.MaxDepth);

        var ex = Assert.Throws<PlanDepthExceededException>(() => new Limit(0, atLimit));
        Assert.Equal(TreeNode<LogicalPlan>.MaxDepth + 1, ex.Depth);
        Assert.Equal(TreeNode<LogicalPlan>.MaxDepth, ex.MaxDepth);
    }

    [Fact]
    public void DepthIsOneForALeaf()
    {
        Assert.Equal(1, PlanFixtures.Relation("t").Depth);
        Assert.Equal(1, new UnresolvedAttribute("a").Depth);
    }

    [Fact]
    public void DepthIsOnePlusMaxChildDepth()
    {
        var leaf = PlanFixtures.Relation("t");
        var filter = new Filter(PlanFixtures.Attr("a"), leaf);
        var project = new Project(new Expression[] { PlanFixtures.Attr("a") }, filter);

        Assert.Equal(1, leaf.Depth);
        Assert.Equal(2, filter.Depth);
        Assert.Equal(3, project.Depth);
    }

    [Fact]
    public void ExpressionDepthGuardAlsoApplies()
    {
        // Nesting unresolved functions deeply throws the same typed exception.
        Expression expr = new UnresolvedAttribute("a");
        for (int i = 1; i < TreeNode<Expression>.MaxDepth; i++)
        {
            expr = new UnresolvedFunction("f", new[] { expr });
        }

        Assert.Equal(TreeNode<Expression>.MaxDepth, expr.Depth);
        Expression deep = expr;
        Assert.Throws<PlanDepthExceededException>(() => new UnresolvedFunction("f", new[] { deep }));
    }
}
