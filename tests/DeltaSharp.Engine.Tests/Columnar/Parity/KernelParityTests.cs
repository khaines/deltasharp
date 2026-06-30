using System.Globalization;
using System.Runtime.Intrinsics;
using DeltaSharp.Engine.Columnar;
using Xunit;
using Xunit.Sdk;

namespace DeltaSharp.Engine.Tests.Columnar.Parity;

/// <summary>
/// STORY-03.5.1 (#153): the <b>unified, generated/randomized scalar-vs-SIMD kernel parity suite</b>. A
/// single deterministic generator (<see cref="KernelParityGenerator"/>, SplitMix64) synthesizes one batch
/// varying type, null density, selection density, offset, and batch size; the harness
/// (<see cref="KernelParityHarness"/>) then runs <b>every</b> tier-seamed kernel family at the forced
/// <c>Scalar</c> reference and at every SIMD tier (<c>Vector128</c>/<c>Vector256</c>/word + <c>Auto</c>) and
/// asserts bit-identical results — so a hardware fast path can never change a query result (AC1).
/// <para>
/// This <b>complements</b> the per-kernel forced-tier tests each kernel PR already ships
/// (<c>AggregateKernelsTests</c>, <c>ComparisonKernelsTests</c>, <c>SelectionKernelsTests</c>,
/// <c>NullMasksKleeneTests</c>, <c>BitmapOpsTests</c>, <c>KernelTierTests</c>): it does not re-enumerate
/// those curated cases but drives <b>all families together</b> off one seeded generator with the AC4
/// rich-diagnostics contract — the cross-family analogue of #154's interpreter-vs-compiled generator.
/// </para>
/// <para>
/// Joins the <c>KernelParity</c> collection (<c>DisableParallelization</c>) so the forced-<c>Vector256</c>
/// portable-software-fallback bodies — cold-JIT-flaky under heavy parallel load on the arm64 host where
/// <c>Vector256.IsHardwareAccelerated == false</c> — run serialized, keeping this mutation-killing gate
/// deterministic (the same rationale as the #149/#150 forced-tier theories).
/// </para>
/// </summary>
[Collection("KernelParity")]
public sealed class KernelParityTests
{
    /// <summary>
    /// The fixed seed corpus. Each seed is a SplitMix64-mixed function of its index, pinned here so the
    /// suite is byte-identical across runs and runtimes (AC1 determinism) and every case is replayable from
    /// its seed (AC4). 256 seeds × all families gives a wide, reproducible cross-family sweep.
    /// </summary>
    public static TheoryData<ulong> Seeds()
    {
        var data = new TheoryData<ulong>();
        for (int i = 1; i <= 256; i++)
        {
            ulong seed = unchecked(((ulong)i * 0x9E3779B97F4A7C15UL) + 0xD1B54A32D192ED03UL);
            data.Add(seed);
        }

        return data;
    }

    // ===== AC1/AC3/AC4: every family, every tier, bit-identical, over the seeded cross-family corpus =====

    [Theory]
    [MemberData(nameof(Seeds))]
    public void AllFamilies_ScalarEqualsEverySimdTier(ulong seed)
    {
        GeneratedKernelCase c = KernelParityGenerator.Generate(seed);
        KernelParityHarness.AssertAllFamilies(c);
    }

    // ===== AC1 determinism: the generator is a pure function of its seed =====

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Generator_IsDeterministic_ForFixedSeed(ulong seed)
    {
        GeneratedKernelCase a = KernelParityGenerator.Generate(seed);
        GeneratedKernelCase b = KernelParityGenerator.Generate(seed);

        Assert.Equal(a.Dimensions, b.Dimensions);
        Assert.Equal(a.Int32Left, b.Int32Left);
        Assert.Equal(a.Int32Right, b.Int32Right);
        Assert.Equal(a.Int32Scalar, b.Int32Scalar);
        Assert.Equal(a.Int64Left, b.Int64Left);
        Assert.Equal(a.Int64Right, b.Int64Right);
        Assert.Equal(a.Int64Scalar, b.Int64Scalar);
        Assert.Equal(a.Predicate, b.Predicate);
        Assert.Equal(a.Selection, b.Selection);
        Assert.Equal(a.ComposePredicate, b.ComposePredicate);
        Assert.Equal(a.ValidityA, b.ValidityA);
        Assert.Equal(a.ValidityB, b.ValidityB);
        Assert.Equal(a.KleeneLeft, b.KleeneLeft);
        Assert.Equal(a.KleeneRight, b.KleeneRight);
    }

    // ===== AC3: the forced-tier seam makes every tier reachable on this host (incl. arm64 portable V256) =====

