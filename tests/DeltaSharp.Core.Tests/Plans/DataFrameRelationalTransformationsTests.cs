using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// STORY-04.2.3 (#162) — the relational <see cref="DataFrame"/> transformation surface:
/// <see cref="DataFrame.Join(DataFrame)"/> and its overloads, <see cref="DataFrame.OrderBy(Column[])"/>
/// / <see cref="DataFrame.Sort(Column[])"/>, <see cref="DataFrame.Limit(int)"/>,
/// <see cref="DataFrame.Distinct"/>, and <see cref="DataFrame.Union(DataFrame)"/>. These tests assert
/// each method builds the correct immutable plan node over the source frames' plans, records join
/// type / condition / ordering without reading any side, leaves the source frames unchanged
/// (structural sharing, #167), and evaluates nothing (the lazy invariant, ADR-0001). The marquee
/// lazy non-vacuity proof lives in <c>LazyEager/DataFrameLazyRelationalTransformationTests</c>.
/// </summary>
public sealed class DataFrameRelationalTransformationsTests
{
    private static DataFrame Left() => new(PlanFixtures.Relation("left"));

    private static DataFrame Right() => new(PlanFixtures.Relation("right"));

    // ----- AC1: Join — no condition (inner, Cartesian) -----

    [Fact]
    public void Join_NoCondition_BuildsInnerJoinWithBothChildren_AndNoCondition()
    {
        DataFrame left = Left();
        DataFrame right = Right();

        DataFrame result = left.Join(right);

        var join = Assert.IsType<Join>(result.Plan);
        Assert.Equal(JoinType.Inner, join.JoinType);
        Assert.Null(join.Condition);
        Assert.Same(left.Plan, join.Left);
        Assert.Same(right.Plan, join.Right);
    }

    // ----- AC1: Join — with condition -----

    [Fact]
    public void Join_WithCondition_BuildsInnerJoinRecordingConditionByReference()
    {
        DataFrame left = Left();
        DataFrame right = Right();
        Column condition = Functions.Col("id");

        DataFrame result = left.Join(right, condition);

        var join = Assert.IsType<Join>(result.Plan);
        Assert.Equal(JoinType.Inner, join.JoinType);
        // The condition is recorded by reference — not evaluated, not rewritten.
        Assert.Same(condition.Expr, join.Condition);
        Assert.Same(left.Plan, join.Left);
        Assert.Same(right.Plan, join.Right);
    }

    // ----- AC1: Join — Spark join-type string mapping -----

    [Theory]
    [InlineData("inner", "Inner")]
    [InlineData("cross", "Cross")]
    [InlineData("outer", "FullOuter")]
    [InlineData("full", "FullOuter")]
    [InlineData("fullouter", "FullOuter")]
    [InlineData("full_outer", "FullOuter")]
    [InlineData("left", "LeftOuter")]
    [InlineData("leftouter", "LeftOuter")]
    [InlineData("left_outer", "LeftOuter")]
    [InlineData("right", "RightOuter")]
    [InlineData("rightouter", "RightOuter")]
    [InlineData("right_outer", "RightOuter")]
    [InlineData("semi", "LeftSemi")]
    [InlineData("leftsemi", "LeftSemi")]
    [InlineData("left_semi", "LeftSemi")]
    [InlineData("anti", "LeftAnti")]
    [InlineData("leftanti", "LeftAnti")]
    [InlineData("left_anti", "LeftAnti")]
    public void Join_MapsSparkJoinTypeString_ToEnum(string joinTypeString, string expected)
    {
        DataFrame result = Left().Join(Right(), Functions.Col("id"), joinTypeString);

        Assert.Equal(expected, Assert.IsType<Join>(result.Plan).JoinType.ToString());
    }

    [Theory]
    [InlineData("INNER")]
    [InlineData("Left_Outer")]
    [InlineData("LEFT OUTER")]
    public void Join_JoinTypeString_IsCaseAndSeparatorInsensitive(string joinTypeString)
    {
        // Normalization (lower-case + strip spaces/underscores) accepts every Spark spelling.
        DataFrame result = Left().Join(Right(), Functions.Col("id"), joinTypeString);

        Assert.IsType<Join>(result.Plan);
    }

    // ----- AC1: Join — using columns -----

    [Fact]
    public void Join_UsingSingleColumn_BuildsInnerUsingJoin()
    {
        DataFrame left = Left();
        DataFrame right = Right();

        DataFrame result = left.Join(right, "id");

        var join = Assert.IsType<Join>(result.Plan);
        Assert.Equal(JoinType.Inner, join.JoinType);
        Assert.Null(join.Condition);
        Assert.NotNull(join.UsingColumns);
        Assert.Equal(new[] { "id" }, join.UsingColumns!);
    }

