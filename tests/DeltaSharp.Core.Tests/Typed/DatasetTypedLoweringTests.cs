using System.Linq.Expressions;
using DeltaSharp.Core.Tests.Plans;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Typed;

/// <summary>
/// STORY-04.2.4 (#163) AC1 — structural-fidelity tests for <see cref="TypedExpressionLowering"/>. Each
/// case lowers a typed lambda and asserts the produced expression IR is <b>structurally equal</b> to
/// the hand-built <see cref="Functions"/>/<see cref="Column"/> equivalent, so a mutation to a single
/// lowering arm (for example <c>Add</c>&#8594;<c>Subtract</c>, or dropping a value-changing cast)
/// reddens the corresponding test. It also pins the correctness fixes the review council flagged:
/// <c>== null</c>/<c>!= null</c> &#8594; <c>IsNull</c>/<c>IsNotNull</c>, bitwise <c>&amp;</c>/<c>|</c>
/// rejection, value-changing numeric <c>Convert</c> &#8594; <c>Cast</c>, and folding of
/// parameter-independent arithmetic subtrees.
/// </summary>
public sealed class DatasetTypedLoweringTests
{
    public sealed class Rec
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public int Age { get; set; }

        public int Flags { get; set; }

        public int Other { get; set; }

        public bool A { get; set; }

