using System.Linq.Expressions;
using System.Reflection;
using DeltaSharp.Types;

namespace DeltaSharp;

/// <summary>
/// Lowers a typed <c>where</c>/<c>select</c> lambda (a <see cref="LambdaExpression"/> over an encoded
/// type <c>T</c>) into a DeltaSharp <see cref="Column"/>, so a strongly typed transformation builds
/// the <b>same</b> immutable logical expression IR — and therefore the same <c>Filter</c>/<c>Project</c>
/// plan nodes — as the equivalent untyped <see cref="DataFrame"/> call (STORY-04.2.4 / #163, AC1).
/// </summary>
/// <remarks>
/// <para>
/// The lambda body is treated as pure <b>data</b>: the tree is walked and translated, never compiled
/// or executed (ADR-0001, and AOT/trim safety). A property access on the lambda parameter
/// (<c>p =&gt; p.Age</c>) becomes <see cref="Functions.Col(string)"/>; a parameter-independent subtree
/// (a literal, a captured variable, or a captured arithmetic expression such as
/// <c>p =&gt; p.Age &gt; threshold + 1</c>) is folded to a constant and becomes
/// <see cref="Functions.Lit(object?)"/>; comparison/boolean/arithmetic operators map onto the
/// corresponding <see cref="Column"/> operators.
/// </para>
/// <para>
/// Fidelity is preserved where C# and SQL diverge: a <c>== null</c>/<c>!= null</c> comparison lowers to
/// <see cref="Column.IsNull"/>/<see cref="Column.IsNotNull"/> (not a 3VL comparison against a NULL
/// literal, which would match nothing); a value-changing numeric <c>Convert</c> (for example
/// <c>(double)intCol</c>) lowers to an explicit <c>Cast</c> rather than being silently dropped; and a
/// bitwise <c>&amp;</c>/<c>|</c> is rejected rather than mis-lowered to logical <c>And</c>/<c>Or</c>.
/// </para>
/// <para>
/// Any node the translator does not understand — a method call, a nested member access, an
/// unsupported operator — raises the deterministic
/// <see cref="UnsupportedTypedExpressionException"/> (AC4). See
/// <c>docs/engineering/design/dataset-typed-bridge.md</c>.
/// </para>
/// </remarks>
internal static class TypedExpressionLowering
{
    /// <summary>Lowers a lambda body to a <see cref="Column"/>, validating that the (single) lambda
    /// parameter is the one the body is written against.</summary>
    public static Column Lower(LambdaExpression lambda)
    {
        if (lambda.Parameters.Count != 1)
        {
            throw new UnsupportedTypedExpressionException(
                "A typed Dataset<T> transformation lambda must take exactly one parameter (the row of "
                + $"type T); this lambda takes {lambda.Parameters.Count}.");
        }

        return LowerNode(lambda.Body, lambda.Parameters[0]);
    }

    private static Column LowerNode(Expression node, ParameterExpression parameter)
    {
        // A subtree that does not reference the row parameter is a compile-time constant (a literal, a
        // captured variable, or an arithmetic expression over them): fold it to a value and lower it to
        // a lit(...) column.
        if (!ReferencesParameter(node, parameter))
        {
            return Functions.Lit(EvaluateConstant(node));
        }

        return node switch
        {
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert =>
                LowerConvert(convert, parameter),
            UnaryExpression { NodeType: ExpressionType.Not } not => LowerNode(not.Operand, parameter).Not(),
            MemberExpression member => LowerMember(member, parameter),
            BinaryExpression binary => LowerBinary(binary, parameter),
            _ => throw Unsupported(node),
        };
    }

