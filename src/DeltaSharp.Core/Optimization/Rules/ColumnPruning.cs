using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Optimization.Rules;

/// <summary>
/// Drops columns a <see cref="ResolvedRelation"/> scan exposes that no operator above it uses
/// (Catalyst <c>ColumnPruning</c>, restricted to scan pruning for M1). It is a single top-down pass
/// that threads a <b>required-attribute set</b> from each parent into its child; only
/// <see cref="ResolvedRelation"/> leaves are rewritten, and the pruned relation reuses the very same
/// <see cref="AttributeReference"/> instances for the kept columns, so every reference above stays
/// valid and the plan's output schema is unchanged
/// (see <c>docs/engineering/design/logical-optimizer.md</c> §3.4).
/// </summary>
/// <remarks>
/// The required set is <see langword="null"/> until a <see cref="Project"/> "cuts" the output: a
/// <see langword="null"/> set means every column flowing through reaches the result and must be kept.
/// A bare <see cref="Filter"/>/relation at the root is therefore reached with <see langword="null"/>,
/// so columns that reach the result un-projected are never pruned.
/// </remarks>
internal sealed class ColumnPruning : Rule
{
    /// <inheritdoc/>
    public override string Name => "ColumnPruning";

    /// <inheritdoc/>
    public override LogicalPlan Apply(LogicalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Prune(plan, required: null);
    }

    /// <summary>
    /// Rewrites <paramref name="plan"/> given the attribute ids its parent requires from its output.
    /// A <see langword="null"/> <paramref name="required"/> means "output not yet cut by a projection
    /// — keep every column that flows through".
    /// </summary>
    private static LogicalPlan Prune(LogicalPlan plan, HashSet<ExprId>? required)
    {
        switch (plan)
        {
            case Project project:
                {
                    // A projection cuts the output: below it, only what the list references survives.
                    HashSet<ExprId> childRequired = AttributeReferences.Of(project.ProjectList);
                    return project.MapChildren(child => Prune(child, childRequired));
                }

            case Filter filter:
                {
                    // Output = child output (pass-through); the predicate also needs its columns.
                    HashSet<ExprId>? childRequired = With(required, filter.Condition);
                    return filter.MapChildren(child => Prune(child, childRequired));
                }

            case Sort sort:
                {
                    // Output = child output; the ordering additionally needs its key columns.
                    HashSet<ExprId>? childRequired = With(required, sort.Order);
                    return sort.MapChildren(child => Prune(child, childRequired));
                }

            case Limit limit:
                // Positional truncation: the required set passes through unchanged.
                return limit.MapChildren(child => Prune(child, required));

            case Distinct distinct:
                // The dedup key is ALL child columns; pruning any would change the row multiset.
                return distinct.MapChildren(child => Prune(child, required: null));

            case ResolvedRelation relation:
                return PruneRelation(relation, required);

            default:
                // Aggregate/Join/Union/WriteToSource and any future operator: conservatively keep all
                // of each child's output (M1 does not model these outputs, so never prune beneath them).
                return plan.MapChildren(child => Prune(child, required: null));
        }
    }

    private static LogicalPlan PruneRelation(ResolvedRelation relation, HashSet<ExprId>? required)
    {
        // A null required set means the relation's columns reach the result un-projected: keep all.
        if (required is null)
        {
            return relation;
        }

        IReadOnlyList<AttributeReference> output = relation.Output;

        // Defensive: output is derived one-per-schema-field in field order; if that ever diverges,
        // do not risk a mismatched schema — leave the relation untouched.
        if (output.Count != relation.Schema.Count)
        {
            return relation;
        }

        var keptOutput = new List<AttributeReference>(output.Count);
        var keptFields = new List<StructField>(output.Count);
        for (int i = 0; i < output.Count; i++)
        {
            if (required.Contains(output[i].ExprId))
            {
                keptOutput.Add(output[i]);
                keptFields.Add(relation.Schema[i]);
            }
        }

        // Nothing to prune (every column is used, or — degenerate — none is): preserve the instance.
        if (keptOutput.Count == output.Count || keptOutput.Count == 0)
        {
            return relation;
        }

        return new ResolvedRelation(
            relation.Identifier, new StructType(keptFields), keptOutput, relation.Options);
    }

    /// <summary>Adds every expression's referenced ids to a copy of <paramref name="required"/>, or
    /// returns <see langword="null"/> when the required set is <see langword="null"/> (output not yet
    /// cut by a projection — keep everything). The single nullable-aware "extend the required set"
    /// helper shared by the pass-through operators (<see cref="Filter"/>'s condition,
    /// <see cref="Sort"/>'s ordering keys).</summary>
    private static HashSet<ExprId>? With(HashSet<ExprId>? required, params Expression[] expressions) =>
        With(required, (IEnumerable<Expression>)expressions);

    /// <summary>Sequence form of the required-set extension helper (the params overload forwards
    /// here); shares the same null handling.</summary>
    private static HashSet<ExprId>? With(HashSet<ExprId>? required, IEnumerable<Expression> expressions)
    {
        if (required is null)
        {
            return null;
        }

        var union = new HashSet<ExprId>(required);
        foreach (Expression expression in expressions)
        {
            AttributeReferences.Collect(expression, union);
        }

        return union;
    }
}
