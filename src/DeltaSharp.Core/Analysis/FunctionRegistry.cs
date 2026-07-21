using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;

namespace DeltaSharp.Analysis;

/// <summary>
/// The analyzer's M1 function registry: binds an <see cref="UnresolvedFunction"/> (a canonical name
/// plus already-resolved, typed argument expressions) to a typed <see cref="ResolvedFunction"/>,
/// classifying it as scalar or aggregate, applying Spark-parity type coercion (ADR-0008) to its
/// arguments — inserting <see cref="Cast"/> nodes where an implicit widening is required — and
/// computing the result type and nullability. It owns the closed M1 function set; an unknown name or
/// an ill-typed/ill-arity call is rejected with a precise <see cref="AnalysisException"/> (AC1/AC3).
/// </summary>
/// <remarks>
/// <para>
/// Scope: the representative M1 set named in EPIC-04 — the aggregates
/// <c>count/sum/avg/min/max</c> and the scalars
/// <c>upper/lower/length/trim/concat/coalesce/current_date/current_timestamp/year/month/dayofmonth/to_date</c>.
/// Anything else is an <see cref="AnalysisErrorKind.UnresolvedFunction"/> diagnostic. Full Spark
/// coercion tables (e.g. string→numeric promotion in every context, the <c>mean</c> synonym,
/// format-string overloads) are deferred and tracked; the interpreter/analyzer stays the correctness
/// reference.
/// </para>
/// <para>
/// Binding runs bottom-up inside the analyzer's reference-resolution pass, so a function's arguments
/// are already resolved and typed when it is bound; a nested function/arithmetic argument is
/// therefore coerced and typed before its parent binds.
/// </para>
/// </remarks>
internal static class FunctionRegistry
{
    /// <summary>
    /// Binds <paramref name="function"/> to a typed <see cref="ResolvedFunction"/>, or throws an
    /// <see cref="AnalysisException"/> if the name is unknown or the arguments are ill-typed/arity.
    /// </summary>
    /// <exception cref="AnalysisException">Unknown function, wrong arity, or uncoercible argument.</exception>
    public static ResolvedFunction Bind(UnresolvedFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);

        string name = function.Name.ToLowerInvariant();
        IReadOnlyList<Expression> args = function.Arguments;
        IReadOnlyList<DataType> argTypes = ArgumentTypes(function);

