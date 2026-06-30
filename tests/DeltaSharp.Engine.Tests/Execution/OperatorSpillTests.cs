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
        store is null ? new(memory) : new(memory) { SpillStore = store };

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
        // Group 0 overflows regardless of grouping order (MaxValue + MaxValue); other groups are normal.
        // ANSI throws in BOTH spill and no-spill; Legacy nulls group 0 in both, identical otherwise.
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
    // AC4 — local EXCHANGE spill: partitions recoverable, per-partition row counts/contents match.
    // ==============================================================================================

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
        var mem1 = new BoundedExecutionMemory(long.MaxValue);
        IBatchStream s1 = Backend.Open(Build(), Ctx(mem1));
        List<List<string>> ample = DrainPerBatch(s1, ExchangeSchema);
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
        Assert.Empty(Directory.GetFiles(store.Root)); // every run's temp file deleted on Dispose
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
        Assert.Empty(Directory.GetFiles(inner.Root)); // partial run files cleaned up on the failure path
        Assert.Equal(0, mem.ReservedBytes);
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
