using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// AC2: a transform produces a new plan while the original's reference and content are
/// unchanged, and untouched subtrees are shared by reference (structural sharing).
/// </summary>
public sealed class StructuralSharingTests
{
    [Fact]
    public void TransformUpRewritesLeafAndLeavesOriginalUnchanged()
    {
        LogicalPlan original = PlanFixtures.SamplePlan();
        LogicalPlan snapshot = PlanFixtures.SamplePlan();

        LogicalPlan rewritten = original.TransformUp(node =>
            node is UnresolvedRelation ? new UnresolvedRelation(new[] { "renamed" }) : node);

        // Original is structurally unchanged.
        Assert.Equal(snapshot, original);
        // A new root was produced.
        Assert.NotSame(original, rewritten);
        Assert.NotEqual(original, rewritten);
        // The rewrite reached the leaf.
        var newRelation = (UnresolvedRelation)((Filter)((Project)rewritten).Child).Child;
        Assert.Equal("renamed", newRelation.Identifier[0]);
    }

    [Fact]
    public void MapChildrenSharesUntouchedSubtreesByReference()
    {
        var leftRelation = PlanFixtures.Relation("left");
        var rightRelation = PlanFixtures.Relation("right");
        var union = new Union(new LogicalPlan[] { leftRelation, rightRelation });

        LogicalPlan rewritten = union.MapChildren(child =>
            child == leftRelation ? new UnresolvedRelation(new[] { "newleft" }) : child);

        var newUnion = (Union)rewritten;
        // The untouched right input is the very same instance.
        Assert.Same(rightRelation, newUnion.Inputs[1]);
        Assert.NotSame(leftRelation, newUnion.Inputs[0]);
    }

    [Fact]
    public void MapChildrenWithNoOpReturnsSameReference()
    {
        LogicalPlan plan = PlanFixtures.SamplePlan();
        LogicalPlan result = plan.MapChildren(child => child);
        Assert.Same(plan, result);
    }

    [Fact]
    public void TransformUpWithNoMatchReturnsSameRootReference()
    {
        LogicalPlan plan = PlanFixtures.SamplePlan();
        LogicalPlan result = plan.TransformUp(node => node);
        Assert.Same(plan, result);
    }

    [Fact]
    public void WithNewChildrenBuildsNewNodeAndPreservesOwnState()
    {
        var project = new Project(
            new Expression[] { PlanFixtures.Attr("a") }, PlanFixtures.Relation("t"));
        var newChild = PlanFixtures.Relation("u");

        var rebuilt = (Project)project.WithNewChildren(new LogicalPlan[] { newChild });

        Assert.NotSame(project, rebuilt);
        Assert.Same(newChild, rebuilt.Child);
        Assert.Equal(project.ProjectList, rebuilt.ProjectList);
    }

    [Fact]
    public void DerivingNewDataFrameLeavesOriginalPlanUnchanged()
    {
        var relation = PlanFixtures.Relation("people");
        var original = new DataFrame(relation);
        LogicalPlan snapshot = PlanFixtures.Relation("people");

        var derived = new DataFrame(new Limit(10, original.Plan));

        // Original DataFrame's plan reference and content are unchanged.
        Assert.Same(relation, original.Plan);
        Assert.Equal(snapshot, original.Plan);
        // The derived plan is new and shares the original plan by reference.
        Assert.NotSame(original.Plan, derived.Plan);
        Assert.Same(original.Plan, ((Limit)derived.Plan).Child);
    }

    [Fact]
    public void WithNewChildrenRejectsWrongChildCount()
    {
        var filter = new Filter(PlanFixtures.Attr("a"), PlanFixtures.Relation("t"));
        Assert.Throws<ArgumentException>(
            () => filter.WithNewChildren(Array.Empty<LogicalPlan>()));
        Assert.Throws<ArgumentException>(() => new Join(
                PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner)
            .WithNewChildren(new LogicalPlan[] { PlanFixtures.Relation("x") }));
    }
}
