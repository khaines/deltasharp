using System;
using System.Collections.Generic;
using System.Globalization;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Actions;

/// <summary>
/// STORY-04.6.4 (#176) Core-side coverage: the cancellation-token and out-metrics action overloads on
/// <see cref="DataFrame"/>, and the threading of session <c>spark.deltasharp.execution.*</c> config into
/// the <c>ExecutionOptions</c> the <see cref="IQueryExecutor"/> seam receives (discharging #416). These
/// use the <see cref="FakeQueryExecutor"/> to observe the seam without a real backend.
/// </summary>
[Collection(SparkSessionTestCollection.Name)]
public sealed class DataFrameBoundaryActionTests
{
    private static StructType PeopleSchema() => new(new[]
    {
        new StructField("name", DataTypes.StringType),
        new StructField("age", DataTypes.IntegerType),
    });

    private static Row Person(string? name, int? age) => new(PeopleSchema(), name, age);

    private static (SparkSession Spark, DataFrame Df) NewBoundFrame(FakeQueryExecutor executor)
    {
        SparkSession spark = SparkSession.Builder().AppName("boundary-actions").GetOrCreate();
        spark.QueryExecutor = executor;
        spark.Catalog.Register("people", PeopleSchema());
        var df = new DataFrame(spark, new UnresolvedRelation(new[] { "people" }));
        return (spark, df);
    }

    // ----- out-metrics overloads (criterion 4) -----

    [Fact]
    public void Collect_OutMetrics_SurfacesExecutorPublishedMetrics()
    {
        var rows = new List<Row> { Person("Alice", 30) };
        var metrics = new ExecutionMetrics(
            TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(5),
            outputRows: 1, outputBatches: 1, bytesScanned: 64, peakMemoryBytes: 128);
        var executor = new FakeQueryExecutor(rows) { MetricsToPublish = metrics };
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            IReadOnlyList<Row> collected = df.Collect(out ExecutionMetrics observed);

            Assert.Single(collected);
            Assert.Same(metrics, observed);
        }
    }

    [Fact]
    public void Collect_OutMetrics_WhenExecutorPublishesNone_ReturnsEmpty()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            _ = df.Collect(out ExecutionMetrics observed);
            Assert.Same(ExecutionMetrics.Empty, observed);
        }
    }

    [Fact]
    public void Count_OutMetrics_SurfacesExecutorPublishedMetrics()
    {
        var metrics = new ExecutionMetrics(
            TimeSpan.Zero, TimeSpan.FromMilliseconds(1),
            outputRows: 3, outputBatches: 1, bytesScanned: 0, peakMemoryBytes: 0);
        var executor = new FakeQueryExecutor(Array.Empty<Row>(), countOverride: 3) { MetricsToPublish = metrics };
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            long count = df.Count(out ExecutionMetrics observed);

            Assert.Equal(3, count);
            Assert.Same(metrics, observed);
        }
    }

    // ----- cancellation overloads (criterion 1) -----

    [Fact]
    public void Collect_WithCancelledToken_ThrowsOperationCanceled()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => df.Collect(cts.Token));
        }
    }

    [Fact]
    public void Count_WithCancelledToken_ThrowsOperationCanceled()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => df.Count(cts.Token));
        }
    }

    // ----- config threading into the seam (discharges #416) -----

    [Fact]
    public void Collect_ThreadsResultAndMemoryBoundsFromSessionConfig()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            spark.Conf.Set("spark.deltasharp.execution.maxResultRows", "7");
            spark.Conf.Set("spark.deltasharp.execution.maxResultBytes", "4096");
            spark.Conf.Set("spark.deltasharp.execution.memoryBudgetBytes", "8192");
            spark.Conf.Set("spark.deltasharp.execution.timeoutMs", "1500");

            _ = df.Collect();

            Assert.NotNull(executor.LastOptions);
            Assert.Equal(7, executor.LastOptions!.MaxResultRows);
            Assert.Equal(4096, executor.LastOptions.MaxResultBytes);
            Assert.Equal(8192, executor.LastOptions.MemoryBudgetBytes);
            Assert.Equal(TimeSpan.FromMilliseconds(1500), executor.LastOptions.Timeout);
        }
    }

    [Fact]
    public void Collect_WithNoBoundsConfigured_ThreadsUnboundedOptions()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            _ = df.Collect();

            Assert.NotNull(executor.LastOptions);
            Assert.Null(executor.LastOptions!.MaxResultRows);
            Assert.Null(executor.LastOptions.MaxResultBytes);
            Assert.Null(executor.LastOptions.MemoryBudgetBytes);
            Assert.Null(executor.LastOptions.Timeout);
        }
    }

    [Fact]
    public void Collect_DefaultSession_ThreadsAnsiMode()
    {
        // #603: unset spark.sql.ansi.enabled defaults to ANSI (the DeltaSharp default).
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            _ = df.Collect();
            Assert.Equal(AnsiMode.Ansi, executor.LastOptions!.AnsiMode);
        }
    }

    [Fact]
    public void Collect_ThreadsLegacyAnsiModeFromSessionConfig()
    {
        // #603: spark.sql.ansi.enabled=false threads AnsiMode.Legacy into the executor's per-action options.
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            spark.Conf.Set("spark.sql.ansi.enabled", false);
            _ = df.Collect();
            Assert.Equal(AnsiMode.Legacy, executor.LastOptions!.AnsiMode);
        }
    }

    [Fact]
    public void Count_ThreadsAnsiModeFromSessionConfig()
    {
        // #603: Count shares the Execute driver, so it threads the session ANSI mode too (default Ansi; Legacy
        // when spark.sql.ansi.enabled=false) — not just Collect.
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            _ = df.Count();
            Assert.Equal(AnsiMode.Ansi, executor.LastOptions!.AnsiMode);

            spark.Conf.Set("spark.sql.ansi.enabled", false);
            _ = df.Count();
            Assert.Equal(AnsiMode.Legacy, executor.LastOptions!.AnsiMode);
        }
    }

    [Fact]
    public void Collect_WithNonNumericBoundConfig_ThrowsBeforeExecution()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            spark.Conf.Set("spark.deltasharp.execution.maxResultRows", "not-a-number");

            Assert.Throws<ArgumentException>(() => df.Collect());
            Assert.Equal(0, executor.CollectCallCount);
        }
    }

    [Fact]
    public void Collect_WithTimeoutAboveCancelAfterCeiling_ClampsInsteadOfThrowing()
    {
        var executor = new FakeQueryExecutor(Array.Empty<Row>());
        (SparkSession spark, DataFrame df) = NewBoundFrame(executor);
        using (spark)
        {
            // A timeout far above CancellationTokenSource.CancelAfter's ~49.7-day ceiling must NOT surface a
            // raw ArgumentOutOfRangeException outside the driver; it is clamped to the max supported (#176 #7).
            spark.Conf.Set("spark.deltasharp.execution.timeoutMs", long.MaxValue.ToString(CultureInfo.InvariantCulture));

            _ = df.Collect();

            Assert.NotNull(executor.LastOptions);
            Assert.Equal(TimeSpan.FromMilliseconds(uint.MaxValue - 1), executor.LastOptions!.Timeout);
        }
    }
}
