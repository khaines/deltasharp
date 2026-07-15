using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Lowers a fixed-width <see cref="PhysicalExpression"/> tree into a single JIT-compiled
/// <see cref="FusedRowKernel"/> (STORY-03.4.2, ADR-0001 optional codegen tier). Each node is lowered
/// — Spark-codegen style — into two locals, a <c>bool isNull</c> flag and a value carrier, accumulated
/// (children first, post-order) into one root <see cref="BlockExpression"/>; value computation is
/// guarded by <c>if (!isNull)</c> so a null operand never triggers a spurious overflow throw, exactly
/// as the interpreter skips computation for null lanes. There are <b>no per-node intermediate
/// vectors</b> — that elimination is the fusion win. Every value/validity rule is emitted to be
/// byte-identical to the interpreted evaluator (the parity oracle): primitive IEEE ops and the
/// comparison-to-boolean step are emitted directly; everything fallible routes through
/// <see cref="CompiledScalarOps"/>, which mirrors the interpreter's private methods (and exception
/// messages) verbatim.
/// </summary>
/// <remarks>
/// This type emits IL via <c>Expression&lt;TDelegate&gt;.Compile()</c>, so it is annotated
/// <see cref="RequiresDynamicCodeAttribute"/> and is reachable only behind the
/// <see cref="ExecutionBackends.IsCompiledBackendAvailable"/> feature guard; NativeAOT elides it.
/// All <see cref="MethodInfo"/> handles come from delegate method-groups (no <c>GetMethod(string)</c>
/// / <c>MakeGenericMethod</c>), so the lowering is also trim-analyzer clean.
/// </remarks>
[RequiresDynamicCode(
    "The compiled expression evaluator emits IL via Expression.Compile (ADR-0001 optional codegen " +
    "tier); it is elided from NativeAOT and only built when RuntimeFeature.IsDynamicCodeSupported is true.")]
internal static class CompiledExpressionLowering
{
    // Reused interpreter helpers (parity by construction): floating order, decimal compare/widen, Kleene 3VL.
    private static readonly MethodInfo CompareDoubleMethod = ((Func<double, double, int>)ScalarReader.CompareDouble).Method;
    private static readonly MethodInfo CompareDecimalMethod = ((Func<DecimalValue, DecimalValue, int>)ScalarReader.CompareDecimal).Method;
    private static readonly MethodInfo ToDoubleMethod = ((Func<DecimalValue, double>)ScalarReader.ToDouble).Method;
    private static readonly MethodInfo KleeneAndMethod = ((Func<bool?, bool?, bool?>)NullPropagation.KleeneAnd).Method;
    private static readonly MethodInfo KleeneOrMethod = ((Func<bool?, bool?, bool?>)NullPropagation.KleeneOr).Method;
    private static readonly MethodInfo KleeneNotMethod = ((Func<bool?, bool?>)NullPropagation.KleeneNot).Method;
    private static readonly MethodInfo TimestampToDateMethod = ((Func<long?, AnsiMode, int?>)TemporalValues.TimestampToDate).Method;
    private static readonly MethodInfo DateToTimestampMethod = ((Func<int?, AnsiMode, long?>)TemporalValues.DateToTimestamp).Method;

