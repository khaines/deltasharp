using DeltaSharp.Optimization;
using DeltaSharp.Optimization.Rules;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Optimization;

/// <summary>
/// STORY-04.5.3 — the rule/batch/fixpoint framework: batches run in order, a fixpoint batch iterates
/// until the plan stops changing, and the <see cref="RuleBatch.MaxIterations"/> safety valve bounds a
/// non-converging batch (termination).
/// </summary>
public sealed class RuleFrameworkTests
{
    [Fact]
    public void RunsFixpointBatch_UntilNoFurtherChange()
    {
        // Filter over two stacked projections. PushPredicateThroughProject moves the filter down one
        // projection per sweep, so reaching the scan requires more than one iteration.
        var relation = OptimizerFixtures.People();
        var innerProject = new Project(
            new Expression[] { OptimizerFixtures.Age, OptimizerFixtures.Name }, relation);
        var outerProject = new Project(new Expression[] { OptimizerFixtures.Age }, innerProject);
        LogicalPlan plan = new Filter(OptimizerFixtures.AgeGreaterThan(21), outerProject);

        var batch = new RuleBatch(
            "push", RuleStrategy.FixedPoint, 100, new PushPredicateThroughProject());
        LogicalPlan result = new Optimizer(new[] { batch }).Optimize(plan);

        // Filter pushed all the way to the scan: Project(Project(Filter(relation))).
        var outer = Assert.IsType<Project>(result);
        var inner = Assert.IsType<Project>(outer.Child);
        var filter = Assert.IsType<Filter>(inner.Child);
        Assert.IsType<ResolvedRelation>(filter.Child);
    }

    [Fact]
    public void OnceBatch_AppliesRulesExactlyOnce()
    {
        var relation = OptimizerFixtures.People();
        var innerProject = new Project(
            new Expression[] { OptimizerFixtures.Age, OptimizerFixtures.Name }, relation);
        var outerProject = new Project(new Expression[] { OptimizerFixtures.Age }, innerProject);
        LogicalPlan plan = new Filter(OptimizerFixtures.AgeGreaterThan(21), outerProject);

        var batch = new RuleBatch("push", RuleStrategy.Once, 100, new PushPredicateThroughProject());
        LogicalPlan result = new Optimizer(new[] { batch }).Optimize(plan);

        // A single sweep pushes the filter below only the outer projection.
        var outer = Assert.IsType<Project>(result);
        var filter = Assert.IsType<Filter>(outer.Child);
        Assert.IsType<Project>(filter.Child);
    }

    [Fact]
    public void HonorsMaxIterations_ForNonConvergingBatch()
    {
        var rule = new TogglingRule();
        var batch = new RuleBatch("toggle", RuleStrategy.FixedPoint, 3, rule);
        var optimizer = new Optimizer(new[] { batch });

        // The rule never converges. In DEBUG/test builds the optimizer surfaces non-convergence as an
        // exception (a rule/ordering bug should be loud); in Release it defensively returns the
        // best-effort plan. Either way the safety valve stops after exactly MaxIterations sweeps.
#if DEBUG
        var ex = Assert.Throws<InvalidOperationException>(
            () => optimizer.Optimize(OptimizerFixtures.People()));
        Assert.Contains("did not converge", ex.Message, StringComparison.Ordinal);
#else
        _ = optimizer.Optimize(OptimizerFixtures.People());
#endif

        Assert.Equal(3, rule.Applications);
    }

    [Fact]
    public void RunsBatchesInOrder()
    {
        var order = new List<string>();
        var first = new RecordingRule("first", order);
        var second = new RecordingRule("second", order);
        var optimizer = new Optimizer(new[]
        {
            new RuleBatch("b1", RuleStrategy.Once, 1, first),
            new RuleBatch("b2", RuleStrategy.Once, 1, second),
        });

        _ = optimizer.Optimize(OptimizerFixtures.People());

        Assert.Equal(new[] { "first", "second" }, order);
    }

    private sealed class TogglingRule : Rule
    {
        public int Applications { get; private set; }

        public override string Name => "Toggling";

        public override LogicalPlan Apply(LogicalPlan plan)
        {
            Applications++;
            var relation = (ResolvedRelation)plan;
            string flipped = relation.Identifier[0] == "a" ? "b" : "a";
            return new ResolvedRelation(
                new[] { flipped }, relation.Schema, relation.Output, relation.Options);
        }
    }

    private sealed class RecordingRule : Rule
    {
        private readonly List<string> _order;

        public RecordingRule(string name, List<string> order)
        {
            Name = name;
            _order = order;
        }

        public override string Name { get; }

        public override LogicalPlan Apply(LogicalPlan plan)
        {
            _order.Add(Name);
            return plan;
        }
    }
}
