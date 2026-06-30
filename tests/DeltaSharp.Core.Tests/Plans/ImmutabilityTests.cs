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
    public void AggregateGroupingAndAggregateInputsAreDefensivelyCopied()
    {
        var grouping = new List<Expression> { PlanFixtures.Attr("g") };
        var aggregate = new List<Expression> { PlanFixtures.Attr("a") };
        var node = new Aggregate(grouping, aggregate, PlanFixtures.Relation("t"));

        grouping.Add(PlanFixtures.Attr("g2"));
        aggregate.Add(PlanFixtures.Attr("a2"));

        Assert.Single(node.GroupingExpressions);
        Assert.Single(node.AggregateExpressions);
        Assert.IsNotType<Expression[]>(node.GroupingExpressions);
        Assert.IsNotType<Expression[]>(node.AggregateExpressions);
        Assert.IsNotType<Expression[]>(node.Expressions);
    }

    [Fact]
    public void SortOrderInputIsDefensivelyCopiedAndNotCastable()
    {
        var order = new List<Expression> { PlanFixtures.Attr("a") };
        var sort = new Sort(order, global: true, PlanFixtures.Relation("t"));

        order.Add(PlanFixtures.Attr("b"));

        Assert.Single(sort.Order);
        Assert.IsNotType<Expression[]>(sort.Order);
        Assert.IsNotType<Expression[]>(sort.Expressions);
    }

    [Fact]
    public void UnionInputsAreDefensivelyCopiedAndNotCastable()
    {
        var inputs = new List<LogicalPlan> { PlanFixtures.Relation("a"), PlanFixtures.Relation("b") };
        var union = new Union(inputs);

        inputs.Add(PlanFixtures.Relation("c"));

        Assert.Equal(2, union.Inputs.Count);
        Assert.IsNotType<LogicalPlan[]>(union.Inputs);
        Assert.IsNotType<LogicalPlan[]>(union.Children);
    }

    [Fact]
    public void JoinUsingColumnsAreDefensivelyCopiedAndNotCastable()
    {
        var columns = new List<string> { "id" };
        var join = new Join(
            PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner, usingColumns: columns);

        columns.Add("k");

        Assert.Single(join.UsingColumns!);
        Assert.IsNotType<string[]>(join.UsingColumns);
    }

    [Fact]
    public void FilterAndJoinExpressionsAreNotCastableToMutableArray()
    {
        var filter = new Filter(PlanFixtures.Attr("a"), PlanFixtures.Relation("t"));
        Assert.IsNotType<Expression[]>(filter.Expressions);

        var join = new Join(
            PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner,
            condition: PlanFixtures.Attr("x"));
        Assert.IsNotType<Expression[]>(join.Expressions);
    }

    [Fact]
    public void SinkDescriptorPartitionColumnsAndOptionsAreDefensivelyCopiedAndNotCastable()
    {
        var partitions = new List<string> { "year" };
        var options = new Dictionary<string, string> { ["k"] = "v" };
        var sink = new SinkDescriptor(
            "parquet", partitionColumns: partitions, options: options);

        partitions.Add("month");
        options["k2"] = "v2";

        Assert.Single(sink.PartitionColumns);
        Assert.Single(sink.Options);
        Assert.IsNotType<string[]>(sink.PartitionColumns);

        // Even the empty-partition case is a non-castable read-only view (no Array.Empty leak).
        var noPartitions = new SinkDescriptor("parquet");
        Assert.IsNotType<string[]>(noPartitions.PartitionColumns);
    }

    [Fact]
    public void EmptyExpressionViewsAreNotCastableToMutableArray()
    {
        Assert.IsNotType<Expression[]>(new Limit(1, PlanFixtures.Relation("t")).Expressions);
        Assert.IsNotType<Expression[]>(new Distinct(PlanFixtures.Relation("t")).Expressions);
        Assert.IsNotType<Expression[]>(PlanFixtures.Relation("t").Expressions);
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
