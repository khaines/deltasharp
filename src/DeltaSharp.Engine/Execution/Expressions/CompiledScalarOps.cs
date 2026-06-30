using System.Reflection;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Scalar arithmetic/comparison/cast primitives the compiled lowering calls from its fused per-row
/// kernels (STORY-03.4.2). Each method is a <b>line-by-line mirror</b> of the corresponding private
/// method on the interpreted <see cref="ArithmeticEvaluator"/> / <see cref="CastEvaluator"/> —
/// including the ANSI/Legacy contract (<see cref="AnsiMode.Ansi"/> throws, <see cref="AnsiMode.Legacy"/>
/// nulls) and the <b>exact exception messages</b> — so the compiled tier is byte-identical to the
/// interpreter (the ADR-0001 parity oracle). A "may fail under Legacy" result is modelled as a
/// nullable return (<c>null</c> = produce SQL NULL); an ANSI violation throws the same exception type
/// and message the interpreter throws.
/// </summary>
/// <remarks>
/// These helpers emit no IL and use no reflection, so they are AOT-safe in isolation; they are only
/// ever reached from the elided compiled tier, so the trimmer removes them on NativeAOT. Floating
/// <c>+</c>/<c>-</c>/<c>*</c> and the comparison-to-boolean step are emitted as primitive
/// <see cref="System.Linq.Expressions.Expression"/> nodes by the lowering (IEEE-identical), so they
/// do not appear here.
/// </remarks>
internal static class CompiledScalarOps
{
    // ---- Arithmetic (mirrors ArithmeticEvaluator.ComputeIntegral/Single/Double/Decimal) ---------

    /// <summary>Mirrors <c>ArithmeticEvaluator.ComputeIntegral</c> (checked 64-bit op, then storage range-check).</summary>
    public static long? TryIntegral(ArithmeticOperator op, long a, long b, long min, long max, AnsiMode mode, string typeName)
    {
        long result;
        switch (op)
        {
            case ArithmeticOperator.Add:
                try
                {
                    result = checked(a + b);
                }
                catch (OverflowException)
                {
                    return OverflowOrNull(op, typeName, mode);
                }

                break;
            case ArithmeticOperator.Subtract:
                try
                {
                    result = checked(a - b);
                }
                catch (OverflowException)
                {
                    return OverflowOrNull(op, typeName, mode);
                }

                break;
            case ArithmeticOperator.Multiply:
                try
                {
                    result = checked(a * b);
                }
                catch (OverflowException)
                {
                    return OverflowOrNull(op, typeName, mode);
                }

                break;
            case ArithmeticOperator.Remainder:
                if (b == 0)
                {
                    return DivideByZeroOrNull(op, mode);
                }

                // Guard b == -1 so long.MinValue % -1 (which traps OverflowException) yields 0.
                result = b == -1 ? 0 : a % b;
                break;
            default:
                throw new InvalidOperationException($"Integral arithmetic does not handle '{op}'.");
        }

        return result < min || result > max ? OverflowOrNull(op, typeName, mode) : result;
    }

    /// <summary>Mirrors <c>ArithmeticEvaluator.ComputeSingle</c> for the only fallible single op, <c>%</c>.</summary>
    public static float? TrySingleRemainder(ArithmeticOperator op, float a, float b, AnsiMode mode)
    {
        if (b == 0f)
        {
            return mode == AnsiMode.Ansi ? throw DivideByZeroError(op) : null;
        }

        return a % b;
    }

    /// <summary>Mirrors <c>ArithmeticEvaluator.ComputeDouble</c> for the fallible double ops, <c>/</c> and <c>%</c>.</summary>
    public static double? TryDoubleDivideOrRemainder(ArithmeticOperator op, double a, double b, AnsiMode mode)
    {
        if (b == 0d)
        {
            return mode == AnsiMode.Ansi ? throw DivideByZeroError(op) : null;
        }

        return op == ArithmeticOperator.Divide ? a / b : a % b;
    }

