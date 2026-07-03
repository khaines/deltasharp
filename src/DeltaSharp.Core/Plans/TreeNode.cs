using System.Text;

namespace DeltaSharp.Plans;

/// <summary>
/// The immutable, Catalyst-style base for every node in the DeltaSharp logical-plan IR. Both
/// <see cref="DeltaSharp.Plans.Logical.LogicalPlan"/> and
/// <see cref="DeltaSharp.Plans.Expressions.Expression"/> derive from it.
/// </summary>
/// <typeparam name="TNode">
/// The concrete node family (the curiously-recurring template parameter), constrained to
/// <c>TreeNode&lt;TNode&gt;</c> so transforms return the concrete node type without casts.
/// </typeparam>
/// <remarks>
/// <para>
/// A tree node is immutable after construction: it exposes only its <see cref="Children"/> and
/// its own descriptor state, and the structural transforms (<see cref="MapChildren"/>,
/// <see cref="TransformDown"/>, <see cref="TransformUp"/>) never mutate an existing node — they
/// return new trees that <b>share</b> unchanged subtrees by reference (structural sharing).
/// </para>
/// <para>
/// Equality is structural and deterministic so plan comparison, caching, and tests are
/// reproducible. These IR contracts live in <c>DeltaSharp.Core</c> as <see langword="internal"/>
/// types and carry no reference to <c>DeltaSharp.Engine</c>
/// (see <c>docs/engineering/design/logical-plan-nodes.md</c>).
/// </para>
/// <para>
/// Each node caches its <see cref="Depth"/> (computed in O(1) from its children's already-cached
/// depths) and the constructor rejects trees deeper than <see cref="MaxDepth"/> with a
/// <see cref="PlanDepthExceededException"/>. This fail-fast guard bounds the otherwise unbounded
/// recursion in equality, hashing, the transforms, and tree rendering, closing a
/// <see cref="StackOverflowException"/> denial-of-service vector. M1 plans are shallow, so the
/// generous limit is invisible to real use.
/// </para>
/// </remarks>
internal abstract class TreeNode<TNode> : IEquatable<TNode>
    where TNode : TreeNode<TNode>
{
    /// <summary>
    /// The maximum supported tree depth. Chosen generously (M1 plans are a handful of nodes
    /// deep) yet well below the ~4000-frame point at which the recursive traversals would
    /// overflow the stack, leaving ample margin. Tracked follow-up: converting the hot
    /// traversals to explicit-stack form removes the need for this cap (design doc §9).
    /// </summary>
    public const int MaxDepth = 1000;

    private readonly IReadOnlyList<TNode> _children;
    private int? _hashCache;

    /// <summary>
    /// Initializes the node with its (already immutable) children, caches the tree depth, and
    /// rejects trees deeper than <see cref="MaxDepth"/>.
    /// </summary>
    /// <param name="children">
    /// The child nodes, already defensively copied into a read-only view (a leaf passes an empty
    /// list). The same instance is exposed by <see cref="Children"/>.
    /// </param>
    protected TreeNode(IReadOnlyList<TNode> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        _children = children;

        int maxChildDepth = 0;
        for (int i = 0; i < children.Count; i++)
        {
            int childDepth = children[i].Depth;
            if (childDepth > maxChildDepth)
            {
                maxChildDepth = childDepth;
            }
        }

        Depth = maxChildDepth + 1;
        if (Depth > MaxDepth)
        {
            throw new PlanDepthExceededException(Depth, MaxDepth);
        }
    }

    /// <summary>The child nodes, in order. A leaf returns an empty list.</summary>
    public IReadOnlyList<TNode> Children => _children;

    /// <summary>
    /// The nesting depth of this subtree: <c>1</c> for a leaf, otherwise
    /// <c>1 + max(child depths)</c>. Cached at construction (children's depths are themselves
    /// cached, so this is O(1) per node) and used by the construction-time depth guard.
    /// </summary>
    public int Depth { get; }

    /// <summary>The Catalyst node name (for example <c>"Project"</c>); a constant per node.</summary>
    public abstract string NodeName { get; }

    /// <summary>
    /// A one-line description of <b>this</b> node — its name and inline expression/descriptor
    /// arguments — <b>excluding</b> child plans (which render as their own tree lines). Carries a
    /// leading apostrophe when the node is not resolved.
    /// </summary>
    public abstract string SimpleString { get; }

    /// <summary>This node typed as the concrete node family.</summary>
    protected TNode Self => (TNode)this;

    /// <summary>
    /// Rebuilds this node with the supplied <paramref name="newChildren"/> (same count and
    /// positions), copying all non-child state. The transforms reconstruct nodes only through
    /// this method.
    /// </summary>
    public abstract TNode WithNewChildren(IReadOnlyList<TNode> newChildren);

    /// <summary>Compares this node's own (non-child) state to another node of the same concrete
    /// type.</summary>
    protected abstract bool NodeEquals(TNode other);

    /// <summary>A deterministic hash of this node's own (non-child) state.</summary>
    protected abstract int NodeHashCode();

    /// <summary>
    /// Applies <paramref name="transform"/> to each child. Returns <b>this same instance</b> when
    /// no child changed (reference-equal), otherwise a new node via <see cref="WithNewChildren"/>.
    /// This no-op short-circuit is the structural-sharing primitive.
    /// </summary>
    public TNode MapChildren(Func<TNode, TNode> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        IReadOnlyList<TNode> children = Children;
        if (children.Count == 0)
        {
            return Self;
        }

        TNode[]? rebuilt = null;
        for (int i = 0; i < children.Count; i++)
        {
            TNode original = children[i];
            TNode mapped = transform(original)
                ?? throw new InvalidOperationException("A child transform returned null.");
            if (!ReferenceEquals(mapped, original))
            {
                rebuilt ??= children.ToArray();
                rebuilt[i] = mapped;
            }
        }

        return rebuilt is null ? Self : WithNewChildren(rebuilt);
    }

    /// <summary>
    /// Pre-order rewrite: applies <paramref name="rule"/> to this node, then recurses into the
    /// (possibly new) node's children.
    /// </summary>
    public TNode TransformDown(Func<TNode, TNode> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        TNode afterSelf = rule(Self)
            ?? throw new InvalidOperationException("A transform rule returned null.");
        return afterSelf.MapChildren(child => child.TransformDown(rule));
    }

    /// <summary>
    /// Post-order rewrite: recurses into children first, then applies <paramref name="rule"/> to
    /// the rebuilt node.
    /// </summary>
    public TNode TransformUp(Func<TNode, TNode> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        TNode afterChildren = MapChildren(child => child.TransformUp(rule));
        return rule(afterChildren)
            ?? throw new InvalidOperationException("A transform rule returned null.");
    }

    /// <summary>Renders this subtree as an indented, multi-line tree string.</summary>
    public string TreeString() =>
        TreeStringRenderer.Render((TNode)this, node => node.SimpleString, node => node.Children);

    /// <summary>Structural value equality: same concrete type, equal own state, pairwise-equal
    /// children.</summary>
    public bool Equals(TNode? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (GetType() != other.GetType() || !NodeEquals(other))
        {
            return false;
        }

        IReadOnlyList<TNode> a = Children;
        IReadOnlyList<TNode> b = other.Children;
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

    /// <inheritdoc/>
    public sealed override bool Equals(object? obj) => obj is TNode node && Equals(node);

    /// <inheritdoc/>
    /// <remarks>
    /// Memoized: the hash is computed once and cached. Safe because nodes are immutable, so the
    /// value never changes; a benign race merely recomputes the same number.
    /// </remarks>
    public sealed override int GetHashCode()
    {
        if (_hashCache is int cached)
        {
            return cached;
        }

        int hash = PlanHash.Combine(NodeHashCode(), PlanHash.OfString(NodeName));
        foreach (TNode child in Children)
        {
            hash = PlanHash.Combine(hash, child.GetHashCode());
        }

        _hashCache = hash;
        return hash;
    }

    /// <inheritdoc/>
    public sealed override string ToString() => TreeString();
}
