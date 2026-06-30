using System.Runtime.CompilerServices;
using DeltaSharp.Engine.Execution;
using Xunit;

namespace DeltaSharp.Engine.Tests.Execution;

/// <summary>
/// Exercises the ADR-0001 execution-backend seam: the always-present interpreted backend, the
/// optional compiled tier, the feature-switched selector, and the differential parity oracle.
/// </summary>
/// <remarks>
/// Compiled-tier assertions run only when <see cref="RuntimeFeature.IsDynamicCodeSupported"/> is
/// <see langword="true"/> (the normal JIT <c>dotnet test</c> host). On a NativeAOT host the
/// compiled tier is elided and those bodies short-circuit, matching the production selection.
/// </remarks>
public class ExecutionBackendTests
{
    public static TheoryData<long, long, long> AffineCases() => new()
    {
        // multiplier, addend, input
        { 0, 0, 0 },
        { 1, 0, 42 },
        { 2, 1, 20 },
        { -3, 7, 5 },
        { 7, -11, -9 },
        { 1, 0, long.MaxValue },
        { 2, 0, long.MaxValue },          // overflow: must wrap identically on both backends
        { long.MinValue, 1, long.MinValue },
    };

    // ----- AffineInt64Kernel reference -----

    [Theory]
    [MemberData(nameof(AffineCases))]
    public void Kernel_Evaluate_ComputesMultiplyThenAdd(long multiplier, long addend, long input)
    {
        var kernel = new AffineInt64Kernel(multiplier, addend);
        Assert.Equal(unchecked((multiplier * input) + addend), kernel.Evaluate(input));
    }

    // ----- Interpreted backend (AC2/AC5: always available, never uses dynamic code) -----

    [Fact]
    public void Interpreted_Instance_IsSingleton()
        => Assert.Same(InterpretedVectorizedBackend.Instance, InterpretedVectorizedBackend.Instance);

    [Fact]
    public void Interpreted_DoesNotUseDynamicCode()
    {
        Assert.False(InterpretedVectorizedBackend.Instance.UsesDynamicCode);
        Assert.Equal("interpreted-vectorized", InterpretedVectorizedBackend.Instance.Name);
    }

    [Theory]
    [MemberData(nameof(AffineCases))]
    public void Interpreted_BuildAffineEvaluator_MatchesReference(long multiplier, long addend, long input)
    {
        var kernel = new AffineInt64Kernel(multiplier, addend);
        Func<long, long> evaluator = InterpretedVectorizedBackend.Instance.BuildAffineEvaluator(kernel);
        Assert.Equal(kernel.Evaluate(input), evaluator(input));
    }

    // ----- Selector (AC2: interpreted always available; compiled gated by the runtime) -----

    [Fact]
    public void Select_NullOptions_Throws()
        => Assert.Throws<ArgumentNullException>(() => ExecutionBackends.Select(null!));

    [Fact]
    public void Select_ForceInterpreted_AlwaysReturnsInterpreted()
    {
        IExecutionBackend backend = ExecutionBackends.Select(new ExecutionBackendOptions { ForceInterpreted = true });
        Assert.Same(InterpretedVectorizedBackend.Instance, backend);
        Assert.False(backend.UsesDynamicCode);
    }

    [Fact]
    public void Select_ForceInterpreted_OverridesAvailableCompiledTier_AndCreatesNoCompiledDelegate()
    {
        // AC3: a force-interpreter override must pin the interpreter and skip the compiled tier
        // entirely — so no CompiledBackend is constructed and no dynamic-code delegate is created —
        // EVEN on a runtime where the compiled tier IS available. Establish that precondition
        // explicitly so the assertion is non-vacuous: on a dynamic-code host the unforced selector
        // genuinely picks the compiled, IL-emitting backend, and the override must still beat it.
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return; // the compiled tier is elided here, so "override an available tier" is unreachable
        }

        IExecutionBackend unforced = ExecutionBackends.Select();
        Assert.True(unforced.UsesDynamicCode);   // the compiled tier is genuinely available on this host
        Assert.Equal("compiled", unforced.Name);

        IExecutionBackend forced =
            ExecutionBackends.Select(new ExecutionBackendOptions { ForceInterpreted = true });

        // The override wins over the available compiled tier...
        Assert.Same(InterpretedVectorizedBackend.Instance, forced);
        Assert.NotSame(unforced, forced);

        // ...and the pinned backend never emits dynamic code: no compiled delegate is created, so its
        // evaluator is the interpreted closure — byte-identical to the kernel reference.
        Assert.False(forced.UsesDynamicCode);
        var kernel = new AffineInt64Kernel(2, 1);
        Func<long, long> evaluator = forced.BuildAffineEvaluator(kernel);
        Assert.Equal(kernel.Evaluate(20), evaluator(20));
    }

    [Fact]
    public void Select_Default_TracksRuntimeDynamicCodeCapability()
    {
        // AC2: when dynamic code is unsupported the interpreted backend is chosen and the compiled
        // tier is blocked; when supported the compiled tier is chosen. Tie the assertion to the
        // host's real capability rather than hard-coding it.
        IExecutionBackend backend = ExecutionBackends.Select();
        Assert.Equal(RuntimeFeature.IsDynamicCodeSupported, backend.UsesDynamicCode);

        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            Assert.Same(InterpretedVectorizedBackend.Instance, backend);
        }
    }

    // ----- Compiled tier + parity oracle (AC5: codegen optional, never required for correctness) -----

    [Fact]
    public void Compiled_ReportsDynamicCode()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return; // compiled tier is elided on this host
        }

        IExecutionBackend backend = ExecutionBackends.Select();
        Assert.True(backend.UsesDynamicCode);
        Assert.Equal("compiled", backend.Name);
    }

    [Theory]
    [MemberData(nameof(AffineCases))]
    public void Parity_CompiledMatchesInterpreted(long multiplier, long addend, long input)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return; // nothing to compare against on a NativeAOT host
        }

        var kernel = new AffineInt64Kernel(multiplier, addend);
        Func<long, long> interpreted =
            ExecutionBackends.Select(new ExecutionBackendOptions { ForceInterpreted = true })
                .BuildAffineEvaluator(kernel);
        Func<long, long> compiled = ExecutionBackends.Select().BuildAffineEvaluator(kernel);

        long expected = kernel.Evaluate(input);
        Assert.Equal(expected, interpreted(input));
        Assert.Equal(expected, compiled(input));
        Assert.Equal(interpreted(input), compiled(input));
    }

    // ----- Options -----

    [Fact]
    public void Options_Default_PrefersCompiledTier()
        => Assert.False(ExecutionBackendOptions.Default.ForceInterpreted);
}
