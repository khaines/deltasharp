using DeltaSharp.Optimization.Rules;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Optimization;

/// <summary>
/// STORY-04.5.3 — the <see cref="CombineFilters"/> and <see cref="PushPredicateThroughProject"/>
/// rules. Covers filter combination, predicate pushdown through pass-through projections (AC1), and
/// the not-applicable case where a predicate references a computed alias (AC2).
/// </summary>
public sealed class PredicatePushdownTests
{
    // ---- CombineFilters ----

    [Fact]
    public void CombineFilters_MergesNestedFilters_IntoConjunction()
    {
        var inner = new Filter(new IsNotNull(OptimizerFixtures.Name), OptimizerFixtures.People());
        var outer = new Filter(OptimizerFixtures.AgeGreaterThan(21), inner);

        var combined = Assert.IsType<Filter>(new CombineFilters().Apply(outer));

        var and = Assert.IsType<And>(combined.Condition);
        Assert.Equal(outer.Condition, and.Left);
        Assert.Equal(inner.Condition, and.Right);
        Assert.IsType<ResolvedRelation>(combined.Child);
    }

    [Fact]
    public void CombineFilters_NoOp_ForSingleFilter()
    {
        var filter = new Filter(OptimizerFixtures.AgeGreaterThan(21), OptimizerFixtures.People());

        Assert.Same(filter, new CombineFilters().Apply(filter));
    }

    // ---- PushPredicateThroughProject ----

    [Fact]
    public void PushPredicate_MovesFilterBelowProject_ForPassThroughColumn()
    {
        var project = new Project(
            new Expression[] { OptimizerFixtures.Age, OptimizerFixtures.Name }, OptimizerFixtures.People());
        var filter = new Filter(OptimizerFixtures.AgeGreaterThan(21), project);

        var pushed = Assert.IsType<Project>(new PushPredicateThroughProject().Apply(filter));

        // Project now sits above the Filter, which sits directly on the relation.
        Assert.Equal(project.ProjectList, pushed.ProjectList);
        var innerFilter = Assert.IsType<Filter>(pushed.Child);
        Assert.Equal(filter.Condition, innerFilter.Condition);
        Assert.IsType<ResolvedRelation>(innerFilter.Child);
    }

    [Fact]
    public void PushPredicate_DoesNotPush_ThroughAliasColumn()
    {
        // The projection produces a computed alias with a fresh id (#10); the filter references that
        // alias, which is not a pass-through column, so the predicate must not be pushed (AC2).
        var aliased = new Alias(
            new BinaryArithmetic(OptimizerFixtures.Age, Literal.OfInt(1), ArithmeticOperator.Add), "age1");
        var project = new Project(new Expression[] { aliased }, OptimizerFixtures.People());
        var aliasRef = new AttributeReference("age1", IntegerType.Instance, nullable: true, new ExprId(10));
        var filter = new Filter(new IsNotNull(aliasRef), project);

        LogicalPlan result = new PushPredicateThroughProject().Apply(filter);

        Assert.Same(filter, result);
    }

    [Fact]
    public void PushPredicate_IsIdempotent()
    {
        var project = new Project(
            new Expression[] { OptimizerFixtures.Age }, OptimizerFixtures.People());
        var filter = new Filter(OptimizerFixtures.AgeGreaterThan(21), project);
        var rule = new PushPredicateThroughProject();

        LogicalPlan once = rule.Apply(filter);
        LogicalPlan twice = rule.Apply(once);

        Assert.Equal(once, twice);
    }
}
