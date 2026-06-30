namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// The immutable base for every node in the expression IR — the minimal seam that
/// STORY-04.4.1 establishes and STORY-04.4.2 (#168) extends with the full expression set
/// (aliases, literals, casts, operators, sort orders, aggregate expressions, unresolved stars).
/// </summary>
/// <remarks>
/// An expression's <see cref="TreeNode{TNode}.SimpleString"/> renders the <b>whole expression
/// inline</b> (its children folded in), matching Catalyst's <c>Expression.toString</c> — unlike
/// a <see cref="DeltaSharp.Plans.Logical.LogicalPlan"/>, whose <c>SimpleString</c> excludes
/// child plans.
/// </remarks>
internal abstract class Expression : TreeNode<Expression>
{
    /// <summary>
    /// Whether this expression and all of its children are resolved. Defaults to "all children
    /// resolved"; the unresolved markers override it to <see langword="false"/>. The analyzer
    /// (FEAT-04.5) — never construction — is what makes an expression resolved.
    /// </summary>
    public virtual bool Resolved
    {
        get
        {
            foreach (Expression child in Children)
            {
                if (!child.Resolved)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
