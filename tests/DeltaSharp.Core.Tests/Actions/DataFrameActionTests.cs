using System;
using System.Collections.Generic;
using DeltaSharp.Core.Tests.LazyEager;
using DeltaSharp.Diagnostics;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Actions;

/// <summary>
/// STORY-04.6.1 (#173): the <c>Collect</c>/<c>Count</c>/<c>Show</c> actions, exercised against a
/// <see cref="FakeQueryExecutor"/> (canned rows) through the internal <c>IQueryExecutor</c>
/// dependency-inversion seam, plus the lazy/eager invariant proven with the #169 audit seam and the
/// default-unsupported-backend diagnostic. Session tests are serialized because they touch the
/// process-wide active/default session state.
/// </summary>
[Collection(SparkSessionTestCollection.Name)]
public sealed class DataFrameActionTests
{
    private static StructType PeopleSchema() => new(new[]
    {
        new StructField("name", DataTypes.StringType),
        new StructField("age", DataTypes.IntegerType),
    });

    private static Row Person(string? name, int? age) =>
        new(PeopleSchema(), name, age);

    /// <summary>Creates a stopped-on-dispose session with the fake executor installed and the
    /// <c>people</c> relation registered, and a DataFrame bound to it over an unresolved scan.</summary>
    private static (SparkSession Spark, DataFrame Df) NewBoundFrame(FakeQueryExecutor executor)
    {
        SparkSession spark = SparkSession.Builder().AppName("actions").GetOrCreate();
        spark.QueryExecutor = executor;
        spark.Catalog.Register("people", PeopleSchema());
        var df = new DataFrame(spark, new UnresolvedRelation(new[] { "people" }));
        return (spark, df);
    }

    // ----- Collect (#173 AC1) -----

