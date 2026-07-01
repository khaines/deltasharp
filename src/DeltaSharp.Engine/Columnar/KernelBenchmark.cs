using System.Diagnostics;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// A lightweight, allocation-free timing harness for the aggregate and comparison kernels (STORY-03.3.1, #149).
/// Like <see cref="NullHelperBenchmark"/> it is deliberately <b>not</b> BenchmarkDotNet — that would add a package
/// dependency and a lock file to the lock-free <c>DeltaSharp.Engine</c> and pull in dynamic-codegen machinery that
/// fights the NativeAOT posture (ADR-0001/ADR-0014). Each measurement builds its input vectors and output buffers
/// up front, warms up, then times a tight kernel loop with <see cref="Stopwatch.GetTimestamp"/>, so the measured
/// region allocates zero bytes. Every <see cref="Result"/> records the <b>batch size</b> and <b>null density</b> it
/// was taken at (the #149 AC), plus comparison <b>selectivity</b> and the active SIMD width, so a sweep is
/// self-describing.
/// </summary>
internal static class KernelBenchmark
{
    /// <summary>The kernels the harness can measure.</summary>
    public enum Operation
    {
        /// <summary>Integral <c>SUM</c> into bigint (<see cref="AggregateKernels.SumInt64"/>).</summary>
        SumInt64,

        /// <summary>Integral <c>MIN</c> (<see cref="AggregateKernels.MinInt64"/>).</summary>
        MinInt64,

        /// <summary>Integral <c>MAX</c> (<see cref="AggregateKernels.MaxInt64"/>).</summary>
        MaxInt64,

        /// <summary>Non-null <c>COUNT</c> (<see cref="AggregateKernels.CountNonNull(ColumnVector)"/>).</summary>
        CountNonNull,

        /// <summary>Vector-vs-vector <c>&lt;</c> over bigint columns (<see cref="ComparisonKernels.Compare(ComparisonOp, ColumnVector, ColumnVector, Span{byte}, Span{byte})"/>).</summary>
        CompareVector,

        /// <summary>Vector-vs-literal <c>&lt;</c> over a bigint column (<see cref="ComparisonKernels.Compare(ComparisonOp, ColumnVector, long, Span{byte}, Span{byte})"/>).</summary>
        CompareScalar,
    }

    /// <summary>
    /// One self-describing measurement: which kernel, at what <paramref name="BatchSize"/> and
    /// <paramref name="NullDensity"/> (and realized <paramref name="Selectivity"/> for comparisons), over how many
    /// iterations, and the timing produced.
    /// </summary>
    /// <param name="Operation">The kernel measured.</param>
    /// <param name="BatchSize">Rows per batch (the vector length).</param>
    /// <param name="NullDensity">Fraction of null rows the input was generated at, in <c>[0, 1]</c>.</param>
    /// <param name="Selectivity">For comparisons, the fraction of <b>non-null</b> rows whose predicate is true (else 0).</param>
    /// <param name="Iterations">Number of kernel invocations timed.</param>
    /// <param name="Elapsed">Total wall time across all iterations.</param>
    /// <param name="HardwareAccelerated">Whether a SIMD tier backs the kernel on this host.</param>
    /// <param name="VectorByteWidth">Active vector lane width in bytes (0 = scalar-only host).</param>
    /// <param name="Checksum">An accumulated result so the loop cannot be optimized away.</param>
    public readonly record struct Result(
        Operation Operation,
        int BatchSize,
        double NullDensity,
        double Selectivity,
        long Iterations,
        TimeSpan Elapsed,
        bool HardwareAccelerated,
        int VectorByteWidth,
        long Checksum)
    {
        /// <summary>Average nanoseconds per row processed.</summary>
        public double NanosecondsPerRow =>
            Iterations == 0 || BatchSize == 0 ? 0 : Elapsed.TotalNanoseconds / ((double)Iterations * BatchSize);

        /// <summary>A one-line, log-friendly rendering carrying the batch-size, null-density, and selectivity metadata.</summary>
        public override string ToString() =>
            $"{Operation,-13} batch={BatchSize,5} nulls={NullDensity,4:0.00} sel={Selectivity,4:0.00} " +
            $"simd={(HardwareAccelerated ? VectorByteWidth + "B" : "scalar"),-7} " +
            $"{NanosecondsPerRow,7:0.000} ns/row  (iters={Iterations})";
    }

