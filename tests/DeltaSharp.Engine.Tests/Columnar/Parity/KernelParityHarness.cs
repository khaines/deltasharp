using System.Globalization;
using System.Runtime.Intrinsics;
using System.Text;
using DeltaSharp.Engine.Columnar;
using Xunit.Sdk;

namespace DeltaSharp.Engine.Tests.Columnar.Parity;

/// <summary>
/// The cross-family parity engine for STORY-03.5.1 (#153). Given one
/// <see cref="GeneratedKernelCase"/> it runs <b>every</b> tier-seamed kernel family
/// (aggregate reductions, comparisons, selection, null masks) at the forced <c>Scalar</c> reference and at
/// every SIMD tier (and <c>Auto</c>), asserting:
/// <list type="number">
///   <item>the forced-<c>Scalar</c> reference equals an <b>independent</b> in-test oracle (catches a bug
///   shared by all tiers — i.e. a scalar-reference or whole-kernel drift), and</item>
///   <item>every SIMD tier is <b>bit-identical</b> to that scalar reference (catches a hardware-fast-path-only
///   divergence — the AC1 contract).</item>
/// </list>
/// Any mismatch throws an <see cref="XunitException"/> carrying the full AC4 replay diagnostics:
/// seed, schema/dimensions, kernel + operands, hardware path (tier), the first diverging index, and a
/// <b>minimized</b> repro (the smallest input prefix that still diverges). The oracles here are written from
/// scratch (plain truth tables / loops) — deliberately <b>not</b> reusing <see cref="KernelScalars"/> or
/// <see cref="NullPropagation"/> — so a co-mutated helper cannot make a parity assertion vacuously pass.
/// <para>
/// Tolerance is exactly <b>bit-identical (0 ULP)</b> across all tiers: the only floating aggregates and
/// comparisons are deliberately scalar-only (#149), so there is no SIMD reassociation anywhere in the
/// columnar kernel surface and no epsilon is needed or permitted. The float NaN / ±0 / ∞ total-order policy
/// is validated separately in <see cref="KernelParityFloatPolicyTests"/>.
/// </para>
/// </summary>
internal static class KernelParityHarness
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The SIMD tiers (and <c>Auto</c>) compared against the forced <c>Scalar</c> reference for the typed-value kernels.</summary>
    public static readonly KernelTier[] SimdTiers = { KernelTier.Vector128, KernelTier.Vector256, KernelTier.Auto };

    /// <summary>The word/SIMD tiers (and <c>Auto</c>) compared against the forced <c>Scalar</c> reference for the null-mask kernels.</summary>
    public static readonly NullMaskTier[] NullSimdTiers =
        { NullMaskTier.Word, NullMaskTier.Vector128, NullMaskTier.Vector256, NullMaskTier.Auto };

    private static readonly ComparisonOp[] AllOps =
    {
        ComparisonOp.Equal, ComparisonOp.NotEqual, ComparisonOp.LessThan,
        ComparisonOp.LessThanOrEqual, ComparisonOp.GreaterThan, ComparisonOp.GreaterThanOrEqual,
    };

    /// <summary>Runs the whole cross-family parity battery on one generated case.</summary>
    public static void AssertAllFamilies(GeneratedKernelCase c)
    {
        AssertAggregateParity(c);
        AssertComparisonParity(c);
        AssertSelectionParity(c);
        AssertNullMaskParity(c);
    }

    // =====================================================================================================
    // Aggregate reductions (KernelTier): SUM/MIN/MAX over int32 and MIN/MAX over int64.
    // =====================================================================================================

    private static void AssertAggregateParity(GeneratedKernelCase c)
    {
        int n = c.Dimensions.BatchSize;

        AssertInt32Reduction(c, "AggregateKernels.SumInt32", c.Int32Left, n,
            static (v, len, t) => AggregateKernels.SumInt32(v.AsSpan(0, len), t),
            static (v, len) => OracleSum(v, len));

        if (n > 0)
        {
            AssertInt32Reduction(c, "AggregateKernels.MinInt32", c.Int32Left, n,
                static (v, len, t) => AggregateKernels.MinInt32(v.AsSpan(0, len), t),
                static (v, len) => OracleMin(v, len));

            AssertInt32Reduction(c, "AggregateKernels.MaxInt32", c.Int32Left, n,
                static (v, len, t) => AggregateKernels.MaxInt32(v.AsSpan(0, len), t),
                static (v, len) => OracleMax(v, len));

            AssertInt64Reduction(c, "AggregateKernels.MinInt64", c.Int64Left, n,
                static (v, len, t) => AggregateKernels.MinInt64(v.AsSpan(0, len), t),
                static (v, len) => OracleMin(v, len));

            AssertInt64Reduction(c, "AggregateKernels.MaxInt64", c.Int64Left, n,
                static (v, len, t) => AggregateKernels.MaxInt64(v.AsSpan(0, len), t),
                static (v, len) => OracleMax(v, len));
        }
    }

    private delegate long Int32ReductionRun(int[] values, int len, KernelTier tier);

    private delegate long Int64ReductionRun(long[] values, int len, KernelTier tier);

    private static void AssertInt32Reduction(
        GeneratedKernelCase c, string kernel, int[] values, int n, Int32ReductionRun run, Func<int[], int, long> oracle)
    {
        string operands = $"int[{n}]";
        long reference = run(values, n, KernelTier.Scalar);

        // (1) scalar reference vs independent oracle.
        long oracled = oracle(values, n);
        if (reference != oracled)
        {
            int min = MinimalPrefix(n, len => run(values, len, KernelTier.Scalar) != oracle(values, len));
            Fail(c, "Aggregate", kernel, operands, "Scalar(reference)-vs-oracle",
                $"scalar={reference.ToString(Inv)} oracle={oracled.ToString(Inv)}",
                $"{kernel}({FormatInts(values, 0, min)}, tier=Scalar) => {run(values, min, KernelTier.Scalar).ToString(Inv)}, oracle => {oracle(values, min).ToString(Inv)}");
        }

        // (2) every SIMD tier vs the scalar reference.
        foreach (KernelTier tier in SimdTiers)
        {
            long actual = run(values, n, tier);
            if (actual != reference)
            {
                int min = MinimalPrefix(n, len => run(values, len, tier) != run(values, len, KernelTier.Scalar));
                Fail(c, "Aggregate", kernel, operands, tier.ToString(),
                    $"reduction result: scalar={reference.ToString(Inv)} {tier}={actual.ToString(Inv)}",
                    $"{kernel}({FormatInts(values, 0, min)}, tier={tier}) => {run(values, min, tier).ToString(Inv)}, expected (Scalar) {run(values, min, KernelTier.Scalar).ToString(Inv)}");
            }
        }
    }

    private static void AssertInt64Reduction(
        GeneratedKernelCase c, string kernel, long[] values, int n, Int64ReductionRun run, Func<long[], int, long> oracle)
    {
        string operands = $"long[{n}]";
        long reference = run(values, n, KernelTier.Scalar);

        long oracled = oracle(values, n);
        if (reference != oracled)
        {
            int min = MinimalPrefix(n, len => run(values, len, KernelTier.Scalar) != oracle(values, len));
            Fail(c, "Aggregate", kernel, operands, "Scalar(reference)-vs-oracle",
                $"scalar={reference.ToString(Inv)} oracle={oracled.ToString(Inv)}",
                $"{kernel}({FormatLongs(values, 0, min)}, tier=Scalar) => {run(values, min, KernelTier.Scalar).ToString(Inv)}, oracle => {oracle(values, min).ToString(Inv)}");
        }

        foreach (KernelTier tier in SimdTiers)
        {
            long actual = run(values, n, tier);
            if (actual != reference)
            {
                int min = MinimalPrefix(n, len => run(values, len, tier) != run(values, len, KernelTier.Scalar));
                Fail(c, "Aggregate", kernel, operands, tier.ToString(),
                    $"reduction result: scalar={reference.ToString(Inv)} {tier}={actual.ToString(Inv)}",
                    $"{kernel}({FormatLongs(values, 0, min)}, tier={tier}) => {run(values, min, tier).ToString(Inv)}, expected (Scalar) {run(values, min, KernelTier.Scalar).ToString(Inv)}");
            }
        }
    }

    // =====================================================================================================
    // Comparison kernels (KernelTier): the six ops, vector-vs-vector and vector-vs-scalar, int32 and int64.
    // =====================================================================================================

    private static void AssertComparisonParity(GeneratedKernelCase c)
    {
        int n = c.Dimensions.BatchSize;
        foreach (ComparisonOp op in AllOps)
        {
            AssertBitmapKernel(c, $"ComparisonKernels.CompareInt32(vv,{op})", $"op={op}, int[{n}] vs int[{n}]", n,
                (len, tier, dest) => ComparisonKernels.CompareInt32(op, c.Int32Left.AsSpan(0, len), c.Int32Right.AsSpan(0, len), dest, tier),
                (len, dest) => OracleCompareInt32(op, c.Int32Left, c.Int32Right, len, dest),
                rowInput: i => $"left={c.Int32Left[i].ToString(Inv)} right={c.Int32Right[i].ToString(Inv)}");

            AssertBitmapKernel(c, $"ComparisonKernels.CompareInt32(vs,{op})", $"op={op}, int[{n}] vs scalar {c.Int32Scalar.ToString(Inv)}", n,
                (len, tier, dest) => ComparisonKernels.CompareInt32(op, c.Int32Left.AsSpan(0, len), c.Int32Scalar, dest, tier),
                (len, dest) => OracleCompareInt32Scalar(op, c.Int32Left, c.Int32Scalar, len, dest),
                rowInput: i => $"left={c.Int32Left[i].ToString(Inv)} scalar={c.Int32Scalar.ToString(Inv)}");

            AssertBitmapKernel(c, $"ComparisonKernels.CompareInt64(vv,{op})", $"op={op}, long[{n}] vs long[{n}]", n,
                (len, tier, dest) => ComparisonKernels.CompareInt64(op, c.Int64Left.AsSpan(0, len), c.Int64Right.AsSpan(0, len), dest, tier),
                (len, dest) => OracleCompareInt64(op, c.Int64Left, c.Int64Right, len, dest),
                rowInput: i => $"left={c.Int64Left[i].ToString(Inv)} right={c.Int64Right[i].ToString(Inv)}");

            AssertBitmapKernel(c, $"ComparisonKernels.CompareInt64(vs,{op})", $"op={op}, long[{n}] vs scalar {c.Int64Scalar.ToString(Inv)}", n,
                (len, tier, dest) => ComparisonKernels.CompareInt64(op, c.Int64Left.AsSpan(0, len), c.Int64Scalar, dest, tier),
                (len, dest) => OracleCompareInt64Scalar(op, c.Int64Left, c.Int64Scalar, len, dest),
                rowInput: i => $"left={c.Int64Left[i].ToString(Inv)} scalar={c.Int64Scalar.ToString(Inv)}");
        }
    }

    private delegate void BitmapRun(int len, KernelTier tier, Span<byte> dest);

    private delegate void BitmapOracle(int len, Span<byte> dest);

    /// <summary>
    /// Asserts a comparison kernel that writes one packed result bitmap is bit-identical across tiers and to
    /// the oracle. The output is canonical (padding 0), so byte-equality over <c>ByteCount(len)</c> is exact.
    /// </summary>
    private static void AssertBitmapKernel(
        GeneratedKernelCase c, string kernel, string operands, int n, BitmapRun run, BitmapOracle oracle, Func<int, string> rowInput)
    {
        int byteCount = Bitmap.ByteCount(n);
        var reference = new byte[Math.Max(1, byteCount)];
        run(n, KernelTier.Scalar, reference);

        byte[] oracled = OracleBitmap(oracle, n);
        int oracleDiff = FirstDiffBit(reference, oracled, n);
        if (oracleDiff >= 0)
        {
            int min = MinimalPrefix(n, len => FirstDiffBit(RunBitmap(run, len, KernelTier.Scalar), OracleBitmap(oracle, len), len) >= 0);
            Fail(c, "Comparison", kernel, operands, "Scalar(reference)-vs-oracle",
                $"row {oracleDiff}: {rowInput(oracleDiff)} → scalar={BitAt(reference, oracleDiff)} oracle={BitAt(oracled, oracleDiff)}",
                $"first diverging row {oracleDiff} of {min}-row prefix; row inputs: {rowInput(oracleDiff)}");
        }

        foreach (KernelTier tier in SimdTiers)
        {
            byte[] actual = RunBitmap(run, n, tier);
            int diff = FirstDiffBit(reference, actual, n);
            if (diff >= 0)
            {
                int min = MinimalPrefix(n, len => FirstDiffBit(RunBitmap(run, len, tier), RunBitmap(run, len, KernelTier.Scalar), len) >= 0);
                Fail(c, "Comparison", kernel, operands, tier.ToString(),
                    $"row {diff}: {rowInput(diff)} → scalar={BitAt(reference, diff)} {tier}={BitAt(actual, diff)}",
                    $"first diverging row {diff} within a {min}-row prefix; row inputs: {rowInput(diff)}");
            }
        }
    }

    // =====================================================================================================
    // Selection kernels (KernelTier): ToSelection (bitmap→indices, varied offset) and Compose (selection∘predicate).
    // =====================================================================================================

    private static void AssertSelectionParity(GeneratedKernelCase c)
    {
        int n = c.Dimensions.BatchSize;
        int offset = c.Dimensions.Offset;

        AssertSelectionKernel(c, "SelectionKernels.ToSelection", $"predicate[{n} bits] @ offset {offset}", n,
            (len, tier, dest) => SelectionKernels.ToSelection(c.Predicate, offset, len, dest.AsSpan(0, len), tier),
            len => OracleToSelection(c.Predicate, offset, len));

        int sel = c.Selection.Length;
        AssertSelectionKernel(c, "SelectionKernels.Compose", $"selection[{sel}] ∘ predicate", sel,
            (len, tier, dest) => SelectionKernels.Compose(c.Selection.AsSpan(0, len), c.ComposePredicate, dest.AsSpan(0, len), tier),
            len => OracleCompose(c.Selection, c.ComposePredicate, len));
    }

    private delegate int SelectionRun(int len, KernelTier tier, int[] dest);

    private static void AssertSelectionKernel(
        GeneratedKernelCase c, string kernel, string operands, int n, SelectionRun run, Func<int, int[]> oracle)
    {
        int[] reference = RunSelection(run, n, KernelTier.Scalar);

        int[] oracled = oracle(n);
        if (!IndicesEqual(reference, oracled))
        {
            int min = MinimalPrefix(n, len => !IndicesEqual(RunSelection(run, len, KernelTier.Scalar), oracle(len)));
            Fail(c, "Selection", kernel, operands, "Scalar(reference)-vs-oracle",
                $"scalar produced {reference.Length} indices, oracle {oracled.Length}: {FirstSelectionDiff(reference, oracled)}",
                $"{kernel} over {min}-row prefix: scalar={FormatInts(reference, 0, Math.Min(reference.Length, 16))} oracle={FormatInts(oracled, 0, Math.Min(oracled.Length, 16))}");
        }

        foreach (KernelTier tier in SimdTiers)
        {
            int[] actual = RunSelection(run, n, tier);
            if (!IndicesEqual(reference, actual))
            {
                int min = MinimalPrefix(n, len => !IndicesEqual(RunSelection(run, len, KernelTier.Scalar), RunSelection(run, len, tier)));
                Fail(c, "Selection", kernel, operands, tier.ToString(),
                    $"scalar produced {reference.Length} indices, {tier} {actual.Length}: {FirstSelectionDiff(reference, actual)}",
                    $"{kernel} over {min}-row prefix: scalar={FormatInts(reference, 0, Math.Min(reference.Length, 16))} {tier}={FormatInts(actual, 0, Math.Min(actual.Length, 16))}");
            }
        }
    }

    private static int[] RunSelection(SelectionRun run, int len, KernelTier tier)
    {
        var dest = new int[Math.Max(1, len)];
        int count = run(len, tier, dest);
        var result = new int[count];
        Array.Copy(dest, result, count);
        return result;
    }

    // =====================================================================================================
    // Null-mask kernels (NullMaskTier): BitmapOps.And, Kleene AND/OR/NOT, and PropagateBinary (the And path).
    // =====================================================================================================

    private static void AssertNullMaskParity(GeneratedKernelCase c)
    {
        int n = c.Dimensions.BatchSize;

        AssertBitmapAndParity(c, n);
        AssertKleeneParity(c, "NullMasks.KleeneAnd", n, c.KleeneLeft, c.KleeneRight, OracleKleeneAnd, KernelKleeneAnd);
        AssertKleeneParity(c, "NullMasks.KleeneOr", n, c.KleeneLeft, c.KleeneRight, OracleKleeneOr, KernelKleeneOr);
        AssertKleeneParity(c, "NullMasks.KleeneNot", n, c.KleeneLeft, null, OracleKleeneNot, KernelKleeneNot);
        AssertPropagateBinaryParity(c, n);
    }

    private static void AssertBitmapAndParity(GeneratedKernelCase c, int n)
    {
        const string Kernel = "BitmapOps.And";
        string operands = $"validityA[{n}] & validityB[{n}]";
        int byteCount = Math.Max(1, Bitmap.ByteCount(n));

        byte[] reference = AndAt(c, n, NullMaskTier.Scalar);
        byte[] oracled = OracleAnd(c.ValidityA, c.ValidityB, n);
        int oracleDiff = FirstDiffBit(reference, oracled, byteCount * 8);
        if (oracleDiff >= 0)
        {
            int min = MinimalPrefix(n, len => FirstDiffBit(AndAt(c, len, NullMaskTier.Scalar), OracleAnd(c.ValidityA, c.ValidityB, len), Math.Max(1, Bitmap.ByteCount(len)) * 8) >= 0);
            Fail(c, "NullMask", Kernel, operands, "Scalar(reference)-vs-oracle",
                $"bit {oracleDiff}: scalar={BitAt(reference, oracleDiff)} oracle={BitAt(oracled, oracleDiff)}",
                $"And over {min}-bit prefix; a[{oracleDiff >> 3}]=0x{reference[oracleDiff >> 3]:X2}");
        }

        foreach (NullMaskTier tier in NullSimdTiers)
        {
            byte[] actual = AndAt(c, n, tier);
            int diff = FirstDiffBit(reference, actual, byteCount * 8);
            if (diff >= 0)
            {
                int min = MinimalPrefix(n, len => FirstDiffBit(AndAt(c, len, NullMaskTier.Scalar), AndAt(c, len, tier), Math.Max(1, Bitmap.ByteCount(len)) * 8) >= 0);
                Fail(c, "NullMask", Kernel, operands, tier.ToString(),
                    $"bit {diff}: scalar={BitAt(reference, diff)} {tier}={BitAt(actual, diff)}",
                    $"And over {min}-bit prefix diverges at bit {diff}");
            }
        }
    }

    private static byte[] AndAt(GeneratedKernelCase c, int len, NullMaskTier tier)
    {
        int byteCount = Math.Max(1, Bitmap.ByteCount(len));
        var dest = new byte[byteCount];
        BitmapOps.And(c.ValidityA, c.ValidityB, dest, len, tier);
        return dest;
    }

    private delegate KleeneResult KleeneKernel(byte[] lv, byte[] lval, byte[] rv, byte[] rval, int len, NullMaskTier tier);

    private readonly record struct KleeneResult(byte[] Values, byte[] Validity, int Nulls);

    private static void AssertKleeneParity(
        GeneratedKernelCase c, string kernel, int n, bool?[] left, bool?[]? right, Func<bool?[], bool?[]?, int, KleeneResult> oracle, KleeneKernel run)
    {
        string operands = right is null ? $"NOT left[{n}]" : $"left[{n}] {kernel["NullMasks.Kleene".Length..]} right[{n}]";

        KleeneResult reference = RunKleene(run, left, right, n, NullMaskTier.Scalar);
        KleeneResult oracled = oracle(left, right, n);
        int oracleDiff = FirstKleeneDiff(reference, oracled, n);
        if (oracleDiff >= 0 || reference.Nulls != oracled.Nulls)
        {
            int idx = oracleDiff < 0 ? 0 : oracleDiff;
            int min = MinimalPrefix(n, len => KleeneDiffers(RunKleene(run, left, right, len, NullMaskTier.Scalar), oracle(left, right, len), len));
            Fail(c, "NullMask", kernel, operands, "Scalar(reference)-vs-oracle",
                $"lane {idx}: scalar=({KleeneLane(reference, idx)}, nulls={reference.Nulls}) oracle=({KleeneLane(oracled, idx)}, nulls={oracled.Nulls})",
                $"{kernel} over {min}-lane prefix; lane {idx} input: left={Lane(left, idx)}{(right is null ? string.Empty : $" right={Lane(right, idx)}")}");
        }

        foreach (NullMaskTier tier in NullSimdTiers)
        {
            KleeneResult actual = RunKleene(run, left, right, n, tier);
            int diff = FirstKleeneDiff(reference, actual, n);
            if (diff >= 0 || reference.Nulls != actual.Nulls)
            {
                int idx = diff < 0 ? 0 : diff;
                int min = MinimalPrefix(n, len => KleeneDiffers(RunKleene(run, left, right, len, NullMaskTier.Scalar), RunKleene(run, left, right, len, tier), len));
                Fail(c, "NullMask", kernel, operands, tier.ToString(),
                    $"lane {idx}: scalar=({KleeneLane(reference, idx)}, nulls={reference.Nulls}) {tier}=({KleeneLane(actual, idx)}, nulls={actual.Nulls})",
                    $"{kernel} over {min}-lane prefix; lane {idx} input: left={Lane(left, idx)}{(right is null ? string.Empty : $" right={Lane(right, idx)}")}");
            }
        }
    }

    private static KleeneResult RunKleene(KleeneKernel run, bool?[] left, bool?[]? right, int len, NullMaskTier tier)
    {
        (byte[] lv, byte[] lval) = EncodePacked(left, len);
        (byte[] rv, byte[] rval) = right is null ? (Array.Empty<byte>(), Array.Empty<byte>()) : EncodePacked(right, len);
        return run(lv, lval, right is null ? lv : rv, right is null ? lval : rval, len, tier);
    }

    private static KleeneResult KernelKleeneAnd(byte[] lv, byte[] lval, byte[] rv, byte[] rval, int len, NullMaskTier tier)
    {
        int bc = Math.Max(1, Bitmap.ByteCount(len));
        var values = new byte[bc];
        var validity = new byte[bc];
        int nulls = NullMasks.KleeneAnd(lv, lval, rv, rval, values, validity, len, tier);
        return new KleeneResult(values, validity, nulls);
    }

    private static KleeneResult KernelKleeneOr(byte[] lv, byte[] lval, byte[] rv, byte[] rval, int len, NullMaskTier tier)
    {
        int bc = Math.Max(1, Bitmap.ByteCount(len));
        var values = new byte[bc];
        var validity = new byte[bc];
        int nulls = NullMasks.KleeneOr(lv, lval, rv, rval, values, validity, len, tier);
        return new KleeneResult(values, validity, nulls);
    }

    private static KleeneResult KernelKleeneNot(byte[] lv, byte[] lval, byte[] rv, byte[] rval, int len, NullMaskTier tier)
    {
        int bc = Math.Max(1, Bitmap.ByteCount(len));
        var values = new byte[bc];
        var validity = new byte[bc];
        int nulls = NullMasks.KleeneNot(lv, lval, values, validity, len, tier);
        return new KleeneResult(values, validity, nulls);
    }

    private static void AssertPropagateBinaryParity(GeneratedKernelCase c, int n)
    {
        // Offset 0 is byte-aligned, so PropagateBinary takes its BitmapOps.And SIMD hot path (a bit-unaligned
        // Validity offset deterministically defers to the scalar reference for every tier — parity-trivial,
        // documented in the design note). This validates the public propagate entry over the And tier seam.
        const string Kernel = "NullMasks.PropagateBinary";
        string operands = $"validity(A[{n}]) & validity(B[{n}])";
        var left = new Validity(c.ValidityA, 0, n);
        var right = new Validity(c.ValidityB, 0, n);

        byte[] reference = PropagateAt(left, right, n, NullMaskTier.Scalar, out int refNulls);
        byte[] oracled = OracleAnd(c.ValidityA, c.ValidityB, n);
        int oracleNulls = OracleNullCount(oracled, n);
        int oracleDiff = FirstDiffBit(reference, oracled, Math.Max(1, Bitmap.ByteCount(n)) * 8);
        if (oracleDiff >= 0 || refNulls != oracleNulls)
        {
            Fail(c, "NullMask", Kernel, operands, "Scalar(reference)-vs-oracle",
                $"bit {Math.Max(0, oracleDiff)}: scalar nulls={refNulls} oracle nulls={oracleNulls}",
                $"PropagateBinary over {n} lanes; validityA[0]=0x{c.ValidityA[0]:X2} validityB[0]=0x{c.ValidityB[0]:X2}");
        }

        foreach (NullMaskTier tier in NullSimdTiers)
        {
            byte[] actual = PropagateAt(left, right, n, tier, out int nulls);
            int diff = FirstDiffBit(reference, actual, Math.Max(1, Bitmap.ByteCount(n)) * 8);
            if (diff >= 0 || nulls != refNulls)
            {
                Fail(c, "NullMask", Kernel, operands, tier.ToString(),
                    $"bit {Math.Max(0, diff)}: scalar nulls={refNulls} {tier} nulls={nulls}",
                    $"PropagateBinary over {n} lanes diverges at bit {Math.Max(0, diff)}");
            }
        }
    }

    private static byte[] PropagateAt(Validity left, Validity right, int n, NullMaskTier tier, out int nulls)
    {
        var dest = new byte[Math.Max(1, Bitmap.ByteCount(n))];
        nulls = NullMasks.PropagateBinary(left, right, dest, tier);
        return dest;
    }

    // =====================================================================================================
    // Independent oracles (plain truth tables / loops — NOT KernelScalars / NullPropagation).
    // =====================================================================================================

    private static long OracleSum(int[] v, int len)
    {
        long total = 0;
        for (int i = 0; i < len; i++)
        {
            total += v[i];
        }

        return total;
    }

    private static long OracleMin(int[] v, int len)
    {
        int best = v[0];
        for (int i = 1; i < len; i++)
        {
            if (v[i] < best)
            {
                best = v[i];
            }
        }

        return best;
    }

    private static long OracleMax(int[] v, int len)
    {
        int best = v[0];
        for (int i = 1; i < len; i++)
        {
            if (v[i] > best)
            {
                best = v[i];
            }
        }

        return best;
    }

    private static long OracleMin(long[] v, int len)
    {
        long best = v[0];
        for (int i = 1; i < len; i++)
        {
            if (v[i] < best)
            {
                best = v[i];
            }
        }

        return best;
    }

    private static long OracleMax(long[] v, int len)
    {
        long best = v[0];
        for (int i = 1; i < len; i++)
        {
            if (v[i] > best)
            {
                best = v[i];
            }
        }

        return best;
    }

    private static bool OracleSign(ComparisonOp op, int sign) => op switch
    {
        ComparisonOp.Equal => sign == 0,
        ComparisonOp.NotEqual => sign != 0,
        ComparisonOp.LessThan => sign < 0,
        ComparisonOp.LessThanOrEqual => sign <= 0,
        ComparisonOp.GreaterThan => sign > 0,
        ComparisonOp.GreaterThanOrEqual => sign >= 0,
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    private static void OracleCompareInt32(ComparisonOp op, int[] l, int[] r, int len, Span<byte> dest)
    {
        for (int i = 0; i < len; i++)
        {
            int sign = l[i] < r[i] ? -1 : l[i] > r[i] ? 1 : 0;
            if (OracleSign(op, sign))
            {
                Bitmap.Set(dest, i, true);
            }
        }
    }

    private static void OracleCompareInt32Scalar(ComparisonOp op, int[] l, int scalar, int len, Span<byte> dest)
    {
        for (int i = 0; i < len; i++)
        {
            int sign = l[i] < scalar ? -1 : l[i] > scalar ? 1 : 0;
            if (OracleSign(op, sign))
            {
                Bitmap.Set(dest, i, true);
            }
        }
    }

    private static void OracleCompareInt64(ComparisonOp op, long[] l, long[] r, int len, Span<byte> dest)
    {
        for (int i = 0; i < len; i++)
        {
            int sign = l[i] < r[i] ? -1 : l[i] > r[i] ? 1 : 0;
            if (OracleSign(op, sign))
            {
                Bitmap.Set(dest, i, true);
            }
        }
    }

    private static void OracleCompareInt64Scalar(ComparisonOp op, long[] l, long scalar, int len, Span<byte> dest)
    {
        for (int i = 0; i < len; i++)
        {
            int sign = l[i] < scalar ? -1 : l[i] > scalar ? 1 : 0;
            if (OracleSign(op, sign))
            {
                Bitmap.Set(dest, i, true);
            }
        }
    }

    private static int[] OracleToSelection(byte[] predicate, int offset, int len)
    {
        var dest = new int[Math.Max(1, len)];
        int count = 0;
        for (int i = 0; i < len; i++)
        {
            if (Bitmap.Get(predicate, offset + i))
            {
                dest[count++] = i;
            }
        }

        var result = new int[count];
        Array.Copy(dest, result, count);
        return result;
    }

    private static int[] OracleCompose(int[] selection, byte[] predicate, int len)
    {
        var dest = new int[Math.Max(1, len)];
        int count = 0;
        for (int p = 0; p < len; p++)
        {
            if (Bitmap.Get(predicate, p))
            {
                dest[count++] = selection[p];
            }
        }

        var result = new int[count];
        Array.Copy(dest, result, count);
        return result;
    }

    private static byte[] OracleAnd(byte[] a, byte[] b, int len)
    {
        var dest = new byte[Math.Max(1, Bitmap.ByteCount(len))];
        for (int i = 0; i < len; i++)
        {
            if (Bitmap.Get(a, i) && Bitmap.Get(b, i))
            {
                Bitmap.Set(dest, i, true);
            }
        }

        return dest;
    }

    private static int OracleNullCount(byte[] validity, int len)
    {
        int nulls = 0;
        for (int i = 0; i < len; i++)
        {
            if (!Bitmap.Get(validity, i))
            {
                nulls++;
            }
        }

        return nulls;
    }

    private static KleeneResult OracleKleeneAnd(bool?[] left, bool?[]? right, int len) => OracleKleene(left, right!, len, And3);

    private static KleeneResult OracleKleeneOr(bool?[] left, bool?[]? right, int len) => OracleKleene(left, right!, len, Or3);

    private static KleeneResult OracleKleeneNot(bool?[] left, bool?[]? right, int len)
    {
        int bc = Math.Max(1, Bitmap.ByteCount(len));
        var values = new byte[bc];
        var validity = new byte[bc];
        int nulls = 0;
        for (int i = 0; i < len; i++)
        {
            bool? res = left[i] is bool b ? !b : null;
            Encode(values, validity, i, res, ref nulls);
        }

        return new KleeneResult(values, validity, nulls);
    }

    private static KleeneResult OracleKleene(bool?[] left, bool?[] right, int len, Func<bool?, bool?, bool?> op)
    {
        int bc = Math.Max(1, Bitmap.ByteCount(len));
        var values = new byte[bc];
        var validity = new byte[bc];
        int nulls = 0;
        for (int i = 0; i < len; i++)
        {
            Encode(values, validity, i, op(left[i], right[i]), ref nulls);
        }

        return new KleeneResult(values, validity, nulls);
    }

    private static void Encode(byte[] values, byte[] validity, int i, bool? res, ref int nulls)
    {
        if (res is bool b)
        {
            Bitmap.Set(validity, i, true);
            if (b)
            {
                Bitmap.Set(values, i, true);
            }
        }
        else
        {
            nulls++;
        }
    }

    // Independent Kleene 3VL truth tables (TRUE/FALSE/UNKNOWN), not reusing NullPropagation.
    private static bool? And3(bool? a, bool? b)
    {
        if (a == false || b == false)
        {
            return false;
        }

        if (a is null || b is null)
        {
            return null;
        }

        return true;
    }

    private static bool? Or3(bool? a, bool? b)
    {
        if (a == true || b == true)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return null;
        }

        return false;
    }

    // =====================================================================================================
    // Comparison / minimization / formatting plumbing.
    // =====================================================================================================

    /// <summary>The smallest prefix length in <c>[1, n]</c> for which <paramref name="diverges"/> is true (the minimized repro).</summary>
    private static int MinimalPrefix(int n, Func<int, bool> diverges)
    {
        for (int len = 1; len <= n; len++)
        {
            if (diverges(len))
            {
                return len;
            }
        }

        return n;
    }

    private static byte[] AllocBitmap(int len) => new byte[Math.Max(1, Bitmap.ByteCount(len))];

    private static byte[] RunBitmap(BitmapRun run, int len, KernelTier tier)
    {
        byte[] dest = AllocBitmap(len);
        run(len, tier, dest);
        return dest;
    }

    private static byte[] OracleBitmap(BitmapOracle oracle, int len)
    {
        byte[] dest = AllocBitmap(len);
        oracle(len, dest);
        return dest;
    }

    private static int FirstDiffBit(byte[] a, byte[] b, int bitLen)
    {
        for (int i = 0; i < bitLen; i++)
        {
            if (Bitmap.Get(a, i) != Bitmap.Get(b, i))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IndicesEqual(int[] a, int[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string FirstSelectionDiff(int[] a, int[] b)
    {
        int min = Math.Min(a.Length, b.Length);
        for (int i = 0; i < min; i++)
        {
            if (a[i] != b[i])
            {
                return $"first differing position {i}: {a[i].ToString(Inv)} vs {b[i].ToString(Inv)}";
            }
        }

        return a.Length == b.Length ? "identical prefix" : $"length differs after {min} matching indices";
    }

    private static int FirstKleeneDiff(KleeneResult a, KleeneResult b, int len)
    {
        for (int i = 0; i < len; i++)
        {
            if (Bitmap.Get(a.Validity, i) != Bitmap.Get(b.Validity, i) || Bitmap.Get(a.Values, i) != Bitmap.Get(b.Values, i))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool KleeneDiffers(KleeneResult a, KleeneResult b, int len) =>
        a.Nulls != b.Nulls || FirstKleeneDiff(a, b, len) >= 0;

    private static (byte[] Values, byte[] Validity) EncodePacked(bool?[] lanes, int len)
    {
        int bc = Math.Max(1, Bitmap.ByteCount(len));
        var values = new byte[bc];
        var validity = new byte[bc];
        for (int i = 0; i < len; i++)
        {
            if (lanes[i] is bool b)
            {
                Bitmap.Set(validity, i, true);
                if (b)
                {
                    Bitmap.Set(values, i, true);
                }
            }
        }

        return (values, validity);
    }

    private static string KleeneLane(KleeneResult r, int i) =>
        !Bitmap.Get(r.Validity, i) ? "NULL" : Bitmap.Get(r.Values, i) ? "TRUE" : "FALSE";

    private static string Lane(bool?[]? lanes, int i) =>
        lanes is null ? "n/a" : lanes[i] is bool b ? (b ? "TRUE" : "FALSE") : "NULL";

    private static int BitAt(byte[] bitmap, int i) => Bitmap.Get(bitmap, i) ? 1 : 0;

    private static string FormatInts(int[] v, int from, int count)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(v[from + i].ToString(Inv));
        }

        return sb.Append(']').ToString();
    }

    private static string FormatLongs(long[] v, int from, int count)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(v[from + i].ToString(Inv));
        }

        return sb.Append(']').ToString();
    }

    private static string HardwarePath(string tierLabel) =>
        $"tier={tierLabel} vs forced-Scalar reference (host: Vector128.IsHardwareAccelerated={Vector128.IsHardwareAccelerated}, " +
        $"Vector256.IsHardwareAccelerated={Vector256.IsHardwareAccelerated}; a forced vector tier runs the portable software fallback so it is reachable on any host)";

    /// <summary>Builds and throws the full AC4 replay diagnostic.</summary>
    private static void Fail(
        GeneratedKernelCase c, string family, string kernel, string operands, string tierLabel, string firstDivergence, string minimalRepro) =>
        throw new XunitException(BuildDiagnostic(c, family, kernel, operands, tierLabel, firstDivergence, minimalRepro));

    /// <summary>
    /// Renders the AC4 mismatch diagnostic (seed, schema/dims, kernel + operands, hardware path/tier, the
    /// first diverging index, and the minimized repro). Exposed so the in-suite non-vacuity self-test can
    /// assert the format carries every required field without modifying production code.
    /// </summary>
    internal static string BuildDiagnostic(
        GeneratedKernelCase c, string family, string kernel, string operands, string tierLabel, string firstDivergence, string minimalRepro)
    {
        string seedHex = "0x" + c.Seed.ToString("X16", Inv);
        var sb = new StringBuilder();
        sb.Append("Scalar-vs-SIMD kernel parity mismatch — the forced-Scalar path is the ADR-0001 oracle; every SIMD tier must be bit-identical.\n");
        sb.Append("  summary        : ").Append(kernel).Append(" diverged at tier ").Append(tierLabel).Append('\n');
        sb.Append("  seed           : ").Append(seedHex).Append('\n');
        sb.Append("  family         : ").Append(family).Append('\n');
        sb.Append("  kernel         : ").Append(kernel).Append('\n');
        sb.Append("  operands       : ").Append(operands).Append('\n');
        sb.Append("  schema / dims  : ").Append(c.Dimensions.Describe()).Append('\n');
        sb.Append("  hardware path  : ").Append(HardwarePath(tierLabel)).Append('\n');
        sb.Append("  first diverge  : ").Append(firstDivergence).Append('\n');
        sb.Append("  minimal repro  : ").Append(minimalRepro).Append('\n');
        sb.Append("  replay         : KernelParityGenerator.Generate(").Append(seedHex).Append(") then re-run this family.\n");
        return sb.ToString();
    }
}
