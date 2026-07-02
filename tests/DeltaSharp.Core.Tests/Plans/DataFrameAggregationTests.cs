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
        // The retained grouping key is the SAME instance in both lists (structural sharing) — the
        // API prepends the recorded grouping expressions verbatim, it does not rebuild them.
        Assert.Same(aggregate.GroupingExpressions[0], aggregate.AggregateExpressions[0]);
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

    [Fact]
    public void GlobalAgg_MultipleAggregates_AppendInOrder()
    {
        // Non-vacuity guard for DataFrame.Agg's varargs: every supplied aggregate (the required first
        // plus each `exprs` element) must appear in the Aggregate output, in call order, with an
        // EMPTY grouping (global aggregation). If Agg were mutated to ignore exprs[1..], this reddens.
        DataFrame df = People();

        DataFrame result = df.Agg(
            Functions.Sum(Functions.Col("salary")).As("total"),
            Functions.Avg(Functions.Col("salary")).As("mean"),
            Functions.Min(Functions.Col("salary")).As("low"),
            Functions.Max(Functions.Col("salary")).As("high"));

        var aggregate = Assert.IsType<Aggregate>(result.Plan);
        Assert.Empty(aggregate.GroupingExpressions);
        // No retained keys (empty grouping) — the four aggregates appear verbatim, in order.
        Assert.Collection(
            aggregate.AggregateExpressions,
            e => Assert.Equal("total", Assert.IsType<Alias>(e).Name),
            e => Assert.Equal("mean", Assert.IsType<Alias>(e).Name),
            e => Assert.Equal("low", Assert.IsType<Alias>(e).Name),
            e => Assert.Equal("high", Assert.IsType<Alias>(e).Name));
    }

    [Fact]
    public void GlobalAgg_NullExprsArray_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentNullException>(
            () => df.Agg(Functions.Sum(Functions.Col("salary")), (Column[])null!));
    }

    [Fact]
    public void GlobalAgg_NullExprElement_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentNullException>(
            () => df.Agg(Functions.Sum(Functions.Col("salary")), null!));
    }

    [Fact]
    public void GlobalCount_BuildsAggregateWithEmptyGroupingAndSingleCountColumn()
    {
        // GroupBy().Count() is a global (no-key) row count: empty grouping, a single aliased `count`.
        DataFrame df = People();

        DataFrame result = df.GroupBy().Count();

        var aggregate = Assert.IsType<Aggregate>(result.Plan);
        Assert.Empty(aggregate.GroupingExpressions);
        Expression only = Assert.Single(aggregate.AggregateExpressions);
        var alias = Assert.IsType<Alias>(only);
        Assert.Equal("count", alias.Name);
        Assert.Equal("count", Assert.IsType<UnresolvedFunction>(alias.Child).Name);
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
        // output shape without depending on function resolution. A parent Project over the resolved
        // Aggregate then references BOTH the grouping key and the aggregate alias, proving they are
        // visible in the aggregate's derived output and that the retained grouping attribute reuses
        // (not rebuilds) the child grouping key's ExprId (the retainGroupColumns ↔ ExprId-reuse loop).
        var catalog = new LocalCatalog();
        catalog.Register("people", new StructType(new[]
        {
            new StructField("dept", StringType.Instance, nullable: true),
            new StructField("salary", LongType.Instance, nullable: true),
        }));
        var analyzer = new Analyzer(catalog);
        DataFrame df = new(new UnresolvedRelation(new[] { "people" }));

        DataFrame aggregated = df.GroupBy("dept").Agg(Functions.Lit(1L).As("marker"));
        // Parent projection reads the grouping key + the aggregate alias out of the Aggregate output.
        var projected = new Project(
            new Expression[] { Functions.Col("dept").Expr, Functions.Col("marker").Expr },
            aggregated.Plan);
        LogicalPlan resolved = analyzer.Resolve(projected);

        var project = Assert.IsType<Project>(resolved);
        var aggregate = Assert.IsType<Aggregate>(project.Child);

        // Grouping key resolves to the dept attribute.
        var groupingKey = Assert.IsType<AttributeReference>(Assert.Single(aggregate.GroupingExpressions));
        Assert.Equal("dept", groupingKey.Name);
        // Output (AggregateExpressions) = retained grouping attribute ⧺ aggregate alias.
        Assert.Collection(
            aggregate.AggregateExpressions,
            e => Assert.Equal("dept", Assert.IsType<AttributeReference>(e).Name),
            e => Assert.Equal("marker", Assert.IsType<Alias>(e).Name));

        // (ii) The retained grouping attribute reuses the child grouping key's ExprId (structural
        // sharing survives resolution — the key is bound once, to one identity).
        var retainedKey = Assert.IsType<AttributeReference>(aggregate.AggregateExpressions[0]);
        Assert.Equal(groupingKey.ExprId, retainedKey.ExprId);

        // (i) The parent Project's references bind against the Aggregate output: `dept` reuses the
        // retained grouping attribute's ExprId, `marker` binds to the aggregate alias' fresh id.
        var projectedDept = Assert.IsType<AttributeReference>(project.ProjectList[0]);
        var projectedMarker = Assert.IsType<AttributeReference>(project.ProjectList[1]);
        Assert.Equal("dept", projectedDept.Name);
        Assert.Equal(retainedKey.ExprId, projectedDept.ExprId);
        Assert.Equal("marker", projectedMarker.Name);
    }

    [Fact]
    public void Analyzer_ComplexGroupingKey_ThrowsDeterministically_TrackedUnder171()
    {
        // F1 boundary: a COMPLEX grouping key (a non-attribute expression, e.g. Col("a") + Col("b"))
        // is retained at the front of the aggregate output; today output derivation cannot name a
        // non-attribute/non-alias element, so Resolve throws a deterministic AnalysisException. This
        // documents the boundary — aggregate naming/aliasing for computed keys lands with #171.
        var catalog = new LocalCatalog();
        catalog.Register("t", new StructType(new[]
        {
            new StructField("a", LongType.Instance, nullable: true),
            new StructField("b", LongType.Instance, nullable: true),
        }));
        var analyzer = new Analyzer(catalog);
        DataFrame df = new(new UnresolvedRelation(new[] { "t" }));

        DataFrame aggregated = df
            .GroupBy(Functions.Col("a").Plus(Functions.Col("b")))
            .Agg(Functions.Lit(1L).As("m"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(aggregated.Plan));
        Assert.Equal(AnalysisErrorKind.UnsupportedProjection, ex.Kind);
    }

    [Fact]
    public void Analyzer_RealAggregateFunction_ReportsDeferredResolution_NotApiExecution()
    {
        // AC3 boundary: an aggregate FUNCTION (count/sum/…) is still unresolved after this story — the
        // ANALYZER is the gate that reports it (a deterministic AnalysisException), never the API. The
        // API only builds a well-formed unresolved Aggregate; it neither coerces nor executes.
        // Aggregate input type-validation + function resolution + Spark auto-naming land with
        // STORY-04.5.2 (#171). ("Nothing is read/executed" is proven by the §7 lazy tests in
        // DataFrameAggregationLazyTests, not here.)
        //
        // The message is PINNED so #171 improves it deliberately: for a bare aggregate the failure
        // fires from output derivation (DeriveOutput → ToAttribute's UnresolvedFunction branch) during
        // ResolveReferences — BEFORE CheckAnalysis — so it carries the UnsupportedProjection kind and
        // the deferred-resolution message (not CheckAnalysis' generic unresolved-reference text).
        var catalog = new LocalCatalog();
        catalog.Register("people", new StructType(new[]
        {
            new StructField("dept", StringType.Instance, nullable: true),
            new StructField("salary", LongType.Instance, nullable: true),
        }));
        var analyzer = new Analyzer(catalog);
        DataFrame df = new(new UnresolvedRelation(new[] { "people" }));

        DataFrame aggregated = df.GroupBy("dept").Agg(Functions.Sum(Functions.Col("salary")));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(aggregated.Plan));
        Assert.Equal(AnalysisErrorKind.UnsupportedProjection, ex.Kind);
        Assert.Equal(
            "Aggregate/function output 'sum' cannot be named yet: aggregate-function resolution and "
            + "Spark auto-naming are deferred to STORY-04.5.2 (#171). Alias the expression (for "
            + "example .As(\"total\")) as the M1 workaround.",
            ex.Message);
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
