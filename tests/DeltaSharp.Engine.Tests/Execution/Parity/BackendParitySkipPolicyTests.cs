using System.Runtime.CompilerServices;
using Xunit;

namespace DeltaSharp.Engine.Tests.Execution.Parity;

/// <summary>
/// Verifies the dynamic-code skip policy that gates the compiled-tier parity cases (STORY-03.5.2 AC3).
/// The <see cref="DynamicCodeFactAttribute"/> / <see cref="DynamicCodeTheoryAttribute"/> decide
/// <see cref="Xunit.FactAttribute.Skip"/> at discovery from <see cref="RuntimeFeature.IsDynamicCodeSupported"/>
/// — the same gate <see cref="DeltaSharp.Engine.Execution.ExecutionBackends.Select()"/> uses — so on a
/// dynamic-code host the compiled cases run and on a NativeAOT / dynamic-code-disabled host they are
/// reported Skipped with a documented capability reason while the interpreter cases stay green.
/// </summary>
public sealed class BackendParitySkipPolicyTests
{
    [Fact]
    public void SkipGate_TracksTheSameRuntimeFeatureAsBackendSelection()
        => Assert.Equal(RuntimeFeature.IsDynamicCodeSupported, DynamicCodeSkip.IsSupported);

    [Fact]
    public void SkipReason_IsDocumentedAndNamesTheGate()
    {
        Assert.False(string.IsNullOrWhiteSpace(DynamicCodeSkip.Reason));
        Assert.Contains("IsDynamicCodeSupported", DynamicCodeSkip.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicCodeFact_SetsSkip_IffDynamicCodeUnsupported()
    {
        var fact = new DynamicCodeFactAttribute();
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            // On a JIT host the compiled cases must actually run (no skip), so the differential is real.
            Assert.Null(fact.Skip);
        }
        else
        {
            Assert.Equal(DynamicCodeSkip.Reason, fact.Skip);
        }
    }

    [Fact]
    public void DynamicCodeTheory_SetsSkip_IffDynamicCodeUnsupported()
    {
        var theory = new DynamicCodeTheoryAttribute();
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            Assert.Null(theory.Skip);
        }
        else
        {
            Assert.Equal(DynamicCodeSkip.Reason, theory.Skip);
        }
    }
}
