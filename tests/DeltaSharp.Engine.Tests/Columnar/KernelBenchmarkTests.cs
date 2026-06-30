using DeltaSharp.Engine.Columnar;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers the STORY-03.3.1 (#149) benchmark-metadata AC: <see cref="KernelBenchmark"/> records the batch size and
/// null density of every measurement (plus comparison selectivity), runs the documented batch-size × null-density
/// sweep, and times an allocation-free steady-state loop.
/// </summary>
public class KernelBenchmarkTests
{
    [Fact]
    public void Measure_RecordsBatchSizeAndNullDensityMetadata_ForEveryOperation()
    {
        // Keep the internal Operation enum out of the public test signature (CS0051) by iterating here.
        KernelBenchmark.Operation[] operations =
        [
            KernelBenchmark.Operation.SumInt64,
            KernelBenchmark.Operation.MinInt64,
            KernelBenchmark.Operation.MaxInt64,
            KernelBenchmark.Operation.CountNonNull,
            KernelBenchmark.Operation.CompareVector,
            KernelBenchmark.Operation.CompareScalar,
        ];

        foreach (KernelBenchmark.Operation operation in operations)
        {
            KernelBenchmark.Result result = KernelBenchmark.Measure(operation, batchSize: 1024, nullDensity: 0.25, iterations: 64);

            Assert.Equal(operation, result.Operation);
            Assert.Equal(1024, result.BatchSize);
            Assert.Equal(0.25, result.NullDensity);
            Assert.Equal(64, result.Iterations);
            Assert.True(result.Elapsed >= TimeSpan.Zero);
            Assert.Equal(BitmapOps.IsHardwareAccelerated, result.HardwareAccelerated);
            Assert.Contains("batch=", result.ToString(), StringComparison.Ordinal);
            Assert.Contains("nulls=", result.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Measure_Comparison_RecordsSelectivityInUnitInterval()
    {
        KernelBenchmark.Result result = KernelBenchmark.Measure(KernelBenchmark.Operation.CompareVector, 4096, 0.0, 32);
        Assert.InRange(result.Selectivity, 0.0, 1.0);
        Assert.True(result.Selectivity > 0.0, "a uniform < comparison over [0,1000) should select roughly half the rows");
    }

    [Fact]
    public void RunSuite_CoversBatchSizeByNullDensityGrid()
    {
        IReadOnlyList<KernelBenchmark.Result> results = KernelBenchmark.RunSuite(KernelBenchmark.Operation.SumInt64, iterations: 16);

        Assert.Equal(KernelBenchmark.DefaultBatchSizes.Length * KernelBenchmark.DefaultNullDensities.Length, results.Count);
        foreach (int batchSize in KernelBenchmark.DefaultBatchSizes)
        {
            foreach (double density in KernelBenchmark.DefaultNullDensities)
            {
                Assert.Contains(results, r => r.BatchSize == batchSize && r.NullDensity == density);
            }
        }
    }

    [Fact]
    public void Measure_SteadyStateLoop_IsAllocationFree()
    {
        // Setup (building the two columns) allocates once; the 200k-iteration timed loop must add nothing, so a
        // per-iteration allocation would blow past this bound by orders of magnitude.
        KernelBenchmark.Measure(KernelBenchmark.Operation.SumInt64, 256, 0.1, 1); // warm up + JIT

        long before = GC.GetAllocatedBytesForCurrentThread();
        KernelBenchmark.Measure(KernelBenchmark.Operation.SumInt64, 256, 0.1, 200_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated <= 16_384, $"Measure allocated {allocated} bytes for 200k iterations (expected only fixed setup).");
    }

    [Fact]
    public void Measure_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KernelBenchmark.Measure(KernelBenchmark.Operation.SumInt64, -1, 0.0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => KernelBenchmark.Measure(KernelBenchmark.Operation.SumInt64, 16, 1.5, 1));
    }
}
