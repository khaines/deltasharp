using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// TreeNode&lt;T&gt; base contract: transform visit order, structural equality, and
/// deterministic (process-independent) hashing.
/// </summary>
public sealed class TreeNodeTests
{
    [Fact]
    public void TransformDownVisitsParentBeforeChildren()
    {
        var visited = new List<string>();
        PlanFixtures.SamplePlan().TransformDown(node =>
        {
            visited.Add(node.NodeName);
            return node;
        });

        Assert.Equal(new[] { "Project", "Filter", "UnresolvedRelation" }, visited);
    }

    [Fact]
    public void TransformUpVisitsChildrenBeforeParent()
    {
        var visited = new List<string>();
        PlanFixtures.SamplePlan().TransformUp(node =>
        {
            visited.Add(node.NodeName);
            return node;
        });

        Assert.Equal(new[] { "UnresolvedRelation", "Filter", "Project" }, visited);
    }

    [Fact]
    public void StructurallyEqualTreesAreEqualAndShareHash()
    {
        LogicalPlan a = PlanFixtures.SamplePlan();
        LogicalPlan b = PlanFixtures.SamplePlan();

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DifferingTreesAreNotEqual()
    {
        LogicalPlan a = PlanFixtures.SamplePlan();
        LogicalPlan b = new Project(
            new Expression[] { PlanFixtures.Attr("a") },
            new Filter(
                new UnresolvedFunction(">", new Expression[] { PlanFixtures.Attr("age"), PlanFixtures.Attr("21") }),
                PlanFixtures.Relation("people")));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferentNodeTypesWithSameShapeAreNotEqual()
    {
        var child = PlanFixtures.Relation("t");
        LogicalPlan distinct = new Distinct(child);
        LogicalPlan limit = new Limit(0, child);
        Assert.NotEqual(distinct, limit);
    }

    [Fact]
    public void HashIsDeterministicAcrossConstructions()
    {
        int first = PlanFixtures.SamplePlan().GetHashCode();
        int second = PlanFixtures.SamplePlan().GetHashCode();
        Assert.Equal(first, second);
    }

    [Fact]
    public void OptionsOrderDoesNotAffectEqualityOrHash()
    {
        var a = new UnresolvedRelation(
            new[] { "t" },
            new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        var b = new UnresolvedRelation(
            new[] { "t" },
            new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void JoinEqualityAccountsForTypeAndCondition()
    {
        var left = PlanFixtures.Relation("a");
        var right = PlanFixtures.Relation("b");
        var inner = new Join(left, right, JoinType.Inner);
        var leftOuter = new Join(left, right, JoinType.LeftOuter);
        var withCondition = new Join(left, right, JoinType.Inner, PlanFixtures.Attr("x"));

        Assert.NotEqual(inner, leftOuter);
        Assert.NotEqual(inner, withCondition);
        Assert.Equal(inner, new Join(
            PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner));
    }

    [Fact]
    public void ToStringRendersTheTree()
    {
        LogicalPlan plan = PlanFixtures.SamplePlan();
        Assert.Equal(plan.TreeString(), plan.ToString());
    }
}
