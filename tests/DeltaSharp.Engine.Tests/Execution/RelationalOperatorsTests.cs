using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.Execution.Spill;
using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Types;
using Xunit;
using ExecutionContext = DeltaSharp.Engine.Execution.ExecutionContext;

namespace DeltaSharp.Engine.Tests.Execution;

/// <summary>
/// Exercises the v1 relational operators added in STORY-03.2.2 — the hash/global
/// <see cref="InterpretedAggregateStream"/>, the byte-sortable <see cref="InterpretedSortStream"/>,
/// the hash <see cref="InterpretedJoinStream"/>, and the local-repartition
/// <see cref="InterpretedExchangeLocalStream"/> — through the public <see cref="InterpretedOperators"/>
/// dispatch. Every test is written to fail on a wrong aggregate/sort/join/partition result (not just on
/// crashes): aggregates are checked against scalar oracles and Spark null/ANSI rules, sorts against the
/// <see cref="RowOrderingComparer"/> oracle across all asc/desc × nulls-first/last shapes, joins against
/// hand-computed fixtures with key multiplicity and null-key policy, and the exchange against the
/// hash-colocation / round-robin / preservation contract. Cross-cutting tests prove laziness,
/// cancellation at batch boundaries, bounded-memory reservation, populated metrics, fail-fast on
/// unshipped shapes, and interpreted↔compiled parity.
/// </summary>
public class RelationalOperatorsTests
{
    private static IExecutionBackend Backend => InterpretedVectorizedBackend.Instance;

    // ----- column builders (storage shape) -----

    private static MutableColumnVector IntCol(params int?[] values)
        => Fill(ColumnVectors.Create(DataTypes.IntegerType, Math.Max(values.Length, 1)), values, (v, x) => v.AppendValue(x));

    private static MutableColumnVector LongCol(params long?[] values)
        => Fill(ColumnVectors.Create(DataTypes.LongType, Math.Max(values.Length, 1)), values, (v, x) => v.AppendValue(x));

    private static MutableColumnVector TsCol(params long?[] values)
        => Fill(ColumnVectors.Create(DataTypes.TimestampType, Math.Max(values.Length, 1)), values, (v, x) => v.AppendValue(x));

    private static MutableColumnVector DblCol(params double?[] values)
        => Fill(ColumnVectors.Create(DataTypes.DoubleType, Math.Max(values.Length, 1)), values, (v, x) => v.AppendValue(x));

    private static MutableColumnVector DecCol(int precision, int scale, params long?[] unscaled)
        => Fill(ColumnVectors.Create(new DecimalType(precision, scale), Math.Max(unscaled.Length, 1)), unscaled, (v, x) => v.AppendValue(x));

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

    private static MutableColumnVector Fill<T>(MutableColumnVector v, T?[] values, Action<MutableColumnVector, T> append)
        where T : struct
    {
        foreach (T? x in values)
        {
            if (x.HasValue)
            {
                append(v, x.Value);
            }
            else
            {
                v.AppendNull();
            }
        }

        return v;
    }

    private static ColumnBatch Batch(StructType schema, params ColumnVector[] columns)
        => new ManagedColumnBatch(schema, columns, columns.Length > 0 ? columns[0].Length : 0);

    private static InMemoryScanOperator Scan(StructType schema, params ColumnBatch[] batches)
        => new(schema, batches);

    private static ExecutionContext Ctx(IExecutionMemory? memory = null, CancellationToken cancellation = default)
        => new(memory ?? BoundedExecutionMemory.Unbounded, cancellation) { SpillStore = new MemorySpillStore() };

    private static List<ColumnBatch> Drain(IBatchStream stream)
    {
        var batches = new List<ColumnBatch>();
        while (stream.TryGetNext(out ColumnBatch? batch))
        {
            batches.Add(batch);
        }

        return batches;
    }

    private static List<ColumnBatch> OpenDrain(PhysicalOperator op, ExecutionContext? ctx = null)
    {
        using IBatchStream stream = Backend.Open(op, ctx ?? Ctx());
        return Drain(stream);
    }

    // ----- output readers (across all drained batches) -----

    private static List<int?> Ints(IEnumerable<ColumnBatch> batches, int ord)
    {
        var values = new List<int?>();
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector c = batch.SelectedColumn(ord);
            for (int i = 0; i < c.Length; i++)
            {
                values.Add(c.IsNull(i) ? null : c.GetValue<int>(i));
            }
        }

