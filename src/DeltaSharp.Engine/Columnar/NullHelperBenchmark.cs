using System.Diagnostics;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// A lightweight, allocation-free timing harness for the branchless null helpers (STORY-02.6.2, #144
/// AC4). It is deliberately <b>not</b> BenchmarkDotNet: that would add a package dependency and a lock
/// file to the otherwise lock-free <c>DeltaSharp.Engine</c> and pull in dynamic-codegen machinery that
/// fights the NativeAOT posture (ADR-0001/ADR-0014). Instead each measurement pre-allocates its buffers,
/// warms up, then times a tight kernel loop with <see cref="Stopwatch.GetTimestamp"/> so the measured
/// region allocates zero bytes. Every <see cref="Result"/> records the <b>batch size</b> and <b>null
/// density</b> it was taken at, plus the active SIMD width, so a sweep is self-describing.
/// </summary>
internal static class NullHelperBenchmark
{
    /// <summary>The null-mask kernels the harness can measure.</summary>
    public enum Operation
    {
        /// <summary>Propagate-on-any-null binary validity AND (<see cref="NullMasks.PropagateBinary"/>).</summary>
        PropagateBinary,

        /// <summary>Kleene AND over bit-packed booleans (<see cref="NullMasks.KleeneAnd"/>).</summary>
        KleeneAnd,

        /// <summary>Kleene OR over bit-packed booleans (<see cref="NullMasks.KleeneOr"/>).</summary>
        KleeneOr,

        /// <summary>Kleene NOT over a bit-packed boolean (<see cref="NullMasks.KleeneNot"/>).</summary>
        KleeneNot,

        /// <summary>Validity null count / popcount (<see cref="BitmapOps.PopCount"/>).</summary>
        PopCount,
    }

    /// <summary>
    /// One self-describing measurement: which kernel, at what <paramref name="BatchSize"/> and
    /// <paramref name="NullDensity"/>, over how many iterations, and the timing it produced.
    /// </summary>
    /// <param name="Operation">The kernel measured.</param>
    /// <param name="BatchSize">Rows per batch (the vector length).</param>
    /// <param name="NullDensity">Fraction of null rows the input was generated at, in <c>[0, 1]</c>.</param>
    /// <param name="Iterations">Number of kernel invocations timed.</param>
    /// <param name="Elapsed">Total wall time across all iterations.</param>
    /// <param name="HardwareAccelerated">Whether a SIMD tier backed the kernel.</param>
    /// <param name="VectorByteWidth">Active vector lane width in bytes (8 = scalar <c>ulong</c> fallback).</param>
    /// <param name="Checksum">An accumulated result so the loop cannot be optimized away.</param>
    public readonly record struct Result(
        Operation Operation,
        int BatchSize,
        double NullDensity,
        long Iterations,
        TimeSpan Elapsed,
        bool HardwareAccelerated,
        int VectorByteWidth,
        long Checksum)
    {
        /// <summary>Average nanoseconds per row processed.</summary>
        public double NanosecondsPerRow =>
            Iterations == 0 || BatchSize == 0 ? 0 : Elapsed.TotalNanoseconds / ((double)Iterations * BatchSize);

        /// <summary>A one-line, log-friendly rendering carrying the batch-size and null-density metadata.</summary>
        public override string ToString() =>
            $"{Operation,-16} batch={BatchSize,5} nulls={NullDensity,4:0.00} " +
            $"simd={(HardwareAccelerated ? VectorByteWidth + "B" : "scalar"),-7} " +
            $"{NanosecondsPerRow,7:0.000} ns/row  (iters={Iterations})";
    }

    /// <summary>The batch sizes the default sweep covers (ADR-0001's ~1k–8k vectorized batch band).</summary>
    public static ReadOnlySpan<int> DefaultBatchSizes => [1024, 4096, 8192];

    /// <summary>The null densities the default sweep covers (no-null, sparse, half, dense).</summary>
    public static ReadOnlySpan<double> DefaultNullDensities => [0.0, 0.1, 0.5, 0.9];

