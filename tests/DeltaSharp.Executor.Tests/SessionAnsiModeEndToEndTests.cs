using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DeltaSharp.Storage;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// End-to-end tests for #603: the session's <c>spark.sql.ansi.enabled</c> is threaded (via
/// <c>ExecutionOptions.From</c> → the physical planner) into BOTH the query path and the write-door
/// (CHECK-constraint enforcement, which inherits the planner's ANSI mode via the sink factory, #602).
/// An Ansi session (the default) REPORTS an arithmetic overflow; a Legacy session WRAPS it (two's
/// complement, not throwing the overflow). The prior tests injected the mode directly into the enforcer;
/// these prove the missing upstream hop — the session config genuinely selects the mode per action.
/// </summary>
[Collection(SessionExecutionTestCollection.Name)]
public sealed class SessionAnsiModeEndToEndTests : IDisposable
{
    private static readonly StructType Schema =
        new(new[] { new StructField("v", IntegerType.Instance, nullable: false) });

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ansi-e2e-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static SparkSession NewSession(bool? ansiEnabled)
    {
        DeltaSharpExecutor.Enable();
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
        SparkSession spark = SparkSession.Builder().AppName("ansi-e2e").GetOrCreate();
        if (ansiEnabled is { } enabled)
        {
            spark.Conf.Set("spark.sql.ansi.enabled", enabled);
        }

        return spark;
    }

    private static IReadOnlyList<Row> MaxValueRows() => new[] { new Row(Schema, int.MaxValue) };

    // ---------- Query path ----------

    [Fact]
    public void QueryPath_AnsiSession_ArithmeticOverflow_Throws()
    {
        // Default (unset) session = ANSI: `v + v` at int.MaxValue overflows int32 and is REPORTED (the backend
        // surfaces ArithmeticOverflowException, wrapped by the driver as QueryExecutionException).
        using SparkSession spark = NewSession(ansiEnabled: null);
        DataFrame df = spark.CreateDataFrame(MaxValueRows(), Schema)
            .Select(Functions.Col("v").Plus(Functions.Col("v")).As("doubled"));

        QueryExecutionException ex = Assert.Throws<QueryExecutionException>(() => df.Collect());
        Assert.IsType<ArithmeticOverflowException>(ex.InnerException);
    }

    [Fact]
    public void QueryPath_LegacySession_ArithmeticOverflow_WrapsToNullWithoutThrowing()
    {
        // spark.sql.ansi.enabled=false = LEGACY: the SAME overflow yields SQL NULL (DeltaSharp's documented
        // Legacy semantics) and does NOT throw — proving the session config selects the mode on the query path.
        using SparkSession spark = NewSession(ansiEnabled: false);
        DataFrame df = spark.CreateDataFrame(MaxValueRows(), Schema)
            .Select(Functions.Col("v").Plus(Functions.Col("v")).As("doubled"));

        IReadOnlyList<Row> rows = df.Collect();
        Assert.True(rows[0].IsNullAt(0));
    }

    [Fact]
    public void QueryPath_RuntimeConfSet_IsHonoredOnTheNextAction()
    {
        // The mode is read per action (ExecutionOptions.From), so a runtime Conf.Set switches behavior on the
        // NEXT action — not cached at session/executor construction.
        using SparkSession spark = NewSession(ansiEnabled: null); // starts ANSI
        DataFrame df = spark.CreateDataFrame(MaxValueRows(), Schema)
            .Select(Functions.Col("v").Plus(Functions.Col("v")).As("doubled"));
        Assert.Throws<QueryExecutionException>(() => df.Collect()); // ANSI reports the overflow

        spark.Conf.Set("spark.sql.ansi.enabled", false); // flip to Legacy at runtime
        DataFrame df2 = spark.CreateDataFrame(MaxValueRows(), Schema)
            .Select(Functions.Col("v").Plus(Functions.Col("v")).As("doubled"));
        Assert.True(df2.Collect()[0].IsNullAt(0)); // now wraps to NULL
    }

    // ---------- Write-door (CHECK-constraint predicate) ----------

    [Fact]
    public void WriteDoor_AnsiSession_CheckPredicateOverflow_ReportsOverflow()
    {
        // A CHECK `v + v > 0` evaluated over v=int.MaxValue: under ANSI the overflow is REPORTED (not a
        // constraint violation) — the write-door inherits the session ANSI mode via the sink factory (#602/#603).
        string table = Table("ansi-check");
        CreateTableWithCheck(table, ansiEnabled: null, "v + v > 0");

        using SparkSession spark = NewSession(ansiEnabled: null);
        QueryExecutionException ex = Assert.Throws<QueryExecutionException>(
            () => spark.CreateDataFrame(MaxValueRows(), Schema).Write.Format("delta").Mode("append").Save(table));
        Assert.IsType<ArithmeticOverflowException>(ex.InnerException);
    }

    [Fact]
    public void WriteDoor_LegacySession_CheckPredicateOverflow_WrapsThenRejectsRow()
    {
        // Under LEGACY the SAME CHECK overflow WRAPS (v + v = -2), so `-2 > 0` is false → the row is rejected
        // as an ordinary constraint violation, NOT an overflow error. Proves the session config drives the
        // write-door mode end-to-end.
        string table = Table("legacy-check");
        CreateTableWithCheck(table, ansiEnabled: false, "v + v > 0");

        using SparkSession spark = NewSession(ansiEnabled: false);
        Assert.Throws<DeltaConstraintViolationException>(
            () => spark.CreateDataFrame(MaxValueRows(), Schema).Write.Format("delta").Mode("append").Save(table));
    }

    private string Table(string name) => Path.Combine(_root, name);

    // Seeds a table (v0) with a safe row, then injects a CHECK constraint at v1.
    private void CreateTableWithCheck(string table, bool? ansiEnabled, string predicate)
    {
        using (SparkSession spark = NewSession(ansiEnabled))
        {
            spark.CreateDataFrame(new[] { new Row(Schema, 1) }, Schema)
                .Write.Format("delta").Mode("append").Save(table);
        }

        string logDir = Path.Combine(table, "_delta_log");
        string metaLine = File.ReadAllLines(Path.Combine(logDir, $"{0:D20}.json"))
            .First(line => line.Contains("\"metaData\"", StringComparison.Ordinal));
        JsonNode root = JsonNode.Parse(metaLine)!;
        JsonObject metadata = root["metaData"]!.AsObject();
        if (metadata["configuration"] is not JsonObject configuration)
        {
            configuration = new JsonObject();
            metadata["configuration"] = configuration;
        }

        configuration["delta.constraints.ck"] = predicate;
        File.WriteAllText(Path.Combine(logDir, $"{1:D20}.json"), root.ToJsonString() + "\n");
    }
}
