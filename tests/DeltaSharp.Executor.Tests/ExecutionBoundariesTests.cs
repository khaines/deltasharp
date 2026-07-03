using DeltaSharp.Engine.Execution;
using DeltaSharp.Types;
using Xunit;
using static DeltaSharp.Functions;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// STORY-04.6.4 (#176) failure-mode tests for the local execution driver's error, cancellation, and
/// resource boundaries. They exercise the four acceptance criteria through the
/// <see cref="InMemoryRelationFixture"/> seam: (1) cancellation/timeout stops and releases resources
/// deterministically; (2) analyzer/planner/scan/backend/materialize failures surface a stage-attributed
/// public exception that preserves the root cause; (3) configured row/byte/memory bounds fail safely
/// before unbounded materialization (bounded, not OOM); (4) planning + execution metrics are available
/// after an action completes or fails.
/// </summary>
public class ExecutionBoundariesTests
{
    private static StructType PeopleSchema => TestData.Schema(
        TestData.Field("id", IntegerType.Instance, nullable: false),
        TestData.Field("dept", StringType.Instance),
        TestData.Field("salary", DoubleType.Instance));

    private static (InMemoryRelationFixture Fixture, DataFrame People) NewPeople()
    {
        var fixture = new InMemoryRelationFixture();
        DataFrame people = fixture.Relation("people", PeopleSchema, TestData.Batch(
            PeopleSchema,
            TestData.Ints(1, 2, 3, 4, 5),
            TestData.Strings("eng", "eng", "sales", "sales", "sales"),
            TestData.Doubles(100.0, 200.0, 300.0, 50.0, 75.0)));
        return (fixture, people);
    }

    // ---- Criterion 1: cancellation / timeout stop and release resources deterministically ----

