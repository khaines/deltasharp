using System;
using System.Collections.Generic;
using System.Threading;
using DeltaSharp;
using DeltaSharp.Core.Tests.Actions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Typed;

/// <summary>
/// STORY-04.7.2 (#178): the typed <see cref="Dataset{T}.Collect()"/> action, exercised end-to-end
/// through the same internal <c>IQueryExecutor</c> seam <see cref="DataFrame.Collect()"/> uses (a
/// <see cref="FakeQueryExecutor"/> returning canned rows), proving that a typed collect runs the plan
/// once and decodes each <see cref="Row"/> into a <c>T</c>. Session tests are serialized because they
/// touch process-wide active/default session state.
/// </summary>
[Collection(SparkSessionTestCollection.Name)]
public sealed class DatasetEncoderCollectTests
{
    private static (SparkSession Spark, DataFrame Df) NewBoundFrame(FakeQueryExecutor executor, StructType schema)
    {
        SparkSession spark = SparkSession.Builder().AppName("encoders").GetOrCreate();
        spark.QueryExecutor = executor;
        spark.Catalog.Register("people", schema);
        var df = new DataFrame(spark, new UnresolvedRelation(new[] { "people" }));
        return (spark, df);
    }

    [Fact]
    public void TypedCollect_RunsPlanThroughExecutor_AndDecodesRowsToT()
    {
        StructType schema = DatasetSchema.Derive<PersonPoco>();
        var rows = new List<Row>
        {
            new(schema, "Alice", 30),
            new(schema, "Bob", 25),
        };
        var executor = new FakeQueryExecutor(rows);
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor, schema);
        using (spark)
        {
            IReadOnlyList<PersonPoco> people = df.As<PersonPoco>().Collect();

            Assert.Equal(2, people.Count);
            Assert.Equal("Alice", people[0].Name);
            Assert.Equal(30, people[0].Age);
            Assert.Equal("Bob", people[1].Name);
            Assert.Equal(25, people[1].Age);

            // Executed exactly once, via the SAME executor seam the untyped DataFrame.Collect uses.
            Assert.Equal(1, executor.CollectCallCount);
        }
    }

    [Fact]
    public void TypedCollect_EmptyResult_ReturnsEmptyList()
    {
        StructType schema = DatasetSchema.Derive<PersonPoco>();
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor, schema);
        using (spark)
        {
            IReadOnlyList<PersonPoco> people = df.As<PersonPoco>().Collect();

            Assert.Empty(people);
            Assert.Equal(1, executor.CollectCallCount);
        }
    }

    [Fact]
    public void TypedCollect_OnUnsupportedT_FailsFast_BeforeExecuting()
    {
        // A positional record's SCHEMA derives fine (so As<T> succeeds), but it cannot be DECODED in M1
        // (no parameterless ctor). The decoder is built/validated before the plan runs, so the executor
        // is never driven.
        StructType schema = DatasetSchema.Derive<PositionalBean>();
        var executor = new FakeQueryExecutor(new List<Row> { new(schema, "x", 1) });
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor, schema);
        using (spark)
        {
            Dataset<PositionalBean> ds = df.As<PositionalBean>();

            Assert.Throws<UnsupportedTypedSchemaException>(() => ds.Collect());
            Assert.Equal(0, executor.CollectCallCount);
        }
    }

    [Fact]
    public void TypedCollect_WithCancelledToken_ThrowsOperationCanceled()
    {
        StructType schema = DatasetSchema.Derive<PersonPoco>();
        var executor = new FakeQueryExecutor(new List<Row> { new(schema, "Alice", 30) });
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor, schema);
        using (spark)
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => df.As<PersonPoco>().Collect(cts.Token));
        }
    }
}
