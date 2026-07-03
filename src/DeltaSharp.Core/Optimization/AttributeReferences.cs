using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Optimization;

/// <summary>
/// Collects the <see cref="ExprId"/>s of the resolved <see cref="AttributeReference"/>s an
/// expression (or expression list) references. Used by <see cref="Rules.ColumnPruning"/> to compute
/// each operator's required-attribute set and by <see cref="Rules.PushPredicateThroughProject"/> to
/// decide whether a predicate references only pass-through columns.
/// </summary>
internal static class AttributeReferences
{
    /// <summary>Adds every referenced attribute id in <paramref name="expression"/> to <paramref name="into"/>.</summary>
    public static void Collect(Expression expression, HashSet<ExprId> into)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(into);
        if (expression is AttributeReference reference)
        {
            into.Add(reference.ExprId);
        }

        foreach (Expression child in expression.Children)
        {
            Collect(child, into);
        }
    }

    /// <summary>Returns the set of attribute ids referenced anywhere in <paramref name="expressions"/>.</summary>
    public static HashSet<ExprId> Of(IReadOnlyList<Expression> expressions)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        var ids = new HashSet<ExprId>();
        for (int i = 0; i < expressions.Count; i++)
        {
            Collect(expressions[i], ids);
        }

        return ids;
    }
}
