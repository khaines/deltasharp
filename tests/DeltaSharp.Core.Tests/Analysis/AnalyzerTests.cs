using DeltaSharp.Analysis;
using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Analysis;

/// <summary>
/// STORY-04.5.1 — the local catalog and schema-resolution analyzer. These tests cover the four
/// acceptance criteria: relation binding to an ADR-0008 schema (AC1), attribute references
/// resolving to stable-id attributes (AC2), Spark-compatible missing/ambiguous diagnostics (AC3),
/// and catalog-miss self-containment with no execution (AC4), plus star expansion, determinism,
/// and the resolved-vs-unresolved contract.
/// </summary>
public sealed class AnalyzerTests
{
    private static readonly StructType PeopleSchema = new(new[]
    {
        new StructField("id", LongType.Instance, nullable: false),
        new StructField("name", StringType.Instance, nullable: true),
        new StructField("age", IntegerType.Instance, nullable: true),
    });

    private static LocalCatalog CatalogWithPeople()
    {
        var catalog = new LocalCatalog();
        catalog.Register("people", PeopleSchema);
        return catalog;
    }

    private static UnresolvedRelation Relation(string name) => new(new[] { name });

    // ---- AC1: relation binding ----

    [Fact]
    public void ResolveRelations_BindsUnresolvedRelation_ToSchemaWithAdr0008Types()
    {
        var analyzer = new Analyzer(CatalogWithPeople());

        LogicalPlan resolved = analyzer.Resolve(Relation("people"));

        var relation = Assert.IsType<ResolvedRelation>(resolved);
        Assert.True(relation.Resolved);
        Assert.Equal(PeopleSchema, relation.Schema);
        Assert.Collection(
            relation.Output,
            a => AssertAttribute(a, "id", LongType.Instance, nullable: false),
            a => AssertAttribute(a, "name", StringType.Instance, nullable: true),
            a => AssertAttribute(a, "age", IntegerType.Instance, nullable: true));
    }

    [Fact]
    public void ResolveRelations_AssignsMonotonicExprIds_StartingAtZero()
    {
        var analyzer = new Analyzer(CatalogWithPeople());

        var relation = Assert.IsType<ResolvedRelation>(analyzer.Resolve(Relation("people")));

        Assert.Equal(new ExprId(0), relation.Output[0].ExprId);
        Assert.Equal(new ExprId(1), relation.Output[1].ExprId);
        Assert.Equal(new ExprId(2), relation.Output[2].ExprId);
    }

    [Fact]
    public void ResolveRelations_IsCaseInsensitive_ForTableNames()
    {
        var analyzer = new Analyzer(CatalogWithPeople());

        LogicalPlan resolved = analyzer.Resolve(Relation("PEOPLE"));

        Assert.IsType<ResolvedRelation>(resolved);
    }

    // ---- AC2: attribute resolution ----

    [Fact]
    public void ResolveReferences_BindsAttributes_WithStableIdsNamesTypesNullability()
    {
        var analyzer = new Analyzer(CatalogWithPeople());
        var plan = new Project(
            new Expression[] { new UnresolvedAttribute("name"), new UnresolvedAttribute("age") },
            Relation("people"));

        var project = Assert.IsType<Project>(analyzer.Resolve(plan));

        Assert.True(project.Resolved);
        var name = Assert.IsType<AttributeReference>(project.ProjectList[0]);
        var age = Assert.IsType<AttributeReference>(project.ProjectList[1]);
        AssertAttribute(name, "name", StringType.Instance, nullable: true);
        AssertAttribute(age, "age", IntegerType.Instance, nullable: true);
    }

    [Fact]
    public void ResolveReferences_ReuseScanExprId_SoColumnAndReferenceShareIdentity()
    {
        var analyzer = new Analyzer(CatalogWithPeople());
        var plan = new Project(
            new Expression[] { new UnresolvedAttribute("age") },
            Relation("people"));

        var project = Assert.IsType<Project>(analyzer.Resolve(plan));

        var scan = Assert.IsType<ResolvedRelation>(project.Child);
        var reference = Assert.IsType<AttributeReference>(project.ProjectList[0]);
        AttributeReference scanAge = scan.Output.Single(a => a.Name == "age");
        Assert.Equal(scanAge.ExprId, reference.ExprId);
    }

