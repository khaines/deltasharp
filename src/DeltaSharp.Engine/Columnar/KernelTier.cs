using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// Selects which SIMD/scalar tier the typed-value aggregate and comparison kernels
/// (<see cref="AggregateKernels"/> / <see cref="ComparisonKernels"/>) execute, used to make every tier
/// <b>deterministically reachable on any host</b> (STORY-03.3.1, #149). It is the typed-value analogue of
/// the validity-bitmap <see cref="NullMaskTier"/> (#144): the inner loops are written against the portable
/// <see cref="System.Runtime.Intrinsics"/> vector API whose software fallback runs on every architecture, so a
/// forced <see cref="Vector256"/> body executes and is parity-checked even on an arm64/NEON CI host where
/// <see cref="Auto"/> constant-folds it away.
/// </summary>
/// <remarks>
/// <para>
/// In production the only caller is <see cref="Auto"/>, which keeps the original
/// <c>IsHardwareAccelerated</c>-guarded dispatch — and therefore the original codegen and the NativeAOT
/// dead-code elimination of the unsupported tiers (ADR-0001/ADR-0014). The forcing values exist purely so the
/// parity tests can drive a specific tier (most importantly the <see cref="Vector256"/> tier, which is
/// constant-folded away under <see cref="Auto"/> on this arm64 box and would otherwise be vacuously "green").
/// </para>
/// <para>
/// Unlike <see cref="NullMaskTier"/> there is no <c>Word</c> (ulong) tier: a typed reduction widens or compares
/// whole <typeparamref name="int"/>/<typeparamref name="long"/> lanes, not packed bits, so the meaningful widths
/// are the 32-byte and 16-byte vectors plus the scalar tail. A forced vector tier runs exactly one vector loop
/// and then lets the scalar tail drain the remainder, so the result is always identical to <see cref="Auto"/>
/// and to the scalar reference regardless of the tier chosen.
/// </para>
/// </remarks>
internal enum KernelTier
{
    /// <summary>
    /// Production dispatch: run the widest tier whose <c>IsHardwareAccelerated</c> guard is a compile-time
    /// <c>true</c> on the target, falling through to the scalar tail. Behaviorally and codegen-identical to the
    /// pre-seam kernels; the only value the engine itself ever passes.
    /// </summary>
    Auto,

    /// <summary>Force the per-element scalar tail loop only (skip the <see cref="Vector256{T}"/>/<see cref="Vector128{T}"/> tiers).</summary>
    Scalar,

    /// <summary>Force the <see cref="Vector128{T}"/> (16-byte) loop (and the scalar tail); skip <see cref="Vector256{T}"/>.</summary>
    Vector128,

    /// <summary>Force the <see cref="Vector256{T}"/> (32-byte) loop (and the scalar tail) via the portable software fallback.</summary>
    Vector256,
}

/// <summary>
/// Resolves a <see cref="KernelTier"/> selection to per-tier "run this loop?" predicates shared by every
/// typed-value kernel, so the dispatch rule lives in exactly one place (mirrors <see cref="NullMaskTierGate"/>).
/// </summary>
/// <remarks>
/// Each helper is force-inlined: under <see cref="KernelTier.Auto"/> the <c>Vector256.IsHardwareAccelerated</c> /
/// <c>Vector128.IsHardwareAccelerated</c> sub-expressions still fold to their per-target compile-time constant
/// (so the AOT/JIT dead-code elimination of an unsupported tier is preserved), while a forced value turns the
/// corresponding portable-vector body into ordinary reachable code. The predicate is evaluated once per kernel
/// call (at loop entry), never per element, so the hot path is unaffected.
/// </remarks>
internal static class KernelTierGate
{
    /// <summary>Whether the <see cref="Vector256{T}"/> loop should run for this <paramref name="tier"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UseVector256(KernelTier tier) =>
        tier == KernelTier.Vector256 || (tier == KernelTier.Auto && Vector256.IsHardwareAccelerated);

    /// <summary>Whether the <see cref="Vector128{T}"/> loop should run for this <paramref name="tier"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UseVector128(KernelTier tier) =>
        tier == KernelTier.Vector128 || (tier == KernelTier.Auto && Vector128.IsHardwareAccelerated);

    /// <summary>The widest active lane width (in bytes) under <see cref="KernelTier.Auto"/>; recorded in benchmark metadata.</summary>
    public static int VectorByteWidth =>
        Vector256.IsHardwareAccelerated ? Vector256<byte>.Count :
        Vector128.IsHardwareAccelerated ? Vector128<byte>.Count : 1;

    /// <summary>Whether any SIMD tier backs these kernels on the current hardware (informational; the scalar fallback is always correct).</summary>
    public static bool IsHardwareAccelerated => Vector128.IsHardwareAccelerated || Vector256.IsHardwareAccelerated;
}
