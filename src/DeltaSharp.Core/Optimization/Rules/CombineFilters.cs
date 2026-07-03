using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Optimization.Rules;

/// <summary>
/// Merges a <see cref="Filter"/> stacked directly on another <see cref="Filter"/> into a single
/// filter over the conjunction of their predicates (Catalyst <c>CombineFilters</c>):
/// <c>Filter(c1, Filter(c2, g)) → Filter(c1 AND c2, g)</c>. A row survives both nested filters iff
/// both predicates are TRUE, which is exactly <c>c1 AND c2</c> under SQL three-valued logic, so the
/// rewrite is semantics-preserving. Chains of three or more filters collapse over successive fixpoint
/// iterations (see <c>docs/engineering/design/logical-optimizer.md</c> §3.2).
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
                ? new Filter(new And(outer.Condition, inner.Condition), inner.Child)
                : node);
    }
}
