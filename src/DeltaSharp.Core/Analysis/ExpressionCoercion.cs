using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;

namespace DeltaSharp.Analysis;

/// <summary>
/// The analyzer's expression binding + type-coercion transform (STORY-04.5.2 / #171). Applied
/// bottom-up to a resolved expression tree, it (1) binds each <see cref="UnresolvedFunction"/> to a
/// typed <see cref="ResolvedFunction"/> via <see cref="FunctionRegistry"/>, and (2) coerces the
/// operands of arithmetic, comparison, boolean, and <see cref="CaseWhen"/> nodes to Spark-compatible
/// common types (ADR-0008), inserting <see cref="Cast"/> nodes for implicit widenings and rejecting
/// type-invalid operand combinations with a precise <see cref="AnalysisException"/>.
/// </summary>
/// <remarks>
/// Because it runs bottom-up, a node's operands are already bound, coerced, and typed when the node
/// itself is visited, so operand types are always known. The transform validates operand
/// <b>types</b>; operator-scoped rules that depend on the enclosing plan node (a
/// <see cref="DeltaSharp.Plans.Logical.Filter"/> condition being boolean, aggregate placement) are
/// enforced separately in <see cref="Analyzer"/>'s CheckAnalysis walk.
/// </remarks>
internal static class ExpressionCoercion
{
    /// <summary>The bottom-up rule: binds/coerces a single node whose children are already coerced.</summary>
    public static Expression Coerce(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return expression switch
        {
            UnresolvedFunction function => FunctionRegistry.Bind(function),
            BinaryArithmetic arithmetic => CoerceArithmetic(arithmetic),
            BinaryComparison comparison => CoerceComparison(comparison),
            And and => new And(RequireBoolean(and.Left, "and"), RequireBoolean(and.Right, "and"))
                .PreserveIfUnchanged(and),
            Or or => new Or(RequireBoolean(or.Left, "or"), RequireBoolean(or.Right, "or"))
                .PreserveIfUnchanged(or),
            Not not => new Not(RequireBoolean(not.Child, "not")).PreserveIfUnchanged(not),
            CaseWhen caseWhen => CoerceCaseWhen(caseWhen),
            _ => expression,
        };
    }

    private static Expression CoerceArithmetic(BinaryArithmetic arithmetic)
    {
        if (arithmetic.Left.Type is not { } leftType || arithmetic.Right.Type is not { } rightType)
        {
            return arithmetic;
        }

        ArithmeticCoercion? coercion =
            ArithmeticResultType.TryResolve(arithmetic.Operator, leftType, rightType);
        if (coercion is not { } c)
        {
            throw AnalysisException.DataTypeMismatch(
                arithmetic.SimpleString,
                $"the '{arithmetic.NodeName}' operator requires numeric operands but got "
                + $"'{leftType.SimpleString}' and '{rightType.SimpleString}'.");
        }

        Expression left = CoerceTo(arithmetic.Left, c.LeftTarget);
        Expression right = CoerceTo(arithmetic.Right, c.RightTarget);
        return ReferenceEquals(left, arithmetic.Left) && ReferenceEquals(right, arithmetic.Right)
            ? arithmetic
            : new BinaryArithmetic(left, right, arithmetic.Operator);
    }

    private static Expression CoerceComparison(BinaryComparison comparison)
    {
        if (comparison.Left.Type is not { } leftType || comparison.Right.Type is not { } rightType)
        {
            return comparison;
        }

        DataType? common = CommonComparableType(leftType, rightType);
        if (common is null)
        {
            throw AnalysisException.DataTypeMismatch(
                comparison.SimpleString,
                $"the '{comparison.NodeName}' operator requires comparable operand types but got "
                + $"'{leftType.SimpleString}' and '{rightType.SimpleString}'.");
        }

        Expression left = CoerceTo(comparison.Left, common);
        Expression right = CoerceTo(comparison.Right, common);
        return ReferenceEquals(left, comparison.Left) && ReferenceEquals(right, comparison.Right)
            ? comparison
            : new BinaryComparison(left, right, comparison.Operator);
    }

    private static Expression CoerceCaseWhen(CaseWhen caseWhen)
    {
        var valueTypes = new List<DataType>(caseWhen.BranchCount + 1);
        foreach ((Expression _, Expression value) in caseWhen.Branches)
        {
            if (value.Type is not { } valueType)
            {
                return caseWhen;
            }

            valueTypes.Add(valueType);
        }

        if (caseWhen.ElseValue is { } elseValue)
        {
            if (elseValue.Type is not { } elseType)
            {
                return caseWhen;
            }

            valueTypes.Add(elseType);
        }

        DataType? common = TypeCoercion.FindWiderCommonType(valueTypes);
        if (common is null)
        {
            throw AnalysisException.DataTypeMismatch(
                caseWhen.SimpleString,
                "the branch/else result values of a CASE expression must share a common type but got "
                + $"[{string.Join(", ", valueTypes.Select(t => t.SimpleString))}].");
        }

        // Rebuild the flattened children [c0, v0, c1, v1, …, (else?)] with boolean-checked conditions
        // and value operands widened to the common result type.
        var rebuilt = new Expression[caseWhen.Children.Count];
        int i = 0;
        foreach ((Expression condition, Expression value) in caseWhen.Branches)
        {
            rebuilt[i++] = RequireBoolean(condition, "CASE WHEN condition");
            rebuilt[i++] = CoerceTo(value, common);
        }

        if (caseWhen.ElseValue is { } trailingElse)
        {
            rebuilt[i] = CoerceTo(trailingElse, common);
        }

        return caseWhen.WithNewChildren(rebuilt);
    }

    /// <summary>The common comparable type of two comparison operands, or <see langword="null"/>
    /// when they are not comparable under M1's rules (equal types, null promotion, numeric widening,
    /// and date/timestamp). Spark's string↔numeric and other cross-family comparison casts are
    /// deferred.</summary>
    private static DataType? CommonComparableType(DataType left, DataType right)
    {
        if (left is NullType && right is NullType)
        {
            return NullType.Instance;
        }

        if (left is NullType)
        {
            return right;
        }

        if (right is NullType)
        {
            return left;
        }

        return left.Equals(right) ? left : TypeCoercion.FindWiderTypeForTwo(left, right);
    }

    /// <summary>Requires <paramref name="operand"/> to be boolean-typed, coercing a typed NULL to
    /// boolean and rejecting any other type with a data-type-mismatch diagnostic.</summary>
    private static Expression RequireBoolean(Expression operand, string context)
    {
        return operand.Type switch
        {
            BooleanType => operand,
            NullType => new Cast(operand, BooleanType.Instance),
            null => operand,
            _ => throw AnalysisException.DataTypeMismatch(
                operand.SimpleString,
                $"a '{context}' operand must be boolean but got '{operand.Type.SimpleString}'."),
        };
    }

    /// <summary>Wraps <paramref name="operand"/> in a <see cref="Cast"/> to <paramref name="target"/>
    /// unless it already has that type (structural sharing on a no-op coercion).</summary>
    private static Expression CoerceTo(Expression operand, DataType target) =>
        operand.Type is { } t && t.Equals(target) ? operand : new Cast(operand, target);

    private static Expression PreserveIfUnchanged(this Expression rebuilt, Expression original) =>
        rebuilt.Equals(original) ? original : rebuilt;
}
