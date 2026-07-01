using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// QE-F1: a <see cref="Join"/> that is still an unresolved <c>USING</c>/natural join must report
/// <see cref="LogicalPlan.Resolved"/> as <see langword="false"/> even when both child plans are
/// resolved. Its shared columns live outside the expression substrate (<c>Expressions</c> is
/// empty), so the generic "children + expressions resolved" check would otherwise flip it to
/// resolved before the analyzer desugars it into an equi-<c>Condition</c>. A prematurely-resolved
/// using-join would be skipped by the fixed-point analyzer and lowered to a physical join with no
/// condition. A condition-join with resolved children + a resolved condition still resolves.
/// </summary>
public sealed class JoinResolutionTests
{
    /// <summary>A resolved leaf plan: no children, no expressions, so the base check resolves it.</summary>
    private sealed class ResolvedLeaf : LogicalPlan
    {
        public ResolvedLeaf()
            : base(PlanCollections.Empty<LogicalPlan>())
        {
        }

        public override IReadOnlyList<Expression> Expressions => PlanCollections.Empty<Expression>();

        public override string NodeName => "ResolvedLeaf";

        public override string SimpleString => "ResolvedLeaf";

        public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren) => this;

        public override LogicalPlan WithNewExpressions(IReadOnlyList<Expression> newExpressions) => this;

        protected override bool NodeEquals(LogicalPlan other) => other is ResolvedLeaf;

        protected override int NodeHashCode() => 0;
    }

    /// <summary>A resolved leaf expression: no children, so the base check resolves it.</summary>
    private sealed class ResolvedExpression : Expression
    {
        public ResolvedExpression()
            : base(PlanCollections.Empty<Expression>())
        {
        }

        public override string NodeName => "ResolvedExpression";

        public override string SimpleString => "resolved";

        public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren) => this;

        protected override bool NodeEquals(Expression other) => other is ResolvedExpression;

        protected override int NodeHashCode() => 0;
    }

    [Fact]
    public void ResolvedLeafAndExpressionFixturesAreThemselvesResolved()
    {
        // Guards against a vacuous test: the fixtures must actually be resolved, otherwise the
        // using-join-false assertions would pass for the wrong reason.
        Assert.True(new ResolvedLeaf().Resolved);
        Assert.True(new ResolvedExpression().Resolved);
    }

    [Fact]
    public void UsingJoinWithResolvedChildrenIsNotResolved()
    {
        var join = new Join(
            new ResolvedLeaf(), new ResolvedLeaf(), JoinType.Inner, usingColumns: new[] { "id" });

        Assert.True(join.Left.Resolved);
        Assert.True(join.Right.Resolved);
        Assert.Empty(join.Expressions);
        Assert.False(join.Resolved);
    }

    [Fact]
    public void NaturalJoinWithResolvedChildrenIsNotResolved()
    {
        var join = new Join(
            new ResolvedLeaf(), new ResolvedLeaf(), JoinType.LeftOuter, isNatural: true);

        Assert.True(join.Left.Resolved);
        Assert.True(join.Right.Resolved);
        Assert.Empty(join.Expressions);
        Assert.False(join.Resolved);
    }

    [Fact]
    public void ConditionJoinWithResolvedChildrenAndConditionIsResolved()
    {
        var join = new Join(
            new ResolvedLeaf(), new ResolvedLeaf(), JoinType.Inner,
            condition: new ResolvedExpression());

        Assert.Null(join.UsingColumns);
        Assert.False(join.IsNatural);
        Assert.True(join.Resolved);
    }

    [Fact]
    public void ConditionJoinWithUnresolvedConditionIsNotResolved()
    {
        // Sanity: the condition-join path still honours expression resolution.
        var join = new Join(
            new ResolvedLeaf(), new ResolvedLeaf(), JoinType.Inner,
            condition: PlanFixtures.Attr("x"));

        Assert.False(join.Resolved);
    }

    [Fact]
    public void UsingJoinWithUnresolvedChildrenIsNotResolved()
    {
        var join = new Join(
            PlanFixtures.Relation("a"), PlanFixtures.Relation("b"), JoinType.Inner,
            usingColumns: new[] { "id" });

        Assert.False(join.Resolved);
    }

    [Fact]
    public void DesugaredConditionJoinFromUsingJoinResolves()
    {
        // Emulates the analyzer replacing a using-join with a resolved condition-join via the
        // WithNewExpressions substrate is not applicable (empty expressions); the analyzer builds a
        // fresh condition-join. That desugared join must resolve.
        var desugared = new Join(
            new ResolvedLeaf(), new ResolvedLeaf(), JoinType.Inner,
            condition: new ResolvedExpression());

        Assert.True(desugared.Resolved);
    }
}
