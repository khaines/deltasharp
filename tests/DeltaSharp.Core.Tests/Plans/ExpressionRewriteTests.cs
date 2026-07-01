using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// QE-F1: the expression-rewrite substrate (<see cref="LogicalPlan.WithNewExpressions"/> +
/// <see cref="LogicalPlan.TransformExpressionsDown"/>/<c>Up</c>) rewrites a plan node's
/// directly-held expressions while sharing untouched children and untouched sibling expressions
/// by reference — symmetric with the child-rewrite substrate.
/// </summary>
public sealed class ExpressionRewriteTests
{
    private static Expression Rename(Expression e) =>
        e is UnresolvedAttribute a && a.NameParts is [var only] && only == "a"
            ? new UnresolvedAttribute("renamed")
            : e;

    [Fact]
    public void TransformExpressionsRewritesProjectExpressionAndSharesUntouchedSubtree()
    {
        var child = PlanFixtures.Relation("t");
        var project = new Project(new Expression[] { PlanFixtures.Attr("a"), PlanFixtures.Attr("b") }, child);

        var rewritten = (Project)project.TransformExpressionsDown(Rename);

        // (a) the expression changed.
        Assert.Equal("renamed", ((UnresolvedAttribute)rewritten.ProjectList[0]).NameParts[0]);
        // (b) the plan node is new.
        Assert.NotSame(project, rewritten);
        // (c) untouched sibling expression and child subtree are shared by reference.
        Assert.Same(project.ProjectList[1], rewritten.ProjectList[1]);
        Assert.Same(child, rewritten.Child);
        // The original is unchanged.
        Assert.Equal("a", ((UnresolvedAttribute)project.ProjectList[0]).NameParts[0]);
    }

    [Fact]
    public void TransformExpressionsRewritesFilterCondition()
    {
        var child = PlanFixtures.Relation("t");
        var filter = new Filter(PlanFixtures.Attr("a"), child);

        var rewritten = (Filter)filter.TransformExpressionsDown(Rename);

        Assert.Equal("renamed", ((UnresolvedAttribute)rewritten.Condition).NameParts[0]);
        Assert.NotSame(filter, rewritten);
        Assert.Same(child, rewritten.Child);
    }

    [Fact]
    public void TransformExpressionsNoOpReturnsSameReference()
    {
        var project = new Project(new Expression[] { PlanFixtures.Attr("x") }, PlanFixtures.Relation("t"));
        Assert.Same(project, project.TransformExpressionsDown(e => e));
    }

    [Fact]
    public void TransformExpressionsReachesNestedExpressionViaExpressionTransformDown()
    {
        // Filter condition is a function whose argument is the attribute to rewrite.
        var func = new UnresolvedFunction(">", new Expression[] { PlanFixtures.Attr("a"), PlanFixtures.Attr("21") });
        var filter = new Filter(func, PlanFixtures.Relation("t"));

        var rewritten = (Filter)filter.TransformExpressionsDown(Rename);

        var newFunc = (UnresolvedFunction)rewritten.Condition;
        Assert.Equal("renamed", ((UnresolvedAttribute)newFunc.Arguments[0]).NameParts[0]);
        // The untouched second argument is shared by reference.
        Assert.Same(func.Arguments[1], newFunc.Arguments[1]);
    }

    [Fact]
    public void AggregateGroupingAggregateSplitRoundTripsThroughWithNewExpressions()
    {
        var grouping = new Expression[] { PlanFixtures.Attr("g1"), PlanFixtures.Attr("g2") };
        var aggregate = new Expression[] { PlanFixtures.Attr("a1") };
        var node = new Aggregate(grouping, aggregate, PlanFixtures.Relation("t"));

        // Expressions is grouping ⧺ aggregate.
        Assert.Equal(3, node.Expressions.Count);

        // Rewrite all three (rename "a" matches none here; identity rebuild via WithNewExpressions).
        var replacement = new Expression[]
        {
            new UnresolvedAttribute("ng1"), new UnresolvedAttribute("ng2"), new UnresolvedAttribute("na1"),
        };
        var rebuilt = (Aggregate)node.WithNewExpressions(replacement);

        // The split is honoured: first GroupingExpressions.Count go to grouping, the rest to aggregate.
        Assert.Equal(2, rebuilt.GroupingExpressions.Count);
        Assert.Single(rebuilt.AggregateExpressions);
        Assert.Equal("ng1", ((UnresolvedAttribute)rebuilt.GroupingExpressions[0]).NameParts[0]);
        Assert.Equal("ng2", ((UnresolvedAttribute)rebuilt.GroupingExpressions[1]).NameParts[0]);
        Assert.Equal("na1", ((UnresolvedAttribute)rebuilt.AggregateExpressions[0]).NameParts[0]);
        // Child shared by reference.
        Assert.Same(node.Child, rebuilt.Child);
    }

    [Fact]
    public void TransformExpressionsOnAggregateRewritesAcrossTheSplit()
    {
        var grouping = new Expression[] { PlanFixtures.Attr("a") };
        var aggregate = new Expression[] { PlanFixtures.Attr("b"), PlanFixtures.Attr("a") };
        var node = new Aggregate(grouping, aggregate, PlanFixtures.Relation("t"));

        var rewritten = (Aggregate)node.TransformExpressionsDown(Rename);

        Assert.Equal("renamed", ((UnresolvedAttribute)rewritten.GroupingExpressions[0]).NameParts[0]);
        Assert.Equal("renamed", ((UnresolvedAttribute)rewritten.AggregateExpressions[1]).NameParts[0]);
        // The untouched aggregate expression "b" is shared by reference.
        Assert.Same(node.AggregateExpressions[0], rewritten.AggregateExpressions[0]);
    }

    [Fact]
    public void WithNewExpressionsRejectsWrongCount()
    {
        var project = new Project(new Expression[] { PlanFixtures.Attr("a") }, PlanFixtures.Relation("t"));
        Assert.Throws<ArgumentException>(() => project.WithNewExpressions(System.Array.Empty<Expression>()));

        var distinct = new Distinct(PlanFixtures.Relation("t"));
        Assert.Throws<ArgumentException>(
            () => distinct.WithNewExpressions(new Expression[] { PlanFixtures.Attr("a") }));
    }

    [Fact]
    public void NoExpressionNodesReturnSelfFromWithNewExpressions()
    {
        var distinct = new Distinct(PlanFixtures.Relation("t"));
        Assert.Same(distinct, distinct.WithNewExpressions(System.Array.Empty<Expression>()));
    }
}
