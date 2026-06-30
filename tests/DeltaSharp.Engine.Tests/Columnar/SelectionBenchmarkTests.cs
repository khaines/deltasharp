using DeltaSharp.Engine.Columnar;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers STORY-03.3.2 (#150) AC4: the lightweight timing harness (<see cref="SelectionBenchmark" />) sweeps the
/// selection kernels across <b>batch size</b> and <b>selectivity</b>, records self-describing throughput metadata for
/// regression gating, and its measured region is allocation-free on the hot path.
/// </summary>
public class SelectionBenchmarkTests
{
    [Fact]
    public void Measure_RecordsBatchSizeAndSelectivityMetadata_ForEveryOperation()
    {
        const int batchSize = 4096;
        const double selectivity = 0.5;

        SelectionBenchmark.Operation[] operations =
        [
            SelectionBenchmark.Operation.ToSelection,
            SelectionBenchmark.Operation.Compose,
        ];

        foreach (SelectionBenchmark.Operation operation in operations)
        {
            SelectionBenchmark.Result result = SelectionBenchmark.Measure(operation, batchSize, selectivity, iterations: 50);

            Assert.Equal(operation, result.Operation);
            Assert.Equal(batchSize, result.BatchSize);        // AC4: batch size recorded
            Assert.Equal(selectivity, result.Selectivity);    // AC4: selectivity recorded
            Assert.Equal(50, result.Iterations);
            Assert.True(result.Elapsed >= TimeSpan.Zero);
            Assert.True(result.NanosecondsPerRow >= 0);        // AC4: throughput recorded for regression gating
            Assert.Equal(KernelTierGate.IsHardwareAccelerated, result.HardwareAccelerated);

            string line = result.ToString();
            Assert.Contains("batch=", line, StringComparison.Ordinal);
            Assert.Contains("sel=", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RunSuite_CoversTheBatchSizeAndSelectivityGrid()
    {
        IReadOnlyList<SelectionBenchmark.Result> results =
            SelectionBenchmark.RunSuite(SelectionBenchmark.Operation.ToSelection, iterations: 20);

        int expectedCells = SelectionBenchmark.DefaultBatchSizes.Length * SelectionBenchmark.DefaultSelectivities.Length;
        Assert.Equal(expectedCells, results.Count);

        foreach (int batchSize in SelectionBenchmark.DefaultBatchSizes)
        {
            foreach (double selectivity in SelectionBenchmark.DefaultSelectivities)
            {
                Assert.Contains(results, r => r.BatchSize == batchSize && r.Selectivity == selectivity);
            }
        }
    }

    [Fact]
    public void Measure_TimedRegion_IsAllocationFree()
    {
        // Cover BOTH kernels (ToSelection and Compose) so a per-invocation allocation regression on either hot path
        // is caught. The internal Operation enum is kept out of the public test signature (CS0051) by iterating here,
        // matching NullHelperBenchmarkTests' convention.
        foreach (SelectionBenchmark.Operation operation in
            new[] { SelectionBenchmark.Operation.ToSelection, SelectionBenchmark.Operation.Compose })
        {
            // A high iteration count makes any per-iteration allocation visible against the fixed setup cost. The
            // 100k-iteration timed loop runs long enough that the background JIT can promote Measure to tier-1
            // *mid-measurement*; that one-time OSR/recompile allocates on this thread and, under parallel test load,
            // intermittently inflates the first reading (a load/timing artifact, not a real per-iteration allocation).
            // Poll until a steady-state (tier-1) pass measures only the fixed setup cost, tolerating the one-time
            // tier-up transient — the same pattern NullHelperBenchmarkTests.Measure_TimedRegion_IsAllocationFree uses.
            const int maxAttempts = 50;
            long allocated = long.MaxValue;
            SelectionBenchmark.Result result = default;
            for (int attempt = 0; attempt < maxAttempts && allocated > 4096; attempt++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                result = SelectionBenchmark.Measure(
                    operation, batchSize: 256, selectivity: 0.5, iterations: 100_000);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (allocated > 4096)
                {
                    Thread.Sleep(5); // let the background JIT promote Measure to tier-1, then re-measure
                }
            }

            // Setup allocates a handful of small int/byte buffers (~2 KB at 256 rows); the 100k-iteration timed loop
            // must add nothing (a per-iteration leak of even a few bytes would total hundreds of KB here).
            Assert.True(
                allocated <= 4096,
                $"Measure({operation}) allocated {allocated} bytes for 100k iterations after {maxAttempts} attempts (expected only fixed setup)");
            Assert.Equal(100_000, result.Iterations);
        }
    }

    [Fact]
    public void Measure_Checksum_ReflectsActualWork()
    {
        // A half-selective predicate over 1024 rows selects hundreds of indices, so the accumulated selected-count
        // checksum is non-zero — proof the timed loop is not dead-code-eliminated.
        SelectionBenchmark.Result result = SelectionBenchmark.Measure(
            SelectionBenchmark.Operation.ToSelection, batchSize: 1024, selectivity: 0.5, iterations: 10);

        Assert.True(result.Checksum > 0, "expected a non-zero selected-count checksum from a half-selective batch");
    }

    [Fact]
    public void Measure_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SelectionBenchmark.Measure(SelectionBenchmark.Operation.ToSelection, -1, 0.5, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SelectionBenchmark.Measure(SelectionBenchmark.Operation.ToSelection, 1024, 1.5, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SelectionBenchmark.Measure(SelectionBenchmark.Operation.ToSelection, 1024, 0.5, -10));
    }
}