        public bool B { get; set; }
    }

    private static Column Lower(Expression<Func<Rec, bool>> predicate) =>
        TypedExpressionLowering.Lower(predicate);

    private static Column LowerSelect(Expression<Func<Rec, object?>> selector) =>
        TypedExpressionLowering.Lower(selector);

    // ----- Item 1: `== null` / `!= null` lower to IsNull / IsNotNull (not 3VL comparisons) -----

    [Fact]
    public void EqualsNull_LowersToIsNull()
    {
        Column lowered = Lower(p => p.Name == null);

        Assert.Equal(Functions.Col("Name").IsNull().Expr, lowered.Expr);
        // A regression to NotEqual/EqualTo against a NULL literal (3VL, matches nothing) would differ:
        Assert.NotEqual(Functions.Col("Name").EqualTo(Functions.Lit(null)).Expr, lowered.Expr);
    }

    [Fact]
    public void NotEqualsNull_LowersToIsNotNull()
    {
        Column lowered = Lower(p => p.Name != null);

        Assert.Equal(Functions.Col("Name").IsNotNull().Expr, lowered.Expr);
        Assert.NotEqual(Functions.Col("Name").NotEqual(Functions.Lit(null)).Expr, lowered.Expr);
    }

    [Fact]
    public void NullOnLeftHandSide_LowersToIsNull()
    {
        Column lowered = Lower(p => null == p.Name);

        Assert.Equal(Functions.Col("Name").IsNull().Expr, lowered.Expr);
    }

    [Fact]
    public void TypedIsNull_MatchesUntypedFunctionsEquivalentPlan()
    {
        DataFrame df = new(PlanFixtures.Relation("people"));

        DataFrame untyped = df.Filter(Functions.Col("Name").IsNull());
        Dataset<Rec> typed = df.As<Rec>().Where(p => p.Name == null);

        Assert.Equal(untyped.Plan, typed.Plan);
    }

    // ----- Item 2: bitwise `&`/`|` is rejected; boolean `&`/`|` is still supported -----

    [Fact]
    public void BitwiseAnd_ThrowsDeterministicDiagnostic()
    {
        var ex = Assert.Throws<UnsupportedTypedExpressionException>(
            () => Lower(p => (p.Flags & 4) == 4));

        Assert.Contains("bitwise", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BitwiseOr_ThrowsDeterministicDiagnostic()
    {
        var ex = Assert.Throws<UnsupportedTypedExpressionException>(
            () => Lower(p => (p.Flags | 4) == 4));

        Assert.Contains("bitwise", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BooleanNonShortCircuitAnd_StillLowersToLogicalAnd()
    {
        Column lowered = Lower(p => p.A & p.B);

        Assert.Equal(Functions.Col("A").And(Functions.Col("B")).Expr, lowered.Expr);
    }

    [Fact]
    public void BooleanNonShortCircuitOr_StillLowersToLogicalOr()
    {
        Column lowered = Lower(p => p.A | p.B);

        Assert.Equal(Functions.Col("A").Or(Functions.Col("B")).Expr, lowered.Expr);
    }

    // ----- Item 3: a value-changing numeric Convert lowers to an explicit Cast -----

    [Fact]
    public void ValueChangingNumericConvert_LowersToCast()
    {
        Column lowered = LowerSelect(p => (double)p.Flags / p.Other);

        Column castLeft = new(new DeltaSharp.Plans.Expressions.Cast(Functions.Col("Flags").Expr, DoubleType.Instance));
        Column castRight = new(new DeltaSharp.Plans.Expressions.Cast(Functions.Col("Other").Expr, DoubleType.Instance));
        Column expected = castLeft.Divide(castRight);

        Assert.Equal(expected.Expr, lowered.Expr);
        // Silently dropping the convert (integer division) would produce a different tree:
        Assert.NotEqual(Functions.Col("Flags").Divide(Functions.Col("Other")).Expr, lowered.Expr);
    }

    [Fact]
    public void BoxingConvertToObject_IsUnwrapped()
    {
        Column lowered = LowerSelect(p => p.Age);

        Assert.Equal(Functions.Col("Age").Expr, lowered.Expr);
    }

    // ----- Item 4: parameter-independent arithmetic folds to a constant; unfoldable is honest -----

    [Fact]
    public void CapturedArithmeticSubtree_FoldsToConstant()
    {
        int threshold = 21;

        Column lowered = Lower(p => p.Age >= threshold + 1);

        Assert.Equal(Functions.Col("Age").Geq(Functions.Lit(22)).Expr, lowered.Expr);
    }

    [Fact]
    public void UnfoldableParameterIndependentSubtree_ThrowsHonestDiagnostic()
    {
        var ex = Assert.Throws<UnsupportedTypedExpressionException>(
            () => Lower(p => p.Age >= Math.Abs(-5)));

        Assert.Contains("Assign it to a local variable", ex.Message);
    }

    // ----- Item 10: arithmetic operators lower structurally (mutation-sensitive) -----

    [Theory]
    [MemberData(nameof(ArithmeticCases))]
    public void Arithmetic_LowersStructurally(Expression<Func<Rec, object?>> selector, Column expected)
    {
        Column lowered = TypedExpressionLowering.Lower(selector);

        Assert.Equal(expected.Expr, lowered.Expr);
    }

    public static TheoryData<Expression<Func<Rec, object?>>, Column> ArithmeticCases() => new()
    {
        { p => p.Age + 2, Functions.Col("Age").Plus(Functions.Lit(2)) },
        { p => p.Age - 2, Functions.Col("Age").Minus(Functions.Lit(2)) },
        { p => p.Age * 2, Functions.Col("Age").Multiply(Functions.Lit(2)) },
        { p => p.Age / 2, Functions.Col("Age").Divide(Functions.Lit(2)) },
        { p => p.Age % 2, Functions.Col("Age").Mod(Functions.Lit(2)) },
    };

    // ----- Item 10: boolean operators lower structurally (mutation-sensitive) -----

    [Fact]
    public void LogicalNot_LowersStructurally()
    {
        Column lowered = Lower(p => !(p.Age > 18));

        Assert.Equal(Functions.Col("Age").Gt(Functions.Lit(18)).Not().Expr, lowered.Expr);
    }

    [Fact]
    public void ShortCircuitAnd_LowersStructurally()
    {
        Column lowered = Lower(p => p.Age > 18 && p.Age < 65);

        Column expected = Functions.Col("Age").Gt(Functions.Lit(18)).And(Functions.Col("Age").Lt(Functions.Lit(65)));
        Assert.Equal(expected.Expr, lowered.Expr);
    }

    [Fact]
    public void ShortCircuitOr_LowersStructurally()
    {
        Column lowered = Lower(p => p.Age < 10 || p.Age > 65);

        Column expected = Functions.Col("Age").Lt(Functions.Lit(10)).Or(Functions.Col("Age").Gt(Functions.Lit(65)));
        Assert.Equal(expected.Expr, lowered.Expr);
    }

    // ----- Item 10: comparison operators lower structurally (mutation-sensitive) -----

    [Theory]
    [MemberData(nameof(ComparisonCases))]
    public void Comparison_LowersStructurally(Expression<Func<Rec, bool>> predicate, Column expected)
    {
        Column lowered = TypedExpressionLowering.Lower(predicate);

        Assert.Equal(expected.Expr, lowered.Expr);
    }

    public static TheoryData<Expression<Func<Rec, bool>>, Column> ComparisonCases() => new()
    {
        { p => p.Age == 30, Functions.Col("Age").EqualTo(Functions.Lit(30)) },
        { p => p.Age != 30, Functions.Col("Age").NotEqual(Functions.Lit(30)) },
        { p => p.Age < 30, Functions.Col("Age").Lt(Functions.Lit(30)) },
        { p => p.Age <= 30, Functions.Col("Age").Leq(Functions.Lit(30)) },
        { p => p.Age > 30, Functions.Col("Age").Gt(Functions.Lit(30)) },
        { p => p.Age >= 30, Functions.Col("Age").Geq(Functions.Lit(30)) },
    };
}
