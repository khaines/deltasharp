using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans.Expressions;

/// <summary>
/// STORY-04.3.3 (#166): the <c>CaseWhen</c> IR node behind <see cref="Functions.When(Column, object?)"/>
/// / <see cref="Column.When(Column, object?)"/> / <see cref="Column.Otherwise(object?)"/>. It is an
/// immutable, structurally-shared expression whose branches/else derive from its flattened children,
/// and it stays unresolved until its children resolve (type coercion is the analyzer's job, #171).
/// </summary>
public sealed class CaseWhenTests
{
    private static readonly Column Flag = Functions.Col("flag");
    private static readonly Column Other = Functions.Col("other");

    [Fact] // AC1: when(...) seeds a single-branch CaseWhen
    public void When_BuildsSingleBranchCaseWhen()
    {
        var caseWhen = Assert.IsType<CaseWhen>(Functions.When(Flag, 1).Expr);

        Assert.Equal(1, caseWhen.BranchCount);
        Assert.False(caseWhen.HasElse);
        Assert.Null(caseWhen.ElseValue);
        Assert.False(caseWhen.Resolved); // condition is an unresolved attribute
        Assert.Null(caseWhen.Type);
        (Expression Condition, Expression Value) branch = caseWhen.Branches[0];
        Assert.Same(Flag.Expr, branch.Condition);
        Assert.IsType<Literal>(branch.Value);
    }

    [Fact] // AC1: when(...).otherwise(...) closes the CASE with an ELSE
    public void WhenOtherwise_BuildsBranchAndElse()
    {
        var caseWhen = Assert.IsType<CaseWhen>(Functions.When(Flag, 1).Otherwise(0).Expr);

        Assert.Equal(1, caseWhen.BranchCount);
        Assert.True(caseWhen.HasElse);
        Assert.IsType<Literal>(caseWhen.ElseValue);
    }

    [Fact] // AC1: chained when(...) appends a branch in order
    public void ChainedWhen_AppendsBranch()
    {
        var caseWhen = Assert.IsType<CaseWhen>(
            Functions.When(Flag, 1).When(Other, 2).Otherwise(0).Expr);

        Assert.Equal(2, caseWhen.BranchCount);
        Assert.True(caseWhen.HasElse);
        Assert.Same(Flag.Expr, caseWhen.Branches[0].Condition);
        Assert.Same(Other.Expr, caseWhen.Branches[1].Condition);
    }

    [Fact]
    public void CaseWhen_RendersCaseSyntax()
    {
        Column column = Functions.When(Flag, 1).When(Other, 2).Otherwise(0);

        Assert.Equal("CASE WHEN 'flag THEN 1 WHEN 'other THEN 2 ELSE 0 END", column.ToString());
    }

    [Fact]
    public void When_WrapsRawValueAsLiteral_AndPassesColumnThrough()
    {
        var literalBranch = Assert.IsType<CaseWhen>(Functions.When(Flag, 5).Expr);
        Assert.IsType<Literal>(literalBranch.Branches[0].Value);

        var columnBranch = Assert.IsType<CaseWhen>(Functions.When(Flag, Functions.Col("v")).Expr);
        Assert.IsType<UnresolvedAttribute>(columnBranch.Branches[0].Value);
    }

    [Fact] // Spark parity: when() only follows when()
    public void When_OnNonCaseWhenColumn_Throws()
        => Assert.Throws<InvalidOperationException>(() => Functions.Col("a").When(Flag, 1));

    [Fact] // Spark parity: otherwise() only follows when()
    public void Otherwise_OnNonCaseWhenColumn_Throws()
        => Assert.Throws<InvalidOperationException>(() => Functions.Col("a").Otherwise(0));

    [Fact] // Spark parity: when() cannot follow otherwise()
    public void When_AfterOtherwise_Throws()
    {
        Column closed = Functions.When(Flag, 1).Otherwise(0);
        Assert.Throws<InvalidOperationException>(() => closed.When(Other, 2));
    }

    [Fact] // Spark parity: otherwise() cannot be set twice
    public void Otherwise_Twice_Throws()
    {
        Column closed = Functions.When(Flag, 1).Otherwise(0);
        Assert.Throws<InvalidOperationException>(() => closed.Otherwise(9));
    }

    [Fact]
    public void When_NullCondition_Throws()
        => Assert.Throws<ArgumentNullException>(() => Functions.When(null!, 1));

    [Fact] // immutability: chaining returns a new column and leaves the source untouched
    public void Chaining_DoesNotMutateSource()
    {
        Column seed = Functions.When(Flag, 1);
        Column extended = seed.When(Other, 2);

        Assert.NotSame(seed, extended);
        Assert.Equal(1, Assert.IsType<CaseWhen>(seed.Expr).BranchCount);
        Assert.Equal(2, Assert.IsType<CaseWhen>(extended.Expr).BranchCount);
    }

    [Fact] // resolution follows children (analyzer resolves later)
    public void Resolved_FollowsChildren()
    {
        // Both condition and value are resolved -> the CaseWhen is resolved.
        var resolvedCondition = new AttributeReference(
            "flag", DeltaSharp.Types.BooleanType.Instance, nullable: false, new ExprId(1));
        var caseWhen = new CaseWhen(resolvedCondition, Literal.OfInt(1));

        Assert.True(caseWhen.Resolved);
    }

    [Fact] // structural sharing: WithNewChildren returns this when nothing changed
    public void WithNewChildren_Unchanged_ReturnsSameInstance()
    {
        var caseWhen = (CaseWhen)Functions.When(Flag, 1).Otherwise(0).Expr;

        Expression same = caseWhen.WithNewChildren(caseWhen.Children);

        Assert.Same(caseWhen, same);
    }

