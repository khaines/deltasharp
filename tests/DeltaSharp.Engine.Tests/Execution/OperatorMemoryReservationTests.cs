using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Engine.Execution.Spill;
using DeltaSharp.Types;
using Xunit;
using ExecutionContext = DeltaSharp.Engine.Execution.ExecutionContext;

namespace DeltaSharp.Engine.Tests.Execution;

/// <summary>
/// Verifies STORY-03.6.1 (operator memory reservations + release discipline): every interpreted
/// operator reserves against the unified <see cref="IExecutionMemory"/> budget BEFORE allocating
/// output vectors / scratch state (AC1), releases every reservation exactly once on the normal,
/// cancellation, and exception paths (AC2 — proved by the over-release-throwing
/// <see cref="BoundedExecutionMemory"/> ledger), reports peak/current reserved bytes, the spill-bytes
/// placeholder, and the reservation (allocation) count (AC3), and keeps release ownership with the
/// producing operator so a consumed batch is never double-released nor leaked (AC4). It also pins the
/// three deferrals absorbed from issue #155: (a) hash-table / list / match-flag collection-overhead
/// accounting, (b) within-batch cancellation polled every <see cref="CancellationPolicy.RowPollInterval"/>
/// rows, and (c) output-side variable-width accounting on the join chunk and the aggregate MIN/MAX copy.
/// </summary>
/// <remarks>
/// These tests share the operator-test directory with the <c>Parity</c> lane but never collide: the
/// <c>OperatorMemory</c> prefix keeps the type names distinct. The budgets in the fail-closed tests are
/// knife-edge by construction — each sits strictly between the with-accounting and without-accounting
/// reserved totals (decoded from the operators' own reservation arithmetic), so deleting the audited
/// reservation flips the test from throwing to passing. That is the non-vacuity contract: a test that
/// cannot fail when its guard is removed proves nothing.
/// </remarks>
public class OperatorMemoryReservationTests
{
    // ----------------------------------------------------------------------------------------------
    // fixtures + helpers
    // ----------------------------------------------------------------------------------------------

    private static IExecutionBackend Backend => InterpretedVectorizedBackend.Instance;

    private static ExecutionContext Ctx(IExecutionMemory memory, CancellationToken cancellation = default)
        => new(memory, cancellation) { SpillStore = new MemorySpillStore() };

    private static MutableColumnVector IntCol(params int?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.IntegerType, Math.Max(values.Length, 1));
        foreach (int? x in values)
        {
            if (x.HasValue)
            {
                v.AppendValue(x.Value);
            }
            else
            {
                v.AppendNull();
            }
        }