    [Fact]
    public void Join_UsingColumns_BuildsInnerUsingJoinWithAllColumns()
    {
        DataFrame result = Left().Join(Right(), new[] { "id", "dept" });

        var join = Assert.IsType<Join>(result.Plan);
        Assert.Equal(JoinType.Inner, join.JoinType);
        Assert.Equal(new[] { "id", "dept" }, join.UsingColumns!);
    }

    [Fact]
    public void Join_UsingColumnsWithJoinType_MapsJoinType()
    {
        DataFrame result = Left().Join(Right(), new[] { "id" }, "left");

        var join = Assert.IsType<Join>(result.Plan);
        Assert.Equal(JoinType.LeftOuter, join.JoinType);
        Assert.Equal(new[] { "id" }, join.UsingColumns!);
    }

    // ----- AC1: Join immutability -----

    [Fact]
    public void Join_LeavesBothSourceFramesUnchanged_AndSharesSubtreesByReference()
    {
        DataFrame left = Left();
        DataFrame right = Right();
        LogicalPlan leftPlan = left.Plan;
        LogicalPlan rightPlan = right.Plan;

        DataFrame result = left.Join(right, Functions.Col("id"));

        Assert.Same(leftPlan, left.Plan);
        Assert.Same(rightPlan, right.Plan);
        var join = (Join)result.Plan;
        Assert.Same(leftPlan, join.Left);
        Assert.Same(rightPlan, join.Right);
    }

    // ----- AC3: bad join-type string -----

