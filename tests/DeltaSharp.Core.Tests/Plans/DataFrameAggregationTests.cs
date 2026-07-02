using DeltaSharp.Analysis;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// STORY-04.2.2 (#161) — the <see cref="DataFrame"/> aggregation surface:
/// <see cref="DataFrame.GroupBy(Column[])"/> / <see cref="DataFrame.GroupBy(string, string[])"/>,
/// the intermediate <see cref="RelationalGroupedDataset"/>, its
/// <see cref="RelationalGroupedDataset.Agg(Column, Column[])"/> / <see cref="RelationalGroupedDataset.Count"/>
/// doors, and the global <see cref="DataFrame.Agg(Column, Column[])"/>. These tests assert each
/// method builds the correct immutable <c>Aggregate</c> plan (grouping expressions, retained grouping
/// columns ⧺ aggregate expressions, Spark-compatible aliases), leaves the source frame unchanged
/// (structural sharing, #167), and evaluates nothing (the lazy invariant, ADR-0001). The marquee lazy
/// non-vacuity proof lives in <c>LazyEager/DataFrameAggregationLazyTests</c>.
/// </summary>
public sealed class DataFrameAggregationTests
{
    private static DataFrame People() => new(PlanFixtures.Relation("people"));

    // ----- AC1: GroupBy records grouping expressions without executing -----

    [Fact]
    public void GroupBy_Columns_RecordsGroupingExpressionsInOrder()
    {
        DataFrame df = People();

        RelationalGroupedDataset grouped = df.GroupBy(Functions.Col("dept"), Functions.Col("team"));

        Assert.Collection(
            grouped.GroupingExpressions,
            e => Assert.Equal("dept", Assert.IsType<UnresolvedAttribute>(e).Name),
            e => Assert.Equal("team", Assert.IsType<UnresolvedAttribute>(e).Name));
        // The grouped handle shares the source plan by reference (structural sharing).
        Assert.Same(df.Plan, grouped.Plan);
    }

    [Fact]
    public void GroupBy_Names_RecordsUnresolvedAttributeGroupingExpressions()
    {
        DataFrame df = People();

        RelationalGroupedDataset grouped = df.GroupBy("dept", "team");

        Assert.Collection(
            grouped.GroupingExpressions,
            e => Assert.Equal("dept", Assert.IsType<UnresolvedAttribute>(e).Name),
            e => Assert.Equal("team", Assert.IsType<UnresolvedAttribute>(e).Name));
    }

    [Fact]
    public void GroupBy_SingleName_RecordsSingleGroupingExpression()
    {
        DataFrame df = People();

        RelationalGroupedDataset grouped = df.GroupBy("dept");

        Expression only = Assert.Single(grouped.GroupingExpressions);
        Assert.Equal("dept", Assert.IsType<UnresolvedAttribute>(only).Name);
    }

    [Fact]
    public void GroupBy_NoColumns_RecordsEmptyGrouping()
    {
        DataFrame df = People();

        RelationalGroupedDataset grouped = df.GroupBy();

        Assert.Empty(grouped.GroupingExpressions);
    }

    [Fact]
    public void GroupBy_DoesNotBuildAnAggregate_UntilAggIsChosen()
    {
        DataFrame df = People();

        // GroupBy alone produces the intermediate handle, not a DataFrame/plan — nothing is grouped.
        RelationalGroupedDataset grouped = df.GroupBy("dept");

        Assert.IsType<RelationalGroupedDataset>(grouped);
        // The source frame's plan is untouched.
        Assert.IsType<UnresolvedRelation>(df.Plan);
    }

    // ----- AC2/AC4: Agg builds an Aggregate with grouping + retained keys ⧺ aggregate exprs -----

    [Fact]
    public void Agg_BuildsAggregateWithGroupingAndRetainedKeysThenAggregate()
    {
        DataFrame df = People();

        DataFrame result = df.GroupBy("dept").Agg(Functions.Sum(Functions.Col("salary")));

        var aggregate = Assert.IsType<Aggregate>(result.Plan);
        // Grouping expressions: the single key.
        Expression grouping = Assert.Single(aggregate.GroupingExpressions);
        Assert.Equal("dept", Assert.IsType<UnresolvedAttribute>(grouping).Name);
        // Aggregate expressions: retained grouping key first (Spark retainGroupColumns=true), then agg.
        Assert.Collection(
            aggregate.AggregateExpressions,
            e => Assert.Equal("dept", Assert.IsType<UnresolvedAttribute>(e).Name),
            e => Assert.Equal("sum", Assert.IsType<UnresolvedFunction>(e).Name));
        Assert.Same(df.Plan, aggregate.Child);
    }