    /// <summary>Lowers and compiles <paramref name="expression"/> into a reusable fused kernel.</summary>
    /// <remarks>The caller (<see cref="CompiledExpressionEvaluators"/>) must have verified
    /// <see cref="CompiledExpressionEvaluators.CanFuse"/> first; unsupported shapes are not reachable here.</remarks>
    public static CompiledFusion Lower(PhysicalExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var context = new LoweringContext();
        Lowered root = LowerNode(expression, context);
        context.Statements.Add(
            Expression.IfThenElse(
                root.IsNull,
                Expression.Call(CompiledVectorAccess.AppendNullMethod, context.Output),
                AppendValue(context.Output, root)));

        BlockExpression body = Expression.Block(context.Variables, context.Statements);
        Expression<FusedRowKernel> lambda =
            Expression.Lambda<FusedRowKernel>(body, context.Inputs, context.Row, context.Output);

        // Compiled fast-path tier — reachable only when RuntimeFeature.IsDynamicCodeSupported is true
        // and elided from NativeAOT. Justified by ADR-0001 (optional codegen tier); see
        // docs/engineering/design/api-governance.md ("Requesting a scoped exception").
#pragma warning disable RS0030 // Banned API: Expression.Compile — scoped ADR-0001 codegen tier.
        FusedRowKernel kernel = lambda.Compile();
#pragma warning restore RS0030
        return new CompiledFusion(kernel, context.SlotOrdinals());
    }

    private static Lowered LowerNode(PhysicalExpression expression, LoweringContext context) => expression switch
    {
        ColumnReference column => LowerColumnReference(column, context),
        Literal literal => LowerLiteral(literal, context),
        ArithmeticExpression arithmetic => LowerArithmetic(arithmetic, context),
        ComparisonExpression comparison => LowerComparison(comparison, context),
        LogicalExpression logical => LowerLogical(logical, context),
        CastExpression cast => LowerCast(cast, context),
        IsNullExpression isNull => LowerIsNull(isNull, context),
        _ => throw new InvalidOperationException(
            $"CanFuse should have rejected '{expression.GetType().Name}' before lowering."),
    };

    // ---- Leaves ---------------------------------------------------------------------------------

    private static Lowered LowerColumnReference(ColumnReference column, LoweringContext context)
    {
        ParameterExpression isNull = context.NewVariable(typeof(bool), "colNull");
        ParameterExpression value = context.NewVariable(CarrierOf(column.Type), "colVal");
        Expression vector = Expression.ArrayIndex(context.Inputs, Expression.Constant(context.Slot(column.Ordinal)));

        context.Statements.Add(Expression.Assign(isNull, Expression.Call(CompiledVectorAccess.IsRowNullMethod, vector, context.Row)));
        context.Statements.Add(Expression.Assign(value, ReadStorage(column.Type, vector, context.Row)));
        return new Lowered(isNull, value, column.Type);
    }

    private static Lowered LowerLiteral(Literal literal, LoweringContext context)
    {
        ParameterExpression isNull = context.NewVariable(typeof(bool), "litNull");
        ParameterExpression value = context.NewVariable(CarrierOf(literal.Type), "litVal");

        context.Statements.Add(Expression.Assign(isNull, Expression.Constant(literal.IsNull)));
        if (!literal.IsNull)
        {
            context.Statements.Add(Expression.Assign(value, LiteralConstant(literal)));
        }

        return new Lowered(isNull, value, literal.Type);
    }

    // ---- Arithmetic (mirrors ArithmeticEvaluator) -----------------------------------------------

    private static Lowered LowerArithmetic(ArithmeticExpression node, LoweringContext context)
    {
        Lowered left = LowerNode(node.Left, context);
        Lowered right = LowerNode(node.Right, context);
        ParameterExpression isNull = context.NewVariable(typeof(bool), "ariNull");
        ParameterExpression value = context.NewVariable(CarrierOf(node.Type), "ariVal");

        context.Statements.Add(Expression.Assign(isNull, Expression.OrElse(left.IsNull, right.IsNull)));
        context.Statements.Add(Expression.IfThen(Expression.Not(isNull), ComputeArithmetic(node, left, right, isNull, value)));
        return new Lowered(isNull, value, node.Type);
    }

