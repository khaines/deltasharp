using System;
using System.Collections.Generic;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// End-to-end DataFrame-API coverage for nested (#608) columns through the public read door: a struct
/// column created via <c>CreateDataFrame</c> now projects and materializes back to a nested <see cref="Row"/>
/// via <c>Collect</c> (encode + decode symmetric). It also pins the current graceful limitation: an operator
/// that would have to GATHER a nested column (a filter's selection over a batch carrying a struct) surfaces a
/// deterministic <see cref="QueryExecutionException"/> citing the nested-Select follow-up, never a raw crash.
/// </summary>
[Collection(SessionExecutionTestCollection.Name)]
public sealed class NestedColumnDataFrameEndToEndTests
{
    private static readonly StructType Inner = new(new[] { new StructField("f", IntegerType.Instance, nullable: false) });

    private static readonly StructType Schema = new(new[]
    {
        new StructField("s", Inner, nullable: true),
        new StructField("k", IntegerType.Instance, nullable: false),
    });

    private static SparkSession NewSession()
    {
        DeltaSharpExecutor.Enable();
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
        return SparkSession.Builder().AppName("nested-e2e").GetOrCreate();
    }

    private static IReadOnlyList<Row> Rows() => new[]
    {
        new Row(Schema, new object?[] { new Row(Inner, 1), 10 }),
        new Row(Schema, new object?[] { null, 20 }),
    };

    [Fact]
    public void SelectStructColumn_Collect_ReturnsNestedRow()
    {
        using SparkSession spark = NewSession();

        IReadOnlyList<Row> got = spark.CreateDataFrame(Rows(), Schema).Select(Functions.Col("s")).Collect();

        Assert.Equal(2, got.Count);
        Assert.Equal(1, Assert.IsType<Row>(got[0][0]).GetAs<int>("f")); // row 0: struct{f:1}
        Assert.True(got[1].IsNullAt(0));                                // row 1: null struct
    }

    [Fact]
    public void FilterCarryingStructColumn_SurfacesDeterministicNestedGatherError()
    {
        // A filter builds a selection over the batch; materializing the carried struct column would require a
        // nested row-gather (Select), which the nested-representation increment defers. It must surface a
        // deterministic QueryExecutionException (not a raw NotSupportedException / process crash).
        using SparkSession spark = NewSession();

        QueryExecutionException ex = Assert.Throws<QueryExecutionException>(
            () => spark.CreateDataFrame(Rows(), Schema).Filter(Functions.Col("k").Gt(15)).Collect());
        Assert.Contains("Select (row gather)", ex.Message, StringComparison.Ordinal);
    }
}
