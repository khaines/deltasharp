using DeltaSharp.Optimization.Rules;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Optimization;

/// <summary>
/// STORY-04.5.3 — the <see cref="ConstantFolding"/> rule. Covers integer/nested folding (AC1),
/// result-type preservation (AC4), the not-applicable cases (AC2), boolean three-valued folding, and
/// the ANSI overflow guard (folding a would-overflow constant must not silently wrap).
/// </summary>
public sealed class ConstantFoldingTests
{
    private static LogicalPlan Fold(LogicalPlan plan) => new ConstantFolding().Apply(plan);

    private static Expression SoleProjection(LogicalPlan plan) =>
        Assert.Single(Assert.IsType<Project>(plan).ProjectList);

    [Fact]
    public void FoldsIntegerArithmetic_ToLiteral()
    {
        LogicalPlan plan = new Project(
            new Expression[] { new Alias(OptimizerFixtures.OnePlusTwo(), "s") },
            OptimizerFixtures.People());

        LogicalPlan folded = Fold(plan);

        var alias = Assert.IsType<Alias>(SoleProjection(folded));
        var literal = Assert.IsType<Literal>(alias.Child);
        Assert.Equal(IntegerType.Instance, literal.Type);
        Assert.Equal(3, literal.Value);
    }

    [Fact]
    public void FoldsNestedArithmetic_InOnePass()
    {
        // (1 + 2) + 3 -> 6
        var nested = new BinaryArithmetic(
            OptimizerFixtures.OnePlusTwo(), Literal.OfInt(3), ArithmeticOperator.Add);
        LogicalPlan plan = new Project(
            new Expression[] { new Alias(nested, "s") }, OptimizerFixtures.People());

        var alias = Assert.IsType<Alias>(SoleProjection(Fold(plan)));
        Assert.Equal(6, Assert.IsType<Literal>(alias.Child).Value);
    }

    [Fact]
    public void PreservesResultType_ForLongMultiply()
    {
        var expr = new BinaryArithmetic(Literal.OfLong(6L), Literal.OfLong(7L), ArithmeticOperator.Multiply);
        LogicalPlan plan = new Project(
            new Expression[] { new Alias(expr, "s") }, OptimizerFixtures.People());

        var literal = Assert.IsType<Literal>(Assert.IsType<Alias>(SoleProjection(Fold(plan))).Child);
        Assert.Equal(LongType.Instance, literal.Type);
        Assert.Equal(42L, literal.Value);
    }

    [Fact]
    public void FoldsBooleanLiterals_UnderThreeValuedLogic()
    {
        // true AND false -> false, inside a Filter condition.
        var condition = new And(Literal.OfBoolean(true), Literal.OfBoolean(false));
        LogicalPlan plan = new Filter(condition, OptimizerFixtures.People());

        var folded = Assert.IsType<Filter>(Fold(plan));
        var literal = Assert.IsType<Literal>(folded.Condition);
        Assert.False((bool)literal.Value!);
    }

    [Fact]
    public void FoldsNotOfNullBoolean_ToNull()
    {
        var condition = new Not(Literal.Null(BooleanType.Instance));
        LogicalPlan plan = new Filter(condition, OptimizerFixtures.People());

        var literal = Assert.IsType<Literal>(Assert.IsType<Filter>(Fold(plan)).Condition);
        Assert.True(literal.IsNull);
        Assert.Equal(BooleanType.Instance, literal.Type);
    }

    [Fact]
    public void DoesNotFold_NonConstantExpression()
    {
        // age + 1 references a column: not foldable, subtree preserved (AC2).
        var expr = new BinaryArithmetic(OptimizerFixtures.Age, Literal.OfInt(1), ArithmeticOperator.Add);
        LogicalPlan plan = new Project(
            new Expression[] { new Alias(expr, "s") }, OptimizerFixtures.People());

        LogicalPlan folded = Fold(plan);

        Assert.Same(plan, folded);
        Assert.IsType<BinaryArithmetic>(Assert.IsType<Alias>(SoleProjection(folded)).Child);
    }

    [Fact]
    public void DoesNotFold_OnAnsiOverflow()
    {
        // int.MaxValue + 1 overflows: ANSI semantics forbid wrapping, so the node is left intact
        // for execution to raise the overflow error.
        var expr = new BinaryArithmetic(
            Literal.OfInt(int.MaxValue), Literal.OfInt(1), ArithmeticOperator.Add);
        LogicalPlan plan = new Project(
            new Expression[] { new Alias(expr, "s") }, OptimizerFixtures.People());

        LogicalPlan folded = Fold(plan);

        Assert.Same(plan, folded);
        Assert.IsType<BinaryArithmetic>(Assert.IsType<Alias>(SoleProjection(folded)).Child);
    }

    [Fact]
    public void DoesNotFold_Division()
    {
        // Division is deliberately out of M1 constant folding (division-by-zero/result-type nuances).
        var expr = new BinaryArithmetic(Literal.OfInt(6), Literal.OfInt(2), ArithmeticOperator.Divide);
        LogicalPlan plan = new Project(
            new Expression[] { new Alias(expr, "s") }, OptimizerFixtures.People());

        Assert.Same(plan, Fold(plan));
    }

    [Fact]
    public void IsIdempotent()
    {
        LogicalPlan plan = new Project(
            new Expression[] { new Alias(OptimizerFixtures.OnePlusTwo(), "s") },
            OptimizerFixtures.People());

        LogicalPlan once = Fold(plan);
        LogicalPlan twice = Fold(once);

        Assert.Equal(once, twice);
    }
}
