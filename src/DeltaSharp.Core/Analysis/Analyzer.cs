using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Analysis;

/// <summary>
/// The first analyzer pass (Catalyst-style): it rewrites an <b>unresolved</b>
/// <see cref="LogicalPlan"/> into a <b>resolved</b> one by binding names against a
/// <see cref="ICatalog"/> and the plan's own output attributes. It applies two resolution rules
/// bottom-up over the immutable IR — never mutating a node, always returning new trees that share
/// unchanged subtrees — and does <b>no</b> physical planning, optimization, or execution.
/// </summary>
/// <remarks>
/// <para>
/// <b>ResolveRelations</b> replaces each <see cref="UnresolvedRelation"/> with a
/// <see cref="ResolvedRelation"/> carrying the catalog schema and its derived output attributes,
/// each assigned a stable <see cref="ExprId"/> (AC1). <b>ResolveReferences</b> then expands
/// <see cref="UnresolvedStar"/>s and binds each <see cref="UnresolvedAttribute"/> to the matching
/// input <see cref="AttributeReference"/> — reusing the scan's already-assigned id so a column and
/// every reference to it share one identity (AC2).
/// </para>
/// <para>
/// Any catalog miss or name-resolution failure raises a single Spark-compatible
/// <see cref="AnalysisException"/> from within the analyze pass. There is no backend to call and
/// none is called: resolution failure never triggers physical planning or execution (AC4).
/// </para>
/// <para>
/// Ids come from an <see cref="ExprIdGenerator"/> seeded fresh per <see cref="Resolve"/> call, so
/// resolution is deterministic run-to-run (no <c>Guid</c>/<c>System.Random</c>).
/// </para>
/// </remarks>
internal sealed class Analyzer
{
    private readonly ICatalog _catalog;