    [Fact]
    public void Agg_HonorsUserAliasOnAggregateOutput()
    {
        DataFrame df = People();

        DataFrame result = df
            .GroupBy("dept")
            .Agg(Functions.Sum(Functions.Col("salary")).As("total"));

        var aggregate = Assert.IsType<Aggregate>(result.Plan);
        // Retained key, then the user-aliased aggregate carrying the exact name "total".
        var alias = Assert.IsType<Alias>(aggregate.AggregateExpressions[1]);
        Assert.Equal("total", alias.Name);
        Assert.Equal("sum", Assert.IsType<UnresolvedFunction>(alias.Child).Name);
    }

    [Fact]
    public void Agg_BareAggregate_IsLeftUnaliased_ForAnalyzerNaming()
    {
        // A bare aggregate is preserved as-is; Spark's auto-name (sum(v)) is assigned by the analyzer
        // when aggregate-function resolution lands (#171). The API never computes the name eagerly.
        DataFrame df = People();

        DataFrame result = df.GroupBy("dept").Agg(Functions.Sum(Functions.Col("salary")));

        var aggregate = Assert.IsType<Aggregate>(result.Plan);
        Assert.IsType<UnresolvedFunction>(aggregate.AggregateExpressions[1]);
    }

    [Fact]
    public void Agg_MultipleAggregates_AppendInOrderAfterRetainedKeys()
    {
        DataFrame df = People();

        DataFrame result = df
            .GroupBy("dept", "team")
            .Agg(
                Functions.Sum(Functions.Col("salary")).As("total"),
                Functions.Avg(Functions.Col("salary")).As("mean"),
                Functions.Count(Functions.Col("id")).As("n"));

        var aggregate = Assert.IsType<Aggregate>(result.Plan);
        Assert.Equal(2, aggregate.GroupingExpressions.Count);
        // Two retained grouping keys, then the three aggregates in call order.
        Assert.Collection(
            aggregate.AggregateExpressions,
            e => Assert.Equal("dept", Assert.IsType<UnresolvedAttribute>(e).Name),
            e => Assert.Equal("team", Assert.IsType<UnresolvedAttribute>(e).Name),
            e => Assert.Equal("total", Assert.IsType<Alias>(e).Name),
            e => Assert.Equal("mean", Assert.IsType<Alias>(e).Name),
            e => Assert.Equal("n", Assert.IsType<Alias>(e).Name));
    }

    [Fact]
    public void Count_BuildsAggregateWithNamedCountAfterRetainedKeys()
    {
        DataFrame df = People();

        DataFrame result = df.GroupBy("dept").Count();

        var aggregate = Assert.IsType<Aggregate>(result.Plan);
        Assert.Collection(
            aggregate.AggregateExpressions,
            e => Assert.Equal("dept", Assert.IsType<UnresolvedAttribute>(e).Name),
            e =>
            {
                var alias = Assert.IsType<Alias>(e);
                Assert.Equal("count", alias.Name);
                Assert.Equal("count", Assert.IsType<UnresolvedFunction>(alias.Child).Name);
            });
    }

    // ----- AC2: global aggregation (df.Agg == groupBy().agg) -----

    [Fact]
    public void GlobalAgg_BuildsAggregateWithEmptyGrouping()
    {
        DataFrame df = People();

        DataFrame result = df.Agg(Functions.Sum(Functions.Col("salary")).As("total"));

        var aggregate = Assert.IsType<Aggregate>(result.Plan);
        Assert.Empty(aggregate.GroupingExpressions);
        // No retained keys — only the aggregate output.
        Expression only = Assert.Single(aggregate.AggregateExpressions);
        Assert.Equal("total", Assert.IsType<Alias>(only).Name);
        Assert.Same(df.Plan, aggregate.Child);
    }

    [Fact]
    public void GlobalAgg_IsEquivalentToGroupByNoKeysThenAgg()
    {
        DataFrame df = People();
        Column agg = Functions.Sum(Functions.Col("salary")).As("total");

        DataFrame viaGlobal = df.Agg(agg);
        DataFrame viaGroupBy = df.GroupBy().Agg(agg);

        Assert.Equal(viaGroupBy.Plan, viaGlobal.Plan);
    }

    // ----- Immutability / structural sharing -----

    [Fact]
    public void Agg_LeavesSourceFrameUnchanged_AndSharesChildByReference()
    {
        DataFrame df = People();
        LogicalPlan original = df.Plan;

        DataFrame result = df.GroupBy("dept").Agg(Functions.Sum(Functions.Col("salary")));

        Assert.Same(original, df.Plan);
        Assert.Same(original, ((Aggregate)result.Plan).Child);
        Assert.NotSame(df.Plan, result.Plan);
    }

