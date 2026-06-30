using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Engine.Execution.Spill;
using DeltaSharp.Engine.Types;
using Xunit;
using ExecutionContext = DeltaSharp.Engine.Execution.ExecutionContext;

namespace DeltaSharp.Engine.Tests.Execution;

/// <summary>
/// Verifies STORY-03.6.2 (#156): the stateful operators (aggregate, sort, join, exchange-local) spill
/// partial state to an <see cref="ISpillStore"/> when a memory reservation is refused, instead of
/// failing closed, and the spilled execution is RESULT-IDENTICAL to the no-spill execution.
/// </summary>
/// <remarks>
/// <para>The core oracle is a spill-vs-no-spill identity test per operator: the SAME input is run under
/// an ample budget (no spill, asserted via <c>SpilledBytes == 0</c>) and a tight budget (forced spill,
/// asserted via <c>SpilledBytes &gt; 0</c>), and the two outputs must be identical — ordered for sort
/// (a global order), order-insensitive multiset for aggregate/join, per-partition for exchange. A
/// mutation to a merge step (drop a spilled run, mis-route a probe partition) breaks the identity, so
/// these tests are non-vacuous.</para>
/// <para>AC5 (I/O failure) injects a <see cref="FaultSpillStore"/> that throws
/// <see cref="SpillIOException"/> on a spill write/read: the operator must surface that deterministic
/// typed error, emit NO partial output, and (after Dispose) release every reservation to zero — proved
/// by the over-release-throwing <see cref="BoundedExecutionMemory"/> ledger.</para>
/// <para>The <c>OperatorSpill</c> type-name prefix keeps these distinct from the sibling
/// <c>OperatorMemory</c> / <c>Parity</c> lanes in this directory.</para>
/// </remarks>
public class OperatorSpillTests
{
    private static IExecutionBackend Backend => InterpretedVectorizedBackend.Instance;

    private static ExecutionContext Ctx(IExecutionMemory memory, ISpillStore? store = null) =>
        new(memory) { SpillStore = store ?? new MemorySpillStore() };

    // ==============================================================================================
    // AC1 — hash AGGREGATE spill: partial state serialized and merged to the same result as no-spill.
    // ==============================================================================================

    [Fact]
    public void Aggregate_Spill_MatchesNoSpill_CountSumMinMaxAvg()
    {
        // 256 groups × 3 rows each. COUNT(*), SUM(long), MIN, MAX, AVG over small ints (exact in double).
        AggregateOperator Build() => Agg(
            Scan(AggIn, AggInput(groups: 256, perGroup: 3)),
            keys: [Col(0, DataTypes.IntegerType)],
            aggs:
            [
                Count(),
                Of(AggregateFunction.Sum, 1, DataTypes.IntegerType),
                Of(AggregateFunction.Min, 1, DataTypes.IntegerType),
                Of(AggregateFunction.Max, 1, DataTypes.IntegerType),
                Of(AggregateFunction.Average, 1, DataTypes.IntegerType),
            ]);

        AssertSpillMatchesNoSpill(Build, tightBudget: 6000, ordered: false);
    }

    [Fact]
    public void Aggregate_Spill_MatchesNoSpill_NullKeyGroupPreserved()
    {
        // Null keys form their own group (Spark GROUP BY keeps nulls); it must survive serialize→merge.
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.IntegerType, 1);
        MutableColumnVector vals = ColumnVectors.Create(DataTypes.IntegerType, 1);
        for (int g = 0; g < 200; g++)
        {
            for (int o = 0; o < 2; o++)
            {
                if (g % 7 == 0)
                {
                    keys.AppendNull(); // funnel every 7th group's rows into the single null-key group
                }
                else
                {
                    keys.AppendValue(g);
                }

                vals.AppendValue((g * 4) + o);
            }
        }

        ColumnBatch batch = Batch(AggIn, keys, vals);
        AggregateOperator Build() => Agg(
            Scan(AggIn, batch),
            keys: [Col(0, DataTypes.IntegerType)],
            aggs: [Count(), Of(AggregateFunction.Sum, 1, DataTypes.IntegerType)]);

