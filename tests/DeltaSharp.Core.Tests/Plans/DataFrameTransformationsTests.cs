using DeltaSharp.Analysis;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// STORY-04.2.1 (#160) — the first <see cref="DataFrame"/> transformation surface:
/// <see cref="DataFrame.Select(Column[])"/>, <see cref="DataFrame.Filter(Column)"/> /
/// <see cref="DataFrame.Where(Column)"/>, and <see cref="DataFrame.WithColumn(string, Column)"/>.
/// These tests assert each method builds the correct immutable <c>Project</c>/<c>Filter</c> plan over
/// the source frame's plan, leaves the source frame unchanged (structural sharing, #167), and
/// evaluates nothing (the lazy invariant, ADR-0001). The marquee lazy non-vacuity proof lives in
/// <c>LazyEager/DataFrameLazyTransformationTests</c>.
/// </summary>
public sealed class DataFrameTransformationsTests
{
    private static readonly StructType PeopleSchema = new(new[]
    {
        new StructField("id", LongType.Instance, nullable: false),
        new StructField("name", StringType.Instance, nullable: true),
        new StructField("age", IntegerType.Instance, nullable: true),
    });

    private static DataFrame People() => new(PlanFixtures.Relation("people"));

    // ----- AC1: Select (Column overload) -----

    [Fact]
    public void Select_Columns_BuildsProjectWithExpressionsInOrder()
    {
        DataFrame df = People();

        DataFrame result = df.Select(Functions.Col("name"), Functions.Col("age"));

        var project = Assert.IsType<Project>(result.Plan);
        Assert.Collection(
            project.ProjectList,
            e => Assert.Equal("name", Assert.IsType<UnresolvedAttribute>(e).Name),
            e => Assert.Equal("age", Assert.IsType<UnresolvedAttribute>(e).Name));
    }

    [Fact]
    public void Select_PreservesAliasExpressions()
    {
        DataFrame df = People();

        DataFrame result = df.Select(Functions.Col("age").As("years"));

        var project = Assert.IsType<Project>(result.Plan);
        Expression only = Assert.Single(project.ProjectList);
        Assert.Equal("years", Assert.IsType<Alias>(only).Name);
    }

    [Fact]
    public void Select_StarColumn_IsPreservedUnexpanded()
    {
        DataFrame df = People();

        DataFrame result = df.Select(Functions.Col("*"));

        var project = Assert.IsType<Project>(result.Plan);
        Expression only = Assert.Single(project.ProjectList);
        Assert.IsType<UnresolvedStar>(only);
    }

    [Fact]
    public void Select_Empty_BuildsEmptyProjection()
    {
        DataFrame df = People();

        DataFrame result = df.Select();

        var project = Assert.IsType<Project>(result.Plan);
        Assert.Empty(project.ProjectList);
    }

    [Fact]
    public void Select_LeavesSourceFrameUnchanged_AndSharesChildByReference()
    {
        DataFrame df = People();
        LogicalPlan original = df.Plan;

        DataFrame result = df.Select(Functions.Col("name"));

        // Source frame's plan is the exact same instance (immutable — no in-place mutation).
        Assert.Same(original, df.Plan);
        // The new Project reuses the source plan as its child (structural sharing).
        Assert.Same(original, ((Project)result.Plan).Child);
        Assert.NotSame(df.Plan, result.Plan);
    }

    // ----- AC1: Select (string overload) -----

    [Fact]
    public void Select_Names_BuildsProjectOfUnresolvedAttributes()
    {
        DataFrame df = People();

        DataFrame result = df.Select("name", "age");

        var project = Assert.IsType<Project>(result.Plan);
        Assert.Collection(
            project.ProjectList,
            e => Assert.Equal("name", Assert.IsType<UnresolvedAttribute>(e).Name),
            e => Assert.Equal("age", Assert.IsType<UnresolvedAttribute>(e).Name));
    }

    [Fact]
    public void Select_SingleName_BuildsSingleAttributeProjection()
    {
        DataFrame df = People();

        DataFrame result = df.Select("name");

        var project = Assert.IsType<Project>(result.Plan);
        Expression only = Assert.Single(project.ProjectList);
        Assert.Equal("name", Assert.IsType<UnresolvedAttribute>(only).Name);
    }

    [Fact]
    public void Select_StarName_BuildsUnresolvedStar()
    {
        DataFrame df = People();

        DataFrame result = df.Select("*");

        var project = Assert.IsType<Project>(result.Plan);
        Assert.IsType<UnresolvedStar>(Assert.Single(project.ProjectList));
    }

