using DeltaSharp.Plans.Expressions;
using Xunit;

namespace DeltaSharp.Core.Tests;

/// <summary>
/// STORY-04.3.1 (#164): the public <see cref="Column"/> reference and alias surface. Column
/// references stay <b>unresolved</b> with no schema lookup (AC1) and aliases are preserved as
/// <c>Alias</c> nodes for the analyzer (AC3).
/// </summary>
public class ColumnTests
{
    [Fact] // AC1
    public void Col_WrapsUnresolvedAttribute_WithoutSchemaLookup()
    {
        Column column = Functions.Col("salary");

        var attribute = Assert.IsType<UnresolvedAttribute>(column.Expr);
        Assert.Equal("salary", attribute.Name);
        Assert.False(attribute.Resolved);
        Assert.Null(attribute.Type);
    }

    [Fact] // AC1
    public void Column_IsAliasForCol()
    {
        Column column = Functions.Column("id");

        var attribute = Assert.IsType<UnresolvedAttribute>(column.Expr);
        Assert.Equal("id", attribute.Name);
        Assert.False(attribute.Resolved);
    }

    [Fact] // AC1
    public void Col_Star_WrapsBareUnresolvedStar()
    {
        Column column = Functions.Col("*");

        var star = Assert.IsType<UnresolvedStar>(column.Expr);
        Assert.Null(star.Target);
        Assert.False(star.Resolved);
    }

    [Fact] // AC1
    public void Col_QualifiedStar_WrapsTargetedUnresolvedStar()
    {
        Column column = Functions.Col("t.*");

        var star = Assert.IsType<UnresolvedStar>(column.Expr);
        Assert.Equal(new[] { "t" }, star.Target);
        Assert.False(star.Resolved);
    }

    [Theory] // AC1 guard
    [InlineData(null)]
    [InlineData("")]
    public void Col_NullOrEmptyName_Throws(string? name)
        => Assert.ThrowsAny<ArgumentException>(() => Functions.Col(name!));

    [Fact] // AC3
    public void As_WrapsExpressionInAliasPreservingChildAndName()
    {
        Column aliased = Functions.Col("salary").As("s");

        var alias = Assert.IsType<Alias>(aliased.Expr);
        Assert.Equal("s", alias.Name);
        var child = Assert.IsType<UnresolvedAttribute>(alias.Child);
        Assert.Equal("salary", child.Name);
    }

    [Fact] // AC3
    public void Alias_And_Name_AreEquivalentToAs()
    {
        Column reference = Functions.Col("a");

        Assert.Equal("'a AS x", reference.Alias("x").ToString());
        Assert.Equal("'a AS x", reference.Name("x").ToString());
        Assert.Equal("'a AS x", reference.As("x").ToString());
    }

    [Fact] // AC3: alias survives for later analyzer resolution
    public void As_PreservedInExpressionTree()
    {
        Column aliased = Functions.Col("salary").As("s");

        var alias = Assert.IsType<Alias>(aliased.Expr);
        Assert.False(alias.Resolved); // unresolved child keeps the alias unresolved
        Assert.Equal("s", alias.Name); // the alias name is preserved on the node

        // The alias wraps and preserves the original child expression untouched.
        var child = Assert.IsType<UnresolvedAttribute>(alias.Child);
        Assert.Equal("salary", child.Name);
    }

    [Fact]
    public void As_NullOrEmptyAlias_Throws()
    {
        Column reference = Functions.Col("a");
        Assert.ThrowsAny<ArgumentException>(() => reference.As(null!));
        Assert.ThrowsAny<ArgumentException>(() => reference.As(string.Empty));
    }

    [Fact]
    public void ToString_RendersInlineExpression()
        => Assert.Equal("'a", Functions.Col("a").ToString());
}