    // A Convert node may be a *safe unwrap* that carries no logical/value meaning — boxing a value type
    // to object (Func<T, object?> selectors), a Nullable<U> lift/unlift, or an identity convert — in
    // which case it is dropped and the operand lowered. A *value-changing numeric conversion* (for
    // example (double)intCol) must NOT be dropped: doing so would silently reinterpret the operation in
    // the source domain (e.g. integer division), so it is preserved as an explicit Cast to the target
    // ADR-0008 type — or, if that target has no supported mapping, rejected deterministically.
    private static Column LowerConvert(UnaryExpression convert, ParameterExpression parameter)
    {
        Type target = convert.Type;
        Type source = convert.Operand.Type;
        Type targetUnderlying = Nullable.GetUnderlyingType(target) ?? target;
        Type sourceUnderlying = Nullable.GetUnderlyingType(source) ?? source;

        // Boxing to object, or a convert whose underlying types match (identity / Nullable<> lift or
        // unlift), changes no value: unwrap it.
        if (target == typeof(object) || targetUnderlying == sourceUnderlying)
        {
            return LowerNode(convert.Operand, parameter);
        }

        Column operand = LowerNode(convert.Operand, parameter);
        DataType targetType = DatasetSchema.MapClrType(targetUnderlying)
            ?? throw new UnsupportedTypedExpressionException(
                $"Unsupported typed conversion to '{target.Name}' in a Dataset<T> lambda: '{convert}'. "
                + "A value-changing numeric conversion lowers to a Cast, but the target CLR type has no "
                + "supported DeltaSharp DataType mapping.");

        return new Column(new Plans.Expressions.Cast(operand.Expr, targetType));
    }

    private static Column LowerMember(MemberExpression member, ParameterExpression parameter)
    {
        // Only a direct property access on the row parameter (p.Prop) lowers to a column reference.
        // Nested access (p.Address.City) is a complex-type concern deferred beyond M1.
        if (member.Expression == parameter && member.Member is PropertyInfo property)
        {
            return Functions.Col(property.Name);
        }

        throw new UnsupportedTypedExpressionException(
            $"Unsupported typed member access '{member}'. Only a direct property access on the "
            + "Dataset<T> row parameter (for example 'row => row.Age') lowers to a column reference.");
    }

    private static Column LowerBinary(BinaryExpression binary, ParameterExpression parameter)
    {
        Column left = LowerNode(binary.Left, parameter);
        Column right = LowerNode(binary.Right, parameter);

        switch (binary.NodeType)
        {
            case ExpressionType.Equal:
                // `x == null` is SQL `x IS NULL`; a 3VL `x = NULL` is UNKNOWN for every row.
                if (IsNullLiteral(right))
                {
                    return left.IsNull();
                }

                if (IsNullLiteral(left))
                {
                    return right.IsNull();
                }

                return left.EqualTo(right);

            case ExpressionType.NotEqual:
                // `x != null` is SQL `x IS NOT NULL`; a 3VL `x <> NULL` is UNKNOWN for every row.
                if (IsNullLiteral(right))
                {
                    return left.IsNotNull();
                }

                if (IsNullLiteral(left))
                {
                    return right.IsNotNull();
                }

                return left.NotEqual(right);

            case ExpressionType.LessThan:
                return left.Lt(right);
            case ExpressionType.LessThanOrEqual:
                return left.Leq(right);
            case ExpressionType.GreaterThan:
                return left.Gt(right);
            case ExpressionType.GreaterThanOrEqual:
                return left.Geq(right);

            // C# emits the SAME And/Or node kind for boolean `&`/`|` and integer *bitwise* `&`/`|`;
            // map to logical And/Or ONLY when the result is boolean, otherwise reject so a bitwise
            // `(p.Flags & 4)` is never silently mis-lowered to `And(col, 4)`. (`&&`/`||` always emit
            // AndAlso/OrElse, so no supported functionality is lost.)
            case ExpressionType.And:
                RequireBooleanLogical(binary);
                return left.And(right);
            case ExpressionType.Or:
                RequireBooleanLogical(binary);
                return left.Or(right);
            case ExpressionType.AndAlso:
                return left.And(right);
            case ExpressionType.OrElse:
                return left.Or(right);

            case ExpressionType.Add:
            case ExpressionType.AddChecked:
                return left.Plus(right);
            case ExpressionType.Subtract:
            case ExpressionType.SubtractChecked:
                return left.Minus(right);
            case ExpressionType.Multiply:
            case ExpressionType.MultiplyChecked:
                return left.Multiply(right);
            case ExpressionType.Divide:
                return left.Divide(right);
            case ExpressionType.Modulo:
                return left.Mod(right);

            default:
                throw Unsupported(binary);
        }
    }