    // ----- AC2: Filter / Where -----

    [Fact]
    public void Filter_BuildsFilterWithConditionExpressionUnevaluated()
    {
        DataFrame df = People();
        Column condition = Functions.Col("age");

        DataFrame result = df.Filter(condition);

        var filter = Assert.IsType<Filter>(result.Plan);
        // The predicate is recorded by reference — not evaluated, not rewritten.
        Assert.Same(condition.Expr, filter.Condition);
        Assert.Same(df.Plan, filter.Child);
    }

    [Fact]
    public void Where_IsEquivalentToFilter()
    {
        DataFrame df = People();
        Column condition = Functions.Col("age");

        DataFrame viaFilter = df.Filter(condition);
        DataFrame viaWhere = df.Where(condition);

        // Same plan node shape (Where is a Spark-parity alias of Filter).
        Assert.Equal(viaFilter.Plan, viaWhere.Plan);
        Assert.IsType<Filter>(viaWhere.Plan);
    }

    [Fact]
    public void Filter_LeavesSourceFrameUnchanged()
    {
        DataFrame df = People();
        LogicalPlan original = df.Plan;

        _ = df.Filter(Functions.Col("age"));

        Assert.Same(original, df.Plan);
    }

    // ----- AC3: WithColumn shape (append & replace both build star + alias) -----

    [Fact]
    public void WithColumn_BuildsProjectOfStarThenAliasedColumn()
    {
        DataFrame df = People();

        DataFrame result = df.WithColumn("doubled", Functions.Col("age"));

        var project = Assert.IsType<Project>(result.Plan);
        Assert.Collection(
            project.ProjectList,
            e => Assert.IsType<UnresolvedStar>(e),
            e =>
            {
                var alias = Assert.IsType<Alias>(e);
                Assert.Equal("doubled", alias.Name);
                Assert.Equal("age", Assert.IsType<UnresolvedAttribute>(alias.Child).Name);
            });
        Assert.Same(df.Plan, project.Child);
    }

    [Fact]
    public void WithColumn_ReplacingExistingName_BuildsSameStarPlusAliasShape()
    {
        // Append and replace build the identical unresolved shape (star + alias). Spark's
        // replace-in-place is a name-resolution concern the analyzer owns (deferred, #398); the
        // DataFrame method's job is only to build the correct unresolved plan.
        DataFrame df = People();

        DataFrame result = df.WithColumn("age", Functions.Col("age"));

        var project = Assert.IsType<Project>(result.Plan);
        Assert.Collection(
            project.ProjectList,
            e => Assert.IsType<UnresolvedStar>(e),
            e => Assert.Equal("age", Assert.IsType<Alias>(e).Name));
    }

    [Fact]
    public void WithColumn_RewrapsAlreadyAliasedColumnWithGivenName()
    {
        DataFrame df = People();

        DataFrame result = df.WithColumn("final", Functions.Col("age").As("ignored"));

        var project = Assert.IsType<Project>(result.Plan);
        // The outer alias carries the WithColumn name; Spark's withColumn(name, col) names by `name`.
        var alias = Assert.IsType<Alias>(project.ProjectList[1]);
        Assert.Equal("final", alias.Name);
    }

    // ----- AC3: WithColumn append resolves end-to-end (append is fully correct today) -----

    [Fact]
    public void WithColumn_Append_ResolvesToOriginalColumnsPlusNewColumn()
    {
        var catalog = new LocalCatalog();
        catalog.Register("people", PeopleSchema);
        var analyzer = new Analyzer(catalog);
        DataFrame df = new(new UnresolvedRelation(new[] { "people" }));

        DataFrame appended = df.WithColumn("age_plus", Functions.Lit(1L).As("age_plus"));
        LogicalPlan resolved = analyzer.Resolve(appended.Plan);

        var project = Assert.IsType<Project>(resolved);
        // Star expanded to the three source columns, then the new column appended at the end.
        Assert.Collection(
            project.ProjectList,
            e => Assert.Equal("id", Assert.IsType<AttributeReference>(e).Name),
            e => Assert.Equal("name", Assert.IsType<AttributeReference>(e).Name),
            e => Assert.Equal("age", Assert.IsType<AttributeReference>(e).Name),
            e => Assert.Equal("age_plus", Assert.IsType<Alias>(e).Name));
    }

    // ----- AC3: WithColumn replace-in-place is NOT yet implemented (characterization tripwire) -----

