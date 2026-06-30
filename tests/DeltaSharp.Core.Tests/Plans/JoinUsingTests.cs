using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// QE-F2: <see cref="Join"/> can represent using-joins (<c>df.join(other, Seq("id"))</c>) and
/// natural joins before resolution, round-tripping through immutability, rendering, equality, and
/// hashing, and is distinct from a condition-join.
/// </summary>
public sealed class JoinUsingTests
{
    private static Join UsingJoin(params string[] columns) =>
        new(PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner, usingColumns: columns);

    [Fact]
    public void UsingJoinRoundTripsImmutableAndRendered()
    {
        var join = UsingJoin("id", "k");

        Assert.Equal(new[] { "id", "k" }, join.UsingColumns);
        Assert.Null(join.Condition);
        Assert.False(join.IsNatural);
        Assert.Equal("'Join Inner, using [id, k]", join.SimpleString);
        // Using columns are not part of the expression substrate.
        Assert.Empty(join.Expressions);
    }

    [Fact]
    public void NaturalJoinRenders()
    {
        var join = new Join(PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.LeftOuter, isNatural: true);
        Assert.True(join.IsNatural);
        Assert.Null(join.UsingColumns);
        Assert.Equal("'Join LeftOuter, natural", join.SimpleString);
    }

    [Fact]
    public void UsingColumnsAreDefensivelyCopied()
    {
        var columns = new List<string> { "id" };
        var join = new Join(
            PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner, usingColumns: columns);

        columns.Add("extra");

        Assert.Single(join.UsingColumns!);
    }

    [Fact]
    public void UsingColumnsViewIsNotCastableToMutableArray()
    {
        var join = UsingJoin("id");
        Assert.IsNotType<string[]>(join.UsingColumns);
    }

    [Fact]
    public void UsingAndConditionAreMutuallyExclusive()
    {
        Assert.Throws<ArgumentException>(() => new Join(
            PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner,
            condition: PlanFixtures.Attr("x"), usingColumns: new[] { "id" }));
    }

    [Fact]
    public void NaturalAndConditionAreMutuallyExclusive()
    {
        Assert.Throws<ArgumentException>(() => new Join(
            PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner,
            condition: PlanFixtures.Attr("x"), isNatural: true));
    }

    [Fact]
    public void NaturalAndUsingAreMutuallyExclusive()
    {
        Assert.Throws<ArgumentException>(() => new Join(
            PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner,
            usingColumns: new[] { "id" }, isNatural: true));
    }

    [Fact]
    public void EmptyUsingColumnsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new Join(
            PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner,
            usingColumns: System.Array.Empty<string>()));
    }

    [Fact]
    public void UsingJoinIsDistinctFromConditionJoinAndOtherUsingColumns()
    {
        var usingJoin = UsingJoin("id");
        var conditionJoin = new Join(
            PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner,
            condition: PlanFixtures.Attr("id"));
        var otherUsing = UsingJoin("other");
        var natural = new Join(PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner, isNatural: true);

        Assert.NotEqual(usingJoin, conditionJoin);
        Assert.NotEqual(usingJoin, otherUsing);
        Assert.NotEqual(usingJoin, natural);
    }

    [Fact]
    public void EqualUsingJoinsAreEqualAndShareHash()
    {
        var a = UsingJoin("id", "k");
        var b = UsingJoin("id", "k");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void WithNewChildrenPreservesUsingColumnsAndNatural()
    {
        var join = UsingJoin("id");
        var rebuilt = (Join)join.WithNewChildren(new LogicalPlan[]
        {
            PlanFixtures.Relation("x"), PlanFixtures.Relation("y"),
        });

        Assert.Equal(new[] { "id" }, rebuilt.UsingColumns);

        var natural = new Join(PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner, isNatural: true);
        var rebuiltNatural = (Join)natural.WithNewChildren(new LogicalPlan[]
        {
            PlanFixtures.Relation("x"), PlanFixtures.Relation("y"),
        });
        Assert.True(rebuiltNatural.IsNatural);
    }

    [Fact]
    public void UsingJoinRendersInTree()
    {
        var join = UsingJoin("id");
        string expected =
            "'Join Inner, using [id]\n"
            + ":- 'UnresolvedRelation [a]\n"
            + "+- 'UnresolvedRelation [b]\n";
        Assert.Equal(expected, join.TreeString());
    }
}
