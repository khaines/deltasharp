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
}