    private static bool IsNullLiteral(Column column) => column.Expr is Plans.Expressions.Literal { IsNull: true };

    private static void RequireBooleanLogical(BinaryExpression binary)
    {
        if (binary.Type != typeof(bool) && binary.Type != typeof(bool?))
        {
            throw new UnsupportedTypedExpressionException(
                $"Unsupported bitwise operator '{binary.NodeType}' in a Dataset<T> lambda: '{binary}'. "
                + "Bitwise operators are not supported (M1); use the boolean short-circuit operators "
                + "'&&'/'||' for logical predicates.");
        }
    }

    // Reads a parameter-independent subtree to a value WITHOUT compiling or executing the lambda: a
    // constant is read directly, a chain of field/property reads over a captured closure is walked by
    // reflection, and a parameter-independent arithmetic/convert subtree (e.g. `threshold + 1`) is
    // folded by recursively evaluating its operands. This keeps the bridge AOT/trim-safe (no
    // Expression.Compile) and lazy.
    private static object? EvaluateConstant(Expression node)
    {
        switch (node)
        {
            case ConstantExpression constant:
                return constant.Value;

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                return ConvertConstant(EvaluateConstant(convert.Operand), convert.Type);

            case UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked } negate:
                return NegateConstant(EvaluateConstant(negate.Operand), negate);

            case MemberExpression { Expression: null } staticMember:
                return ReadMember(staticMember.Member, target: null);

            case MemberExpression member:
                return ReadMember(member.Member, EvaluateConstant(member.Expression));

            case BinaryExpression binary:
                return ApplyArithmetic(
                    binary.NodeType, EvaluateConstant(binary.Left), EvaluateConstant(binary.Right), binary);

