using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>Shared builders for logical-plan tests.</summary>
internal static class PlanFixtures
{
    public static UnresolvedRelation Relation(params string[] identifier) =>
        new(identifier.Length == 0 ? new[] { "t" } : identifier);

    public static UnresolvedAttribute Attr(string name) => new(name);

    public static UnresolvedFunction Gt(string column, string literalName) =>
        new(">", new Expression[] { new UnresolvedAttribute(column), new UnresolvedAttribute(literalName) });

    /// <summary>A small unanalyzed plan: Project over Filter over UnresolvedRelation.</summary>
    public static LogicalPlan SamplePlan() =>
        new Project(
            new Expression[] { Attr("a"), Attr("b") },
            new Filter(
                new UnresolvedFunction(">", new Expression[] { Attr("age"), Attr("21") }),
                Relation("people")));
}
