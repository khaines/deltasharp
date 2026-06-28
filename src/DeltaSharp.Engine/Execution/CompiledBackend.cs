using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The optional codegen tier (ADR-0001): fuses a kernel into a JIT-compiled delegate via
/// <c>Expression&lt;TDelegate&gt;.Compile()</c>. Because it emits IL at runtime it is
/// NativeAOT-incompatible, so the whole type is annotated
/// <see cref="RequiresDynamicCodeAttribute"/>. It is constructed <b>only</b> behind the
/// <see cref="ExecutionBackends.IsCompiledBackendAvailable"/> feature guard and is therefore
/// dead-code-eliminated from NativeAOT publishes; it is never required for correctness — the
/// interpreted backend always produces the same results.
/// </summary>
[RequiresDynamicCode(
    "The compiled execution backend emits IL via Expression.Compile (ADR-0001 optional codegen " +
    "tier); it is elided from NativeAOT and is only constructed when " +
    "RuntimeFeature.IsDynamicCodeSupported is true.")]
public sealed class CompiledBackend : IExecutionBackend
{
    /// <inheritdoc />
    public string Name => "compiled";

    /// <inheritdoc />
    public bool UsesDynamicCode => true;

    /// <inheritdoc />
    public Func<long, long> BuildAffineEvaluator(AffineInt64Kernel kernel)
    {
        // Build the expression tree (Multiplier * value) + Addend and JIT-compile it. The tree
        // mirrors AffineInt64Kernel.Evaluate exactly (unchecked arithmetic), so the compiled
        // delegate is bit-for-bit identical to the interpreted backend, including on overflow.
        ParameterExpression value = Expression.Parameter(typeof(long), "value");
        BinaryExpression body = Expression.Add(
            Expression.Multiply(Expression.Constant(kernel.Multiplier, typeof(long)), value),
            Expression.Constant(kernel.Addend, typeof(long)));
        Expression<Func<long, long>> lambda = Expression.Lambda<Func<long, long>>(body, value);

        // Compiled fast-path tier — reachable only when RuntimeFeature.IsDynamicCodeSupported is
        // true and elided from NativeAOT publishes. Justified by ADR-0001 (optional codegen tier);
        // see docs/engineering/design/api-governance.md ("Requesting a scoped exception").
#pragma warning disable RS0030 // Banned API: Expression.Compile — scoped ADR-0001 codegen tier.
        return lambda.Compile();
#pragma warning restore RS0030
    }
}
