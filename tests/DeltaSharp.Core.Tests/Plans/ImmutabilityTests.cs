using System.Collections;
using System.Reflection;
using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// AC1: each M1 node and its children are immutable after construction — nodes are sealed,
/// collection inputs are defensively copied, and collection properties are read-only views.
/// </summary>
public sealed class ImmutabilityTests
{
    private static readonly Type[] LogicalNodeTypes =
    {
        typeof(UnresolvedRelation), typeof(Project), typeof(Filter), typeof(Aggregate),
        typeof(Join), typeof(Sort), typeof(Limit), typeof(Distinct), typeof(Union),
        typeof(WriteToSource),
    };

    [Fact]
    public void EveryLogicalNodeTypeIsSealed()
    {
        foreach (Type type in LogicalNodeTypes)
        {
            Assert.True(type.IsSealed, $"{type.Name} must be sealed.");
        }
    }

    [Fact]
    public void ExpressionMarkerTypesAreSealed()
    {
        Assert.True(typeof(UnresolvedAttribute).IsSealed);
        Assert.True(typeof(UnresolvedFunction).IsSealed);
    }

    [Fact]
    public void NoLogicalNodePropertyHasAPublicSetter()
    {
        foreach (Type type in LogicalNodeTypes)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.False(
                    property.SetMethod is { IsPublic: true },
                    $"{type.Name}.{property.Name} must not expose a public setter.");
            }
        }
    }

    [Fact]
    public void ProjectListInputIsDefensivelyCopied()
    {
        var list = new List<Expression> { PlanFixtures.Attr("a"), PlanFixtures.Attr("b") };
        var project = new Project(list, PlanFixtures.Relation("t"));

        list.Add(PlanFixtures.Attr("c"));

        Assert.Equal(2, project.ProjectList.Count);
    }

    [Fact]
    public void RelationOptionsInputIsDefensivelyCopied()
    {
        var options = new Dictionary<string, string> { ["k"] = "v" };
        var relation = new UnresolvedRelation(new[] { "t" }, options);

        options["k2"] = "v2";

        Assert.Single(relation.Options);
    }

    [Fact]
    public void ChildrenAndProjectListAreReadOnlyAndNotCastableToArray()
    {
        var project = new Project(
            new Expression[] { PlanFixtures.Attr("a") }, PlanFixtures.Relation("t"));

        Assert.IsNotType<Expression[]>(project.ProjectList);
        Assert.IsNotType<LogicalPlan[]>(project.Children);
        Assert.IsAssignableFrom<IReadOnlyList<Expression>>(project.ProjectList);
        // A read-only view cannot be mutated through IList.
        if (project.ProjectList is IList writableList)
        {
            Assert.True(writableList.IsReadOnly);
        }
    }

    [Fact]
    public void IdentifierMustNotBeEmpty()
    {
        Assert.Throws<ArgumentException>(() => new UnresolvedRelation(Array.Empty<string>()));
    }

    [Fact]
    public void LimitRejectsNegativeCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Limit(-1, PlanFixtures.Relation("t")));
    }

    [Fact]
    public void UnionRequiresAtLeastTwoInputs()
    {
        Assert.Throws<ArgumentException>(
            () => new Union(new LogicalPlan[] { PlanFixtures.Relation("t") }));
    }
}
