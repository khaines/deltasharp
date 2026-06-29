using DeltaSharp.Engine.Columnar;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers STORY-02.6.2 (#144) AC4: the lightweight timing harness (<see cref="NullHelperBenchmark"/>)
/// runs each kernel and records self-describing metadata — most importantly the <b>batch size</b> and
/// <b>null density</b> — and its measured region is allocation-free.
/// </summary>
public class NullHelperBenchmarkTests
{
    [Fact]
    public void Measure_RecordsBatchSizeAndNullDensityMetadata_ForEveryOperation()
    {
        const int batchSize = 4096;
        const double nullDensity = 0.25;

        // Keep the internal Operation enum out of the public test signature (CS0051) by iterating here.
        NullHelperBenchmark.Operation[] operations =
        [
            NullHelperBenchmark.Operation.PropagateBinary,
            NullHelperBenchmark.Operation.KleeneAnd,
            NullHelperBenchmark.Operation.KleeneOr,
            NullHelperBenchmark.Operation.KleeneNot,
            NullHelperBenchmark.Operation.PopCount,
        ];

        foreach (NullHelperBenchmark.Operation operation in operations)
        {
            NullHelperBenchmark.Result result = NullHelperBenchmark.Measure(operation, batchSize, nullDensity, iterations: 50);

            Assert.Equal(operation, result.Operation);
            Assert.Equal(batchSize, result.BatchSize);          // AC4: batch size recorded
            Assert.Equal(nullDensity, result.NullDensity);      // AC4: null density recorded
            Assert.Equal(50, result.Iterations);
            Assert.True(result.Elapsed >= TimeSpan.Zero);
            Assert.True(result.NanosecondsPerRow >= 0);
            Assert.Equal(BitmapOps.IsHardwareAccelerated, result.HardwareAccelerated);

            // The metadata renders into a single line carrying both dimensions.
            string line = result.ToString();
            Assert.Contains("batch=", line, StringComparison.Ordinal);
            Assert.Contains("nulls=", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RunSuite_CoversTheBatchSizeAndNullDensityGrid()
    {
        IReadOnlyList<NullHelperBenchmark.Result> results =
            NullHelperBenchmark.RunSuite(NullHelperBenchmark.Operation.KleeneAnd, iterations: 20);

        int expectedCells = NullHelperBenchmark.DefaultBatchSizes.Length * NullHelperBenchmark.DefaultNullDensities.Length;
        Assert.Equal(expectedCells, results.Count);

        // Every (batch size, null density) cell is present and self-describing.
        foreach (int batchSize in NullHelperBenchmark.DefaultBatchSizes)
        {
            foreach (double nullDensity in NullHelperBenchmark.DefaultNullDensities)
            {
                Assert.Contains(results, r => r.BatchSize == batchSize && r.NullDensity == nullDensity);
            }
        }
    }

    [Fact]
    public void Measure_TimedRegion_IsAllocationFree()
    {
        // A high iteration count makes any per-iteration allocation visible against the fixed setup cost.
        NullHelperBenchmark.Result warm = NullHelperBenchmark.Measure(
            NullHelperBenchmark.Operation.KleeneAnd, batchSize: 2048, nullDensity: 0.5, iterations: 1);
        Assert.True(warm.Iterations == 1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        NullHelperBenchmark.Result result = NullHelperBenchmark.Measure(
            NullHelperBenchmark.Operation.KleeneAnd, batchSize: 2048, nullDensity: 0.5, iterations: 100_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Setup allocates a handful of small buffers; the 100k-iteration timed loop must add nothing.
        Assert.True(allocated <= 4096, $"Measure allocated {allocated} bytes for 100k iterations (expected only fixed setup)");
        Assert.Equal(100_000, result.Iterations);
    }

    [Fact]
    public void Measure_Checksum_ReflectsActualWork()
    {
        // A dense-null KleeneAnd produces many null lanes, so the accumulated null-count checksum is
        // non-zero — proof the timed loop is not dead-code-eliminated.
        NullHelperBenchmark.Result result = NullHelperBenchmark.Measure(
            NullHelperBenchmark.Operation.KleeneAnd, batchSize: 1024, nullDensity: 0.9, iterations: 10);

        Assert.True(result.Checksum > 0, "expected a non-zero null-count checksum from a dense-null batch");
    }

    [Fact]
    public void Measure_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NullHelperBenchmark.Measure(NullHelperBenchmark.Operation.KleeneAnd, -1, 0.5, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NullHelperBenchmark.Measure(NullHelperBenchmark.Operation.KleeneAnd, 1024, 1.5, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NullHelperBenchmark.Measure(NullHelperBenchmark.Operation.KleeneAnd, 1024, 0.5, -10));
    }
}
