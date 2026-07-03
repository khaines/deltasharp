using System;
using System.Collections.Generic;
using System.Linq;
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

    // ----- A (#412): Limit/Distinct must thread the session so a following action runs -----

    [Fact]
    public void Limit_ThreadsSession_SoAFollowingActionRuns()
    {
        var rows = new List<Row> { Person("Alice", 30) };
        var executor = new FakeQueryExecutor(rows);
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            DataFrame limited = df.Limit(1);
            Assert.NotNull(limited.Session);
            Assert.Same(spark, limited.Session);

            // Would throw InvalidOperationException on a session-dropping Limit (the #412 regression).
            Assert.Single(limited.Collect());
        }
    }

    [Fact]
    public void Distinct_ThreadsSession_SoCountRuns()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>(), countOverride: 4);
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            DataFrame distinct = df.Distinct();
            Assert.NotNull(distinct.Session);
            Assert.Same(spark, distinct.Session);

            Assert.Equal(4, distinct.Count());
        }
    }

    [Fact]
    public void SelectThenLimit_ThreadsSession_SoShowRuns()
    {
        var rows = new List<Row> { Person("Alice", 30) };
        var executor = new FakeQueryExecutor(rows);
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            DataFrame projected = df.Select(Functions.Col("name")).Limit(5);
            Assert.Same(spark, projected.Session);

            string table = projected.ShowString(numRows: 20, truncate: true);
            Assert.Contains("Alice", table);
        }
    }

    [Fact]
    public void EveryTransformation_ThreadsTheSameNonNullSession_AndStaysExecutable()
    {
        var rows = new List<Row> { Person("Alice", 30) };
        var executor = new FakeQueryExecutor(rows);
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            DataFrame other = new DataFrame(spark, new UnresolvedRelation(new[] { "people" }));

            var transforms = new (string Name, Func<DataFrame, DataFrame> Apply)[]
            {
                ("Select", d => d.Select(Functions.Col("name"))),
                ("Select(names)", d => d.Select("name")),
                ("Filter", d => d.Filter(Functions.Col("age").Gt(21))),
                ("Where", d => d.Where(Functions.Col("age").Gt(21))),
                ("WithColumn", d => d.WithColumn("bump", Functions.Col("age").Plus(1))),
                ("Sort", d => d.Sort(Functions.Col("name"))),
                ("OrderBy", d => d.OrderBy("name")),
                ("Limit", d => d.Limit(3)),
                ("Distinct", d => d.Distinct()),
                ("Join", d => d.Join(other)),
                ("Union", d => d.Union(other)),
                ("CrossJoin", d => d.CrossJoin(other)),
                ("GroupBy.Count", d => d.GroupBy(Functions.Col("name")).Count()),
                ("GroupBy.Agg", d => d.GroupBy(Functions.Col("name")).Agg(Functions.Count(Functions.Lit(1L)))),
            };

            foreach ((string name, Func<DataFrame, DataFrame> apply) in transforms)
            {
                DataFrame result = apply(df);
                Assert.True(result.Session is not null, $"{name} dropped the session.");
                Assert.Same(spark, result.Session);

                // The frame stays executable: an action on it reaches the fake backend (would throw
                // InvalidOperationException if the session had been dropped).
                Assert.Equal(rows.Count, result.Collect().Count);
            }
        }
    }

    // ----- F (#412): an action must reject a stopped session (Spark parity) -----

    [Fact]
    public void Collect_OnStoppedSession_ThrowsSessionStopped()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        spark.Stop();

        Assert.Throws<SessionStoppedException>(() => df.Collect());
    }

    [Fact]
    public void ShowString_OnEmptyResult_StillRendersColumnHeaders()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            string table = df.ShowString(numRows: 20, truncate: true);

            // The header is derived from the analyzed output schema, so an empty result still shows
            // real column names (Spark parity) rather than the degenerate ++/++ box.
            const string expected =
                "+----+---+\n" +
                "|name|age|\n" +
                "+----+---+\n" +
                "+----+---+\n";
            Assert.Equal(expected, table);
        }
    }

    [Fact]
    public void ShowString_RendersDuplicateOutputColumnNames_WhereCollectAndCountSucceed()
    {
        // A join of two frames over the same relation (name, age) yields a duplicate-name output
        // (name, age, name, age); Select(Col("name"), Col("name")) yields (name, name). Spark's show()
        // renders duplicate headers, and Collect/Count already succeed on such plans (they never
        // materialize a dup-rejecting StructType). The header must be dup-name tolerant too (#419).
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            DataFrame other = new DataFrame(spark, new UnresolvedRelation(new[] { "people" }));

            foreach (DataFrame dup in new[]
            {
                df.Join(other),
                df.Select(Functions.Col("name"), Functions.Col("name")),
            })
            {
                string table = dup.ShowString(numRows: 20, truncate: true);

                // The header (second line: separator, header, separator, separator) renders the
                // duplicate "name" column twice rather than throwing SchemaValidationException (which the
                // StructType-based header path did).
                string headerLine = table.Split('\n')[1];
                int nameHeaders = headerLine.Split('|').Count(cell => cell == "name");
                Assert.Equal(2, nameHeaders);

                // Collect/Count remain reachable on the very same duplicate-output-name plan.
                Assert.Empty(dup.Collect());
                Assert.Equal(0, dup.Count());
            }
        }
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
