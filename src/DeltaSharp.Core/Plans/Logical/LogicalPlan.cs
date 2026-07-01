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
    private bool? _resolved;

    /// <summary>Initializes the plan with its child plans (a leaf passes an empty list).</summary>
    protected LogicalPlan(IReadOnlyList<LogicalPlan> children)
        : base(children)
    {
    }

    /// <summary>The expressions held directly by this node (excluding child plans). A node with
    /// no expressions returns an empty list.</summary>
    public abstract IReadOnlyList<Expression> Expressions { get; }

    /// <summary>
    /// Whether this plan is fully resolved: all children resolved and all directly-held
    /// expressions resolved. Unresolved leaves (for example
    /// <see cref="UnresolvedRelation"/>) override this to <see langword="false"/>.
    /// </summary>
    /// <remarks>Memoized: safe because nodes are immutable, so the value never changes.</remarks>
    public virtual bool Resolved
    {
        get
        {
            if (_resolved is bool cached)
            {
                return cached;
            }

            bool resolved = ComputeResolved();
            _resolved = resolved;
            return resolved;
        }
    }

    /// <summary>
    /// Whether this node's <b>own</b> (non-child, non-expression) state is resolved. Defaults to
    /// <see langword="true"/> so the generic "children + directly-held expressions" check in
    /// <see cref="Resolved"/> fully governs resolution. A node overrides this to gate resolution
    /// on state the generic check cannot see — most importantly a <see cref="Join"/> that is still
    /// an unresolved using/natural join, whose shared columns live outside the expression
    /// substrate (its <see cref="Expressions"/> are empty) and so must never be reported resolved
    /// until the analyzer desugars them into a <see cref="Join.Condition"/>.
    /// </summary>
    protected virtual bool IsNodeResolved => true;

    private bool ComputeResolved()
    {
        if (!IsNodeResolved)
        {
            return false;
        }

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

    /// <summary>
    /// Rebuilds this node with the supplied <paramref name="newExpressions"/> (same count and
    /// positions as <see cref="Expressions"/>), copying all child and non-expression state. This
    /// is the expression-rewrite counterpart of
    /// <see cref="TreeNode{TNode}.WithNewChildren"/> — the substrate the analyzer (#171) and
    /// optimizer (#172) rewrite plan-held expressions through, instead of hand-rebuilding each
    /// node type. A node with no expressions validates an empty list and returns itself.
    /// </summary>
    /// <remarks>
    /// For <see cref="Aggregate"/> the combined list is the grouping expressions followed by the
    /// aggregate expressions; this method honours that split when rebuilding.
    /// </remarks>
    public abstract LogicalPlan WithNewExpressions(IReadOnlyList<Expression> newExpressions);

    /// <summary>
    /// Applies <paramref name="transform"/> to each directly-held expression. Returns <b>this same
    /// instance</b> when no expression changed (reference-equal), otherwise a new node via
    /// <see cref="WithNewExpressions"/>. This is the expression-side structural-sharing primitive,
    /// symmetric with <see cref="TreeNode{TNode}.MapChildren"/>.
    /// </summary>
    public LogicalPlan MapExpressions(Func<Expression, Expression> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        IReadOnlyList<Expression> expressions = Expressions;
        if (expressions.Count == 0)
        {
            return this;
        }

        Expression[]? rebuilt = null;
        for (int i = 0; i < expressions.Count; i++)
        {
            Expression original = expressions[i];
            Expression mapped = transform(original)
                ?? throw new InvalidOperationException("An expression transform returned null.");
            if (!ReferenceEquals(mapped, original))
            {
                rebuilt ??= expressions.ToArray();
                rebuilt[i] = mapped;
            }
        }

        return rebuilt is null ? this : WithNewExpressions(rebuilt);
    }

    /// <summary>
    /// Rewrites every directly-held expression tree pre-order with <paramref name="rule"/>
    /// (via <see cref="TreeNode{TNode}.TransformDown"/> on each expression), sharing unchanged
    /// expressions and unchanged child plans by reference. Plan children are not visited — combine
    /// with <see cref="TreeNode{TNode}.TransformDown"/> to rewrite expressions across a whole plan
    /// tree (Catalyst's <c>transformAllExpressions</c>).
    /// </summary>
    public LogicalPlan TransformExpressionsDown(Func<Expression, Expression> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return MapExpressions(expression => expression.TransformDown(rule));
    }

    /// <summary>
    /// Rewrites every directly-held expression tree post-order with <paramref name="rule"/>
    /// (via <see cref="TreeNode{TNode}.TransformUp"/> on each expression). Plan children are not
    /// visited.
    /// </summary>
    public LogicalPlan TransformExpressionsUp(Func<Expression, Expression> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return MapExpressions(expression => expression.TransformUp(rule));
    }

    /// <summary>The unresolved-marker prefix (<c>'</c> when not resolved) used by
    /// <see cref="TreeNode{TNode}.SimpleString"/>.</summary>
    protected string UnresolvedPrefix => Resolved ? string.Empty : "'";

    /// <summary>Renders an expression list inline as <c>[a, b, c]</c>.</summary>
    protected static string RenderList(IReadOnlyList<Expression> expressions) =>
        "[" + string.Join(", ", expressions.Select(e => e.SimpleString)) + "]";
}
