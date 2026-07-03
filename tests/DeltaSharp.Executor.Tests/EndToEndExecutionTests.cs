using DeltaSharp.Engine.Execution;
using DeltaSharp.Types;
using Xunit;
using static DeltaSharp.Functions;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// End-to-end tests that run analyzed plans over the REAL EPIC-03 backend via the in-memory relation
/// fixture, asserting exact <see cref="Row"/> values and schema (STORY-04.6.2 AC — a supported plan
/// maps to executable operators and returns correct rows).
/// </summary>
public class EndToEndExecutionTests
{
    private static StructType PeopleSchema => TestData.Schema(
        TestData.Field("id", IntegerType.Instance, nullable: false),
        TestData.Field("dept", StringType.Instance),
        TestData.Field("salary", DoubleType.Instance));

    // id, dept, salary — one salary is NULL to exercise null-aware filter/sum.
    private static (InMemoryRelationFixture Fixture, DataFrame People) NewPeople()
    {
        var fixture = new InMemoryRelationFixture();
        DataFrame people = fixture.Relation("people", PeopleSchema, TestData.Batch(
            PeopleSchema,
            TestData.Ints(1, 2, 3, 4, 5),
            TestData.Strings("eng", "eng", "sales", "sales", "sales"),
            TestData.Doubles(100.0, 200.0, 300.0, 50.0, null)));
        return (fixture, people);
    }

    [Fact]
    public void FilterThenProject_ReturnsExactRows()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        IReadOnlyList<Row> rows = fixture.Collect(people.Filter(Col("salary").Gt(150.0)).Select(Col("id")));

