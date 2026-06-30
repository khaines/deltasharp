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

    [Fact]
    public void WithNewChildrenSwapsChildForEveryUnaryNode()
    {
        var oldChild = PlanFixtures.Relation("old");
        var newChild = PlanFixtures.Relation("new");

        AssertUnarySwap(new Limit(5, oldChild), newChild, n => ((Limit)n).Child, n => ((Limit)n).Count == 5);
        AssertUnarySwap(
            new Sort(new Expression[] { PlanFixtures.Attr("a") }, global: true, oldChild),
            newChild,
            n => ((Sort)n).Child,
            n => ((Sort)n).Global && ((Sort)n).Order.Count == 1);
        AssertUnarySwap(
            new Aggregate(new Expression[] { PlanFixtures.Attr("g") }, new Expression[] { PlanFixtures.Attr("a") }, oldChild),
            newChild,
            n => ((Aggregate)n).Child,
            n => ((Aggregate)n).GroupingExpressions.Count == 1 && ((Aggregate)n).AggregateExpressions.Count == 1);
        AssertUnarySwap(new Distinct(oldChild), newChild, n => ((Distinct)n).Child, _ => true);
        AssertUnarySwap(
            new WriteToSource(oldChild, new SinkDescriptor("parquet")),
            newChild,
            n => ((WriteToSource)n).Child,
            n => ((WriteToSource)n).Sink.Format == "parquet");
    }

    private static void AssertUnarySwap(
        LogicalPlan node,
        LogicalPlan newChild,
        Func<LogicalPlan, LogicalPlan> childOf,
        Func<LogicalPlan, bool> ownStatePreserved)
    {
        LogicalPlan rebuilt = node.WithNewChildren(new[] { newChild });
        Assert.NotSame(node, rebuilt);
        Assert.IsType(node.GetType(), rebuilt);
        Assert.Same(newChild, childOf(rebuilt));
        Assert.True(ownStatePreserved(rebuilt), $"{node.NodeName} lost its own state on rebuild.");
    }

    [Fact]
    public void WithNewChildrenRebuildsJoinPreservingTypeAndCondition()
    {
        var join = new Join(
            PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.LeftOuter,
            condition: PlanFixtures.Attr("x"));
        var newLeft = PlanFixtures.Relation("l");
        var newRight = PlanFixtures.Relation("r");

        var rebuilt = (Join)join.WithNewChildren(new LogicalPlan[] { newLeft, newRight });

        Assert.NotSame(join, rebuilt);
        Assert.Same(newLeft, rebuilt.Left);
        Assert.Same(newRight, rebuilt.Right);
        Assert.Equal(JoinType.LeftOuter, rebuilt.JoinType);
        Assert.Equal(join.Condition, rebuilt.Condition);
    }

    [Fact]
    public void WithNewChildrenRebuildsUnionPreservingArity()
    {
        var union = new Union(new LogicalPlan[] { PlanFixtures.Relation("a"), PlanFixtures.Relation("b") });
        var rebuilt = (Union)union.WithNewChildren(new LogicalPlan[]
        {
            PlanFixtures.Relation("x"), PlanFixtures.Relation("y"),
        });

        Assert.NotSame(union, rebuilt);
        Assert.Equal(2, rebuilt.Inputs.Count);

        // The arity-equals-original contract: a different child count is rejected.
        Assert.Throws<ArgumentException>(() => union.WithNewChildren(new LogicalPlan[]
        {
            PlanFixtures.Relation("x"), PlanFixtures.Relation("y"), PlanFixtures.Relation("z"),
        }));
    }
}
