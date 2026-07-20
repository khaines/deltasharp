using DeltaSharp.Types;

namespace DeltaSharp.Plans.Expressions;

/// <summary>
/// The immutable base for every node in the expression IR — the minimal seam that
/// STORY-04.4.1 establishes and STORY-04.4.2 (#168) extends with the full expression set
/// (aliases, literals, casts, operators, sort orders, aggregate expressions, unresolved stars).
/// </summary>
/// <remarks>
/// <para>
/// An expression's <see cref="TreeNode{TNode}.SimpleString"/> renders the <b>whole expression
/// inline</b> (its children folded in), matching Catalyst's <c>Expression.toString</c> — unlike
/// a <see cref="DeltaSharp.Plans.Logical.LogicalPlan"/>, whose <c>SimpleString</c> excludes
/// child plans. This inline render subsumes the need for a separate expression debug renderer:
/// each node composes its children's <c>SimpleString</c>, so aliases, literals, unresolved
/// attributes, and functions are distinguishable directly (AC4).
/// </para>
/// <para>
/// The type/nullability model is a set of eager <b>hints</b> (ADR-0008 / ADR-0016 shared
/// <see cref="DataType"/>). <see cref="Type"/> is the known result type where analysis-independent
/// (literals, casts, boolean-valued predicates) and <see langword="null"/> where it needs coercion
/// (arithmetic) or binding (the unresolved markers). <see cref="Nullable"/> is conservative
/// (<see langword="true"/>) until analysis proves otherwise. A node states these by overriding the
/// virtuals — not via a constructor field — matching Catalyst.
/// </para>
/// </remarks>
internal abstract class Expression : TreeNode<Expression>
{
    private bool? _resolved;

    /// <summary>Initializes the expression with its children (a leaf passes an empty list).</summary>
    protected Expression(IReadOnlyList<Expression> children)
        : base(children)
    {
    }

    /// <summary>
    /// The ADR-0008 result-type <b>hint</b> (the shared <see cref="DataType"/>), or
    /// <see langword="null"/> when it is unknown until analysis — the unresolved markers, and
    /// arithmetic whose result type needs numeric/decimal coercion. Known eagerly for literals,
    /// casts (their target), and the boolean-valued predicates. Overridden per node (AC3).
    /// </summary>
    public virtual DataType? Type => null;

    /// <summary>
    /// The nullability <b>hint</b> for this node — conservatively <see langword="true"/> where the
    /// real nullability is unknown before analysis (AC1). Overridden per node.
    /// </summary>
    public virtual bool Nullable => true;

    /// <summary>
    /// The mode-aware nullability hint: identical to <see cref="Nullable"/> under <see cref="AnsiMode.Ansi"/>,
    /// but under <see cref="AnsiMode.Legacy"/> an overflow-capable arithmetic or a lossy cast can manufacture SQL
    /// NULL from otherwise-non-null operands (DeltaSharp nulls on overflow/invalid-cast in Legacy rather than
    /// throwing), so those nodes widen to nullable. The default forwards <see cref="Nullable"/> (a leaf/literal is
    /// mode-independent); structural nodes that PROPAGATE child nullability override this to recurse mode-awarely.
    /// </summary>
    public virtual bool NullableUnder(AnsiMode mode) => Nullable;

    /// <summary>
    /// Whether evaluating this expression yields the same result for the same input row every time
    /// (Catalyst <c>Expression.deterministic</c>). An expression is deterministic exactly when it
    /// and all of its children are; a <b>non-deterministic</b> node (a future <c>rand</c>/<c>uuid</c>/
    /// <c>current_row_timestamp</c>, tracked under #413) <b>must override this to
    /// <see langword="false"/></b> so optimizer rules never duplicate, reorder, or push it in a way
    /// that changes how often it is evaluated. Every expression the M1 IR models is deterministic, so
    /// the guard is inert today; it exists so <c>CombineFilters</c> and
    /// <c>PushPredicateThroughProject</c> stay correct the moment such nodes land.
    /// </summary>
    public virtual bool Deterministic
    {
        get
        {
            foreach (Expression child in Children)
            {
                if (!child.Deterministic)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Whether this expression and all of its children are resolved. Defaults to "all children
    /// resolved"; the unresolved markers override it to <see langword="false"/>. The analyzer
    /// (FEAT-04.5) — never construction — is what makes an expression resolved.
    /// </summary>
    /// <remarks>Memoized: safe because nodes are immutable, so the value never changes. The lazy
    /// <c>bool?</c> memo is written without synchronization; this is benign because the recompute is
    /// idempotent over immutable nodes, so a race merely repeats identical work (matching #167's
    /// memoized hash). The assumption is single-threaded resolution by today's analyzer.</remarks>
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

    private bool ComputeResolved()
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

    /// <summary>Builds the single-child list for a unary node, rejecting a null child.</summary>
    private protected static IReadOnlyList<Expression> Unary(Expression child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return PlanCollections.AsReadOnly(child);
    }

    /// <summary>Builds the two-child list for a binary node, rejecting a null operand.</summary>
    private protected static IReadOnlyList<Expression> Binary(Expression left, Expression right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return PlanCollections.AsReadOnly(left, right);
    }

    /// <summary>
    /// Validates that a <see cref="TreeNode{TNode}.WithNewChildren"/> call supplies exactly
    /// <paramref name="expected"/> non-null children, returning the list.
    /// </summary>
    private protected static IReadOnlyList<Expression> RequireArity(
        IReadOnlyList<Expression> newChildren, int expected, string nodeName)
    {
        ArgumentNullException.ThrowIfNull(newChildren);
        if (newChildren.Count != expected)
        {
            throw new ArgumentException(
                $"{nodeName} expects exactly {expected} child expression(s) but got "
                + $"{newChildren.Count}.",
                nameof(newChildren));
        }

        for (int i = 0; i < newChildren.Count; i++)
        {
            if (newChildren[i] is null)
            {
                throw new ArgumentException(
                    "Child expressions must not be null.", nameof(newChildren));
            }
        }

        return newChildren;
    }
}
