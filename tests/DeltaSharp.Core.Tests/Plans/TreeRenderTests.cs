using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// AC4: rendering a plan before analysis keeps unresolved attributes, functions, and relations
/// explicitly unresolved (a leading apostrophe), and <c>Resolved</c> is false.
/// </summary>
public sealed class TreeRenderTests
{
    [Fact]
    public void UnanalyzedProjectFilterRelationRendersWithUnresolvedMarkers()
    {
        LogicalPlan plan = PlanFixtures.SamplePlan();

        string expected =
            "'Project ['a, 'b]\n"
            + "+- 'Filter ('>('age, '21))\n"
            + "   +- 'UnresolvedRelation [people]\n";

        Assert.Equal(expected, plan.TreeString());
    }

    [Fact]
    public void UnionRendersBranchConnectors()
    {
        var union = new Union(new LogicalPlan[]
        {
            PlanFixtures.Relation("left"),
            PlanFixtures.Relation("right"),
        });

        string expected =
            "'Union\n"
            + ":- 'UnresolvedRelation [left]\n"
            + "+- 'UnresolvedRelation [right]\n";

        Assert.Equal(expected, union.TreeString());
    }

    [Fact]
    public void EveryLineOfAnUnanalyzedPlanIsApostrophePrefixed()
    {
        string[] lines = PlanFixtures.SamplePlan()
            .TreeString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines)
        {
            Assert.Contains('\'', line);
        }
    }

    [Fact]
    public void UnresolvedMarkersReportNotResolved()
    {
        Assert.False(new UnresolvedAttribute("a").Resolved);
        Assert.False(new UnresolvedFunction("sum", new Expression[] { new UnresolvedAttribute("a") }).Resolved);
        Assert.False(PlanFixtures.Relation("t").Resolved);
    }

    [Fact]
    public void PlanContainingAnUnresolvedNodeIsItselfUnresolved()
    {
        Assert.False(PlanFixtures.SamplePlan().Resolved);
    }

    [Fact]
    public void DistinctFunctionRendersDistinctKeyword()
    {
        var function = new UnresolvedFunction(
            "count", new Expression[] { new UnresolvedAttribute("a") }, isDistinct: true);
        Assert.Equal("'count(distinct 'a)", function.SimpleString);
    }

    [Fact]
    public void MultipartAttributeRendersDottedName()
    {
        Assert.Equal("'t.a", new UnresolvedAttribute(new[] { "t", "a" }).SimpleString);
    }
}
