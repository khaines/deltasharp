using DeltaSharp.Analysis;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// Reconstructs the per-node output <see cref="AttributeReference"/> lists of an analyzed logical
/// plan so the physical planner can resolve every <see cref="AttributeReference"/> to an input
/// ordinal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this duplicates analyzer internals.</b> Core's <see cref="LogicalPlan"/> does not store
/// output attributes (unlike Catalyst): only <see cref="ResolvedRelation"/> exposes its
/// <see cref="ResolvedRelation.Output"/>. The analyzer (FEAT-04.5) computes output attribute lists —
/// minting fresh <see cref="ExprId"/>s for <see cref="Project"/>/<see cref="Aggregate"/> alias and
/// function outputs — into a transient map that is discarded after analysis. Parent nodes reference
/// those fresh ids, but the ids live nowhere in the tree, so the bridge cannot recover them from the
/// analyzed plan alone.
/// </para>
/// <para>
/// This type re-derives them by <b>mirroring the analyzer's exact numbering</b>: phase 1 assigns ids
/// <c>0..(k-1)</c> to the <c>k</c> relation output attributes across the tree, so alias/function
/// outputs are minted starting at <c>k</c> in the same bottom-up, left-to-right order the analyzer
/// uses. Because the analyzed tree structure is identical and the traversal is deterministic, the
/// reconstructed ids match the ids the tree's own <see cref="AttributeReference"/>s already carry. If
/// they ever <i>don't</i> (e.g. after an optimizer pass mints or reorders outputs), resolution fails
/// loudly via <see cref="UnsupportedPlanException"/> rather than producing a silently wrong plan.
/// </para>
/// <para>
/// <b>Rebase note (#421).</b> This mirroring duplicates analyzer internals. #172 (optimizer) and #173
/// (Row/IQueryExecutor) are now merged, so the branch binds the real Core seam — but the durable
/// analyzer/optimizer <i>output</i> seam is still pending (<b>#421</b>): once it lands, replace this
/// reconstruction with the exposed seam and delete the mirroring. See
/// <c>docs/engineering/design/physical-planning.md</c>.
/// </para>
/// </remarks>
internal sealed class LogicalOutput
{
    private readonly Dictionary<LogicalPlan, IReadOnlyList<AttributeReference>> _outputs = new(ReferenceEqualityComparer.Instance);
    private long _nextId;

    private LogicalOutput()
    {
    }

    /// <summary>Derives and memoizes the output attributes of every node in <paramref name="analyzedPlan"/>.</summary>
    /// <param name="analyzedPlan">The analyzed (and optionally optimized) logical plan root.</param>
    /// <returns>A populated lookup keyed by node reference identity.</returns>
    /// <exception cref="UnsupportedPlanException">A node has no M1 output-derivation rule.</exception>
    public static LogicalOutput Derive(LogicalPlan analyzedPlan)
    {
        ArgumentNullException.ThrowIfNull(analyzedPlan);
        var output = new LogicalOutput
        {
            // Phase 1 consumed exactly one id per relation output attribute; alias/function outputs
            // continue from there. See the type remarks for why this reproduces the analyzer's order.
            _nextId = CountRelationAttributes(analyzedPlan),
        };
        output.Visit(analyzedPlan);
        return output;
    }

    /// <summary>The output attributes of <paramref name="node"/> (name, type, nullability, ExprId).</summary>
    /// <param name="node">A node that was part of the derived plan.</param>
    /// <returns>Its ordered output attributes.</returns>
    public IReadOnlyList<AttributeReference> OutputOf(LogicalPlan node) =>
        _outputs.TryGetValue(node, out IReadOnlyList<AttributeReference>? output)
            ? output
            : throw new UnsupportedPlanException(
                $"Output was not derived for node '{node.NodeName}'; the plan was not traversed bottom-up.");

    private IReadOnlyList<AttributeReference> Visit(LogicalPlan node)
    {
        foreach (LogicalPlan child in node.Children)
        {
            Visit(child);
        }

        IReadOnlyList<AttributeReference> output = Derive(node, _outputs);
        _outputs[node] = output;
        return output;
    }

    private IReadOnlyList<AttributeReference> Derive(
        LogicalPlan node,
        IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputs)
    {
        switch (node)
        {
            case ResolvedRelation relation:
                return relation.Output;

            case LocalRelation { Output: { } localOutput }:
                return localOutput;

            case Project project:
                return ProjectionOutput(project.ProjectList);

            case Aggregate aggregate:
                return ProjectionOutput(aggregate.AggregateExpressions);

            case Join join:
                return join.JoinType is JoinType.LeftSemi or JoinType.LeftAnti
                    ? outputs[join.Left]
                    : Concat(outputs[join.Left], outputs[join.Right]);

            // Shape-preserving operators expose their (first) child's output unchanged. Union reuses
            // its first input's attributes to match the analyzer (set-op widening is TODO(#392)).
            // WriteToSource (STORY-04.6.3) is a shape-preserving root over its child's rows: the write
            // node's output attributes are its child's, so the physical WriteToSink drains that schema.
            case Filter or Sort or Limit or Distinct or Union or WriteToSource:
                return outputs[node.Children[0]];

            default:
                throw UnsupportedPlanException.ForNode(
                    node.NodeName, "physical planning has no output-derivation rule for this operator");
        }
    }

    private IReadOnlyList<AttributeReference> ProjectionOutput(IReadOnlyList<Expression> projectList)
    {
        var output = new AttributeReference[projectList.Count];
        for (int i = 0; i < projectList.Count; i++)
        {
            output[i] = ToAttribute(projectList[i]);
        }

        return output;
    }

    private AttributeReference ToAttribute(Expression element)
    {
        switch (element)
        {
            case AttributeReference attribute:
                return attribute;

            case Alias alias:
                DataType aliasType = alias.Type
                    ?? throw UnsupportedPlanException.ForExpression(alias.NodeName, "alias output type is unresolved");
                return new AttributeReference(alias.Name, aliasType, alias.Nullable, new ExprId(_nextId++));

            case ResolvedFunction function:
                return new AttributeReference(
                    CoercionHelpers.PrettyReference(function), function.Type, function.Nullable, new ExprId(_nextId++));

            case GetStructField field:
                // A bare nested reference is auto-named after the extracted field (Spark parity):
                // `select(col("s.f"))` exposes column `f` (#580). Mirrors the analyzer's ToAttribute.
                DataType fieldType = field.Type
                    ?? throw UnsupportedPlanException.ForExpression(field.NodeName, "nested field output type is unresolved");
                return new AttributeReference(field.FieldName, fieldType, field.Nullable, new ExprId(_nextId++));

            default:
                throw UnsupportedPlanException.ForExpression(
                    element.NodeName, "projection element is not a named output (expected an attribute, alias, or function)");
        }
    }

    private static IReadOnlyList<AttributeReference> Concat(
        IReadOnlyList<AttributeReference> left, IReadOnlyList<AttributeReference> right)
    {
        var combined = new AttributeReference[left.Count + right.Count];
        for (int i = 0; i < left.Count; i++)
        {
            combined[i] = left[i];
        }

        for (int i = 0; i < right.Count; i++)
        {
            combined[left.Count + i] = right[i];
        }

        return combined;
    }

    private static long CountRelationAttributes(LogicalPlan plan)
    {
        long count = plan switch
        {
            ResolvedRelation relation => relation.Output.Count,
            LocalRelation { Output: { } output } => output.Count,
            _ => 0,
        };
        foreach (LogicalPlan child in plan.Children)
        {
            count += CountRelationAttributes(child);
        }

        return count;
    }
}