    private static Expression ComputeArithmetic(
        ArithmeticExpression node, Lowered left, Lowered right, ParameterExpression isNull, ParameterExpression value)
    {
        switch (node.EvalKind)
        {
            case ArithmeticEvalKind.Integral:
                {
                    (long min, long max) = IntegralRange(node.Type);
                    Expression call = Expression.Call(
                        CompiledScalarOps.TryIntegralMethod,
                        Expression.Constant(node.Operator),
                        AsInt64(left),
                        AsInt64(right),
                        Expression.Constant(min),
                        Expression.Constant(max),
                        Expression.Constant(node.Mode),
                        Expression.Constant(node.Type.SimpleString));
                    return FallibleAssign(
                        call, typeof(long?), CompiledScalarOps.HasValueInt64Method, CompiledScalarOps.UnwrapInt64Method,
                        unwrapped => Narrow(unwrapped, value.Type), isNull, value);
                }

            case ArithmeticEvalKind.Single:
                if (node.Operator == ArithmeticOperator.Remainder)
                {
                    Expression call = Expression.Call(
                        CompiledScalarOps.TrySingleRemainderMethod,
                        Expression.Constant(node.Operator), AsSingle(left), AsSingle(right), Expression.Constant(node.Mode));
                    return FallibleAssign(
                        call, typeof(float?), CompiledScalarOps.HasValueSingleMethod, CompiledScalarOps.UnwrapSingleMethod,
                        unwrapped => unwrapped, isNull, value);
                }

                return Expression.Assign(value, PrimitiveFloating(node.Operator, AsSingle(left), AsSingle(right)));

            case ArithmeticEvalKind.Double:
                if (node.Operator is ArithmeticOperator.Divide or ArithmeticOperator.Remainder)
                {
                    Expression call = Expression.Call(
                        CompiledScalarOps.TryDoubleDivideOrRemainderMethod,
                        Expression.Constant(node.Operator), AsDouble(left), AsDouble(right), Expression.Constant(node.Mode));
                    return FallibleAssign(
                        call, typeof(double?), CompiledScalarOps.HasValueDoubleMethod, CompiledScalarOps.UnwrapDoubleMethod,
                        unwrapped => unwrapped, isNull, value);
                }

                return Expression.Assign(value, PrimitiveFloating(node.Operator, AsDouble(left), AsDouble(right)));

            default:
                {
                    Expression call = Expression.Call(
                        CompiledScalarOps.TryDecimalMethod,
                        Expression.Constant(node.Operator),
                        AsDecimal(left),
                        AsDecimal(right),
                        Expression.Constant((DecimalType)node.Type, typeof(DecimalType)),
                        Expression.Constant(node.Mode));
                    return FallibleAssign(
                        call, typeof(DecimalValue?), CompiledScalarOps.HasValueDecimalMethod, CompiledScalarOps.UnwrapDecimalMethod,
                        unwrapped => unwrapped, isNull, value);
                }
        }
    }

    // ---- Comparison (mirrors ComparisonEvaluator) -----------------------------------------------

    private static Lowered LowerComparison(ComparisonExpression node, LoweringContext context)
    {
        Lowered left = LowerNode(node.Left, context);
        Lowered right = LowerNode(node.Right, context);
        ParameterExpression isNull = context.NewVariable(typeof(bool), "cmpNull");
        ParameterExpression value = context.NewVariable(typeof(bool), "cmpVal");

        context.Statements.Add(Expression.Assign(isNull, Expression.OrElse(left.IsNull, right.IsNull)));
        Expression boolean = ComparisonToBoolean(node.Operator, CompareSign(node, left, right));
        context.Statements.Add(Expression.IfThen(Expression.Not(isNull), Expression.Assign(value, boolean)));
        return new Lowered(isNull, value, node.Type);
    }

    private static Expression CompareSign(ComparisonExpression node, Lowered left, Lowered right) => node.EvalKind switch
    {
        ComparisonEvalKind.Int64 or ComparisonEvalKind.Date or ComparisonEvalKind.Timestamp or ComparisonEvalKind.Boolean =>
            Expression.Call(CompiledScalarOps.CompareInt64Method, AsInt64(left), AsInt64(right)),
        ComparisonEvalKind.Double => Expression.Call(CompareDoubleMethod, AsDouble(left), AsDouble(right)),
        ComparisonEvalKind.Decimal => Expression.Call(CompareDecimalMethod, AsDecimal(left), AsDecimal(right)),
        ComparisonEvalKind.DateTimestamp =>
            Expression.Call(CompiledScalarOps.CompareInt64Method, PromoteToMicros(left), PromoteToMicros(right)),
        _ => throw new InvalidOperationException($"CanFuse should have rejected comparison kind '{node.EvalKind}'."),
    };

