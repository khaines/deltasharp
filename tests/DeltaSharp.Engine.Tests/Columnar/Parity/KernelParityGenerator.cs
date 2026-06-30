using DeltaSharp.Engine.Columnar;

namespace DeltaSharp.Engine.Tests.Columnar.Parity;

/// <summary>
/// The five orthogonal input dimensions a generated case varies (STORY-03.5.1 AC1): the element
/// <b>type</b> is covered by running every family on the one case (int32, int64, boolean/3VL, packed
/// validity), so this record carries the remaining four numeric dimensions plus the seed for diagnostics.
/// </summary>
/// <param name="BatchSize">Logical row count <c>n</c>; swept to hit sub-byte, byte, and vector-width tails (AC1 batch size).</param>
/// <param name="NullDensity">Approximate fraction of SQL <c>NULL</c> / UNKNOWN lanes (AC1 null density).</param>
/// <param name="SelectionDensity">Approximate fraction of set predicate/selection bits (AC1 selection density).</param>
/// <param name="Offset">A bit offset into the predicate/validity window, incl. non-byte-aligned values (AC1 offset).</param>
internal sealed record KernelCaseDimensions(int BatchSize, double NullDensity, double SelectionDensity, int Offset)
{
    /// <summary>A compact one-line rendering for the AC4 mismatch diagnostic's schema/dims field.</summary>
    public string Describe() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"batchSize={BatchSize} nullDensity={NullDensity:0.000} selectionDensity={SelectionDensity:0.000} offset={Offset}");
}

/// <summary>
/// One fully-synthesized parity case: every input array each kernel family needs to run, derived
/// deterministically from <see cref="Seed"/> so the case is replayable from its seed alone.
/// </summary>
internal sealed class GeneratedKernelCase
{
    /// <summary>The seed this case was generated from (emitted in every diagnostic).</summary>
    public required ulong Seed { get; init; }

    /// <summary>The four numeric dimensions this case realizes.</summary>
    public required KernelCaseDimensions Dimensions { get; init; }

    // --- aggregate / comparison: int32 (and the date physical layout) ---
    public required int[] Int32Left { get; init; }

    public required int[] Int32Right { get; init; }

    public required int Int32Scalar { get; init; }

    // --- aggregate / comparison: int64 (and the timestamp physical layout) ---
    public required long[] Int64Left { get; init; }

    public required long[] Int64Right { get; init; }

    public required long Int64Scalar { get; init; }

    // --- selection ---

    /// <summary>An LSB-first predicate bitmap covering at least <c>Offset + BatchSize</c> bits.</summary>
    public required byte[] Predicate { get; init; }

    /// <summary>An ascending, unique base selection of physical indices in <c>[0, BatchSize)</c>.</summary>
    public required int[] Selection { get; init; }

    /// <summary>An LSB-first predicate over the <see cref="Selection"/> positions (covers <c>Selection.Length</c> bits).</summary>
    public required byte[] ComposePredicate { get; init; }

    // --- null masks / bitmap (NullMaskTier) ---

    /// <summary>A packed validity bitmap of <c>ByteCount(BatchSize)</c> bytes (bit set ⇒ non-null).</summary>
    public required byte[] ValidityA { get; init; }

    public required byte[] ValidityB { get; init; }

    /// <summary>Three-valued-logic boolean lanes for the Kleene kernels (null = UNKNOWN).</summary>
    public required bool?[] KleeneLeft { get; init; }

    public required bool?[] KleeneRight { get; init; }
}

/// <summary>
/// The deterministic, seeded synthesizer behind the cross-family parity suite (STORY-03.5.1). A single
/// <see cref="Generate(ulong)"/> draws (via <see cref="KernelParityRng"/>, SplitMix64) a batch size, null
/// density, selection density, and offset, then materializes every per-family input so one seed exercises
/// <b>all</b> kernel families at once — the cross-family analogue of #154's per-expression generator.
/// </summary>
internal static class KernelParityGenerator
{
    /// <summary>
    /// Boundary batch sizes: empty, single, sub-byte tails, exact byte multiples ±1, and multiples of the
    /// 16-/32-byte vector widths ±1, so a forced tier's vector body, its narrower tail, and the scalar
    /// remainder are all exercised. A seed either picks one of these or a uniform random length (below).
    /// </summary>
    private static readonly int[] BoundaryLengths =
    {
        0, 1, 2, 3, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65,
        127, 128, 129, 255, 256, 257, 511, 512, 513, 1000, 1023, 1024, 1025, 2048, 4096,
    };

