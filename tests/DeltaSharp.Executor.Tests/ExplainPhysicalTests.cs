using DeltaSharp.Types;
using Xunit;
using static DeltaSharp.Functions;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// STORY-04.7.3 (#179) AC3: physical-mode <c>EXPLAIN</c>. Renders the physical operator tree — scans,
/// filters, projections, joins, aggregates, sorts, limits, unions — through the registered
/// <see cref="LocalQueryExecutor"/> seam (<c>IQueryExecutor.ExplainPhysical</c>), <b>without executing</b>
/// the query. Unsupported plans render a diagnostic line rather than throwing (AC4).
/// </summary>
public class ExplainPhysicalTests
{
    private static StructType PeopleSchema => TestData.Schema(
        TestData.Field("id", IntegerType.Instance, nullable: false),
        TestData.Field("dept", StringType.Instance),
        TestData.Field("salary", DoubleType.Instance));

    private static (InMemoryRelationFixture Fixture, DataFrame People) NewPeople()
    {
        var fixture = new InMemoryRelationFixture();
        DataFrame people = fixture.Relation("people", PeopleSchema, TestData.Batch(
            PeopleSchema, TestData.Ints(1), TestData.Strings("eng"), TestData.Doubles(100.0)));
        return (fixture, people);
    }

    [Fact]
    public void Explain_Scan_RendersScanNode()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        string text = fixture.ExplainPhysical(people);
        Assert.StartsWith("Scan [id, dept, salary]", text);
    }

    [Fact]
    public void Explain_Filter_RendersFilterOverScan()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        string text = fixture.ExplainPhysical(people.Filter(Col("salary").Gt(150.0)));

        string[] lines = text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Filter", lines[0]);
        Assert.Contains("+- Scan", lines[1]);
    }

    [Fact]
    public void Explain_Project_RendersProjectNode()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        string text = fixture.ExplainPhysical(people.Select(Col("id"), Col("salary")));
        Assert.StartsWith("Project [id, salary]", text);
        Assert.Contains("+- Scan", text);
    }

    [Fact]
    public void Explain_Aggregate_RendersAggregateNode()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        string text = fixture.ExplainPhysical(people.GroupBy(Col("dept")).Agg(Sum(Col("salary"))));
        Assert.Contains("Aggregate", text);
        Assert.Contains("functions=[sum(", text);
    }

    [Fact]
    public void Explain_Sort_RendersSortNode()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        string text = fixture.ExplainPhysical(people.OrderBy(Col("salary").Desc()));
        Assert.Contains("Sort [", text);
        Assert.Contains("DESC", text);
    }

    [Fact]
    public void Explain_Limit_RendersLimitNode()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        string text = fixture.ExplainPhysical(people.Limit(5));
        Assert.StartsWith("Limit 5", text);
    }

    [Fact]
    public void Explain_Distinct_RendersProjectOverAggregate()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        string text = fixture.ExplainPhysical(people.Distinct());
        Assert.StartsWith("Project", text);
        Assert.Contains("Aggregate", text);
    }

    [Fact]
    public void Explain_Join_RendersJoinNode_WithTwoScans()
    {
        var fixture = new InMemoryRelationFixture();
        StructType empSchema = TestData.Schema(
            TestData.Field("empId", IntegerType.Instance, nullable: false),
            TestData.Field("deptId", IntegerType.Instance, nullable: false));
        StructType deptSchema = TestData.Schema(
            TestData.Field("dId", IntegerType.Instance, nullable: false),
            TestData.Field("dname", StringType.Instance));

        DataFrame emp = fixture.Relation("emp", empSchema, TestData.Batch(
            empSchema, TestData.Ints(1), TestData.Ints(10)));
        DataFrame dept = fixture.Relation("dept", deptSchema, TestData.Batch(
            deptSchema, TestData.Ints(10), TestData.Strings("eng")));

        string text = fixture.ExplainPhysical(emp.Join(dept, Col("deptId").EqualTo(Col("dId"))));

        Assert.Contains("Join Inner", text);
        // Both inputs render as branch scans under the join.
        Assert.Contains(":- Scan", text);
        Assert.Contains("+- Scan", text);
    }

    [Fact]
    public void Explain_Union_RendersUnionNode_WithBranchConnectors()
    {
        var fixture = new InMemoryRelationFixture();
        DataFrame a = fixture.Relation("ua", PeopleSchema, TestData.Batch(
            PeopleSchema, TestData.Ints(1), TestData.Strings("eng"), TestData.Doubles(100.0)));
        DataFrame b = fixture.Relation("ub", PeopleSchema, TestData.Batch(
            PeopleSchema, TestData.Ints(2), TestData.Strings("sales"), TestData.Doubles(200.0)));

        string text = fixture.ExplainPhysical(a.Union(b));

        Assert.StartsWith("Union", text);
        Assert.Contains(":- Scan", text);
        Assert.Contains("+- Scan", text);
    }

    // ----- AC4: an unsupported node renders a diagnostic, not a thrown UnsupportedPlanException -----

    [Fact]
    public void Explain_UnsupportedNode_RendersDiagnostic_NotThrow()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        // A cross join has no EPIC-03 equi-join mapping in M1 -> UnsupportedPlanException at planning time,
        // which ExplainPhysical must render as a diagnostic line rather than rethrow.
        DataFrame other = fixture.Relation("other", PeopleSchema, TestData.Batch(
            PeopleSchema, TestData.Ints(9), TestData.Strings("x"), TestData.Doubles(9.0)));

        string text = fixture.ExplainPhysical(people.CrossJoin(other));

        Assert.StartsWith("<cannot plan physically:", text);
    }

    // ----- No execution: physical rendering plans but does not run -----

    [Fact]
    public void Explain_RendersTree_WithoutMaterializingRows()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        DataFrame query = people.Filter(Col("salary").Gt(150.0)).Select(Col("id"));

        string explained = fixture.ExplainPhysical(query);

        // The filter excludes the only row; had Explain executed, a collect would return zero rows. Explain
        // instead returns the operator tree, and a SEPARATE collect is the only path that runs.
        Assert.Contains("Project", explained);
        Assert.Contains("Filter", explained);
        Assert.Empty(fixture.Collect(query));
    }

    // ----- Full Core -> Executor seam through a registered session -----

    [Fact]
    public void Explain_ThroughRegisteredSessionExecutor_RendersPhysicalPlan()
    {
        var fixture = new InMemoryRelationFixture(useDefaultScanSource: true);
        DataFrame people = fixture.Relation("people_explain_seam", PeopleSchema, TestData.Batch(
            PeopleSchema, TestData.Ints(1, 2), TestData.Strings("eng", "sales"), TestData.Doubles(100.0, 200.0)));

        using SparkSession session = SparkSession.Builder().AppName("explain-seam").GetOrCreate();

        string text = fixture.ExplainPhysicalViaSession(session, people.Filter(Col("salary").Gt(150.0)));

        Assert.Contains("Filter", text);
        Assert.Contains("Scan", text);
    }
}
