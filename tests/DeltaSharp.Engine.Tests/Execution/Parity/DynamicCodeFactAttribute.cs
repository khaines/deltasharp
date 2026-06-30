using System.Runtime.CompilerServices;
using Xunit;

namespace DeltaSharp.Engine.Tests.Execution.Parity;

/// <summary>
/// The capability reason a compiled-tier parity case is reported as <b>skipped</b> when the host
/// runtime cannot generate dynamic code (STORY-03.5.2 AC3). It is a single, documented string so a
/// reader of a skipped run sees <i>why</i> the compiled oracle could not be exercised, exactly as a
/// NativeAOT publish elides the <see cref="DeltaSharp.Engine.Execution.CompiledBackend"/> (ADR-0001).
/// </summary>
internal static class DynamicCodeSkip
{
    /// <summary>
    /// Whether the host can JIT-compile the optional <see cref="DeltaSharp.Engine.Execution.CompiledBackend"/>
    /// expression tier. Forwards <see cref="RuntimeFeature.IsDynamicCodeSupported"/> — the <b>same</b>
    /// gate <see cref="DeltaSharp.Engine.Execution.ExecutionBackends.Select()"/> uses in production, so a
    /// skipped case here mirrors the runtime that would have elided the compiled tier.
    /// </summary>
    public static bool IsSupported => RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>
    /// The documented capability reason surfaced on a skipped compiled-tier case. It names the gate so
    /// the skip is self-explanatory: the compiled expression evaluator emits IL via
    /// <c>Expression.Compile</c> and is unreachable when the runtime forbids dynamic code (NativeAOT,
    /// the Mono interpreter, or any future <c>IsDynamicCodeSupported == false</c> host).
    /// </summary>
    public const string Reason =
        "compiled tier elided: RuntimeFeature.IsDynamicCodeSupported is false on this host "
        + "(NativeAOT / dynamic-code-disabled runtime), so there is no JIT-compiled evaluator to compare "
        + "against the interpreter oracle; the interpreter-only cases remain green (ADR-0001).";
}

/// <summary>
/// A <see cref="FactAttribute"/> that auto-skips with <see cref="DynamicCodeSkip.Reason"/> when the
/// host cannot generate dynamic code. The <see cref="FactAttribute.Skip"/> is decided at discovery
/// from <see cref="RuntimeFeature.IsDynamicCodeSupported"/>, so on a JIT runtime (this host) the case
/// <b>runs</b> and on a NativeAOT / dynamic-code-disabled host xUnit reports it <b>Skipped</b> with the
/// documented reason — satisfying STORY-03.5.2 AC3 without an external skippable-fact package.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DynamicCodeFactAttribute : FactAttribute
{
    /// <summary>Creates the fact, setting <see cref="FactAttribute.Skip"/> when dynamic code is unsupported.</summary>
    public DynamicCodeFactAttribute()
    {
        if (!DynamicCodeSkip.IsSupported)
        {
            Skip = DynamicCodeSkip.Reason;
        }
    }
}

/// <summary>
/// The <see cref="TheoryAttribute"/> counterpart to <see cref="DynamicCodeFactAttribute"/>: auto-skips
/// every data row with <see cref="DynamicCodeSkip.Reason"/> when the host cannot generate dynamic code,
/// and runs them all on a JIT runtime (STORY-03.5.2 AC3).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DynamicCodeTheoryAttribute : TheoryAttribute
{
    /// <summary>Creates the theory, setting <see cref="FactAttribute.Skip"/> when dynamic code is unsupported.</summary>
    public DynamicCodeTheoryAttribute()
    {
        if (!DynamicCodeSkip.IsSupported)
        {
            Skip = DynamicCodeSkip.Reason;
        }
    }
}
