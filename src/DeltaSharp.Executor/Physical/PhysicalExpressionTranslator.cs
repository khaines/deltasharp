using DeltaSharp.Types;
using CoreArithmeticOperator = DeltaSharp.Plans.Expressions.ArithmeticOperator;
using CoreComparisonOperator = DeltaSharp.Plans.Expressions.ComparisonOperator;
using CoreExpr = DeltaSharp.Plans.Expressions.Expression;
using CoreLiteral = DeltaSharp.Plans.Expressions.Literal;
using CoreNullOrdering = DeltaSharp.Plans.Expressions.NullOrdering;
using CoreSortDirection = DeltaSharp.Plans.Expressions.SortDirection;
using CoreSortOrder = DeltaSharp.Plans.Expressions.SortOrder;
using EngineAggregateExpression = DeltaSharp.Engine.Execution.AggregateExpression;
using EngineAggregateFunction = DeltaSharp.Engine.Execution.AggregateFunction;
using EngineArithmeticExpression = DeltaSharp.Engine.Execution.ArithmeticExpression;
using EngineArithmeticOperator = DeltaSharp.Engine.Execution.ArithmeticOperator;
using EngineCastExpression = DeltaSharp.Engine.Execution.CastExpression;
using EngineColumnReference = DeltaSharp.Engine.Execution.ColumnReference;
using EngineComparisonExpression = DeltaSharp.Engine.Execution.ComparisonExpression;
using EngineComparisonOperator = DeltaSharp.Engine.Execution.ComparisonOperator;
using EngineIsNullExpression = DeltaSharp.Engine.Execution.IsNullExpression;
using EngineLiteral = DeltaSharp.Engine.Execution.Literal;
using EngineLogicalExpression = DeltaSharp.Engine.Execution.LogicalExpression;
using EngineLogicalOperator = DeltaSharp.Engine.Execution.LogicalOperator;
using EngineNullOrdering = DeltaSharp.Engine.Execution.NullOrdering;
using EnginePhysicalExpression = DeltaSharp.Engine.Execution.PhysicalExpression;
using EngineSortDirection = DeltaSharp.Engine.Execution.SortDirection;
using EngineSortOrder = DeltaSharp.Engine.Execution.SortOrder;
using ExprAlias = DeltaSharp.Plans.Expressions.Alias;
using ExprAnd = DeltaSharp.Plans.Expressions.And;
using ExprAttributeReference = DeltaSharp.Plans.Expressions.AttributeReference;
using ExprBinaryArithmetic = DeltaSharp.Plans.Expressions.BinaryArithmetic;
using ExprBinaryComparison = DeltaSharp.Plans.Expressions.BinaryComparison;
using ExprCast = DeltaSharp.Plans.Expressions.Cast;
using ExprIsNotNull = DeltaSharp.Plans.Expressions.IsNotNull;
using ExprIsNull = DeltaSharp.Plans.Expressions.IsNull;
using ExprNot = DeltaSharp.Plans.Expressions.Not;
using ExprOr = DeltaSharp.Plans.Expressions.Or;
using ExprResolvedFunction = DeltaSharp.Plans.Expressions.ResolvedFunction;

namespace DeltaSharp.Executor;

/// <summary>
/// Translates a resolved Core <see cref="CoreExpr"/> into an EPIC-03
/// <see cref="EnginePhysicalExpression"/>, resolving each <see cref="ExprAttributeReference"/> to a
/// column ordinal against a supplied input attribute list. Every node whose translation is not
/// modelled in M1 (for example <c>CaseWhen</c> or a scalar function) raises the deterministic
/// <see cref="UnsupportedPlanException"/> — the bridge never emits an approximate expression.
/// </summary>
internal sealed class PhysicalExpressionTranslator
{
    private readonly Dictionary<long, int> _ordinalByExprId;
    private readonly AnsiMode _mode;

    private PhysicalExpressionTranslator(IReadOnlyList<ExprAttributeReference> input, AnsiMode mode)
    {
        _mode = mode;
        _ordinalByExprId = new Dictionary<long, int>(input.Count);
        for (int i = 0; i < input.Count; i++)
        {
            // A later duplicate id would shadow an earlier column; keep the first (leftmost) binding.
            _ordinalByExprId.TryAdd(input[i].ExprId.Value, i);
        }
    }

