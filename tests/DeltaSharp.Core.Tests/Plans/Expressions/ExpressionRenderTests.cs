using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// STORY-04.4.2 (#168) <b>AC4</b>: expressions render so that aliases, unresolved attributes,
/// functions, and literals are distinguishable. The merged <see cref="TreeNode{TNode}"/> base
/// (#167) folds each node's children into its inline <see cref="TreeNode{TNode}.SimpleString"/>, so
/// that property <b>subsumes</b> the prior bespoke expression debug renderer; the multi-line
/// <see cref="TreeNode{TNode}.TreeString"/> is used for whole-tree diagnostics.
/// </summary>
public class ExpressionRenderTests
{
    [Fact]
    public void UnresolvedAttribute_RendersWithLeadingQuote()
    {
        Assert.Equal("'salary", new UnresolvedAttribute("salary").SimpleString);
        Assert.Equal("'t.salary", new UnresolvedAttribute(["t", "salary"]).SimpleString);
    }

    [Fact]
    public void ResolvedAttribute_RendersNameWithExprId()
    {
        var reference = new AttributeReference("salary", LongType.Instance, nullable: true, new ExprId(7));

        Assert.Equal("salary#7", reference.SimpleString);
    }

    [Fact]
    public void Literal_RendersValue_StringsDoubleQuoted_NullAsNull()
    {
        Assert.Equal("42", Literal.OfInt(42).SimpleString);
        Assert.Equal("\"ok\"", Literal.OfString("ok").SimpleString);
        Assert.Equal("true", Literal.OfBoolean(true).SimpleString);
        Assert.Equal("null", Literal.Null(IntegerType.Instance).SimpleString);
    }

    [Fact]
    public void UnresolvedFunction_RendersWithLeadingQuoteAndDistinct()
    {
        var scalar = new UnresolvedFunction("sqrt", [new UnresolvedAttribute("x")]);
        var aggregate = new UnresolvedFunction("count", [new UnresolvedAttribute("id")], isDistinct: true);

        Assert.Equal("'sqrt('x)", scalar.SimpleString);
        Assert.Equal("'count(distinct 'id)", aggregate.SimpleString);
    }

    [Fact]
    public void Alias_RendersChildAsName()
    {
        var alias = new Alias(
            new BinaryArithmetic(new UnresolvedAttribute("a"), new UnresolvedAttribute("b"), ArithmeticOperator.Add),
            "total");

        Assert.Equal("('a + 'b) AS total", alias.SimpleString);
    }

    [Fact]
    public void Cast_RendersTargetTypeSimpleString()
    {
        var cast = new Cast(new UnresolvedAttribute("x"), LongType.Instance);

        Assert.Equal("cast('x as bigint)", cast.SimpleString);
    }

    [Fact]
    public void Operators_RenderInfix()
    {
        Assert.Equal("('x >= 10)", new BinaryComparison(new UnresolvedAttribute("x"), Literal.OfInt(10), ComparisonOperator.GreaterThanOrEqual).SimpleString);
        Assert.Equal("('p AND 'q)", new And(new UnresolvedAttribute("p"), new UnresolvedAttribute("q")).SimpleString);
        Assert.Equal("(NOT 'p)", new Not(new UnresolvedAttribute("p")).SimpleString);
        Assert.Equal("('x IS NULL)", new IsNull(new UnresolvedAttribute("x")).SimpleString);
        Assert.Equal("('a <=> 'b)", new EqualNullSafe(new UnresolvedAttribute("a"), new UnresolvedAttribute("b")).SimpleString);
    }

    [Fact]
    public void SortOrder_RendersDirectionAndNulls()
    {
        var order = new SortOrder(new UnresolvedAttribute("x"), SortDirection.Descending, NullOrdering.NullsLast);

        Assert.Equal("'x DESC NULLS LAST", order.SimpleString);
    }

    [Fact]
    public void Star_RendersBareAndQualified()
    {
        Assert.Equal("*", new UnresolvedStar().SimpleString);
        Assert.Equal("t.*", new UnresolvedStar(["t"]).SimpleString);
    }

    [Fact]
    public void FourRequiredKinds_ArePairwiseDistinguishable()
    {
        // Same source identifier "x" appears as a literal value, an unresolved attribute, a function,
        // and inside an alias — the renders must be mutually distinct.
        string literal = Literal.OfString("x").SimpleString;               // "x"
        string unresolvedAttr = new UnresolvedAttribute("x").SimpleString; // 'x
        string function = new UnresolvedFunction("x", []).SimpleString;    // 'x()
        string alias = new Alias(new UnresolvedAttribute("x"), "x").SimpleString; // 'x AS x

        var renders = new[] { literal, unresolvedAttr, function, alias };
        Assert.Equal(renders.Length, renders.Distinct().Count());

        // And specifically: a string literal (double-quoted) never collides with an unresolved
        // attribute (single leading quote).
        Assert.StartsWith("\"", literal);
        Assert.StartsWith("'", unresolvedAttr);
        Assert.DoesNotContain("AS", unresolvedAttr);
        Assert.Contains(" AS ", alias);
    }

    [Fact]
    public void TreeString_RendersIndentedMultilineTree_AndToStringMatches()
    {
        var expression = new BinaryComparison(
            new UnresolvedAttribute("x"), Literal.OfInt(10), ComparisonOperator.GreaterThanOrEqual);

        string expected =
            "('x >= 10)\n"
            + ":- 'x\n"
            + "+- 10\n";

        Assert.Equal(expected, expression.TreeString());
        // The merged base's ToString is the multi-line tree render.
        Assert.Equal(expression.TreeString(), expression.ToString());
    }
}
