using DeltaSharp.Plans.Expressions;
using Xunit;

namespace DeltaSharp.Core.Tests;

/// <summary>
/// STORY-04.3.3 (#166): the common functions registry surface. Each M1 function builds a named
/// <b>unresolved</b> function node (or a <c>CaseWhen</c> for <c>when</c>) with documented arguments
/// (AC1); aggregate calls are distinguished only by their canonical Spark name — binding is the
/// analyzer's job (AC2); unsupported <c>expr</c> reports a documented diagnostic (AC3).
/// </summary>
public class FunctionsRegistryTests
{
    private static UnresolvedFunction AssertFunction(
        Column column, string name, int argCount, bool isDistinct = false)
    {
        var function = Assert.IsType<UnresolvedFunction>(column.Expr);
        Assert.Equal(name, function.Name);
        Assert.Equal(argCount, function.Arguments.Count);
        Assert.Equal(isDistinct, function.IsDistinct);
        Assert.False(function.Resolved); // lazy: never resolved at construction
        Assert.Null(function.Type);
        return function;
    }

    [Fact] // AC1/AC2
    public void Count_Column_BuildsUnresolvedCountOfOneArg()
    {
        UnresolvedFunction function = AssertFunction(Functions.Count(Functions.Col("id")), "count", 1);
        Assert.IsType<UnresolvedAttribute>(function.Arguments[0]);
    }

    [Fact] // AC1
    public void Count_ColumnName_BindsToColReference()
    {
        UnresolvedFunction function = AssertFunction(Functions.Count("id"), "count", 1);
        var attribute = Assert.IsType<UnresolvedAttribute>(function.Arguments[0]);
        Assert.Equal("id", attribute.Name);
    }

    [Fact] // AC1: count(*) carries a star argument
    public void CountStar_BuildsCountOfUnresolvedStar()
    {
        UnresolvedFunction function = AssertFunction(Functions.Count("*"), "count", 1);
        Assert.IsType<UnresolvedStar>(function.Arguments[0]);
    }

    [Fact] // AC1/AC2: distinct variant sets IsDistinct
    public void CountDistinct_SetsIsDistinctAndOrdersArgs()
    {
        UnresolvedFunction function = AssertFunction(
            Functions.CountDistinct(Functions.Col("a"), Functions.Col("b")),
            "count",
            argCount: 2,
            isDistinct: true);
        Assert.Equal("a", Assert.IsType<UnresolvedAttribute>(function.Arguments[0]).Name);
        Assert.Equal("b", Assert.IsType<UnresolvedAttribute>(function.Arguments[1]).Name);
    }

    [Fact]
    public void CountDistinct_SingleColumn_HasOneArg()
        => AssertFunction(Functions.CountDistinct(Functions.Col("a")), "count", 1, isDistinct: true);

    [Theory] // AC1/AC2: the aggregate names the analyzer classifies as aggregates
    [InlineData("sum")]
    [InlineData("avg")]
    [InlineData("min")]
    [InlineData("max")]
    public void Aggregate_Column_BuildsNamedUnaryFunction(string name)
    {
        Column column = name switch
        {
            "sum" => Functions.Sum(Functions.Col("x")),
            "avg" => Functions.Avg(Functions.Col("x")),
            "min" => Functions.Min(Functions.Col("x")),
            _ => Functions.Max(Functions.Col("x")),
        };

        AssertFunction(column, name, 1);
    }

    [Theory]
    [InlineData("sum")]
    [InlineData("avg")]
    [InlineData("min")]
    [InlineData("max")]
    public void Aggregate_ColumnName_BindsToColReference(string name)
    {
        Column column = name switch
        {
            "sum" => Functions.Sum("x"),
            "avg" => Functions.Avg("x"),
            "min" => Functions.Min("x"),
            _ => Functions.Max("x"),
        };

        UnresolvedFunction function = AssertFunction(column, name, 1);
        Assert.Equal("x", Assert.IsType<UnresolvedAttribute>(function.Arguments[0]).Name);
    }