    [Fact] // WithNewChildren with a genuinely different child rebuilds and preserves shape
    public void WithNewChildren_ChangedChild_RebuildsAndPreservesElseAndBranches()
    {
        // CASE WHEN 'flag THEN 1 WHEN 'other THEN 2 ELSE 0 END
        var original = (CaseWhen)Functions.When(Flag, 1).When(Other, 2).Otherwise(0).Expr;
        Assert.True(original.HasElse);
        Assert.Equal(2, original.BranchCount);

        // Swap the second branch's condition for a genuinely different expression.
        var replacement = Functions.Col("swapped").Expr;
        var newChildren = original.Children.ToArray();
        int changedIndex = 2; // second branch condition in [c0, v0, c1, v1, else]
        newChildren[changedIndex] = replacement;

        var rebuilt = Assert.IsType<CaseWhen>(original.WithNewChildren(newChildren));

        Assert.NotSame(original, rebuilt); // ignoring the new children (return this) would fail here
        Assert.True(rebuilt.HasElse);
        Assert.Equal(2, rebuilt.BranchCount);
        Assert.Same(original.ElseValue, rebuilt.ElseValue); // else preserved
        Assert.Same(replacement, rebuilt.Branches[1].Condition); // the changed child is present
        Assert.Same(original.Branches[0].Condition, rebuilt.Branches[0].Condition); // others intact
        Assert.Same(original.Branches[1].Value, rebuilt.Branches[1].Value);
    }

    [Fact] // MapChildren rewrite swaps a child and rebuilds, preserving the else
    public void MapChildren_SwapsChild_PreservesElse()
    {
        var original = (CaseWhen)Functions.When(Flag, 1).Otherwise(0).Expr;
        var replacement = Functions.Col("swapped").Expr;

        var rebuilt = Assert.IsType<CaseWhen>(
            original.MapChildren(child => ReferenceEquals(child, Flag.Expr) ? replacement : child));

        Assert.NotSame(original, rebuilt);
        Assert.True(rebuilt.HasElse);
        Assert.Same(replacement, rebuilt.Branches[0].Condition);
        Assert.Same(original.ElseValue, rebuilt.ElseValue);
    }

    [Fact] // cross-type: NodeEquals=>true is safe only under the base same-type guard
    public void Equality_DifferentNodeType_IsNotEqual()
    {
        Expression a = Functions.Col("a").Expr;
        Expression b = Functions.Col("b").Expr;

        var caseWhen = new CaseWhen(a, b);
        var and = new And(a, b);

        Assert.NotEqual<Expression>(caseWhen, and);
        Assert.NotEqual<Expression>(and, caseWhen);
    }

    [Fact]
    public void WithNewChildren_WrongArity_Throws()
    {
        var caseWhen = (CaseWhen)Functions.When(Flag, 1).Expr;

        Assert.Throws<ArgumentException>(
            () => caseWhen.WithNewChildren(new Expression[] { Flag.Expr }));
    }

    [Fact] // equality is structural (branch/else split derives from child parity)
    public void Equality_IsStructural()
    {
        Column a = Functions.When(Functions.Col("f"), 1).Otherwise(0);
        Column b = Functions.When(Functions.Col("f"), 1).Otherwise(0);

        Assert.Equal(a.Expr, b.Expr);
        Assert.Equal(a.Expr.GetHashCode(), b.Expr.GetHashCode());

        Column noElse = Functions.When(Functions.Col("f"), 1);
        Assert.NotEqual(a.Expr, noElse.Expr);
    }

    [Fact]
    public void Constructor_SingleBranch_RequiresConditionAndValue()
    {
        Assert.Throws<ArgumentNullException>(() => new CaseWhen(null!, Literal.OfInt(1)));
        Assert.Throws<ArgumentNullException>(() => new CaseWhen(Flag.Expr, null!));
    }

    [Fact] // nullability derives from result values + else (not the blanket base true)
    public void Nullable_NoElse_IsNullable()
    {
        // No else -> an unmatched row is an implicit SQL NULL, so the result is nullable.
        var caseWhen = new CaseWhen(Flag.Expr, Literal.OfInt(1));
        Assert.True(caseWhen.Nullable);
    }

    [Fact] // an else plus all non-null values makes the CASE non-nullable
    public void Nullable_ElseAndAllNonNullValues_IsNotNullable()
    {
        var caseWhen = new CaseWhen(Flag.Expr, Literal.OfInt(1)).WithElse(Literal.OfInt(0));
        Assert.False(caseWhen.Nullable);
    }

    [Fact] // a nullable else propagates nullability
    public void Nullable_NullableElse_IsNullable()
    {
        var caseWhen = new CaseWhen(Flag.Expr, Literal.OfInt(1))
            .WithElse(Literal.Null(DeltaSharp.Types.IntegerType.Instance));
        Assert.True(caseWhen.Nullable);
    }

    [Fact] // a nullable branch value propagates nullability even with a non-null else
    public void Nullable_NullableBranchValue_IsNullable()
    {
        var caseWhen = new CaseWhen(Flag.Expr, Literal.Null(DeltaSharp.Types.IntegerType.Instance))
            .WithElse(Literal.OfInt(0));
        Assert.True(caseWhen.Nullable);
    }

    [Fact] // a nullable condition does NOT make the result nullable
    public void Nullable_NullableConditionOnly_IsNotNullable()
    {
        // Flag is an unresolved attribute (Nullable == true), used only as the condition.
        var caseWhen = new CaseWhen(Flag.Expr, Literal.OfInt(1)).WithElse(Literal.OfInt(0));
        Assert.True(Flag.Expr.Nullable);
        Assert.False(caseWhen.Nullable);
    }
}
