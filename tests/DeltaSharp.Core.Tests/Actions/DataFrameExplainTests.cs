using System;
using System.Collections.Generic;
using System.Linq;
using DeltaSharp.Core.Tests.LazyEager;
using DeltaSharp.Diagnostics;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;
using static DeltaSharp.Functions;

namespace DeltaSharp.Core.Tests.Actions;

/// <summary>
/// STORY-04.7.3 (#179): <c>DataFrame.Explain</c> renders each pipeline stage (parsed/analyzed/optimized
/// logical + physical) WITHOUT executing. These Core tests cover the logical/extended path and the
/// non-throwing diagnostics (AC1, AC2, AC4, AC5) against a <see cref="FakeQueryExecutor"/>; physical-mode
/// rendering through the real planner is covered in <c>DeltaSharp.Executor.Tests</c>. Session tests are
/// serialized because they touch process-wide active/default session state.
/// </summary>
[Collection(SparkSessionTestCollection.Name)]
public sealed class DataFrameExplainTests
{
    private static StructType PeopleSchema() => new(new[]
    {
        new StructField("name", DataTypes.StringType),
        new StructField("age", DataTypes.IntegerType),
    });

    /// <summary>A session with the fake executor installed and the <c>people</c> relation registered, and
    /// a DataFrame bound to it over an unresolved scan.</summary>
    private static (SparkSession Spark, DataFrame Df) NewBoundFrame(FakeQueryExecutor executor)
    {
        SparkSession spark = SparkSession.Builder().AppName("explain").GetOrCreate();
        spark.QueryExecutor = executor;
        spark.Catalog.Register("people", PeopleSchema());
        var df = new DataFrame(spark, new UnresolvedRelation(new[] { "people" }));
        return (spark, df);
    }

    private static FakeQueryExecutor NewExecutor() => new(Array.Empty<Row>());

    // ----- AC1: logical rendering shows unresolved markers and never executes -----

    [Fact]
    public void Explain_Extended_ParsedSection_ShowsUnresolvedMarkers()
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            string text = df.Select(Col("name")).Filter(Col("age").Gt(21)).ExplainString(ExplainMode.Extended);