    private static Expression PromoteToMicros(Lowered operand) =>
        operand.Type is DateType
            ? Expression.Multiply(AsInt64(operand), Expression.Constant(TemporalValues.MicrosPerDay))
            : AsInt64(operand);

    private static Expression ComparisonToBoolean(ComparisonOperator op, Expression sign)
    {
        ConstantExpression zero = Expression.Constant(0);
        return op switch
        {
            ComparisonOperator.Equal => Expression.Equal(sign, zero),
            ComparisonOperator.NotEqual => Expression.NotEqual(sign, zero),
            ComparisonOperator.LessThan => Expression.LessThan(sign, zero),
            ComparisonOperator.LessThanOrEqual => Expression.LessThanOrEqual(sign, zero),
            ComparisonOperator.GreaterThan => Expression.GreaterThan(sign, zero),
            ComparisonOperator.GreaterThanOrEqual => Expression.GreaterThanOrEqual(sign, zero),
            _ => throw new InvalidOperationException($"Unknown comparison operator '{op}'."),
        };
    }

    // ---- Logical (mirrors LogicalEvaluator; reuses NullPropagation Kleene) -----------------------

    private static Lowered LowerLogical(LogicalExpression node, LoweringContext context)
    {
        Lowered left = LowerNode(node.Left, context);
        ParameterExpression isNull = context.NewVariable(typeof(bool), "logNull");
        ParameterExpression value = context.NewVariable(typeof(bool), "logVal");

        Expression kleene;
        if (node.Operator == LogicalOperator.Not)
        {
            kleene = Expression.Call(KleeneNotMethod, ToNullableBool(left));
        }
        else
        {
            Lowered right = LowerNode(node.Right, context);
            kleene = Expression.Call(
                node.Operator == LogicalOperator.And ? KleeneAndMethod : KleeneOrMethod,
                ToNullableBool(left),
                ToNullableBool(right));
        }

        ParameterExpression result = context.NewVariable(typeof(bool?), "logK");
        context.Statements.Add(Expression.Assign(result, kleene));
        context.Statements.Add(Expression.Assign(isNull, Expression.Not(Expression.Call(CompiledScalarOps.HasValueBoolMethod, result))));
        context.Statements.Add(Expression.Assign(value, Expression.Call(CompiledScalarOps.UnwrapBoolMethod, result)));
        return new Lowered(isNull, value, node.Type);
    }

    private static Expression ToNullableBool(Lowered operand) =>
        Expression.Condition(
            operand.IsNull,
            Expression.Constant(null, typeof(bool?)),
            Expression.Convert(operand.Value, typeof(bool?)));

    // ---- Cast (mirrors CastEvaluator) -----------------------------------------------------------

    private static Lowered LowerCast(CastExpression node, LoweringContext context)
    {
        Lowered child = LowerNode(node.Child, context);
        ParameterExpression isNull = context.NewVariable(typeof(bool), "castNull");
        ParameterExpression value = context.NewVariable(CarrierOf(node.Type), "castVal");

        context.Statements.Add(Expression.Assign(isNull, child.IsNull)); // null in -> null out
        context.Statements.Add(Expression.IfThen(Expression.Not(isNull), ComputeCast(node, child, isNull, value)));
        return new Lowered(isNull, value, node.Type);
    }