        Assert.Equal(new[] { "id" }, rows[0].Schema.Select(f => f.Name));
        Assert.Equal(new[] { 2, 3 }, rows.Select(r => r.GetAs<int>(0)));
    }

    [Fact]
    public void GroupByAgg_SumsSalaryPerDept_NullExcluded()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        IReadOnlyList<Row> rows = fixture.Collect(people.GroupBy(Col("dept")).Agg(Sum(Col("salary"))));

        Assert.Equal(new[] { "dept", "sum(salary)" }, rows[0].Schema.Select(f => f.Name));
        Dictionary<string, double> byDept = rows.ToDictionary(r => r.GetAs<string>("dept"), r => r.GetAs<double>("sum(salary)"));
        Assert.Equal(300.0, byDept["eng"]);   // 100 + 200
        Assert.Equal(350.0, byDept["sales"]); // 300 + 50 (+ null excluded)
    }

    [Fact]
    public void FilterProjectGroupAgg_ComposeEndToEnd()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        DataFrame query = people
            .Filter(Col("salary").IsNotNull())
            .Select(Col("dept"), Col("salary"))
            .GroupBy(Col("dept"))
            .Agg(Sum(Col("salary")));

        IReadOnlyList<Row> rows = fixture.Collect(query);
        Dictionary<string, double> byDept = rows.ToDictionary(r => r.GetAs<string>("dept"), r => r.GetAs<double>(1));
        Assert.Equal(300.0, byDept["eng"]);
        Assert.Equal(350.0, byDept["sales"]);
    }

    [Fact]
    public void InnerJoin_ReturnsMatchedRows()
    {
        var fixture = new InMemoryRelationFixture();
        StructType empSchema = TestData.Schema(
            TestData.Field("empId", IntegerType.Instance, nullable: false),
            TestData.Field("deptId", IntegerType.Instance, nullable: false));
        StructType deptSchema = TestData.Schema(
            TestData.Field("dId", IntegerType.Instance, nullable: false),
            TestData.Field("dname", StringType.Instance));

        DataFrame emp = fixture.Relation("emp", empSchema, TestData.Batch(
            empSchema, TestData.Ints(1, 2, 3), TestData.Ints(10, 20, 10)));
        DataFrame dept = fixture.Relation("dept", deptSchema, TestData.Batch(
            deptSchema, TestData.Ints(10, 20), TestData.Strings("eng", "sales")));

        IReadOnlyList<Row> rows = fixture.Collect(emp.Join(dept, Col("deptId").EqualTo(Col("dId"))));

        Assert.Equal(3, rows.Count);
        Dictionary<int, string> nameByEmp = rows.ToDictionary(r => r.GetAs<int>("empId"), r => r.GetAs<string>("dname"));
        Assert.Equal("eng", nameByEmp[1]);
        Assert.Equal("sales", nameByEmp[2]);
        Assert.Equal("eng", nameByEmp[3]);
    }

    [Fact]
    public void OrderByDescThenLimit_TruncatesSortedRows()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        IReadOnlyList<Row> rows = fixture.Collect(people.OrderBy(Col("id").Desc()).Limit(2).Select(Col("id")));

        Assert.Equal(new[] { 5, 4 }, rows.Select(r => r.GetAs<int>(0)));
    }

    [Fact]
    public void Limit_KeepsLeadingRows()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        IReadOnlyList<Row> rows = fixture.Collect(people.Limit(2).Select(Col("id")));

        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.GetAs<int>(0)));
    }

    [Fact]
    public void Distinct_DeduplicatesRows()
    {
        var fixture = new InMemoryRelationFixture();
        StructType schema = TestData.Schema(TestData.Field("v", IntegerType.Instance, nullable: false));
        DataFrame df = fixture.Relation("dups", schema, TestData.Batch(
            schema, TestData.Ints(1, 1, 2, 3, 3, 3)));

        IReadOnlyList<Row> rows = fixture.Collect(df.Distinct());

        Assert.Equal(new[] { "v" }, rows[0].Schema.Select(f => f.Name));
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.GetAs<int>(0)).OrderBy(v => v));
    }

    [Fact]
    public void Union_ConcatenatesBothInputs()
    {
        var fixture = new InMemoryRelationFixture();
        DataFrame a = fixture.Relation("ua", PeopleSchema, TestData.Batch(
            PeopleSchema, TestData.Ints(1, 2), TestData.Strings("eng", "eng"), TestData.Doubles(1.0, 2.0)));
        DataFrame b = fixture.Relation("ub", PeopleSchema, TestData.Batch(
            PeopleSchema, TestData.Ints(3, 4), TestData.Strings("sales", "sales"), TestData.Doubles(3.0, 4.0)));

        IReadOnlyList<Row> rows = fixture.Collect(a.Union(b).Select(Col("id")));

        Assert.Equal(4, rows.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, rows.Select(r => r.GetAs<int>(0)).OrderBy(v => v));
    }

    [Fact]
    public void Count_MatchesCollectCount_Scan()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        Assert.Equal(5, fixture.Count(people));
        Assert.Equal(fixture.Collect(people).Count, fixture.Count(people));
    }

    [Fact]
    public void Count_MatchesCollectCount_AfterFilterAndGroup()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        DataFrame filtered = people.Filter(Col("salary").Gt(150.0));
        Assert.Equal(2, fixture.Count(filtered));

        DataFrame grouped = people.GroupBy(Col("dept")).Agg(Sum(Col("salary")));
        Assert.Equal(fixture.Collect(grouped).Count, fixture.Count(grouped));
    }

    [Fact]
    public void InterpretedAndDefaultBackends_ProduceIdenticalRows()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        DataFrame query = people.GroupBy(Col("dept")).Agg(Sum(Col("salary")));

        var forced = new ExecutionBackendOptions { ForceInterpreted = true };
        Dictionary<string, double> interpreted = fixture.Collect(query, forced)
            .ToDictionary(r => r.GetAs<string>("dept"), r => r.GetAs<double>(1));
        Dictionary<string, double> auto = fixture.Collect(query, ExecutionBackendOptions.Default)
            .ToDictionary(r => r.GetAs<string>("dept"), r => r.GetAs<double>(1));

        Assert.Equal(interpreted, auto);
    }
}