    private static readonly double[] Densities = { 0.0, 0.03, 0.1, 0.25, 0.5, 0.75, 0.9, 0.97, 1.0 };

    /// <summary>Synthesizes the case for <paramref name="seed"/> (identical across runs and runtimes).</summary>
    public static GeneratedKernelCase Generate(ulong seed)
    {
        var rng = new KernelParityRng(seed);

        // --- batch size: half boundary-driven, half uniform random over [0, 4096] ---
        int n = rng.NextBool() ? rng.Pick(BoundaryLengths) : rng.Next(0, 4097);

        // --- densities: half curated, half uniform random ---
        double nullDensity = rng.NextBool() ? rng.Pick(Densities) : rng.NextDouble();
        double selectionDensity = rng.NextBool() ? rng.Pick(Densities) : rng.NextDouble();

        // --- offset: bit offset into the predicate/validity window (incl. non-byte-aligned) ---
        int offset = rng.Next(0, 24);

        var dims = new KernelCaseDimensions(n, nullDensity, selectionDensity, offset);

        return new GeneratedKernelCase
        {
            Seed = seed,
            Dimensions = dims,
            Int32Left = RandomInts(rng, n),
            Int32Right = RandomInts(rng, n),
            Int32Scalar = RandomInt(rng),
            Int64Left = RandomLongs(rng, n),
            Int64Right = RandomLongs(rng, n),
            Int64Scalar = RandomLong(rng),
            Predicate = RandomBitmap(rng, offset + n, selectionDensity),
            Selection = RandomSelection(rng, n, selectionDensity, out int selCount),
            ComposePredicate = RandomBitmap(rng, selCount, selectionDensity),
            ValidityA = RandomBitmap(rng, n, 1.0 - nullDensity),
            ValidityB = RandomBitmap(rng, n, 1.0 - nullDensity),
            KleeneLeft = RandomLanes(rng, n, nullDensity),
            KleeneRight = RandomLanes(rng, n, nullDensity),
        };
    }

    // --- primitive draws (extreme values injected at low probability to stress min/max and widening) ---

    private static int RandomInt(KernelParityRng rng) => rng.Next(8) switch
    {
        0 => int.MinValue,
        1 => int.MaxValue,
        2 => 0,
        _ => rng.NextInt(-1000, 1000),
    };

    private static long RandomLong(KernelParityRng rng) => rng.Next(8) switch
    {
        0 => long.MinValue,
        1 => long.MaxValue,
        2 => 0,
        _ => rng.NextLong(-1_000_000, 1_000_000),
    };

    private static int[] RandomInts(KernelParityRng rng, int length)
    {
        var values = new int[length];
        for (int i = 0; i < length; i++)
        {
            values[i] = RandomInt(rng);
        }

        return values;
    }

    private static long[] RandomLongs(KernelParityRng rng, int length)
    {
        var values = new long[length];
        for (int i = 0; i < length; i++)
        {
            values[i] = RandomLong(rng);
        }

        return values;
    }

    /// <summary>An LSB-first bitmap of <c>ByteCount(bitCount)</c> bytes; each bit set with probability <paramref name="setDensity"/>.</summary>
    private static byte[] RandomBitmap(KernelParityRng rng, int bitCount, double setDensity)
    {
        var bits = new byte[Math.Max(1, Bitmap.ByteCount(bitCount))];
        for (int i = 0; i < bitCount; i++)
        {
            if (rng.NextDouble() < setDensity)
            {
                Bitmap.Set(bits, i, true);
            }
        }

        return bits;
    }

    /// <summary>An ascending, unique base selection in <c>[0, length)</c>; each index kept with probability <paramref name="density"/>.</summary>
    private static int[] RandomSelection(KernelParityRng rng, int length, double density, out int count)
    {
        var scratch = new int[length];
        count = 0;
        for (int i = 0; i < length; i++)
        {
            if (rng.NextDouble() < density)
            {
                scratch[count++] = i;
            }
        }

        var selection = new int[count];
        Array.Copy(scratch, selection, count);
        return selection;
    }

    /// <summary><paramref name="length"/> three-valued-logic lanes at approximately <paramref name="nullDensity"/> nulls.</summary>
    private static bool?[] RandomLanes(KernelParityRng rng, int length, double nullDensity)
    {
        var lanes = new bool?[length];
        for (int i = 0; i < length; i++)
        {
            lanes[i] = rng.NextDouble() < nullDensity ? null : rng.NextBool();
        }

        return lanes;
    }
}