            default:
                throw Unfoldable(node);
        }
    }

    private static object? ConvertConstant(object? value, Type targetType)
    {
        if (value is null)
        {
            return null;
        }

        Type targetUnderlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (targetUnderlying == typeof(object) || value.GetType() == targetUnderlying)
        {
            return value;
        }

        // A parameter-independent numeric convert (e.g. `(long)intLocal`) is folded to the target CLR
        // value so the resulting literal carries the intended type.
        if ((value is IConvertible && targetUnderlying.IsPrimitive) || targetUnderlying == typeof(decimal))
        {
            return System.Convert.ChangeType(
                value, targetUnderlying, System.Globalization.CultureInfo.InvariantCulture);
        }

        return value;
    }

    private static object NegateConstant(object? value, Expression node) => value switch
    {
        int i => -i,
        long l => -l,
        double d => -d,
        float f => -f,
        decimal m => -m,
        _ => throw Unfoldable(node),
    };

    private static object ApplyArithmetic(ExpressionType op, object? left, object? right, Expression node)
    {
        if (left is null || right is null)
        {
            throw Unfoldable(node);
        }

        return (left, right) switch
        {
            (int a, int b) => Arithmetic(op, a, b, node),
            (long a, long b) => Arithmetic(op, a, b, node),
            (double a, double b) => Arithmetic(op, a, b, node),
            (float a, float b) => Arithmetic(op, a, b, node),
            (decimal a, decimal b) => Arithmetic(op, a, b, node),
            _ => throw Unfoldable(node),
        };
    }

    private static int Arithmetic(ExpressionType op, int a, int b, Expression node) => op switch
    {
        ExpressionType.Add or ExpressionType.AddChecked => a + b,
        ExpressionType.Subtract or ExpressionType.SubtractChecked => a - b,
        ExpressionType.Multiply or ExpressionType.MultiplyChecked => a * b,
        ExpressionType.Divide => a / b,
        ExpressionType.Modulo => a % b,
        _ => throw Unfoldable(node),
    };

    private static long Arithmetic(ExpressionType op, long a, long b, Expression node) => op switch
    {
        ExpressionType.Add or ExpressionType.AddChecked => a + b,
        ExpressionType.Subtract or ExpressionType.SubtractChecked => a - b,
        ExpressionType.Multiply or ExpressionType.MultiplyChecked => a * b,
        ExpressionType.Divide => a / b,
        ExpressionType.Modulo => a % b,
        _ => throw Unfoldable(node),
    };

    private static double Arithmetic(ExpressionType op, double a, double b, Expression node) => op switch
    {
        ExpressionType.Add or ExpressionType.AddChecked => a + b,
        ExpressionType.Subtract or ExpressionType.SubtractChecked => a - b,
        ExpressionType.Multiply or ExpressionType.MultiplyChecked => a * b,
        ExpressionType.Divide => a / b,
        ExpressionType.Modulo => a % b,
        _ => throw Unfoldable(node),
    };

    private static float Arithmetic(ExpressionType op, float a, float b, Expression node) => op switch
    {
        ExpressionType.Add or ExpressionType.AddChecked => a + b,
        ExpressionType.Subtract or ExpressionType.SubtractChecked => a - b,
        ExpressionType.Multiply or ExpressionType.MultiplyChecked => a * b,
        ExpressionType.Divide => a / b,
        ExpressionType.Modulo => a % b,
        _ => throw Unfoldable(node),
    };

    private static decimal Arithmetic(ExpressionType op, decimal a, decimal b, Expression node) => op switch
    {
        ExpressionType.Add or ExpressionType.AddChecked => a + b,
        ExpressionType.Subtract or ExpressionType.SubtractChecked => a - b,
        ExpressionType.Multiply or ExpressionType.MultiplyChecked => a * b,
        ExpressionType.Divide => a / b,
        ExpressionType.Modulo => a % b,
        _ => throw Unfoldable(node),
    };

    private static object? ReadMember(MemberInfo member, object? target) => member switch
    {
        FieldInfo field => field.GetValue(target),
        PropertyInfo property => property.GetValue(target),
        _ => throw new UnsupportedTypedExpressionException(
            $"Unsupported constant member '{member.Name}' in a typed Dataset<T> lambda."),
    };

    private static bool ReferencesParameter(Expression node, ParameterExpression parameter) =>
        new ParameterFinder(parameter).Found(node);

    private static UnsupportedTypedExpressionException Unsupported(Expression node) =>
        new($"Unsupported typed expression node '{node.NodeType}' in a Dataset<T> lambda: '{node}'. "
            + "Supported nodes are property access on the row parameter, constants/captured values, "
            + "comparison (==, !=, <, <=, >, >=), boolean (&&, ||, !), and arithmetic (+, -, *, /, %).");

    private static UnsupportedTypedExpressionException Unfoldable(Expression node) =>
        new($"Cannot evaluate the parameter-independent subexpression '{node}' (a '{node.NodeType}' "
            + "node) in a Dataset<T> lambda without executing it. Assign it to a local variable and "
            + "reference that local instead, so the bridge can read it as a constant.");

    private sealed class ParameterFinder : ExpressionVisitor
    {
        private readonly ParameterExpression _parameter;
        private bool _found;

        public ParameterFinder(ParameterExpression parameter) => _parameter = parameter;

        public bool Found(Expression node)
        {
            _found = false;
            Visit(node);
            return _found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == _parameter)
            {
                _found = true;
            }

            return node;
        }
    }
}