        return name switch
        {
            "count" => BindCount(function, args, argTypes),
            "sum" => BindSum(function, args, argTypes),
            "avg" => BindAvg(function, args, argTypes),
            "min" or "max" => BindMinMax(name, function, args, argTypes),

            "upper" or "lower" or "trim" => BindStringToString(name, function, args, argTypes),
            "length" => BindLength(function, args, argTypes),
            "concat" => BindConcat(function, args, argTypes),
            "coalesce" => BindCoalesce(function, args, argTypes),
            "current_date" => BindNullary(name, DateType.Instance, function, argTypes),
            "current_timestamp" => BindNullary(name, TimestampType.Instance, function, argTypes),
            "year" or "month" or "dayofmonth" => BindDatePart(name, function, args, argTypes),
            "to_date" => BindToDate(function, args, argTypes),

            _ => throw AnalysisException.UnknownFunction(function.Name, argTypes),
        };
    }

    // ---- aggregates -------------------------------------------------------------------------

    private static ResolvedFunction BindCount(
        UnresolvedFunction fn, IReadOnlyList<Expression> args, IReadOnlyList<DataType> argTypes)
    {
        if (args.Count < 1)
        {
            throw AnalysisException.InvalidFunctionArgument(
                fn.Name, argTypes, "count requires at least one argument.");
        }

        // count never returns NULL (an empty group counts 0); it accepts arguments of any type.
        return new ResolvedFunction(
            "count", FunctionKind.Aggregate, LongType.Instance, nullable: false, args, fn.IsDistinct,
            FunctionNullability.Fixed);
    }

    private static ResolvedFunction BindSum(
        UnresolvedFunction fn, IReadOnlyList<Expression> args, IReadOnlyList<DataType> argTypes)
    {
        DataType input = RequireUnary(fn, args, argTypes, "sum");
        if (!TypeCoercion.IsNumeric(input))
        {
            throw AnalysisException.InvalidFunctionArgument(
                fn.Name, argTypes, "sum requires a numeric argument.");
        }

        // Spark: integral sums accumulate in bigint, decimals widen by 10 digits, floats in double.
        DataType result = input switch
        {
            DecimalType d => DecimalArithmetic.Bounded(d.Precision + 10, d.Scale),
            FloatType or DoubleType => DoubleType.Instance,
            _ => LongType.Instance,
        };

        return new ResolvedFunction(
            "sum", FunctionKind.Aggregate, result, nullable: true, args, fn.IsDistinct,
            // Fixed: aggregate nullability is empty-group-driven, not argument-propagating (never widened by arg overflow).
            FunctionNullability.Fixed);
    }

    private static ResolvedFunction BindAvg(
        UnresolvedFunction fn, IReadOnlyList<Expression> args, IReadOnlyList<DataType> argTypes)
    {
        DataType input = RequireUnary(fn, args, argTypes, "avg");
        if (!TypeCoercion.IsNumeric(input))
        {
            throw AnalysisException.InvalidFunctionArgument(
                fn.Name, argTypes, "avg requires a numeric argument.");
        }

        // Spark: avg of a decimal widens precision/scale by 4; every other numeric averages in double.
        DataType result = input is DecimalType d
            ? DecimalArithmetic.Bounded(d.Precision + 4, d.Scale + 4)
            : DoubleType.Instance;

        return new ResolvedFunction(
            "avg", FunctionKind.Aggregate, result, nullable: true, args, fn.IsDistinct,
            // Fixed: aggregate nullability is empty-group-driven, not argument-propagating (never widened by arg overflow).
            FunctionNullability.Fixed);
    }

    private static ResolvedFunction BindMinMax(
        string name, UnresolvedFunction fn, IReadOnlyList<Expression> args, IReadOnlyList<DataType> argTypes)
    {
        DataType input = RequireUnary(fn, args, argTypes, name);
        if (!IsOrderable(input))
        {
            throw AnalysisException.InvalidFunctionArgument(
                fn.Name, argTypes, $"{name} requires an orderable (atomic) argument.");
        }

        // min/max return the input type; the extremum of an empty group is NULL.
        return new ResolvedFunction(
            name, FunctionKind.Aggregate, input, nullable: true, args, fn.IsDistinct,
            // Fixed: aggregate nullability is empty-group-driven, not argument-propagating (never widened by arg overflow).
            FunctionNullability.Fixed);
    }

    // ---- scalar: string ---------------------------------------------------------------------

    private static ResolvedFunction BindStringToString(
        string name, UnresolvedFunction fn, IReadOnlyList<Expression> args, IReadOnlyList<DataType> argTypes)
    {
        DataType input = RequireUnary(fn, args, argTypes, name);
        Expression coerced = CoerceToString(fn, input, args[0], name);
        return new ResolvedFunction(
            name, FunctionKind.Scalar, StringType.Instance, args[0].Nullable, new[] { coerced },
            nullPropagation: FunctionNullability.PropagatesAny);
    }

    private static ResolvedFunction BindLength(
        UnresolvedFunction fn, IReadOnlyList<Expression> args, IReadOnlyList<DataType> argTypes)
    {
        DataType input = RequireUnary(fn, args, argTypes, "length");

        // length accepts string or binary directly; other atomics coerce to string first.
        Expression coerced = input is StringType or BinaryType
            ? args[0]
            : CoerceToString(fn, input, args[0], "length");
        return new ResolvedFunction(
            "length", FunctionKind.Scalar, IntegerType.Instance, args[0].Nullable, new[] { coerced },
            nullPropagation: FunctionNullability.PropagatesAny);
    }

    private static ResolvedFunction BindConcat(
        UnresolvedFunction fn, IReadOnlyList<Expression> args, IReadOnlyList<DataType> argTypes)
    {
        if (args.Count < 1)
        {
            throw AnalysisException.InvalidFunctionArgument(
                fn.Name, argTypes, "concat requires at least one argument.");
        }

        // M1 string concat: every argument is coerced to string; the result is nullable if any is.
        var coerced = new Expression[args.Count];
        bool nullable = false;
        for (int i = 0; i < args.Count; i++)
        {
            coerced[i] = CoerceToString(fn, argTypes[i], args[i], "concat");
            nullable |= args[i].Nullable;
        }

        return new ResolvedFunction(
            "concat", FunctionKind.Scalar, StringType.Instance, nullable, coerced,
            nullPropagation: FunctionNullability.PropagatesAny);
    }

    private static ResolvedFunction BindCoalesce(
        UnresolvedFunction fn, IReadOnlyList<Expression> args, IReadOnlyList<DataType> argTypes)
    {
        if (args.Count < 1)
        {
            throw AnalysisException.InvalidFunctionArgument(
                fn.Name, argTypes, "coalesce requires at least one argument.");
        }

        DataType? common = TypeCoercion.FindWiderCommonType(argTypes);
        if (common is null)
        {
            throw AnalysisException.InvalidFunctionArgument(
                fn.Name,
                argTypes,
                "coalesce arguments have no common type; all arguments must be coercible to a single type.");
        }

        // coalesce is null only when every argument is null; each argument widens to the common type.
        var coerced = new Expression[args.Count];
        bool allNullable = true;
        for (int i = 0; i < args.Count; i++)
        {
            coerced[i] = CoercionHelpers.CastIfNeeded(args[i], common);
            allNullable &= args[i].Nullable;
        }

        return new ResolvedFunction(
            "coalesce", FunctionKind.Scalar, common, allNullable, coerced,
            nullPropagation: FunctionNullability.PropagatesAll);
    }

    // ---- scalar: temporal -------------------------------------------------------------------

    private static ResolvedFunction BindNullary(
        string name, DataType result, UnresolvedFunction fn, IReadOnlyList<DataType> argTypes)
    {
        if (argTypes.Count != 0)
        {
            throw AnalysisException.InvalidFunctionArgument(
                fn.Name, argTypes, $"{name} takes no arguments.");
        }

        return new ResolvedFunction(
            name, FunctionKind.Scalar, result, nullable: false, Array.Empty<Expression>(),
            nullPropagation: FunctionNullability.Fixed);
    }

    private static ResolvedFunction BindDatePart(
        string name, UnresolvedFunction fn, IReadOnlyList<Expression> args, IReadOnlyList<DataType> argTypes)
    {
        DataType input = RequireUnary(fn, args, argTypes, name);

        // year/month/dayofmonth accept date/timestamp directly; a string is implicitly parsed to a
        // date. Numeric/boolean inputs are rejected (Spark's string→date promotion is the only
        // implicit path modelled in M1).
        Expression arg = input switch
        {
            DateType or TimestampType => args[0],
            StringType => new Cast(args[0], DateType.Instance),
            _ => throw AnalysisException.InvalidFunctionArgument(
                fn.Name, argTypes, $"{name} requires a date, timestamp, or string argument."),
        };

        return new ResolvedFunction(
            name, FunctionKind.Scalar, IntegerType.Instance, args[0].Nullable, new[] { arg },
            nullPropagation: FunctionNullability.PropagatesAny);
    }

    private static ResolvedFunction BindToDate(
        UnresolvedFunction fn, IReadOnlyList<Expression> args, IReadOnlyList<DataType> argTypes)
    {
        DataType input = RequireUnary(fn, args, argTypes, "to_date");

        Expression arg = input switch
        {
            DateType => args[0],
            TimestampType or StringType => new Cast(args[0], DateType.Instance),
            _ => throw AnalysisException.InvalidFunctionArgument(
                fn.Name, argTypes, "to_date requires a string, date, or timestamp argument."),
        };

        // to_date can yield NULL on an unparseable string, so the result is always nullable. This is a
        // fixed rule (not derived from the argument's nullability), so it must NOT widen/narrow with a
        // NOT-NULL argument under Ansi — classify Fixed so NullableUnder stays the stored `true` (#627).
        return new ResolvedFunction(
            "to_date", FunctionKind.Scalar, DateType.Instance, nullable: true, new[] { arg },
            nullPropagation: FunctionNullability.Fixed);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static IReadOnlyList<DataType> ArgumentTypes(UnresolvedFunction fn)
    {
        var types = new DataType[fn.Arguments.Count];
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = fn.Arguments[i].Type
                ?? throw AnalysisException.InvalidFunctionArgument(
                    fn.Name,
                    Array.Empty<DataType>(),
                    $"argument #{i + 1} ('{CoercionHelpers.PrettyReference(fn.Arguments[i])}') has no result type.");
        }

        return types;
    }

    private static DataType RequireUnary(
        UnresolvedFunction fn, IReadOnlyList<Expression> args, IReadOnlyList<DataType> argTypes, string name)
    {
        if (args.Count != 1)
        {
            throw AnalysisException.InvalidFunctionArgument(
                fn.Name, argTypes, $"{name} requires exactly one argument but got {args.Count}.");
        }

        return argTypes[0];
    }

    /// <summary>Coerces <paramref name="arg"/> to string, inserting a <see cref="Cast"/> for a
    /// non-string atomic input and rejecting a non-atomic (complex) input.</summary>
    private static Expression CoerceToString(
        UnresolvedFunction fn, DataType input, Expression arg, string name)
    {
        if (input is StringType)
        {
            return arg;
        }

        if (input is StructType or ArrayType or MapType)
        {
            throw AnalysisException.InvalidFunctionArgument(
                fn.Name,
                new[] { input },
                $"{name} cannot implicitly coerce a complex type to string.");
        }

        return new Cast(arg, StringType.Instance);
    }

    private static bool IsOrderable(DataType type) =>
        type is BooleanType or ByteType or ShortType or IntegerType or LongType
            or FloatType or DoubleType or DecimalType or StringType or DateType or TimestampType;
}