    [Fact]
    public void Collect_ReturnsExecutorRows_AndInvokesBackendOnce()
    {
        var rows = new List<Row> { Person("Alice", 30), Person("Bob", 25) };
        var executor = new FakeQueryExecutor(rows);
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            IReadOnlyList<Row> collected = df.Collect();

            Assert.Equal(2, collected.Count);
            Assert.Equal("Alice", collected[0]["name"]);
            Assert.Equal(25, collected[1]["age"]);
            Assert.Equal(1, executor.CollectCallCount);
            Assert.Equal(0, executor.CountCallCount);
        }
    }

    [Fact]
    public void Collect_PassesAnAnalyzedPlanToTheExecutor()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            _ = df.Collect();
            Assert.NotNull(executor.LastPlan);
            // The plan handed to the backend is analyzer-resolved (the scan was unresolved on input).
            Assert.True(executor.LastPlan!.Resolved);
        }
    }

    // ----- Count (#173 AC2) -----

    [Fact]
    public void Count_ReturnsExecutorCount_WithoutMaterializing()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>(), countOverride: 7);
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            long count = df.Count();

            Assert.Equal(7, count);
            Assert.Equal(1, executor.CountCallCount);
            Assert.Equal(0, executor.CollectCallCount);
        }
    }

    // ----- Show (#173 AC3, #177 AC4) -----

    [Fact]
    public void ShowString_RendersSparkStyleTable_WithTruncationFooter()
    {
        var rows = new List<Row>
        {
            Person("Alice", 30),
            Person("Bob", 25),
            Person("Carol", 41),
        };
        var executor = new FakeQueryExecutor(rows);
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            string table = df.ShowString(numRows: 2, truncate: true);

            const string expected =
                "+-----+---+\n" +
                "| name|age|\n" +
                "+-----+---+\n" +
                "|Alice| 30|\n" +
                "|  Bob| 25|\n" +
                "+-----+---+\n" +
                "only showing top 2 rows\n";
            Assert.Equal(expected, table);
        }
    }

    [Fact]
    public void ShowString_NoFooter_WhenResultFitsWithinNumRows()
    {
        var rows = new List<Row> { Person("Alice", 30) };
        var executor = new FakeQueryExecutor(rows);
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            string table = df.ShowString(numRows: 20, truncate: true);

            const string expected =
                "+-----+---+\n" +
                "| name|age|\n" +
                "+-----+---+\n" +
                "|Alice| 30|\n" +
                "+-----+---+\n";
            Assert.Equal(expected, table);
        }
    }

    [Fact]
    public void ShowString_RendersNullAsLiteral()
    {
        var rows = new List<Row> { Person(null, null) };
        var executor = new FakeQueryExecutor(rows);
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            string table = df.ShowString(numRows: 20, truncate: true);
            Assert.Contains("|null|null|", table);
        }
    }

    [Fact]
    public void ShowString_TruncateFalse_LeftJustifiesAndKeepsFullValue()
    {
        var schema = new StructType(new[] { new StructField("s", DataTypes.StringType) });
        var longValue = new string('x', 40);
        var rows = new List<Row> { new(schema, longValue) };
        var executor = new FakeQueryExecutor(rows);
        SparkSession spark = SparkSession.Builder().AppName("actions").GetOrCreate();
        using (spark)
        {
            spark.QueryExecutor = executor;
            spark.Catalog.Register("wide", schema);
            var df = new DataFrame(spark, new UnresolvedRelation(new[] { "wide" }));

            string table = df.ShowString(numRows: 20, truncate: false);

            Assert.Contains(longValue, table);
            // Left-justified: value starts immediately after the border.
            Assert.Contains("|" + longValue + "|", table);
        }
    }

    [Fact]
    public void ShowString_Truncate_CutsLongCellsWithEllipsis()
    {
        var schema = new StructType(new[] { new StructField("s", DataTypes.StringType) });
        var rows = new List<Row> { new(schema, "this-is-a-very-long-cell-value") };
        var executor = new FakeQueryExecutor(rows);
        SparkSession spark = SparkSession.Builder().AppName("actions").GetOrCreate();
        using (spark)
        {
            spark.QueryExecutor = executor;
            spark.Catalog.Register("wide", schema);
            var df = new DataFrame(spark, new UnresolvedRelation(new[] { "wide" }));

            string table = df.ShowString(numRows: 20, truncate: true);

            // 20-char default: first 17 chars + "..." = "this-is-a-very-lo...".
            Assert.Contains("this-is-a-very-lo...", table);
        }
    }

    [Fact]
    public void ShowString_NegativeNumRows_Throws()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => df.ShowString(-1, truncate: true));
        }
    }

    [Fact]
    public void Show_DoesNotChangeTheUnderlyingPlan()
    {
        var rows = new List<Row> { Person("Alice", 30), Person("Bob", 25) };
        var executor = new FakeQueryExecutor(rows);
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            LogicalPlan before = df.Plan;
            _ = df.ShowString(1, truncate: true);
            Assert.Same(before, df.Plan);
        }
    }

    // ----- default-unsupported backend diagnostic (#173) -----

    [Fact]
    public void Collect_WithNoBackendRegistered_ThrowsClearDiagnostic()
    {
        SparkSession spark = SparkSession.Builder().AppName("no-backend").GetOrCreate();
        using (spark)
        {
            spark.Catalog.Register("people", PeopleSchema());
            var df = new DataFrame(spark, new UnresolvedRelation(new[] { "people" }));

            QueryExecutionException ex = Assert.Throws<QueryExecutionException>(() => df.Collect());
            Assert.Contains("DeltaSharp.Executor", ex.Message);
        }
    }

    [Fact]
    public void Count_WithNoBackendRegistered_ThrowsClearDiagnostic()
    {
        SparkSession spark = SparkSession.Builder().AppName("no-backend").GetOrCreate();
        using (spark)
        {
            spark.Catalog.Register("people", PeopleSchema());
            var df = new DataFrame(spark, new UnresolvedRelation(new[] { "people" }));

            Assert.Throws<QueryExecutionException>(() => df.Count());
        }
    }

    [Fact]
    public void Action_OnSessionFreeFrame_ThrowsInvalidOperation()
    {
        var df = new DataFrame(new UnresolvedRelation(new[] { "people" }));
        Assert.Throws<InvalidOperationException>(() => df.Collect());
    }

    // ----- executor factory registration seam (#174 wiring point) -----

    [Fact]
    public void RegisterQueryExecutorFactory_InstallsBackendForNewSessions()
    {
        var rows = new List<Row> { Person("Alice", 30) };
        var executor = new FakeQueryExecutor(rows);
        try
        {
            SparkSession.RegisterQueryExecutorFactory(_ => executor);
            SparkSession spark = SparkSession.Builder().AppName("factory").GetOrCreate();
            using (spark)
            {
                spark.Catalog.Register("people", PeopleSchema());
                var df = new DataFrame(spark, new UnresolvedRelation(new[] { "people" }));

                Assert.Single(df.Collect());
                Assert.Equal(1, executor.CollectCallCount);
            }
        }
        finally
        {
            SparkSession.RegisterQueryExecutorFactory(null);
        }
    }

    // ----- lazy/eager invariant via the #169 audit seam (#173 AC4) -----

    [Fact]
    public void TransformationChain_TriggersNoExecution()
    {
        var recording = new RecordingAudit();
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        using (ExecutionAudit.BeginScope(recording))
        {
            // A whole chain of transformations — building plan nodes only.
            _ = df.Select(Functions.Col("name"))
                  .Filter(Functions.Col("age").Gt(21))
                  .Limit(5)
                  .Distinct();

            Assert.True(recording.ObservedNoExecution);
            Assert.Equal(0, executor.CollectCallCount);
            Assert.Equal(0, executor.CountCallCount);
        }
    }

    [Fact]
    public void Collect_RecordsExactlyOneAnalyzerStage_AndTheFullBackendPath()
    {
        var rows = new List<Row> { Person("Alice", 30) };
        var executor = new FakeQueryExecutor(rows);
        var recording = new RecordingAudit();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        using (ExecutionAudit.BeginScope(recording))
        {
            _ = df.Collect();

            Assert.Equal(
                new[] { ExecutionStage.Analyzer, ExecutionStage.Planner, ExecutionStage.Backend },
                recording.StagePath);
            Assert.Single(recording.StagePath, s => s == ExecutionStage.Analyzer);
        }
    }

    [Fact]
    public void Count_RecordsExactlyOneAnalyzerStagePerAction()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>(), countOverride: 3);
        var recording = new RecordingAudit();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        using (ExecutionAudit.BeginScope(recording))
        {
            _ = df.Count();
            _ = df.Count();

            int analyzerStages = 0;
            foreach (ExecutionStage stage in recording.StagePath)
            {
                if (stage == ExecutionStage.Analyzer)
                {
                    analyzerStages++;
                }
            }

            Assert.Equal(2, analyzerStages);
        }
    }
}
