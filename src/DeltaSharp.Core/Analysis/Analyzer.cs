using DeltaSharp.Diagnostics;
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

        // Emit the #169 audit seam's Analyzer milestone. Analysis is an eager, action-driven stage:
        // a lazy transformation (Select/Filter/WithColumn/…) must never reach here. A recording sink
        // installed for a transformation-only chain therefore observes an empty stage path, so an
        // accidental eager-analyze regression reddens (see DataFrameLazyTransformationTests).
        ExecutionAudit.StageEntered(ExecutionStage.Analyzer);

        var idGenerator = new ExprIdGenerator();
        LogicalPlan withRelations = ResolveRelations(plan, idGenerator);
        var outputByPlan = new Dictionary<LogicalPlan, IReadOnlyList<AttributeReference>>(
            ReferenceEqualityComparer.Instance);
        LogicalPlan resolved = ResolveReferences(withRelations, idGenerator, outputByPlan);
        CheckAnalysis(resolved, outputByPlan);
        return resolved;
    }

    /// <summary>ResolveRelations: bind every <see cref="UnresolvedRelation"/> via the catalog.</summary>
    private LogicalPlan ResolveRelations(LogicalPlan plan, ExprIdGenerator idGenerator) =>
        plan.TransformUp(node =>
            node is UnresolvedRelation relation
                ? BindRelation(relation, idGenerator)
                : node);

    private ResolvedRelation BindRelation(UnresolvedRelation relation, ExprIdGenerator idGenerator)
    {
        if (!_catalog.TryGetRelation(relation.Identifier, out CatalogTable? table))
        {
            throw AnalysisException.TableOrViewNotFound(relation.Identifier);
        }

        StructType schema = table.Schema;
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
    private LogicalPlan ResolveReferences(
        LogicalPlan plan,
        ExprIdGenerator idGenerator,
        Dictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan)
    {
        return plan.TransformUp(node =>
        {
            IReadOnlyList<AttributeReference> input = CollectInput(node, outputByPlan);

            LogicalPlan resolved = node is Project project
                ? ExpandStars(project, outputByPlan)
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

    /// <summary>
    /// CheckAnalysis: the analyzer's post-condition. After the rule pass, the result must be
    /// <b>fully resolved</b>. This walk verifies that and throws a loud
    /// <see cref="AnalysisException"/> naming the offending node/expression if any unresolved marker
    /// survived — an <see cref="UnresolvedAttribute"/>, an <see cref="UnresolvedStar"/> (for example
    /// a star outside a <see cref="Project"/> that was never expanded), an
    /// <see cref="UnresolvedFunction"/> (function resolution is a later story), or an operator that
    /// is otherwise still unresolved (for example a using/natural <see cref="Join"/> the analyzer
    /// has not desugared). Without this check such residuals would leak silently as a
    /// not-fully-resolved plan. It additionally enforces structural set-operation compatibility: a
    /// <see cref="Union"/> whose inputs differ in <b>column count</b> (arity) raises a Spark-parity
    /// diagnostic (deep column-type compatibility/coercion is deferred to STORY-04.5.2 / #171).
    /// </summary>
    private static void CheckAnalysis(
        LogicalPlan plan,
        IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan)
    {
        foreach (LogicalPlan child in plan.Children)
        {
            CheckAnalysis(child, outputByPlan);
        }

        foreach (Expression expression in plan.Expressions)
        {
            CheckExpression(expression, plan.NodeName);
        }

        // Children and directly-held expressions are clean; any remaining unresolution is the
        // node's own state (e.g. an undesugared using/natural join).
        if (!plan.Resolved)
        {
            throw AnalysisException.UnresolvedOperator(plan.NodeName);
        }

        // Structural set-operation compatibility: every Union input must expose the same number of
        // columns (arity). Deep column-type compatibility/coercion is deferred to STORY-04.5.2
        // (#171); this is the arity half only (AC3).
        if (plan is Union union)
        {
            CheckUnionArity(union, outputByPlan);
        }
    }

    /// <summary>Verifies every input of a resolved <see cref="Union"/> exposes the same column
    /// count, raising a Spark-parity diagnostic naming the mismatched arities otherwise.</summary>
    private static void CheckUnionArity(
        Union union,
        IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan)
    {
        int firstColumnCount = ChildOutput(union.Inputs[0], outputByPlan).Count;
        for (int i = 1; i < union.Inputs.Count; i++)
        {
            int columnCount = ChildOutput(union.Inputs[i], outputByPlan).Count;
            if (columnCount != firstColumnCount)
            {
                throw AnalysisException.NumberOfColumnsMismatch(
                    union.NodeName, firstColumnCount, i, columnCount);
            }
        }
    }

    /// <summary>Walks an expression subtree, throwing if any unresolved marker survives.</summary>
    private static void CheckExpression(Expression expression, string ownerNodeName)
    {
        switch (expression)
        {
            case UnresolvedAttribute attribute:
                throw AnalysisException.UnresolvedExpression(attribute.Name, ownerNodeName);

            case UnresolvedStar star:
                throw AnalysisException.UnresolvedExpression(star.SimpleString, ownerNodeName);

            case UnresolvedFunction function:
                throw AnalysisException.UnresolvedExpression(function.Name, ownerNodeName);
        }

        foreach (Expression child in expression.Children)
        {
            CheckExpression(child, ownerNodeName);
        }
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
            return project;
        }

        IReadOnlyList<AttributeReference> childOutput =
            ChildOutput(project.Child, outputByPlan);
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

        return new Project(expanded, project.Child);
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

            case Join join:
                return JoinOutput(join, node, outputByPlan);

            // Shape-preserving unary operators expose their single child's output unchanged.
            case Filter:
            case Sort:
            case Limit:
            case Distinct:
            case WriteToSource:
                return ChildOutput(node.Children[0], outputByPlan);

            case Union:
                // TODO(#392): set-op output semantics (fresh ids + nullability widening). Union
                // currently reuses its first input's attributes; Spark mints fresh output
                // attributes and widens nullability across inputs.
                return ChildOutput(node.Children[0], outputByPlan);

            default:
                // A node whose output shape is not explicitly modelled (e.g. a future set-op such as
                // Intersect/Except or a Generate) must not silently pass a child's output through —
                // that would derive a wrong output and corrupt downstream name resolution.
                throw new NotSupportedException(
                    $"Output derivation is not defined for operator '{node.NodeName}'. Add an "
                    + "explicit DeriveOutput case before introducing this operator to the analyzer.");
        }
    }

    /// <summary>The output of a resolved <see cref="Join"/>: left ⧺ right for value-adding joins,
    /// but <b>left-only</b> for <see cref="JoinType.LeftSemi"/>/<see cref="JoinType.LeftAnti"/>,
    /// which filter the left side and never widen it with right-side columns (Spark parity).</summary>
    private static IReadOnlyList<AttributeReference> JoinOutput(
        Join join,
        LogicalPlan node,
        IReadOnlyDictionary<LogicalPlan, IReadOnlyList<AttributeReference>> outputByPlan) =>
        join.JoinType is JoinType.LeftSemi or JoinType.LeftAnti
            ? ChildOutput(node.Children[0], outputByPlan)
            : CollectInput(node, outputByPlan);

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
                    ?? throw AnalysisException.UnsupportedProjection(
                        $"Output type of '{alias.Child.SimpleString}' (aliased as '{alias.Name}') "
                        + "cannot be determined until type coercion (STORY-04.5.2 / #171).",
                        alias.Name);
                return new AttributeReference(alias.Name, type, alias.Nullable, idGenerator.Next());

            case UnresolvedFunction function:
                // A bare aggregate/scalar function reached output derivation while still unresolved.
                // Aggregate-function resolution and Spark auto-naming are deferred to STORY-04.5.2
                // (#171), so we cannot mint an output name yet. Reject deterministically here (this
                // fires during ResolveReferences, before CheckAnalysis) with a targeted message
                // rather than the generic "not a named output element" fallback, and point at the
                // alias workaround. Kind stays UnsupportedProjection.
                throw AnalysisException.UnsupportedProjection(
                    $"Aggregate/function output '{function.Name}' cannot be named yet: "
                    + "aggregate-function resolution and Spark auto-naming are deferred to "
                    + "STORY-04.5.2 (#171). Alias the expression (for example .As(\"total\")) as the "
                    + "M1 workaround.",
                    function.Name);

            default:
                throw AnalysisException.UnsupportedProjection(
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
