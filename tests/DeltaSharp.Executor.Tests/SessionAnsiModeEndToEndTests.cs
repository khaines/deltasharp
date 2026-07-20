using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DeltaSharp.Analysis;
using DeltaSharp.Storage;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// End-to-end tests for #603: the session's <c>spark.sql.ansi.enabled</c> is threaded (via
/// <c>ExecutionOptions.From</c> → the physical planner) into BOTH the query path and the write-door
/// (CHECK-constraint enforcement, which inherits the planner's ANSI mode via the sink factory, #602).
/// An Ansi session (the default) REPORTS an arithmetic overflow; a Legacy session NULLs it (DeltaSharp
/// nulls on overflow — it never wraps to a two's-complement value; note this differs from Spark non-ANSI,
/// which wraps). The prior tests injected the mode directly into the enforcer; these prove the missing
/// upstream hop — the session config genuinely selects the mode per action.
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
    public void QueryPath_LegacySession_ArithmeticOverflow_NullsWithoutThrowing()
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
        Assert.True(df2.Collect()[0].IsNullAt(0)); // now nulls the overflow
    }

    [Fact]
    public void ExplainPhysical_IsModeIndependent_AnsiAndLegacyRenderIdentically()
    {
        // ExplainPhysical is non-executing: ANSI mode is an eval-time overflow/cast semantic that never appears
        // in the rendered plan tree, so the physical explain of the SAME query is byte-identical across modes.
        // This pins the assumption behind leaving ExplainPhysical on the planner default.
        string ansiPlan;
        using (SparkSession ansi = NewSession(ansiEnabled: null))
        {
            ansiPlan = ansi.CreateDataFrame(MaxValueRows(), Schema)
                .Select(Functions.Col("v").Plus(Functions.Col("v")).As("doubled")).ExplainString(ExplainMode.Simple);
        }

        string legacyPlan;
        using (SparkSession legacy = NewSession(ansiEnabled: false))
        {
            legacyPlan = legacy.CreateDataFrame(MaxValueRows(), Schema)
                .Select(Functions.Col("v").Plus(Functions.Col("v")).As("doubled")).ExplainString(ExplainMode.Simple);
        }

        Assert.Equal(ansiPlan, legacyPlan);
    }

    // ---------- #614: output-schema nullability is mode-aware ----------

    [Fact]
    public void OutputSchema_LegacySession_ArithmeticOverflowColumn_IsNullable()
    {
        // #614: `v + v` over a NOT-NULL `v` can null on overflow under Legacy (DeltaSharp nulls rather
        // than throwing), so the analyzed output column must be reported nullable — under-reporting a
        // NOT-NULL column that can materialize SQL NULL is the bug this fixes.
        using SparkSession spark = NewSession(ansiEnabled: false);
        DataFrame df = spark.CreateDataFrame(MaxValueRows(), Schema)
            .Select(Functions.Col("v").Plus(Functions.Col("v")).As("doubled"));

        Assert.True(AnalyzedNullability(spark, df, "doubled"));
    }

    [Fact]
    public void OutputSchema_AnsiSession_ArithmeticOverflowColumn_FollowsOperands()
    {
        // Under Ansi the same overflow THROWS instead of nulling, so nullability follows the operands:
        // `v + v` over a NOT-NULL `v` stays NOT-NULL. This pins that the Legacy widening is mode-scoped
        // and Ansi output nullability is byte-identical to the pre-#614 (mode-independent) behavior.
        using SparkSession spark = NewSession(ansiEnabled: null);
        DataFrame df = spark.CreateDataFrame(MaxValueRows(), Schema)
            .Select(Functions.Col("v").Plus(Functions.Col("v")).As("doubled"));

        Assert.False(AnalyzedNullability(spark, df, "doubled"));
    }

    [Fact]
    public void OutputSchema_DefaultAnalyzerCtors_DoNotWiden_AnsiIsTheDefault()
    {
        // #614 default-guard: the Analyzer overloads WITHOUT an explicit AnsiMode must default to Ansi,
        // NOT Legacy — so `v + v` reports NOT-NULL even when the plan came from a Legacy session. This
        // kills a "flip the Analyzer default to Legacy" mutant: both default-ctor paths must stay Ansi.
        using SparkSession spark = NewSession(ansiEnabled: false);
        DataFrame df = spark.CreateDataFrame(MaxValueRows(), Schema)
            .Select(Functions.Col("v").Plus(Functions.Col("v")).As("doubled"));

        _ = new Analyzer(spark.Catalog).Resolve(
            df.Plan, out IReadOnlyList<(string Name, DataType Type, bool Nullable)> oneArg);
        _ = new Analyzer(spark.Catalog, spark.FileRelationResolver).Resolve(
            df.Plan, out IReadOnlyList<(string Name, DataType Type, bool Nullable)> twoArg);

        Assert.False(oneArg.Single(c => c.Name == "doubled").Nullable);
        Assert.False(twoArg.Single(c => c.Name == "doubled").Nullable);
    }

    [Fact]
    public void OutputSchema_ComparisonOverArithmetic_IsModeAware()
    {
        // A comparison propagates its operands' nullability: `(v + v) > 0` inherits the arithmetic's
        // Legacy widening, so the aliased output column is nullable under Legacy, NOT-NULL under Ansi.
        Column comparison = Functions.Col("v").Plus(Functions.Col("v")).Gt(Functions.Lit(0)).As("cmp");

        using (SparkSession legacy = NewSession(ansiEnabled: false))
        {
            DataFrame df = legacy.CreateDataFrame(MaxValueRows(), Schema).Select(comparison);
            Assert.True(AnalyzedNullability(legacy, df, "cmp"));
        }

        using (SparkSession ansi = NewSession(ansiEnabled: null))
        {
            DataFrame df = ansi.CreateDataFrame(MaxValueRows(), Schema).Select(comparison);
            Assert.False(AnalyzedNullability(ansi, df, "cmp"));
        }
    }

    [Fact]
    public void OutputSchema_CaseWhenOverArithmetic_IsModeAware()
    {
        // A CASE re-exposes its branch VALUES' nullability: a branch value of `v + v` (with a NOT-NULL
        // else `v`) makes the output nullable under Legacy but NOT-NULL under Ansi — the CaseWhen gap.
        Column caseWhen = Functions
            .When(Functions.Col("v").Gt(Functions.Lit(0)), Functions.Col("v").Plus(Functions.Col("v")))
            .Otherwise(Functions.Col("v"))
            .As("x");

        using (SparkSession legacy = NewSession(ansiEnabled: false))
        {
            DataFrame df = legacy.CreateDataFrame(MaxValueRows(), Schema).Select(caseWhen);
            Assert.True(AnalyzedNullability(legacy, df, "x"));
        }

        using (SparkSession ansi = NewSession(ansiEnabled: null))
        {
            DataFrame df = ansi.CreateDataFrame(MaxValueRows(), Schema).Select(caseWhen);
            Assert.False(AnalyzedNullability(ansi, df, "x"));
        }
    }

    // Reads the analyzed (resolved) output column's nullability the way a DataFrame action does — the
    // session's ANSI lens is threaded into the analyzer's output-schema derivation (#614). Uses the
    // internal analyzer directly (InternalsVisibleTo) so the assertion is on the resolved schema, not a
    // rendered plan string. A cast analog is not covered end-to-end (the public Column API exposes no
    // cast), so Cast.NullableUnder's Legacy widening is validated at the Core expression level (#614).
    private static bool AnalyzedNullability(SparkSession spark, DataFrame df, string column)
    {
        _ = new Analyzer(spark.Catalog, spark.FileRelationResolver, spark.AnsiMode)
            .Resolve(df.Plan, out IReadOnlyList<(string Name, DataType Type, bool Nullable)> output);
        return output.Single(c => c.Name == column).Nullable;
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
    public void WriteDoor_LegacySession_CheckPredicateOverflow_NullsThenRejectsRow()
    {
        // Under LEGACY the CHECK overflow yields SQL NULL (DeltaSharp NULLs on overflow — it never wraps to a
        // two's-complement value), and NULL is not-true → the row is rejected as an ordinary constraint
        // violation, NOT an overflow error. Proves the session config drives the write-door mode end-to-end.
        string table = Table("legacy-check");
        CreateTableWithCheck(table, ansiEnabled: false, "v + v > 0");

        using SparkSession spark = NewSession(ansiEnabled: false);
        Assert.Throws<DeltaConstraintViolationException>(
            () => spark.CreateDataFrame(MaxValueRows(), Schema).Write.Format("delta").Mode("append").Save(table));
    }

    [Fact]
    public void WriteDoor_LegacySession_OverflowIsNulled_NotWrapped_SoStaysFailClosed()
    {
        // Bypass guard (security): a CHECK `v + v < 0` at v=int.MaxValue would be ADMITTED by a genuine
        // two's-complement WRAP (-2 < 0 = true), which would be a real constraint-enforcement bypass. Because
        // DeltaSharp NULLs the overflow instead, the predicate is NULL (not-true) and the row is REJECTED — so
        // this test regresses (goes green→fail) only if Legacy ever starts wrapping. It locks fail-closed.
        string table = Table("legacy-bypass-guard");
        CreateTableWithCheck(table, ansiEnabled: false, "v + v < 0");

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