    [Fact]
    public void ResolveReferences_ResolvesAttributesInsideFilterPredicate()
    {
        var analyzer = new Analyzer(CatalogWithPeople());
        var plan = new Filter(
            new BinaryComparison(
                new UnresolvedAttribute("age"),
                Literal.OfInt(21),
                ComparisonOperator.GreaterThan),
            Relation("people"));

        var filter = Assert.IsType<Filter>(analyzer.Resolve(plan));

        Assert.True(filter.Resolved);
        var comparison = Assert.IsType<BinaryComparison>(filter.Condition);
        AssertAttribute((AttributeReference)comparison.Left, "age", IntegerType.Instance, nullable: true);
    }

    [Fact]
    public void ResolveReferences_MatchesColumnNamesCaseInsensitively()
    {
        var analyzer = new Analyzer(CatalogWithPeople());
        var plan = new Project(
            new Expression[] { new UnresolvedAttribute("AGE") },
            Relation("people"));

        var project = Assert.IsType<Project>(analyzer.Resolve(plan));

        var reference = Assert.IsType<AttributeReference>(project.ProjectList[0]);
        Assert.Equal("age", reference.Name);
    }

    // ---- Star expansion ----

    [Fact]
    public void ResolveReferences_ExpandsStar_ToChildOutput()
    {
        var analyzer = new Analyzer(CatalogWithPeople());
        var plan = new Project(new Expression[] { new UnresolvedStar() }, Relation("people"));

        var project = Assert.IsType<Project>(analyzer.Resolve(plan));

        Assert.True(project.Resolved);
        Assert.Collection(
            project.ProjectList,
            a => Assert.Equal("id", ((AttributeReference)a).Name),
            a => Assert.Equal("name", ((AttributeReference)a).Name),
            a => Assert.Equal("age", ((AttributeReference)a).Name));
    }

    [Fact]
    public void ResolveReferences_ExpandsStar_PreservingSurroundingElements()
    {
        var analyzer = new Analyzer(CatalogWithPeople());
        var plan = new Project(
            new Expression[] { new UnresolvedAttribute("id"), new UnresolvedStar() },
            Relation("people"));

        var project = Assert.IsType<Project>(analyzer.Resolve(plan));

        Assert.Equal(4, project.ProjectList.Count);
        Assert.Equal("id", ((AttributeReference)project.ProjectList[0]).Name);
        Assert.Equal("age", ((AttributeReference)project.ProjectList[3]).Name);
    }

    // ---- Determinism ----

