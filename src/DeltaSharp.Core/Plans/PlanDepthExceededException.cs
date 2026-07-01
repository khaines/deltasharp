namespace DeltaSharp.Plans;

/// <summary>
/// Thrown at <see cref="TreeNode{TNode}"/> construction when a plan/expression tree's nesting
/// depth exceeds <see cref="TreeNode{TNode}.MaxDepth"/>. The recursive traversals
/// (<c>Equals</c>/<c>GetHashCode</c>/<c>TransformDown</c>/<c>TransformUp</c>/<c>TreeString</c>)
/// are not depth-bounded, so an adversarially deep tree could otherwise trigger an uncatchable
/// <see cref="StackOverflowException"/>. Guarding the depth at construction makes the failure a
/// catchable, fail-fast, deterministic exception instead.
/// </summary>
internal sealed class PlanDepthExceededException : InvalidOperationException
{
    /// <summary>Creates the exception with a deterministic message.</summary>
    /// <param name="depth">The depth the offending node would have had.</param>
    /// <param name="maxDepth">The configured maximum allowed depth.</param>
    public PlanDepthExceededException(int depth, int maxDepth)
        : base($"Plan tree depth {depth} exceeds the maximum supported depth of {maxDepth}. "
            + "Deeply nested plans are rejected at construction to avoid unbounded recursion in "
            + "tree traversal (equality, hashing, transforms, and rendering).")
    {
        Depth = depth;
        MaxDepth = maxDepth;
    }

    /// <summary>The depth the offending node would have had.</summary>
    public int Depth { get; }

    /// <summary>The configured maximum allowed depth.</summary>
    public int MaxDepth { get; }
}