    [Fact] // AC1: coalesce keeps its argument order
    public void Coalesce_BuildsScalarFunctionInOrder()
    {
        UnresolvedFunction function = AssertFunction(
            Functions.Coalesce(Functions.Col("a"), Functions.Col("b"), Functions.Lit(0)),
            "coalesce",
            argCount: 3);
        Assert.Equal("a", Assert.IsType<UnresolvedAttribute>(function.Arguments[0]).Name);
        Assert.Equal("b", Assert.IsType<UnresolvedAttribute>(function.Arguments[1]).Name);
        Assert.IsType<Literal>(function.Arguments[2]);
    }

    [Fact] // guard
    public void Coalesce_Empty_Throws()
        => Assert.Throws<ArgumentException>(() => Functions.Coalesce());

    [Fact] // guard
    public void Coalesce_NullElement_Throws()
        => Assert.Throws<ArgumentException>(() => Functions.Coalesce(Functions.Col("a"), null!));

    [Fact] // guard
    public void Coalesce_NullArray_Throws()
        => Assert.Throws<ArgumentNullException>(() => Functions.Coalesce(null!));

    [Fact]
    public void Concat_BuildsScalarFunction()
        => AssertFunction(Functions.Concat(Functions.Col("a"), Functions.Col("b")), "concat", 2);

    [Fact]
    public void Concat_Empty_Throws()
        => Assert.Throws<ArgumentException>(() => Functions.Concat());

    [Theory] // AC1: string helpers
    [InlineData("upper")]
    [InlineData("lower")]
    [InlineData("length")]
    [InlineData("trim")]
    public void StringHelper_BuildsUnaryScalarFunction(string name)
    {
        Column column = name switch
        {
            "upper" => Functions.Upper(Functions.Col("s")),
            "lower" => Functions.Lower(Functions.Col("s")),
            "length" => Functions.Length(Functions.Col("s")),
            _ => Functions.Trim(Functions.Col("s")),
        };

        AssertFunction(column, name, 1);
    }

    [Fact] // AC1: nullary date helpers take no arguments
    public void CurrentDateAndTimestamp_BuildZeroArgFunctions()
    {
        AssertFunction(Functions.CurrentDate(), "current_date", 0);
        AssertFunction(Functions.CurrentTimestamp(), "current_timestamp", 0);
    }

    [Theory] // AC1: date extraction helpers
    [InlineData("year")]
    [InlineData("month")]
    [InlineData("dayofmonth")]
    [InlineData("to_date")]
    public void DateHelper_BuildsUnaryScalarFunction(string name)
    {
        Column column = name switch
        {
            "year" => Functions.Year(Functions.Col("d")),
            "month" => Functions.Month(Functions.Col("d")),
            "dayofmonth" => Functions.DayOfMonth(Functions.Col("d")),
            _ => Functions.ToDate(Functions.Col("d")),
        };

        AssertFunction(column, name, 1);
    }

    [Theory] // null-argument guards on the unary builders
    [InlineData("sum")]
    [InlineData("upper")]
    [InlineData("year")]
    public void UnaryFunction_NullColumn_Throws(string name)
    {
        Assert.Throws<ArgumentNullException>(() => _ = name switch
        {
            "sum" => Functions.Sum((Column)null!),
            "upper" => Functions.Upper(null!),
            _ => Functions.Year(null!),
        });
    }

    [Fact] // AC3: unsupported expr reports a documented diagnostic
    public void Expr_IsUnsupported_ThrowsNotSupported()
    {
        NotSupportedException ex =
            Assert.Throws<NotSupportedException>(() => Functions.Expr("a + 1"));
        Assert.Contains("#159", ex.Message, StringComparison.Ordinal);
    }

    [Theory] // AC3: expr still validates its argument before the diagnostic
    [InlineData(null)]
    [InlineData("")]
    public void Expr_NullOrEmpty_ThrowsArgument(string? text)
        => Assert.ThrowsAny<ArgumentException>(() => Functions.Expr(text!));

    [Fact] // AC1 rendering: an unresolved function renders with a leading apostrophe
    public void UnresolvedFunction_RendersWithApostrophe()
    {
        Assert.Equal("'sum('salary)", Functions.Sum("salary").ToString());
        Assert.Equal("'count(distinct 'a)", Functions.CountDistinct(Functions.Col("a")).ToString());
    }
}
