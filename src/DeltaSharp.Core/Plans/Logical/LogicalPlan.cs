using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// The immutable base for every node in the unresolved logical plan — a Catalyst-style tree of
/// relational operators the public API builds before analysis. Constructing a plan does
/// <b>zero</b> work (no scan, no I/O, no Engine), preserving the lazy/eager invariant
/// (ADR-0001).
/// </summary>
internal abstract class LogicalPlan : TreeNode<LogicalPlan>
{
    /// <summary>The expressions held directly by this node (excluding child plans). A node with
    /// no expressions returns an empty list.</summary>
    public abstract IReadOnlyList<Expression> Expressions { get; }

    /// <summary>
    /// Whether this plan is fully resolved: all children resolved and all directly-held
    /// expressions resolved. Unresolved leaves (for example
    /// <see cref="UnresolvedRelation"/>) override this to <see langword="false"/>.
    /// </summary>
    public virtual bool Resolved
    {
        get
        {
            foreach (LogicalPlan child in Children)
            {
                if (!child.Resolved)
                {
                    return false;
                }
            }

            foreach (Expression expression in Expressions)
            {
                if (!expression.Resolved)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>The unresolved-marker prefix (<c>'</c> when not resolved) used by
    /// <see cref="TreeNode{TNode}.SimpleString"/>.</summary>
    protected string UnresolvedPrefix => Resolved ? string.Empty : "'";

    /// <summary>Renders an expression list inline as <c>[a, b, c]</c>.</summary>
    protected static string RenderList(IReadOnlyList<Expression> expressions) =>
        "[" + string.Join(", ", expressions.Select(e => e.SimpleString)) + "]";
}
