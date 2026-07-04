using System;
using System.Collections.Generic;
using System.Linq;
using DeltaSharp.Types;
using Xunit;
using static DeltaSharp.Functions;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// STORY-04.6.3 (#175) end-to-end tests for the write door: they exercise the PUBLIC Core API
/// (<see cref="DataFrame.Write"/> → <see cref="DataFrameWriter"/>.<c>Save</c>) through the
/// module-initializer-registered <see cref="LocalQueryExecutor"/> and the process-wide
/// <see cref="InMemorySinkRegistry.Default"/>, proving AC1's local sink materializes the written rows on
/// the eager <c>Save</c> action (and round-trips), AC3's unsupported format and AC4's Delta/Parquet
/// deferral each fail with a deterministic diagnostic before any output, a write failure is
/// stage-attributed (STORY-04.6.4), and the write door never leaks a secret. Core-only tests cannot
/// execute, so the materialization proofs live here. See <c>docs/engineering/design/write-door.md</c>.
/// </summary>
[Collection(SessionExecutionTestCollection.Name)]
public class WriteDoorEndToEndTests
{
    private static readonly StructType PeopleSchema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField("name", StringType.Instance, nullable: true),
    });

    private static SparkSession NewSession()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
        return SparkSession.Builder().AppName("write-door-e2e").GetOrCreate();
    }

    private static string UniqueTarget() => $"mem://write-door/{Guid.NewGuid():N}";

    private static IReadOnlyList<Row> Rows() => new[]
    {
        new Row(PeopleSchema, 1, "alice"),
        new Row(PeopleSchema, 2, "bob"),
        new Row(PeopleSchema, 3, null),
    };

    [Fact]
    public void Save_ToMemorySink_ExecutesEagerly_AndRoundTripsRows()
    {
        using SparkSession spark = NewSession();
        string target = UniqueTarget();
        DataFrame df = spark.CreateDataFrame(Rows(), PeopleSchema);

        df.Write.Format("memory").Mode("overwrite").Save(target);

        // The eager Save committed the rows to the local sink; read them back and compare.
        Assert.True(InMemorySinkRegistry.Default.TryRead(target, out StructType? schema, out IReadOnlyList<Row> written));
        Assert.Equal(PeopleSchema, schema);
        Assert.Equal(3, written.Count);
        Assert.Equal(new[] { 1, 2, 3 }, written.Select(r => r.GetAs<int>("id")));
        Assert.Equal("alice", written[0].GetAs<string>("name"));
        Assert.True(written[2].IsNullAt(1)); // the null name round-trips

        InMemorySinkRegistry.Default.Clear(target);
    }

    [Fact]
    public void Save_AfterTransformations_WritesTheTransformedResult()
    {
        using SparkSession spark = NewSession();
        string target = UniqueTarget();
        DataFrame df = spark.CreateDataFrame(Rows(), PeopleSchema)
            .Filter(Col("id").Gt(1))
            .Select(Col("id"));

        df.Write.Format("memory").Mode("overwrite").Save(target);

        Assert.True(InMemorySinkRegistry.Default.TryRead(target, out StructType? schema, out IReadOnlyList<Row> written));
        Assert.Single(schema!); // the write drained the projected schema (id only)
        Assert.Equal(new[] { 2, 3 }, written.Select(r => r.GetAs<int>("id")));

        InMemorySinkRegistry.Default.Clear(target);
    }

    [Fact]
    public void Save_AppendMode_AppendsToExistingRows()
    {
        using SparkSession spark = NewSession();
        string target = UniqueTarget();
        DataFrame df = spark.CreateDataFrame(Rows(), PeopleSchema);

        df.Write.Format("memory").Mode("overwrite").Save(target);
        df.Write.Format("memory").Mode("append").Save(target);

        Assert.True(InMemorySinkRegistry.Default.TryRead(target, out _, out IReadOnlyList<Row> written));
        Assert.Equal(6, written.Count); // 3 + 3

        InMemorySinkRegistry.Default.Clear(target);
    }

    [Fact]
    public void Save_IgnoreMode_LeavesTheExistingTargetUnchanged()
    {
        using SparkSession spark = NewSession();
        string target = UniqueTarget();
        DataFrame df = spark.CreateDataFrame(Rows(), PeopleSchema);

        df.Write.Format("memory").Mode("overwrite").Save(target);
        // A different, larger frame with Ignore must NOT overwrite the existing target.
        DataFrame other = spark.CreateDataFrame(
            new[] { new Row(PeopleSchema, 9, "z"), new Row(PeopleSchema, 8, "y"), new Row(PeopleSchema, 7, "x"), new Row(PeopleSchema, 6, "w") },
            PeopleSchema);
        other.Write.Format("memory").Mode("ignore").Save(target);

        Assert.True(InMemorySinkRegistry.Default.TryRead(target, out _, out IReadOnlyList<Row> written));
        Assert.Equal(3, written.Count); // unchanged — the ignore skipped

        InMemorySinkRegistry.Default.Clear(target);
    }

    [Fact]
    public void Save_ErrorIfExists_OntoExistingTarget_FailsStageAttributed_WithoutOverwriting()
    {
        using SparkSession spark = NewSession();
        string target = UniqueTarget();
        DataFrame df = spark.CreateDataFrame(Rows(), PeopleSchema);

        df.Write.Format("memory").Mode("overwrite").Save(target);

        // The default mode is ErrorIfExists; writing again onto the existing target must fail.
        QueryExecutionException ex = Assert.Throws<QueryExecutionException>(
            () => df.Write.Format("memory").Save(target));

        // The failure is stage-attributed to the backend (the commit conflict surfaces during execution).
        Assert.Equal(QueryExecutionStage.Backend, ex.Stage);

        // No partial output: the original three rows are intact (the conflicting write committed nothing).
        Assert.True(InMemorySinkRegistry.Default.TryRead(target, out _, out IReadOnlyList<Row> written));
        Assert.Equal(3, written.Count);

        InMemorySinkRegistry.Default.Clear(target);
    }

    [Fact]
    public void Save_UnsupportedFormat_FailsWithDeterministicDiagnostic_BeforeAnyOutput()
    {
        using SparkSession spark = NewSession();
        string target = UniqueTarget();
        DataFrame df = spark.CreateDataFrame(Rows(), PeopleSchema);

        Exception? ex = Record.Exception(() => df.Write.Format("orc").Save(target));

        Assert.NotNull(ex);
        Assert.Contains("orc", ex!.Message);
        // Nothing was committed for the unsupported format.
        Assert.False(InMemorySinkRegistry.Default.TryRead(target, out _, out _));
    }

    [Theory]
    [InlineData("delta")]
    [InlineData("parquet")]
    public void Save_DeferredFormat_FailsWithDeterministicEpic05Diagnostic_BeforeAnyOutput(string format)
    {
        using SparkSession spark = NewSession();
        string target = UniqueTarget();
        DataFrame df = spark.CreateDataFrame(Rows(), PeopleSchema);

        Exception? ex = Record.Exception(() => df.Write.Format(format).Save(target));

        Assert.NotNull(ex);
        Assert.Contains("EPIC-05", ex!.Message);
        Assert.Contains(format, ex.Message);
        Assert.False(InMemorySinkRegistry.Default.TryRead(target, out _, out _));
    }

    [Fact]
    public void Save_DefaultFormat_DefersToEpic05()
    {
        using SparkSession spark = NewSession();
        DataFrame df = spark.CreateDataFrame(Rows(), PeopleSchema);

        // No Format() call → Spark default (parquet) → EPIC-05 deferral.
        Exception? ex = Record.Exception(() => df.Write.Save(UniqueTarget()));

        Assert.NotNull(ex);
        Assert.Contains("EPIC-05", ex!.Message);
        Assert.Contains("parquet", ex.Message);
    }

    [Fact]
    public void Save_EmptyFrame_WritesZeroRows()
    {
        using SparkSession spark = NewSession();
        string target = UniqueTarget();
        DataFrame df = spark.CreateDataFrame(Array.Empty<Row>(), PeopleSchema);

        df.Write.Format("memory").Mode("overwrite").Save(target);

        Assert.True(InMemorySinkRegistry.Default.TryRead(target, out StructType? schema, out IReadOnlyList<Row> written));
        Assert.Equal(PeopleSchema, schema);
        Assert.Empty(written);

        InMemorySinkRegistry.Default.Clear(target);
    }

    [Fact]
    public void Save_DoesNotLeakSecret_InAFailureDiagnostic()
    {
        using SparkSession spark = NewSession();
        // A deferred format so analysis raises a diagnostic that stringifies the (credential-bearing) path.
        const string secretPath = "abfss://c@a.dfs.core.windows.net/t?sig=DEADBEEFSECRET";
        DataFrame df = spark.CreateDataFrame(Rows(), PeopleSchema);

        Exception? ex = Record.Exception(() => df.Write.Format("delta").Save(secretPath));

        Assert.NotNull(ex);
        Assert.DoesNotContain("DEADBEEFSECRET", ex!.Message);
        Assert.Contains("<redacted>", ex.Message);
    }

    [Fact]
    public void Save_ErrorIfExists_CollisionDiagnostic_DoesNotLeakSecretInPath()
    {
        using SparkSession spark = NewSession();
        string target = $"abfss://c@a.dfs.core.windows.net/{Guid.NewGuid():N}?sig=DEADBEEFSECRET";
        DataFrame df = spark.CreateDataFrame(Rows(), PeopleSchema);

        // First write lands; the second (ErrorIfExists) collides and surfaces a redacted diagnostic.
        df.Write.Format("memory").Mode("overwrite").Save(target);
        QueryExecutionException ex = Assert.Throws<QueryExecutionException>(
            () => df.Write.Format("memory").Save(target));

        Assert.DoesNotContain("DEADBEEFSECRET", ex.Message);

        InMemorySinkRegistry.Default.Clear(target);
    }
}
