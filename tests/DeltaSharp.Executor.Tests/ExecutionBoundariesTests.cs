using System.Threading.Tasks;
using DeltaSharp.Engine.Columnar;
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

    // ---- Criterion 1 (#1): Count / empty-Collect over BARE roots honor cancel + timeout ----
    // These plan shapes (a bare scan, a Limit root, a Union root) never reach PhysicalRuntime.Run's
    // per-batch poll on the Count path (Count never materializes rows), so only the driver's UPFRONT
    // token gate makes them deterministic. Removing that gate silently returns a result instead.

    [Fact]
    public void Count_PreCancelledToken_OverBareScan_ThrowsOperationCanceled()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => fixture.CountWithMetrics(people, cancellationToken: cts.Token));
    }

    [Fact]
    public void Count_ZeroTimeout_OverBareScan_ThrowsTimeoutException()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        Assert.Throws<TimeoutException>(
            () => fixture.CountWithMetrics(people, timeout: TimeSpan.Zero));
    }

    [Fact]
    public void Count_PreCancelledToken_OverLimitRoot_ThrowsOperationCanceled()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => fixture.CountWithMetrics(people.Limit(3), cancellationToken: cts.Token));
    }

    [Fact]
    public void Count_ZeroTimeout_OverUnionRoot_ThrowsTimeoutException()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        Assert.Throws<TimeoutException>(
            () => fixture.CountWithMetrics(people.Union(people), timeout: TimeSpan.Zero));
    }

    [Fact]
    public void Collect_EmptyRelation_PreCancelledToken_ThrowsOperationCanceled()
    {
        var fixture = new InMemoryRelationFixture();
        DataFrame empty = fixture.Relation("empty", PeopleSchema, TestData.Batch(
            PeopleSchema, TestData.Ints(), TestData.Strings(), TestData.Doubles()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // An empty result never enters the Materialize row loop, so — like Count — only the upfront gate
        // makes a pre-cancelled empty Collect deterministic.
        Assert.Throws<OperationCanceledException>(
            () => fixture.CollectWithMetrics(empty, cancellationToken: cts.Token));
    }

    // ---- Criterion 1 (AC1 disposal): an in-flight cancel stops PROMPTLY and DISPOSES the runtime ----

    [Fact]
    public void Cancellation_InFlight_StopsPromptly_AndDisposesRuntime()
    {
        var fixture = new InMemoryRelationFixture();
        DataFrame probe = fixture.RelationSchemaOnly("probe", PeopleSchema).Filter(Col("salary").Gt(0.0));

        (Exception? error, bool runtimeDisposed, int batchAccessCount) =
            fixture.RunInFlightCancelDisposalProbe(probe);

        // (a) Stopped promptly: the backend read the source exactly once before the in-flight cancellation
        // tripped — no further batches were pulled and no rows were materialized.
        Assert.Equal(1, batchAccessCount);
        Assert.IsAssignableFrom<OperationCanceledException>(error);

        // (b) Released deterministically: the executor disposed the run's PhysicalRuntime on the cancel
        // path. This assertion FAILS if LocalQueryExecutor's disposal `finally` is removed.
        Assert.True(runtimeDisposed);
    }

    // ---- Criterion 2 additions: the new general Materialize catch (#2) and deferred-encode Scan (#6) ----

    [Fact]
    public void MaterializeFailure_GeneralFault_IsWrappedAsMaterializeStage_PreservingRootCause()
    {
        var fixture = new InMemoryRelationFixture();
        DataFrame bare = fixture.RelationSchemaOnly("sentinel_bare", PeopleSchema);

        // The sentinel batches throw a generic exception when the materializer reads them — a fault that is
        // NOT cancellation, an unsupported plan, or a result-limit breach — so it must fall through to the
        // driver's GENERAL Materialize catch (#2) rather than escape unwrapped.
        Exception ex = fixture.CollectExpectingMaterializeFault(bare);
        var qee = Assert.IsType<QueryExecutionException>(ex);
        Assert.Equal(QueryExecutionStage.Materialize, qee.Stage);
        Assert.IsType<ExecutionSentinelScanSource.BatchExecutedException>(qee.InnerException);
        Assert.NotNull(qee.Metrics);
    }

    [Fact]
    public void ScanFailure_DeferredEncodeMismatch_IsAttributedToScanStage()
    {
        var fixture = new InMemoryRelationFixture();
        StructType schema = TestData.Schema(TestData.Field("v", LongType.Instance, nullable: false));
        // A LocalRelation row supplies an int where the schema declares long; #158's deferred row→batch
        // encode runs inside ScanPlan.Execute, so the mismatch is a SCAN/data-in failure (#6), not a Plan one.
        DataFrame df = fixture.LocalRelationFrame(schema, new[] { new Row(schema, 1) });

        var ex = Assert.Throws<UnsupportedPlanException>(() => fixture.CollectWithMetrics(df));
        Assert.Equal(QueryExecutionStage.Scan, ex.Stage);
    }

    // ---- Criterion 3 (AC3): the result bound fails BEFORE materializing the overflow batch ----
    // A poison batch throws if its VALUES are read. Placed after the in-bound rows, it proves the row cap
    // is checked BEFORE materialization: the surfaced exception is the deterministic bound breach, never
    // the poison read. A mutation that materialized first, then checked, would surface the poison instead.

    [Fact]
    public void RowLimit_OneOverAcrossBatches_TripsBeforeMaterializingOverflowBatch()
    {
        var fixture = new InMemoryRelationFixture();
        StructType schema = TestData.Schema(TestData.Field("n", IntegerType.Instance, nullable: false));
        ColumnBatch inBound = TestData.Batch(schema, TestData.Ints(1, 2));
        var poison = new PoisonReadColumnBatch(schema, rowCount: 2);
        DataFrame df = fixture.Relation("bounded", schema, inBound, poison);

        // Cap = 2 (exactly the first batch). The second (poison) batch pushes the total to 4 > 2, so the
        // materializer trips at the batch boundary WITHOUT reading the poison values.
        var ex = Assert.Throws<QueryExecutionException>(() => fixture.CollectWithMetrics(df, maxResultRows: 2));
        Assert.Equal(QueryExecutionStage.Materialize, ex.Stage);
        Assert.IsType<ResultLimitExceededException>(ex.InnerException);
    }

    [Fact]
    public void RowLimit_ExactlyAtLimit_MaterializesAllRowsWithoutTripping()
    {
        var fixture = new InMemoryRelationFixture();
        StructType schema = TestData.Schema(TestData.Field("n", IntegerType.Instance, nullable: false));
        DataFrame df = fixture.Relation("exact", schema, TestData.Batch(schema, TestData.Ints(1, 2, 3)));

        // A result whose size exactly equals the cap must succeed and materialize every row.
        (IReadOnlyList<Row> rows, _) = fixture.CollectWithMetrics(df, maxResultRows: 3);
        Assert.Equal(3, rows.Count);
    }

    // ---- Criterion 4 (AC4): BytesScanned counted once (#3); failure + concurrency metrics (#4/#5) ----

    [Fact]
    public void Metrics_BytesScanned_IsAttributedOnceRegardlessOfPlanDepth()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        // A single materialization boundary (filter over scan) reports the source-scan bytes once.
        (_, ExecutionMetrics oneDeep) = fixture.CollectWithMetrics(people.Filter(Col("salary").Gt(0.0)));
        // A two-deep plan (project over filter over scan) re-wraps the intermediate result in another scan
        // at each boundary; BytesScanned must still equal the SINGLE source read, not accumulate ∝ depth.
        (_, ExecutionMetrics twoDeep) = fixture.CollectWithMetrics(
            people.Filter(Col("salary").Gt(0.0)).Select(Col("id")));

        Assert.True(oneDeep.BytesScanned > 0);
        Assert.Equal(oneDeep.BytesScanned, twoDeep.BytesScanned);
    }

    [Fact]
    public void Metrics_OnRowLimitFailure_ArePopulatedWithMeaningfulValues()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        // A filtered plan (so a real source scan runs and BytesScanned is attributed) whose 5 surviving
        // rows exceed a 2-row cap.
        (ExecutionMetrics metrics, Exception? error) =
            fixture.CollectCapturingMetrics(people.Filter(Col("salary").Gt(0.0)), maxResultRows: 2);

        // The failure path surfaces partial-but-meaningful metrics via the out-metrics sink: planning ran
        // to completion, the backend produced batches, and the source was scanned before the cap tripped.
        Assert.IsType<QueryExecutionException>(error);
        Assert.True(metrics.PlanningDuration > TimeSpan.Zero);
        Assert.True(metrics.OutputBatches >= 1);
        Assert.True(metrics.BytesScanned > 0);
    }

    [Fact]
    public void Metrics_OnBackendFailure_ArePopulated()
    {
        (InMemoryRelationFixture fixture, DataFrame people) = NewPeople();

        (ExecutionMetrics metrics, Exception? error) =
            fixture.CollectCapturingMetrics(people.Filter(Col("salary").Gt(0.0)), memoryBudgetBytes: sizeof(int));

        var qee = Assert.IsType<QueryExecutionException>(error);
        Assert.Equal(QueryExecutionStage.Backend, qee.Stage);
        // Planning completed before the backend failed, so a real planning duration is reported (#11).
        Assert.True(metrics.PlanningDuration > TimeSpan.Zero);
        Assert.NotNull(qee.Metrics);
    }

    [Fact]
    public void Metrics_TwoConcurrentActionsOverDefaultOptions_DoNotCorruptEachOther()
    {
        // Both actions run over the process-wide shared ExecutionOptions.Default; the per-call metrics sink
        // (not the shared options) carries each run's counters, so concurrent actions must not race or
        // clobber one another (#4/#5). Two independent fixtures share only ExecutionOptions.Default.
        var fixtureA = new InMemoryRelationFixture();
        DataFrame a = fixtureA.Relation("concA", PeopleSchema, TestData.Batch(
            PeopleSchema, TestData.Ints(1, 2, 3), TestData.Strings("x", "y", "z"), TestData.Doubles(1, 2, 3)));
        var fixtureB = new InMemoryRelationFixture();
        DataFrame b = fixtureB.Relation("concB", PeopleSchema, TestData.Batch(
            PeopleSchema, TestData.Ints(1, 2, 3, 4, 5, 6, 7),
            TestData.Strings("a", "b", "c", "d", "e", "f", "g"),
            TestData.Doubles(1, 2, 3, 4, 5, 6, 7)));

        var results = new (long Rows, ExecutionMetrics Metrics)[2];
        Parallel.Invoke(
            () =>
            {
                for (int i = 0; i < 50; i++)
                {
                    (IReadOnlyList<Row> rows, ExecutionMetrics m) = fixtureA.CollectViaDefaultOptions(a);
                    results[0] = (rows.Count, m);
                }
            },
            () =>
            {
                for (int i = 0; i < 50; i++)
                {
                    (IReadOnlyList<Row> rows, ExecutionMetrics m) = fixtureB.CollectViaDefaultOptions(b);
                    results[1] = (rows.Count, m);
                }
            });

        Assert.Equal(3, results[0].Rows);
        Assert.Equal(3, results[0].Metrics.OutputRows);
        Assert.Equal(7, results[1].Rows);
        Assert.Equal(7, results[1].Metrics.OutputRows);
    }

    // A batch whose metadata (schema, row count, selection) is well-formed but whose VALUE accessors throw,
    // so reading any cell trips. Used to prove the result-bound check fires BEFORE value materialization
    // (AC3): the bound breach is surfaced, never this poison read.
    private sealed class PoisonReadColumnBatch(StructType schema, int rowCount) : ColumnBatch
    {
        private readonly PoisonReadColumnVector _column = new(schema[0].DataType, rowCount);

        public override StructType Schema => schema;

        public override int RowCount => rowCount;

        public override int ColumnCount => schema.Count;

        public override SelectionVector? Selection => null;

        public override ColumnVector Column(int ordinal) => _column;

        public override ColumnBatch Slice(int offset, int length) =>
            throw new InvalidOperationException("Poison batch must not be sliced.");

        public override ColumnBatch WithSelection(SelectionVector selection) =>
            throw new InvalidOperationException("Poison batch must not be re-selected.");
    }

    private sealed class PoisonReadColumnVector(DataType type, int length) : ColumnVector(type)
    {
        private static InvalidOperationException Poison() =>
            new("Poison vector value was read: the result bound was NOT enforced before materialization.");

        public override int Length => length;

        public override int Offset => 0;

        public override bool HasNulls => false;

        public override int NullCount => 0;

        public override bool IsNull(int index) => throw Poison();

        public override ReadOnlySpan<T> GetValues<T>() => throw Poison();

        public override ReadOnlySpan<byte> GetBytes(int index) => throw Poison();

        public override ColumnVector Slice(int offset, int length) => throw Poison();
    }
}