            string parsed = Section(text, "Parsed Logical Plan");
            Assert.Contains("'Project", parsed);
            Assert.Contains("'Filter", parsed);
            Assert.Contains("'UnresolvedRelation [people]", parsed);
            // Every parsed line carries the unresolved apostrophe marker (pre-analysis).
            foreach (string line in parsed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.Contains('\'', line);
            }
        }
    }

    [Fact]
    public void Explain_Extended_DoesNotExecute()
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            var recording = new RecordingAudit();
            using (ExecutionAudit.BeginScope(recording))
            {
                _ = df.Select(Col("name")).ExplainString(ExplainMode.Extended);
            }

            // Explain analyzes the plan (the Analyzer milestone may be recorded — analysis is NOT
            // execution), but it must never EXECUTE: no file opened, no row read, and neither the fake
            // executor's Collect/Count nor the Planner/Backend pipeline stages are reached (ADR-0001).
            Assert.Equal(0, recording.FilesOpened);
            Assert.Equal(0, recording.RowsRead);
            Assert.DoesNotContain(ExecutionStage.Planner, recording.StagePath);
            Assert.DoesNotContain(ExecutionStage.Backend, recording.StagePath);
            Assert.Equal(0, executor.CollectCallCount);
            Assert.Equal(0, executor.CountCallCount);
        }
    }

    [Fact]
    public void Explain_Simple_DoesNotExecute_ButPlansPhysically()
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            string text = df.ExplainString(ExplainMode.Simple);

            Assert.Equal(0, executor.CollectCallCount);
            Assert.Equal(0, executor.CountCallCount);
            Assert.Equal(1, executor.ExplainPhysicalCallCount);
            Assert.Contains(FakeQueryExecutor.FakePhysicalPlanText, text);
        }
    }

    // ----- AC2: extended renders analyzed and optimized separately from unresolved -----

    [Fact]
    public void Explain_Extended_RendersFourSectionsInOrder()
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            string text = df.ExplainString(ExplainMode.Extended);

            int parsed = text.IndexOf("== Parsed Logical Plan ==", StringComparison.Ordinal);
            int analyzed = text.IndexOf("== Analyzed Logical Plan ==", StringComparison.Ordinal);
            int optimized = text.IndexOf("== Optimized Logical Plan ==", StringComparison.Ordinal);
            int physical = text.IndexOf("== Physical Plan ==", StringComparison.Ordinal);

            Assert.True(parsed >= 0 && analyzed > parsed && optimized > analyzed && physical > optimized,
                $"sections out of order:\n{text}");
        }
    }

    [Fact]
    public void Explain_Extended_AnalyzedSection_HasNoUnresolvedMarkers()
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            string text = df.Select(Col("name")).ExplainString(ExplainMode.Extended);

            string analyzed = Section(text, "Analyzed Logical Plan");
            Assert.DoesNotContain("'", analyzed);
            Assert.Contains("Project", analyzed);
        }
    }

    [Fact]
    public void Explain_Simple_RendersOnlyPhysicalSection()
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            string text = df.ExplainString(ExplainMode.Simple);

            Assert.Contains("== Physical Plan ==", text);
            Assert.DoesNotContain("== Parsed Logical Plan ==", text);
            Assert.DoesNotContain("== Analyzed Logical Plan ==", text);
        }
    }

    // ----- AC4: unresolved/unsupported constructs render as diagnostics, never a raw exception -----

    [Fact]
    public void Explain_UnknownColumn_RendersDiagnostic_WithoutThrowing()
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            // 'missing' is not a column of people, so analysis fails — but Explain must not throw.
            string text = df.Select(Col("missing")).ExplainString(ExplainMode.Extended);

            // The parsed plan is still shown (diagnostics not hidden), and the analyzed/optimized/physical
            // sections carry a diagnostic line rather than an escaping exception.
            Assert.Contains("'Project", Section(text, "Parsed Logical Plan"));
            Assert.Contains("<cannot analyze plan:", Section(text, "Analyzed Logical Plan"));
            Assert.Contains("<cannot analyze plan:", Section(text, "Physical Plan"));
        }
    }

    [Fact]
    public void Explain_NoExecutionBackend_PhysicalSection_IsDiagnostic_NotThrow()
    {
        // Do NOT install a fake executor: the session falls back to the UnsupportedQueryExecutor, whose
        // ExplainPhysical returns a diagnostic (unlike Collect/Count, which throw).
        SparkSession spark = SparkSession.Builder().AppName("explain-nobackend").GetOrCreate();
        spark.Catalog.Register("people", PeopleSchema());
        var df = new DataFrame(spark, new UnresolvedRelation(new[] { "people" }));
        using (spark)
        {
            string text = df.ExplainString(ExplainMode.Simple);

            Assert.Contains("no execution backend is registered", text);
        }
    }

    // ----- AC5: physical string flows through unchanged (metrics seam is optional / absent in M1) -----

    [Fact]
    public void Explain_PhysicalSection_CarriesExecutorString_Unchanged()
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            string text = df.ExplainString(ExplainMode.Extended);
            Assert.Contains(FakeQueryExecutor.FakePhysicalPlanText, Section(text, "Physical Plan"));
        }
    }

    // ----- API surface / Spark parity -----

    [Theory]
    [InlineData("simple", false)]
    [InlineData("SIMPLE", false)]
    [InlineData("extended", true)]
    [InlineData("Extended", true)]
    public void Explain_StringMode_MatchesEnum(string mode, bool expectLogicalSections)
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            ExplainMode parsed = expectLogicalSections ? ExplainMode.Extended : ExplainMode.Simple;
            Assert.Equal(df.ExplainString(parsed), ExplainStringForMode(df, mode));
        }
    }

    [Fact]
    public void Explain_UnknownStringMode_Throws()
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            Assert.Throws<ArgumentException>(() => df.Explain("nonsense"));
        }
    }

    [Fact]
    public void Explain_AfterStop_ThrowsSessionStopped()
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        spark.Stop();
        Assert.Throws<SessionStoppedException>(() => df.Explain(ExplainMode.Extended));
    }

    // ----- Codegen/Cost/Formatted diagnostic notes -----

    [Fact]
    public void Explain_Codegen_RendersPhysicalWithCodegenNote()
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            string text = df.ExplainString(ExplainMode.Codegen);
            Assert.Contains("== Physical Plan ==", text);
            Assert.Contains("codegen mode", text);
        }
    }

    [Fact]
    public void Explain_Cost_RendersLogicalSectionsWithCostNote()
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            string text = df.ExplainString(ExplainMode.Cost);
            Assert.Contains("== Analyzed Logical Plan ==", text);
            Assert.Contains("cost mode", text);
        }
    }

    [Fact]
    public void Explain_Formatted_RendersPhysicalWithFormattedNote()
    {
        var executor = NewExecutor();
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            string text = df.ExplainString(ExplainMode.Formatted);
            Assert.Contains("== Physical Plan ==", text);
            Assert.Contains("formatted mode", text);
        }
    }

    /// <summary>Reads the body of a named EXPLAIN section (from its header to the next header or EOF).</summary>
    private static string Section(string explainText, string title)
    {
        string header = $"== {title} ==";
        int start = explainText.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"section '{title}' not found in:\n{explainText}");
        start += header.Length;
        int next = explainText.IndexOf("\n== ", start, StringComparison.Ordinal);
        return next < 0 ? explainText[start..] : explainText[start..next];
    }

    /// <summary>Drives <c>Explain(string)</c> through the console and captures the printed text so the
    /// string-overload parity assertion can compare it against <c>ExplainString</c>.</summary>
    private static string ExplainStringForMode(DataFrame df, string mode)
    {
        System.IO.TextWriter original = Console.Out;
        var buffer = new System.IO.StringWriter();
        Console.SetOut(buffer);
        try
        {
            df.Explain(mode);
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }
}