    /// <summary>The batch sizes the default sweep covers (ADR-0001's ~1k–8k vectorized batch band).</summary>
    public static ReadOnlySpan<int> DefaultBatchSizes => [1024, 4096, 8192];

    /// <summary>The null densities the default sweep covers (no-null, sparse, half, dense).</summary>
    public static ReadOnlySpan<double> DefaultNullDensities => [0.0, 0.1, 0.5, 0.9];

    /// <summary>
    /// Measures one <paramref name="operation"/> at <paramref name="batchSize"/> rows and <paramref name="nullDensity"/>
    /// nulls. Inputs are generated deterministically (a seeded xorshift, never <c>System.Random</c>) and every buffer is
    /// allocated before the timed region, which runs <paramref name="iterations"/> kernel calls allocation-free.
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

        ulong state = seed == 0 ? 1UL : seed;
        double validProbability = 1.0 - nullDensity;
        int byteCount = Bitmap.ByteCount(batchSize);

        // Inputs span [0, 1000): comparing against the midpoint 500 yields ~50% selectivity. Built once, off the clock.
        ColumnVector left = BuildLongColumn(batchSize, validProbability, range: 1000, ref state);
        ColumnVector right = BuildLongColumn(batchSize, validProbability, range: 1000, ref state);
        const long literal = 500;
        byte[] outValues = new byte[Math.Max(1, byteCount)];
        byte[] outValidity = new byte[Math.Max(1, byteCount)];

        long checksum = 0;
        double selectivity = 0;

        // Warm up (JIT, tier-up, branch predictor) outside the timed region; also realize the comparison selectivity.
        checksum += Invoke(operation, left, right, literal, outValues, outValidity);
        if (operation is Operation.CompareVector or Operation.CompareScalar)
        {
            int trueRows = BitmapOps.PopCount(outValues, batchSize);
            int validRows = BitmapOps.PopCount(outValidity, batchSize);
            selectivity = validRows == 0 ? 0 : (double)trueRows / validRows;
        }

        long start = Stopwatch.GetTimestamp();
        for (long iteration = 0; iteration < iterations; iteration++)
        {
            checksum += Invoke(operation, left, right, literal, outValues, outValidity);
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        return new Result(
            operation, batchSize, nullDensity, selectivity, iterations, elapsed,
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

    private static long Invoke(Operation operation, ColumnVector left, ColumnVector right, long literal, byte[] outValues, byte[] outValidity)
        => operation switch
        {
            Operation.SumInt64 => AggregateKernels.SumInt64(left, AnsiMode.Ansi).GetValueOrDefault(),
            Operation.MinInt64 => AggregateKernels.MinInt64(left).GetValueOrDefault(),
            Operation.MaxInt64 => AggregateKernels.MaxInt64(left).GetValueOrDefault(),
            Operation.CountNonNull => AggregateKernels.CountNonNull(left),
            Operation.CompareVector => ComparisonKernels.Compare(ComparisonOp.LessThan, left, right, outValues, outValidity),
            Operation.CompareScalar => ComparisonKernels.Compare(ComparisonOp.LessThan, left, literal, outValues, outValidity),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };

    /// <summary>
    /// Builds a <c>bigint</c> column of <paramref name="length"/> rows where each row is valid with probability
    /// <paramref name="validProbability"/> (else null) and valid values are a seeded xorshift draw in
    /// <c>[0, range)</c>. Allocates (off the timed path); the returned vector is no-null when
    /// <paramref name="validProbability"/> is <c>1</c>, so it exercises the SIMD fast path.
    /// </summary>
    private static ColumnVector BuildLongColumn(int length, double validProbability, long range, ref ulong state)
    {
        MutableColumnVector vector = ColumnVectors.Create(LongType.Instance, length);
        uint validThreshold = (uint)Math.Clamp(validProbability * uint.MaxValue, 0, uint.MaxValue);
        for (int i = 0; i < length; i++)
        {
            bool valid = NextUInt32(ref state) < validThreshold;
            if (valid)
            {
                vector.AppendValue((long)(NextUInt32(ref state) % (ulong)range));
            }
            else
            {
                vector.AppendNull();
            }
        }

        return vector;
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