        return values;
    }

    private static List<long?> Longs(IEnumerable<ColumnBatch> batches, int ord)
    {
        var values = new List<long?>();
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector c = batch.SelectedColumn(ord);
            for (int i = 0; i < c.Length; i++)
            {
                values.Add(c.IsNull(i) ? null : c.GetValue<long>(i));
            }
        }

        return values;
    }

    private static List<double?> Dbls(IEnumerable<ColumnBatch> batches, int ord)
    {
        var values = new List<double?>();
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector c = batch.SelectedColumn(ord);
            for (int i = 0; i < c.Length; i++)
            {
                values.Add(c.IsNull(i) ? null : c.GetValue<double>(i));
            }
        }

        return values;
    }

    // Reads a COMPACT decimal column's unscaled magnitude (stored as long for precision <= 18).
    private static List<long?> DecUnscaled(IEnumerable<ColumnBatch> batches, int ord) => Longs(batches, ord);

    private static List<string?> Strs(IEnumerable<ColumnBatch> batches, int ord)
    {
        var values = new List<string?>();
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector column = batch.SelectedColumn(ord);
            for (int i = 0; i < column.Length; i++)
            {
                values.Add(column.IsNull(i) ? null : Encoding.UTF8.GetString(column.GetBytes(i)));
            }
        }

        return values;
    }

    private static int RowCount(IEnumerable<ColumnBatch> batches) => batches.Sum(b => b.LogicalRowCount);

    // Order-insensitive multiset equality via an ordinal-sorted string projection. A wrong/missing/extra
    // row changes the sorted projection, so this is mutation-resistant despite ignoring row order.
    private static void AssertSameRows<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        static List<string> Norm(IEnumerable<T> rows) =>
            rows.Select(x => x?.ToString() ?? "\u2205").OrderBy(s => s, StringComparer.Ordinal).ToList();

        Assert.Equal(Norm(expected), Norm(actual));
    }

    // ===================================================================================================
    // AGGREGATE
    // ===================================================================================================

    private static AggregateOperator Agg(PhysicalOperator input, ColumnReference[] keys, AggregateExpression[] aggs)
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

    private static AggregateExpression Count() => new(AggregateFunction.Count, null);

    private static AggregateExpression Of(AggregateFunction fn, int ord, DataType type, AnsiMode mode = AnsiMode.Ansi)
        => new(fn, new ColumnReference(ord, type, nullable: true), mode);

    [Fact]
    public void GlobalAggregate_AllFunctions_MatchScalarOracle_WithSparkNullSemantics()
    {
        // x = [1, 2, 3, null]. COUNT(*) counts the null row too (=4); COUNT(x)/SUM/AVG/MIN/MAX skip it.
        var schema = new StructType([new StructField("x", DataTypes.IntegerType, nullable: true)]);
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, IntCol(1, 2, 3, null))),
            keys: [],
            aggs:
            [
                Count(),                                   // a0: COUNT(*)        -> 4
                Of(AggregateFunction.Count, 0, DataTypes.IntegerType),   // a1: COUNT(x)  -> 3
                Of(AggregateFunction.Sum, 0, DataTypes.IntegerType),     // a2: SUM(x)    -> 6 (long)
                Of(AggregateFunction.Min, 0, DataTypes.IntegerType),     // a3: MIN(x)    -> 1
                Of(AggregateFunction.Max, 0, DataTypes.IntegerType),     // a4: MAX(x)    -> 3
                Of(AggregateFunction.Average, 0, DataTypes.IntegerType), // a5: AVG(x)    -> 2.0
            ]);

        List<ColumnBatch> result = OpenDrain(agg);

        Assert.Equal(1, RowCount(result));
        Assert.Equal(4L, Longs(result, 0)[0]);
        Assert.Equal(3L, Longs(result, 1)[0]);
        Assert.Equal(6L, Longs(result, 2)[0]);
        Assert.Equal(1, Ints(result, 3)[0]);
        Assert.Equal(3, Ints(result, 4)[0]);
        Assert.Equal(2.0, Dbls(result, 5)[0]);
    }

    [Fact]
    public void GlobalAggregate_EmptyInput_EmitsExactlyOneRow_CountZeroOthersNull()
    {
        var schema = new StructType([new StructField("x", DataTypes.IntegerType, nullable: true)]);
        AggregateOperator agg = Agg(
            Scan(schema), // zero batches
            keys: [],
            aggs: [Count(), Of(AggregateFunction.Sum, 0, DataTypes.IntegerType), Of(AggregateFunction.Average, 0, DataTypes.IntegerType), Of(AggregateFunction.Min, 0, DataTypes.IntegerType)]);

        List<ColumnBatch> result = OpenDrain(agg);

        Assert.Equal(1, RowCount(result));
        Assert.Equal(0L, Longs(result, 0)[0]);   // COUNT(*) over empty = 0
        Assert.Null(Longs(result, 1)[0]);         // SUM over empty = NULL
        Assert.Null(Dbls(result, 2)[0]);          // AVG over empty = NULL
        Assert.Null(Ints(result, 3)[0]);          // MIN over empty = NULL
    }

    [Fact]
    public void GroupedAggregate_EmptyInput_EmitsZeroRows()
    {
        var schema = new StructType([new StructField("k", DataTypes.StringType, nullable: true)]);
        AggregateOperator agg = Agg(
            Scan(schema),
            keys: [new ColumnReference(0, DataTypes.StringType, nullable: true)],
            aggs: [Count()]);

        List<ColumnBatch> result = OpenDrain(agg);

        Assert.Equal(0, RowCount(result));
    }

    [Fact]
    public void GroupedAggregate_SumAndCount_PerGroup_WithNullKeyGroup()
    {
        // key = [a, a, b, null, null]; val = [1, 2, 3, 4, null].
        // groups: a -> count*=2 countv=2 sum=3 ; b -> 1/1/3 ; null -> count*=2 countv=1 sum=4.
        var schema = new StructType(
        [
            new StructField("k", DataTypes.StringType, nullable: true),
            new StructField("v", DataTypes.IntegerType, nullable: true),
        ]);
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, StrCol("a", "a", "b", null, null), IntCol(1, 2, 3, 4, null))),
            keys: [new ColumnReference(0, DataTypes.StringType, nullable: true)],
            aggs: [Count(), Of(AggregateFunction.Count, 1, DataTypes.IntegerType), Of(AggregateFunction.Sum, 1, DataTypes.IntegerType)]);

        List<ColumnBatch> result = OpenDrain(agg);

        var rows = Zip4(Strs(result, 0), Longs(result, 1), Longs(result, 2), Longs(result, 3));
        var expected = new List<(string?, long?, long?, long?)>
        {
            ("a", 2L, 2L, 3L),
            ("b", 1L, 1L, 3L),
            (null, 2L, 1L, 4L),
        };
        AssertSameRows(expected, rows);
    }

    private static List<(string?, long?, long?, long?)> Zip4(List<string?> a, List<long?> b, List<long?> c, List<long?> d)
    {
        var rows = new List<(string?, long?, long?, long?)>(a.Count);
        for (int i = 0; i < a.Count; i++)
        {
            rows.Add((a[i], b[i], c[i], d[i]));
        }

        return rows;
    }

    [Fact]
    public void GroupedAggregate_AllNullValueGroup_SumMinMaxAvgNull_CountStarCountsRows_CountArgZero()
    {
        var schema = new StructType(
        [
            new StructField("k", DataTypes.StringType, nullable: false),
            new StructField("v", DataTypes.IntegerType, nullable: true),
        ]);
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, StrCol("g", "g"), IntCol(null, null))),
            keys: [new ColumnReference(0, DataTypes.StringType, nullable: false)],
            aggs:
            [
                Count(),
                Of(AggregateFunction.Count, 1, DataTypes.IntegerType),
                Of(AggregateFunction.Sum, 1, DataTypes.IntegerType),
                Of(AggregateFunction.Min, 1, DataTypes.IntegerType),
                Of(AggregateFunction.Max, 1, DataTypes.IntegerType),
                Of(AggregateFunction.Average, 1, DataTypes.IntegerType),
            ]);

        List<ColumnBatch> result = OpenDrain(agg);

        Assert.Equal(1, RowCount(result));
        Assert.Equal(2L, Longs(result, 1)[0]); // COUNT(*) = 2 (counts null-valued rows)
        Assert.Equal(0L, Longs(result, 2)[0]); // COUNT(v) = 0
        Assert.Null(Longs(result, 3)[0]);       // SUM = null
        Assert.Null(Ints(result, 4)[0]);        // MIN = null
        Assert.Null(Ints(result, 5)[0]);        // MAX = null
        Assert.Null(Dbls(result, 6)[0]);        // AVG = null
    }

    [Fact]
    public void GlobalSum_Long_AnsiOverflow_Throws()
    {
        var schema = new StructType([new StructField("v", DataTypes.LongType, nullable: true)]);
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, LongCol(long.MaxValue, 1))),
            keys: [],
            aggs: [Of(AggregateFunction.Sum, 0, DataTypes.LongType, AnsiMode.Ansi)]);

        using IBatchStream stream = Backend.Open(agg, Ctx());
        Assert.Throws<ArithmeticOverflowException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void GlobalSum_Long_LegacyOverflow_NullsTheGroup()
    {
        var schema = new StructType([new StructField("v", DataTypes.LongType, nullable: true)]);
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, LongCol(long.MaxValue, 1))),
            keys: [],
            aggs: [Of(AggregateFunction.Sum, 0, DataTypes.LongType, AnsiMode.Legacy)]);

        List<ColumnBatch> result = OpenDrain(agg);

        Assert.Equal(1, RowCount(result));
        Assert.Null(Longs(result, 0)[0]);
    }

    [Fact]
    public void GlobalSum_Decimal_WidensPrecisionAndAccumulatesExactly()
    {
        // decimal(5,2) values 123.45 + 111.11 -> decimal(15,2) = 234.56 (unscaled 23456).
        var schema = new StructType([new StructField("v", new DecimalType(5, 2), nullable: true)]);
        var sum = Of(AggregateFunction.Sum, 0, new DecimalType(5, 2));
        Assert.Equal(new DecimalType(15, 2), sum.Type); // SUM widens precision by 10 (capped at 38)

        AggregateOperator agg = Agg(Scan(schema, Batch(schema, DecCol(5, 2, 12345, 11111))), keys: [], aggs: [sum]);
        List<ColumnBatch> result = OpenDrain(agg);

        Assert.Equal(23456L, DecUnscaled(result, 0)[0]);
    }

    [Fact]
    public void GlobalMinMax_Double_TreatsNaNAsLargest_AndSkipsNulls()
    {
        var schema = new StructType([new StructField("v", DataTypes.DoubleType, nullable: true)]);
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, DblCol(1.0, double.NaN, 2.0, null))),
            keys: [],
            aggs: [Of(AggregateFunction.Min, 0, DataTypes.DoubleType), Of(AggregateFunction.Max, 0, DataTypes.DoubleType)]);

        List<ColumnBatch> result = OpenDrain(agg);

        Assert.Equal(1.0, Dbls(result, 0)[0]);          // MIN ignores NaN-as-largest -> 1.0
        Assert.True(double.IsNaN(Dbls(result, 1)![0]!.Value)); // MAX = NaN (largest)
    }

    [Fact]
    public void GlobalMinMax_String_Lexicographic()
    {
        var schema = new StructType([new StructField("v", DataTypes.StringType, nullable: true)]);
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, StrCol("banana", "apple", "cherry", null))),
            keys: [],
            aggs: [Of(AggregateFunction.Min, 0, DataTypes.StringType), Of(AggregateFunction.Max, 0, DataTypes.StringType)]);

        List<ColumnBatch> result = OpenDrain(agg);

        Assert.Equal("apple", Strs(result, 0)[0]);
        Assert.Equal("cherry", Strs(result, 1)[0]);
    }

    [Fact]
    public void GroupedAggregate_DoubleKey_CanonicalizesNaNAndNegativeZero()
    {
        // Spark groups all NaNs together and treats -0.0 == 0.0; the byte-sortable key must do the same.
        // keys [NaN, NaN, 0.0, -0.0] -> exactly 2 groups, each COUNT(*) = 2.
        var schema = new StructType(
        [
            new StructField("k", DataTypes.DoubleType, nullable: false),
            new StructField("v", DataTypes.IntegerType, nullable: false),
        ]);
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, DblCol(double.NaN, double.NaN, 0.0, -0.0), IntCol(1, 1, 1, 1))),
            keys: [new ColumnReference(0, DataTypes.DoubleType, nullable: false)],
            aggs: [Count()]);

        List<ColumnBatch> result = OpenDrain(agg);

        Assert.Equal(2, RowCount(result));
        Assert.All(Longs(result, 1), c => Assert.Equal(2L, c));
    }

    [Fact]
    public void AverageOfDecimal_IsDeferred_FailsFastAtOpen()
    {
        var schema = new StructType([new StructField("v", new DecimalType(10, 2), nullable: true)]);
        AggregateOperator agg = Agg(Scan(schema, Batch(schema, DecCol(10, 2, 100))), keys: [], aggs: [Of(AggregateFunction.Average, 0, new DecimalType(10, 2))]);

        Assert.Throws<UnsupportedOperatorException>(() => Backend.Open(agg, Ctx()));
    }

    [Fact]
    public void Aggregate_PopulatesMetrics()
    {
        var schema = new StructType(
        [
            new StructField("k", DataTypes.StringType, nullable: false),
            new StructField("v", DataTypes.IntegerType, nullable: true),
        ]);
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, StrCol("a", "a", "b"), IntCol(1, 2, 3))),
            keys: [new ColumnReference(0, DataTypes.StringType, nullable: false)],
            aggs: [Of(AggregateFunction.Sum, 1, DataTypes.IntegerType)]);

        List<ColumnBatch> _ = OpenDrain(agg);
        OperatorMetricsSnapshot m = agg.Metrics.Snapshot();

        Assert.Equal(3, m.InputRows);   // 3 source rows consumed
        Assert.Equal(2, m.OutputRows);  // 2 groups emitted
        Assert.True(m.PeakMemoryBytes > 0);
    }

    [Fact]
    public void Aggregate_OverBudget_ThrowsExecutionMemoryException()
    {
        var schema = new StructType(
        [
            new StructField("k", DataTypes.StringType, nullable: false),
            new StructField("v", DataTypes.IntegerType, nullable: true),
        ]);
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, StrCol("a", "b", "c"), IntCol(1, 2, 3))),
            keys: [new ColumnReference(0, DataTypes.StringType, nullable: false)],
            aggs: [Of(AggregateFunction.Sum, 1, DataTypes.IntegerType)]);

        using IBatchStream stream = Backend.Open(agg, Ctx(new BoundedExecutionMemory(8)));
        Assert.Throws<ExecutionMemoryException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void Aggregate_IsLazy_NoRowsConsumedBeforeFirstPull()
    {
        var schema = new StructType(
        [
            new StructField("k", DataTypes.StringType, nullable: false),
            new StructField("v", DataTypes.IntegerType, nullable: true),
        ]);
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, StrCol("a", "b"), IntCol(1, 2))),
            keys: [new ColumnReference(0, DataTypes.StringType, nullable: false)],
            aggs: [Of(AggregateFunction.Sum, 1, DataTypes.IntegerType)]);

        using IBatchStream stream = Backend.Open(agg, Ctx());
        Assert.Equal(0, agg.Metrics.Snapshot().InputRows); // Open must not pull the child

        Assert.True(stream.TryGetNext(out _));
        Assert.Equal(2, agg.Metrics.Snapshot().InputRows);
    }

    [Fact]
    public void Aggregate_CancelledBeforeFirstPull_Throws()
    {
        var schema = new StructType([new StructField("v", DataTypes.IntegerType, nullable: true)]);
        AggregateOperator agg = Agg(Scan(schema, Batch(schema, IntCol(1, 2))), keys: [], aggs: [Of(AggregateFunction.Sum, 0, DataTypes.IntegerType)]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using IBatchStream stream = Backend.Open(agg, Ctx(cancellation: cts.Token));

        Assert.Throws<OperationCanceledException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void Aggregate_InterpretedAndCompiledBackends_ProduceIdenticalResults()
    {
        var schema = new StructType(
        [
            new StructField("k", DataTypes.StringType, nullable: true),
            new StructField("v", DataTypes.IntegerType, nullable: true),
        ]);
        AggregateOperator Build() => Agg(
            Scan(schema, Batch(schema, StrCol("a", "b", "a", null), IntCol(1, 2, 3, 4))),
            keys: [new ColumnReference(0, DataTypes.StringType, nullable: true)],
            aggs: [Of(AggregateFunction.Sum, 1, DataTypes.IntegerType)]);

        (List<string?> Keys, List<long?> Sums) Run(IExecutionBackend backend)
        {
            AggregateOperator agg = Build();
            using IBatchStream stream = backend.Open(agg, Ctx());
            List<ColumnBatch> result = Drain(stream);
            return (Strs(result, 0), Longs(result, 1));
        }

        (List<string?> Keys, List<long?> Sums) interpreted = Run(ExecutionBackends.Select(new ExecutionBackendOptions { ForceInterpreted = true }));
        (List<string?> Keys, List<long?> Sums) compiled = Run(ExecutionBackends.Select());

        AssertSameRows(Zip2(interpreted.Keys, interpreted.Sums), Zip2(compiled.Keys, compiled.Sums));
        var expected = new List<(string?, long?)> { ("a", 4L), ("b", 2L), (null, 4L) };
        AssertSameRows(expected, Zip2(interpreted.Keys, interpreted.Sums));
    }

    private static List<(string?, long?)> Zip2(List<string?> a, List<long?> b)
    {
        var rows = new List<(string?, long?)>(a.Count);
        for (int i = 0; i < a.Count; i++)
        {
            rows.Add((a[i], b[i]));
        }

        return rows;
    }

    // ===================================================================================================
    // SORT
    // ===================================================================================================

    // Oracle: sort row indices by the RowOrderingComparer over the key schema, breaking ties by input
    // index (the operator's documented stable tie-break), then project to ids.
    private static List<long> ExpectedOrder(StructType keySchema, object?[][] keyValsPerRow, long[] ids, SortKeyOrdering[] orderings)
    {
        int[] keyFields = Enumerable.Range(0, keySchema.Count).ToArray();
        var comparer = new RowOrderingComparer(keySchema, keyFields, orderings);
        var idx = Enumerable.Range(0, ids.Length).ToList();
        idx.Sort((a, b) =>
        {
            int c = comparer.Compare(new RowData(keySchema, keyValsPerRow[a]), new RowData(keySchema, keyValsPerRow[b]));
            return c != 0 ? c : a.CompareTo(b);
        });
        return idx.Select(i => ids[i]).ToList();
    }

    public static IEnumerable<object[]> Orderings()
    {
        foreach (SortDirection dir in new[] { SortDirection.Ascending, SortDirection.Descending })
        {
            foreach (NullOrdering no in new[] { NullOrdering.NullsFirst, NullOrdering.NullsLast })
            {
                yield return [dir, no];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Orderings))]
    public void Sort_Int_MatchesComparerOracle_AcrossAllOrderings(SortDirection dir, NullOrdering nulls)
    {
        var schema = new StructType(
        [
            new StructField("key", DataTypes.IntegerType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        ]);
        int?[] keys = [3, null, 1, 2, null, 1, -7];
        long[] ids = [0, 1, 2, 3, 4, 5, 6];
        var op = new SortOperator(
            Scan(schema, Batch(schema, IntCol(keys), LongCol([.. ids.Select(i => (long?)i)]))),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: true), dir, nulls)]);

        List<long?> actual = Longs(OpenDrain(op), 1);

        var keySchema = new StructType([new StructField("key", DataTypes.IntegerType, nullable: true)]);
        object?[][] keyVals = [.. keys.Select(k => new object?[] { k.HasValue ? k.Value : null })];
        List<long> expected = ExpectedOrder(keySchema, keyVals, ids, [ToOrdering(dir, nulls)]);

        Assert.Equal(expected, actual.Select(v => v!.Value).ToList());
    }

    [Theory]
    [MemberData(nameof(Orderings))]
    public void Sort_Double_NaNAndNegativeZero_MatchComparerOracle(SortDirection dir, NullOrdering nulls)
    {
        var schema = new StructType(
        [
            new StructField("key", DataTypes.DoubleType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        ]);
        double?[] keys = [1.0, double.NaN, null, -0.0, 0.0, 2.0, double.NegativeInfinity];
        long[] ids = [0, 1, 2, 3, 4, 5, 6];
        var op = new SortOperator(
            Scan(schema, Batch(schema, DblCol(keys), LongCol([.. ids.Select(i => (long?)i)]))),
            [new SortOrder(new ColumnReference(0, DataTypes.DoubleType, nullable: true), dir, nulls)]);

        List<long?> actual = Longs(OpenDrain(op), 1);

        var keySchema = new StructType([new StructField("key", DataTypes.DoubleType, nullable: true)]);
        object?[][] keyVals = [.. keys.Select(k => new object?[] { k.HasValue ? k.Value : null })];
        List<long> expected = ExpectedOrder(keySchema, keyVals, ids, [ToOrdering(dir, nulls)]);

        Assert.Equal(expected, actual.Select(v => v!.Value).ToList());
    }

    [Theory]
    [MemberData(nameof(Orderings))]
    public void Sort_Decimal_MatchesComparerOracle(SortDirection dir, NullOrdering nulls)
    {
        var dec = new DecimalType(9, 2);
        var schema = new StructType(
        [
            new StructField("key", dec, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        ]);
        long?[] unscaled = [12345, null, -50, 0, 999, -50];
        long[] ids = [0, 1, 2, 3, 4, 5];
        var op = new SortOperator(
            Scan(schema, Batch(schema, DecCol(9, 2, unscaled), LongCol([.. ids.Select(i => (long?)i)]))),
            [new SortOrder(new ColumnReference(0, dec, nullable: true), dir, nulls)]);

        List<long?> actual = Longs(OpenDrain(op), 1);

        var keySchema = new StructType([new StructField("key", dec, nullable: true)]);
        object?[][] keyVals = [.. unscaled.Select(u => new object?[] { u.HasValue ? (Int128)u.Value : null })];
        List<long> expected = ExpectedOrder(keySchema, keyVals, ids, [ToOrdering(dir, nulls)]);

        Assert.Equal(expected, actual.Select(v => v!.Value).ToList());
    }

    [Theory]
    [MemberData(nameof(Orderings))]
    public void Sort_Timestamp_MatchesComparerOracle(SortDirection dir, NullOrdering nulls)
    {
        var schema = new StructType(
        [
            new StructField("key", DataTypes.TimestampType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        ]);
        long?[] keys = [1_000, null, -500, 0, long.MaxValue, -500];
        long[] ids = [0, 1, 2, 3, 4, 5];
        var op = new SortOperator(
            Scan(schema, Batch(schema, TsCol(keys), LongCol([.. ids.Select(i => (long?)i)]))),
            [new SortOrder(new ColumnReference(0, DataTypes.TimestampType, nullable: true), dir, nulls)]);

        List<long?> actual = Longs(OpenDrain(op), 1);

        var keySchema = new StructType([new StructField("key", DataTypes.TimestampType, nullable: true)]);
        object?[][] keyVals = [.. keys.Select(k => new object?[] { k.HasValue ? k.Value : null })];
        List<long> expected = ExpectedOrder(keySchema, keyVals, ids, [ToOrdering(dir, nulls)]);

        Assert.Equal(expected, actual.Select(v => v!.Value).ToList());
    }

    [Fact]
    public void Sort_MultiKey_PrimaryAscNullsFirst_SecondaryDescNullsLast_MatchesOracle()
    {
        var schema = new StructType(
        [
            new StructField("k1", DataTypes.IntegerType, nullable: true),
            new StructField("k2", DataTypes.StringType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        ]);
        int?[] k1 = [1, 1, null, 2, 1, null];
        string?[] k2 = ["b", "a", "z", null, null, "a"];
        long[] ids = [0, 1, 2, 3, 4, 5];
        var op = new SortOperator(
            Scan(schema, Batch(schema, IntCol(k1), StrCol(k2), LongCol([.. ids.Select(i => (long?)i)]))),
            [
                new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: true), SortDirection.Ascending, NullOrdering.NullsFirst),
                new SortOrder(new ColumnReference(1, DataTypes.StringType, nullable: true), SortDirection.Descending, NullOrdering.NullsLast),
            ]);

        List<long?> actual = Longs(OpenDrain(op), 2);

        var keySchema = new StructType(
        [
            new StructField("k1", DataTypes.IntegerType, nullable: true),
            new StructField("k2", DataTypes.StringType, nullable: true),
        ]);
        object?[][] keyVals = [.. Enumerable.Range(0, ids.Length).Select(i => new object?[] { k1[i].HasValue ? k1[i]!.Value : null, k2[i] })];
        List<long> expected = ExpectedOrder(
            keySchema,
            keyVals,
            ids,
            [
                new SortKeyOrdering(SortKeyDirection.Ascending, NullSortOrder.NullsFirst),
                new SortKeyOrdering(SortKeyDirection.Descending, NullSortOrder.NullsLast),
            ]);

        Assert.Equal(expected, actual.Select(v => v!.Value).ToList());
    }

    [Fact]
    public void Sort_EqualKeys_PreservesInputOrder_StableTieBreak()
    {
        var schema = new StructType(
        [
            new StructField("key", DataTypes.IntegerType, nullable: false),
            new StructField("id", DataTypes.LongType, nullable: false),
        ]);
        var op = new SortOperator(
            Scan(schema, Batch(schema, IntCol(5, 5, 5, 5), LongCol(10, 11, 12, 13))),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);

        List<long?> actual = Longs(OpenDrain(op), 1);

        Assert.Equal(new long?[] { 10, 11, 12, 13 }, actual);
    }

    [Fact]
    public void Sort_PreservesAllRows_AsMultiset()
    {
        var schema = new StructType(
        [
            new StructField("key", DataTypes.IntegerType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        ]);
        int?[] keys = [9, 3, 3, null, 7, 3, null, 1];
        long[] ids = [0, 1, 2, 3, 4, 5, 6, 7];
        var op = new SortOperator(
            Scan(schema, Batch(schema, IntCol(keys), LongCol([.. ids.Select(i => (long?)i)]))),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: true))]);

        List<long?> actual = Longs(OpenDrain(op), 1);

        AssertSameRows(ids.Select(i => (long?)i), actual);
        Assert.Equal(ids.Length, actual.Count);
    }

    [Fact]
    public void Sort_AcrossMultipleInputBatches_MergesIntoTotalOrder()
    {
        var schema = new StructType(
        [
            new StructField("key", DataTypes.IntegerType, nullable: false),
            new StructField("id", DataTypes.LongType, nullable: false),
        ]);
        var op = new SortOperator(
            Scan(
                schema,
                Batch(schema, IntCol(5, 1), LongCol(50, 10)),
                Batch(schema, IntCol(3, 1, 4), LongCol(30, 11, 40))),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);

        List<ColumnBatch> result = OpenDrain(op);

        Assert.Equal(new int?[] { 1, 1, 3, 4, 5 }, Ints(result, 0));
        Assert.Equal(new long?[] { 10, 11, 30, 40, 50 }, Longs(result, 1)); // stable: id 10 before 11 for equal key 1
    }

    [Fact]
    public void Sort_PopulatesMetrics_OutputEqualsInput()
    {
        var schema = new StructType([new StructField("key", DataTypes.IntegerType, nullable: false)]);
        var op = new SortOperator(Scan(schema, Batch(schema, IntCol(3, 1, 2))), [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);

        List<ColumnBatch> _ = OpenDrain(op);
        OperatorMetricsSnapshot m = op.Metrics.Snapshot();

        Assert.Equal(3, m.InputRows);
        Assert.Equal(3, m.OutputRows);
        Assert.True(m.PeakMemoryBytes > 0);
    }

    [Fact]
    public void Sort_OverBudget_ThrowsExecutionMemoryException()
    {
        var schema = new StructType([new StructField("key", DataTypes.LongType, nullable: false)]);
        var op = new SortOperator(Scan(schema, Batch(schema, LongCol(3, 1, 2, 4, 5))), [new SortOrder(new ColumnReference(0, DataTypes.LongType, nullable: false))]);

        using IBatchStream stream = Backend.Open(op, Ctx(new BoundedExecutionMemory(8)));
        Assert.Throws<ExecutionMemoryException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void Sort_IsLazy_NoRowsConsumedBeforeFirstPull()
    {
        var schema = new StructType([new StructField("key", DataTypes.IntegerType, nullable: false)]);
        var op = new SortOperator(Scan(schema, Batch(schema, IntCol(3, 1, 2))), [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);

        using IBatchStream stream = Backend.Open(op, Ctx());
        Assert.Equal(0, op.Metrics.Snapshot().InputRows);
        Assert.True(stream.TryGetNext(out _));
        Assert.Equal(3, op.Metrics.Snapshot().InputRows);
    }

    [Fact]
    public void Sort_CancelledBeforeFirstPull_Throws()
    {
        var schema = new StructType([new StructField("key", DataTypes.IntegerType, nullable: false)]);
        var op = new SortOperator(Scan(schema, Batch(schema, IntCol(3, 1, 2))), [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using IBatchStream stream = Backend.Open(op, Ctx(cancellation: cts.Token));
        Assert.Throws<OperationCanceledException>(() => stream.TryGetNext(out _));
    }

    private static SortKeyOrdering ToOrdering(SortDirection dir, NullOrdering nulls) => new(
        dir == SortDirection.Ascending ? SortKeyDirection.Ascending : SortKeyDirection.Descending,
        nulls == NullOrdering.NullsFirst ? NullSortOrder.NullsFirst : NullSortOrder.NullsLast);

    // ===================================================================================================
    // JOIN
    // ===================================================================================================

    private static readonly StructType LeftSchema = new(
    [
        new StructField("lk", DataTypes.IntegerType, nullable: true),
        new StructField("lv", DataTypes.StringType, nullable: true),
    ]);

    private static readonly StructType RightSchema = new(
    [
        new StructField("rk", DataTypes.IntegerType, nullable: true),
        new StructField("rv", DataTypes.StringType, nullable: true),
    ]);

    private static StructType JoinOut(bool leftOnly) => leftOnly
        ? new StructType([new StructField("lk", DataTypes.IntegerType, nullable: true), new StructField("lv", DataTypes.StringType, nullable: true)])
        : new StructType(
        [
            new StructField("lk", DataTypes.IntegerType, nullable: true),
            new StructField("lv", DataTypes.StringType, nullable: true),
            new StructField("rk", DataTypes.IntegerType, nullable: true),
            new StructField("rv", DataTypes.StringType, nullable: true),
        ]);

    private static JoinOperator Join(JoinType type, ColumnBatch left, ColumnBatch right)
    {
        bool leftOnly = type is JoinType.LeftSemi or JoinType.LeftAnti;
        return new JoinOperator(
            Scan(LeftSchema, left),
            Scan(RightSchema, right),
            JoinOut(leftOnly),
            type,
            [new ColumnReference(0, DataTypes.IntegerType, nullable: true)],
            [new ColumnReference(0, DataTypes.IntegerType, nullable: true)]);
    }

    private static List<(int?, string?, int?, string?)> JoinRows(List<ColumnBatch> b)
    {
        List<int?> lk = Ints(b, 0);
        List<string?> lv = Strs(b, 1);
        List<int?> rk = Ints(b, 2);
        List<string?> rv = Strs(b, 3);
        var rows = new List<(int?, string?, int?, string?)>(lk.Count);
        for (int i = 0; i < lk.Count; i++)
        {
            rows.Add((lk[i], lv[i], rk[i], rv[i]));
        }

        return rows;
    }

    private static List<(int?, string?)> LeftOnlyRows(List<ColumnBatch> b)
    {
        List<int?> lk = Ints(b, 0);
        List<string?> lv = Strs(b, 1);
        var rows = new List<(int?, string?)>(lk.Count);
        for (int i = 0; i < lk.Count; i++)
        {
            rows.Add((lk[i], lv[i]));
        }

        return rows;
    }

    // Shared fixture: left keys [1,2,2,3], right keys [2,2,4], with distinguishable values.
    private static ColumnBatch LeftFixture() => Batch(LeftSchema, IntCol(1, 2, 2, 3), StrCol("a", "b", "c", "d"));

    private static ColumnBatch RightFixture() => Batch(RightSchema, IntCol(2, 2, 4), StrCol("x", "y", "z"));

    [Fact]
    public void InnerJoin_ProducesCartesianProductOfMatchingKeys()
    {
        List<ColumnBatch> result = OpenDrain(Join(JoinType.Inner, LeftFixture(), RightFixture()));

        AssertSameRows(
            new (int?, string?, int?, string?)[]
            {
                (2, "b", 2, "x"), (2, "b", 2, "y"),
                (2, "c", 2, "x"), (2, "c", 2, "y"),
            },
            JoinRows(result));
    }

    [Fact]
    public void LeftOuterJoin_PadsUnmatchedLeftRowsWithNullRight()
    {
        List<ColumnBatch> result = OpenDrain(Join(JoinType.LeftOuter, LeftFixture(), RightFixture()));

        AssertSameRows(
            new (int?, string?, int?, string?)[]
            {
                (1, "a", null, null),
                (2, "b", 2, "x"), (2, "b", 2, "y"),
                (2, "c", 2, "x"), (2, "c", 2, "y"),
                (3, "d", null, null),
            },
            JoinRows(result));
    }

    [Fact]
    public void RightOuterJoin_PadsUnmatchedRightRowsWithNullLeft()
    {
        List<ColumnBatch> result = OpenDrain(Join(JoinType.RightOuter, LeftFixture(), RightFixture()));

        AssertSameRows(
            new (int?, string?, int?, string?)[]
            {
                (2, "b", 2, "x"), (2, "c", 2, "x"),
                (2, "b", 2, "y"), (2, "c", 2, "y"),
                (null, null, 4, "z"),
            },
            JoinRows(result));
    }

    [Fact]
    public void FullOuterJoin_EmitsMatchesAndBothSidesUnmatched()
    {
        List<ColumnBatch> result = OpenDrain(Join(JoinType.FullOuter, LeftFixture(), RightFixture()));

        AssertSameRows(
            new (int?, string?, int?, string?)[]
            {
                (2, "b", 2, "x"), (2, "b", 2, "y"),
                (2, "c", 2, "x"), (2, "c", 2, "y"),
                (1, "a", null, null),
                (3, "d", null, null),
                (null, null, 4, "z"),
            },
            JoinRows(result));
    }

    [Fact]
    public void LeftSemiJoin_EmitsEachMatchingLeftRowOnce_LeftColumnsOnly()
    {
        List<ColumnBatch> result = OpenDrain(Join(JoinType.LeftSemi, LeftFixture(), RightFixture()));

        AssertSameRows(new (int?, string?)[] { (2, "b"), (2, "c") }, LeftOnlyRows(result));
    }

    [Fact]
    public void LeftAntiJoin_EmitsLeftRowsWithNoMatch_LeftColumnsOnly()
    {
        List<ColumnBatch> result = OpenDrain(Join(JoinType.LeftAnti, LeftFixture(), RightFixture()));

        AssertSameRows(new (int?, string?)[] { (1, "a"), (3, "d") }, LeftOnlyRows(result));
    }

    [Fact]
    public void Join_NullKeys_NeverMatch_InnerDropsThem()
    {
        ColumnBatch left = Batch(LeftSchema, IntCol(1, null), StrCol("a", "n"));
        ColumnBatch right = Batch(RightSchema, IntCol(1, null), StrCol("x", "m"));

        List<ColumnBatch> result = OpenDrain(Join(JoinType.Inner, left, right));

        AssertSameRows(new (int?, string?, int?, string?)[] { (1, "a", 1, "x") }, JoinRows(result));
    }

    [Fact]
    public void Join_NullKeys_AreUnmatchedInOuterAndAntiJoins()
    {
        ColumnBatch Left() => Batch(LeftSchema, IntCol(1, null), StrCol("a", "n"));
        ColumnBatch Right() => Batch(RightSchema, IntCol(1, null), StrCol("x", "m"));

        AssertSameRows(
            new (int?, string?, int?, string?)[] { (1, "a", 1, "x"), (null, "n", null, null) },
            JoinRows(OpenDrain(Join(JoinType.LeftOuter, Left(), Right()))));

        AssertSameRows(
            new (int?, string?, int?, string?)[] { (1, "a", 1, "x"), (null, null, null, "m") },
            JoinRows(OpenDrain(Join(JoinType.RightOuter, Left(), Right()))));

        AssertSameRows(
            new (int?, string?)[] { (null, "n") },
            LeftOnlyRows(OpenDrain(Join(JoinType.LeftAnti, Left(), Right()))));
    }

    [Fact]
    public void InnerJoin_ManyMatches_SpansMultipleOutputBatches_AndResumesCorrectly()
    {
        // 40 left rows × 40 right rows on key 1 = 1600 matches > the 1024-row output chunk, forcing the
        // resumable probe state machine to cross at least one batch boundary mid-left-row.
        const int n = 40;
        var lk = new int?[n];
        var lv = new string?[n];
        var rk = new int?[n];
        var rv = new string?[n];
        for (int i = 0; i < n; i++)
        {
            lk[i] = 1;
            lv[i] = $"L{i}";
            rk[i] = 1;
            rv[i] = $"R{i}";
        }

        List<ColumnBatch> result = OpenDrain(Join(JoinType.Inner, Batch(LeftSchema, IntCol(lk), StrCol(lv)), Batch(RightSchema, IntCol(rk), StrCol(rv))));

        Assert.True(result.Count >= 2); // chunked
        Assert.Equal(n * n, RowCount(result));
        List<(int?, string?, int?, string?)> rows = JoinRows(result);
        Assert.All(rows, r => Assert.Equal((1, 1), (r.Item1, r.Item3)));
        // Each left value pairs with all n right values exactly once.
        foreach (IGrouping<string?, (int?, string?, int?, string?)> g in rows.GroupBy(r => r.Item2))
        {
            Assert.Equal(n, g.Count());
            Assert.Equal(n, g.Select(r => r.Item4).Distinct().Count());
        }
    }

    [Fact]
    public void Join_PopulatesMetrics_InputRowsCountBothSides()
    {
        JoinOperator op = Join(JoinType.Inner, LeftFixture(), RightFixture());
        List<ColumnBatch> result = OpenDrain(op);
        OperatorMetricsSnapshot m = op.Metrics.Snapshot();

        Assert.Equal(7, m.InputRows);  // 4 left + 3 right
        Assert.Equal(RowCount(result), m.OutputRows);
        Assert.True(m.PeakMemoryBytes > 0);
    }

    [Fact]
    public void Join_BuildSideOverBudget_ThrowsExecutionMemoryException()
    {
        JoinOperator op = Join(JoinType.Inner, LeftFixture(), RightFixture());
        using IBatchStream stream = Backend.Open(op, Ctx(new BoundedExecutionMemory(8)));
        Assert.Throws<ExecutionMemoryException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void Join_IsLazy_NoRowsConsumedBeforeFirstPull()
    {
        JoinOperator op = Join(JoinType.Inner, LeftFixture(), RightFixture());
        using IBatchStream stream = Backend.Open(op, Ctx());
        Assert.Equal(0, op.Metrics.Snapshot().InputRows);
        Assert.True(stream.TryGetNext(out _));
        Assert.Equal(7, op.Metrics.Snapshot().InputRows);
    }

    [Fact]
    public void Join_CancelledBeforeFirstPull_Throws()
    {
        JoinOperator op = Join(JoinType.Inner, LeftFixture(), RightFixture());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using IBatchStream stream = Backend.Open(op, Ctx(cancellation: cts.Token));
        Assert.Throws<OperationCanceledException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void Join_MismatchedOutputSchema_FailsFastAtOpen()
    {
        // Output declares 3 fields but inner join must be left(2)++right(2)=4.
        var bad = new StructType(
        [
            new StructField("lk", DataTypes.IntegerType, nullable: true),
            new StructField("lv", DataTypes.StringType, nullable: true),
            new StructField("rk", DataTypes.IntegerType, nullable: true),
        ]);
        var op = new JoinOperator(
            Scan(LeftSchema, LeftFixture()),
            Scan(RightSchema, RightFixture()),
            bad,
            JoinType.Inner,
            [new ColumnReference(0, DataTypes.IntegerType, nullable: true)],
            [new ColumnReference(0, DataTypes.IntegerType, nullable: true)]);

        Assert.Throws<ArgumentException>(() => Backend.Open(op, Ctx()));
    }

    [Fact]
    public void Join_InterpretedAndCompiledBackends_ProduceIdenticalResults()
    {
        List<(int?, string?, int?, string?)> Run(IExecutionBackend backend)
        {
            JoinOperator op = Join(JoinType.FullOuter, LeftFixture(), RightFixture());
            using IBatchStream stream = backend.Open(op, Ctx());
            return JoinRows(Drain(stream));
        }

        AssertSameRows(
            Run(ExecutionBackends.Select(new ExecutionBackendOptions { ForceInterpreted = true })),
            Run(ExecutionBackends.Select()));
    }

    // ===================================================================================================
    // EXCHANGE-LOCAL
    // ===================================================================================================

    private static readonly StructType ExSchema = new(
    [
        new StructField("k", DataTypes.IntegerType, nullable: true),
        new StructField("id", DataTypes.LongType, nullable: false),
    ]);

    private static ExchangeLocalOperator Exchange(int partitions, bool keyed, params ColumnBatch[] batches)
        => new(
            Scan(ExSchema, batches),
            partitions,
            keyed ? [new ColumnReference(0, DataTypes.IntegerType, nullable: true)] : null);

    // Replays the operator's own partition formula (RowKeyProjection canonical encoding -> FNV-1a mod N)
    // as an oracle. This is the gold check: a stream that switched to row-index or any other assignment
    // would diverge from this for at least one row.
    private static int ExpectedPartition(int key, int n)
    {
        var projection = new RowKeyProjection(
            [new ColumnReference(0, DataTypes.IntegerType, nullable: true)],
            ExSchema,
            "interpreted-vectorized",
            OperatorKind.ExchangeLocal);
        ColumnBatch batch = Batch(ExSchema, IntCol(key), LongCol(0));
        var memory = new BatchEvaluationMemory(BoundedExecutionMemory.Unbounded);
        try
        {
            ColumnVector[] keyVectors = projection.Evaluate(batch, memory, default);
            byte[] encoded = projection.Encode(keyVectors, 0, out _);
            return (int)(RowKey.Fnv1a(encoded) % (uint)n);
        }
        finally
        {
            memory.Release();
        }
    }

    [Fact]
    public void Exchange_HashPartition_AssignsEachRowToFnvPartition_PreservesAllRows_Spreads()
    {
        const int n = 4;
        int[] keys = [10, 20, 10, 30, 20, 40, 10, 50];
        long[] ids = [0, 1, 2, 3, 4, 5, 6, 7];
        ColumnBatch input = Batch(ExSchema, IntCol([.. keys.Select(k => (int?)k)]), LongCol([.. ids.Select(i => (long?)i)]));

        List<ColumnBatch> result = OpenDrain(Exchange(n, keyed: true, input));

        // Positional contract: exactly N output batches for the one input batch, in partition-id order.
        Assert.Equal(n, result.Count);

        // Every row must land in exactly the FNV-derived partition; collect ids for preservation too.
        var seenIds = new List<long>();
        for (int p = 0; p < result.Count; p++)
        {
            ColumnVector kCol = result[p].SelectedColumn(0);
            ColumnVector idCol = result[p].SelectedColumn(1);
            for (int r = 0; r < idCol.Length; r++)
            {
                seenIds.Add(idCol.GetValue<long>(r));
                Assert.Equal(ExpectedPartition(kCol.GetValue<int>(r), n), p);
            }
        }

        AssertSameRows(ids.Select(i => (long?)i), seenIds.Select(i => (long?)i)); // no loss / no duplication
        Assert.True(keys.Select(k => ExpectedPartition(k, n)).Distinct().Count() >= 2); // hash actually spreads
    }

    [Fact]
    public void Exchange_HashPartition_IsDeterministic_AcrossRuns()
    {
        int?[] keys = [10, 20, 30, 40, 50, 60];
        long[] ids = [0, 1, 2, 3, 4, 5];
        ColumnBatch Input() => Batch(ExSchema, IntCol(keys), LongCol([.. ids.Select(i => (long?)i)]));

        List<long> First() => PartitionOfEachId(OpenDrain(Exchange(4, keyed: true, Input())));

        Assert.Equal(First(), First());
    }

    // Returns a list parallel to id where element = partition index that id landed in.
    private static List<long> PartitionOfEachId(List<ColumnBatch> result)
    {
        var byId = new SortedDictionary<long, int>();
        for (int p = 0; p < result.Count; p++)
        {
            ColumnVector idCol = result[p].SelectedColumn(1);
            for (int r = 0; r < idCol.Length; r++)
            {
                byId[idCol.GetValue<long>(r)] = p;
            }
        }

        return byId.Values.Select(v => (long)v).ToList();
    }

    [Fact]
    public void Exchange_NoKeys_AssignsRoundRobinStartingAtZero()
    {
        long[] ids = [0, 1, 2, 3, 4, 5, 6];
        ColumnBatch input = Batch(ExSchema, IntCol(9, 9, 9, 9, 9, 9, 9), LongCol([.. ids.Select(i => (long?)i)]));

        List<ColumnBatch> result = OpenDrain(Exchange(3, keyed: false, input));

        Assert.Equal(3, result.Count);
        // Round-robin from 0: id 0->p0, 1->p1, 2->p2, 3->p0, 4->p1, 5->p2, 6->p0.
        Assert.Equal(new long?[] { 0, 3, 6 }, Longs([result[0]], 1));
        Assert.Equal(new long?[] { 1, 4 }, Longs([result[1]], 1));
        Assert.Equal(new long?[] { 2, 5 }, Longs([result[2]], 1));
    }

    [Fact]
    public void Exchange_NullKeyRows_AreRoutedNotDropped()
    {
        int?[] keys = [null, 7, null, 7];
        long[] ids = [0, 1, 2, 3];
        ColumnBatch input = Batch(ExSchema, IntCol(keys), LongCol([.. ids.Select(i => (long?)i)]));

        List<ColumnBatch> result = OpenDrain(Exchange(3, keyed: true, input));

        AssertSameRows(ids.Select(i => (long?)i), Longs(result, 1)); // all rows survive, none dropped
        Assert.Equal(ids.Length, RowCount(result));
    }

    [Fact]
    public void Exchange_EachInputBatch_EmitsExactlyPartitionCountBatches()
    {
        ColumnBatch b1 = Batch(ExSchema, IntCol(1, 2), LongCol(0, 1));
        ColumnBatch b2 = Batch(ExSchema, IntCol(3, 4, 5), LongCol(2, 3, 4));

        List<ColumnBatch> result = OpenDrain(Exchange(3, keyed: true, b1, b2));

        Assert.Equal(6, result.Count); // 2 input batches × 3 partitions, including empties
        Assert.Equal(5, RowCount(result));
    }

    [Fact]
    public void Exchange_RecordsShuffleBytes_AndMetrics()
    {
        ColumnBatch input = Batch(ExSchema, IntCol(1, 2, 3, 4), LongCol(0, 1, 2, 3));
        ExchangeLocalOperator op = Exchange(2, keyed: true, input);

        List<ColumnBatch> result = OpenDrain(op);
        OperatorMetricsSnapshot m = op.Metrics.Snapshot();

        Assert.Equal(4, m.InputRows);
        Assert.True(m.ShuffleBytes > 0);
        Assert.True(m.PeakMemoryBytes > 0);
        Assert.Equal(4, RowCount(result));
    }

    [Fact]
    public void Exchange_IsLazy_NoRowsConsumedBeforeFirstPull()
    {
        ExchangeLocalOperator op = Exchange(2, keyed: true, Batch(ExSchema, IntCol(1, 2), LongCol(0, 1)));
        using IBatchStream stream = Backend.Open(op, Ctx());
        Assert.Equal(0, op.Metrics.Snapshot().InputRows);
        Assert.True(stream.TryGetNext(out _));
        Assert.True(op.Metrics.Snapshot().InputRows > 0);
    }

    [Fact]
    public void Exchange_CancelledBeforeFirstPull_Throws()
    {
        ExchangeLocalOperator op = Exchange(2, keyed: true, Batch(ExSchema, IntCol(1, 2), LongCol(0, 1)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using IBatchStream stream = Backend.Open(op, Ctx(cancellation: cts.Token));
        Assert.Throws<OperationCanceledException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void Exchange_InterpretedAndCompiledBackends_ProduceIdenticalAssignment()
    {
        int?[] keys = [11, 22, 33, 44, 55, 66];
        long[] ids = [0, 1, 2, 3, 4, 5];
        ColumnBatch Input() => Batch(ExSchema, IntCol(keys), LongCol([.. ids.Select(i => (long?)i)]));

        List<long> Run(IExecutionBackend backend)
        {
            ExchangeLocalOperator op = new(Scan(ExSchema, Input()), 4, [new ColumnReference(0, DataTypes.IntegerType, nullable: true)]);
            using IBatchStream stream = backend.Open(op, Ctx());
            return PartitionOfEachId(Drain(stream));
        }

        Assert.Equal(
            Run(ExecutionBackends.Select(new ExecutionBackendOptions { ForceInterpreted = true })),
            Run(ExecutionBackends.Select()));
    }

    // ===================================================================================================
    // PRESELECTED (SELECTION-VECTOR) INPUTS  — B1
    //
    // Each operator is driven by an input batch carrying a NON-IDENTITY, UNORDERED SelectionVector whose
    // UNSELECTED physical rows hold poison (an extreme / a null) that would change the result if wrongly
    // included. The oracle compares against the result over the LOGICALLY-SELECTED rows only, so reading
    // the raw Column(c) instead of the selection-aware SelectedColumn(c) is caught.
    // ===================================================================================================

    // Builds a batch over full physical columns, then carries an unordered selection over it. InMemoryScan
    // preserves the selection, so the operator sees only the logical (selected) rows.
    private static ColumnBatch Preselected(StructType schema, int[] selection, params ColumnVector[] columns)
        => Batch(schema, columns).WithSelection(new SelectionVector(selection));

    [Fact]
    public void Sort_PreselectedUnorderedInput_SortsLogicalRowsOnly()
    {
        var schema = new StructType(
        [
            new StructField("key", DataTypes.IntegerType, nullable: true),
            new StructField("id", DataTypes.LongType, nullable: false),
        ]);

        // Physical keys [100, 30, 10, null, 20]; unselected rows 0 (extreme 100) and 3 (null) are poison.
        // Selection [4,1,2] (unordered) -> logical rows (20,id4),(30,id1),(10,id2).
        ColumnBatch input = Preselected(schema, [4, 1, 2], IntCol(100, 30, 10, null, 20), LongCol(0, 1, 2, 3, 4));
        var op = new SortOperator(
            Scan(schema, input),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: true), SortDirection.Ascending, NullOrdering.NullsFirst)]);

        List<long?> ids = Longs(OpenDrain(op), 1);

        // Sorted ascending by the selected keys [20,30,10] -> [10,20,30] -> ids [2,4,1].
        Assert.Equal(new long?[] { 2, 4, 1 }, ids);
    }

    [Fact]
    public void InnerJoin_PreselectedBothSides_JoinsLogicalRowsOnly()
    {
        // Left physical lk [2,3,2,1]; unselected rows 0 ("Q0") and 1 ("Q1") are poison. Selection [3,2]
        // (unordered) -> logical (1,"a"),(2,"b"). Probe-side mutation would emit "Q1"/lk 3 instead.
        ColumnBatch left = Preselected(LeftSchema, [3, 2], IntCol(2, 3, 2, 1), StrCol("Q0", "Q1", "b", "a"));

        // Right physical rk [9,9,2,2,4]; unselected rows 0 ("P0") and 1 ("P1") are poison. Selection
        // [4,2,3] (unordered) -> logical (4,"z"),(2,"x"),(2,"y"). Build-side mutation would emit "P1".
        ColumnBatch right = Preselected(RightSchema, [4, 2, 3], IntCol(9, 9, 2, 2, 4), StrCol("P0", "P1", "x", "y", "z"));

        List<ColumnBatch> result = OpenDrain(Join(JoinType.Inner, left, right));

        AssertSameRows(
            new (int?, string?, int?, string?)[] { (2, "b", 2, "x"), (2, "b", 2, "y") },
            JoinRows(result));
    }

    [Fact]
    public void GroupedAggregate_PreselectedUnorderedInput_AggregatesLogicalRowsOnly()
    {
        var schema = new StructType(
        [
            new StructField("k", DataTypes.StringType, nullable: false),
            new StructField("v", DataTypes.IntegerType, nullable: true),
        ]);

        // Unselected rows 0 ("a",1000) and 3 ("z",-9999) are poison: they would inflate group "a" and
        // add a phantom "z" group. Selection [4,2,1] (unordered) -> logical ("a",3),("b",2),("a",1).
        ColumnBatch input = Preselected(
            schema, [4, 2, 1], StrCol("a", "a", "b", "z", "a"), IntCol(1000, 1, 2, -9999, 3));
        AggregateOperator agg = Agg(
            Scan(schema, input),
            keys: [new ColumnReference(0, DataTypes.StringType, nullable: false)],
            aggs: [Of(AggregateFunction.Sum, 1, DataTypes.IntegerType)]);

        List<ColumnBatch> result = OpenDrain(agg);

        AssertSameRows(new (string?, long?)[] { ("a", 4L), ("b", 2L) }, Zip2(Strs(result, 0), Longs(result, 1)));
        Assert.Equal(2, RowCount(result));
    }

    [Fact]
    public void Exchange_PreselectedInput_RoutesLogicalRowsOnly()
    {
        const int n = 4;

        // Unselected rows 0 (id 100) and 4 (id 200) are poison ids that must not leak into any partition.
        // Selection [5,1,2,3] (unordered) -> logical keys [40,10,20,30], ids [4,1,2,3].
        ColumnBatch input = Preselected(
            ExSchema, [5, 1, 2, 3], IntCol(999, 10, 20, 30, 999, 40), LongCol(100, 1, 2, 3, 200, 4));

        List<ColumnBatch> result = OpenDrain(Exchange(n, keyed: true, input));

        AssertSameRows(new long?[] { 1, 2, 3, 4 }, Longs(result, 1)); // only selected rows survive
        for (int p = 0; p < result.Count; p++)
        {
            ColumnVector kCol = result[p].SelectedColumn(0);
            for (int r = 0; r < kCol.Length; r++)
            {
                Assert.Equal(ExpectedPartition(kCol.GetValue<int>(r), n), p);
            }
        }
    }

    // ===================================================================================================
    // VARIABLE-WIDTH MEMORY BUDGET  — B2 (P3 / P3b / MIN-MAX) and no-leak lock-in (N5)
    // ===================================================================================================

    [Fact]
    public void Sort_VariableWidthPayload_OverBudget_ThrowsExecutionMemoryException() // P3
    {
        var schema = new StructType(
        [
            new StructField("ord", DataTypes.IntegerType, nullable: false),     // small sort key
            new StructField("payload", DataTypes.StringType, nullable: false),  // wide buffered value
        ]);

        string big = new string('x', 50_000);
        var ords = new int?[50];
        var payloads = new string?[50];
        for (int i = 0; i < 50; i++)
        {
            ords[i] = i;
            payloads[i] = big;
        }

        // The flat estimate would charge ~25 bytes/row (well under 8 KB for 50 rows); only charging the
        // TRUE 50 KB payload makes the first buffered row exceed the budget.
        var op = new SortOperator(
            Scan(schema, Batch(schema, IntCol(ords), StrCol(payloads))),
            [new SortOrder(new ColumnReference(0, DataTypes.IntegerType, nullable: false))]);

        using IBatchStream stream = Backend.Open(op, Ctx(new BoundedExecutionMemory(8192)));
        Assert.Throws<ExecutionMemoryException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void Join_VariableWidthBuildPayload_OverBudget_ThrowsExecutionMemoryException() // P3b
    {
        string big = new string('y', 50_000);
        var rk = new int?[50];
        var rv = new string?[50];
        for (int i = 0; i < 50; i++)
        {
            rk[i] = 1;
            rv[i] = big;
        }

        ColumnBatch left = Batch(LeftSchema, IntCol(1), StrCol("a"));
        ColumnBatch right = Batch(RightSchema, IntCol(rk), StrCol(rv));
        JoinOperator op = Join(JoinType.Inner, left, right);

        using IBatchStream stream = Backend.Open(op, Ctx(new BoundedExecutionMemory(8192)));
        Assert.Throws<ExecutionMemoryException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void GlobalMinMax_LargeStrings_OverBudget_ThrowsExecutionMemoryException()
    {
        var schema = new StructType([new StructField("v", DataTypes.StringType, nullable: true)]);
        string a = new string('a', 50_000);
        string b = new string('b', 50_000);

        // MIN retains its running-best string; charging its true length makes the first retained value
        // exceed the tiny budget (the flat 32-byte per-group estimate could not).
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, StrCol(a, b))),
            keys: [],
            aggs: [Of(AggregateFunction.Min, 0, DataTypes.StringType)]);

        using IBatchStream stream = Backend.Open(agg, Ctx(new BoundedExecutionMemory(4096)));
        Assert.Throws<ExecutionMemoryException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void InnerJoin_MultiChunk_UnderBuildPlusTwoChunkBudget_Succeeds_NoLeak() // N5
    {
        // 40×40 = 1600 matches on key 1 -> two output chunks (1024 + 576). The budget covers the build
        // table plus ~1.5 output chunks: it suffices ONLY because each emitted chunk's output reservation
        // is released on the next pull. Were it leaked, draining the second chunk would need two full
        // chunks and over-run the budget.
        const int n = 40;
        var lk = new int?[n];
        var lv = new string?[n];
        var rk = new int?[n];
        var rv = new string?[n];
        for (int i = 0; i < n; i++)
        {
            lk[i] = 1;
            lv[i] = $"L{i}";
            rk[i] = 1;
            rv[i] = $"R{i}";
        }

        JoinOperator op = Join(
            JoinType.Inner, Batch(LeftSchema, IntCol(lk), StrCol(lv)), Batch(RightSchema, IntCol(rk), StrCol(rv)));

        List<ColumnBatch> result = OpenDrain(op, Ctx(new BoundedExecutionMemory(52_000)));

        Assert.True(result.Count >= 2);          // chunked across at least two output batches
        Assert.Equal(n * n, RowCount(result));   // every match emitted, none dropped, budget never refused
    }

    // ===================================================================================================
    // DECIMAL SUM OVERFLOW AT THE AGGREGATE LEVEL  — B4
    // ===================================================================================================

    // Builds a (non-compact) decimal column from Int128 unscaled magnitudes — needed to drive the
    // decimal SUM overflow branch, which the long-based DecCol helper cannot reach.
    private static MutableColumnVector BigDecCol(int precision, int scale, params Int128[] unscaled)
    {
        MutableColumnVector v = ColumnVectors.Create(new DecimalType(precision, scale), Math.Max(unscaled.Length, 1));
        foreach (Int128 x in unscaled)
        {
            v.AppendValue(x);
        }

        return v;
    }

    [Fact]
    public void GlobalSum_Decimal_AnsiOverflow_Throws()
    {
        // 9e37 + 9e37 = 1.8e38: the FINAL sum is out of range for decimal(38,0) (#156 B2 defers the fit
        // check to emit, where the unchecked Int128 mantissa fails ToType). Order-independent — still throws.
        var schema = new StructType([new StructField("v", new DecimalType(38, 0), nullable: true)]);
        Int128 big = Int128.Parse("90000000000000000000000000000000000000");
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, BigDecCol(38, 0, big, big))),
            keys: [],
            aggs: [Of(AggregateFunction.Sum, 0, new DecimalType(38, 0), AnsiMode.Ansi)]);

        using IBatchStream stream = Backend.Open(agg, Ctx());
        Assert.Throws<ArithmeticOverflowException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void GlobalSum_Decimal_LegacyOverflow_NullsTheGroup()
    {
        var schema = new StructType([new StructField("v", new DecimalType(38, 0), nullable: true)]);
        Int128 big = Int128.Parse("90000000000000000000000000000000000000");
        AggregateOperator agg = Agg(
            Scan(schema, Batch(schema, BigDecCol(38, 0, big, big))),
            keys: [],
            aggs: [Of(AggregateFunction.Sum, 0, new DecimalType(38, 0), AnsiMode.Legacy)]);

        List<ColumnBatch> result = OpenDrain(agg);

        Assert.Equal(1, RowCount(result));
        Assert.True(result[0].SelectedColumn(0).IsNull(0)); // overflow nulled the group
    }
}
