using DeltaSharp.Analysis;
using DeltaSharp.Optimization;
using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Optimization;

/// <summary>
/// STORY-04.5.3 — the end-to-end <see cref="Optimizer"/>: new immutable trees (AC1), no-op preservation
/// (AC2), independently renderable analyzed/optimized stages (AC3), output-schema equivalence with the
/// analyzed plan (AC4), plus idempotence and determinism.
/// </summary>
public sealed class OptimizerTests
{
    private static readonly StructType PeopleSchema = new(new[]
    {
        new StructField("id", LongType.Instance, nullable: false),
        new StructField("name", StringType.Instance, nullable: true),
        new StructField("age", IntegerType.Instance, nullable: true),
    });

    private static LogicalPlan AnalyzeSelectAge()
    {
        var catalog = new LocalCatalog();
        catalog.Register("people", PeopleSchema);
        LogicalPlan unresolved = new Project(
            new Expression[] { new UnresolvedAttribute("age") },
            new UnresolvedRelation(new[] { "people" }));
        return new Analyzer(catalog).Resolve(unresolved);
    }

    [Fact]
    public void ReturnsNewTree_WithoutMutatingInput()
    {
        LogicalPlan input = new Project(
            new Expression[] { OptimizerFixtures.Age }, OptimizerFixtures.People());
        LogicalPlan snapshot = new Project(
            new Expression[] { OptimizerFixtures.Age }, OptimizerFixtures.People());

        LogicalPlan optimized = new Optimizer().Optimize(input);

        // Input is structurally unchanged (immutability) and a new tree was produced with a pruned scan.
        Assert.Equal(snapshot, input);
        Assert.NotSame(input, optimized);
        var relation = (ResolvedRelation)optimized.Children[0];
        Assert.Single(relation.Output);
    }

    [Fact]
    public void NoOp_ReturnsReferenceEqualPlan()
    {
        LogicalPlan relation = OptimizerFixtures.People();

        Assert.Same(relation, new Optimizer().Optimize(relation));
    }

