using System;
using System.Collections.Generic;
using System.Linq;
using DeltaSharp.Types;
using Xunit;
using static DeltaSharp.Functions;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// STORY-04.1.2 (#158) end-to-end tests for the read door: they exercise the PUBLIC Core API
/// (<see cref="SparkSession.CreateDataFrame(IEnumerable{Row}, StructType)"/> and
/// <see cref="SparkSession.Read"/>) through the module-initializer-registered
/// <see cref="LocalQueryExecutor"/>, proving AC1's in-memory source materializes correct rows on an
/// action (and stays lazy until then) and AC2's Parquet scan defers to a deterministic EPIC-05
/// diagnostic. Core-only tests cannot execute, so the materialization proof lives here.
/// </summary>
[Collection(SessionExecutionTestCollection.Name)]
public class ReadDoorEndToEndTests
{
    private static readonly StructType AllTypesSchema = new(new[]
    {
        new StructField("b", BooleanType.Instance, nullable: true),
        new StructField("tb", ByteType.Instance, nullable: true),
        new StructField("sh", ShortType.Instance, nullable: true),
        new StructField("i", IntegerType.Instance, nullable: true),
        new StructField("l", LongType.Instance, nullable: true),
        new StructField("f", FloatType.Instance, nullable: true),
        new StructField("d", DoubleType.Instance, nullable: true),
        new StructField("s", StringType.Instance, nullable: true),
        new StructField("bin", BinaryType.Instance, nullable: true),
        new StructField("dt", DateType.Instance, nullable: true),
        new StructField("ts", TimestampType.Instance, nullable: true),
        new StructField("dec", new DecimalType(10, 2), nullable: true),
    });

    private static readonly DateOnly SampleDate = new(2021, 6, 1);
    private static readonly DateTime SampleTimestamp = new(2021, 6, 1, 12, 30, 15, DateTimeKind.Utc);