    /// <summary>Mirrors <c>ArithmeticEvaluator.ComputeDecimal</c> (exact op via <see cref="DecimalValue"/>, then fit to the result type).</summary>
    public static DecimalValue? TryDecimal(
        ArithmeticOperator op, DecimalValue a, DecimalValue b, DecimalType resultType, AnsiMode mode)
    {
        ArgumentNullException.ThrowIfNull(resultType);
        DecimalValue exact;
        try
        {
            exact = op switch
            {
                ArithmeticOperator.Add => DecimalValue.Add(a, b),
                ArithmeticOperator.Subtract => DecimalValue.Subtract(a, b),
                ArithmeticOperator.Multiply => DecimalValue.Multiply(a, b),
                _ => throw new InvalidOperationException("Decimal divide/remainder is rejected when the evaluator is built."),
            };
        }
        catch (ArithmeticOverflowException) when (mode == AnsiMode.Legacy)
        {
            return null;
        }

        // Under Ansi an out-of-range fit throws (from ToType); under Legacy it nulls.
        return exact.ToType(resultType, mode);
    }

    // ---- Comparison (mirrors ComparisonEvaluator.Compare for the integral-family kinds) ----------

    /// <summary>Three-way sign of two 64-bit operands; mirrors <c>ReadInt64(left).CompareTo(ReadInt64(right))</c>.</summary>
    public static int CompareInt64(long a, long b) => a.CompareTo(b);

    // ---- Decimal carriers / boolean coercion ----------------------------------------------------

    /// <summary>Builds a <see cref="DecimalValue"/> from a compact (or widened integral) mantissa.</summary>
    public static DecimalValue MakeDecimal(long unscaled, int scale) => new(unscaled, scale);

    /// <summary>Builds a <see cref="DecimalValue"/> from a wide mantissa.</summary>
    public static DecimalValue MakeDecimalWide(Int128 unscaled, int scale) => new(unscaled, scale);

    /// <summary>Mirrors the decimal arm of <c>CastEvaluator.CastToBoolean</c> (<c>Unscaled != 0</c>).</summary>
    public static bool DecimalToBoolean(DecimalValue value) => value.Unscaled != Int128.Zero;

    // ---- Casts (mirrors CastEvaluator.TryCastToIntegral/TryCastToDecimal) ------------------------

    /// <summary>Mirrors the float/double arm of <c>CastEvaluator.TryCastToIntegral</c> (NaN/Inf reject, truncate, range).</summary>
    public static long? TryCastDoubleToIntegral(double value, long min, double upperExclusive, AnsiMode mode, string typeName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return CastFailOrNull(mode, typeName);
        }

        double truncated = Math.Truncate(value);
        if (truncated < min || truncated >= upperExclusive)
        {
            return CastFailOrNull(mode, typeName);
        }