    private static Expression ComputeCast(CastExpression node, Lowered child, ParameterExpression isNull, ParameterExpression value)
    {
        DataType source = node.Child.Type;
        DataType target = node.Type;
        if (source.Equals(target))
        {
            return Expression.Assign(value, child.Value); // identity copy
        }

        switch (target)
        {
            case BooleanType:
                return Expression.Assign(value, CastToBoolean(source, child));

            case ByteType or ShortType or IntegerType or LongType:
                return CastToIntegral(node, source, child, isNull, value);

            case FloatType:
                return Expression.Assign(value, Expression.Convert(ReadAsDouble(source, child), typeof(float)));

            case DoubleType:
                return Expression.Assign(value, ReadAsDouble(source, child));

            case DecimalType decimalTarget:
                {
                    Expression sourceDecimal = source is BooleanType
                        ? Expression.Call(CompiledScalarOps.MakeDecimalMethod, BoolToInt64(child), Expression.Constant(0))
                        : AsDecimal(child);
                    Expression call = Expression.Call(
                        CompiledScalarOps.TryFitDecimalMethod, sourceDecimal,
                        Expression.Constant(decimalTarget, typeof(DecimalType)), Expression.Constant(node.Mode));
                    return FallibleAssign(
                        call, typeof(DecimalValue?), CompiledScalarOps.HasValueDecimalMethod, CompiledScalarOps.UnwrapDecimalMethod,
                        unwrapped => unwrapped, isNull, value);
                }

            case DateType:
                {
                    Expression call = Expression.Call(
                        TimestampToDateMethod, Expression.Convert(AsInt64(child), typeof(long?)), Expression.Constant(node.Mode));
                    return FallibleAssign(
                        call, typeof(int?), CompiledScalarOps.HasValueNullableInt32Method, CompiledScalarOps.UnwrapNullableInt32Method,
                        unwrapped => unwrapped, isNull, value);
                }

            case TimestampType:
                {
                    Expression call = Expression.Call(
                        DateToTimestampMethod,
                        Expression.Convert(Expression.Convert(AsInt64(child), typeof(int)), typeof(int?)),
                        Expression.Constant(node.Mode));
                    return FallibleAssign(
                        call, typeof(long?), CompiledScalarOps.HasValueInt64Method, CompiledScalarOps.UnwrapInt64Method,
                        unwrapped => unwrapped, isNull, value);
                }

            default:
                throw new InvalidOperationException($"CanFuse should have rejected cast to '{target.SimpleString}'.");
        }
    }

    private static Expression CastToIntegral(
        CastExpression node, DataType source, Lowered child, ParameterExpression isNull, ParameterExpression value)
    {
        // Boolean source is always in range (0/1) — the interpreter applies no range check.
        if (source is BooleanType)
        {
            return Expression.Assign(value, Narrow(BoolToInt64(child), value.Type));
        }

        (long min, long max, double upperExclusive) = CastIntegralRange(node.Type);
        ConstantExpression typeName = Expression.Constant(node.Type.SimpleString);
        ConstantExpression mode = Expression.Constant(node.Mode);
        Expression call = source switch
        {
            FloatType or DoubleType => Expression.Call(
                CompiledScalarOps.TryCastDoubleToIntegralMethod,
                AsDouble(child), Expression.Constant(min), Expression.Constant(upperExclusive), mode, typeName),
            DecimalType => Expression.Call(
                CompiledScalarOps.TryCastDecimalToIntegralMethod,
                child.Value, Expression.Constant(min), Expression.Constant(max), mode, typeName),
            _ => Expression.Call(
                CompiledScalarOps.TryCastIntegralToIntegralMethod,
                AsInt64(child), Expression.Constant(min), Expression.Constant(max), mode, typeName),
        };

        return FallibleAssign(
            call, typeof(long?), CompiledScalarOps.HasValueInt64Method, CompiledScalarOps.UnwrapInt64Method,
            unwrapped => Narrow(unwrapped, value.Type), isNull, value);
    }

