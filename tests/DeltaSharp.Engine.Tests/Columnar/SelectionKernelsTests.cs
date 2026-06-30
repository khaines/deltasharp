using DeltaSharp.Engine.Columnar;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers STORY-03.3.2 (#150): the bitmap → selection and selection ∘ predicate kernels
/// (<see cref="SelectionKernels" />). Every assertion uses an <b>independent</b> per-bit oracle written here in plain
/// C# (never the kernel itself), and the forced-tier theories make the <see cref="KernelTier.Vector256" /> body
/// reachable and mutation-killable even on the arm64 CI host where <see cref="KernelTier.Auto" /> folds it away.
/// </summary>
[Collection("KernelParity")]
public class SelectionKernelsTests
{
    /// <summary>Lengths straddling byte and vector (128-/256-bit) boundaries, with ±1 neighbours and a wide span.</summary>
    public static readonly int[] Lengths =
    {
        0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 255, 256, 257, 1000, 1024, 4096,
    };

    /// <summary>Source-bit offsets, including non-byte-aligned values that force the scalar lead-in.</summary>
    public static readonly int[] Offsets = { 0, 1, 3, 5, 7, 8, 9, 13, 16, 31, 33, 64, 100 };

    /// <summary>The explicitly-forced tiers (every value except <see cref="KernelTier.Auto" />).</summary>
    internal static readonly KernelTier[] ForcedTiers = { KernelTier.Scalar, KernelTier.Vector128, KernelTier.Vector256 };

    public static TheoryData<int> LengthData => new(Lengths);

    // =====================================================================================================
    // AC1 — bitmap → selection: ordered, unique, in-bounds, across offsets/tails and tiers
    // =====================================================================================================

    [Theory]
    [MemberData(nameof(LengthData))]
    public void ToSelection_MatchesPerBitOracle_AcrossOffsetsAndTiers(int length)
    {
        var rng = new Random(unchecked(0x5E1EC700 ^ (length * 31)));
        foreach (double density in new[] { 0.0, 0.1, 0.5, 0.9, 1.0 })
        {
            foreach (int offset in Offsets)
            {
                byte[] predicate = BuildBitmap(rng, offset, length, density);
                int[] expected = ScalarOracle(predicate, offset, length);

                foreach (KernelTier tier in ForcedTiers)
                {
                    var dest = new int[Math.Max(1, length)];
                    int count = SelectionKernels.ToSelection(predicate, offset, length, dest, tier);

                    Assert.Equal(expected.Length, count);
                    Assert.Equal(expected, dest[..count]);
                    AssertOrderedUniqueInBounds(dest, count, length);
                }

                // Auto must agree with the forced reference too.
                var autoDest = new int[Math.Max(1, length)];
                int autoCount = SelectionKernels.ToSelection(predicate, offset, length, autoDest, KernelTier.Auto);
                Assert.Equal(expected, autoDest[..autoCount]);
            }
        }
    }

    [Theory]
    [MemberData(nameof(LengthData))]
    public void ToSelection_EdgePredicates_AllTiersIdentical(int length)
    {
        // Empty / all-pass / all-fail / sparse (a single bit near the end) — AC3.
        byte[] allFail = NewBitmap(0, length);
        byte[] allPass = SetWindow(NewBitmap(0, length), 0, length, valueEveryBit: true);
        byte[] sparse = NewBitmap(0, length);
        if (length > 0)
        {
            Bitmap.Set(sparse, length - 1, true); // a lone set bit straddling the final partial byte/vector tail
        }

        foreach (byte[] predicate in new[] { allFail, allPass, sparse })
        {
            int[] expected = ScalarOracle(predicate, 0, length);
            foreach (KernelTier tier in ForcedTiers)
            {
                var dest = new int[Math.Max(1, length)];
                int count = SelectionKernels.ToSelection(predicate, 0, length, dest, tier);
                Assert.Equal(expected, dest[..count]);
                AssertOrderedUniqueInBounds(dest, count, length);
            }
        }

        // all-fail selects nothing; all-pass selects every row.
        Assert.Empty(ScalarOracle(allFail, 0, length));
        Assert.Equal(length, ScalarOracle(allPass, 0, length).Length);
    }

    [Fact]
    public void ToSelection_ReturnsSelectionVector_OrderedUniqueInBounds()
    {
        byte[] predicate = NewBitmap(0, 10);
        foreach (int row in new[] { 1, 4, 7, 9 })
        {
            Bitmap.Set(predicate, row, true);
        }

        SelectionVector selection = SelectionKernels.ToSelection(predicate, 0, 10);
        Assert.Equal(new[] { 1, 4, 7, 9 }, selection.Indices.ToArray());
    }

    [Fact]
    public void ToSelection_NonByteAlignedOffset_SkipsLeadInBits()
    {
        // Window starts at source bit 5; only logical rows 2 and 6 (source bits 7 and 11) are set.
        byte[] predicate = NewBitmap(0, 16);
        Bitmap.Set(predicate, 7, true);
        Bitmap.Set(predicate, 11, true);

        foreach (KernelTier tier in ForcedTiers)
        {
            var dest = new int[8];
            int count = SelectionKernels.ToSelection(predicate, offset: 5, length: 8, dest, tier);
            Assert.Equal(new[] { 2, 6 }, dest[..count]);
        }
    }

    // =====================================================================================================
    // AC2 — selection ∘ predicate composition: predicate applies over already-selected rows
    // =====================================================================================================

    [Theory]
    [MemberData(nameof(LengthData))]
    public void Compose_MatchesIndependentOracle_AcrossTiers(int length)
    {
        var rng = new Random(unchecked(0xC0FFEE ^ (length * 17)));
        int[] selection = RandomSelection(rng, length);

        foreach (double density in new[] { 0.0, 0.1, 0.5, 0.9, 1.0 })
        {
            byte[] predicate = BuildBitmap(rng, 0, selection.Length, density);
            int[] expected = ComposeOracle(selection, predicate);

            foreach (KernelTier tier in ForcedTiers)
            {
                var dest = new int[Math.Max(1, selection.Length)];
                int count = SelectionKernels.Compose(selection, predicate, dest, tier);
                Assert.Equal(expected, dest[..count]);
            }
        }
    }

    [Fact]
    public void Compose_AppliesPredicateOverAlreadySelectedRows_InOriginalRowSpace()
    {
        // Base selection keeps physical rows {2,3,5,7,8} (e.g. the survivors of a first predicate).
        var baseSelection = new SelectionVector(new[] { 2, 3, 5, 7, 8 });

        // Second predicate over the FIVE selected positions: keep positions 0, 2, 4 -> physical 2, 5, 8.
        byte[] predicate = NewBitmap(0, 5);
        Bitmap.Set(predicate, 0, true);
        Bitmap.Set(predicate, 2, true);
        Bitmap.Set(predicate, 4, true);

        SelectionVector composed = SelectionKernels.Compose(baseSelection, predicate, KernelTier.Auto);
        Assert.Equal(new[] { 2, 5, 8 }, composed.Indices.ToArray());
    }

    [Fact]
    public void Compose_EqualsApplyingBothPredicatesInOrder()
    {
        // First predicate over 24 physical rows, then a second predicate over the survivors. Composing the
        // selection of predicate-1 with the (selection-position-indexed) predicate-2 must equal selecting rows
        // that pass BOTH, computed independently.
        const int rows = 24;
        var rng = new Random(0xB07);
        byte[] p1 = BuildBitmap(rng, 0, rows, 0.5);
        int[] firstSelection = ScalarOracle(p1, 0, rows);

        byte[] p2 = BuildBitmap(rng, 0, firstSelection.Length, 0.5);
        int[] composed = ComposeOracle(firstSelection, p2);

        // Independent: a row survives iff p1[row] AND p2[its rank among p1-survivors].
        var expected = new List<int>();
        int rank = 0;
        for (int row = 0; row < rows; row++)
        {
            if (Bitmap.Get(p1, row))
            {
                if (Bitmap.Get(p2, rank))
                {
                    expected.Add(row);
                }

                rank++;
            }
        }

        Assert.Equal(expected, composed);

        var dest = new int[firstSelection.Length];
        int count = SelectionKernels.Compose(firstSelection, p2, dest, KernelTier.Auto);
        Assert.Equal(expected, dest[..count].ToArray());
    }

    [Fact]
    public void Compose_EmptySelection_YieldsEmpty()
    {
        SelectionVector composed = SelectionKernels.Compose(SelectionVector.Range(0), ReadOnlySpan<byte>.Empty, KernelTier.Auto);
        Assert.Equal(0, composed.Count);
    }

    // =====================================================================================================
    // Validation
    // =====================================================================================================

    [Fact]
    public void ToSelection_RejectsBadArguments()
    {
        var dest = new int[8];
        Assert.Throws<ArgumentOutOfRangeException>(() => SelectionKernels.ToSelection(new byte[1], -1, 4, dest));
        Assert.Throws<ArgumentOutOfRangeException>(() => SelectionKernels.ToSelection(new byte[1], 0, -1, dest));
        Assert.Throws<ArgumentException>(() => SelectionKernels.ToSelection(new byte[1], 0, 64, dest)); // predicate too short
        Assert.Throws<ArgumentException>(() => SelectionKernels.ToSelection(new byte[8], 0, 64, new int[1])); // dest too short
    }

    [Fact]
    public void Compose_RejectsBadArguments()
    {
        Assert.Throws<ArgumentException>(() => SelectionKernels.Compose(new int[64], new byte[1], new int[64])); // predicate too short
        Assert.Throws<ArgumentException>(() => SelectionKernels.Compose(new int[64], new byte[8], new int[1])); // dest too short
        Assert.Throws<ArgumentNullException>(() => SelectionKernels.Compose(null!, ReadOnlySpan<byte>.Empty, KernelTier.Auto));
    }

    // =====================================================================================================
    // Independent oracles + builders (deliberately NOT calling the kernel under test)
    // =====================================================================================================

    private static int[] ScalarOracle(ReadOnlySpan<byte> predicate, int offset, int length)
    {
        var result = new List<int>();
        for (int i = 0; i < length; i++)
        {
            if ((predicate[(offset + i) >> 3] & (1 << ((offset + i) & 7))) != 0)
            {
                result.Add(i);
            }
        }

        return result.ToArray();
    }

    private static int[] ComposeOracle(ReadOnlySpan<int> selection, ReadOnlySpan<byte> predicate)
    {
        var result = new List<int>();
        for (int p = 0; p < selection.Length; p++)
        {
            if ((predicate[p >> 3] & (1 << (p & 7))) != 0)
            {
                result.Add(selection[p]);
            }
        }

        return result.ToArray();
    }

    private static void AssertOrderedUniqueInBounds(int[] dest, int count, int length)
    {
        for (int k = 0; k < count; k++)
        {
            Assert.InRange(dest[k], 0, Math.Max(0, length - 1)); // in [0, length)
            if (k > 0)
            {
                Assert.True(dest[k] > dest[k - 1], $"indices must be strictly ascending (ordered + unique) at {k}");
            }
        }
    }

    private static byte[] NewBitmap(int offset, int length) => new byte[Math.Max(1, Bitmap.ByteCount(offset + length))];

    private static byte[] SetWindow(byte[] bitmap, int offset, int length, bool valueEveryBit)
    {
        for (int i = 0; i < length; i++)
        {
            Bitmap.Set(bitmap, offset + i, valueEveryBit);
        }

        return bitmap;
    }

    private static byte[] BuildBitmap(Random rng, int offset, int length, double density)
    {
        byte[] bitmap = NewBitmap(offset, length);

        // Pre-seed the padding/lead-in bits with random junk so the kernel must honour offset and tail bounds and
        // never leak a bit outside [offset, offset + length).
        for (int b = 0; b < bitmap.Length; b++)
        {
            bitmap[b] = (byte)rng.Next(0, 256);
        }

        for (int i = 0; i < length; i++)
        {
            Bitmap.Set(bitmap, offset + i, rng.NextDouble() < density);
        }

        return bitmap;
    }

    private static int[] RandomSelection(Random rng, int length)
    {
        // A strictly-ascending physical selection over a slightly larger row space (models prior filter survivors).
        var selection = new int[length];
        int physical = 0;
        for (int p = 0; p < length; p++)
        {
            physical += 1 + rng.Next(0, 3);
            selection[p] = physical;
        }

        return selection;
    }
}
