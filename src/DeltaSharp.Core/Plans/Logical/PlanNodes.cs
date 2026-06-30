using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>Shared helpers for the logical-plan nodes: child-arity validation and structural
/// expression comparison/hashing.</summary>
internal static class PlanNodes
{
    /// <summary>Validates and returns the single child of a unary node's
    /// <c>WithNewChildren</c> call.</summary>
    public static LogicalPlan SingleChild(IReadOnlyList<LogicalPlan> newChildren, string nodeName)
    {
        ArgumentNullException.ThrowIfNull(newChildren);
        if (newChildren.Count != 1)
        {
            throw new ArgumentException(
                $"{nodeName} is a unary node and expects exactly one child but got "
                + $"{newChildren.Count}.",
                nameof(newChildren));
        }

        return newChildren[0] ?? throw new ArgumentException(
            "Child cannot be null.", nameof(newChildren));
    }

    /// <summary>Validates and returns the two children of a binary node's
    /// <c>WithNewChildren</c> call.</summary>
    public static (LogicalPlan Left, LogicalPlan Right) TwoChildren(
        IReadOnlyList<LogicalPlan> newChildren, string nodeName)
    {
        ArgumentNullException.ThrowIfNull(newChildren);
        if (newChildren.Count != 2)
        {
            throw new ArgumentException(
                $"{nodeName} is a binary node and expects exactly two children but got "
                + $"{newChildren.Count}.",
                nameof(newChildren));
        }

        LogicalPlan left = newChildren[0] ?? throw new ArgumentException(
            "Left child cannot be null.", nameof(newChildren));
        LogicalPlan right = newChildren[1] ?? throw new ArgumentException(
            "Right child cannot be null.", nameof(newChildren));
        return (left, right);
    }

    /// <summary>
    /// Validates that a <c>WithNewExpressions</c> call supplies exactly
    /// <paramref name="expected"/> expressions (the node's arity), returning the list.
    /// </summary>
    public static IReadOnlyList<Expression> RequireExpressions(
        IReadOnlyList<Expression> newExpressions, int expected, string nodeName)
    {
        ArgumentNullException.ThrowIfNull(newExpressions);
        if (newExpressions.Count != expected)
        {
            throw new ArgumentException(
                $"{nodeName} expects exactly {expected} expression(s) but got "
                + $"{newExpressions.Count}.",
                nameof(newExpressions));
        }

        for (int i = 0; i < newExpressions.Count; i++)
        {
            if (newExpressions[i] is null)
            {
                throw new ArgumentException(
                    "Expression cannot be null.", nameof(newExpressions));
            }
        }

        return newExpressions;
    }

    /// <summary>Validates that a no-expression node's <c>WithNewExpressions</c> call is empty.</summary>
    public static void RequireNoExpressions(
        IReadOnlyList<Expression> newExpressions, string nodeName)
    {
        ArgumentNullException.ThrowIfNull(newExpressions);
        if (newExpressions.Count != 0)
        {
            throw new ArgumentException(
                $"{nodeName} holds no expressions but got {newExpressions.Count}.",
                nameof(newExpressions));
        }
    }

    /// <summary>Ordered structural equality of two expression lists.</summary>
    public static bool ExpressionsEqual(
        IReadOnlyList<Expression> a, IReadOnlyList<Expression> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Order-sensitively folds an expression list's structural hashes into
    /// <paramref name="seed"/>.</summary>
    public static int HashExpressions(int seed, IReadOnlyList<Expression> expressions)
    {
        int hash = seed;
        foreach (Expression expression in expressions)
        {
            hash = PlanHash.Combine(hash, expression.GetHashCode());
        }

        return hash;
    }
}
