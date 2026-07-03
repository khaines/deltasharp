using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Optimization.Rules;

/// <summary>
/// Pushes a <see cref="Filter"/> below a <see cref="Project"/> when the predicate references only
/// <b>pass-through</b> columns (Catalyst <c>PushPredicateThroughNonJoin</c>, restricted to Project):
/// <c>Filter(cond, Project(list, child)) → Project(list, Filter(cond, child))</c>.
/// </summary>
/// <remarks>
/// The push is valid exactly when every attribute the condition references is a pass-through
/// projection element — a bare <see cref="AttributeReference"/> in <c>list</c> (not an
/// <see cref="Alias"/>/computed column). The analyzer gives such a pass-through element the <b>same</b>
/// <see cref="ExprId"/> as the child attribute it forwards, whereas an alias/function element gets a
/// <b>fresh</b> id absent from <c>child</c>. So "references only pass-through columns" is precisely
/// "every referenced id exists in <c>child</c>", under which the pushed filter is well-formed and
/// evaluates identically. Predicates over a computed alias are left in place (precondition not met →
/// subtree preserved). See <c>docs/engineering/design/logical-optimizer.md</c> §3.3.
/// </remarks>
internal sealed class PushPredicateThroughProject : Rule
{
    /// <inheritdoc/>
    public override string Name => "PushPredicateThroughProject";

    /// <inheritdoc/>
    public override LogicalPlan Apply(LogicalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.TransformUp(TryPush);
    }

    private static LogicalPlan TryPush(LogicalPlan node)
    {
        if (node is not Filter { Child: Project project } filter)
        {
            return node;
        }

        var passThroughIds = new HashSet<ExprId>();
        foreach (Expression element in project.ProjectList)
        {
            if (element is AttributeReference reference)
            {
                passThroughIds.Add(reference.ExprId);
            }
        }

        var conditionIds = new HashSet<ExprId>();
        AttributeReferences.Collect(filter.Condition, conditionIds);

        // Push only when every referenced attribute is a pass-through column of the projection.
        if (!conditionIds.IsSubsetOf(passThroughIds))
        {
            return node;
        }

        return new Project(project.ProjectList, new Filter(filter.Condition, project.Child));
    }
}
