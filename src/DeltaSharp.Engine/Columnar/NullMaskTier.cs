using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// Selects which SIMD/scalar word tier the branchless null-mask kernels (<see cref="BitmapOps"/> /
/// <see cref="NullMasks"/>) execute, used to make every tier <b>deterministically reachable on any host</b>
/// (STORY-02.6.2, #144).
/// </summary>
/// <remarks>
/// <para>
/// The kernels are written against the portable <see cref="System.Runtime.Intrinsics"/> vector API
/// (<see cref="Vector256.LoadUnsafe"/>, <see cref="Vector256.AndNot"/>, the bitwise operators, ...), which
/// has a <b>software fallback that runs on every architecture</b> — so a <see cref="Vector256{T}"/> body is
/// fully runnable on an arm64 box even though that box reports <c>Vector256.IsHardwareAccelerated == false</c>.
/// In production the only caller is <see cref="Auto"/>, which keeps the original
/// <c>IsHardwareAccelerated</c>-guarded dispatch and therefore the original codegen and the NativeAOT
/// dead-code elimination of the unsupported tiers (ADR-0001/ADR-0014). The forcing values exist so the
/// parity tests can drive a specific tier — most importantly the <see cref="Vector256"/> tier, which is
/// constant-folded away under <see cref="Auto"/> on an arm64/NEON CI host and would otherwise be untested
/// (its mutations would be vacuously "green").
/// </para>
/// <para>
/// A forced tier runs its own loop and then lets the narrower <c>ulong</c>/<c>byte</c> tail loops drain the
/// remaining bytes (a forced vector tier runs exactly one vector loop, never the other), so the result is
/// always byte-identical to <see cref="Auto"/> and to the scalar <see cref="NullPropagation"/> oracle
/// regardless of the tier chosen.
/// </para>
/// </remarks>
internal enum NullMaskTier
{
    /// <summary>
    /// Production dispatch: run the widest tier whose <c>IsHardwareAccelerated</c> guard is a compile-time
    /// <c>true</c> on the target, falling through to the <c>ulong</c> and <c>byte</c> tails. Behaviorally and
    /// codegen-identical to the pre-seam kernels; the only value the engine itself ever passes.
    /// </summary>
    Auto,

    /// <summary>Force the per-<c>byte</c> tail loop only (skip the <c>Vector256</c>/<c>Vector128</c>/<c>ulong</c> tiers).</summary>
    Scalar,

    /// <summary>Force the <c>ulong</c> (8-byte) word loop (and its <c>byte</c> tail); skip the vector tiers.</summary>
    Word,

    /// <summary>Force the <see cref="Vector128{T}"/> (16-byte) loop (and the narrower tails); skip <see cref="Vector256{T}"/>.</summary>
    Vector128,

    /// <summary>Force the <see cref="Vector256{T}"/> (32-byte) loop (and the narrower tails) via the portable software fallback.</summary>
    Vector256,
}

/// <summary>
/// Resolves a <see cref="NullMaskTier"/> selection to per-tier "run this loop?" predicates shared by every
/// branchless kernel, so the dispatch rule lives in exactly one place.
/// </summary>
/// <remarks>
/// Each helper is force-inlined: under <see cref="NullMaskTier.Auto"/> the <c>Vector256.IsHardwareAccelerated</c>
/// / <c>Vector128.IsHardwareAccelerated</c> sub-expressions still fold to their per-target compile-time
/// constant (so the AOT/JIT dead-code elimination of an unsupported tier is preserved), while a forced value
/// turns the corresponding portable-vector body into ordinary reachable code. The predicate is evaluated
/// once per kernel call (at loop entry), never per element, so the hot path is unaffected.
/// </remarks>
internal static class NullMaskTierGate
{
    /// <summary>Whether the <see cref="Vector256{T}"/> loop should run for this <paramref name="tier"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UseVector256(NullMaskTier tier) =>
        tier == NullMaskTier.Vector256 || (tier == NullMaskTier.Auto && Vector256.IsHardwareAccelerated);

    /// <summary>Whether the <see cref="Vector128{T}"/> loop should run for this <paramref name="tier"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UseVector128(NullMaskTier tier) =>
        tier == NullMaskTier.Vector128 || (tier == NullMaskTier.Auto && Vector128.IsHardwareAccelerated);

    /// <summary>Whether the <c>ulong</c> word loop should run (everything except a forced <see cref="NullMaskTier.Scalar"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UseWord(NullMaskTier tier) => tier != NullMaskTier.Scalar;
}