    private static Expression CastToBoolean(DataType source, Lowered child) => source switch
    {
        FloatType or DoubleType => Expression.NotEqual(AsDouble(child), Expression.Constant(0d)),
        DecimalType => Expression.Call(CompiledScalarOps.DecimalToBooleanMethod, child.Value),
        _ => Expression.NotEqual(AsInt64(child), Expression.Constant(0L)),
    };

    private static Expression ReadAsDouble(DataType source, Lowered child) =>
        source is BooleanType
            ? Expression.Condition(child.Value, Expression.Constant(1d), Expression.Constant(0d))
            : AsDouble(child);

    // ---- IsNull (mirrors NullCheckEvaluator) ----------------------------------------------------

    private static Lowered LowerIsNull(IsNullExpression node, LoweringContext context)
    {
        // The child's full setup still runs (it may throw on ANSI overflow, exactly like the
        // interpreter materializing the child vector); only its validity bit feeds the result.
        Lowered child = LowerNode(node.Child, context);
        ParameterExpression isNull = context.NewVariable(typeof(bool), "isnNull");
        ParameterExpression value = context.NewVariable(typeof(bool), "isnVal");

        context.Statements.Add(Expression.Assign(isNull, Expression.Constant(false))); // IS [NOT] NULL is never null
        context.Statements.Add(Expression.Assign(value, node.Negated ? Expression.Not(child.IsNull) : child.IsNull));
        return new Lowered(isNull, value, node.Type);
    }

    // ---- Shared lowering helpers ----------------------------------------------------------------

    /// <summary>
    /// Emits <c>temp = call; if (temp.HasValue) value = project(temp.Value); else isNull = true;</c>,
    /// the compiled form of the interpreter's "fallible op returns false -&gt; AppendNull" pattern.
    /// </summary>
    private static Expression FallibleAssign(
        Expression call, Type nullableType, MethodInfo hasValue, MethodInfo unwrap,
        Func<Expression, Expression> project, ParameterExpression isNull, ParameterExpression value)
    {
        ParameterExpression temp = Expression.Variable(nullableType, "fallible");
        return Expression.Block(
            new[] { temp },
            Expression.Assign(temp, call),
            Expression.IfThenElse(
                Expression.Call(hasValue, temp),
                Expression.Assign(value, project(Expression.Call(unwrap, temp))),
                Expression.Assign(isNull, Expression.Constant(true))));
    }

    private static Expression PrimitiveFloating(ArithmeticOperator op, Expression a, Expression b) => op switch
    {
        ArithmeticOperator.Add => Expression.Add(a, b),
        ArithmeticOperator.Subtract => Expression.Subtract(a, b),
        ArithmeticOperator.Multiply => Expression.Multiply(a, b),
        _ => throw new InvalidOperationException($"Floating fast path does not handle '{op}'."),
    };

    /// <summary>Widens an operand to a sign-extended <see cref="long"/>; mirrors <see cref="ScalarReader.ReadInt64"/>.</summary>
    private static Expression AsInt64(Lowered operand) => operand.Type switch
    {
        BooleanType => BoolToInt64(operand),
        ByteType => Expression.Convert(Expression.Convert(operand.Value, typeof(sbyte)), typeof(long)), // (long)(sbyte)b
        ShortType or IntegerType or DateType => Expression.Convert(operand.Value, typeof(long)),
        LongType or TimestampType or TimestampNtzType => operand.Value,
        _ => throw new InvalidOperationException($"'{operand.Type.SimpleString}' cannot be read as a 64-bit integer."),
    };

    private static Expression BoolToInt64(Lowered operand) =>
        Expression.Condition(operand.Value, Expression.Constant(1L), Expression.Constant(0L));

    /// <summary>Widens an operand to IEEE <see cref="double"/>; mirrors <see cref="ScalarReader.ReadDouble"/>.</summary>
    private static Expression AsDouble(Lowered operand) => operand.Type switch
    {
        FloatType => Expression.Convert(operand.Value, typeof(double)),
        DoubleType => operand.Value,
        DecimalType => Expression.Call(ToDoubleMethod, operand.Value),
        _ => Expression.Convert(AsInt64(operand), typeof(double)),
    };