    /// <summary>Creates a translator that resolves references against <paramref name="input"/>.</summary>
    /// <param name="input">The ordered input attributes (a child's output, or one join side).</param>
    /// <param name="mode">The ANSI strictness lens for arithmetic/casts.</param>
    /// <returns>A translator bound to that input.</returns>
    public static PhysicalExpressionTranslator For(IReadOnlyList<ExprAttributeReference> input, AnsiMode mode) =>
        new(input, mode);

    /// <summary>Translates a value/predicate expression to its Engine equivalent.</summary>
    /// <param name="expression">The resolved Core expression.</param>
    /// <returns>The equivalent Engine physical expression.</returns>
    /// <exception cref="UnsupportedPlanException">The expression has no M1 mapping.</exception>
    public EnginePhysicalExpression Translate(CoreExpr expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        switch (expression)
        {
            case ExprAttributeReference attribute:
                return ColumnRef(attribute);

            case ExprAlias alias:
                return Translate(alias.Child);

            case CoreLiteral literal:
                return LiteralOf(literal);

            case ExprBinaryComparison comparison:
                return new EngineComparisonExpression(
                    Translate(comparison.Left), Translate(comparison.Right), MapComparison(comparison.Operator));

            case ExprBinaryArithmetic arithmetic:
                return new EngineArithmeticExpression(
                    Translate(arithmetic.Left), Translate(arithmetic.Right), MapArithmetic(arithmetic.Operator), _mode);

            case ExprAnd and:
                return new EngineLogicalExpression(Translate(and.Left), Translate(and.Right), EngineLogicalOperator.And);

            case ExprOr or:
                return new EngineLogicalExpression(Translate(or.Left), Translate(or.Right), EngineLogicalOperator.Or);

            case ExprNot not:
                return new EngineLogicalExpression(Translate(not.Child));

            case ExprIsNull isNull:
                return new EngineIsNullExpression(Translate(isNull.Child), negated: false);

            case ExprIsNotNull isNotNull:
                return new EngineIsNullExpression(Translate(isNotNull.Child), negated: true);

            case ExprCast cast:
                return new EngineCastExpression(Translate(cast.Child), cast.TargetType, _mode);

            case ExprResolvedFunction function:
                throw UnsupportedPlanException.ForExpression(
                    $"{function.Name}()",
                    "only aggregate functions in an Aggregate's aggregate list are supported in M1 (scalar functions are deferred)");

            default:
                throw UnsupportedPlanException.ForExpression(
                    expression.NodeName, "no M1 translation to an EPIC-03 physical expression");
        }
    }

    /// <summary>Translates a resolved sort key (<see cref="CoreSortOrder"/>) to an Engine sort order.</summary>
    /// <param name="order">The resolved Core sort order.</param>
    /// <returns>The Engine sort order (key expression + direction + null ordering).</returns>
    public EngineSortOrder TranslateSortOrder(CoreSortOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        EngineSortDirection direction = order.Direction == CoreSortDirection.Ascending
            ? EngineSortDirection.Ascending
            : EngineSortDirection.Descending;
        EngineNullOrdering nulls = order.NullOrdering == CoreNullOrdering.NullsFirst
            ? EngineNullOrdering.NullsFirst
            : EngineNullOrdering.NullsLast;
        return new EngineSortOrder(Translate(order.Child), direction, nulls);
    }

