using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// A projection: evaluates <see cref="ProjectList"/> over its child. Target of
/// <c>select</c>/<c>withColumn</c>. Spark parity: <c>Project</c>.
/// </summary>
internal sealed class Project : LogicalPlan
{
    /// <summary>Creates a projection.</summary>
    public Project(IEnumerable<Expression> projectList, LogicalPlan child)
    {
        ProjectList = PlanCollections.ToImmutable(projectList, nameof(projectList));
        Child = child ?? throw new ArgumentNullException(nameof(child));
        _children = PlanCollections.AsReadOnly(Child);
    }

    private readonly IReadOnlyList<LogicalPlan> _children;

    /// <summary>The projected expressions, in order.</summary>
    public IReadOnlyList<Expression> ProjectList { get; }

    /// <summary>The input plan.</summary>
    public LogicalPlan Child { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<LogicalPlan> Children => _children;

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions => ProjectList;

    /// <inheritdoc/>
    public override string NodeName => "Project";

    /// <inheritdoc/>
    public override string SimpleString => $"{UnresolvedPrefix}Project {RenderList(ProjectList)}";

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren) =>
        new Project(ProjectList, PlanNodes.SingleChild(newChildren, NodeName));

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) =>
        other is Project project && PlanNodes.ExpressionsEqual(ProjectList, project.ProjectList);

    /// <inheritdoc/>
    protected override int NodeHashCode() =>
        PlanNodes.HashExpressions(PlanHash.Seed, ProjectList);
}