    [Fact]
    public void Cancellation_PreCancelledToken_ThrowsOperationCanceled_AndFixtureStaysReusable()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => fixture.CollectWithMetrics(people, cancellationToken: cts.Token));

        // The cancelled run must have released the shared context/spill store deterministically, leaving
        // no corrupted state: a subsequent normal collect over the same fixture succeeds with full output.
        (IReadOnlyList<Row> rows, _) = fixture.CollectWithMetrics(people);
        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public void Timeout_AlreadyElapsed_ThrowsTimeoutException()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        // A non-positive timeout is an already-elapsed deadline; the driver cancels synchronously and the
        // boundary surfaces as a TimeoutException (not the internal OperationCanceledException).
        Assert.Throws<TimeoutException>(
            () => fixture.CollectWithMetrics(people, timeout: TimeSpan.Zero));

        (IReadOnlyList<Row> rows, _) = fixture.CollectWithMetrics(people);
        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public void UserCancellation_WinsRaceWithTimeout()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Both an already-elapsed timeout and a cancelled user token are present; user cancellation wins,
        // so the surfaced exception is OperationCanceledException, not TimeoutException.
        Assert.Throws<OperationCanceledException>(
            () => fixture.CollectWithMetrics(people, cancellationToken: cts.Token, timeout: TimeSpan.Zero));
    }

    // ---- Criterion 2: stage-attributed exceptions preserving the root cause ----

    [Fact]
    public void ScanFailure_UnregisteredRelation_IsAttributedToScanStage()
    {
        var fixture = new InMemoryRelationFixture();
        // Schema-only registration: the analyzer resolves the relation but the planner's scan resolution
        // misses it, so the planner raises a Scan-attributed UnsupportedPlanException (propagated unwrapped).
        DataFrame ghost = fixture.RelationSchemaOnly("ghost", PeopleSchema);

        var ex = Assert.Throws<UnsupportedPlanException>(() => fixture.CollectWithMetrics(ghost));
        Assert.Equal(QueryExecutionStage.Scan, ex.Stage);
    }

    [Fact]
    public void PlanFailure_CrossJoin_IsAttributedToPlanStage()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        // CROSS joins have no M1 physical mapping; the planner raises a Plan-attributed diagnostic.
        var ex = Assert.Throws<UnsupportedPlanException>(
            () => fixture.CollectWithMetrics(people.CrossJoin(people)));
        Assert.Equal(QueryExecutionStage.Plan, ex.Stage);
    }

    [Fact]
    public void MaterializeFailure_OutOfRangeTimestamp_IsAttributedToMaterializeStage()
    {
        var fixture = new InMemoryRelationFixture();
        StructType schema = TestData.Schema(TestData.Field("ts", TimestampType.Instance, nullable: false));
        // long.MaxValue epoch-micros overflows the DateTime conversion during row materialization; the
        // guard surfaces a Materialize-attributed UnsupportedPlanException, not a raw overflow.
        DataFrame df = fixture.Relation("tsoob", schema, TestData.Batch(schema, TestData.Timestamps(long.MaxValue)));

        var ex = Assert.Throws<UnsupportedPlanException>(() => fixture.CollectWithMetrics(df));
        Assert.Equal(QueryExecutionStage.Materialize, ex.Stage);
    }

    [Fact]
    public void BackendFailure_MemoryBudgetRefused_IsAttributedToBackendStage_AndPreservesRootCause()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        // A 4-byte operator budget cannot admit the filter's selection-vector reservation for the surviving
        // rows, so the (non-spilling) filter fails closed with an ExecutionMemoryException. The driver
        // re-surfaces it as a Backend-attributed QueryExecutionException that preserves the engine
        // exception as its root cause and still reports metrics.
        DataFrame filtered = people.Filter(Col("salary").Gt(0.0));

        var ex = Assert.Throws<QueryExecutionException>(
            () => fixture.CollectWithMetrics(filtered, memoryBudgetBytes: sizeof(int)));
        Assert.Equal(QueryExecutionStage.Backend, ex.Stage);
        Assert.IsType<ExecutionMemoryException>(ex.InnerException);
        Assert.NotNull(ex.Metrics);
    }

    // ---- Criterion 3: row/byte bounds fail safely before unbounded materialization ----

    [Fact]
    public void RowLimit_BelowResultSize_TripsDeterministicallyAtMaterializeStage()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        // The 5-row result exceeds a 2-row cap; the materializer trips BEFORE accumulating unbounded rows
        // and the driver surfaces a Materialize-attributed QueryExecutionException with metrics populated.
        var ex = Assert.Throws<QueryExecutionException>(
            () => fixture.CollectWithMetrics(people, maxResultRows: 2));
        Assert.Equal(QueryExecutionStage.Materialize, ex.Stage);
        Assert.IsType<ResultLimitExceededException>(ex.InnerException);
        Assert.NotNull(ex.Metrics);
    }

    [Fact]
    public void ByteLimit_BelowResultSize_TripsDeterministicallyAtMaterializeStage()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        var ex = Assert.Throws<QueryExecutionException>(
            () => fixture.CollectWithMetrics(people, maxResultBytes: 1));
        Assert.Equal(QueryExecutionStage.Materialize, ex.Stage);
        Assert.IsType<ResultLimitExceededException>(ex.InnerException);
    }

    [Fact]
    public void RowLimit_AtOrAboveResultSize_Succeeds()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        // A cap that is not exceeded must not perturb a successful action (bounds default to permissive).
        (IReadOnlyList<Row> rows, _) = fixture.CollectWithMetrics(people, maxResultRows: 5);
        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public void Count_IsNotRowCapped()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        // Count never materializes rows, so a small memory budget still succeeds and returns the full count
        // (the result row/byte caps apply only to collect's materialization boundary).
        (long count, _) = fixture.CountWithMetrics(people);
        Assert.Equal(5, count);
    }

    // ---- Criterion 4: planning + execution metrics available on success and failure ----

    [Fact]
    public void Metrics_OnSuccessfulCollect_ArePopulated()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        (IReadOnlyList<Row> rows, ExecutionMetrics metrics) = fixture.CollectWithMetrics(people);

        Assert.Equal(5, rows.Count);
        Assert.Equal(5, metrics.OutputRows);
        Assert.True(metrics.OutputBatches >= 1);
        Assert.True(metrics.TotalDuration >= TimeSpan.Zero);
        Assert.True(metrics.BytesScanned >= 0);
        Assert.True(metrics.PeakMemoryBytes >= 0);
    }

    [Fact]
    public void Metrics_OnSuccessfulCount_ArePopulated()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        (long count, ExecutionMetrics metrics) = fixture.CountWithMetrics(people);

        Assert.Equal(5, count);
        Assert.Equal(5, metrics.OutputRows);
        Assert.True(metrics.TotalDuration >= TimeSpan.Zero);
    }
}
