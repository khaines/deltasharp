using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Builds the compiled fast-path <see cref="ExpressionEvaluator"/> when an expression is fusable, and
/// transparently falls back to the interpreted <see cref="ExpressionEvaluators.Build"/> otherwise
/// (STORY-03.4.2, ADR-0001). The compiled tier is <b>never a correctness dependency</b>: it handles a
/// strict subset (fixed-width arithmetic/comparison/boolean/cast/null) of what the interpreter handles,
/// so any shape it cannot fuse — strings/binary, or the v1 gaps the interpreter itself rejects (decimal
/// <c>/</c>/<c>%</c>, unsupported casts) — is delegated to the interpreter, which evaluates it or raises
/// the identical <see cref="UnsupportedOperatorException"/>. The result is therefore byte-identical to,
/// and never worse than, the interpreted backend.
/// </summary>
/// <remarks>
/// Annotated <see cref="RequiresDynamicCodeAttribute"/> (it can reach <c>Expression.Compile</c>);
/// reachable only behind the dynamic-code feature guard and elided from NativeAOT.
/// </remarks>
[RequiresDynamicCode(
    "Builds a compiled fused evaluator via Expression.Compile (ADR-0001 optional codegen tier); reachable " +
    "only behind the IsCompiledBackendAvailable feature guard and elided from NativeAOT.")]
internal static class CompiledExpressionEvaluators
{
    /// <summary>
    /// Resolves <paramref name="expression"/> into the compiled evaluator (compiling/caching its kernel in
    /// <paramref name="cache"/>) when it is fusable, else the interpreted evaluator.
    /// </summary>
    /// <param name="expression">The resolved physical expression to bind.</param>
    /// <param name="inputSchema">The operator's input schema (for column-reference validation).</param>
    /// <param name="backendName">The backend attributed in any <see cref="UnsupportedOperatorException"/>.</param>
    /// <param name="kind">The operator kind attributed in any <see cref="UnsupportedOperatorException"/>.</param>
    /// <param name="cache">The delegate cache for compile-once-per-shape reuse.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A column reference is out of range or disagrees with the input schema.</exception>
    /// <exception cref="UnsupportedOperatorException">The shape is not executable by the interpreted tier either.</exception>
    public static ExpressionEvaluator Build(
        PhysicalExpression expression,
        StructType inputSchema,
        string backendName,
        OperatorKind kind,
        CompiledExpressionCache cache)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(inputSchema);
        ArgumentNullException.ThrowIfNull(backendName);
        ArgumentNullException.ThrowIfNull(cache);

        if (!CanFuse(expression))
        {
            // Not fusable: the interpreter evaluates it (e.g. strings) or rejects it identically.
            return ExpressionEvaluators.Build(expression, inputSchema, backendName, kind);
        }

        // A fusable tree's only build-time failure is an invalid column reference; validate it with the
        // same checks (and messages) the interpreter uses so rejection is parity-identical.
        ValidateColumnReferences(expression, inputSchema);
        CompiledFusion fusion = cache.GetOrCompile(expression);
        return new CompiledExpressionEvaluator(expression.Type, expression.Nullable, fusion);
    }

    /// <summary>
    /// Whether the compiled tier can fuse the entire tree. Conservative by design: every node must be a
    /// fixed-width shape the lowering supports, so a single string/binary lane (or a v1 interpreter gap)
    /// anywhere routes the whole expression to the interpreter.
    /// </summary>
    public static bool CanFuse(PhysicalExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        switch (expression)
        {
            case ColumnReference column:
                return IsFusableType(column.Type);

            case Literal literal:
                return IsFusableType(literal.Type);

            case ArithmeticExpression arithmetic:
                // Decimal /,% is rejected by the interpreter at build time; let it fall back so the
                // identical UnsupportedOperatorException is thrown.
                if (arithmetic.EvalKind == ArithmeticEvalKind.Decimal
                    && arithmetic.Operator is ArithmeticOperator.Divide or ArithmeticOperator.Remainder)
                {
                    return false;
                }

                return IsFusableType(arithmetic.Type) && CanFuse(arithmetic.Left) && CanFuse(arithmetic.Right);

            case ComparisonExpression comparison:
                return comparison.EvalKind is not (ComparisonEvalKind.String or ComparisonEvalKind.Binary)
                    && CanFuse(comparison.Left)
                    && CanFuse(comparison.Right);

            case LogicalExpression logical:
                return CanFuse(logical.Left)
                    && (logical.Operator == LogicalOperator.Not || CanFuse(logical.Right));

            case CastExpression cast:
                return CastEvaluator.IsSupported(cast.Child.Type, cast.TargetType)
                    && IsFusableType(cast.Child.Type)
                    && IsFusableType(cast.TargetType)
                    && CanFuse(cast.Child);

            case IsNullExpression isNull:
                return CanFuse(isNull.Child);

            default:
                return false;
        }
    }

    /// <summary>Whether a type has a fixed-width carrier the compiled lowering can read/write.</summary>
    private static bool IsFusableType(DataType type) => type switch
    {
        BooleanType or ByteType or ShortType or IntegerType or LongType
            or FloatType or DoubleType or DateType or TimestampType or DecimalType => true,
        _ => false, // string, binary, and any future variable-width type
    };

    /// <summary>Mirrors <c>ExpressionEvaluators.BuildColumnReference</c>'s validation (same exceptions/messages).</summary>
    private static void ValidateColumnReferences(PhysicalExpression expression, StructType inputSchema)
    {
        if (expression is ColumnReference column)
        {
            if ((uint)column.Ordinal >= (uint)inputSchema.Count)
            {
                throw new ArgumentException(
                    $"Column reference {column.Ordinal} is out of range for the input schema ({inputSchema.Count} field(s)).",
                    nameof(column));
            }

            DataType schemaType = inputSchema[column.Ordinal].DataType;
            if (!schemaType.Equals(column.Type))
            {
                throw new ArgumentException(
                    $"Column reference {column.Ordinal} is typed '{column.Type.SimpleString}' but the input column "
                    + $"'{inputSchema[column.Ordinal].Name}' is '{schemaType.SimpleString}'.",
                    nameof(column));
            }

            return;
        }

        foreach (PhysicalExpression child in expression.Children)
        {
            ValidateColumnReferences(child, inputSchema);
        }
    }
}
