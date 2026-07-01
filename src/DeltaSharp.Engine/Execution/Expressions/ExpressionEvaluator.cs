using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// A bound, vector-at-a-time kernel for one <see cref="PhysicalExpression"/> node (STORY-03.4.1). It
/// evaluates over a <see cref="ColumnBatch"/>'s logical rows and returns a <see cref="ColumnVector"/>
/// of exactly <see cref="ColumnBatch.LogicalRowCount"/> rows, in logical (selection) order, carrying
/// both values and validity — the AOT-clean semantic baseline (the ADR-0001 parity oracle) that the
/// STORY-03.4.2 compiled tier must match. Leaves return zero-copy views; computed nodes materialize a
/// contiguous result so downstream operators keep their <see cref="ColumnVector.GetValues{T}"/> fast
/// path.
/// </summary>
/// <remarks>
/// Building the evaluator tree (see <see cref="ExpressionEvaluators.Build"/>) performs <b>no row
/// work</b> — it only resolves kernels and rejects unsupported shapes, preserving the lazy/eager
/// invariant. <see cref="Evaluate"/> is pull-time. No member emits IL or uses reflection, so the
/// interpreted path carries no <c>[RequiresDynamicCode]</c>.
/// </remarks>
internal abstract class ExpressionEvaluator
{
    /// <summary>Initializes the resolved result type and nullability of the produced vector.</summary>
    protected ExpressionEvaluator(DataType type, bool nullable)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
        Nullable = nullable;
    }

    /// <summary>The result type of the vector this evaluator produces.</summary>
    public DataType Type { get; }

    /// <summary>Whether the produced vector may contain nulls.</summary>
    public bool Nullable { get; }

    /// <summary>
    /// Evaluates the node over <paramref name="batch"/>'s logical rows, returning a vector of
    /// <see cref="ColumnBatch.LogicalRowCount"/> rows in logical order. Materialized intermediates and
    /// outputs reserve their footprint against <paramref name="memory"/> before allocating.
    /// </summary>
    /// <exception cref="ExecutionMemoryException">A reservation exceeds the run's memory budget.</exception>
    /// <exception cref="ArithmeticOverflowException">An ANSI overflow/out-of-range condition occurs.</exception>
    /// <exception cref="DivideByZeroException">An ANSI division or modulo by zero occurs.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled.</exception>
    public abstract ColumnVector Evaluate(ColumnBatch batch, BatchEvaluationMemory memory, CancellationToken cancellationToken);
}

/// <summary>
/// Builds an <see cref="ExpressionEvaluator"/> tree from a resolved <see cref="PhysicalExpression"/>,
/// the AOT-clean dispatch the interpreted backend uses (STORY-03.4.1). Unknown node types and the v1
/// capability gaps (decimal divide/remainder, unsupported cast pairs) raise
/// <see cref="UnsupportedOperatorException"/> at build (Open) time — never a silent row-at-a-time
/// fallback, and never deferred to a mid-stream failure.
/// </summary>
internal static class ExpressionEvaluators
{
    /// <summary>Resolves <paramref name="expression"/> into an evaluator over <paramref name="inputSchema"/>.</summary>
    /// <param name="expression">The resolved physical expression to bind.</param>
    /// <param name="inputSchema">The operator's input schema (for column-reference validation).</param>
    /// <param name="backendName">The backend attributed in any <see cref="UnsupportedOperatorException"/>.</param>
    /// <param name="kind">The operator kind attributed in any <see cref="UnsupportedOperatorException"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A column reference is out of range or disagrees with the input schema.</exception>
    /// <exception cref="UnsupportedOperatorException">The node shape is not executable by the interpreted tier.</exception>
    public static ExpressionEvaluator Build(
        PhysicalExpression expression, StructType inputSchema, string backendName, OperatorKind kind)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(inputSchema);
        ArgumentNullException.ThrowIfNull(backendName);

        switch (expression)
        {
            case ColumnReference column:
                return BuildColumnReference(column, inputSchema);

            case Literal literal:
                return new LiteralEvaluator(literal);

            case ArithmeticExpression arithmetic:
                if (arithmetic.EvalKind == ArithmeticEvalKind.Decimal
                    && arithmetic.Operator is ArithmeticOperator.Divide or ArithmeticOperator.Remainder)
                {
                    throw new UnsupportedOperatorException(
                        kind,
                        backendName,
                        $"decimal '{arithmetic.Operator}' value rounding is deferred by the type system; "
                        + "cast an operand to double or use +, -, *");
                }

                return new ArithmeticEvaluator(
                    arithmetic,
                    Build(arithmetic.Left, inputSchema, backendName, kind),
                    Build(arithmetic.Right, inputSchema, backendName, kind));

            case ComparisonExpression comparison:
                return new ComparisonEvaluator(
                    comparison,
                    Build(comparison.Left, inputSchema, backendName, kind),
                    Build(comparison.Right, inputSchema, backendName, kind));

            case LogicalExpression logical:
                return logical.Operator == LogicalOperator.Not
                    ? new LogicalEvaluator(logical, Build(logical.Left, inputSchema, backendName, kind), right: null)
                    : new LogicalEvaluator(
                        logical,
                        Build(logical.Left, inputSchema, backendName, kind),
                        Build(logical.Right, inputSchema, backendName, kind));

            case CastExpression cast:
                if (!CastEvaluator.IsSupported(cast.Child.Type, cast.TargetType))
                {
                    throw new UnsupportedOperatorException(
                        kind,
                        backendName,
                        $"cast from '{cast.Child.Type.SimpleString}' to '{cast.TargetType.SimpleString}' "
                        + "is not in the interpreted v1 cast matrix (string/binary, numeric<->temporal, "
                        + "and float/double->decimal casts arrive later)");
                }

                return new CastEvaluator(cast, Build(cast.Child, inputSchema, backendName, kind));

            case IsNullExpression isNull:
                return new NullCheckEvaluator(isNull, Build(isNull.Child, inputSchema, backendName, kind));

            default:
                throw new UnsupportedOperatorException(
                    kind,
                    backendName,
                    $"the interpreted expression evaluator does not support '{expression.GetType().Name}' "
                    + "(compiled-fusion specialization is STORY-03.4.2)");
        }
    }

    private static ColumnReferenceEvaluator BuildColumnReference(ColumnReference column, StructType inputSchema)
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

        return new ColumnReferenceEvaluator(column);
    }
}