    [Fact]
    public void AnalyzedAndOptimizedPlans_RenderIndependently()
    {
        LogicalPlan analyzed = AnalyzeSelectAge();
        LogicalPlan optimized = new Optimizer().Optimize(analyzed);

        string analyzedText = analyzed.TreeString();
        string optimizedText = optimized.TreeString();

        Assert.False(string.IsNullOrWhiteSpace(analyzedText));
        Assert.False(string.IsNullOrWhiteSpace(optimizedText));

        // The analyzed scan exposes all three columns; the optimized scan is pruned — so the two
        // stages render differently and can be shown separately (EXPLAIN, AC3).
        Assert.NotEqual(analyzedText, optimizedText);
        Assert.Contains("name", analyzedText, StringComparison.Ordinal);
        Assert.DoesNotContain("name", optimizedText, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesPlanOutput_ForAnalyzedPlan()
    {
        LogicalPlan analyzed = AnalyzeSelectAge();

        var optimized = Assert.IsType<Project>(new Optimizer().Optimize(analyzed));

        // The top projection (which defines the output schema) is preserved exactly (AC4).
        Assert.Equal(((Project)analyzed).ProjectList, optimized.ProjectList);
        // The scan was pruned to just the referenced column.
        var relation = Assert.IsType<ResolvedRelation>(optimized.Child);
        Assert.Equal(new[] { "age" }, relation.Output.Select(a => a.Name));
    }

    [Fact]
    public void CombinesFoldsAndPrunes_InFixpointBatch()
    {
        // Filter(true AND (age > 21)) over Filter(true) over Project(age) over people.
        var trueLit = Literal.OfBoolean(true);
        var condition = new And(trueLit, OptimizerFixtures.AgeGreaterThan(21));
        LogicalPlan plan = new Filter(
            condition,
            new Filter(Literal.OfBoolean(true),
                new Project(new Expression[] { OptimizerFixtures.Age }, OptimizerFixtures.People())));

        LogicalPlan optimized = new Optimizer().Optimize(plan);

        // CombineFilters merges the two filters and PushPredicateThroughProject moves the merged
        // filter below the projection; ColumnPruning then prunes the scan to just 'age'. The redundant
        // `true` predicates are NOT folded away: M1 constant folding only collapses an all-literal
        // node, and `And(true, age > 21)` has a non-literal operand (M1 has no BooleanSimplification),
        // so the conjunction structure is preserved.
        var relation = (ResolvedRelation)DescendToRelation(optimized);
        Assert.Single(relation.Output);
        Assert.Equal("age", relation.Output[0].Name);

        // The pushed filter still carries a conjunction (the `true` conjuncts survive, unfolded).
        var project = Assert.IsType<Project>(optimized);
        var pushedFilter = Assert.IsType<Filter>(project.Child);
        Assert.IsType<And>(pushedFilter.Condition);
    }

    [Fact]
    public void IsIdempotent()
    {
        LogicalPlan analyzed = AnalyzeSelectAge();

        LogicalPlan once = new Optimizer().Optimize(analyzed);
        LogicalPlan twice = new Optimizer().Optimize(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void IsIdempotent_ForStackedConstantTrueFilters()
    {
        // Filter(true, Filter(true, relation)). CombineFilters synthesizes And(true, true), which the
        // co-located ConstantFolding (in the same global fixpoint batch) folds to `true` on the next
        // sweep. A second Optimize must reproduce a structurally-equal plan (global-fixpoint idempotence).
        LogicalPlan plan = new Filter(
            Literal.OfBoolean(true),
            new Filter(Literal.OfBoolean(true), OptimizerFixtures.People()));

        LogicalPlan once = new Optimizer().Optimize(plan);
        LogicalPlan twice = new Optimizer().Optimize(once);

        Assert.Equal(once, twice);

        // The two stacked filters collapsed to a single filter and the synthesized And(true, true)
        // was fully folded to a boolean literal — no residual conjunction survives.
        var filter = Assert.IsType<Filter>(once);
        var literal = Assert.IsType<Literal>(filter.Condition);
        Assert.Equal(BooleanType.Instance, literal.Type);
        Assert.True((bool)literal.Value!);
        Assert.IsType<ResolvedRelation>(filter.Child);
    }

    [Fact]
    public void IsIdempotent_ForStackedConstantFilters_FoldingToFalse()
    {
        // Filter(true, Filter(false, relation)) → And(false, true) → folded to `false`.
        LogicalPlan plan = new Filter(
            Literal.OfBoolean(true),
            new Filter(Literal.OfBoolean(false), OptimizerFixtures.People()));

        LogicalPlan once = new Optimizer().Optimize(plan);
        LogicalPlan twice = new Optimizer().Optimize(once);

        Assert.Equal(once, twice);

        var filter = Assert.IsType<Filter>(once);
        var literal = Assert.IsType<Literal>(filter.Condition);
        Assert.False((bool)literal.Value!);
    }

    [Fact]
    public void Optimize_RejectsUnresolvedPlan()
    {
        // The optimizer's rules assume a resolved (analyzed) plan with bound ids/types; an unresolved
        // plan is a programming error and must be rejected up front rather than silently mis-optimized.
        LogicalPlan unresolved = new UnresolvedRelation(new[] { "people" });

        Assert.False(unresolved.Resolved);
        var ex = Assert.Throws<InvalidOperationException>(() => new Optimizer().Optimize(unresolved));
        Assert.Contains("resolved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsDeterministic_AcrossRuns()
    {
        LogicalPlan a = new Optimizer().Optimize(AnalyzeSelectAge());
        LogicalPlan b = new Optimizer().Optimize(AnalyzeSelectAge());

        Assert.Equal(a, b);
    }

    private static LogicalPlan DescendToRelation(LogicalPlan plan)
    {
        LogicalPlan node = plan;
        while (node is not ResolvedRelation)
        {
            node = node.Children[0];
        }

        return node;
    }
}
