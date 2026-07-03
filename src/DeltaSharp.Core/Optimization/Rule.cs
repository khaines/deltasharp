using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Optimization;

/// <summary>
/// A single, semantics-preserving optimization rule (Catalyst <c>Rule[LogicalPlan]</c>) — a total,
/// pure function <see cref="Apply"/> from an analyzed <see cref="LogicalPlan"/> to an equivalent,
/// cheaper one. Building on the immutable <c>TreeNode&lt;LogicalPlan&gt;</c> substrate, a rule never
/// mutates a node: it returns a <b>new</b> tree (sharing unchanged subtrees by reference) when it
/// rewrites, and the <b>same instance</b> when a precondition is not met so the subtree is preserved
/// (see <c>docs/engineering/design/logical-optimizer.md</c>).
/// </summary>
internal abstract class Rule
{
    /// <summary>The rule's stable name (used for deterministic ordering and diagnostics).</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Applies the rule to <paramref name="plan"/>, returning an equivalent plan. Implementations
    /// must be idempotent, deterministic, and return <paramref name="plan"/> itself when they change
    /// nothing.
    /// </summary>
    public abstract LogicalPlan Apply(LogicalPlan plan);
}