        return (long)truncated;
    }

    /// <summary>Mirrors the decimal arm of <c>CastEvaluator.TryCastToIntegral</c> (truncate toward zero, range).</summary>
    public static long? TryCastDecimalToIntegral(DecimalValue value, long min, long max, AnsiMode mode, string typeName)
    {
        Int128 integerPart = value.Unscaled / DecimalValue.Pow10(value.Scale); // truncates toward zero
        if (integerPart < min || integerPart > max)
        {
            return CastFailOrNull(mode, typeName);
        }

        return (long)integerPart;
    }

    /// <summary>Mirrors the integral arm of <c>CastEvaluator.TryCastToIntegral</c> (range-check only).</summary>
    public static long? TryCastIntegralToIntegral(long value, long min, long max, AnsiMode mode, string typeName) =>
        value < min || value > max ? CastFailOrNull(mode, typeName) : value;

    /// <summary>Mirrors <c>CastEvaluator.TryCastToDecimal</c>'s fit step (HALF_UP to the target scale).</summary>
    public static DecimalValue? TryFitDecimal(DecimalValue source, DecimalType target, AnsiMode mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        return source.ToType(target, mode);
    }

    // ---- Nullable unwrap helpers (avoid Expression.Property(string), which is trim-unsafe) --------

    public static bool HasValue(long? value) => value.HasValue;

    public static long Unwrap(long? value) => value.GetValueOrDefault();

    public static bool HasValue(float? value) => value.HasValue;

    public static float Unwrap(float? value) => value.GetValueOrDefault();

    public static bool HasValue(double? value) => value.HasValue;

    public static double Unwrap(double? value) => value.GetValueOrDefault();

    public static bool HasValue(DecimalValue? value) => value.HasValue;

    public static DecimalValue Unwrap(DecimalValue? value) => value.GetValueOrDefault();

    public static bool HasValue(bool? value) => value.HasValue;

    public static bool Unwrap(bool? value) => value.GetValueOrDefault();

    public static bool HasValue(int? value) => value.HasValue;

    public static int Unwrap(int? value) => value.GetValueOrDefault();

    // ---- ANSI/Legacy outcome helpers (identical messages to the interpreter) ---------------------

    private static long? OverflowOrNull(ArithmeticOperator op, string typeName, AnsiMode mode) =>
        mode == AnsiMode.Ansi ? throw OverflowError(op, typeName) : null;

    private static long? DivideByZeroOrNull(ArithmeticOperator op, AnsiMode mode) =>
        mode == AnsiMode.Ansi ? throw DivideByZeroError(op) : null;

    private static long? CastFailOrNull(AnsiMode mode, string typeName) =>
        mode == AnsiMode.Ansi ? throw CastError(typeName) : null;

    private static ArithmeticOverflowException OverflowError(ArithmeticOperator op, string typeName) =>
        new($"Arithmetic '{op}' overflowed '{typeName}'.");

    private static DivideByZeroException DivideByZeroError(ArithmeticOperator op) =>
        new($"Division by zero in arithmetic '{op}'.");

    private static ArithmeticOverflowException CastError(string typeName) =>
        new($"Cast to '{typeName}' is out of range.");

    // ---- Cached method handles (method-group => MethodInfo; analyzer-safe) ------------------------

    public static readonly MethodInfo TryIntegralMethod =
        ((Func<ArithmeticOperator, long, long, long, long, AnsiMode, string, long?>)TryIntegral).Method;

    public static readonly MethodInfo TrySingleRemainderMethod =
        ((Func<ArithmeticOperator, float, float, AnsiMode, float?>)TrySingleRemainder).Method;

    public static readonly MethodInfo TryDoubleDivideOrRemainderMethod =
        ((Func<ArithmeticOperator, double, double, AnsiMode, double?>)TryDoubleDivideOrRemainder).Method;

    public static readonly MethodInfo TryDecimalMethod =
        ((Func<ArithmeticOperator, DecimalValue, DecimalValue, DecimalType, AnsiMode, DecimalValue?>)TryDecimal).Method;

    public static readonly MethodInfo CompareInt64Method = ((Func<long, long, int>)CompareInt64).Method;
    public static readonly MethodInfo MakeDecimalMethod = ((Func<long, int, DecimalValue>)MakeDecimal).Method;
    public static readonly MethodInfo MakeDecimalWideMethod = ((Func<Int128, int, DecimalValue>)MakeDecimalWide).Method;
    public static readonly MethodInfo DecimalToBooleanMethod = ((Func<DecimalValue, bool>)DecimalToBoolean).Method;

    public static readonly MethodInfo TryCastDoubleToIntegralMethod =
        ((Func<double, long, double, AnsiMode, string, long?>)TryCastDoubleToIntegral).Method;

    public static readonly MethodInfo TryCastDecimalToIntegralMethod =
        ((Func<DecimalValue, long, long, AnsiMode, string, long?>)TryCastDecimalToIntegral).Method;

    public static readonly MethodInfo TryCastIntegralToIntegralMethod =
        ((Func<long, long, long, AnsiMode, string, long?>)TryCastIntegralToIntegral).Method;

    public static readonly MethodInfo TryFitDecimalMethod =
        ((Func<DecimalValue, DecimalType, AnsiMode, DecimalValue?>)TryFitDecimal).Method;

    public static readonly MethodInfo HasValueInt64Method = ((Func<long?, bool>)HasValue).Method;
    public static readonly MethodInfo UnwrapInt64Method = ((Func<long?, long>)Unwrap).Method;
    public static readonly MethodInfo HasValueSingleMethod = ((Func<float?, bool>)HasValue).Method;
    public static readonly MethodInfo UnwrapSingleMethod = ((Func<float?, float>)Unwrap).Method;
    public static readonly MethodInfo HasValueDoubleMethod = ((Func<double?, bool>)HasValue).Method;
    public static readonly MethodInfo UnwrapDoubleMethod = ((Func<double?, double>)Unwrap).Method;
    public static readonly MethodInfo HasValueDecimalMethod = ((Func<DecimalValue?, bool>)HasValue).Method;
    public static readonly MethodInfo UnwrapDecimalMethod = ((Func<DecimalValue?, DecimalValue>)Unwrap).Method;
    public static readonly MethodInfo HasValueBoolMethod = ((Func<bool?, bool>)HasValue).Method;
    public static readonly MethodInfo UnwrapBoolMethod = ((Func<bool?, bool>)Unwrap).Method;
    public static readonly MethodInfo HasValueNullableInt32Method = ((Func<int?, bool>)HasValue).Method;
    public static readonly MethodInfo UnwrapNullableInt32Method = ((Func<int?, int>)Unwrap).Method;
}