    [Fact]
    public void GroupByAgg_ChainsOverPriorTransformations_LeavingEachStageIntact()
    {
        DataFrame df = People();
        LogicalPlan root = df.Plan;

        DataFrame chained = df
            .Filter(Functions.Col("active"))
            .GroupBy("dept")
            .Agg(Functions.Max(Functions.Col("salary")).As("top"));

        var aggregate = Assert.IsType<Aggregate>(chained.Plan);
        var filter = Assert.IsType<Filter>(aggregate.Child);
        Assert.Same(root, filter.Child);
        Assert.Same(root, df.Plan);
    }

    // ----- Analyzer round-trip: structural Aggregate output = grouping attrs ⧺ agg aliases -----

    [Fact]
    public void Analyzer_DerivesAggregateOutput_AsGroupingAttributesThenAggregateAliases()
    {
        // The analyzer's structural Aggregate output derivation (grouping attributes followed by the
        // aggregate aliases) is exercised here. Aggregate-FUNCTION resolution and pretty-naming are
        // deferred to #171, so this round-trip uses grouping keys retained by the API plus a
        // resolvable aliased expression (a literal) in aggregate position — enough to prove the
        // output shape without depending on function resolution.
        var catalog = new LocalCatalog();
        catalog.Register("people", new StructType(new[]
        {
            new StructField("dept", StringType.Instance, nullable: true),
            new StructField("salary", LongType.Instance, nullable: true),
        }));
        var analyzer = new Analyzer(catalog);
        DataFrame df = new(new UnresolvedRelation(new[] { "people" }));

        DataFrame aggregated = df.GroupBy("dept").Agg(Functions.Lit(1L).As("marker"));
        LogicalPlan resolved = analyzer.Resolve(aggregated.Plan);

        var aggregate = Assert.IsType<Aggregate>(resolved);
        // Grouping key resolves to the dept attribute.
        Assert.Equal("dept", Assert.IsType<AttributeReference>(Assert.Single(aggregate.GroupingExpressions)).Name);
        // Output (AggregateExpressions) = retained grouping attribute ⧺ aggregate alias.
        Assert.Collection(
            aggregate.AggregateExpressions,
            e => Assert.Equal("dept", Assert.IsType<AttributeReference>(e).Name),
            e => Assert.Equal("marker", Assert.IsType<Alias>(e).Name));
    }

    [Fact]
    public void Analyzer_RealAggregateFunction_ReportsDeferredResolution_NotApiExecution()
    {
        // AC3 boundary: an aggregate FUNCTION (count/sum/…) is still unresolved after this story — the
        // ANALYZER is the gate that reports it (deterministic AnalysisException), never the API. The
        // API only builds a well-formed unresolved Aggregate; it neither coerces nor executes.
        // Aggregate input type-validation + function resolution land with STORY-04.5.2 (#171).
        var catalog = new LocalCatalog();
        catalog.Register("people", new StructType(new[]
        {
            new StructField("dept", StringType.Instance, nullable: true),
            new StructField("salary", LongType.Instance, nullable: true),
        }));
        var analyzer = new Analyzer(catalog);
        DataFrame df = new(new UnresolvedRelation(new[] { "people" }));

        DataFrame aggregated = df.GroupBy("dept").Agg(Functions.Sum(Functions.Col("salary")));

        Assert.Throws<AnalysisException>(() => analyzer.Resolve(aggregated.Plan));
    }

    // ----- Null / empty argument guards -----

    [Fact]
    public void GroupBy_NullColumnsArray_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentNullException>(() => df.GroupBy((Column[])null!));
    }

    [Fact]
    public void GroupBy_NullColumnElement_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentNullException>(() => df.GroupBy(Functions.Col("a"), null!));
    }

    [Fact]
    public void GroupBy_NullFirstName_Throws()
    {
        DataFrame df = People();
        Assert.ThrowsAny<ArgumentException>(() => df.GroupBy((string)null!));
    }

    [Fact]
    public void GroupBy_EmptyFirstName_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentException>(() => df.GroupBy(string.Empty));
    }

    [Fact]
    public void GroupBy_NullRestElement_Throws()
    {
        DataFrame df = People();
        Assert.ThrowsAny<ArgumentException>(() => df.GroupBy("a", null!));
    }

    [Fact]
    public void GroupBy_EmptyRestElement_Throws()
    {
        DataFrame df = People();
        Assert.ThrowsAny<ArgumentException>(() => df.GroupBy("a", string.Empty));
    }

    [Fact]
    public void Agg_NullFirstExpr_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentNullException>(() => df.GroupBy("dept").Agg(null!));
    }

    [Fact]
    public void Agg_NullExprsArray_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentNullException>(
            () => df.GroupBy("dept").Agg(Functions.Count(Functions.Col("id")), (Column[])null!));
    }

    [Fact]
    public void Agg_NullExprElement_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentNullException>(
            () => df.GroupBy("dept").Agg(Functions.Count(Functions.Col("id")), null!));
    }

    [Fact]
    public void GlobalAgg_NullFirstExpr_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentNullException>(() => df.Agg(null!));
    }
}