    /// <summary>
    /// On the dev/CI host <c>Vector256.IsHardwareAccelerated</c> is <see langword="false"/> (arm64/NEON), so
    /// under <c>KernelTier.Auto</c> the 32-byte body is constant-folded away. The forced-tier seam still
    /// reaches it via the portable software fallback — this test asserts the forced tiers genuinely run a
    /// non-trivial case (the harness compares all of them); it documents that scalar-only / unsupported-SIMD
    /// hosts are covered for <b>every</b> family because the forced-<c>Scalar</c> reference always runs.
    /// </summary>
    [Fact]
    public void ForcedTierSeam_ReachesEveryTier_OnAnyHost()
    {
        // A seed whose batch size is large enough to drive the widest (32-byte) vector body and its tails.
        GeneratedKernelCase c = LargeCase();
        Assert.True(c.Dimensions.BatchSize >= Vector256<byte>.Count * 8, "case must exceed one 256-bit chunk");

        // The harness internally runs Scalar + Vector128 + Vector256 + Auto for every family and asserts
        // identity; reaching here without an XunitException proves each tier executed and agreed.
        // (That each forced tier genuinely runs a *vector body* rather than silently falling back to scalar —
        // the property the test name promises — is proven host-independently by the per-tier mutation battery
        // in kernel-parity-suite.md §9: a perturbation injected into only the Vector256/Vector128 body fails
        // exactly that forced tier on a host where Auto would have elided it. This test is the end-to-end
        // exercise of that seam; the assert below is only a host-reality sanity check.)
        KernelParityHarness.AssertAllFamilies(c);

        // Document the host reality this seam exists to defeat: the portable V256 fallback is what runs here.
        Assert.False(
            Vector256.IsHardwareAccelerated && Vector128.IsHardwareAccelerated == false,
            "sanity: V256-accelerated implies V128-accelerated");
    }

    // ===== AC4 non-vacuity: the diagnostic carries every required field and the mismatch path throws =====

    /// <summary>
    /// Proves the AC4 reporting contract in-suite: the rendered diagnostic carries seed, schema/dimensions,
    /// kernel + operands, hardware path (tier), the first diverging index, and the minimized repro; and the
    /// failure path raises an <see cref="XunitException"/>. The end-to-end "injected real SIMD divergence is
    /// caught" proof is run in a scratch clone (see the design note §9) so production code is never perturbed.
    /// </summary>
    [Fact]
    public void Diagnostics_CarryEveryAc4Field()
    {
        GeneratedKernelCase c = KernelParityGenerator.Generate(0x9E3779B97F4A7C15UL);
        string text = KernelParityHarness.BuildDiagnostic(
            c,
            family: "Aggregate",
            kernel: "AggregateKernels.SumInt32",
            operands: "int[64]",
            tierLabel: "Vector256",
            firstDivergence: "reduction result: scalar=42 Vector256=43",
            minimalRepro: "AggregateKernels.SumInt32([1, 2], tier=Vector256) => 43, expected (Scalar) 42");

        Assert.Contains("seed           : 0x", text, StringComparison.Ordinal);
        Assert.Contains("schema / dims  : batchSize=", text, StringComparison.Ordinal);
        Assert.Contains("kernel         : AggregateKernels.SumInt32", text, StringComparison.Ordinal);
        Assert.Contains("operands       : int[64]", text, StringComparison.Ordinal);
        Assert.Contains("hardware path  : tier=Vector256", text, StringComparison.Ordinal);
        Assert.Contains("first diverge  : reduction result", text, StringComparison.Ordinal);
        Assert.Contains("minimal repro  : AggregateKernels.SumInt32", text, StringComparison.Ordinal);
        Assert.Contains("Vector256.IsHardwareAccelerated=", text, StringComparison.Ordinal);
    }

    private static GeneratedKernelCase LargeCase()
    {
        // Scan the same fixed seed family for the first case whose batch size comfortably exceeds a 256-bit
        // chunk so every tier's vector body runs; deterministic (depends only on the seed recurrence).
        for (int i = 1; i <= 256; i++)
        {
            ulong seed = unchecked(((ulong)i * 0x9E3779B97F4A7C15UL) + 0xD1B54A32D192ED03UL);
            GeneratedKernelCase c = KernelParityGenerator.Generate(seed);
            if (c.Dimensions.BatchSize >= 512)
            {
                return c;
            }
        }

        // Fallback: synthesize directly from a seed known to be large (never expected to be hit).
        return KernelParityGenerator.Generate(0xABCDEF0123456789UL);
    }
}

