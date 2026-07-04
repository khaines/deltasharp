using System.Linq.Expressions;
using System.Reflection;

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
/// (a literal or a captured variable such as <c>p =&gt; p.Age &gt; threshold</c>) is read to a
/// constant and becomes <see cref="Functions.Lit(object?)"/>; comparison/boolean/arithmetic operators
/// map onto the corresponding <see cref="Column"/> operators.
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
        // A subtree that does not reference the row parameter is a compile-time constant (a literal or
        // a captured variable): read it to a value and lower it to a lit(...) column.
        if (!ReferencesParameter(node, parameter))
        {
            return Functions.Lit(EvaluateConstant(node));
        }

        return node switch
        {
            // Unwrap boxing/lifting conversions (e.g. Func<T, object?> boxes value-typed members, and
            // Nullable<> comparisons introduce lifted converts); they carry no logical meaning.
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert =>
                LowerNode(convert.Operand, parameter),
            UnaryExpression { NodeType: ExpressionType.Not } not => LowerNode(not.Operand, parameter).Not(),
            MemberExpression member => LowerMember(member, parameter),
            BinaryExpression binary => LowerBinary(binary, parameter),
            _ => throw Unsupported(node),
        };
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

        return binary.NodeType switch
        {
            ExpressionType.Equal => left.EqualTo(right),
            ExpressionType.NotEqual => left.NotEqual(right),
            ExpressionType.LessThan => left.Lt(right),
            ExpressionType.LessThanOrEqual => left.Leq(right),
            ExpressionType.GreaterThan => left.Gt(right),
            ExpressionType.GreaterThanOrEqual => left.Geq(right),
            ExpressionType.AndAlso or ExpressionType.And => left.And(right),
            ExpressionType.OrElse or ExpressionType.Or => left.Or(right),
            ExpressionType.Add or ExpressionType.AddChecked => left.Plus(right),
            ExpressionType.Subtract or ExpressionType.SubtractChecked => left.Minus(right),
            ExpressionType.Multiply or ExpressionType.MultiplyChecked => left.Multiply(right),
            ExpressionType.Divide => left.Divide(right),
            ExpressionType.Modulo => left.Mod(right),
            _ => throw Unsupported(binary),
        };
    }

    // Reads a parameter-independent subtree to a value WITHOUT compiling or executing the lambda: a
    // constant is read directly, and a chain of field/property reads over a captured closure is walked
    // by reflection. This keeps the bridge AOT/trim-safe (no Expression.Compile) and lazy.
    private static object? EvaluateConstant(Expression node)
    {
        switch (node)
        {
            case ConstantExpression constant:
                return constant.Value;

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                return EvaluateConstant(convert.Operand);

            case MemberExpression { Expression: null } staticMember:
                return ReadMember(staticMember.Member, target: null);

            case MemberExpression member:
                return ReadMember(member.Member, EvaluateConstant(member.Expression));

            default:
                throw Unsupported(node);
        }
    }

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
