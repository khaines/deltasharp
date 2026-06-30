using System.Runtime.Intrinsics;
using DeltaSharp.Engine.Columnar;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Covers STORY-03.3.1 (#149) AC3: the capability guards run only the SIMD tiers the host actually supports under
/// <see cref="KernelTier.Auto"/> (so an unsupported intrinsic path is never reached in production), while the
/// forcing values make a specific portable-vector body deterministically reachable for parity testing. The kernel
/// result must be identical no matter which tier the guard selects.
/// </summary>
[Collection("KernelParity")]
public class KernelTierTests
{
    [Fact]
    public void Auto_TracksHardwareCapability()
    {
        Assert.Equal(Vector256.IsHardwareAccelerated, KernelTierGate.UseVector256(KernelTier.Auto));
        Assert.Equal(Vector128.IsHardwareAccelerated, KernelTierGate.UseVector128(KernelTier.Auto));
    }

    [Fact]
    public void ForcedVector256_IsReachableEvenWhenNotHardwareAccelerated()
    {
        // The portable Vector256 software fallback lets a forced 256-bit body run on any host (the whole point of
        // the seam): the gate says "yes" regardless of Vector256.IsHardwareAccelerated.
        Assert.True(KernelTierGate.UseVector256(KernelTier.Vector256));
        Assert.False(KernelTierGate.UseVector128(KernelTier.Vector256)); // 256 forced -> 128 loop suppressed
    }

    [Fact]
    public void ForcedVector128_RunsOnlyThe128Loop()
    {
        Assert.True(KernelTierGate.UseVector128(KernelTier.Vector128));
        Assert.False(KernelTierGate.UseVector256(KernelTier.Vector128));
    }

    [Fact]
    public void ForcedScalar_SuppressesEveryVectorLoop()
    {
        Assert.False(KernelTierGate.UseVector256(KernelTier.Scalar));
        Assert.False(KernelTierGate.UseVector128(KernelTier.Scalar));
    }

    [Fact]
    public void VectorByteWidth_ReflectsTheWidestActiveTier()
    {
        int expected = Vector256.IsHardwareAccelerated ? 32 : Vector128.IsHardwareAccelerated ? 16 : 1;
        Assert.Equal(expected, KernelTierGate.VectorByteWidth);
        Assert.Equal(Vector128.IsHardwareAccelerated || Vector256.IsHardwareAccelerated, KernelTierGate.IsHardwareAccelerated);
    }

    [Fact]
    public void AutoDispatch_EqualsTheHardwareAppropriateForcedTier()
    {
        int[] values = KernelTestSupport.RandomInts(new Random(123), 1000, -10_000, 10_000);

        KernelTier hardwareTier = Vector256.IsHardwareAccelerated ? KernelTier.Vector256
            : Vector128.IsHardwareAccelerated ? KernelTier.Vector128
            : KernelTier.Scalar;

        Assert.Equal(AggregateKernels.SumInt32(values, hardwareTier), AggregateKernels.SumInt32(values, KernelTier.Auto));
        Assert.Equal(AggregateKernels.MinInt32(values, hardwareTier), AggregateKernels.MinInt32(values, KernelTier.Auto));
        Assert.Equal(AggregateKernels.MaxInt32(values, hardwareTier), AggregateKernels.MaxInt32(values, KernelTier.Auto));
    }
}
