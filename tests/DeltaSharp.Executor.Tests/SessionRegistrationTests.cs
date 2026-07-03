using DeltaSharp.Types;
using Xunit;
using static DeltaSharp.Functions;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// Tests the Core↔Executor registration seam: the module initializer registers
/// <see cref="LocalQueryExecutor"/> into <see cref="SparkSession"/>, so a session created by any app
/// that references DeltaSharp.Executor executes queries for real (STORY-04.6.2 — IQueryExecutor wiring).
/// </summary>
public class SessionRegistrationTests
{
    private static StructType PeopleSchema => TestData.Schema(
        TestData.Field("id", IntegerType.Instance, nullable: false),
        TestData.Field("dept", StringType.Instance),
        TestData.Field("salary", DoubleType.Instance));

    [Fact]
    public void SparkSession_ResolvesLocalQueryExecutor()
    {
        using SparkSession session = SparkSession.Builder().AppName("reg-type").GetOrCreate();

        Assert.Equal(nameof(LocalQueryExecutor), SessionSeamAccess.ExecutorTypeName(session));
    }

    [Fact]
    public void SparkSession_ExecutesEndToEndThroughRegisteredExecutor()
    {
        // Register into the process-wide default source the module-initializer factory reads.
        var fixture = new InMemoryRelationFixture(useDefaultScanSource: true);
        DataFrame people = fixture.Relation("people_session_seam", PeopleSchema, TestData.Batch(
            PeopleSchema,
            TestData.Ints(1, 2, 3),
            TestData.Strings("eng", "eng", "sales"),
            TestData.Doubles(100.0, 200.0, 300.0)));

        using SparkSession session = SparkSession.Builder().AppName("reg-e2e").GetOrCreate();

        IReadOnlyList<Row> rows = fixture.CollectViaSession(
            session, people.Filter(Col("salary").Gt(150.0)).Select(Col("id")));

        Assert.Equal(new[] { 2, 3 }, rows.Select(r => r.GetAs<int>(0)));
    }
}
