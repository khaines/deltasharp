using System;
using System.Collections.Generic;
using DeltaSharp.Analysis;
using DeltaSharp.Core.Tests.Actions;
using DeltaSharp.Core.Tests.LazyEager;
using DeltaSharp.Diagnostics;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.WriteDoor;

/// <summary>
/// STORY-04.6.3 (#175) — the write door on the Core surface: <see cref="DataFrame.Write"/> →
/// <see cref="DataFrameWriter"/>. Covers the four acceptance criteria that are observable without a real
/// sink: writer configuration is lazy and only <c>Save</c> executes (AC2), <c>Save</c> analyzes and
/// crosses the execution seam as an eager action building a <see cref="WriteToSource"/> intent (AC1), an
/// unsupported format is a deterministic diagnostic before any output (AC3), and a recognized-but-deferred
/// format routes to a deterministic EPIC-05 diagnostic (AC4). End-to-end materialization into the local
/// sink lives in <c>DeltaSharp.Executor.Tests</c> (Core cannot execute). Serialized because it touches
/// process-wide session state. See <c>docs/engineering/design/write-door.md</c>.
/// </summary>
[Collection(SparkSessionTestCollection.Name)]
public sealed class WriteDoorTests
{
    private static StructType PeopleSchema() => new(new[]
    {
        new StructField("name", DataTypes.StringType),
        new StructField("age", DataTypes.IntegerType),
    });