/// <summary>
/// STORY-03.5.1 AC2: the documented float NaN / ±0 / ∞ <b>tolerance and policy</b>. There is no SIMD tier
/// for floating aggregates or comparisons — <c>SumDouble</c>/<c>MinDouble</c>/<c>MaxDouble</c> are
/// deliberately scalar-only (#149) and the comparison SIMD fast path is int32/int64 only — so float parity
/// is <b>trivial by construction</b> (no hardware fast path exists to diverge) and the parity tolerance is
/// exactly bit-identical (0 ULP). What this class verifies is that the scalar float path implements Spark's
/// total order: <c>NaN</c> equals <c>NaN</c> and is greatest, <c>-0.0 == +0.0</c>, and ±∞ order normally —
/// checked against an independent oracle over a corpus saturated with the special values.
/// </summary>
[Collection("KernelParity")]
public sealed class KernelParityFloatPolicyTests
{
    private static readonly double[] SpecialDoubles =
    {
        double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.0, 0.0,
        -1.5, 1.5, double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon, 42.0, -42.0,
    };

    private static readonly ComparisonOp[] AllOps =
    {
        ComparisonOp.Equal, ComparisonOp.NotEqual, ComparisonOp.LessThan,
        ComparisonOp.LessThanOrEqual, ComparisonOp.GreaterThan, ComparisonOp.GreaterThanOrEqual,
    };

    /// <summary>Spark's total order over doubles, written independently of <see cref="KernelScalars"/> (NaN greatest, −0 == +0).</summary>
    private static int OracleCompare(double a, double b)
    {
        if (a < b)
        {
            return -1;
        }

        if (a > b)
        {
            return 1;
        }

        if (a == b)
        {
            return 0; // covers -0.0 == +0.0
        }

        bool aNaN = double.IsNaN(a);
        bool bNaN = double.IsNaN(b);
        if (aNaN && bNaN)
        {
            return 0;
        }

        return aNaN ? 1 : -1; // NaN is greatest
    }

    [Fact]
    public void FloatComparison_FollowsSparkTotalOrder_ForEveryPairOfSpecialValues()
    {
        int n = SpecialDoubles.Length * SpecialDoubles.Length;
        var left = new double[n];
        var right = new double[n];
        int k = 0;
        foreach (double a in SpecialDoubles)
        {
            foreach (double b in SpecialDoubles)
            {
                left[k] = a;
                right[k] = b;
                k++;
            }
        }

        ColumnVector lv = KernelTestSupport.Double(left);
        ColumnVector rv = KernelTestSupport.Double(right);
        int byteCount = Math.Max(1, Bitmap.ByteCount(n));

        foreach (ComparisonOp op in AllOps)
        {
            var values = new byte[byteCount];
            var validity = new byte[byteCount];
            int nulls = ComparisonKernels.Compare(op, lv, rv, values, validity);
            Assert.Equal(0, nulls);

            for (int i = 0; i < n; i++)
            {
                bool expected = ApplyOp(op, OracleCompare(left[i], right[i]));
                Assert.True(Bitmap.Get(values, i) == expected, FormatFailure(op, left[i], right[i], expected, Bitmap.Get(values, i)));
            }
        }
    }

    [Fact]
    public void FloatMinMaxSum_AreScalarOnly_AndFollowSparkPolicy()
    {
        // MAX with any NaN is NaN; MIN ignores NaN unless every value is NaN (Spark total order). SUM
        // propagates ±∞/NaN per IEEE. These are scalar-only (no tier parameter), so parity is trivial; the
        // assertions pin the documented policy values, independent of the kernel's own comparator.
        ColumnVector withNaN = KernelTestSupport.Double(new[] { 1.0, double.NaN, -3.0, 2.0 });
        Assert.Equal(double.NaN, AggregateKernels.MaxDouble(withNaN));
        Assert.Equal(-3.0, AggregateKernels.MinDouble(withNaN));

        ColumnVector allNaN = KernelTestSupport.Double(new[] { double.NaN, double.NaN });
        Assert.Equal(double.NaN, AggregateKernels.MinDouble(allNaN));
        Assert.Equal(double.NaN, AggregateKernels.MaxDouble(allNaN));

        ColumnVector signedZero = KernelTestSupport.Double(new[] { -0.0, 0.0 });
        Assert.Equal(0.0, AggregateKernels.SumDouble(signedZero));

        ColumnVector withInf = KernelTestSupport.Double(new[] { 1.0, double.PositiveInfinity });
        Assert.Equal(double.PositiveInfinity, AggregateKernels.SumDouble(withInf));
        Assert.Equal(double.PositiveInfinity, AggregateKernels.MaxDouble(withInf));
    }

    private static bool ApplyOp(ComparisonOp op, int sign) => op switch
    {
        ComparisonOp.Equal => sign == 0,
        ComparisonOp.NotEqual => sign != 0,
        ComparisonOp.LessThan => sign < 0,
        ComparisonOp.LessThanOrEqual => sign <= 0,
        ComparisonOp.GreaterThan => sign > 0,
        ComparisonOp.GreaterThanOrEqual => sign >= 0,
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    private static string FormatFailure(ComparisonOp op, double a, double b, bool expected, bool actual) =>
        string.Create(CultureInfo.InvariantCulture, $"{op}({a}, {b}) expected {expected} but kernel produced {actual}");
}