    /// <summary>Creates an analyzer bound to <paramref name="catalog"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is null.</exception>
    public Analyzer(ICatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <summary>
    /// Resolves <paramref name="plan"/>, returning an equivalent fully-resolved plan.
    /// </summary>
    /// <param name="plan">The unresolved (or partially resolved) input plan.</param>
    /// <returns>A new resolved plan tree; unchanged subtrees are shared by reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    /// <exception cref="AnalysisException">A relation or column reference did not resolve.</exception>
    public LogicalPlan Resolve(LogicalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var idGenerator = new ExprIdGenerator();
        LogicalPlan withRelations = ResolveRelations(plan, idGenerator);
        return ResolveReferences(withRelations, idGenerator);
    }

    /// <summary>ResolveRelations: bind every <see cref="UnresolvedRelation"/> via the catalog.</summary>
    private LogicalPlan ResolveRelations(LogicalPlan plan, ExprIdGenerator idGenerator) =>
        plan.TransformUp(node =>
            node is UnresolvedRelation relation
                ? BindRelation(relation, idGenerator)
                : node);

    private ResolvedRelation BindRelation(UnresolvedRelation relation, ExprIdGenerator idGenerator)
    {
        if (!_catalog.TryGetRelation(relation.Identifier, out StructType? schema))
        {
            throw AnalysisException.TableOrViewNotFound(relation.Identifier);
        }

        var output = new AttributeReference[schema.Count];
        for (int i = 0; i < schema.Count; i++)
        {
            StructField field = schema[i];
            output[i] = new AttributeReference(
                field.Name, field.DataType, field.Nullable, idGenerator.Next());
        }

        return new ResolvedRelation(relation.Identifier, schema, output, relation.Options);
    }

    /// <summary>
    /// ResolveReferences: expand stars and bind attributes bottom-up. Each node is resolved against
    /// the (already-resolved) output of its children, whose attribute lists are memoized as they are
    /// produced so a parent reads its children's output in O(1).
    /// </summary>
    private LogicalPlan ResolveReferences(LogicalPlan plan, ExprIdGenerator idGenerator)
    {
        var outputByPlan = new Dictionary<LogicalPlan, IReadOnlyList<AttributeReference>>(
            ReferenceEqualityComparer.Instance);

        return plan.TransformUp(node =>
        {
            IReadOnlyList<AttributeReference> input = CollectInput(node, outputByPlan);

            LogicalPlan resolved = node is Project project
                ? ExpandStars(project, node, outputByPlan)
                : node;

            resolved = resolved.MapExpressions(
                expression => expression.TransformUp(
                    inner => inner is UnresolvedAttribute attribute
                        ? ResolveAttribute(attribute, input)
                        : inner));

            outputByPlan[resolved] = DeriveOutput(resolved, outputByPlan, idGenerator);
            return resolved;
        });
    }

    /// <summary>The concatenated output attributes of <paramref name="node"/>'s resolved children
    /// (empty for a leaf), forming the name-resolution scope for this node's expressions.</summary>
    private static IReadOnlyList<AttributeReference> CollectInput(
        LogicalPlan node, IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan)
    {
        if (node.Children.Count == 0)
        {
            return Array.Empty<AttributeReference>();
        }

        if (node.Children.Count == 1)
        {
            return ChildOutput(node.Children[0], outputByPlan);
        }

        var combined = new List<AttributeReference>();
        foreach (LogicalPlan child in node.Children)
        {
            combined.AddRange(ChildOutput(child, outputByPlan));
        }

        return combined;
    }

    /// <summary>Replaces every <see cref="UnresolvedStar"/> in a projection with the child's output
    /// attributes, preserving the position of the star within the projection list.</summary>
    private static LogicalPlan ExpandStars(
        Project project,
        LogicalPlan resolvedNode,
        IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan)
    {
        bool hasStar = false;
        foreach (Expression element in project.ProjectList)
        {
            if (element is UnresolvedStar)
            {
                hasStar = true;
                break;
            }
        }

        if (!hasStar)
        {
            return resolvedNode;
        }

        IReadOnlyList<AttributeReference> childOutput =
            ChildOutput(resolvedNode.Children[0], outputByPlan);
        var expanded = new List<Expression>(project.ProjectList.Count);
        foreach (Expression element in project.ProjectList)
        {
            if (element is UnresolvedStar)
            {
                // M1: qualifiers are not tracked, so both `*` and `t.*` expand to the full child
                // output. Qualifier-scoped expansion is deferred behind the metastore/catalog seam.
                expanded.AddRange(childOutput);
            }
            else
            {
                expanded.Add(element);
            }
        }

        return new Project(expanded, ((Project)resolvedNode).Child);
    }

    private static AttributeReference ResolveAttribute(
        UnresolvedAttribute attribute, IReadOnlyList<AttributeReference> input)
    {
        // M1: qualifiers are not modelled on resolved attributes, so a multipart reference binds on
        // its trailing column part (`t.a` → `a`). Namespace-aware binding is a later story.
        string columnName = attribute.NameParts[^1];

        AttributeReference? found = null;
        List<AttributeReference>? ambiguous = null;
        foreach (AttributeReference candidate in input)
        {
            if (!string.Equals(candidate.Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found is null)
            {
                found = candidate;
            }
            else
            {
                ambiguous ??= new List<AttributeReference> { found };
                ambiguous.Add(candidate);
            }
        }

        if (ambiguous is not null)
        {
            throw AnalysisException.AmbiguousReference(attribute.Name, ambiguous);
        }

        return found ?? throw AnalysisException.UnresolvedColumn(attribute.Name, input);
    }

    /// <summary>Derives the output attribute list of a resolved node from its shape and its
    /// children's memoized outputs.</summary>
    private static IReadOnlyList<AttributeReference> DeriveOutput(
        LogicalPlan node,
        IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan,
        ExprIdGenerator idGenerator)
    {
        switch (node)
        {
            case ResolvedRelation relation:
                return relation.Output;

            case Project project:
                return ProjectionOutput(project.ProjectList, idGenerator);

            case Aggregate aggregate:
                return ProjectionOutput(aggregate.AggregateExpressions, idGenerator);

            case Join:
                return CollectInput(node, outputByPlan);

            default:
                // Unary shape-preserving operators (Filter, Sort, Limit, Distinct, WriteToSource)
                // and Union pass their (first) child's output through unchanged.
                return node.Children.Count == 0
                    ? Array.Empty<AttributeReference>()
                    : ChildOutput(node.Children[0], outputByPlan);
        }
    }

    private static IReadOnlyList<AttributeReference> ProjectionOutput(
        IReadOnlyList<Expression> projectList, ExprIdGenerator idGenerator)
    {
        var output = new AttributeReference[projectList.Count];
        for (int i = 0; i < projectList.Count; i++)
        {
            output[i] = ToAttribute(projectList[i], idGenerator);
        }

        return output;
    }

    /// <summary>Maps a resolved projection element to the attribute it exposes: an attribute is
    /// itself; an alias becomes a fresh attribute named for the alias.</summary>
    private static AttributeReference ToAttribute(Expression element, ExprIdGenerator idGenerator)
    {
        switch (element)
        {
            case AttributeReference attribute:
                return attribute;

            case Alias alias:
                DataType type = alias.Type
                    ?? throw new InvalidOperationException(
                        $"Alias '{alias.Name}' has no resolved type; type coercion is not part of "
                        + "the M1 analyzer.");
                return new AttributeReference(alias.Name, type, alias.Nullable, idGenerator.Next());

            default:
                throw new InvalidOperationException(
                    $"Projection element '{element.SimpleString}' is not a named output element "
                    + "(expected an attribute or an alias).");
        }
    }

    private static IReadOnlyList<AttributeReference> ChildOutput(
        LogicalPlan child,
        IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan) =>
        outputByPlan.TryGetValue(child, out IReadOnlyList<AttributeReference>? output)
            ? output
            : throw new InvalidOperationException(
                $"Output for child node '{child.NodeName}' was not resolved before its parent; "
                + "the bottom-up resolution order is violated.");
}