        return v;
    }

    private static MutableColumnVector StrCol(params string?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, Math.Max(values.Length, 1));
        foreach (string? x in values)
        {
            if (x is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendBytes(Encoding.UTF8.GetBytes(x));
            }
        }

        return v;
    }

    private static MutableColumnVector BoolCol(params bool?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.BooleanType, Math.Max(values.Length, 1));
        foreach (bool? x in values)
        {
            if (x.HasValue)
            {
                v.AppendValue(x.Value);
            }
            else
            {
                v.AppendNull();
            }
        }

        return v;
    }

    private static ColumnBatch Batch(StructType schema, params ColumnVector[] columns)
        => new ManagedColumnBatch(schema, columns, columns[0].Length);

    private static InMemoryScanOperator Scan(StructType schema, params ColumnBatch[] batches)
        => new(schema, batches);

    private static List<ColumnBatch> Drain(IBatchStream stream)
    {
        var batches = new List<ColumnBatch>();
        while (stream.TryGetNext(out ColumnBatch? batch))
        {
            batches.Add(batch);
        }

        return batches;
    }

    // ----- schemas -----

    private static readonly StructType KeyCountIn = new(
    [
        new StructField("k", DataTypes.IntegerType, nullable: true),
    ]);

    private static readonly StructType KeyCountOut = new(
    [
        new StructField("k", DataTypes.IntegerType, nullable: true),
        new StructField("c", DataTypes.LongType, nullable: false),
    ]);

    private static readonly StructType StrValIn = new(
    [
        new StructField("s", DataTypes.StringType, nullable: true),
    ]);

    private static readonly StructType MinStrOut = new(
    [
        new StructField("m", DataTypes.StringType, nullable: true),
    ]);

    private static readonly StructType StrKeyIn = new(
    [
        new StructField("k", DataTypes.StringType, nullable: true),
    ]);

    private static readonly StructType StrKeyCountOut = new(
    [
        new StructField("k", DataTypes.StringType, nullable: true),
        new StructField("c", DataTypes.LongType, nullable: false),
    ]);

    private static readonly StructType IntKeyMinStrIn = new(
    [
        new StructField("k", DataTypes.IntegerType, nullable: true),
        new StructField("s", DataTypes.StringType, nullable: true),
    ]);

    private static readonly StructType IntKeyMinStrOut = new(
    [
        new StructField("k", DataTypes.IntegerType, nullable: true),
        new StructField("m", DataTypes.StringType, nullable: true),
    ]);

    private static readonly StructType FilterSchema = new(
    [
        new StructField("id", DataTypes.IntegerType, nullable: false),
        new StructField("flag", DataTypes.BooleanType, nullable: false),
    ]);

    private static readonly StructType SortSchema = new(
    [
        new StructField("id", DataTypes.IntegerType, nullable: false),
    ]);

    private static readonly StructType StrSortSchema = new(
    [
        new StructField("s", DataTypes.StringType, nullable: true),
    ]);

    private static readonly StructType JoinLeft = new(
    [
        new StructField("lk", DataTypes.IntegerType, nullable: true),
        new StructField("lv", DataTypes.StringType, nullable: true),
    ]);

    private static readonly StructType JoinRight = new(
    [
        new StructField("rk", DataTypes.IntegerType, nullable: true),
        new StructField("rv", DataTypes.StringType, nullable: true),
    ]);

    private static readonly StructType JoinOut = new(
    [
        new StructField("lk", DataTypes.IntegerType, nullable: true),
        new StructField("lv", DataTypes.StringType, nullable: true),
        new StructField("rk", DataTypes.IntegerType, nullable: true),
        new StructField("rv", DataTypes.StringType, nullable: true),
    ]);

    // ----- operator builders -----

    private static AggregateOperator GroupByCount(InMemoryScanOperator input)
        => new(
            input,
            KeyCountOut,
            [new ColumnReference(0, DataTypes.IntegerType, nullable: true)],
            [new AggregateExpression(AggregateFunction.Count, null)]);

    // GROUP BY a (variable-width) STRING key, COUNT(*). Exercises the key-column output copy whose true
    // var-width must be reserved in ResolveGroup (F1).
    private static AggregateOperator GroupByStrCount(InMemoryScanOperator input)
        => new(
            input,
            StrKeyCountOut,
            [new ColumnReference(0, DataTypes.StringType, nullable: true)],
            [new AggregateExpression(AggregateFunction.Count, null)]);

    // GROUP BY an INTEGER key, MIN(string). The MIN output copy reserves a var-width per group in the
    // BuildResult emit loop, giving that loop an observable per-group reservation for the F2a cancel test.
    private static AggregateOperator GroupByIntMinStr(InMemoryScanOperator input)
        => new(
            input,
            IntKeyMinStrOut,
            [new ColumnReference(0, DataTypes.IntegerType, nullable: true)],
            [new AggregateExpression(AggregateFunction.Min, new ColumnReference(1, DataTypes.StringType, nullable: true))]);

    private static AggregateOperator GlobalMin(InMemoryScanOperator input)
        => new(
            input,
            MinStrOut,
            [],
            [new AggregateExpression(AggregateFunction.Min, new ColumnReference(0, DataTypes.StringType, nullable: true))]);

    private static JoinOperator InnerJoin(ColumnBatch left, ColumnBatch right)
        => new(
            Scan(JoinLeft, left),
            Scan(JoinRight, right),
            JoinOut,
            JoinType.Inner,
            [new ColumnReference(0, DataTypes.IntegerType, nullable: true)],
            [new ColumnReference(0, DataTypes.IntegerType, nullable: true)]);

    private static FilterOperator FilterTrue(InMemoryScanOperator input)
        => new(input, new ColumnReference(1, DataTypes.BooleanType, nullable: false));

    private static SortOperator SortById(InMemoryScanOperator input)
        => new(input, [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);

    private static SortOperator SortByStr(InMemoryScanOperator input)
        => new(input, [new SortOrder(new ColumnReference(0, DataTypes.StringType, nullable: true))]);

    private static ExchangeLocalOperator HashExchange(InMemoryScanOperator input)
        => new(input, partitionCount: 4, [new ColumnReference(0, DataTypes.IntegerType, nullable: false)]);

    /// <summary>A scan over a single batch of <paramref name="rows"/> distinct integer keys.</summary>
    private static InMemoryScanOperator DistinctKeyScan(int rows)
    {
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.IntegerType, rows);
        for (int i = 0; i < rows; i++)
        {
            keys.AppendValue(i);
        }

        return Scan(KeyCountIn, Batch(KeyCountIn, keys));
    }

    /// <summary>A right-side build batch of <paramref name="n"/> distinct integer keys with tiny payloads.</summary>
    private static ColumnBatch DistinctBuild(int n)
    {
        MutableColumnVector rk = ColumnVectors.Create(DataTypes.IntegerType, n);
        MutableColumnVector rv = ColumnVectors.Create(DataTypes.StringType, n);
        for (int i = 0; i < n; i++)
        {
            rk.AppendValue(i);
            rv.AppendBytes(Encoding.UTF8.GetBytes("R"));
        }

        return Batch(JoinRight, rk, rv);
    }

    /// <summary>A scan of one batch with <paramref name="groups"/> distinct wide (<paramref name="keyLen"/>-byte)
    /// string keys — exercises the aggregate grouping-KEY output copy's var-width charge (F1).</summary>
    private static InMemoryScanOperator WideStringKeyScan(int groups, int keyLen)
    {
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.StringType, groups);
        for (int i = 0; i < groups; i++)
        {
            // Distinct keys that are all keyLen bytes long: a fixed filler plus a unique short prefix,
            // truncated to keyLen so every group's reserved key var-width is identical and exact.
            string s = i.ToString("D6") + new string('k', keyLen);
            keys.AppendBytes(Encoding.UTF8.GetBytes(s)[..keyLen]);
        }

        return Scan(StrKeyIn, Batch(StrKeyIn, keys));
    }

    /// <summary>A scan of one batch with <paramref name="n"/> distinct integer keys, each carrying a
    /// non-null non-empty string value so a grouped MIN reserves exactly once per group on input (retain)
    /// and once per group on output (BuildResult emit). Used by the F2a emit-cancel test.</summary>
    private static InMemoryScanOperator DistinctIntKeyStrScan(int n)
    {
        MutableColumnVector k = ColumnVectors.Create(DataTypes.IntegerType, n);
        MutableColumnVector s = ColumnVectors.Create(DataTypes.StringType, n);
        for (int i = 0; i < n; i++)
        {
            k.AppendValue(i);
            s.AppendBytes(Encoding.UTF8.GetBytes("v" + i));
        }

        return Scan(IntKeyMinStrIn, Batch(IntKeyMinStrIn, k, s));
    }

    /// <summary>
    /// A child <see cref="IBatchStream"/> that yields one buffered batch, then — on the EOF pull the sort's
    /// buffer loop makes after draining — cancels the supplied source and returns <see langword="false"/>
    /// <i>without</i> a batch-boundary token check. This lands the cancellation deterministically in the
    /// window between buffering and <c>Array.Sort</c>, the only place the sort's in-comparer poll (F2b) can
    /// be the observer (the real scan checks the token at its boundary, which would otherwise catch the
    /// cancel before the sort). No thread race: cancellation is driven by the pull sequence itself.
    /// </summary>
    private sealed class CancelOnEofStream : IBatchStream
    {
        private readonly ColumnBatch _batch;
        private readonly CancellationTokenSource _cts;
        private int _calls;

        internal CancelOnEofStream(StructType schema, ColumnBatch batch, CancellationTokenSource cts)
        {
            Schema = schema;
            _batch = batch;
            _cts = cts;
        }

        public StructType Schema { get; }

        public bool TryGetNext([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ColumnBatch? batch)
        {
            if (_calls++ == 0)
            {
                batch = _batch;
                return true;
            }

            _cts.Cancel();
            batch = null;
            return false;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// An <see cref="IExecutionMemory"/> that delegates to an inner budget but counts successful
    /// reservation events and, once <see cref="_cancelAfter"/> reservations have succeeded, cancels the
    /// supplied source. It fires cancellation deterministically <i>inside</i> a single large input
    /// batch's per-row loop (every distinct-key row reserves exactly once), so the within-batch poll
    /// granularity (deferral (b)) is observed without a thread race.
    /// </summary>
    private sealed class CancelOnReserveMemory : IExecutionMemory
    {
        private readonly IExecutionMemory _inner;
        private readonly CancellationTokenSource _cts;
        private readonly int _cancelAfter;

        internal CancelOnReserveMemory(IExecutionMemory inner, CancellationTokenSource cts, int cancelAfter)
        {
            _inner = inner;
            _cts = cts;
            _cancelAfter = cancelAfter;
        }

        internal int Reservations { get; private set; }

        public long BudgetBytes => _inner.BudgetBytes;

        public long ReservedBytes => _inner.ReservedBytes;

        public long AvailableBytes => _inner.AvailableBytes;

        public long MaxSpillBytes => _inner.MaxSpillBytes;

        public long SpilledBytes => _inner.SpilledBytes;

        public bool TryReserve(long bytes)
        {
            if (!_inner.TryReserve(bytes))
            {
                return false;
            }

            Reservations++;
            if (Reservations == _cancelAfter)
            {
                _cts.Cancel();
            }

            return true;
        }

        public void Release(long bytes) => _inner.Release(bytes);

        public void RecordSpill(long bytes) => _inner.RecordSpill(bytes);
    }

    // ==============================================================================================
    // CancellationPolicy unit contract (deferral (b) granularity)
    // ==============================================================================================

    [Fact]
    public void CancellationPolicy_Poll_ThrowsOnlyAtIntervalBoundaries()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Row 0 and every multiple of the interval are poll points; rows between them are not, so a
        // single large batch is uncancellable for at most RowPollInterval rows. Mutating the interval
        // to int.MaxValue (poll never) makes the boundary assertions below stop throwing.
        Assert.Throws<OperationCanceledException>(() => CancellationPolicy.Poll(cts.Token, 0));
        Assert.Throws<OperationCanceledException>(() => CancellationPolicy.Poll(cts.Token, CancellationPolicy.RowPollInterval));
        Assert.Throws<OperationCanceledException>(() => CancellationPolicy.Poll(cts.Token, 2 * CancellationPolicy.RowPollInterval));

        // Between boundaries the cancelled token is intentionally not observed (the per-row cost stays a
        // single mask-and-branch), bounding — not eliminating — the uncancellable window.
        CancellationPolicy.Poll(cts.Token, 1);
        CancellationPolicy.Poll(cts.Token, CancellationPolicy.RowPollInterval - 1);
        CancellationPolicy.Poll(cts.Token, CancellationPolicy.RowPollInterval + 1);
    }

    // ==============================================================================================
    // AC1 — reserve-before-allocate / fail-closed with rollback
    // ==============================================================================================

    [Fact]
    public void Filter_OverBudget_FailsClosed_AndLeavesNoReservation()
    {
        // Budget fits a single int, but three rows survive -> the selection-vector reservation (3 ints)
        // is refused. Because the operator reserves BEFORE materializing the SelectionVector, the refusal
        // throws with nothing allocated and the budget left untouched (TryReserve rolls its own add back).
        var mem = new BoundedExecutionMemory(sizeof(int));
        InMemoryScanOperator scan = Scan(FilterSchema, Batch(FilterSchema, IntCol(1, 2, 3), BoolCol(true, true, true)));
        using IBatchStream stream = Backend.Open(FilterTrue(scan), Ctx(mem));

        Assert.Throws<ExecutionMemoryException>(() => stream.TryGetNext(out _));
        Assert.Equal(0, mem.ReservedBytes);
    }

    // ==============================================================================================
    // AC2 — exactly-once release on normal / cancel / exception paths (ledger-proved)
    // ==============================================================================================

    [Fact]
    public void Aggregate_NormalCompletion_ReleasesExactlyOnce_AndDisposeIsIdempotent()
    {
        // The over-release-throwing BoundedExecutionMemory ledger is the proof surface: any byte released
        // twice throws ArgumentOutOfRangeException. A blocking aggregate holds its reservation until
        // Dispose, so the budget is non-zero while draining and exactly zero afterwards.
        var mem = new BoundedExecutionMemory(long.MaxValue);
        AggregateOperator agg = GroupByCount(DistinctKeyScan(32));
        IBatchStream stream = Backend.Open(agg, Ctx(mem));

        Drain(stream);
        Assert.True(mem.ReservedBytes > 0); // result rows live until the consumer finishes -> still held

        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes); // released exactly once

        // Idempotent dispose: a second release of the same bytes would trip the ledger; it must not.
        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes);
    }

    [Fact]
    public void Aggregate_CancelledMidBuild_ReleasesAllReservations()
    {
        // Cancel after a handful of groups are reserved, mid-build. The build aborts at the next poll
        // boundary with the partial reservation still held; Dispose must release it exactly once so the
        // ledger returns to zero (a double release would throw).
        using var cts = new CancellationTokenSource();
        var mem = new CancelOnReserveMemory(new BoundedExecutionMemory(long.MaxValue), cts, cancelAfter: 8);
        AggregateOperator agg = GroupByCount(DistinctKeyScan(4096));
        IBatchStream stream = Backend.Open(agg, Ctx(mem, cts.Token));

        Assert.Throws<OperationCanceledException>(() => Drain(stream));
        Assert.True(mem.ReservedBytes > 0); // partial build still held after the cancel

        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes);
    }

    [Fact]
    public void Join_ReservationRefused_SpillsAndReleasesAllReservations_ExactlyOnce()
    {
        // A tight budget refuses the build reservation partway through; STORY-03.6.2 now switches the join
        // to a grace-hash spill instead of failing closed. The build/probe partition to disk and the join
        // still completes; Dispose releases every reservation exactly once (the ledger would throw on a
        // double release) so the budget returns to zero, and the spill is recorded in the metric.
        var mem = new BoundedExecutionMemory(4000);
        JoinOperator join = InnerJoin(Batch(JoinLeft, IntCol(), StrCol()), DistinctBuild(64));
        IBatchStream stream = Backend.Open(join, Ctx(mem));

        Drain(stream); // grace spill, no throw
        Assert.True(join.Metrics.Snapshot().SpilledBytes > 0); // the build side spilled

        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes);
    }

    [Fact]
    public void Sort_NormalCompletion_ReleasesExactlyOnce()
    {
        var mem = new BoundedExecutionMemory(long.MaxValue);
        InMemoryScanOperator scan = Scan(SortSchema, Batch(SortSchema, IntCol(3, 1, 2, 5, 4)));
        IBatchStream stream = Backend.Open(SortById(scan), Ctx(mem));

        Drain(stream);
        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes);
        stream.Dispose(); // idempotent; a double release would trip the ledger
        Assert.Equal(0, mem.ReservedBytes);
    }

    [Fact]
    public void ExchangeLocal_NormalCompletion_ReleasesExactlyOnce()
    {
        var mem = new BoundedExecutionMemory(long.MaxValue);
        InMemoryScanOperator scan = Scan(SortSchema, Batch(SortSchema, IntCol(1, 2, 3, 4, 5, 6, 7, 8)));
        IBatchStream stream = Backend.Open(HashExchange(scan), Ctx(mem));

        Drain(stream);
        Assert.Equal(0, mem.ReservedBytes); // streaming operator holds at most one in-flight partition set
        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes);
    }

    // ==============================================================================================
    // AC3 — metrics: peak / current reserved, spill-bytes placeholder, allocation count
    // ==============================================================================================

    [Fact]
    public void Aggregate_Metrics_ReportPeakCurrentSpillAndAllocationCount()
    {
        const int groups = 64;
        var mem = new BoundedExecutionMemory(long.MaxValue);
        AggregateOperator agg = GroupByCount(DistinctKeyScan(groups));
        IBatchStream stream = Backend.Open(agg, Ctx(mem));

        Drain(stream);

        OperatorMetricsSnapshot held = agg.Metrics.Snapshot();
        Assert.Equal(groups, held.AllocationCount);          // one reservation event per discovered group
        Assert.True(held.PeakMemoryBytes > 0);
        Assert.Equal(held.PeakMemoryBytes, held.CurrentReservedBytes); // blocking op holds its whole peak
        Assert.Equal(0, held.SpilledBytes);                  // ample budget: nothing spills (spill is STORY-03.6.2)

        stream.Dispose();
        OperatorMetricsSnapshot drained = agg.Metrics.Snapshot();
        Assert.Equal(0, drained.CurrentReservedBytes);       // every reservation released
        Assert.Equal(held.PeakMemoryBytes, drained.PeakMemoryBytes); // peak is a high-water mark, never decays
        Assert.Equal(groups, drained.AllocationCount);
    }

    // ==============================================================================================
    // AC4 — ownership transfer: producer releases, consumer never double-releases nor leaks
    // ==============================================================================================

    [Fact]
    public void Filter_OwnershipTransfer_ReleasesPreviousBatch_NoDoubleReleaseNoLeak()
    {
        // A streaming filter accounts for at most one in-flight batch: each pull releases the prior
        // emission's reservation and reserves the current one. The downstream consumer (this test) holds
        // the emitted batches but never releases their bytes — release ownership stays with the producer.
        // The ledger proves there is no double release; the final zero proves there is no leak.
        var mem = new BoundedExecutionMemory(long.MaxValue);
        InMemoryScanOperator scan = Scan(
            FilterSchema,
            Batch(FilterSchema, IntCol(1, 2, 3), BoolCol(true, true, true)),
            Batch(FilterSchema, IntCol(4), BoolCol(true)));
        IBatchStream stream = Backend.Open(FilterTrue(scan), Ctx(mem));

        Assert.True(stream.TryGetNext(out ColumnBatch? first));
        long afterFirst = mem.ReservedBytes;
        Assert.Equal(3 * sizeof(int), afterFirst); // three surviving rows reserved

        Assert.True(stream.TryGetNext(out ColumnBatch? second));
        Assert.Equal(sizeof(int), mem.ReservedBytes); // one surviving row; the prior 3-int reservation was released, not doubled

        Assert.False(stream.TryGetNext(out _));
        Assert.Equal(0, mem.ReservedBytes); // last in-flight batch released on drain — no leak

        Assert.NotNull(first);
        Assert.NotNull(second);
        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes);
    }

    // ==============================================================================================
    // Deferral (a) — collection-overhead accounting (hash entry / list / match flag).
    // Knife-edge budgets: WITHOUT the overhead charge the build fits and never spills; WITH it the build
    // crosses the budget and triggers the STORY-03.6.2 spill. Deleting the overhead term flips these from
    // spill (SpilledBytes > 0) to no-spill (SpilledBytes == 0).
    // ==============================================================================================

    [Fact]
    public void Aggregate_HighCardinality_TightBudget_Spills_OnCollectionOverhead()
    {
        // 64 distinct groups. Per-group reserve = state(8) + output(12) + keyLen(5) + HashTableEntryBytes(64)
        // = 89 (=> 5696 total); without the 64-byte hash entry it is 25 (=> 1600 total). Budget 3200 sits
        // between: the hash-entry overhead alone tips the build over the budget, so the aggregate spills.
        var mem = new BoundedExecutionMemory(3200);
        AggregateOperator agg = GroupByCount(DistinctKeyScan(64));
        using IBatchStream stream = Backend.Open(agg, Ctx(mem));

        List<ColumnBatch> output = Drain(stream); // spills, no throw
        Assert.True(agg.Metrics.Snapshot().SpilledBytes > 0);
        Assert.Equal(64, output.Sum(b => b.LogicalRowCount)); // all 64 groups recovered
    }

    [Fact]
    public void Join_HighCardinalityBuild_TightBudget_Spills_OnCollectionOverhead()
    {
        // 64 distinct build keys. Per-key reserve = row+key(25) + payload(1) + match(1) + entry(64) +
        // list(48) = 139 (=> 8896 total); without the 113 bytes of collection overhead it is 26
        // (=> 1664 total). Budget 4000 sits between, so the overhead alone trips the grace-hash spill.
        var mem = new BoundedExecutionMemory(4000);
        JoinOperator join = InnerJoin(Batch(JoinLeft, IntCol(), StrCol()), DistinctBuild(64));
        using IBatchStream stream = Backend.Open(join, Ctx(mem));

        Drain(stream); // spills, no throw
        Assert.True(join.Metrics.Snapshot().SpilledBytes > 0);
    }

    // ==============================================================================================
    // Deferral (b) — within-batch cancellation observed within RowPollInterval rows.
    // ==============================================================================================

    [Fact]
    public void Aggregate_CancelMidLargeBatch_ObservedWithinPollInterval()
    {
        // One 4096-row batch the producer did not chunk; every distinct-key row reserves once. Cancellation
        // fires after the 8th reservation (deep inside the per-row loop, long past the row-0 poll). With the
        // poll present, the build stops at the next boundary (row RowPollInterval), so at most
        // RowPollInterval rows are processed; widening the poll to never would run all 4096 rows and never
        // throw, failing both assertions below.
        const int rows = 4096;
        using var cts = new CancellationTokenSource();
        var mem = new CancelOnReserveMemory(new BoundedExecutionMemory(long.MaxValue), cts, cancelAfter: 8);
        AggregateOperator agg = GroupByCount(DistinctKeyScan(rows));
        using IBatchStream stream = Backend.Open(agg, Ctx(mem, cts.Token));

        Assert.Throws<OperationCanceledException>(() => Drain(stream));
        Assert.True(
            mem.Reservations <= CancellationPolicy.RowPollInterval,
            $"cancellation observed after {mem.Reservations} rows; expected within {CancellationPolicy.RowPollInterval}");
        Assert.True(mem.Reservations < rows, "the build must stop mid-batch, not run to completion");
    }

    // ==============================================================================================
    // Deferral (c) — output-side variable-width accounting (join chunk + aggregate MIN/MAX copy).
    // Knife-edge budgets: the build/retention fits, but the wide value copied to the OUTPUT tips over.
    // Deleting the output VariableWidthBytes charge flips these from throw to pass.
    // ==============================================================================================

    [Fact]
    public void Join_LargeStringOutput_TightBudget_FailsClosed_OnOutputVarWidth()
    {
        // 1 build key + 1 probe row, both carrying a 4096-byte string. The build table fits (~4234 bytes),
        // but the joined output row copies both wide values, charging +8192 on top of the flat row estimate
        // (=> output reserve 8232). Budget 8000 admits the build but refuses the output var-width copy;
        // without that charge the output reserve is only the flat 40 bytes and the join drains.
        string big = new('y', 4096);
        JoinOperator join = InnerJoin(
            Batch(JoinLeft, IntCol(1), StrCol(big)),
            Batch(JoinRight, IntCol(1), StrCol(big)));
        var mem = new BoundedExecutionMemory(8000);
        using IBatchStream stream = Backend.Open(join, Ctx(mem));

        Assert.Throws<ExecutionMemoryException>(() => Drain(stream));
    }

    [Fact]
    public void Aggregate_MinMaxLargeString_TightBudget_FailsClosed_OnOutputVarWidth()
    {
        // Global MIN over a 4096-byte string. The aggregator retains the running-best value (input-side
        // reservation, ~4144 bytes incl. state+output) which fits a 6000-byte budget; the BuildResult copy
        // of that value into the output column charges another 4096 (=> 8240), refused here. Without the
        // output var-width charge the copy is free and the aggregate drains.
        string big = new('x', 4096);
        AggregateOperator agg = GlobalMin(Scan(StrValIn, Batch(StrValIn, StrCol(big, big))));
        var mem = new BoundedExecutionMemory(6000);
        using IBatchStream stream = Backend.Open(agg, Ctx(mem));

        Assert.Throws<ExecutionMemoryException>(() => Drain(stream));
    }

    // ==============================================================================================
    // Input/build-side variable-width accounting — the var-width term on the BUFFERED payload (not
    // the output copy) drives the spill decision. Knife-edge budgets bracket the reservation WITHOUT the
    // var-width term (which fits and never spills) and WITH it (which trips the spill / fail-closed seam).
    // Sort over a single oversized row still fails closed (a lone row cannot be partially spilled); the
    // join spills its build side. Deleting the input/build-side VariableWidthBytes term flips these.
    // ==============================================================================================

    [Fact]
    public void Sort_LargeStringInput_TightBudget_FailsClosed_OnInputVarWidth()
    {
        // One sort row over a 4096-byte string. InterpretedSortStream reserves per buffered row:
        //   _rowBytes(16, flat string estimate) + key.Length(4099 = 1 null-marker + 4096 body + 2 term)
        //   + VariableWidthBytes(4096, true string length) + PermutationEntryBytes(8) = 8219.
        // The output selection chunk then reserves length*sizeof(int) = 4. Without the var-width term
        // the buffer reserve is 4123 (=> 4127 incl. chunk), which fits; with it the build needs 8219.
        // Budget 6000 sits strictly between (4127 < 6000 < 8219): the build is admitted up to the flat
        // + key + permutation reserve but refused once the TRUE string length is charged.
        string big = new('y', 4096);
        SortOperator sort = SortByStr(Scan(StrSortSchema, Batch(StrSortSchema, StrCol(big))));
        var mem = new BoundedExecutionMemory(6000);
        using IBatchStream stream = Backend.Open(sort, Ctx(mem));

        Assert.Throws<ExecutionMemoryException>(() => Drain(stream));
    }

    [Fact]
    public void Join_LargeStringBuild_TightBudget_Spills_OnBuildVarWidth()
    {
        // 1 build (right) row carrying a 4096-byte string, EMPTY probe (left). InterpretedJoinStream
        // reserves for the build row:
        //   _buildRowBytes(20 = int 4 + flat string 16) + key.Length(5 = 1 null-marker + 4 int)
        //   + VariableWidthBytes(4096, true string length)
        //   + overhead(113 = MatchFlag 1 + HashTableEntry 64 + ListHeader 48) = 4234.
        // Without the var-width term the build reserve is 138, which fits and never spills; with it the
        // build needs 4234. Budget 2000 sits strictly between (138 < 2000 < 4234): only the TRUE build-
        // payload string length tips the reservation over, switching the join to a grace-hash spill.
        string big = new('y', 4096);
        JoinOperator join = InnerJoin(
            Batch(JoinLeft, IntCol(), StrCol()),
            Batch(JoinRight, IntCol(1), StrCol(big)));
        var mem = new BoundedExecutionMemory(2000);
        using IBatchStream stream = Backend.Open(join, Ctx(mem));

        Drain(stream); // grace spill, no throw (empty probe -> zero output rows)
        Assert.True(join.Metrics.Snapshot().SpilledBytes > 0);
    }

    // ==============================================================================================
    // F1 — aggregate grouping-KEY output var-width (the data-scaled budget bypass the red-team found).
    // Knife-edge budget: the flat key sizing + the byte-sortable DICTIONARY key (encoded.Length) fit, but
    // the TRUE var-width of the wide key copied into the OUTPUT key columns (_keyColumns) tips over.
    // Deleting the new `+ VariableWidthBytes(keyVectors, row)` term in ResolveGroup flips this from throw
    // to pass (the new test then DRAINS), so the term is the sole tipping factor.
    // ==============================================================================================

    [Fact]
    public void Aggregate_GroupByLargeStringKey_TightBudget_FailsClosed_OnKeyVarWidth()
    {
        // One group whose key is a 4096-byte string, COUNT(*). InterpretedAggregateStream.ResolveGroup
        // reserves per newly-discovered group:
        //   state(8) + output(24 = flat string key 16 + long count 8) + encoded.Length(4099
        //   = 1 null-marker + 4096 body + 2 terminator) + HashTableEntryBytes(64)
        //   + VariableWidthBytes(keyVectors, row)(4096, the TRUE key length copied into _keyColumns) = 8291.
        // Without the new key var-width term the reserve is 4195 (= 8291 - 4096), which fits and drains.
        // Budget 6000 sits strictly between (4195 < 6000 < 8291): only the grouping-key var-width copied
        // into the output key columns tips the reservation over — the exact #359 data-scaled class.
        string big = new('k', 4096);
        AggregateOperator agg = GroupByStrCount(Scan(StrKeyIn, Batch(StrKeyIn, StrCol(big, big))));
        var mem = new BoundedExecutionMemory(6000);
        using IBatchStream stream = Backend.Open(agg, Ctx(mem));

        Assert.Throws<ExecutionMemoryException>(() => Drain(stream));
    }

    // ==============================================================================================
    // F2a — the aggregate BuildResult emit loop is cancellable within a bounded window.
    // A cancel fired the instant the emit loop starts reserving is observed at the next poll boundary
    // (RowPollInterval groups), before any output batch is produced. Removing the emit-loop poll lets the
    // whole emit loop run to completion (an output batch is emitted before the re-entry check throws), so
    // the zero-output-before-cancel assertion flips — that is the non-vacuity contract.
    // ==============================================================================================

    [Fact]
    public void Aggregate_CancelDuringResultEmit_ObservedWithinPollInterval_ReleasesAllReservations()
    {
        // 4096 distinct integer-key groups, MIN(string). The build phase reserves deterministically twice
        // per row (ResolveGroup + the MIN running-best Retain) => 2*groups = 8192 reservations, all before
        // BuildResult. The emit loop then reserves once per group (the MIN output var-width copy). Cancel
        // after reservation 8193 — the first reservation of the emit loop (emit group 0) — so cancellation
        // is fired strictly INSIDE BuildResult, past the whole build. The emit poll observes it at the next
        // boundary (emit group RowPollInterval), so the build throws before emitting any output row.
        const int groups = 4096;
        const int buildReservations = 2 * groups;            // ResolveGroup + Retain per row (deterministic)
        const int cancelAfter = buildReservations + 1;       // first emit-loop reservation
        using var cts = new CancellationTokenSource();
        var mem = new CancelOnReserveMemory(new BoundedExecutionMemory(long.MaxValue), cts, cancelAfter);
        AggregateOperator agg = GroupByIntMinStr(DistinctIntKeyStrScan(groups));
        IBatchStream stream = Backend.Open(agg, Ctx(mem, cts.Token));

        int emitted = 0;
        Assert.Throws<OperationCanceledException>(() =>
        {
            while (stream.TryGetNext(out _))
            {
                emitted++;
            }
        });

        // Observed inside BuildResult: no output row was emitted before the cancel was seen.
        Assert.Equal(0, emitted);
        // Observed within RowPollInterval groups of the cancel: the emit loop stopped early, it did not run
        // to completion. (Without the emit poll it would reserve all `groups` outputs => 3*groups total.)
        Assert.True(
            mem.Reservations < 3 * groups,
            $"emit ran to completion ({mem.Reservations} reservations); the emit-loop poll did not stop it");
        Assert.True(
            mem.Reservations <= cancelAfter + CancellationPolicy.RowPollInterval,
            $"cancel observed after {mem.Reservations - buildReservations} emit reservations; "
            + $"expected within {CancellationPolicy.RowPollInterval}");
        Assert.True(mem.ReservedBytes > 0); // partial build+emit still held until Dispose

        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes); // released exactly once (a double release trips the ledger)
    }

    // ==============================================================================================
    // F2b — the in-memory sort (Array.Sort) is cancellable within a bounded number of comparisons.
    // The cancel is fired deterministically in the window between buffering and the sort (the child's EOF
    // pull), the only place the in-comparer poll can be the observer. Its first comparison polls, so the
    // cancel is seen at sort entry and NO output row is produced. Removing the in-comparer poll lets
    // Array.Sort run uncancellably to completion (an output batch is emitted before the re-entry check
    // throws), flipping the zero-output assertion — the non-vacuity contract for the cancellable sort.
    // ==============================================================================================

    [Fact]
    public void Sort_CancelEnteringSort_ObservedBeforeAnyOutput_ReleasesAllReservations()
    {
        // 64 reverse-ordered rows so Array.Sort performs comparisons. The child yields the one buffered
        // batch, then cancels on its EOF pull (after buffering, before the sort) WITHOUT a boundary token
        // check — so the sort's in-comparer poll is the sole observer. Constructed directly because the
        // public scan checks the token at its boundary and would catch the cancel before the sort.
        const int rows = 64;
        var ids = ColumnVectors.Create(DataTypes.IntegerType, rows);
        for (int i = 0; i < rows; i++)
        {
            ids.AppendValue(rows - i);
        }

        ColumnBatch batch = Batch(SortSchema, ids);
        var sortOp = SortById(Scan(SortSchema, batch));
        var orderings = new[]
        {
            new DeltaSharp.Engine.RowFormat.SortKeyOrdering(
                DeltaSharp.Engine.RowFormat.SortKeyDirection.Ascending,
                DeltaSharp.Engine.RowFormat.NullSortOrder.NullsFirst),
        };
        var projection = new RowKeyProjection(
            [sortOp.SortOrders[0].Expression], sortOp.InputSchema(0), "interpreted-vectorized",
            OperatorKind.Sort, orderings);

        using var cts = new CancellationTokenSource();
        var child = new CancelOnEofStream(SortSchema, batch, cts);
        var mem = new BoundedExecutionMemory(long.MaxValue);
        IBatchStream stream = new InterpretedSortStream(sortOp, projection, child, Ctx(mem, cts.Token));

        int emitted = 0;
        Assert.Throws<OperationCanceledException>(() =>
        {
            while (stream.TryGetNext(out _))
            {
                emitted++;
            }
        });

        Assert.Equal(0, emitted); // the sort observed the cancel at entry; no reordered row was emitted
        Assert.True(mem.ReservedBytes > 0); // buffered rows still held until Dispose

        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes); // released exactly once (a double release trips the ledger)
    }
}