    /// <summary>Narrows an operand to IEEE <see cref="float"/>; mirrors <see cref="ScalarReader.ReadSingle"/>.</summary>
    private static Expression AsSingle(Lowered operand) => operand.Type switch
    {
        FloatType => operand.Value,
        DoubleType => Expression.Convert(operand.Value, typeof(float)),
        DecimalType => Expression.Convert(Expression.Call(ToDoubleMethod, operand.Value), typeof(float)),
        _ => Expression.Convert(AsInt64(operand), typeof(float)),
    };

    /// <summary>Reads an operand as an exact <see cref="DecimalValue"/>; mirrors <see cref="ScalarReader.ReadDecimal"/>.</summary>
    private static Expression AsDecimal(Lowered operand) => operand.Type switch
    {
        DecimalType => operand.Value,
        ByteType or ShortType or IntegerType or LongType => Expression.Call(
            CompiledScalarOps.MakeDecimalMethod, AsInt64(operand), Expression.Constant(0)),
        _ => throw new InvalidOperationException($"'{operand.Type.SimpleString}' cannot be read as a decimal."),
    };

    private static Expression Narrow(Expression int64, Type carrier) =>
        carrier == typeof(long) ? int64 : Expression.Convert(int64, carrier);

    private static Expression ReadStorage(DataType type, Expression vector, Expression row) => type switch
    {
        BooleanType => Expression.Call(CompiledVectorAccess.ReadBooleanMethod, vector, row),
        ByteType => Expression.Call(CompiledVectorAccess.ReadByteMethod, vector, row),
        ShortType => Expression.Call(CompiledVectorAccess.ReadInt16Method, vector, row),
        IntegerType or DateType => Expression.Call(CompiledVectorAccess.ReadInt32Method, vector, row),
        LongType or TimestampType or TimestampNtzType => Expression.Call(CompiledVectorAccess.ReadInt64Method, vector, row),
        FloatType => Expression.Call(CompiledVectorAccess.ReadSingleMethod, vector, row),
        DoubleType => Expression.Call(CompiledVectorAccess.ReadDoubleMethod, vector, row),
        DecimalType { IsCompact: true } compact => Expression.Call(
            CompiledScalarOps.MakeDecimalMethod, Expression.Call(CompiledVectorAccess.ReadDecimalCompactMethod, vector, row),
            Expression.Constant(compact.Scale)),
        DecimalType wide => Expression.Call(
            CompiledScalarOps.MakeDecimalWideMethod, Expression.Call(CompiledVectorAccess.ReadDecimalWideMethod, vector, row),
            Expression.Constant(wide.Scale)),
        _ => throw new InvalidOperationException($"No compiled storage read for type '{type.SimpleString}'."),
    };

    private static Expression LiteralConstant(Literal literal) => literal.Type switch
    {
        BooleanType => Expression.Constant((bool)literal.Value!, typeof(bool)),
        ByteType => Expression.Constant(unchecked((byte)(sbyte)literal.Value!), typeof(byte)),
        ShortType => Expression.Constant((short)literal.Value!, typeof(short)),
        IntegerType or DateType => Expression.Constant((int)literal.Value!, typeof(int)),
        LongType or TimestampType or TimestampNtzType => Expression.Constant((long)literal.Value!, typeof(long)),
        FloatType => Expression.Constant((float)literal.Value!, typeof(float)),
        DoubleType => Expression.Constant((double)literal.Value!, typeof(double)),
        DecimalType decimalType => Expression.Constant(
            new DecimalValue((Int128)literal.Value!, decimalType.Scale), typeof(DecimalValue)),
        _ => throw new InvalidOperationException($"No compiled literal for type '{literal.Type.SimpleString}'."),
    };

