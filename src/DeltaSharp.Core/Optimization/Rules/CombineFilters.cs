using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Optimization.Rules;

/// <summary>
/// Merges a <see cref="Filter"/> stacked directly on another <see cref="Filter"/> into a single
/// filter over the conjunction of their predicates (Catalyst <c>CombineFilters</c>):
/// <c>Filter(outer, Filter(inner, g)) → Filter(inner AND outer, g)</c>. A row survives both nested
/// filters iff both predicates are TRUE, which is exactly <c>inner AND outer</c> under SQL
/// three-valued logic, so the rewrite is semantics-preserving. The conjunction is emitted
/// <b>inner (child) predicate first</b> to match Spark: under short-circuit ANSI evaluation the
/// operand order is observable, and preserving <c>inner AND outer</c> keeps a guard the inner filter
/// provided (e.g. <c>age != 0</c>) ahead of a predicate that depends on it (e.g. <c>1 / age &gt; 0</c>),
/// never raising an error the original nested plan could not. Because the rule runs
/// <c>TransformUp</c> (post-order), a chain of N stacked filters collapses in a
/// <b>single</b> bottom-up sweep (see <c>docs/engineering/design/logical-optimizer.md</c> §3.2). The
/// merge fires only when both predicates are <see cref="Expression.Deterministic"/> — combining a
/// non-deterministic predicate could change how often it is evaluated (guard inert in M1, tracked
/// under #413).
/// </summary>
internal sealed class CombineFilters : Rule
{
    /// <inheritdoc/>
    public override string Name => "CombineFilters";

    /// <inheritdoc/>
    public override LogicalPlan Apply(LogicalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.TransformUp(node =>
            node is Filter { Child: Filter inner } outer
                && outer.Condition.Deterministic && inner.Condition.Deterministic
                ? new Filter(new And(inner.Condition, outer.Condition), inner.Child)
                : node);
    }
}
