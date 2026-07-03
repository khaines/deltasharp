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
    public void Explain_Project_RendersColumnReferences_WithNames()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        string text = fixture.ExplainPhysical(people.Select(Col("id"), Col("salary")));
        // Bare pass-through columns render as name#ordinal (resolved against the child schema), not just names.
        Assert.StartsWith("Project [id#0, salary#2]", text);
        Assert.Contains("+- Scan", text);
    }

    [Fact]
    public void Explain_Project_RendersComputedProjectionExpressions_WithAlias()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        // A computed projection must show the EXPRESSION (not just the output name), aliased to its
        // output column — otherwise Select((a+b).As("c")) hides the computation behind a bare name (AC3).
        string text = fixture.ExplainPhysical(people.Select((Col("id") + Col("id")).As("twice")));
        Assert.StartsWith("Project [(id#0 + id#0) AS twice]", text);
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

    // ----- AC4 (broad, non-UnsupportedPlanException faults): PhysicalPlanner.Plan builds Engine
    // expressions during planning, and some Engine expression constructors throw a RAW ArgumentException
    // on operand-type combinations the analyzer ACCEPTS but the interpreted backend has no kernel for.
    // ExplainPhysical must render these as a diagnostic too (never rethrow), so Explain still shows the
    // logical sections. These exercise the REAL LocalQueryExecutor throwing path (the coverage the
    // stub FakeQueryExecutor.ExplainPhysical cannot reach). -----

    [Fact]
    public void Explain_NullEqualsNull_RendersDiagnostic_NotThrow()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        // `lit(null) == lit(null)` type-checks (void == void) in the analyzer, but the Engine
        // ComparisonExpression ctor throws ArgumentException while PLANNING (no void comparison kernel).
        string text = fixture.ExplainPhysical(people.Filter(Lit(null).EqualTo(Lit(null))));

        Assert.StartsWith("<cannot plan physically:", text);
        Assert.Contains("void", text);
    }

    [Fact]
    public void Explain_ComplexTypedEquality_RendersDiagnostic_NotThrow()
    {
        var fixture = new InMemoryRelationFixture();
        StructType arraySchema = TestData.Schema(
            TestData.Field("arr", new ArrayType(IntegerType.Instance, containsNull: true)));
        // No batches needed: planning only reads the batch REFERENCE, never the rows, before the
        // ComparisonExpression ctor throws — so the diagnostic is produced without touching data.
        DataFrame arrays = fixture.Relation("arrs", arraySchema);

        // `array<int> == array<int>` type-checks in the analyzer, but the Engine has no complex-typed
        // comparison kernel, so the ComparisonExpression ctor throws ArgumentException during planning.
        string text = fixture.ExplainPhysical(arrays.Filter(Col("arr").EqualTo(Col("arr"))));

        Assert.StartsWith("<cannot plan physically:", text);
        Assert.Contains("array<int>", text);
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

    // ----- No execution (rigorous): a scan source whose batches THROW/COUNT on any access proves
    // ExplainPhysical is planner-only. If ExplainPhysical were changed to execute, the sentinel would be
    // read (AccessCount > 0) and the broad AC4 catch would turn the read into a diagnostic — so ALL of
    // these assertions would fail. Collect, which DOES execute, arms the sentinel (read count > 0). -----

    [Fact]
    public void Explain_PlansOnly_NeverOpensOrReadsBatches()
    {
        var fixture = new InMemoryRelationFixture();
        StructType schema = PeopleSchema;
        // A real batch registers so the analyzer resolves the relation; the physical run below, however,
        // reads through an ExecutionSentinelScanSource whose batch list explodes/counts on any access.
        DataFrame people = fixture.Relation("people_sentinel", schema, TestData.Batch(
            schema, TestData.Ints(1), TestData.Strings("eng"), TestData.Doubles(100.0)));
        DataFrame query = people.Filter(Col("salary").Gt(150.0)).Select(Col("id"));

        (string text, int batchAccessCount) = fixture.ExplainPhysicalWatched(query);

        // Planner-only: the tree rendered, no operator opened, no batch read.
        Assert.Contains("Project", text);
        Assert.Contains("Filter", text);
        Assert.Contains("Scan", text);
        Assert.DoesNotContain("<cannot plan physically", text);
        Assert.Equal(0, batchAccessCount);

        // Prove the sentinel is armed: an actual execution (Collect) DOES read the sentinel batches.
        Assert.True(fixture.CountBatchAccessesDuringCollect(query) > 0,
            "Collect must read the sentinel batches, proving the sentinel discriminates execution from planning.");
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

    // ----- Full Core -> Executor seam through the PUBLIC DataFrame.ToExplainString: the four Extended
    // sections all flow from a single real analysis pass, and the physical section is the real planner's
    // tree (not a stub), rendered without executing. -----

    [Fact]
    public void ToExplainString_Extended_ThroughRealSeam_RendersAllFourSections()
    {
        var fixture = new InMemoryRelationFixture(useDefaultScanSource: true);
        using SparkSession session = SparkSession.Builder().AppName("explain-extended-seam").GetOrCreate();
        DataFrame people = fixture.RelationBoundTo(session, "people_extended_seam", PeopleSchema, TestData.Batch(
            PeopleSchema, TestData.Ints(1), TestData.Strings("eng"), TestData.Doubles(100.0)));

        string text = people.Filter(Col("salary").Gt(150.0)).Select(Col("id"), Col("salary"))
            .ToExplainString(ExplainMode.Extended);

        int parsed = text.IndexOf("== Parsed Logical Plan ==", System.StringComparison.Ordinal);
        int analyzed = text.IndexOf("== Analyzed Logical Plan ==", System.StringComparison.Ordinal);
        int optimized = text.IndexOf("== Optimized Logical Plan ==", System.StringComparison.Ordinal);
        int physical = text.IndexOf("== Physical Plan ==", System.StringComparison.Ordinal);
        Assert.True(parsed >= 0 && analyzed > parsed && optimized > analyzed && physical > optimized,
            $"sections out of order:\n{text}");

        // The physical section is the REAL planner tree: named ordinals, scan leaf, project over filter.
        string physicalSection = text[physical..];
        Assert.Contains("Project [id#0, salary#2]", physicalSection);
        Assert.Contains("Filter ((salary#2 > 150", physicalSection);
        Assert.Contains("+- Scan [id, dept, salary]", physicalSection);
        // Rendered without executing: the filter excludes the only row, but a separate collect proves it.
        Assert.Empty(fixture.Collect(people.Filter(Col("salary").Gt(150.0)).Select(Col("id"), Col("salary"))));
    }
}
