using DeltaSharp.Types;
using Xunit;
using static DeltaSharp.Functions;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// Tests that unsupported plans yield a deterministic <see cref="UnsupportedPlanException"/>
/// naming the offending node, never a silent wrong plan (STORY-04.6.2 AC — unsupported-plan diagnostic).
/// </summary>
public class UnsupportedPlanTests
{
    private static (InMemoryRelationFixture Fixture, DataFrame Emp, DataFrame Dept) NewJoinInputs()
    {
        var fixture = new InMemoryRelationFixture();
        StructType empSchema = TestData.Schema(
            TestData.Field("empId", IntegerType.Instance, nullable: false),
            TestData.Field("deptId", IntegerType.Instance, nullable: false));
        StructType deptSchema = TestData.Schema(
            TestData.Field("dId", IntegerType.Instance, nullable: false),
            TestData.Field("dname", StringType.Instance));

        DataFrame emp = fixture.Relation("uemp", empSchema, TestData.Batch(
            empSchema, TestData.Ints(1, 2), TestData.Ints(10, 20)));
        DataFrame dept = fixture.Relation("udept", deptSchema, TestData.Batch(
            deptSchema, TestData.Ints(10, 20), TestData.Strings("eng", "sales")));
        return (fixture, emp, dept);
    }

    [Fact]
    public void CrossJoin_ThrowsDeterministicUnsupportedDiagnostic()
    {
        (InMemoryRelationFixture fixture, DataFrame emp, DataFrame dept) = NewJoinInputs();

        var ex = Assert.Throws<UnsupportedPlanException>(() => fixture.Plan(emp.CrossJoin(dept)));
        Assert.Contains("Join", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JoinWithoutCondition_ThrowsUnsupportedDiagnostic()
    {
        (InMemoryRelationFixture fixture, DataFrame emp, DataFrame dept) = NewJoinInputs();

        var ex = Assert.Throws<UnsupportedPlanException>(() => fixture.Plan(emp.Join(dept)));
        Assert.Contains("Join", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThetaJoinPredicate_ThrowsUnsupportedDiagnostic()
    {
        (InMemoryRelationFixture fixture, DataFrame emp, DataFrame dept) = NewJoinInputs();

        var ex = Assert.Throws<UnsupportedPlanException>(
            () => fixture.Plan(emp.Join(dept, Col("deptId").Gt(Col("dId")))));
        Assert.Contains("non-equi", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedDiagnostic_IsDeterministic_SameMessageEachTime()
    {
        (InMemoryRelationFixture fixture, DataFrame emp, DataFrame dept) = NewJoinInputs();

        string first = Assert.Throws<UnsupportedPlanException>(() => fixture.Plan(emp.CrossJoin(dept))).Message;
        string second = Assert.Throws<UnsupportedPlanException>(() => fixture.Plan(emp.CrossJoin(dept))).Message;
        Assert.Equal(first, second);
    }
}
