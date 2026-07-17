using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Types;
using Xunit;
using static DeltaSharp.Functions;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// Physical-plan SHAPE tests: for each supported logical node, assert the mapped physical operator
/// tree (STORY-04.6.2 AC — each supported node maps to an EPIC-03 executable operator).
/// </summary>
public class PhysicalPlanShapeTests
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
    public void Scan_MapsTo_ScanPlan()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        PhysicalPlan plan = fixture.Plan(people);
        Assert.IsType<ScanPlan>(plan);
        Assert.Empty(plan.Children);
    }

    [Fact]
    public void Filter_MapsTo_FilterOperator_OverScan()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        PhysicalPlan plan = fixture.Plan(people.Filter(Col("salary").Gt(150.0)));

        FilterPlan filter = Assert.IsType<FilterPlan>(plan);
        Assert.IsType<ComparisonExpression>(filter.Predicate);
        Assert.IsType<ScanPlan>(Assert.Single(filter.Children));
    }

    [Fact]
    public void Filter_OnNestedField_MapsTo_StructFieldExpression_OverScan()
    {
        // #580: a multipart reference `addr.zip` resolves to a struct-field extraction and translates
        // to an Engine StructFieldExpression (nested field access), wiring analyzer -> translator.
        StructType addr = TestData.Schema(TestData.Field("zip", IntegerType.Instance, nullable: false));
        StructType schema = TestData.Schema(
            TestData.Field("id", IntegerType.Instance, nullable: false),
            TestData.Field("addr", addr, nullable: true));
        var fixture = new InMemoryRelationFixture();
        DataFrame people = fixture.Relation("people", schema, TestData.Batch(
            schema, TestData.Ints(1), new StructColumnVector(addr, new[] { TestData.Ints(90210) })));

        PhysicalPlan plan = fixture.Plan(people.Filter(Col("addr.zip").Gt(0)));

        FilterPlan filter = Assert.IsType<FilterPlan>(plan);
        ComparisonExpression predicate = Assert.IsType<ComparisonExpression>(filter.Predicate);
        StructFieldExpression field = Assert.IsType<StructFieldExpression>(predicate.Left);
        Assert.Equal(0, field.Ordinal);
        Assert.Equal(IntegerType.Instance, field.Type);
        Assert.IsType<ColumnReference>(field.Child);
        Assert.IsType<ScanPlan>(Assert.Single(filter.Children));
    }

    [Fact]
    public void Project_MapsTo_ProjectOperator_OverScan()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        PhysicalPlan plan = fixture.Plan(people.Select(Col("id"), Col("salary")));

        ProjectPlan project = Assert.IsType<ProjectPlan>(plan);
        Assert.Equal(2, project.Projections.Count);
        Assert.Equal(new[] { "id", "salary" }, project.OutputSchema.Select(f => f.Name));
        Assert.IsType<ScanPlan>(Assert.Single(project.Children));
    }

    [Fact]
    public void GroupByAgg_MapsTo_AggregateOperator()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        PhysicalPlan plan = fixture.Plan(people.GroupBy(Col("dept")).Agg(Sum(Col("salary"))));

        AggregatePlan aggregate = Assert.IsType<AggregatePlan>(plan);
        Assert.Single(aggregate.GroupingKeys);
        AggregateExpression agg = Assert.Single(aggregate.Aggregates);
        Assert.Equal(AggregateFunction.Sum, agg.Function);
        Assert.Equal(new[] { "dept", "sum(salary)" }, aggregate.OutputSchema.Select(f => f.Name));
    }

    [Fact]
    public void Sort_MapsTo_SortOperator()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        PhysicalPlan plan = fixture.Plan(people.OrderBy(Col("salary").Desc()));

        SortPlan sort = Assert.IsType<SortPlan>(plan);
        SortOrder order = Assert.Single(sort.SortOrders);
        Assert.Equal(SortDirection.Descending, order.Direction);
        Assert.IsType<ScanPlan>(Assert.Single(sort.Children));
    }

    [Fact]
    public void Limit_MapsTo_LimitPlan_Bridge()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        PhysicalPlan plan = fixture.Plan(people.Limit(5));

        LimitPlan limit = Assert.IsType<LimitPlan>(plan);
        Assert.Equal(5, limit.Count);
        Assert.IsType<ScanPlan>(Assert.Single(limit.Children));
    }

    [Fact]
    public void Distinct_LowersTo_ProjectOverAggregate()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        PhysicalPlan plan = fixture.Plan(people.Distinct());

        ProjectPlan project = Assert.IsType<ProjectPlan>(plan);
        AggregatePlan aggregate = Assert.IsType<AggregatePlan>(Assert.Single(project.Children));
        Assert.Equal(3, aggregate.GroupingKeys.Count); // group by all three columns
        Assert.Equal(AggregateFunction.Count, Assert.Single(aggregate.Aggregates).Function);
        Assert.Equal(new[] { "id", "dept", "salary" }, project.OutputSchema.Select(f => f.Name));
    }

    [Fact]
    public void Join_MapsTo_JoinOperator_WithTwoScans()
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

        PhysicalPlan plan = fixture.Plan(emp.Join(dept, Col("deptId").EqualTo(Col("dId"))));

        JoinPlan join = Assert.IsType<JoinPlan>(plan);
        Assert.Equal(JoinType.Inner, join.JoinType);
        Assert.Single(join.LeftKeys);
        Assert.Single(join.RightKeys);
        Assert.Equal(2, join.Children.Count);
        Assert.All(join.Children, child => Assert.IsType<ScanPlan>(child));
        Assert.Equal(new[] { "empId", "deptId", "dId", "dname" }, join.OutputSchema.Select(f => f.Name));
    }

    [Fact]
    public void Union_MapsTo_UnionPlan_WithTwoChildren()
    {
        var fixture = new InMemoryRelationFixture();
        DataFrame a = fixture.Relation("a", PeopleSchema, TestData.Batch(
            PeopleSchema, TestData.Ints(1), TestData.Strings("eng"), TestData.Doubles(100.0)));
        DataFrame b = fixture.Relation("b", PeopleSchema, TestData.Batch(
            PeopleSchema, TestData.Ints(2), TestData.Strings("sales"), TestData.Doubles(200.0)));

        PhysicalPlan plan = fixture.Plan(a.Union(b));

        UnionPlan union = Assert.IsType<UnionPlan>(plan);
        Assert.Equal(2, union.Children.Count);
    }
}
