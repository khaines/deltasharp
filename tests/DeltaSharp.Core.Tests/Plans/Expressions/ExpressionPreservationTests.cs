using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// STORY-04.4.2 (#168) <b>AC1</b>: from column/function API calls, expression nodes preserve source
/// name, argument order, the nullability hint, and unresolved status.
/// </summary>
public class ExpressionPreservationTests
{
    [Fact]
    public void UnresolvedAttribute_PreservesMultipartNameAndUnresolvedStatus()
    {
        var attribute = new UnresolvedAttribute(["t", "salary"]);

        Assert.Equal(new[] { "t", "salary" }, attribute.NameParts);
        Assert.Equal("t.salary", attribute.Name);
        Assert.False(attribute.Resolved);
        Assert.Null(attribute.Type);
    }

    [Fact]
    public void UnresolvedFunction_PreservesNameArgumentOrderDistinctAndUnresolvedStatus()
    {
        var first = new UnresolvedAttribute("a");
        var second = Literal.OfInt(2);
        var third = new UnresolvedAttribute("c");

        var function = new UnresolvedFunction("coalesce", [first, second, third], isDistinct: true);

        Assert.Equal("coalesce", function.Name);
        Assert.True(function.IsDistinct);
        Assert.False(function.Resolved);
        // Argument order is positional and preserved exactly.
        Assert.Same(first, function.Arguments[0]);
        Assert.Same(second, function.Arguments[1]);
        Assert.Same(third, function.Arguments[2]);
        Assert.Equal(3, function.Arguments.Count);
    }

    [Fact]
    public void Alias_PreservesOutputNameAndChild()
    {
        var child = new BinaryArithmetic(new UnresolvedAttribute("a"), new UnresolvedAttribute("b"), ArithmeticOperator.Add);

        var alias = new Alias(child, "total");

        Assert.Equal("total", alias.Name);
        Assert.Same(child, alias.Child);
    }

    [Fact]
    public void UnresolvedStar_PreservesQualifierAndUnresolvedStatus()
    {
        var bare = new UnresolvedStar();
        var qualified = new UnresolvedStar(["t"]);

        Assert.Null(bare.Target);
        Assert.False(bare.Resolved);
        Assert.Equal(new[] { "t" }, qualified.Target);
        Assert.False(qualified.Resolved);
    }

    [Fact]
    public void NullabilityHint_LiteralValueIsNonNull_NullLiteralIsNullable()
    {
        Assert.False(Literal.OfInt(7).Nullable);
        Assert.True(Literal.Null(IntegerType.Instance).Nullable);
    }

    [Fact]
    public void NullabilityHint_PropagatesOnAnyNullAcrossOperands()
    {
        var nonNull = Literal.OfLong(1);
        var nullable = new UnresolvedAttribute("maybe"); // unknown -> conservatively nullable

        Assert.False(new BinaryArithmetic(nonNull, nonNull, ArithmeticOperator.Multiply).Nullable);
        Assert.True(new BinaryArithmetic(nonNull, nullable, ArithmeticOperator.Multiply).Nullable);
        Assert.True(new Or(nullable, nonNull).Nullable);
    }

    [Fact]
    public void NullabilityHint_NullPredicatesAreNeverNull()
    {
        var nullable = new UnresolvedAttribute("maybe");

        Assert.False(new IsNull(nullable).Nullable);
        Assert.False(new IsNotNull(nullable).Nullable);
        Assert.False(new EqualNullSafe(nullable, nullable).Nullable);
    }

    [Fact]
    public void ResolvedStatus_FoldsOverChildren()
    {
        var resolvedLeaf = new AttributeReference("x", IntegerType.Instance, nullable: true, new ExprId(1));
        var unresolvedLeaf = new UnresolvedAttribute("y");

        Assert.True(new Alias(resolvedLeaf, "x").Resolved);
        Assert.False(new Alias(unresolvedLeaf, "y").Resolved);
        Assert.False(new BinaryComparison(resolvedLeaf, unresolvedLeaf, ComparisonOperator.Equal).Resolved);
    }

    [Fact]
    public void SortOrder_PreservesDirectionAndNullOrdering()
    {
        var order = new SortOrder(new UnresolvedAttribute("x"), SortDirection.Descending, NullOrdering.NullsLast);

        Assert.Equal(SortDirection.Descending, order.Direction);
        Assert.Equal(NullOrdering.NullsLast, order.NullOrdering);
    }
}