    public WriteDoorTests()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
    }

    /// <summary>Creates a session with the fake executor installed and a <c>people</c> relation
    /// registered, plus a DataFrame bound to it over an unresolved scan (mirrors the action tests).</summary>
    private static (SparkSession Spark, DataFrame Df) NewBoundFrame(FakeQueryExecutor executor)
    {
        SparkSession spark = SparkSession.Builder().AppName("write-door").GetOrCreate();
        spark.QueryExecutor = executor;
        spark.Catalog.Register("people", PeopleSchema());
        var df = new DataFrame(spark, new UnresolvedRelation(new[] { "people" }));
        return (spark, df);
    }

    // ---------------- AC2: writer configuration is lazy ----------------

    [Fact]
    public void Write_ReturnsAFreshWriter_PerAccess()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            DataFrameWriter first = df.Write;
            DataFrameWriter second = df.Write;

            Assert.NotNull(first);
            Assert.NotSame(first, second); // staged config on one writer never leaks into another
        }
    }

    [Fact]
    public void WriterConfiguration_IsLazy_TouchesNoAuditSeam_AndNeverExecutes()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        var recording = new RecordingAudit();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        using (ExecutionAudit.BeginScope(recording))
        {
            // A whole chain of writer configuration — updating intent only, returning the writer.
            DataFrameWriter writer = df.Write
                .Format("memory")
                .Mode(SaveMode.Overwrite)
                .Option("k", "v")
                .Option("flag", true)
                .Options(new Dictionary<string, string> { ["a"] = "b" })
                .PartitionBy("age");

            Assert.NotNull(writer);
            Assert.True(recording.ObservedNoExecution);   // no analyze/plan/execute happened
            Assert.Empty(recording.StagePath);
            Assert.Equal(0, executor.WriteCallCount);      // no eager crossing of the seam
        }
    }

    [Fact]
    public void WriterConfiguration_ReturnsTheSameWriter_ForChaining()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            DataFrameWriter writer = df.Write;

            // Each config call returns the same writer instance (Spark's mutable-builder shape).
            Assert.Same(writer, writer.Format("memory"));
            Assert.Same(writer, writer.Mode(SaveMode.Append));
            Assert.Same(writer, writer.Mode("overwrite"));
            Assert.Same(writer, writer.Option("k", "v"));
            Assert.Same(writer, writer.Option("b", true));
            Assert.Same(writer, writer.Option("l", 3L));
            Assert.Same(writer, writer.Option("d", 1.5d));
            Assert.Same(writer, writer.Options(new Dictionary<string, string>()));
            Assert.Same(writer, writer.PartitionBy("age"));
        }
    }

    [Theory]
    [InlineData("append", SaveMode.Append)]
    [InlineData("APPEND", SaveMode.Append)]
    [InlineData("overwrite", SaveMode.Overwrite)]
    [InlineData("ignore", SaveMode.Ignore)]
    [InlineData("error", SaveMode.ErrorIfExists)]
    [InlineData("errorifexists", SaveMode.ErrorIfExists)]
    [InlineData("default", SaveMode.ErrorIfExists)]
    public void Mode_String_ParsesCaseInsensitively_AndRecordsIntent(string modeString, SaveMode expected)
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            // Saving with format "memory" reaches the seam; the analyzed WriteToSource carries the mode.
            df.Write.Format("memory").Mode(modeString).Save("mem://mode");

            var write = Assert.IsType<WriteToSource>(executor.LastPlan);
            Assert.Equal(expected, write.Sink.Mode);
        }
    }

    [Fact]
    public void Mode_String_Unknown_ThrowsDeterministically_BeforeSave()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            // Spark parity: mode(String) validates at call time, so a bad mode fails BEFORE any Save/output.
            ArgumentException ex = Assert.Throws<ArgumentException>(() => df.Write.Mode("upsert"));
            Assert.Contains("upsert", ex.Message);
            Assert.Equal(0, executor.WriteCallCount);
        }
    }

    // ---------------- AC1: Save is an eager action through the seam ----------------

    [Fact]
    public void Save_ExecutesEagerly_BuildingAnAnalyzedWriteToSourceIntent()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            df.Write.Format("memory").Mode(SaveMode.Overwrite).Save("mem://t1");

            Assert.Equal(1, executor.WriteCallCount);        // crossed the seam exactly once
            Assert.Equal(0, executor.CollectCallCount);
            Assert.Equal(0, executor.CountCallCount);

            var write = Assert.IsType<WriteToSource>(executor.LastPlan);
            Assert.True(write.Child.Resolved);               // the child scan was analyzer-resolved
            Assert.Equal("memory", write.Sink.Format);
            Assert.Equal(SaveMode.Overwrite, write.Sink.Mode);
            Assert.Equal("mem://t1", write.Sink.Path);
        }
    }

    [Fact]
    public void Save_RecordsExactlyOneAnalyzerStage_AndTheFullBackendPath()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        var recording = new RecordingAudit();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        using (ExecutionAudit.BeginScope(recording))
        {
            df.Write.Format("memory").Save("mem://t2");

            // Same eager pipeline shape the read actions record (analyze → plan → backend).
            Assert.Equal(
                new[] { ExecutionStage.Analyzer, ExecutionStage.Planner, ExecutionStage.Backend },
                recording.StagePath);
            Assert.Single(recording.StagePath, s => s == ExecutionStage.Analyzer);
        }
    }

    [Fact]
    public void Save_CoercesTypedOptions_ToInvariantStrings_OnTheWriteIntent()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            df.Write.Format("memory")
                .Option("flag", true)
                .Option("count", 42L)
                .Option("ratio", 1.5d)
                .Save("mem://opts");

            var write = Assert.IsType<WriteToSource>(executor.LastPlan);
            Assert.Equal("true", write.Sink.Options["flag"]);
            Assert.Equal("42", write.Sink.Options["count"]);
            Assert.Equal("1.5", write.Sink.Options["ratio"]);
        }
    }

    // ---------------- AC3: unsupported format is a deterministic diagnostic ----------------

    [Fact]
    public void Save_UnsupportedFormat_ThrowsDeterministically_BeforeCrossingTheSeam()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            AnalysisException ex = Assert.Throws<AnalysisException>(
                () => df.Write.Format("orc").Save("mem://x"));

            Assert.Equal(AnalysisErrorKind.UnsupportedDataSink, ex.Kind);
            Assert.Contains("orc", ex.Message);
            Assert.Contains("memory", ex.Message);   // names the supported local sink alternative
            Assert.Equal(0, executor.WriteCallCount); // failed at analysis, before any output/seam crossing
        }
    }

    // ---------------- AC4: recognized-but-deferred format routes to EPIC-05 ----------------

    [Theory]
    [InlineData("delta")]
    [InlineData("parquet")]
    public void Save_DeferredFormat_RoutesToDeterministicEpic05Diagnostic(string format)
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            AnalysisException ex = Assert.Throws<AnalysisException>(
                () => df.Write.Format(format).Save("mem://x"));

            Assert.Equal(AnalysisErrorKind.UnsupportedDataSink, ex.Kind);
            Assert.Contains("EPIC-05", ex.Message);
            Assert.Contains(format, ex.Message);
            Assert.Equal(0, executor.WriteCallCount);
        }
    }

    [Fact]
    public void Save_WithNoFormat_DefaultsToParquet_RoutingToEpic05()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            // Spark parity: the default write source is parquet, which in M1 is deferred to EPIC-05.
            AnalysisException ex = Assert.Throws<AnalysisException>(() => df.Write.Save("mem://x"));

            Assert.Equal(AnalysisErrorKind.UnsupportedDataSink, ex.Kind);
            Assert.Contains("EPIC-05", ex.Message);
            Assert.Contains("parquet", ex.Message);
            Assert.Equal(0, executor.WriteCallCount);
        }
    }

    [Fact]
    public void Save_DeferredFormat_DiagnosticRedactsSecretInPath()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            AnalysisException ex = Assert.Throws<AnalysisException>(() => df.Write
                .Format("delta")
                .Save("abfss://c@a.dfs.core.windows.net/t?sig=DEADBEEFSECRET"));

            Assert.DoesNotContain("DEADBEEFSECRET", ex.Message);
            Assert.Contains("<redacted>", ex.Message);
        }
    }

    // ---------------- lifecycle parity with the read door ----------------

    [Fact]
    public void Save_OnStoppedSession_ThrowsSessionStopped()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        spark.Stop();

        Assert.Throws<SessionStoppedException>(() => df.Write.Format("memory").Save("mem://x"));
    }

    [Fact]
    public void Write_OnStoppedSession_StillReturnsWriter_ButSaveThrows()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        spark.Stop();

        // Getting/configuring the writer is lazy (no session check); only the Save action guards lifecycle.
        DataFrameWriter writer = df.Write.Format("memory");
        Assert.NotNull(writer);
        Assert.Throws<SessionStoppedException>(() => writer.Save("mem://x"));
    }

    // ---------------- secret redaction on the sink descriptor ----------------

    [Fact]
    public void SinkDescriptor_SimpleString_RedactsCredentialBearingPath()
    {
        var sink = new SinkDescriptor(
            "memory",
            SaveMode.Overwrite,
            path: "abfss://c@a.dfs.core.windows.net/t?sig=DEADBEEFSECRET");

        string text = sink.SimpleString;

        Assert.DoesNotContain("DEADBEEFSECRET", text);
        Assert.Contains("<redacted>", text);
    }
}
