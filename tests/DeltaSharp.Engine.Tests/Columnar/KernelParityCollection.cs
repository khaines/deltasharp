using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Serializes the SIMD kernel parity test classes (aggregate / comparison / forced-tier) into a single
/// non-parallel xUnit collection. The forced-tier theories exercise the <b>portable</b> Vector256
/// software fallback on hosts where <c>Vector256.IsHardwareAccelerated == false</c> (e.g. the arm64 CI
/// box); under heavy parallel load the cold JIT of that fallback was observed to flake intermittently
/// (a load/timing artifact — proven not a kernel correctness or memory-safety defect by multi-million-call
/// concurrent stress in the PR #358 council). Disabling parallelization for these classes keeps the
/// mutation-killing parity gate deterministic so a real SIMD regression is never dismissed as "the flake".
/// </summary>
[CollectionDefinition("KernelParity", DisableParallelization = true)]
public sealed class KernelParityCollection
{
}