    /// <summary>Translates a resolved aggregate function term to an Engine aggregate expression.</summary>
    /// <param name="function">The resolved aggregate <see cref="ExprResolvedFunction"/>.</param>
    /// <returns>The Engine aggregate expression (function + optional input).</returns>
    public EngineAggregateExpression TranslateAggregate(ExprResolvedFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        EngineAggregateFunction fn = function.Name switch
        {
            "count" => EngineAggregateFunction.Count,
            "sum" => EngineAggregateFunction.Sum,
            "min" => EngineAggregateFunction.Min,
            "max" => EngineAggregateFunction.Max,
            "avg" => EngineAggregateFunction.Average,
            _ => throw UnsupportedPlanException.ForExpression(
                $"{function.Name}()", "not a supported M1 aggregate (count/sum/min/max/avg)"),
        };

        if (function.IsDistinct)
        {
            throw UnsupportedPlanException.ForExpression(
                $"{function.Name}(DISTINCT …)", "DISTINCT aggregates are deferred");
        }

        if (function.Arguments.Count != 1)
        {
            throw UnsupportedPlanException.ForExpression(
                $"{function.Name}()", $"expected exactly one argument but found {function.Arguments.Count}");
        }

        CoreExpr argument = function.Arguments[0];

        // COUNT(*)/COUNT(literal) counts every row: model it as Engine COUNT(*) (null input).
        if (fn == EngineAggregateFunction.Count && argument is CoreLiteral { IsNull: false })
        {
            return new EngineAggregateExpression(EngineAggregateFunction.Count, input: null, _mode);
        }

        return new EngineAggregateExpression(fn, Translate(argument), _mode);
    }

    private EngineColumnReference ColumnRef(ExprAttributeReference attribute)
    {
        if (!_ordinalByExprId.TryGetValue(attribute.ExprId.Value, out int ordinal))
        {
            throw new UnsupportedPlanException(
                $"Could not resolve attribute '{attribute.Name}#{attribute.ExprId}' against the operator's input; "
                + "the reconstructed output ExprIds drifted from the plan (needs the #172/#173 output seam).");
        }

        return new EngineColumnReference(ordinal, attribute.Type, attribute.Nullable);
    }

    private static EngineLiteral LiteralOf(CoreLiteral literal)
    {
        DataType type = literal.Type;
        if (literal.IsNull)
        {
            return EngineLiteral.Null(type);
        }

        object value = literal.Value!;
        return type switch
        {
            BooleanType => EngineLiteral.OfBoolean((bool)value),
            ByteType => EngineLiteral.OfByte((sbyte)value),
            ShortType => EngineLiteral.OfShort((short)value),
            IntegerType => EngineLiteral.OfInt((int)value),
            LongType => EngineLiteral.OfLong((long)value),
            FloatType => EngineLiteral.OfFloat((float)value),
            DoubleType => EngineLiteral.OfDouble((double)value),
            DateType => EngineLiteral.OfDate((int)value),
            TimestampType => EngineLiteral.OfTimestamp((long)value),
            StringType => EngineLiteral.OfString((string)value),
            BinaryType => EngineLiteral.OfBinary((byte[])value),
            DecimalType decimalType => EngineLiteral.OfDecimal((Int128)value, decimalType),
            _ => throw UnsupportedPlanException.ForExpression(
                "Literal", $"no M1 mapping for literal of type '{type.SimpleString}'"),
        };
    }

    private static EngineComparisonOperator MapComparison(CoreComparisonOperator op) => op switch
    {
        CoreComparisonOperator.Equal => EngineComparisonOperator.Equal,
        CoreComparisonOperator.NotEqual => EngineComparisonOperator.NotEqual,
        CoreComparisonOperator.LessThan => EngineComparisonOperator.LessThan,
        CoreComparisonOperator.LessThanOrEqual => EngineComparisonOperator.LessThanOrEqual,
        CoreComparisonOperator.GreaterThan => EngineComparisonOperator.GreaterThan,
        CoreComparisonOperator.GreaterThanOrEqual => EngineComparisonOperator.GreaterThanOrEqual,
        _ => throw UnsupportedPlanException.ForExpression("BinaryComparison", $"unknown operator '{op}'"),
    };

    private static EngineArithmeticOperator MapArithmetic(CoreArithmeticOperator op) => op switch
    {
        CoreArithmeticOperator.Add => EngineArithmeticOperator.Add,
        CoreArithmeticOperator.Subtract => EngineArithmeticOperator.Subtract,
        CoreArithmeticOperator.Multiply => EngineArithmeticOperator.Multiply,
        CoreArithmeticOperator.Divide => EngineArithmeticOperator.Divide,
        CoreArithmeticOperator.Remainder => EngineArithmeticOperator.Remainder,
        _ => throw UnsupportedPlanException.ForExpression("BinaryArithmetic", $"unknown operator '{op}'"),
    };
}