    [Fact]
    public void Resolve_IsDeterministic_AcrossRuns()
    {
        var plan = new Project(
            new Expression[] { new UnresolvedAttribute("name"), new UnresolvedAttribute("age") },
            new Filter(
                new BinaryComparison(
                    new UnresolvedAttribute("age"), Literal.OfInt(21), ComparisonOperator.GreaterThan),
                Relation("people")));

        LogicalPlan first = new Analyzer(CatalogWithPeople()).Resolve(plan);
        LogicalPlan second = new Analyzer(CatalogWithPeople()).Resolve(plan);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    // ---- AC3: diagnostics ----

    [Fact]
    public void MissingColumn_ThrowsUnresolvedColumn_NamingReferenceAndCandidates()
    {
        var analyzer = new Analyzer(CatalogWithPeople());
        var plan = new Project(
            new Expression[] { new UnresolvedAttribute("salary") },
            Relation("people"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(plan));

        Assert.Equal(AnalysisErrorKind.UnresolvedColumn, ex.Kind);
        Assert.Equal("salary", ex.Reference);
        Assert.Contains("salary", ex.Message);
        Assert.Equal(new[] { "id", "name", "age" }, ex.Candidates);
        Assert.Contains("id, name, age", ex.Message);
    }

    [Fact]
    public void AmbiguousColumn_ThrowsAmbiguousReference_ListingBothCandidates()
    {
        var catalog = new LocalCatalog();
        var left = new StructType(new[] { new StructField("id", LongType.Instance) });
        var right = new StructType(new[] { new StructField("id", LongType.Instance) });
        catalog.Register("l", left);
        catalog.Register("r", right);
        var analyzer = new Analyzer(catalog);
        var join = new Join(Relation("l"), Relation("r"), JoinType.Inner);
        var plan = new Project(new Expression[] { new UnresolvedAttribute("id") }, join);

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(plan));

        Assert.Equal(AnalysisErrorKind.AmbiguousReference, ex.Kind);
        Assert.Equal("id", ex.Reference);
        Assert.Equal(2, ex.Candidates.Count);
        // AC3: the diagnostic must name the candidate attributes (id#<left>, id#<right>), not just
        // report the count. Left ids are 0 (l.id) and 1 (r.id) in scan/bottom-up order.
        Assert.Equal(new[] { "id#0", "id#1" }, ex.Candidates);
        Assert.Contains("ambiguous", ex.Message);
        Assert.Contains("id#0", ex.Message);
        Assert.Contains("id#1", ex.Message);
    }

    // ---- AC4: catalog miss is self-contained ----

    [Fact]
    public void CatalogMiss_ThrowsTableOrViewNotFound_NamingIdentifier()
    {
        var analyzer = new Analyzer(new LocalCatalog());

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(Relation("ghost")));

        Assert.Equal(AnalysisErrorKind.TableOrViewNotFound, ex.Kind);
        Assert.Equal("ghost", ex.Reference);
        Assert.Contains("Table or view not found: ghost", ex.Message);
    }

    [Fact]
    public void CatalogMiss_ThrowsBeforeResolvingAnyReferences()
    {
        // AC4: a relation miss aborts analysis at ResolveRelations — the enclosing Project's
        // attribute is never bound and no physical/backend step runs (there is none to run).
        var analyzer = new Analyzer(new LocalCatalog());
        var plan = new Project(
            new Expression[] { new UnresolvedAttribute("whatever") },
            Relation("ghost"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(plan));

        Assert.Equal(AnalysisErrorKind.TableOrViewNotFound, ex.Kind);
    }

    // ---- F10: alias output contract ----

    [Fact]
    public void ResolveReferences_AliasOutput_HasFreshDistinctExprId_AndForwardedType()
    {
        // An Alias over a resolved attribute exposes a NEW output attribute (fresh ExprId, distinct
        // from the aliased column's id) that forwards the child's type. Observe the alias output by
        // referencing it by name from an enclosing projection.
        var analyzer = new Analyzer(CatalogWithPeople());
        var inner = new Project(
            new Expression[] { new Alias(new UnresolvedAttribute("age"), "age_copy") },
            Relation("people"));
        var outer = new Project(
            new Expression[] { new UnresolvedAttribute("age_copy") }, inner);

        var resolvedOuter = Assert.IsType<Project>(analyzer.Resolve(outer));

        var aliasOutput = Assert.IsType<AttributeReference>(resolvedOuter.ProjectList[0]);
        AssertAttribute(aliasOutput, "age_copy", IntegerType.Instance, nullable: true);

        var resolvedInner = Assert.IsType<Project>(resolvedOuter.Child);
        var scan = Assert.IsType<ResolvedRelation>(resolvedInner.Child);
        AttributeReference scanAge = scan.Output.Single(a => a.Name == "age");
        Assert.NotEqual(scanAge.ExprId, aliasOutput.ExprId);
    }

    // ---- F1: LeftSemi/LeftAnti join output is left-only ----

    private static LocalCatalog CatalogWithTwoSides()
    {
        var catalog = new LocalCatalog();
        catalog.Register("l", new StructType(new[] { new StructField("lid", LongType.Instance) }));
        catalog.Register("r", new StructType(new[] { new StructField("rid", LongType.Instance) }));
        return catalog;
    }

    [Fact]
    public void ResolveReferences_LeftSemiJoin_OutputIsLeftOnly()
    {
        var analyzer = new Analyzer(CatalogWithTwoSides());
        var join = new Join(Relation("l"), Relation("r"), JoinType.LeftSemi);
        var plan = new Project(new Expression[] { new UnresolvedStar() }, join);

        var project = Assert.IsType<Project>(analyzer.Resolve(plan));

        // The star expands to the join's output, which for a LeftSemi join is the left side only.
        Assert.Collection(
            project.ProjectList,
            a => Assert.Equal("lid", ((AttributeReference)a).Name));
    }

    [Fact]
    public void ResolveReferences_LeftSemiJoin_RightColumnAbove_IsUnresolved()
    {
        var analyzer = new Analyzer(CatalogWithTwoSides());
        var join = new Join(Relation("l"), Relation("r"), JoinType.LeftSemi);
        var plan = new Project(new Expression[] { new UnresolvedAttribute("rid") }, join);

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(plan));

        Assert.Equal(AnalysisErrorKind.UnresolvedColumn, ex.Kind);
        Assert.Equal("rid", ex.Reference);
    }

