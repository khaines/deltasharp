using DeltaSharp.Optimization.Rules;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Optimization;

/// <summary>
/// STORY-04.5.3 — the <see cref="ColumnPruning"/> rule. Covers scan-column pruning below a projection
/// (AC1), top-level output-schema preservation (AC4), and the not-applicable cases: all columns used,
/// columns reaching the result un-projected, and pruning blocked below <see cref="Distinct"/> (AC2).
/// </summary>
public sealed class ColumnPruningTests
{
    private static LogicalPlan Prune(LogicalPlan plan) => new ColumnPruning().Apply(plan);

    private static ResolvedRelation RelationUnder(LogicalPlan plan)
    {
        LogicalPlan node = plan;
        while (node is not ResolvedRelation)
        {
            node = node.Children[0];
        }

        return (ResolvedRelation)node;
    }

    [Fact]
    public void DropsUnreferencedScanColumns_BelowProjection()
    {
        // Project only 'age' (#2): 'id' and 'name' should be pruned from the scan.
        LogicalPlan plan = new Project(
            new Expression[] { OptimizerFixtures.Age }, OptimizerFixtures.People());

        var project = Assert.IsType<Project>(Prune(plan));
        var relation = Assert.IsType<ResolvedRelation>(project.Child);

        var kept = Assert.Single(relation.Output);
        Assert.Equal("age", kept.Name);
        Assert.Equal(new[] { "age" }, relation.Schema.Fields.Select(f => f.Name));
    }

    [Fact]
    public void KeepsColumnsUsedByFilter_BetweenProjectAndScan()
    {
        // Project 'name' (#1) over Filter(age > 21) over people: keep name AND age, drop id.
        LogicalPlan plan = new Project(
            new Expression[] { OptimizerFixtures.Name },
            new Filter(OptimizerFixtures.AgeGreaterThan(21), OptimizerFixtures.People()));

        ResolvedRelation relation = RelationUnder(Prune(plan));

        Assert.Equal(new[] { "name", "age" }, relation.Output.Select(a => a.Name));
    }

    [Fact]
    public void PreservesTopLevelOutputSchema()
    {
        var project = new Project(new Expression[] { OptimizerFixtures.Age }, OptimizerFixtures.People());

        var pruned = Assert.IsType<Project>(Prune(project));

        // The projection list (which defines the plan's output schema) is untouched.
        Assert.Equal(project.ProjectList, pruned.ProjectList);
    }

    [Fact]
    public void NoOp_WhenAllColumnsUsed()
    {
        LogicalPlan plan = new Project(
            new Expression[] { OptimizerFixtures.Id, OptimizerFixtures.Name, OptimizerFixtures.Age },
            OptimizerFixtures.People());

        Assert.Same(plan, Prune(plan));
    }

    [Fact]
    public void DoesNotPrune_ColumnsReachingResultUnprojected()
    {
        // Filter directly on the relation (no projection cut): all columns are the plan output.
        LogicalPlan plan = new Filter(OptimizerFixtures.AgeGreaterThan(21), OptimizerFixtures.People());

        LogicalPlan pruned = Prune(plan);

        Assert.Same(plan, pruned);
        Assert.Equal(3, RelationUnder(pruned).Output.Count);
    }

    [Fact]
    public void DoesNotPrune_BelowDistinct()
    {
        // Distinct dedups on ALL child columns, so pruning beneath it would change the row multiset.
        LogicalPlan plan = new Project(
            new Expression[] { OptimizerFixtures.Age },
            new Distinct(OptimizerFixtures.People()));

        LogicalPlan pruned = Prune(plan);

        Assert.Same(plan, pruned);
        Assert.Equal(3, RelationUnder(pruned).Output.Count);
    }

    [Fact]
    public void KeepsColumnRequiredBySort_BetweenProjectAndScan()
    {
        // Project 'age' (#2) over Sort(by name #1) over people: the scan must retain 'name' because
        // the ordering below the projection still needs it, even though it is not in the output.
        var order = new SortOrder(OptimizerFixtures.Name, SortDirection.Ascending, NullOrdering.NullsFirst);
        LogicalPlan plan = new Project(
            new Expression[] { OptimizerFixtures.Age },
            new Sort(new Expression[] { order }, global: true, OptimizerFixtures.People()));

        ResolvedRelation relation = RelationUnder(Prune(plan));

        // 'id' is dropped; both 'name' (sort key) and 'age' (output) are retained, in schema order.
        Assert.Equal(new[] { "name", "age" }, relation.Output.Select(a => a.Name));
    }

    [Fact]
    public void DoesNotPrune_AggregateInputs_PreservingReferences()
    {
        // Project a group column over an Aggregate: M1 does not model Aggregate's output, so it never
        // prunes the Aggregate's direct scan input. The whole plan (Aggregate and its child relation)
        // must be reference-preserved so a future "prune aggregate inputs" change cannot silently
        // corrupt retained group columns / auto-named outputs (#402/#404).
        var count = new ResolvedFunction(
            "count", FunctionKind.Aggregate, LongType.Instance, nullable: false,
            new Expression[] { OptimizerFixtures.Age });
        var aggregate = new Aggregate(
            new Expression[] { OptimizerFixtures.Age }, new Expression[] { count }, OptimizerFixtures.People());
        LogicalPlan plan = new Project(new Expression[] { OptimizerFixtures.Age }, aggregate);

        LogicalPlan pruned = Prune(plan);

        Assert.Same(plan, pruned);
        Assert.Same(aggregate, Assert.IsType<Project>(pruned).Child);
        Assert.Equal(3, RelationUnder(pruned).Output.Count);
    }

    [Fact]
    public void IsIdempotent()
    {
        LogicalPlan plan = new Project(
            new Expression[] { OptimizerFixtures.Age }, OptimizerFixtures.People());

        LogicalPlan once = Prune(plan);
        LogicalPlan twice = Prune(once);

        Assert.Equal(once, twice);
    }
}
