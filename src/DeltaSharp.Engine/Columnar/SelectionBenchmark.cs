using System.Diagnostics;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// A lightweight, allocation-free timing harness for the selection kernels (<see cref="SelectionKernels" />,
/// STORY-03.3.2 #150 AC4). Like <see cref="NullHelperBenchmark" />/<see cref="KernelBenchmark" /> it is deliberately
/// <b>not</b> BenchmarkDotNet — that would add a package dependency and a lock file to the lock-free
/// <c>DeltaSharp.Engine</c> and pull in dynamic-codegen machinery that fights the NativeAOT posture
/// (ADR-0001/ADR-0014). Each measurement builds its predicate bitmap and the reusable <c>int[]</c> selection scratch
/// up front, warms up, then times a tight kernel loop with <see cref="Stopwatch.GetTimestamp" />, so the measured
/// region allocates zero bytes. Every <see cref="Result" /> records the <b>batch size</b> and the <b>selectivity</b>
/// the predicate was generated at, plus the active SIMD width, so a regression sweep is self-describing.
/// </summary>
internal static class SelectionBenchmark
{
    /// <summary>The selection kernels the harness can measure.</summary>
    public enum Operation
    {
        /// <summary>Bitmap → selection (<see cref="SelectionKernels.ToSelection(ReadOnlySpan{byte}, int, int, Span{int}, KernelTier)" />).</summary>
        ToSelection,

        /// <summary>Selection ∘ predicate composition (<see cref="SelectionKernels.Compose(ReadOnlySpan{int}, ReadOnlySpan{byte}, Span{int}, KernelTier)" />).</summary>
        Compose,
    }

    /// <summary>
    /// One self-describing measurement: which kernel, at what <paramref name="BatchSize" /> and target
    /// <paramref name="Selectivity" />, over how many iterations, and the timing it produced.
    /// </summary>
    /// <param name="Operation">The kernel measured.</param>
    /// <param name="BatchSize">Rows per batch (the predicate length).</param>
    /// <param name="Selectivity">Fraction of rows whose predicate bit is set, in <c>[0, 1]</c>.</param>
    /// <param name="Iterations">Number of kernel invocations timed.</param>
    /// <param name="Elapsed">Total wall time across all iterations.</param>
    /// <param name="HardwareAccelerated">Whether a SIMD tier backs the kernel on this host.</param>
    /// <param name="VectorByteWidth">Active vector lane width in bytes (1 = scalar-only host).</param>
    /// <param name="Checksum">An accumulated result so the loop cannot be optimized away.</param>
    public readonly record struct Result(
        Operation Operation,
        int BatchSize,
        double Selectivity,
        long Iterations,
        TimeSpan Elapsed,
        bool HardwareAccelerated,
        int VectorByteWidth,
        long Checksum)
    {
        /// <summary>Average nanoseconds per row scanned.</summary>
        public double NanosecondsPerRow =>
            Iterations == 0 || BatchSize == 0 ? 0 : Elapsed.TotalNanoseconds / ((double)Iterations * BatchSize);

        /// <summary>A one-line, log-friendly rendering carrying the batch-size and selectivity metadata.</summary>
        public override string ToString() =>
            $"{Operation,-12} batch={BatchSize,5} sel={Selectivity,4:0.00} " +
            $"simd={(HardwareAccelerated ? VectorByteWidth + "B" : "scalar"),-7} " +
            $"{NanosecondsPerRow,7:0.000} ns/row  (iters={Iterations})";
    }

    /// <summary>The batch sizes the default sweep covers (ADR-0001's ~1k–8k vectorized batch band).</summary>
    public static ReadOnlySpan<int> DefaultBatchSizes => [1024, 4096, 8192];

    /// <summary>The selectivities the default sweep covers (all-fail, sparse, half, dense, all-pass).</summary>
    public static ReadOnlySpan<double> DefaultSelectivities => [0.0, 0.1, 0.5, 0.9, 1.0];

    /// <summary>
    /// Measures one <paramref name="operation" /> at <paramref name="batchSize" /> rows and the target
    /// <paramref name="selectivity" />. The predicate is generated deterministically (a seeded xorshift, never
    /// <c>System.Random</c>) and every buffer — predicate, base selection, and output scratch — is allocated before the
    /// timed region, which runs <paramref name="iterations" /> kernel calls allocation-free.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is out of its valid range.</exception>
    public static Result Measure(Operation operation, int batchSize, double selectivity, long iterations, ulong seed = 0x9E3779B97F4A7C15UL)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(iterations);
        if (selectivity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(selectivity), selectivity, "Selectivity must be in [0, 1].");
        }

        ulong state = seed == 0 ? 1UL : seed;
        int byteCount = Bitmap.ByteCount(batchSize);

        // Buffers allocated up front so the timed region below is allocation-free.
        byte[] predicate = FillBitmap(byteCount, batchSize, selectivity, ref state);
        int[] selection = new int[Math.Max(1, batchSize)];
        int[] dest = new int[Math.Max(1, batchSize)];
        for (int i = 0; i < batchSize; i++)
        {
            selection[i] = i; // an identity base selection for the composition path
        }

        long checksum = 0;

        // Warm up (JIT, tier-up, branch predictor) outside the timed region.
        checksum += Invoke(operation, predicate, selection, dest, batchSize);

        long start = Stopwatch.GetTimestamp();
        for (long iteration = 0; iteration < iterations; iteration++)
        {
            checksum += Invoke(operation, predicate, selection, dest, batchSize);
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        return new Result(
            operation, batchSize, selectivity, iterations, elapsed,
            KernelTierGate.IsHardwareAccelerated, KernelTierGate.VectorByteWidth, checksum);
    }

    /// <summary>
    /// Runs <see cref="Measure" /> across <see cref="DefaultBatchSizes" /> × <see cref="DefaultSelectivities" />,
    /// returning one self-describing <see cref="Result" /> per cell.
    /// </summary>
    public static IReadOnlyList<Result> RunSuite(Operation operation, long iterations = 2000)
    {
        var results = new List<Result>(DefaultBatchSizes.Length * DefaultSelectivities.Length);
        foreach (int batchSize in DefaultBatchSizes)
        {
            foreach (double selectivity in DefaultSelectivities)
            {
                results.Add(Measure(operation, batchSize, selectivity, iterations));
            }
        }

        return results;
    }

    private static long Invoke(Operation operation, byte[] predicate, int[] selection, int[] dest, int length)
        => operation switch
        {
            Operation.ToSelection => SelectionKernels.ToSelection(predicate, 0, length, dest),
            Operation.Compose => SelectionKernels.Compose(selection, predicate, dest),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };

    /// <summary>
    /// Allocates and deterministically fills a packed bitmap so each bit is set with probability
    /// <paramref name="setProbability" />, using a seeded xorshift64 PRNG (reproducible, AOT-safe, and not
    /// <c>System.Random</c>, which is banned in production code). Padding lanes stay <c>0</c>.
    /// </summary>
    private static byte[] FillBitmap(int byteCount, int length, double setProbability, ref ulong state)
    {
        var bits = new byte[Math.Max(1, byteCount)];
        uint threshold = (uint)Math.Clamp(setProbability * uint.MaxValue, 0, uint.MaxValue);
        for (int i = 0; i < length; i++)
        {
            if (NextUInt32(ref state) < threshold)
            {
                bits[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        return bits;
    }

    /// <summary>One step of a xorshift64 generator; deterministic and dependency-free (not <c>System.Random</c>).</summary>
    private static uint NextUInt32(ref ulong state)
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return (uint)(state >> 32);
    }
}