    [Fact]
    public void WithColumn_ReplacingExistingName_ResolvesToDuplicateColumns_KnownWrong_Tripwire()
    {
        // CHARACTERIZATION TRIPWIRE (#398): replace-in-place is not implemented. Today WithColumn on an
        // EXISTING name resolves to TWO same-named outputs (star-expanded original + appended alias)
        // instead of Spark's single in-place replacement. This test pins that known-wrong behaviour so
        // it fails — prompting its removal — the moment #398 lands and replace-in-place is correct.
        var catalog = new LocalCatalog();
        catalog.Register("people", PeopleSchema);
        var analyzer = new Analyzer(catalog);
        DataFrame df = new(new UnresolvedRelation(new[] { "people" }));

        DataFrame replaced = df.WithColumn("age", Functions.Lit(99).As("age"));
        LogicalPlan resolved = analyzer.Resolve(replaced.Plan);

        var project = Assert.IsType<Project>(resolved);
        // Known-wrong: id, name, age (from star), age (appended) — a DUPLICATE age, not a replacement.
        Assert.Collection(
            project.ProjectList,
            e => Assert.Equal("id", Assert.IsType<AttributeReference>(e).Name),
            e => Assert.Equal("name", Assert.IsType<AttributeReference>(e).Name),
            e => Assert.Equal("age", Assert.IsType<AttributeReference>(e).Name),
            e => Assert.Equal("age", Assert.IsType<Alias>(e).Name));
    }

    // ----- Chaining preserves laziness/immutability of every intermediate frame -----

    [Fact]
    public void ChainedTransformations_BuildNestedPlan_LeavingEachStageIntact()
    {
        DataFrame df = People();
        LogicalPlan root = df.Plan;

        DataFrame chained = df
            .Filter(Functions.Col("age"))
            .Select("name", "age")
            .WithColumn("flag", Functions.Lit(true));

        // Outermost is the WithColumn Project over the Select Project over the Filter over the source.
        var withColumn = Assert.IsType<Project>(chained.Plan);
        var select = Assert.IsType<Project>(withColumn.Child);
        var filter = Assert.IsType<Filter>(select.Child);
        Assert.Same(root, filter.Child);
        // The original frame is untouched by all of the above.
        Assert.Same(root, df.Plan);
    }

    // ----- Null / empty argument guards -----

    [Fact]
    public void Select_NullColumnsArray_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentNullException>(() => df.Select((Column[])null!));
    }

    [Fact]
    public void Select_NullColumnElement_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentNullException>(() => df.Select(Functions.Col("a"), null!));
    }

    [Fact]
    public void Select_NullFirstName_Throws()
    {
        DataFrame df = People();
        Assert.ThrowsAny<ArgumentException>(() => df.Select((string)null!));
    }

    [Fact]
    public void Select_EmptyFirstName_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentException>(() => df.Select(string.Empty));
    }

    [Fact]
    public void Select_NullRestName_Throws()
    {
        DataFrame df = People();
        Assert.ThrowsAny<ArgumentException>(() => df.Select("a", null!));
    }

    [Fact]
    public void Select_NullRestElement_Throws()
    {
        // A null ELEMENT within a non-null rest array (distinct from Select_NullRestName_Throws, which
        // passes a null ARRAY). Each rest name flows through Functions.Col, whose ThrowIfNullOrEmpty
        // raises ArgumentNullException for a null element.
        DataFrame df = People();
        Assert.ThrowsAny<ArgumentException>(() => df.Select("a", "b", null!));
    }

    [Fact]
    public void Select_EmptyRestElement_Throws()
    {
        // An empty rest name is rejected per-element by Functions.Col's ThrowIfNullOrEmpty guard.
        DataFrame df = People();
        Assert.ThrowsAny<ArgumentException>(() => df.Select("a", string.Empty));
    }

    [Fact]
    public void Filter_NullCondition_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentNullException>(() => df.Filter(null!));
    }

    [Fact]
    public void Where_NullCondition_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentNullException>(() => df.Where(null!));
    }

    [Fact]
    public void WithColumn_NullName_Throws()
    {
        DataFrame df = People();
        Assert.ThrowsAny<ArgumentException>(() => df.WithColumn(null!, Functions.Col("a")));
    }

    [Fact]
    public void WithColumn_EmptyName_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentException>(() => df.WithColumn(string.Empty, Functions.Col("a")));
    }

    [Fact]
    public void WithColumn_NullColumn_Throws()
    {
        DataFrame df = People();
        Assert.Throws<ArgumentNullException>(() => df.WithColumn("x", null!));
    }
}