    /// <summary>
    /// Measures one <paramref name="operation"/> at <paramref name="batchSize"/> rows and
    /// <paramref name="nullDensity"/> nulls. Inputs are generated deterministically (a seeded xorshift,
    /// never <c>System.Random</c>) and all buffers are allocated before the timed region, which runs
    /// <paramref name="iterations"/> kernel calls allocation-free.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is out of its valid range.</exception>
    public static Result Measure(Operation operation, int batchSize, double nullDensity, long iterations, ulong seed = 0x9E3779B97F4A7C15UL)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(iterations);
        if (nullDensity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(nullDensity), nullDensity, "Null density must be in [0, 1].");
        }

        int byteCount = Bitmap.ByteCount(batchSize);
        double validProbability = 1.0 - nullDensity;
        ulong state = seed == 0 ? 1UL : seed;

        // Buffers allocated up front so the timed region below is allocation-free.
        byte[] leftValues = FillBitmap(byteCount, batchSize, 0.5, ref state);
        byte[] leftValidity = FillBitmap(byteCount, batchSize, validProbability, ref state);
        byte[] rightValues = FillBitmap(byteCount, batchSize, 0.5, ref state);
        byte[] rightValidity = FillBitmap(byteCount, batchSize, validProbability, ref state);
        byte[] outValues = new byte[Math.Max(1, byteCount)];
        byte[] outValidity = new byte[Math.Max(1, byteCount)];

        long checksum = 0;

        // Warm up (JIT, tier-up, and branch predictor) outside the timed region.
        checksum += Invoke(operation, leftValues, leftValidity, rightValues, rightValidity, outValues, outValidity, batchSize);

        long start = Stopwatch.GetTimestamp();
        for (long iteration = 0; iteration < iterations; iteration++)
        {
            checksum += Invoke(operation, leftValues, leftValidity, rightValues, rightValidity, outValues, outValidity, batchSize);
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        return new Result(
            operation, batchSize, nullDensity, iterations, elapsed,
            BitmapOps.IsHardwareAccelerated, BitmapOps.VectorByteWidth, checksum);
    }

    /// <summary>
    /// Runs <see cref="Measure"/> across <see cref="DefaultBatchSizes"/> × <see cref="DefaultNullDensities"/>,
    /// returning one self-describing <see cref="Result"/> per cell.
    /// </summary>
    public static IReadOnlyList<Result> RunSuite(Operation operation, long iterations = 2000)
    {
        var results = new List<Result>(DefaultBatchSizes.Length * DefaultNullDensities.Length);
        foreach (int batchSize in DefaultBatchSizes)
        {
            foreach (double nullDensity in DefaultNullDensities)
            {
                results.Add(Measure(operation, batchSize, nullDensity, iterations));
            }
        }

        return results;
    }

    private static long Invoke(
        Operation operation,
        byte[] leftValues,
        byte[] leftValidity,
        byte[] rightValues,
        byte[] rightValidity,
        byte[] outValues,
        byte[] outValidity,
        int length)
        => operation switch
        {
            Operation.PropagateBinary =>
                NullMasks.PropagateBinary(new Validity(leftValidity, 0, length), new Validity(rightValidity, 0, length), outValidity),
            Operation.KleeneAnd =>
                NullMasks.KleeneAnd(leftValues, leftValidity, rightValues, rightValidity, outValues, outValidity, length),
            Operation.KleeneOr =>
                NullMasks.KleeneOr(leftValues, leftValidity, rightValues, rightValidity, outValues, outValidity, length),
            Operation.KleeneNot =>
                NullMasks.KleeneNot(leftValues, leftValidity, outValues, outValidity, length),
            Operation.PopCount =>
                BitmapOps.PopCount(leftValidity, length),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };

    /// <summary>
    /// Allocates and deterministically fills a packed bitmap so each bit is set with probability
    /// <paramref name="setProbability"/>, using a seeded xorshift64 PRNG (reproducible, AOT-safe, and not
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

    /// <summary>One step of a xorshift64 generator; deterministic and dependency-free.</summary>
    private static uint NextUInt32(ref ulong state)
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return (uint)(state >> 32);
    }
}
