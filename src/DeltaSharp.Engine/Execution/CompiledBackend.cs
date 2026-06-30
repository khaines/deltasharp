using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.Types;

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
    // Compile-once-per-shape cache of fused expression kernels (STORY-03.4.2). Consulted at Open time
    // (never per batch/row), so the engine stays lock-free; elided from NativeAOT with this type.
    private readonly CompiledExpressionCache _expressionCache = new();

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

    /// <inheritdoc />
    /// <remarks>The compiled tier supports exactly what the interpreter does (it only fuses hot
    /// scalar expressions; it never reimplements operators), so it reports the same supported kinds.</remarks>
    public bool Supports(OperatorKind kind) => InterpretedOperators.Supports(kind);

    /// <inheritdoc />
    /// <remarks>Operator execution is delegated to the shared <see cref="InterpretedOperators"/> dispatch —
    /// the same code the interpreter runs — so operator results are identical across backends by construction
    /// (ADR-0001 parity oracle). The compiled tier's value is fusing scalar expressions (later FEAT-03.2),
    /// not reimplementing operators; unsupported shapes throw <see cref="UnsupportedOperatorException"/>
    /// attributed to this backend.</remarks>
    public IBatchStream Open(PhysicalOperator op, ExecutionContext context)
        => InterpretedOperators.Open(Name, op, context);

    /// <summary>
    /// Builds the fused, JIT-compiled evaluator for a hot filter predicate or projection expression
    /// (STORY-03.4.2) — the compiled tier's value-add over the interpreter. Fusable fixed-width trees
    /// (arithmetic/comparison/boolean/cast/null) lower to one cached <see cref="FusedRowKernel"/> per
    /// shape (no per-node intermediate vectors); any other shape transparently falls back to the
    /// interpreted evaluator, so the result is byte-identical to — and never worse than — the
    /// interpreter (the ADR-0001 parity oracle). Wiring this into the operator-owned filter/project
    /// streams is deferred to the operator layer (PR #148); the parity suite (#154) exercises it here.
    /// </summary>
    /// <param name="expression">The resolved predicate/projection expression to evaluate.</param>
    /// <param name="inputSchema">The operator's input schema, for column-reference validation.</param>
    /// <param name="kind">The operator kind attributed in any <see cref="UnsupportedOperatorException"/>.</param>
    /// <returns>A compiled evaluator when fusable; otherwise the interpreted evaluator.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal Expressions.ExpressionEvaluator BuildExpressionEvaluator(
        PhysicalExpression expression, StructType inputSchema, OperatorKind kind)
        => CompiledExpressionEvaluators.Build(expression, inputSchema, Name, kind, _expressionCache);

    /// <summary>A snapshot of the expression-fusion cache counters (compile/hit/eviction); for diagnostics and tests.</summary>
    internal CompiledExpressionCacheMetrics ExpressionCacheMetrics => _expressionCache.Metrics;
}
