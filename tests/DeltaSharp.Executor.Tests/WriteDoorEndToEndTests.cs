using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

    // ---------------- MUST-FIX 1: authoritative committed count + post-commit cancellation boundary ----

    [Fact]
    public void Write_IgnoreMode_OntoExistingTarget_ReportsAndCommitsZeroRows()
    {
        // Regression: the write finalize used to re-count the CHILD result (rows PRODUCED), so an Ignore
        // that skipped an existing target over-reported the full child count instead of the 0 the sink
        // actually committed. This asserts the AUTHORITATIVE committed count is threaded out.
        var fixture = new InMemoryRelationFixture();
        var registry = new InMemorySinkRegistry();
        string target = UniqueTarget();

        DataFrame initial = fixture.LocalRelationFrame(PeopleSchema, Rows()); // 3 rows
        (long firstCount, _) = fixture.WriteWithMetrics(
            initial, "memory", SaveMode.Overwrite, target, registry);
        Assert.Equal(3, firstCount);

        // A DIFFERENT, LARGER frame (4 rows) with Ignore onto the existing target must skip.
        DataFrame other = fixture.LocalRelationFrame(PeopleSchema, new[]
        {
            new Row(PeopleSchema, 9, "z"),
            new Row(PeopleSchema, 8, "y"),
            new Row(PeopleSchema, 7, "x"),
            new Row(PeopleSchema, 6, "w"),
        });
        (long ignoreCount, ExecutionMetrics metrics) = fixture.WriteWithMetrics(
            other, "memory", SaveMode.Ignore, target, registry);

        // The committed count (and the reported metrics) is 0 — NOT the child's produced 4 rows.
        Assert.Equal(0, ignoreCount);
        Assert.Equal(0, metrics.OutputRows);

        // The existing target is unchanged (still the original 3 rows) — the ignore committed nothing.
        Assert.True(registry.TryRead(target, out _, out IReadOnlyList<Row> written));
        Assert.Equal(3, written.Count);
    }

    [Fact]
    public void Write_AppendMode_OntoExistingTarget_ReportsAppendedCount_AndAppendsRows()
    {
        // Regression: the write finalize must thread out the AUTHORITATIVE committed count the sink
        // returns — for Append that is the number of rows APPENDED (rows.Count), NOT the child's produced
        // count and NOT the combined prior+new total. The end-state must be prior+N (appended, not
        // replaced). Initial 2 rows + appended 3 rows distinguishes the appended count (3) from the
        // combined total (5) and from a replace (which would keep only 3).
        var fixture = new InMemoryRelationFixture();
        var registry = new InMemorySinkRegistry();
        string target = UniqueTarget();

        DataFrame initial = fixture.LocalRelationFrame(PeopleSchema, new[]
        {
            new Row(PeopleSchema, 10, "carol"),
            new Row(PeopleSchema, 20, "dave"),
        }); // 2 rows
        (long firstCount, _) = fixture.WriteWithMetrics(
            initial, "memory", SaveMode.Overwrite, target, registry);
        Assert.Equal(2, firstCount);

        // A DIFFERENT 3-row frame (same schema) appended onto the existing 2-row target.
        DataFrame appended = fixture.LocalRelationFrame(PeopleSchema, Rows()); // 3 rows (ids 1,2,3)
        (long appendCount, ExecutionMetrics metrics) = fixture.WriteWithMetrics(
            appended, "memory", SaveMode.Append, target, registry);

        // The committed count (and reported metrics) is the 3 APPENDED rows — not 5 (the combined total)
        // and not 2 (the prior count).
        Assert.Equal(3, appendCount);
        Assert.Equal(3, metrics.OutputRows);

        // The end-state is prior + appended = 5 rows, in order (appended, not replaced).
        Assert.True(registry.TryRead(target, out _, out IReadOnlyList<Row> written));
        Assert.Equal(5, written.Count);
        Assert.Equal(new[] { 10, 20, 1, 2, 3 }, written.Select(r => r.GetAs<int>("id")));
    }

    [Fact]
    public void Write_ErrorIfExists_OntoFreshTarget_ReportsCommittedCount_AndCommitsRows()
    {
        // Regression: ErrorIfExists onto a NON-existing target is the success path — it must WRITE the
        // rows and thread out the AUTHORITATIVE committed count (rows.Count). The failing-collision path is
        // covered elsewhere; this pins the success path's count contract AND end-state. A fresh per-test
        // registry guarantees the target does not already exist (no timing/ordering race).
        var fixture = new InMemoryRelationFixture();
        var registry = new InMemorySinkRegistry();
        string target = UniqueTarget();

        DataFrame df = fixture.LocalRelationFrame(PeopleSchema, Rows()); // 3 rows

        // No prior write: ErrorIfExists onto the empty target succeeds and commits.
        (long committed, ExecutionMetrics metrics) = fixture.WriteWithMetrics(
            df, "memory", SaveMode.ErrorIfExists, target, registry);

        // The committed count (and reported metrics) is the 3 rows written.
        Assert.Equal(3, committed);
        Assert.Equal(3, metrics.OutputRows);

        // The target holds exactly those 3 rows.
        Assert.True(registry.TryRead(target, out _, out IReadOnlyList<Row> written));
        Assert.Equal(3, written.Count);
        Assert.Equal(new[] { 1, 2, 3 }, written.Select(r => r.GetAs<int>("id")));
    }

    [Fact]
    public void Write_CancelFiringAfterCommit_DoesNotFail_AndLeavesTheWriteCommitted()
    {
        // Regression (Quality C7): a cancel scheduled to fire AFTER the commit used to throw from the
        // post-commit CountRows finalize, surfacing an ALREADY-COMMITTED write as a cancelled/failed Save.
        // The commit is now the final failure boundary: a post-commit cancel is never observed.
        var fixture = new InMemoryRelationFixture();
        var registry = new InMemorySinkRegistry();
        string target = UniqueTarget();
        using var cts = new CancellationTokenSource();

        // The hook fires cancellation in the window immediately AFTER the sink commits and returns.
        var factory = new PostCommitHookSinkFactory(registry, cts.Cancel);
        DataFrame df = fixture.LocalRelationFrame(PeopleSchema, Rows()); // 3 rows

        Exception? error = Record.Exception(() =>
        {
            (long committed, _) = fixture.WriteWithMetrics(
                df, "memory", SaveMode.Overwrite, target, factory, cts.Token);
            Assert.Equal(3, committed); // the write reports the committed rows, not a failure
        });

        // No exception — a committed write never surfaces as cancelled/failed.
        Assert.Null(error);
        Assert.True(cts.IsCancellationRequested); // the post-commit cancel really did fire

        // The rows are present: the write committed and completed.
        Assert.True(registry.TryRead(target, out _, out IReadOnlyList<Row> written));
        Assert.Equal(3, written.Count);
    }

    // ---------------- MUST-FIX 2: Overwrite replacement is proven ----------------

    [Fact]
    public void Save_Overwrite_OnExistingTarget_ReplacesRows()
    {
        using SparkSession spark = NewSession();
        string target = UniqueTarget();

        // Initial write: rows with ids 1,2,3.
        spark.CreateDataFrame(Rows(), PeopleSchema)
            .Write.Format("memory").Mode("overwrite").Save(target);

        // Overwrite with DIFFERENT rows (ids 100,200) — the target must hold ONLY these afterwards.
        DataFrame replacement = spark.CreateDataFrame(
            new[]
            {
                new Row(PeopleSchema, 100, "neo"),
                new Row(PeopleSchema, 200, "trinity"),
            },
            PeopleSchema);
        replacement.Write.Format("memory").Mode("overwrite").Save(target);

        Assert.True(InMemorySinkRegistry.Default.TryRead(target, out _, out IReadOnlyList<Row> written));
        // Replacement — not append (would be 4) and not ignore (would keep the original 3).
        Assert.Equal(2, written.Count);
        Assert.Equal(new[] { 100, 200 }, written.Select(r => r.GetAs<int>("id")));

        InMemorySinkRegistry.Default.Clear(target);
    }

    // ---------------- SHOULD-FIX 3: `path` option honored + case-insensitive routing ----------------

    [Theory]
    [InlineData("path")]
    [InlineData("PATH")]
    [InlineData("Path")]
    public void Save_WithPathOption_NoSavePath_RoutesToThatTarget(string optionKey)
    {
        using SparkSession spark = NewSession();
        InMemorySinkRegistry.Default.Clear("memory://default");
        string target = UniqueTarget();
        DataFrame df = spark.CreateDataFrame(Rows(), PeopleSchema);

        // No Save(path): the `path` option (any casing, Spark parity) resolves the target.
        df.Write.Format("memory").Mode("overwrite").Option(optionKey, target).Save();

        Assert.True(InMemorySinkRegistry.Default.TryRead(target, out _, out IReadOnlyList<Row> written));
        Assert.Equal(3, written.Count);
        // It must NOT have fallen through to the path-less default key.
        Assert.False(InMemorySinkRegistry.Default.TryRead("memory://default", out _, out _));

        InMemorySinkRegistry.Default.Clear(target);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("PATH")]
    public void Write_PathOptionOnDescriptorWithoutPath_RoutesCaseInsensitively(string optionKey)
    {
        // Exercises SinkRegistry.TargetKey directly: a descriptor whose target lives ONLY in a `path`
        // option (any casing), with no descriptor.Path, must still route to that target — not the default.
        var fixture = new InMemoryRelationFixture();
        var registry = new InMemorySinkRegistry();
        string target = UniqueTarget();
        DataFrame df = fixture.LocalRelationFrame(PeopleSchema, Rows());

        (long committed, _) = fixture.WriteWithMetrics(
            df,
            "memory",
            SaveMode.Overwrite,
            path: null,
            registry,
            options: new Dictionary<string, string> { [optionKey] = target });

        Assert.Equal(3, committed);
        Assert.True(registry.TryRead(target, out _, out IReadOnlyList<Row> written));
        Assert.Equal(3, written.Count);
        Assert.False(registry.TryRead("memory://default", out _, out _));
    }
}