        AssertSpillMatchesNoSpill(Build, tightBudget: 4000, ordered: false);
    }

    [Fact]
    public void Aggregate_Spill_MatchesNoSpill_SumDecimal()
    {
        var dec = new DecimalType(18, 2);
        var schema = new StructType(
        [
            new StructField("k", DataTypes.IntegerType, nullable: true),
            new StructField("d", dec, nullable: true),
        ]);
        var outSchema = new StructType(
        [
            new StructField("k", DataTypes.IntegerType, nullable: true),
            new StructField("s", new DecimalType(28, 2), nullable: true),
        ]);

        MutableColumnVector keys = ColumnVectors.Create(DataTypes.IntegerType, 1);
        MutableColumnVector d = ColumnVectors.Create(dec, 1);
        for (int g = 0; g < 180; g++)
        {
            for (int o = 0; o < 3; o++)
            {
                keys.AppendValue(g);
                d.AppendValue((long)((g * 100) + o)); // unscaled
            }
        }

        ColumnBatch batch = Batch(schema, keys, d);
        AggregateOperator Build() => new(
            Scan(schema, batch),
            outSchema,
            [Col(0, DataTypes.IntegerType)],
            [new AggregateExpression(AggregateFunction.Sum, new ColumnReference(1, dec, nullable: true))]);

        AssertSpillMatchesNoSpill(Build, tightBudget: 5000, ordered: false);
    }

    [Theory]
    [InlineData(AnsiMode.Ansi)]
    [InlineData(AnsiMode.Legacy)]
    public void Aggregate_Spill_PreservesOverflowSemantics(AnsiMode mode)
    {
        // Group 0 overflows on the FINAL value regardless of grouping order (MaxValue + MaxValue = 2·MaxValue
        // does not fit bigint); other groups are normal. Post-#156-B2 the fit check is on the final sum, so
        // ANSI throws in BOTH spill and no-spill and Legacy nulls group 0 in both, identical otherwise.
        ColumnBatch Data()
        {
            MutableColumnVector keys = ColumnVectors.Create(DataTypes.IntegerType, 1);
            MutableColumnVector vals = ColumnVectors.Create(DataTypes.LongType, 1);
            keys.AppendValue(0);
            vals.AppendValue(long.MaxValue);
            keys.AppendValue(0);
            vals.AppendValue(long.MaxValue);
            for (int g = 1; g < 200; g++)
            {
                keys.AppendValue(g);
                vals.AppendValue((long)g);
            }

            return Batch(LongValIn, keys, vals);
        }

        AggregateOperator Build() => new(
            Scan(LongValIn, Data()),
            LongSumOut,
            [Col(0, DataTypes.IntegerType)],
            [new AggregateExpression(AggregateFunction.Sum, new ColumnReference(1, DataTypes.LongType, nullable: true), mode)]);

        if (mode == AnsiMode.Ansi)
        {
            Assert.Throws<ArithmeticOverflowException>(() => RunRender(Build(), LongSumOut, long.MaxValue, out _, out _));
            Assert.Throws<ArithmeticOverflowException>(() => RunRender(Build(), LongSumOut, 3000, out _, out _));
        }
        else
        {
            AssertSpillMatchesNoSpill(Build, tightBudget: 3000, ordered: false);
        }
    }

    [Theory]
    [InlineData(AnsiMode.Ansi)]
    [InlineData(AnsiMode.Legacy)]
    public void Aggregate_Spill_TransientOverflow_FinalInRange_SpillEqualsNoSpill(AnsiMode mode)
    {
        // Architect A1 (#156 B2) repro: group 0's contributions are long.MaxValue, then (after many other
        // groups push the table to spill) +1000 and -1000. The TRUE sum is long.MaxValue — IN RANGE — but a
        // single-pass CHECKED fold poisons the group on the transient long.MaxValue+1000 step (Legacy→NULL,
        // ANSI→throw), while a per-partition spill fold restarts +1000/-1000 from 0 and never sees the
        // transient. That made the same query+data flip NULL/throw ↔ MaxValue purely on the memory budget.
        //
        // After B2 (Int128 accumulate, fit-check deferred to the FINAL value), BOTH arms react only to the
        // true sum: neither throws nor nulls, and both emit long.MaxValue for group 0 — spill == no-spill.
        ColumnBatch Data()
        {
            MutableColumnVector keys = ColumnVectors.Create(DataTypes.IntegerType, 1);
            MutableColumnVector vals = ColumnVectors.Create(DataTypes.LongType, 1);
            keys.AppendValue(0);
            vals.AppendValue(long.MaxValue); // group 0's first contribution, spilled before the rest arrive
            for (int g = 1; g < 400; g++)     // many groups in between → forces a spill that flushes group 0
            {
                keys.AppendValue(g);
                vals.AppendValue((long)g);
            }

            keys.AppendValue(0);
            vals.AppendValue(1000L);  // transient: MaxValue + 1000 would overflow a checked long fold
            keys.AppendValue(0);
            vals.AppendValue(-1000L); // …but the FINAL sum is long.MaxValue, back in range
            return Batch(LongValIn, keys, vals);
        }

        AggregateOperator Build() => new(
            Scan(LongValIn, Data()),
            LongSumOut,
            [Col(0, DataTypes.IntegerType)],
            [new AggregateExpression(AggregateFunction.Sum, new ColumnReference(1, DataTypes.LongType, nullable: true), mode)]);

        // No-spill (ample): the true in-range sum, no throw/null in EITHER mode.
        List<string> ample = RunRender(Build(), LongSumOut, long.MaxValue, out long spilledAmple, out _);
        Assert.Equal(0, spilledAmple);
        Assert.Contains("0|" + long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), ample);

        // Spill (tight): must AGREE — same in-range value, no transient-driven divergence.
        List<string> tight = RunRender(Build(), LongSumOut, 3000, out long spilledTight, out long leakTight);
        Assert.True(spilledTight > 0, "the tight budget must force a spill");
        Assert.Equal(0, leakTight);
        Assert.Contains("0|" + long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), tight);
        AssertSameRows(ample, tight);
    }

    // ==============================================================================================
    // AC1 (#156 N1) — decimal SUM full-width accumulator: a decimal mantissa is ALREADY a full-width
    // Int128, so an unchecked Int128 accumulator wraps after as few as 3 near-max values and silently
    // returns a wrong in-range value. The BigInteger accumulator holds the exact running sum and the single
    // Emit gate detects the true overflow (ANSI throws, Legacy nulls) — order-invariantly.
    // ==============================================================================================

    [Theory]
    [InlineData(AnsiMode.Ansi)]
    [InlineData(AnsiMode.Legacy)]
    public void Aggregate_Decimal_SumOverflowsInt128Accumulator_DetectedNotWrapped(AnsiMode mode)
    {
        // Architect C7 repro: three decimal(38,0) values of 8.5e37. Int128.MaxValue ≈ 1.7014e38, so the
        // FIRST two already sum to 1.7e38 (still inside Int128) and the THIRD wraps an unchecked Int128
        // accumulator to a small-magnitude value that PASSES ToType — a silent wrong result. The true sum is
        // 2.55e38, a genuine decimal(38,0) overflow: ANSI must throw, Legacy must NULL the group.
        var decType = new DecimalType(38, 0);
        var schema = new StructType([new StructField("v", decType, nullable: true)]);
        var outSchema = new StructType([new StructField("s", decType, nullable: true)]);
        Int128 v = Int128.Parse("85000000000000000000000000000000000000"); // 8.5e37

        AggregateOperator Build()
        {
            MutableColumnVector col = ColumnVectors.Create(decType, 3);
            col.AppendValue(v);
            col.AppendValue(v);
            col.AppendValue(v);
            return new AggregateOperator(
                Scan(schema, Batch(schema, col)),
                outSchema,
                [],
                [new AggregateExpression(AggregateFunction.Sum, new ColumnReference(0, decType, nullable: true), mode)]);
        }

        if (mode == AnsiMode.Ansi)
        {
            // No silent wrap: the true overflow surfaces as the typed exception, never a wrong value.
            Assert.Throws<ArithmeticOverflowException>(() => RunRender(Build(), outSchema, long.MaxValue, out _, out _));
        }
        else
        {
            List<string> rows = RunRender(Build(), outSchema, long.MaxValue, out _, out _);
            Assert.Single(rows);
            Assert.Equal("\u2205", rows[0]); // Legacy overflow nulls the group — never the wrapped value
        }
    }

    [Theory]
    [InlineData(AnsiMode.Ansi)]
    [InlineData(AnsiMode.Legacy)]
    public void Aggregate_Spill_Decimal_TransientOverflow_FinalInRange_SpillEqualsNoSpill(AnsiMode mode)
    {
        // Decimal analogue of the A1 (#156 B2) order-invariance proof, now past the Int128 boundary: group 0
        // gets +9.0e37, then (after many other groups force a spill that flushes it) +9.0e37 and -9.0e37. The
        // INTERMEDIATE partial 1.8e38 exceeds Int128.MaxValue (≈1.7014e38) — a CHECKED Int128 fold would
        // throw (ANSI) / null (Legacy) on that transient in the single-pass no-spill arm while a
        // re-partitioned spill arm restarts +9.0e37/-9.0e37 from 0 and never sees it → divergence. The TRUE
        // sum is 9.0e37, a valid decimal(38,0) (< 1e38). The BigInteger accumulator never wraps and never
        // throws on the transient, so BOTH arms emit 9.0e37 and agree — spill == no-spill, order-invariant.
        var decType = new DecimalType(38, 0);
        var schema = new StructType([new StructField("k", DataTypes.IntegerType, nullable: true), new StructField("v", decType, nullable: true)]);
        var outSchema = new StructType([new StructField("k", DataTypes.IntegerType, nullable: true), new StructField("s", decType, nullable: true)]);
        Int128 nine = Int128.Parse("90000000000000000000000000000000000000"); // 9.0e37

        ColumnBatch Data()
        {
            MutableColumnVector keys = ColumnVectors.Create(DataTypes.IntegerType, 1);
            MutableColumnVector vals = ColumnVectors.Create(decType, 1);
            keys.AppendValue(0);
            vals.AppendValue(nine); // group 0's first contribution, spilled before the rest arrive
            for (int g = 1; g < 400; g++) // many groups in between → forces a spill that flushes group 0
            {
                keys.AppendValue(g);
                vals.AppendValue((Int128)g);
            }

            keys.AppendValue(0);
            vals.AppendValue(nine); // transient: 9e37 + 9e37 = 1.8e38 overflows a checked Int128 fold…
            keys.AppendValue(0);
            vals.AppendValue(-nine); // …but the FINAL sum is 9.0e37, a valid decimal(38,0)
            return Batch(schema, keys, vals);
        }

        AggregateOperator Build() => new(
            Scan(schema, Data()),
            outSchema,
            [Col(0, DataTypes.IntegerType)],
            [new AggregateExpression(AggregateFunction.Sum, new ColumnReference(1, decType, nullable: true), mode)]);

        string expectedGroup0 = "0|" + nine.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // No-spill (ample): the true in-range sum, no throw/null in EITHER mode.
        List<string> ample = RunRender(Build(), outSchema, long.MaxValue, out long spilledAmple, out _);
        Assert.Equal(0, spilledAmple);
        Assert.Contains(expectedGroup0, ample);

        // Spill (tight): must AGREE — same in-range value, no transient-driven divergence.
        List<string> tight = RunRender(Build(), outSchema, 5000, out long spilledTight, out long leakTight);
        Assert.True(spilledTight > 0, "the tight budget must force a spill");
        Assert.Equal(0, leakTight);
        Assert.Contains(expectedGroup0, tight);
        AssertSameRows(ample, tight);
    }

    // ==============================================================================================
    // AC2 — SORT spill: sorted runs are k-way merged in the same global order as no-spill (byte-identical).
    // ==============================================================================================

    [Fact]
    public void Sort_Spill_MatchesNoSpill_SingleKey()
    {
        ColumnBatch Data()
        {
            MutableColumnVector ids = ColumnVectors.Create(DataTypes.IntegerType, 1);
            MutableColumnVector tag = ColumnVectors.Create(DataTypes.IntegerType, 1);
            int x = 1;
            for (int i = 0; i < 2000; i++)
            {
                x = (x * 1103515245) + 12345; // deterministic LCG, fixed seed
                ids.AppendValue(x & 0x3FF);    // many duplicate keys to exercise the stable tie-break
                tag.AppendValue(i);            // unique tag => verifies stable ordinal tie-break survives
            }

            return Batch(SortTwoCol, ids, tag);
        }

        SortOperator Build() => new(
            Scan(SortTwoCol, Data()),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);

        AssertSpillMatchesNoSpill(Build, tightBudget: 20000, ordered: true);
    }

    [Theory]
    [InlineData(SortDirection.Ascending, NullOrdering.NullsFirst)]
    [InlineData(SortDirection.Ascending, NullOrdering.NullsLast)]
    [InlineData(SortDirection.Descending, NullOrdering.NullsFirst)]
    [InlineData(SortDirection.Descending, NullOrdering.NullsLast)]
    public void Sort_Spill_MatchesNoSpill_MultiKey_NullsFirstLast(SortDirection dir, NullOrdering nulls)
    {
        ColumnBatch Data()
        {
            MutableColumnVector k1 = ColumnVectors.Create(DataTypes.IntegerType, 1);
            MutableColumnVector k2 = ColumnVectors.Create(DataTypes.StringType, 1);
            MutableColumnVector tag = ColumnVectors.Create(DataTypes.IntegerType, 1);
            int x = 7;
            for (int i = 0; i < 1500; i++)
            {
                x = (x * 1103515245) + 12345;
                if ((x & 0xF) == 0)
                {
                    k1.AppendNull();
                }
                else
                {
                    k1.AppendValue(x & 0x7);
                }

                if ((x & 0xF0) == 0)
                {
                    k2.AppendNull();
                }
                else
                {
                    k2.AppendBytes(Encoding.UTF8.GetBytes("s" + ((x >> 4) & 0x7)));
                }

                tag.AppendValue(i);
            }

            return Batch(MultiKeySort, k1, k2, tag);
        }

        SortOperator Build() => new(
            Scan(MultiKeySort, Data()),
            [
                new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: true), dir, nulls),
                new SortOrder(new ColumnReference(1, DataTypes.StringType, nullable: true), dir, nulls),
            ]);

        AssertSpillMatchesNoSpill(Build, tightBudget: 40000, ordered: true);
    }

    [Fact]
    public void Sort_Spill_MatchesNoSpill_DoubleNaNNegativeZero()
    {
        double[] specials = [double.NaN, 0.0, -0.0, double.PositiveInfinity, double.NegativeInfinity, 1.5, -1.5];
        ColumnBatch Data()
        {
            MutableColumnVector k = ColumnVectors.Create(DataTypes.DoubleType, 1);
            MutableColumnVector tag = ColumnVectors.Create(DataTypes.IntegerType, 1);
            int x = 3;
            for (int i = 0; i < 1400; i++)
            {
                x = (x * 1103515245) + 12345;
                int sel = (x >>> 8) % specials.Length;
                k.AppendValue(specials[sel]);
                tag.AppendValue(i);
            }

            return Batch(DblSort, k, tag);
        }

        SortOperator Build() => new(
            Scan(DblSort, Data()),
            [new SortOrder(new ColumnReference(0, DataTypes.DoubleType, nullable: true))]);

        AssertSpillMatchesNoSpill(Build, tightBudget: 25000, ordered: true);
    }

    // ==============================================================================================
    // AC3 — hash JOIN spill: partitioned spill/probe preserves cardinality and null-key semantics
    //       across all six join types.
    // ==============================================================================================

    [Theory]
    [InlineData(JoinType.Inner)]
    [InlineData(JoinType.LeftOuter)]
    [InlineData(JoinType.RightOuter)]
    [InlineData(JoinType.FullOuter)]
    [InlineData(JoinType.LeftSemi)]
    [InlineData(JoinType.LeftAnti)]
    public void Join_Spill_MatchesNoSpill_AllTypes(JoinType type)
    {
        // Build and probe share overlapping keys with DUPLICATES (multiplicity) on both sides, plus
        // build-only keys, probe-only keys, and NULL keys on both sides (null never matches). The spilled
        // grace join must reproduce the exact no-spill multiset for every join type.
        JoinOperator Build() => MakeJoin(type, JoinBuildData(), JoinProbeData());
        AssertSpillMatchesNoSpill(() => Build(), JoinOut(type), tightBudget: 100000, ordered: false);
    }

    [Fact]
    public void Join_Spill_PreservesMultiplicityAcrossPartitionBoundary()
    {
        // One key with M build rows and N probe rows must yield exactly M×N joined rows even when that
        // key's partition is spilled. Spread many such keys so several land in spilled partitions.
        ColumnBatch BuildSide()
        {
            MutableColumnVector k = ColumnVectors.Create(DataTypes.IntegerType, 1);
            MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, 1);
            for (int key = 0; key < 500; key++)
            {
                for (int m = 0; m < 3; m++) // M = 3 build rows per key
                {
                    k.AppendValue(key);
                    v.AppendBytes(Encoding.UTF8.GetBytes($"b{key}.{m}"));
                }
            }

            return Batch(JoinRight, k, v);
        }

        ColumnBatch ProbeSide()
        {
            MutableColumnVector k = ColumnVectors.Create(DataTypes.IntegerType, 1);
            MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, 1);
            for (int key = 0; key < 500; key++)
            {
                for (int n = 0; n < 2; n++) // N = 2 probe rows per key => expect 6 joined rows/key
                {
                    k.AppendValue(key);
                    v.AppendBytes(Encoding.UTF8.GetBytes($"p{key}.{n}"));
                }
            }

            return Batch(JoinLeft, k, v);
        }

        JoinOperator Build() => MakeJoin(JoinType.Inner, BuildSide(), ProbeSide());

        // No-spill cardinality is exactly 500 keys × 3 × 2 = 3000 rows.
        List<string> ample = RunRender(Build(), JoinOut(JoinType.Inner), long.MaxValue, out long spilledAmple, out _);
        Assert.Equal(0, spilledAmple);
        Assert.Equal(3000, ample.Count);

        List<string> tight = RunRender(Build(), JoinOut(JoinType.Inner), 90000, out long spilledTight, out long leaked);
        Assert.True(spilledTight > 0);
        Assert.Equal(0, leaked);
        AssertSameRows(ample, tight);
    }

    // ==============================================================================================
    // F2 — the spill CAP must bound disk for JOIN incrementally: a join whose probe is many batches must
    // fail closed MID-DRAIN after at most ~one batch of overshoot, not after the whole probe is on disk.
    // ==============================================================================================

    [Fact]
    public void Join_SpillCap_FailsClosedMidProbeDrain_BoundedOvershoot_NotWholeStream()
    {
        const int probeBatches = 300;
        const int rowsPerProbeBatch = 64;
        const int buildRows = 2000;

        // Build is a single batch; under a tight memory budget it grace-spills. Probe is MANY small batches
        // so the per-batch incremental cap check can fail closed partway through the probe drain.
        ColumnBatch BuildSide()
        {
            MutableColumnVector k = ColumnVectors.Create(DataTypes.IntegerType, 1);
            MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, 1);
            for (int i = 0; i < buildRows; i++)
            {
                k.AppendValue(i % 1000);
                v.AppendBytes(Encoding.UTF8.GetBytes($"b{i}"));
            }

            return Batch(JoinRight, k, v);
        }

        ColumnBatch[] ProbeBatches()
        {
            var batches = new ColumnBatch[probeBatches];
            for (int b = 0; b < probeBatches; b++)
            {
                MutableColumnVector k = ColumnVectors.Create(DataTypes.IntegerType, 1);
                MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, 1);
                for (int r = 0; r < rowsPerProbeBatch; r++)
                {
                    k.AppendValue(((b * rowsPerProbeBatch) + r) % 1000);
                    v.AppendBytes(Encoding.UTF8.GetBytes($"p{b}.{r}"));
                }

                batches[b] = Batch(JoinLeft, k, v);
            }

            return batches;
        }

        JoinOperator BuildJoin() => new(
            Scan(JoinLeft, ProbeBatches()),
            Scan(JoinRight, BuildSide()),
            JoinOut(JoinType.Inner),
            JoinType.Inner,
            [new ColumnReference(0, DataTypes.IntegerType, nullable: true)],
            [new ColumnReference(0, DataTypes.IntegerType, nullable: true)]);

        const long budget = 90000; // tight enough to grace-spill the build, ample enough for per-batch scratch

        // Non-vacuity: with a GENEROUS cap the identical join completes with full correct output.
        var genMem = new BoundedExecutionMemory(budget, long.MaxValue);
        JoinOperator genOp = BuildJoin();
        long fullOutput;
        using (IBatchStream gen = Backend.Open(genOp, Ctx(genMem)))
        {
            fullOutput = Drain(gen).Sum(b => (long)b.LogicalRowCount);
        }

        long fullSpill = genOp.Metrics.Snapshot().SpilledBytes;
        Assert.True(fullSpill > 0, "the tight budget must force the join to grace-spill");
        Assert.True(fullOutput > 0, "the generous-cap join must produce output");

        // Tight cap: a fifth of the whole spill — well ABOVE the (small) build spill so the breach lands in
        // the PROBE drain, yet a small fraction of the whole probe so it must trip partway through.
        long cap = fullSpill / 5;
        var mem = new BoundedExecutionMemory(budget, cap);
        JoinOperator op = BuildJoin();
        IBatchStream stream = Backend.Open(op, Ctx(mem));

        var emitted = new List<ColumnBatch>();
        SpillBudgetExceededException? error = null;
        try
        {
            while (stream.TryGetNext(out ColumnBatch? batch))
            {
                emitted.Add(batch);
            }
        }
        catch (SpillBudgetExceededException ex)
        {
            error = ex;
        }

        long failSpill = mem.SpilledBytes;

        Assert.NotNull(error);                                // deterministic typed fail-closed
        Assert.Equal(0, emitted.Sum(b => b.LogicalRowCount)); // partitioning precedes emit → NO partial join output
        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes);                   // release-all (the ledger proves exactly-once)

        // Bounded overshoot: at the throw the cumulative spilled total is at most the cap plus ~one probe
        // batch — NOT the entire probe stream. (fullSpill/probeBatches over-estimates a single probe batch
        // since fullSpill includes the build too, so it is a safe per-batch upper bound.)
        long oneBatchUpperBound = (fullSpill / probeBatches) + 256;
        Assert.True(
            failSpill <= cap + oneBatchUpperBound,
            $"overshoot must be ≤ ~one batch: failSpill={failSpill}, cap={cap}, oneBatchUpperBound={oneBatchUpperBound}");
        Assert.True(
            failSpill < fullSpill / 2,
            $"must fail closed MID-drain, not after writing the whole stream: failSpill={failSpill}, fullSpill={fullSpill}");
    }



    [Fact]
    public void Exchange_Spill_MatchesNoSpill_PerPartitionCountsAndContents()
    {
        ColumnBatch Data()
        {
            MutableColumnVector k = ColumnVectors.Create(DataTypes.IntegerType, 1);
            MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, 1);
            int x = 11;
            for (int i = 0; i < 1500; i++)
            {
                x = (x * 1103515245) + 12345;
                k.AppendValue(x & 0x1FF);
                v.AppendBytes(Encoding.UTF8.GetBytes("v" + i));
            }

            return Batch(ExchangeSchema, k, v);
        }

        ExchangeLocalOperator Build() => new(
            Scan(ExchangeSchema, Data()),
            partitionCount: 8,
            [new ColumnReference(0, DataTypes.IntegerType, nullable: false)]);

        // Render PER PARTITION: the exchange emits one batch per partition in id order, so partition i is
        // emission i. Compare each partition's row multiset (and therefore its count) independently.
        ExchangeLocalOperator ampleOp = Build();
        var mem1 = new BoundedExecutionMemory(long.MaxValue);
        IBatchStream s1 = Backend.Open(ampleOp, Ctx(mem1));
        List<List<string>> ample = DrainPerBatch(s1, ExchangeSchema);
        Assert.Equal(0, ampleOp.Metrics.Snapshot().SpilledBytes); // non-vacuity: the ample arm never spills
        s1.Dispose();

        ExchangeLocalOperator tightOp = Build();
        var mem2 = new BoundedExecutionMemory(8000);
        IBatchStream s2 = Backend.Open(tightOp, Ctx(mem2));
        List<List<string>> tight = DrainPerBatch(s2, ExchangeSchema);
        Assert.True(tightOp.Metrics.Snapshot().SpilledBytes > 0);
        s2.Dispose();
        Assert.Equal(0, mem2.ReservedBytes);

        Assert.Equal(ample.Count, tight.Count); // same number of emitted partitions
        for (int p = 0; p < ample.Count; p++)
        {
            Assert.Equal(ample[p].Count, tight[p].Count); // per-partition row COUNT matches
            AssertSameRows(ample[p], tight[p]);            // per-partition CONTENTS match
        }
    }

    // ==============================================================================================
    // AC5 — injected spill I/O FAILURE: release-all + deterministic error + no partial output.
    // ==============================================================================================

    [Fact]
    public void Aggregate_SpillWriteFailure_ReleasesAll_DeterministicError_NoPartialOutput()
    {
        AggregateOperator Build() => Agg(
            Scan(AggIn, AggInput(groups: 256, perGroup: 3)),
            keys: [Col(0, DataTypes.IntegerType)],
            aggs: [Count(), Of(AggregateFunction.Sum, 1, DataTypes.IntegerType)]);

        AssertSpillWriteFailureIsClean(Build, AggOut, tightBudget: 6000);
    }

    [Fact]
    public void Sort_SpillWriteFailure_ReleasesAll_DeterministicError_NoPartialOutput()
    {
        SortOperator Build() => new(
            Scan(SortTwoCol, SortInput(2000)),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);

        AssertSpillWriteFailureIsClean(Build, SortTwoCol, tightBudget: 8000);
    }

    [Fact]
    public void Join_SpillWriteFailure_ReleasesAll_DeterministicError_NoPartialOutput()
    {
        JoinOperator Build() => MakeJoin(JoinType.Inner, JoinBuildData(), JoinProbeData());
        AssertSpillWriteFailureIsClean(Build, JoinOut(JoinType.Inner), tightBudget: 6000);
    }

    [Fact]
    public void Exchange_SpillWriteFailure_ReleasesAll_DeterministicError_NoPartialOutput()
    {
        ExchangeLocalOperator Build() => new(
            Scan(ExchangeSchema, ExchangeInput(1500)),
            partitionCount: 8,
            [new ColumnReference(0, DataTypes.IntegerType, nullable: false)]);

        AssertSpillWriteFailureIsClean(Build, ExchangeSchema, tightBudget: 4000);
    }

    [Fact]
    public void Sort_SpillReadFailure_ReleasesAll_DeterministicError()
    {
        // A read failure during the k-way merge phase must also fail deterministically and release all.
        var store = new FaultSpillStore { FailOnRead = true };
        SortOperator op = new(
            Scan(SortTwoCol, SortInput(2000)),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);
        var mem = new BoundedExecutionMemory(20000);
        IBatchStream stream = Backend.Open(op, Ctx(mem, store));

        Assert.Throws<SpillIOException>(() => Drain(stream));
        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes);
    }

    // ==============================================================================================
    // Temp-file cleanup: a disk-backed spill leaves no leaked temp files on completion OR failure.
    // ==============================================================================================

    [Fact]
    public void TempFileSpillStore_NormalCompletion_DeletesAllTempFiles()
    {
        using var store = new TempFileSpillStore();
        SortOperator op = new(
            Scan(SortTwoCol, SortInput(2000)),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);
        var mem = new BoundedExecutionMemory(20000);
        IBatchStream stream = Backend.Open(op, Ctx(mem, store));

        Drain(stream);
        Assert.True(Directory.Exists(store.Root)); // runs spilled into the temp dir
        Assert.NotEmpty(Directory.GetFiles(store.Root));

        stream.Dispose();
        AssertEventuallyNoFiles(store.Root); // every run's temp file deleted on Dispose
    }

    [Fact]
    public void TempFileSpillStore_WriteFailure_DeletesAllTempFiles()
    {
        using var inner = new TempFileSpillStore();
        var store = new FaultSpillStore(inner) { FailOnWriteAfter = 3 };
        SortOperator op = new(
            Scan(SortTwoCol, SortInput(2000)),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);
        var mem = new BoundedExecutionMemory(8000);
        IBatchStream stream = Backend.Open(op, Ctx(mem, store));

        Assert.Throws<SpillIOException>(() => Drain(stream));
        stream.Dispose();
        AssertEventuallyNoFiles(inner.Root); // partial run files cleaned up on the failure path
        Assert.Equal(0, mem.ReservedBytes);
    }

    [Fact]
    public void TempFileSpillStore_Cancellation_DeletesAllTempFiles()
    {
        // Cancel deterministically AFTER real spill bytes have been written (the 200th spill write), then
        // dispose: every temp file — the run being written and any completed runs — must be gone. The
        // trigger is a write count, not a timer, so the test is race-free.
        using var cts = new CancellationTokenSource();
        using var inner = new TempFileSpillStore();
        var store = new CancelAfterWritesStore(inner, cts, cancelAfter: 200);
        SortOperator op = new(
            Scan(SortTwoCol, SortInput(2000)),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);
        var mem = new BoundedExecutionMemory(8000);
        IBatchStream stream = Backend.Open(op, new ExecutionContext(mem, cts.Token) { SpillStore = store });

        Assert.Throws<OperationCanceledException>(() => Drain(stream));
        stream.Dispose();
        AssertEventuallyNoFiles(inner.Root);
        Assert.Equal(0, mem.ReservedBytes);
    }

    // ==============================================================================================
    // #156 B1 — bounded blast-radius: disk default, a cumulative spill cap, and owner-only temp perms.
    // ==============================================================================================

    [Fact]
    public void ExecutorDefault_SpillStore_IsTempFileStore_NotMemory()
    {
        // B1(a): the production default must be the disk store (which actually relieves memory), NOT the
        // off-ledger MemorySpillStore (which re-holds spilled bytes on the GC heap, untracked → pod OOM).
        var ctx = new ExecutionContext(BoundedExecutionMemory.Unbounded);
        try
        {
            Assert.IsType<TempFileSpillStore>(ctx.SpillStore);
            Assert.IsNotType<MemorySpillStore>(ctx.SpillStore);
        }
        finally
        {
            ((IDisposable)ctx.SpillStore).Dispose();
        }
    }

    [Fact]
    public void SpillBytesCap_Exceeded_FailsClosed_DeterministicError_ReleasesAll_NoPartialOutput()
    {
        // B1(b): a tight MEMORY budget forces a spill, but a tiny cumulative SPILL cap is breached by the
        // very first spilled run → fail closed with the typed SpillBudgetExceededException, no partial
        // output, and (after Dispose) every reservation released to zero. The memory budget (20000) is the
        // SAME as the Generous companion below — large enough to complete the merge — so the ONLY reason
        // this run fails is the spill cap. That makes the non-vacuity clean: drop the cap check and this
        // identical run drains to full output instead of throwing a different (memory) error.
        SortOperator op = new(
            Scan(SortTwoCol, SortInput(2000)),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);
        var mem = new BoundedExecutionMemory(budgetBytes: 20000, maxSpillBytes: 1);
        var store = new MemorySpillStore();
        IBatchStream stream = Backend.Open(op, Ctx(mem, store));

        var emitted = new List<ColumnBatch>();
        SpillBudgetExceededException? error = null;
        try
        {
            while (stream.TryGetNext(out ColumnBatch? batch))
            {
                emitted.Add(batch);
            }
        }
        catch (SpillBudgetExceededException ex)
        {
            error = ex;
        }

        Assert.NotNull(error);                                // deterministic typed error
        Assert.Equal(0, emitted.Sum(b => b.LogicalRowCount)); // no partial output reached the consumer
        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes);                   // release-all (the ledger proves exactly-once)
    }

    [Fact]
    public void SpillBytesCap_Generous_AllowsSpillToComplete()
    {
        // Non-vacuity for the cap: with the SAME tight memory budget but an unbounded spill cap, the spill
        // completes and produces full output — so the failure above is the CAP, not just any spill.
        SortOperator op = new(
            Scan(SortTwoCol, SortInput(2000)),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);
        var mem = new BoundedExecutionMemory(budgetBytes: 20000, maxSpillBytes: long.MaxValue);
        var store = new MemorySpillStore();
        using IBatchStream stream = Backend.Open(op, Ctx(mem, store));

        List<string> rows = Render(Drain(stream), SortTwoCol);
        Assert.Equal(2000, rows.Count);
        Assert.True(mem.SpilledBytes > 0, "the tight budget must force a spill");
    }

    [Fact]
    public void TempFileSpillStore_OnUnix_CreatesOwnerOnlyDirAndFiles()
    {
        // B1(c): spilled tenant rows must not be world/group readable on a shared pod (Security F3).
        if (OperatingSystem.IsWindows())
        {
            return; // UnixFileMode is a no-op on Windows
        }

        using var store = new TempFileSpillStore();
        ISpillSegment segment = store.CreateSegment("perm");
        segment.Write(new byte[] { 1, 2, 3 });

        const UnixFileMode dir0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode file0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(dir0700, File.GetUnixFileMode(store.Root));

        // Security F1: the dir name must carry the prefix but an UNPREDICTABLE random suffix (mkdtemp) — it
        // must NOT be the old deterministic deltasharp-spill-{pid}-{counter} name an attacker could pre-create.
        string name = Path.GetFileName(store.Root);
        Assert.StartsWith("deltasharp-spill-", name);
        Assert.NotEqual($"deltasharp-spill-{Environment.ProcessId}-1", name);

        string file = Assert.Single(Directory.GetFiles(store.Root));
        Assert.Equal(file0600, File.GetUnixFileMode(file));
        segment.Dispose();
    }

    [Fact]
    public void TempFileSpillStore_Roots_AreUnpredictable_AndDistinct()
    {
        // Security F1: each store materializes a UNIQUE unguessable temp dir (Directory.CreateTempSubdirectory),
        // so an attacker cannot pre-create the predicted path to leak segment metadata or clobber the 0600
        // data files. Two stores must get distinct roots, both carrying the prefix but NOT the old predictable
        // deltasharp-spill-{pid}-{counter} format.
        using var a = new TempFileSpillStore();
        using var b = new TempFileSpillStore();

        // Root is empty until the first spill materializes the directory lazily (a store that never spills
        // allocates no disk).
        Assert.Equal(string.Empty, a.Root);

        a.CreateSegment("x").Write(new byte[] { 1 });
        b.CreateSegment("x").Write(new byte[] { 1 });

        Assert.NotEqual(a.Root, b.Root);
        Assert.StartsWith("deltasharp-spill-", Path.GetFileName(a.Root));
        Assert.StartsWith("deltasharp-spill-", Path.GetFileName(b.Root));

        string predictable = $"deltasharp-spill-{Environment.ProcessId}-1";
        Assert.NotEqual(predictable, Path.GetFileName(a.Root));
        Assert.NotEqual(predictable, Path.GetFileName(b.Root));
    }

    [Fact]
    public void Sort_DisposeFaultOnOneRun_StillDisposesSiblings_CleansTempFiles()
    {
        // Security F3 (defense-in-depth): one run segment's Dispose throwing an unexpected exception must not
        // strand its sibling runs — they must all be disposed (their temp files deleted) and the fault still
        // surfaces (aggregated). Reverting DisposeRuns to a flat foreach makes a sibling file leak.
        using var realStore = new TempFileSpillStore();
        var store = new DisposeFaultSpillStore(realStore);
        SortOperator op = new(
            Scan(SortTwoCol, SortInput(2000)),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);
        var mem = new BoundedExecutionMemory(20000);
        IBatchStream stream = Backend.Open(op, Ctx(mem, store));

        Drain(stream); // full sorted output; spills into multiple runs (sibling segments)
        Assert.True(op.Metrics.Snapshot().SpilledBytes > 0, "the tight budget must force a spill");
        Assert.True(
            Directory.GetFiles(realStore.Root).Length > 1,
            "the test needs multiple sibling runs to prove siblings are disposed past the throw");

        AggregateException agg = Assert.Throws<AggregateException>(stream.Dispose);
        Assert.Single(agg.InnerExceptions); // exactly the one injected fault

        // Every sibling run's temp file was still deleted despite the first run's Dispose throwing.
        AssertEventuallyNoFiles(realStore.Root);
        Assert.Equal(0, mem.ReservedBytes);
    }

    // A spill store that wraps a real store and makes the FIRST segment's Dispose throw (after delegating, so
    // its own file is still deleted) — proves a Dispose fault on one run cannot strand the siblings (F3).
    private sealed class DisposeFaultSpillStore : ISpillStore
    {
        private readonly ISpillStore _inner;
        private int _created;

        public DisposeFaultSpillStore(ISpillStore inner) => _inner = inner;

        public ISpillSegment CreateSegment(string label)
        {
            bool faulty = Interlocked.Increment(ref _created) == 1;
            return new Segment(_inner.CreateSegment(label), faulty);
        }

        private sealed class Segment : ISpillSegment
        {
            private readonly ISpillSegment _inner;
            private readonly bool _faulty;

            public Segment(ISpillSegment inner, bool faulty)
            {
                _inner = inner;
                _faulty = faulty;
            }

            public long BytesWritten => _inner.BytesWritten;

            public void Write(ReadOnlySpan<byte> record) => _inner.Write(record);

            public ISpillSegmentReader OpenRead() => _inner.OpenRead();

            public void Dispose()
            {
                _inner.Dispose();
                if (_faulty)
                {
                    throw new InvalidOperationException("injected segment dispose fault");
                }
            }
        }
    }

    // Polls to a steady state instead of a single Directory.GetFiles snapshot, so a slow filesystem delete
    // cannot flake the cleanup assertions; on failure it reports the remaining file names. (Quality MAJOR.)
    private static void AssertEventuallyNoFiles(string root)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (!Directory.Exists(root) || Directory.GetFiles(root).Length == 0)
            {
                return;
            }

            Thread.Sleep(5);
        }

        string[] remaining = Directory.Exists(root) ? Directory.GetFiles(root) : [];
        Assert.True(
            remaining.Length == 0,
            $"temp files remained under '{root}': {string.Join(", ", remaining.Select(Path.GetFileName))}");
    }

    // A spill store that cancels the supplied source after the Nth payload write reaches the inner store —
    // a deterministic cancel-mid-spill trigger (no timing) for the cancellation cleanup test.
    private sealed class CancelAfterWritesStore : ISpillStore
    {
        private readonly ISpillStore _inner;
        private readonly CancellationTokenSource _cts;
        private readonly int _cancelAfter;
        private int _writes;

        public CancelAfterWritesStore(ISpillStore inner, CancellationTokenSource cts, int cancelAfter)
        {
            _inner = inner;
            _cts = cts;
            _cancelAfter = cancelAfter;
        }

        public ISpillSegment CreateSegment(string label) => new Segment(this, _inner.CreateSegment(label));

        private void OnWrite()
        {
            if (Interlocked.Increment(ref _writes) == _cancelAfter)
            {
                _cts.Cancel();
            }
        }

        private sealed class Segment : ISpillSegment
        {
            private readonly CancelAfterWritesStore _store;
            private readonly ISpillSegment _inner;

            public Segment(CancelAfterWritesStore store, ISpillSegment inner)
            {
                _store = store;
                _inner = inner;
            }

            public long BytesWritten => _inner.BytesWritten;

            public void Write(ReadOnlySpan<byte> record)
            {
                _inner.Write(record);
                _store.OnWrite();
            }

            public ISpillSegmentReader OpenRead() => _inner.OpenRead();

            public void Dispose() => _inner.Dispose();
        }
    }

    // ==============================================================================================
    // shared assertions
    // ==============================================================================================

    private static void AssertSpillMatchesNoSpill(Func<PhysicalOperator> build, long tightBudget, bool ordered) =>
        AssertSpillMatchesNoSpill(build, build().OutputSchema, tightBudget, ordered);

    private static void AssertSpillMatchesNoSpill(
        Func<PhysicalOperator> build, StructType schema, long tightBudget, bool ordered)
    {
        List<string> ample = RunRender(build(), schema, long.MaxValue, out long spilledAmple, out long leakAmple);
        Assert.Equal(0, spilledAmple); // ample budget never spills
        Assert.Equal(0, leakAmple);

        List<string> tight = RunRender(build(), schema, tightBudget, out long spilledTight, out long leakTight);
        Assert.True(spilledTight > 0, "the tight budget must force a spill"); // non-vacuity: spill happened
        Assert.Equal(0, leakTight);                                           // released to zero

        if (ordered)
        {
            Assert.Equal(ample, tight); // byte-identical global order
        }
        else
        {
            AssertSameRows(ample, tight); // identical multiset
        }
    }

    // Forces a spill, then injects a write failure: the operator must throw SpillIOException, surface NO
    // output rows, and (after Dispose) release every reservation to zero.
    private static void AssertSpillWriteFailureIsClean(Func<PhysicalOperator> build, StructType schema, long tightBudget)
    {
        var store = new FaultSpillStore { FailOnWriteAfter = 2 };
        PhysicalOperator op = build();
        var mem = new BoundedExecutionMemory(tightBudget);
        IBatchStream stream = Backend.Open(op, Ctx(mem, store));

        var emitted = new List<ColumnBatch>();
        SpillIOException? error = null;
        try
        {
            while (stream.TryGetNext(out ColumnBatch? batch))
            {
                emitted.Add(batch);
            }
        }
        catch (SpillIOException ex)
        {
            error = ex;
        }

        Assert.NotNull(error);                                       // deterministic typed error
        Assert.Equal(0, emitted.Sum(b => b.LogicalRowCount));        // no partial output reached the consumer

        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes);                          // release-all (the ledger proves exactly-once)
    }

    // ==============================================================================================
    // rendering / draining
    // ==============================================================================================

    private static List<ColumnBatch> Drain(IBatchStream stream)
    {
        var batches = new List<ColumnBatch>();
        while (stream.TryGetNext(out ColumnBatch? batch))
        {
            batches.Add(batch);
        }

        return batches;
    }

    private static List<string> RunRender(
        PhysicalOperator op, StructType schema, long budget, out long spilled, out long leaked)
    {
        var mem = new BoundedExecutionMemory(budget);
        IBatchStream stream = Backend.Open(op, Ctx(mem));
        List<string> rows;
        try
        {
            rows = Render(Drain(stream), schema);
        }
        finally
        {
            stream.Dispose();
        }

        spilled = op.Metrics.Snapshot().SpilledBytes;
        leaked = mem.ReservedBytes;
        return rows;
    }

    private static List<List<string>> DrainPerBatch(IBatchStream stream, StructType schema)
    {
        var perBatch = new List<List<string>>();
        while (stream.TryGetNext(out ColumnBatch? batch))
        {
            perBatch.Add(Render([batch], schema));
        }

        return perBatch;
    }

    private static List<string> Render(IEnumerable<ColumnBatch> batches, StructType schema)
    {
        var rows = new List<string>();
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector[] cols = new ColumnVector[schema.Count];
            for (int c = 0; c < schema.Count; c++)
            {
                cols[c] = batch.SelectedColumn(c);
            }

            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                var sb = new StringBuilder();
                for (int c = 0; c < schema.Count; c++)
                {
                    if (c > 0)
                    {
                        sb.Append('|');
                    }

                    sb.Append(RenderCell(cols[c], r, schema[c].DataType));
                }

                rows.Add(sb.ToString());
            }
        }

        return rows;
    }

    private static string RenderCell(ColumnVector col, int r, DataType type)
    {
        if (col.IsNull(r))
        {
            return "\u2205";
        }

        return type switch
        {
            IntegerType or DateType => col.GetValue<int>(r).ToString(),
            LongType or TimestampType => col.GetValue<long>(r).ToString(),
            ShortType => col.GetValue<short>(r).ToString(),
            ByteType => col.GetValue<byte>(r).ToString(),
            BooleanType => col.GetValue<bool>(r).ToString(),
            FloatType => BitConverter.SingleToInt32Bits(col.GetValue<float>(r)).ToString(),
            DoubleType => BitConverter.DoubleToInt64Bits(col.GetValue<double>(r)).ToString(),
            DecimalType { IsCompact: true } => col.GetValue<long>(r).ToString(),
            DecimalType => col.GetValue<Int128>(r).ToString(),
            StringType or BinaryType => Convert.ToBase64String(col.GetBytes(r)),
            _ => throw new NotSupportedException(type.ToString()),
        };
    }

    // Order-insensitive multiset equality via an ordinal-sorted projection (a wrong/missing/extra row
    // changes the sorted projection, so this is mutation-resistant despite ignoring row order).
    private static void AssertSameRows(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        List<string> e = expected.OrderBy(s => s, StringComparer.Ordinal).ToList();
        List<string> a = actual.OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(e, a);
    }

    // ==============================================================================================
    // schemas + builders
    // ==============================================================================================

    private static readonly StructType AggIn = new(
    [
        new StructField("k", DataTypes.IntegerType, nullable: true),
        new StructField("v", DataTypes.IntegerType, nullable: true),
    ]);

    private static readonly StructType AggOut = new(
    [
        new StructField("k", DataTypes.IntegerType, nullable: true),
        new StructField("c", DataTypes.LongType, nullable: false),
        new StructField("s", DataTypes.LongType, nullable: true),
    ]);

    private static readonly StructType LongValIn = new(
    [
        new StructField("k", DataTypes.IntegerType, nullable: true),
        new StructField("v", DataTypes.LongType, nullable: true),
    ]);

    private static readonly StructType LongSumOut = new(
    [
        new StructField("k", DataTypes.IntegerType, nullable: true),
        new StructField("s", DataTypes.LongType, nullable: true),
    ]);

    private static readonly StructType SortTwoCol = new(
    [
        new StructField("id", DataTypes.IntegerType, nullable: false),
        new StructField("tag", DataTypes.IntegerType, nullable: false),
    ]);

    private static readonly StructType MultiKeySort = new(
    [
        new StructField("k1", DataTypes.IntegerType, nullable: true),
        new StructField("k2", DataTypes.StringType, nullable: true),
        new StructField("tag", DataTypes.IntegerType, nullable: false),
    ]);

    private static readonly StructType DblSort = new(
    [
        new StructField("k", DataTypes.DoubleType, nullable: true),
        new StructField("tag", DataTypes.IntegerType, nullable: false),
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

    private static readonly StructType ExchangeSchema = new(
    [
        new StructField("k", DataTypes.IntegerType, nullable: false),
        new StructField("v", DataTypes.StringType, nullable: false),
    ]);

    private static ColumnReference Col(int ord, DataType type) => new(ord, type, nullable: true);

    private static AggregateExpression Count() => new(AggregateFunction.Count, null);

    private static AggregateExpression Of(AggregateFunction fn, int ord, DataType type) =>
        new(fn, new ColumnReference(ord, type, nullable: true));

    private static AggregateOperator Agg(InMemoryScanOperator input, ColumnReference[] keys, AggregateExpression[] aggs)
    {
        var fields = new List<StructField>(keys.Length + aggs.Length);
        for (int i = 0; i < keys.Length; i++)
        {
            fields.Add(new StructField($"k{i}", keys[i].Type, keys[i].Nullable));
        }

        for (int i = 0; i < aggs.Length; i++)
        {
            fields.Add(new StructField($"a{i}", aggs[i].Type, aggs[i].Nullable));
        }

        return new AggregateOperator(input, new StructType(fields), keys, aggs);
    }

    private static StructType JoinOut(JoinType type) => type is JoinType.LeftSemi or JoinType.LeftAnti
        ? JoinLeft
        : new StructType(
        [
            new StructField("lk", DataTypes.IntegerType, nullable: true),
            new StructField("lv", DataTypes.StringType, nullable: true),
            new StructField("rk", DataTypes.IntegerType, nullable: true),
            new StructField("rv", DataTypes.StringType, nullable: true),
        ]);

    private static JoinOperator MakeJoin(JoinType type, ColumnBatch build, ColumnBatch probe) => new(
        Scan(JoinLeft, probe),
        Scan(JoinRight, build),
        JoinOut(type),
        type,
        [new ColumnReference(0, DataTypes.IntegerType, nullable: true)],
        [new ColumnReference(0, DataTypes.IntegerType, nullable: true)]);

    private static ColumnBatch JoinBuildData()
    {
        MutableColumnVector k = ColumnVectors.Create(DataTypes.IntegerType, 1);
        MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, 1);
        for (int key = 0; key < 1000; key++)
        {
            int copies = (key % 3) + 1; // 1..3 build rows per key (multiplicity)
            for (int c = 0; c < copies; c++)
            {
                k.AppendValue(key);
                v.AppendBytes(Encoding.UTF8.GetBytes($"b{key}.{c}"));
            }
        }

        // Null-key build rows: never indexed, surface only in RIGHT/FULL OUTER unmatched.
        for (int n = 0; n < 5; n++)
        {
            k.AppendNull();
            v.AppendBytes(Encoding.UTF8.GetBytes($"bnull{n}"));
        }

        return Batch(JoinRight, k, v);
    }

    private static ColumnBatch JoinProbeData()
    {
        MutableColumnVector k = ColumnVectors.Create(DataTypes.IntegerType, 1);
        MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, 1);
        for (int key = 500; key < 1500; key++) // overlaps [500,1000) with build; [1000,1500) are probe-only
        {
            int copies = (key % 2) + 1; // 1..2 probe rows per key (multiplicity)
            for (int c = 0; c < copies; c++)
            {
                k.AppendValue(key);
                v.AppendBytes(Encoding.UTF8.GetBytes($"p{key}.{c}"));
            }
        }

        // Null-key probe rows: never match; LEFT/FULL pad with null right, ANTI keep, INNER/SEMI/RIGHT drop.
        for (int n = 0; n < 4; n++)
        {
            k.AppendNull();
            v.AppendBytes(Encoding.UTF8.GetBytes($"pnull{n}"));
        }

        return Batch(JoinLeft, k, v);
    }

    private static ColumnBatch AggInput(int groups, int perGroup)
    {
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.IntegerType, 1);
        MutableColumnVector vals = ColumnVectors.Create(DataTypes.IntegerType, 1);
        for (int g = 0; g < groups; g++)
        {
            for (int o = 0; o < perGroup; o++)
            {
                keys.AppendValue(g);
                vals.AppendValue((g * 8) + o);
            }
        }

        return Batch(AggIn, keys, vals);
    }

    private static ColumnBatch SortInput(int rows)
    {
        MutableColumnVector ids = ColumnVectors.Create(DataTypes.IntegerType, 1);
        MutableColumnVector tag = ColumnVectors.Create(DataTypes.IntegerType, 1);
        int x = 1;
        for (int i = 0; i < rows; i++)
        {
            x = (x * 1103515245) + 12345;
            ids.AppendValue(x & 0x3FF);
            tag.AppendValue(i);
        }

        return Batch(SortTwoCol, ids, tag);
    }

    private static ColumnBatch ExchangeInput(int rows)
    {
        MutableColumnVector k = ColumnVectors.Create(DataTypes.IntegerType, 1);
        MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, 1);
        int x = 11;
        for (int i = 0; i < rows; i++)
        {
            x = (x * 1103515245) + 12345;
            k.AppendValue(x & 0x1FF);
            v.AppendBytes(Encoding.UTF8.GetBytes("v" + i));
        }

        return Batch(ExchangeSchema, k, v);
    }

    private static ColumnBatch Batch(StructType schema, params ColumnVector[] columns) =>
        new ManagedColumnBatch(schema, columns, columns[0].Length);

    private static InMemoryScanOperator Scan(StructType schema, params ColumnBatch[] batches) =>
        new(schema, batches);
}