    private static SparkSession NewSession()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
        return SparkSession.Builder().AppName("read-door-e2e").GetOrCreate();
    }

    [Fact]
    public void CreateDataFrame_Collect_RoundTripsEveryAtomicTypeAndNulls()
    {
        using SparkSession spark = NewSession();
        var rows = new[]
        {
            new Row(
                AllTypesSchema,
                true, (sbyte)1, (short)2, 3, 4L, 1.5f, 2.5d, "hi",
                new byte[] { 1, 2, 3 }, SampleDate, SampleTimestamp, 12.34m),
            new Row(AllTypesSchema, null, null, null, null, null, null, null, null, null, null, null, null),
        };

        DataFrame df = spark.CreateDataFrame(rows, AllTypesSchema);
        IReadOnlyList<Row> collected = df.Collect();

        Assert.Equal(2, collected.Count);
        Row first = collected[0];
        Assert.True(first.GetAs<bool>("b"));
        Assert.Equal((sbyte)1, first.GetAs<sbyte>("tb"));
        Assert.Equal((short)2, first.GetAs<short>("sh"));
        Assert.Equal(3, first.GetAs<int>("i"));
        Assert.Equal(4L, first.GetAs<long>("l"));
        Assert.Equal(1.5f, first.GetAs<float>("f"));
        Assert.Equal(2.5d, first.GetAs<double>("d"));
        Assert.Equal("hi", first.GetAs<string>("s"));
        Assert.Equal(new byte[] { 1, 2, 3 }, first.GetAs<byte[]>("bin"));
        Assert.Equal(SampleDate, first.GetAs<DateOnly>("dt"));
        Assert.Equal(SampleTimestamp, first.GetAs<DateTime>("ts"));
        Assert.Equal(12.34m, first.GetAs<decimal>("dec"));

        Row second = collected[1];
        for (int c = 0; c < AllTypesSchema.Count; c++)
        {
            Assert.True(second.IsNullAt(c));
        }
    }

    [Fact]
    public void CreateDataFrame_PreservesDecimalScale_OnRoundTrip()
    {
        using SparkSession spark = NewSession();
        var schema = new StructType(new[] { new StructField("amount", new DecimalType(5, 2), nullable: false) });
        var rows = new[] { new Row(schema, 100.00m) };

        Row row = Assert.Single(spark.CreateDataFrame(rows, schema).Collect());

        // Scale is preserved: 100.00m keeps two fractional digits (not normalized to 100m).
        Assert.Equal(2, decimal.GetBits(row.GetAs<decimal>("amount"))[3] >> 16 & 0xFF);
        Assert.Equal(100.00m, row.GetAs<decimal>("amount"));
    }

    [Fact]
    public void CreateDataFrame_Count_ReturnsRowCount()
    {
        using SparkSession spark = NewSession();
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        var rows = new[] { new Row(schema, 1), new Row(schema, 2), new Row(schema, 3) };

        Assert.Equal(3L, spark.CreateDataFrame(rows, schema).Count());
    }

    [Fact]
    public void CreateDataFrame_Transformations_ExecuteOverLocalRelation()
    {
        using SparkSession spark = NewSession();
        var schema = new StructType(new[]
        {
            new StructField("id", IntegerType.Instance, nullable: false),
            new StructField("salary", DoubleType.Instance, nullable: true),
        });
        var rows = new[]
        {
            new Row(schema, 1, 100.0d),
            new Row(schema, 2, 250.0d),
            new Row(schema, 3, 50.0d),
        };

        IReadOnlyList<Row> result = spark.CreateDataFrame(rows, schema)
            .Filter(Col("salary").Gt(75.0))
            .Select(Col("id"))
            .Collect();

        Assert.Equal(new[] { 1, 2 }, result.Select(r => r.GetAs<int>(0)));
    }

    [Fact]
    public void CreateDataFrame_EmptySequence_CollectsNoRows_AndCountsZero()
    {
        using SparkSession spark = NewSession();
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });

        DataFrame df = spark.CreateDataFrame(Array.Empty<Row>(), schema);

        Assert.Empty(df.Collect());
        Assert.Equal(0L, df.Count());
    }

    [Fact]
    public void CreateDataFrame_IsLazy_UntilAnAction()
    {
        using SparkSession spark = NewSession();
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        var source = new CountingRowSequence(new[] { new Row(schema, 1), new Row(schema, 2) });

        DataFrame df = spark.CreateDataFrame(source, schema).Select(Col("id"));
        Assert.Equal(0, source.EnumerationCount); // no action yet → never enumerated

        _ = df.Collect();
        Assert.Equal(1, source.EnumerationCount); // the action enumerated the source exactly once
    }

    [Fact]
    public void ReadParquet_Action_FailsWithDeterministicEpic05Diagnostic()
    {
        using SparkSession spark = NewSession();

        Exception? ex = Record.Exception(() => spark.Read.Parquet("/data/does-not-exist.parquet").Collect());

        Assert.NotNull(ex);
        Assert.Contains("EPIC-05", ex!.Message);
        Assert.Contains("parquet", ex.Message);
    }

    // ---------------- M1: stable, replayable snapshot (Count == Collect, no divergence) ----------------

    [Fact]
    public void CreateDataFrame_MultiAction_CountAndCollectAgree_OnTheSameFrame()
    {
        using SparkSession spark = NewSession();
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        DataFrame df = spark.CreateDataFrame(new[] { new Row(schema, 1), new Row(schema, 2) }, schema);

        long count = df.Count();
        IReadOnlyList<Row> collected = df.Collect();

        // The memoizing snapshot makes every action see identical rows: Count and Collect never diverge.
        Assert.Equal(collected.Count, count);
        Assert.Equal(2, collected.Count);
    }

    [Fact]
    public void CreateDataFrame_SingleUseIterator_ReplaysAcrossTwoActions()
    {
        using SparkSession spark = NewSession();
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        var source = new SingleUseRowSequence(new[] { new Row(schema, 1), new Row(schema, 2), new Row(schema, 3) });

        DataFrame df = spark.CreateDataFrame(source, schema);

        // First action snapshots; a second action replays the snapshot rather than re-enumerating the
        // (now-exhausted) single-use source. Without memoization the second action would see zero rows.
        Assert.Equal(3L, df.Count());
        Assert.Equal(3, df.Collect().Count);
    }

    [Fact]
    public void CreateDataFrame_MutationAfterFirstAction_DoesNotChangeResults()
    {
        using SparkSession spark = NewSession();
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        var source = new List<Row> { new(schema, 1), new(schema, 2) };

        DataFrame df = spark.CreateDataFrame(source, schema);
        long firstCount = df.Count();      // snapshots the two rows
        source.Add(new Row(schema, 3));    // mutate the source AFTER the first action

        // The snapshot is stable: the mutation is invisible to every later action on the same frame.
        Assert.Equal(2L, firstCount);
        Assert.Equal(2L, df.Count());
        Assert.Equal(2, df.Collect().Count);
    }

    // ---------------- M2: a Core-only action path runs (bootstrap-enabled) ----------------

    [Fact]
    public void CreateDataFrame_Count_RunsThroughCoreOnlyPublicApi()
    {
        // Reaches an action through ONLY DeltaSharp.Core public types. This passes in isolation
        // (e.g. --filter ~ReadDoorEndToEnd) because the collection's ExecutorBootstrapFixture called
        // DeltaSharpExecutor.Enable() — proving the M2 bootstrap makes the read door usable end-to-end
        // without first touching an Executor type to trip the module initializer.
        using SparkSession spark = NewSession();
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });

        long count = spark.CreateDataFrame(new[] { new Row(schema, 1), new Row(schema, 2) }, schema).Count();

        Assert.Equal(2L, count);
    }

    // ---------------- M3: planning a LocalRelation does not enumerate the source ----------------

    [Fact]
    public void PlanningLocalRelation_DoesNotEnumerateTheSource()
    {
        using SparkSession spark = NewSession();
        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance, nullable: false) });
        var source = new CountingRowSequence(new[] { new Row(schema, 1), new Row(schema, 2) });
        DataFrame df = spark.CreateDataFrame(source, schema);

        // PhysicalPlanner.Plan is the path #179 ExplainPhysical runs; it must build the physical tree
        // WITHOUT enumerating (or doing IO on) the user source — the row→batch encoding is deferred to
        // ScanPlan.Execute (M3). If Plan() enumerated, an IO-backed source would do IO at explain time.
        var fixture = new InMemoryRelationFixture();
        PhysicalPlan physical = fixture.Plan(df);

        Assert.NotNull(physical);
        Assert.Equal(0, source.EnumerationCount);
    }

    /// <summary>An <see cref="IEnumerable{Row}"/> that counts how many times it is enumerated, so a test
    /// can prove CreateDataFrame is lazy (zero until an action) and materialization enumerates once.</summary>
    private sealed class CountingRowSequence : IEnumerable<Row>
    {
        private readonly IReadOnlyList<Row> _rows;

        public CountingRowSequence(IReadOnlyList<Row> rows) => _rows = rows;

        public int EnumerationCount { get; private set; }

        public IEnumerator<Row> GetEnumerator()
        {
            EnumerationCount++;
            return _rows.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>An <see cref="IEnumerable{Row}"/> that yields its rows only on the FIRST enumeration and
    /// an empty sequence thereafter — proving the memoizing snapshot replays from cache rather than
    /// re-enumerating a single-use source (M1).</summary>
    private sealed class SingleUseRowSequence : IEnumerable<Row>
    {
        private readonly IReadOnlyList<Row> _rows;
        private bool _consumed;

        public SingleUseRowSequence(IReadOnlyList<Row> rows) => _rows = rows;

        public IEnumerator<Row> GetEnumerator()
        {
            if (_consumed)
            {
                return System.Linq.Enumerable.Empty<Row>().GetEnumerator();
            }

            _consumed = true;
            return _rows.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