    [Fact]
    public void Join_UnsupportedJoinTypeString_ThrowsNamingValidTypes()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Left().Join(Right(), Functions.Col("id"), "sideways"));

        Assert.Contains("sideways", ex.Message);
        // The diagnostic names the supported aliases (AC3).
        Assert.Contains("inner", ex.Message);
        Assert.Contains("left_semi", ex.Message);
    }

    // ----- AC2: OrderBy / Sort -----

    [Fact]
    public void OrderBy_Columns_BuildsGlobalSortWithAscendingNullsFirstByDefault()
    {
        DataFrame df = Left();

        DataFrame result = df.OrderBy(Functions.Col("age"));

        var sort = Assert.IsType<Sort>(result.Plan);
        Assert.True(sort.Global);
        Assert.Same(df.Plan, sort.Child);
        var order = Assert.IsType<SortOrder>(Assert.Single(sort.Order));
        Assert.Equal(SortDirection.Ascending, order.Direction);
        Assert.Equal(NullOrdering.NullsFirst, order.NullOrdering);
        Assert.Equal("age", Assert.IsType<UnresolvedAttribute>(order.Child).Name);
    }

    [Fact]
    public void OrderBy_DescColumn_PreservesDescendingNullsLast()
    {
        DataFrame result = Left().OrderBy(Functions.Col("age").Desc());

        var sort = Assert.IsType<Sort>(result.Plan);
        var order = Assert.IsType<SortOrder>(Assert.Single(sort.Order));
        Assert.Equal(SortDirection.Descending, order.Direction);
        Assert.Equal(NullOrdering.NullsLast, order.NullOrdering);
    }

    [Fact]
    public void OrderBy_ExplicitAscColumn_IsNotDoubleWrapped()
    {
        // A column that is already an ordering term (Asc/Desc) passes through unwrapped — no
        // SortOrder-over-SortOrder.
        DataFrame result = Left().OrderBy(Functions.Col("age").Asc());

        var sort = Assert.IsType<Sort>(result.Plan);
        var order = Assert.IsType<SortOrder>(Assert.Single(sort.Order));
        Assert.IsType<UnresolvedAttribute>(order.Child);
    }

    [Fact]
    public void OrderBy_MixedDirections_PreservesOrderAndPerColumnDirection()
    {
        DataFrame result = Left().OrderBy(Functions.Col("a"), Functions.Col("b").Desc());

        var sort = Assert.IsType<Sort>(result.Plan);
        Assert.Collection(
            sort.Order,
            e => Assert.Equal(SortDirection.Ascending, ((SortOrder)e).Direction),
            e => Assert.Equal(SortDirection.Descending, ((SortOrder)e).Direction));
    }

    [Fact]
    public void OrderBy_Names_BuildsAscendingSortOfEachNamedColumn()
    {
        DataFrame result = Left().OrderBy("a", "b");

        var sort = Assert.IsType<Sort>(result.Plan);
        Assert.Collection(
            sort.Order,
            e =>
            {
                var order = Assert.IsType<SortOrder>(e);
                Assert.Equal(SortDirection.Ascending, order.Direction);
                Assert.Equal("a", Assert.IsType<UnresolvedAttribute>(order.Child).Name);
            },
            e => Assert.Equal("b", Assert.IsType<UnresolvedAttribute>(((SortOrder)e).Child).Name));
    }

    [Fact]
    public void Sort_IsEquivalentToOrderBy()
    {
        DataFrame df = Left();
        Column col = Functions.Col("age");

        Assert.Equal(df.OrderBy(col).Plan, df.Sort(col).Plan);
        Assert.Equal(df.OrderBy("age").Plan, df.Sort("age").Plan);
    }

    [Fact]
    public void OrderBy_LeavesSourceFrameUnchanged()
    {
        DataFrame df = Left();
        LogicalPlan original = df.Plan;

        _ = df.OrderBy(Functions.Col("age"));

        Assert.Same(original, df.Plan);
    }

    // ----- AC2: Limit -----

    [Fact]
    public void Limit_BuildsLimitNodeWithCountOverChild()
    {
        DataFrame df = Left();

        DataFrame result = df.Limit(10);

        var limit = Assert.IsType<Limit>(result.Plan);
        Assert.Equal(10, limit.Count);
        Assert.Same(df.Plan, limit.Child);
    }

    [Fact]
    public void Limit_Zero_IsAllowed()
    {
        Assert.Equal(0, Assert.IsType<Limit>(Left().Limit(0).Plan).Count);
    }

    [Fact]
    public void Limit_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Left().Limit(-1));
    }

    // ----- AC2: Distinct -----

    [Fact]
    public void Distinct_BuildsDistinctNodeOverChild()
    {
        DataFrame df = Left();

        DataFrame result = df.Distinct();

        var distinct = Assert.IsType<Distinct>(result.Plan);
        Assert.Same(df.Plan, distinct.Child);
    }

    // ----- AC2: Union -----

    [Fact]
    public void Union_BuildsUnionOfBothInputsInOrder()
    {
        DataFrame left = Left();
        DataFrame right = Right();

        DataFrame result = left.Union(right);

        var union = Assert.IsType<Union>(result.Plan);
        Assert.Collection(
            union.Inputs,
            p => Assert.Same(left.Plan, p),
            p => Assert.Same(right.Plan, p));
    }

    [Fact]
    public void UnionAll_IsEquivalentToUnion()
    {
        DataFrame left = Left();
        DataFrame right = Right();

        Assert.Equal(left.Union(right).Plan, left.UnionAll(right).Plan);
    }

    [Fact]
    public void Union_LeavesBothSourceFramesUnchanged()
    {
        DataFrame left = Left();
        DataFrame right = Right();
        LogicalPlan leftPlan = left.Plan;
        LogicalPlan rightPlan = right.Plan;

        _ = left.Union(right);

        Assert.Same(leftPlan, left.Plan);
        Assert.Same(rightPlan, right.Plan);
    }

    // ----- AC4: chaining preserves node order + intent -----

    [Fact]
    public void ChainedRelationalTransforms_BuildNestedPlan_PreservingOrder()
    {
        DataFrame left = Left();
        DataFrame right = Right();

        DataFrame chained = left
            .Join(right, Functions.Col("id"))
            .Where(Functions.Col("age"))
            .OrderBy(Functions.Col("age").Desc())
            .Distinct()
            .Limit(5);

        // Outermost → innermost: Limit / Distinct / Sort / Filter / Join.
        var limit = Assert.IsType<Limit>(chained.Plan);
        var distinct = Assert.IsType<Distinct>(limit.Child);
        var sort = Assert.IsType<Sort>(distinct.Child);
        var filter = Assert.IsType<Filter>(sort.Child);
        var join = Assert.IsType<Join>(filter.Child);
        Assert.Same(left.Plan, join.Left);
        Assert.Same(right.Plan, join.Right);
    }

    // ----- Null / empty argument guards -----

    [Fact]
    public void Join_NullRight_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Left().Join((DataFrame)null!));
    }

    [Fact]
    public void Join_NullCondition_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Left().Join(Right(), (Column)null!));
    }

    [Fact]
    public void Join_NullJoinTypeString_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => Left().Join(Right(), Functions.Col("id"), (string)null!));
    }

    [Fact]
    public void Join_NullUsingColumns_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => Left().Join(Right(), (System.Collections.Generic.IEnumerable<string>)null!));
    }

    [Fact]
    public void Join_EmptyUsingColumn_Throws()
    {
        Assert.Throws<ArgumentException>(() => Left().Join(Right(), string.Empty));
    }

    [Fact]
    public void OrderBy_NullColumnsArray_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Left().OrderBy((Column[])null!));
    }

    [Fact]
    public void OrderBy_NullColumnElement_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Left().OrderBy(Functions.Col("a"), null!));
    }

    [Fact]
    public void OrderBy_NullFirstName_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => Left().OrderBy((string)null!));
    }

    [Fact]
    public void Union_NullOther_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Left().Union(null!));
    }
}