    private static Expression AppendValue(ParameterExpression output, Lowered root)
    {
        MethodInfo append = root.Type switch
        {
            BooleanType => CompiledVectorAccess.AppendBooleanMethod,
            ByteType => CompiledVectorAccess.AppendByteMethod,
            ShortType => CompiledVectorAccess.AppendInt16Method,
            IntegerType or DateType => CompiledVectorAccess.AppendInt32Method,
            LongType or TimestampType or TimestampNtzType => CompiledVectorAccess.AppendInt64Method,
            FloatType => CompiledVectorAccess.AppendSingleMethod,
            DoubleType => CompiledVectorAccess.AppendDoubleMethod,
            DecimalType => CompiledVectorAccess.AppendDecimalMethod,
            _ => throw new InvalidOperationException($"No compiled append for type '{root.Type.SimpleString}'."),
        };
        return Expression.Call(append, output, root.Value);
    }

    private static Type CarrierOf(DataType type) => type switch
    {
        BooleanType => typeof(bool),
        ByteType => typeof(byte),
        ShortType => typeof(short),
        IntegerType or DateType => typeof(int),
        LongType or TimestampType or TimestampNtzType => typeof(long),
        FloatType => typeof(float),
        DoubleType => typeof(double),
        DecimalType => typeof(DecimalValue),
        _ => throw new InvalidOperationException($"'{type.SimpleString}' has no fixed-width carrier (not fusable)."),
    };

    private static (long Min, long Max) IntegralRange(DataType type) => type switch
    {
        ByteType => (sbyte.MinValue, sbyte.MaxValue),
        ShortType => (short.MinValue, short.MaxValue),
        IntegerType => (int.MinValue, int.MaxValue),
        LongType => (long.MinValue, long.MaxValue),
        _ => throw new InvalidOperationException($"'{type.SimpleString}' is not an integral arithmetic result."),
    };

    private static (long Min, long Max, double UpperExclusive) CastIntegralRange(DataType type) => type switch
    {
        ByteType => (sbyte.MinValue, sbyte.MaxValue, sbyte.MaxValue + 1.0),
        ShortType => (short.MinValue, short.MaxValue, short.MaxValue + 1.0),
        IntegerType => (int.MinValue, int.MaxValue, int.MaxValue + 1.0),

        // long.MaxValue is not representable as a double; reject against the exact exclusive bound 2^63.
        LongType => (long.MinValue, long.MaxValue, 9223372036854775808.0),
        _ => throw new InvalidOperationException($"'{type.SimpleString}' is not an integral cast target."),
    };

    /// <summary>The lowered value/validity locals for one node (Spark-codegen carriers).</summary>
    private readonly record struct Lowered(ParameterExpression IsNull, ParameterExpression Value, DataType Type);

    /// <summary>Accumulates the block's locals, statements, and the dense input-slot map during lowering.</summary>
    private sealed class LoweringContext
    {
        private readonly List<int> _slotOrdinals = new();

        public ParameterExpression Inputs { get; } = Expression.Parameter(typeof(ColumnVector[]), "inputs");

        public ParameterExpression Row { get; } = Expression.Parameter(typeof(int), "row");

        public ParameterExpression Output { get; } = Expression.Parameter(typeof(MutableColumnVector), "output");

        public List<ParameterExpression> Variables { get; } = new();

        public List<Expression> Statements { get; } = new();

        public ParameterExpression NewVariable(Type type, string name)
        {
            ParameterExpression variable = Expression.Variable(type, name);
            Variables.Add(variable);
            return variable;
        }

        /// <summary>Maps a column ordinal to a dense input slot (first-encounter order); shared ordinals reuse a slot.</summary>
        public int Slot(int ordinal)
        {
            int index = _slotOrdinals.IndexOf(ordinal);
            if (index >= 0)
            {
                return index;
            }

            _slotOrdinals.Add(ordinal);
            return _slotOrdinals.Count - 1;
        }

        public int[] SlotOrdinals() => _slotOrdinals.ToArray();
    }
}