    [Fact]
    public void ResolveReferences_LeftAntiJoin_OutputIsLeftOnly()
    {
        var analyzer = new Analyzer(CatalogWithTwoSides());
        var join = new Join(Relation("l"), Relation("r"), JoinType.LeftAnti);
        var plan = new Project(new Expression[] { new UnresolvedStar() }, join);

        var project = Assert.IsType<Project>(analyzer.Resolve(plan));

        Assert.Collection(
            project.ProjectList,
            a => Assert.Equal("lid", ((AttributeReference)a).Name));
    }

    [Fact]
    public void ResolveReferences_InnerJoin_OutputIsBothSides()
    {
        var analyzer = new Analyzer(CatalogWithTwoSides());
        var join = new Join(Relation("l"), Relation("r"), JoinType.Inner);
        var plan = new Project(new Expression[] { new UnresolvedStar() }, join);

        var project = Assert.IsType<Project>(analyzer.Resolve(plan));

        Assert.Collection(
            project.ProjectList,
            a => Assert.Equal("lid", ((AttributeReference)a).Name),
            a => Assert.Equal("rid", ((AttributeReference)a).Name));
    }

    // ---- F2: CheckAnalysis post-condition ----

    [Fact]
    public void CheckAnalysis_StarOutsideProject_ThrowsUnresolvedPlan()
    {
        // A star can only be expanded inside a Project; a star elsewhere (here a Filter condition)
        // survives resolution. CheckAnalysis must fail LOUDLY rather than return an unresolved plan.
        var analyzer = new Analyzer(CatalogWithPeople());
        var plan = new Filter(new UnresolvedStar(), Relation("people"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(plan));

        Assert.Equal(AnalysisErrorKind.UnresolvedPlan, ex.Kind);
        Assert.Equal("*", ex.Reference);
        Assert.Contains("Filter", ex.Message);
    }

    [Fact]
    public void CheckAnalysis_ResidualUnresolvedFunction_ThrowsUnresolvedPlan()
    {
        // Function resolution is a later story; a residual UnresolvedFunction leaves the plan
        // not-fully-resolved. CheckAnalysis names the offending reference and throws.
        var analyzer = new Analyzer(CatalogWithPeople());
        var plan = new Filter(
            new UnresolvedFunction("upper", new Expression[] { new UnresolvedAttribute("name") }),
            Relation("people"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(plan));

        Assert.Equal(AnalysisErrorKind.UnresolvedPlan, ex.Kind);
        Assert.Equal("upper", ex.Reference);
    }

    private static void AssertAttribute(
        AttributeReference attribute, string name, DataType type, bool nullable)
    {
        Assert.Equal(name, attribute.Name);
        Assert.Equal(type, attribute.Type);
        Assert.Equal(nullable, attribute.Nullable);
    }
}
