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
/// lowering arm (for example <c>Add</c>&#8594;<c>Subtract</c>, or reintroducing a value-changing cast)
/// reddens the corresponding test. It also pins the correctness fixes the review council flagged:
/// <c>== null</c>/<c>!= null</c> &#8594; <c>IsNull</c>/<c>IsNotNull</c>, bitwise <c>&amp;</c>/<c>|</c>
/// rejection, C# numeric <c>Convert</c> promotions/casts <b>unwrapped</b> so a typed expression lowers
/// to the byte-identical plan as its untyped <c>Col op Col</c> equivalent (<b>no fork</b>), and folding
/// of parameter-independent arithmetic subtrees.
/// </summary>
public sealed class DatasetTypedLoweringTests
{
    public enum Color
    {
        Red = 0,
        Green = 1,
        Blue = 2,
    }

    public sealed class Rec
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public int Age { get; set; }

        public int Flags { get; set; }

        public int Other { get; set; }

        public bool A { get; set; }

        public bool B { get; set; }

        public short ShortCol1 { get; set; }

        public short ShortCol2 { get; set; }

        // Spark's `byte`/TINYINT is a *signed* 8-bit integer; the schema deriver maps C# `sbyte`
        // (not the unsigned `byte`) to ByteType, so these use `sbyte`.
        public sbyte ByteCol1 { get; set; }

        public sbyte ByteCol2 { get; set; }

        public float FloatCol { get; set; }

        public double DoubleCol { get; set; }
    }

    // Separate record: an enum property has no ADR-0008 schema mapping, so it must NOT sit on `Rec`
    // (which `df.As<Rec>()` derives a schema from). The enum no-fork test lowers a lambda directly and
    // never derives a schema, so a dedicated encoded type is safe.
    public sealed class EnumRec
    {
        public Color EnumCol { get; set; }
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

    // ----- Item 3 / NO-FORK: C# numeric Convert promotions & explicit casts are UNWRAPPED so a typed
    // arithmetic/comparison expression lowers to the byte-identical plan as its untyped `Col op Col`
    // equivalent. Each pin below REDDENS if a `Cast` the untyped side lacks is (re)introduced. -----

    [Fact]
    public void NumericConvert_UnwrapsToMatchUntyped()
    {
        // `(double)p.Flags / p.Other` — C# inserts Convert(Flags→double) and promotes Other→double.
        // Both Converts UNWRAP, so the typed plan is the SAME `Divide(Col, Col)` as the untyped API.
        // DeltaSharp's `/` is ALREADY fractional (returns DOUBLE, matching Spark — see
        // ArithmeticResultType), so the `(double)` cast is redundant AND must not bake a Cast into the
        // plan; doing so would pre-empt Catalyst's TypeCoercion and FORK from the untyped side.
        Column typed = LowerSelect(p => (double)p.Flags / p.Other);
        Column untyped = Functions.Col("Flags") / Functions.Col("Other");

        Assert.Equal(untyped.Expr, typed.Expr);

        // Reddens if the removed value-changing-Cast branch is reintroduced:
        Column castLeft = new(new DeltaSharp.Plans.Expressions.Cast(Functions.Col("Flags").Expr, DoubleType.Instance));
        Column castRight = new(new DeltaSharp.Plans.Expressions.Cast(Functions.Col("Other").Expr, DoubleType.Instance));
        Assert.NotEqual(castLeft.Divide(castRight).Expr, typed.Expr);
    }

    [Fact]
    public void ShortAddition_LowersToSamePlanAsUntyped_NoFork()
    {
        // C# promotes both `short` operands to `int` (Plus(Convert(Col,int), Convert(Col,int))); both
        // Converts UNWRAP so the typed plan equals the untyped `Plus(Col, Col)`.
        Column typed = LowerSelect(p => p.ShortCol1 + p.ShortCol2);
        Column untyped = Functions.Col("ShortCol1").Plus(Functions.Col("ShortCol2"));

        Assert.Equal(untyped.Expr, typed.Expr);
        // Reddens if a Cast(Col AS int) is reintroduced on either operand:
        Assert.NotEqual(
            new Column(new DeltaSharp.Plans.Expressions.Cast(Functions.Col("ShortCol1").Expr, IntegerType.Instance))
                .Plus(new Column(new DeltaSharp.Plans.Expressions.Cast(Functions.Col("ShortCol2").Expr, IntegerType.Instance)))
                .Expr,
            typed.Expr);
    }

    [Fact]
    public void ByteAddition_LowersToSamePlanAsUntyped_NoFork()
    {
        // Two `sbyte` (Spark TINYINT) operands promote to `int` in C#; both Converts UNWRAP so the typed
        // plan equals the untyped `Plus(Col, Col)`.
        Column typed = LowerSelect(p => p.ByteCol1 + p.ByteCol2);
        Column untyped = Functions.Col("ByteCol1").Plus(Functions.Col("ByteCol2"));

        Assert.Equal(untyped.Expr, typed.Expr);
    }

    [Fact]
    public void MixedIntLongAddition_LowersToSamePlanAsUntyped_NoFork()
    {
        // `int op long` promotes the int operand to long (Plus(Convert(Age,long), Id)); the Convert
        // UNWRAPS so the typed plan equals the untyped `Plus(Col, Col)` — Catalyst does the widening.
        Column typed = LowerSelect(p => p.Age + p.Id);
        Column untyped = Functions.Col("Age").Plus(Functions.Col("Id"));

        Assert.Equal(untyped.Expr, typed.Expr);
        // Reddens if a Cast(Age AS long) is reintroduced:
        Assert.NotEqual(
            new Column(new DeltaSharp.Plans.Expressions.Cast(Functions.Col("Age").Expr, LongType.Instance))
                .Plus(Functions.Col("Id")).Expr,
            typed.Expr);
    }

    [Fact]
    public void MixedFloatDoubleAddition_LowersToSamePlanAsUntyped_NoFork()
    {
        // `float op double` promotes the float operand to double; the Convert UNWRAPS so the typed plan
        // equals the untyped `Plus(Col, Col)`.
        Column typed = LowerSelect(p => p.FloatCol + p.DoubleCol);
        Column untyped = Functions.Col("FloatCol").Plus(Functions.Col("DoubleCol"));

        Assert.Equal(untyped.Expr, typed.Expr);
        // Reddens if a Cast(FloatCol AS double) is reintroduced:
        Assert.NotEqual(
            new Column(new DeltaSharp.Plans.Expressions.Cast(Functions.Col("FloatCol").Expr, DoubleType.Instance))
                .Plus(Functions.Col("DoubleCol")).Expr,
            typed.Expr);
    }

    [Fact]
    public void ShortComparison_LowersToSamePlanAsUntyped_NoFork()
    {
        // A comparison over two `short` operands also promotes both to `int`; both Converts UNWRAP so
        // the typed plan equals the untyped `Gt(Col, Col)`.
        Column typed = Lower(p => p.ShortCol1 > p.ShortCol2);
        Column untyped = Functions.Col("ShortCol1").Gt(Functions.Col("ShortCol2"));

        Assert.Equal(untyped.Expr, typed.Expr);
    }

    [Fact]
    public void EnumComparison_LowersToSamePlanAsUntyped_NoFork()
    {
        // `p.EnumCol == Color.Green` compiles to Equal(Convert(EnumCol,int), Convert(Green,int)). The
        // left Convert (enum→int on a column) UNWRAPS to `Col("EnumCol")`; the right folds to `Lit(1)`.
        // The typed plan is therefore the SAME `(EnumCol = 1)` as the untyped API — NOT the forked
        // `(cast(EnumCol as int) = 1)`.
        Expression<Func<EnumRec, bool>> predicate = p => p.EnumCol == Color.Green;
        Column typed = TypedExpressionLowering.Lower(predicate);
        Column untyped = Functions.Col("EnumCol").EqualTo(Functions.Lit(1));

        Assert.Equal(untyped.Expr, typed.Expr);
        // Reddens if a Cast(EnumCol AS int) is reintroduced on the column operand:
        Assert.NotEqual(
            new Column(new DeltaSharp.Plans.Expressions.Cast(Functions.Col("EnumCol").Expr, IntegerType.Instance))
                .EqualTo(Functions.Lit(1)).Expr,
            typed.Expr);
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

    // ----- Finding 2: `checked(...)` constant folding honors overflow protection -----

    [Fact]
    public void CheckedOverflowingConstantFold_ThrowsDeterministicDiagnostic()
    {
        int max = int.MaxValue;

        // `checked(max + 1)` is a parameter-independent AddChecked subtree the user explicitly asked to
        // overflow-guard. Folding it must THROW rather than silently wrap to int.MinValue
        // (-2147483648) and emit `('Age' > -2147483648)` — a silent wrong plan.
        var ex = Assert.Throws<UnsupportedTypedExpressionException>(
            () => Lower(p => p.Age > checked(max + 1)));

        Assert.Contains("overflows at translation time", ex.Message);
        Assert.IsType<OverflowException>(ex.InnerException);
    }

    [Fact]
    public void CheckedOverflowingConstantFold_DoesNotSilentlyWrap()
    {
        int max = int.MaxValue;

        // Mutation-sensitive: if the fold used plain (unchecked) `a + b` it would return the wrapped
        // literal int.MinValue and lowering would SUCCEED with `('Age' > -2147483648)`. Prove it never
        // does by confirming lowering throws and produces no column.
        Column? lowered = null;
        Assert.Throws<UnsupportedTypedExpressionException>(
            () => lowered = Lower(p => p.Age > checked(max + 1)));
        Assert.Null(lowered);

        // The wrapped constant that a silent mis-fold would have produced:
        Assert.NotEqual(
            Functions.Col("Age").Gt(Functions.Lit(unchecked(int.MaxValue + 1))).Expr,
            (lowered ?? Functions.Col("Age")).Expr);
    }

    [Fact]
    public void CheckedNonOverflowingConstantFold_StillFoldsToConstant()
    {
        int one = 1;

        // A non-overflowing `checked(one + 1)` must still fold to Lit(2) — the checked guard only
        // rejects genuine overflow, not all checked arithmetic.
        Column lowered = Lower(p => p.Age > checked(one + 1));

        Assert.Equal(Functions.Col("Age").Gt(Functions.Lit(2)).Expr, lowered.Expr);
    }

    // ----- Finding A: `checked`/`unchecked` on COLUMN operands is rejected (no per-expression Spark
    // mapping), never silently lowered to a plain (unchecked) Plus/Cast -----

    [Fact]
    public void CheckedColumnArithmetic_ThrowsDeterministicDiagnostic()
    {
        // `checked(p.Age + p.Other)` is an AddChecked node over COLUMN operands. C# `checked`/`unchecked`
        // has no faithful per-expression Spark mapping (overflow is session-config governed), so it must
        // be rejected — NOT silently lowered to a plain (unchecked) Plus.
        var ex = Assert.Throws<UnsupportedTypedExpressionException>(
            () => Lower(p => checked(p.Age + p.Other) > 0));

        Assert.Contains("not honored per-expression on column operands", ex.Message);
        Assert.Contains("ANSI", ex.Message);
    }

    [Fact]
    public void CheckedColumnArithmetic_DoesNotSilentlyLowerToPlus()
    {
        // Mutation-sensitive: if the AddChecked arm fell through to `left.Plus(right)` it would silently
        // emit `(('Age' + 'Other') > 0)` — dropping the explicit `checked` intent. Prove lowering throws
        // and produces no column, and that the dropped-guard tree is never the result.
        Column? lowered = null;
        Assert.Throws<UnsupportedTypedExpressionException>(
            () => lowered = Lower(p => checked(p.Age + p.Other) > 0));
        Assert.Null(lowered);

        Assert.NotEqual(
            Functions.Col("Age").Plus(Functions.Col("Other")).Gt(Functions.Lit(0)).Expr,
            (lowered ?? Functions.Col("Age")).Expr);
    }

    [Fact]
    public void CheckedColumnConvert_ThrowsDeterministicDiagnostic()
    {
        // `checked((int)p.Id)` is a ConvertChecked (long→int) over a COLUMN operand. A plain
        // (unchecked) value-changing convert now UNWRAPS to the bare column, but a *checked* one asks
        // for a per-expression overflow guard that has no faithful Spark mapping, so it must be
        // rejected — NOT silently unwrapped (nor lowered to an unchecked Cast).
        var ex = Assert.Throws<UnsupportedTypedExpressionException>(
            () => LowerSelect(p => checked((int)p.Id)));

        Assert.Contains("not honored per-expression on column operands", ex.Message);
        Assert.Contains("ANSI", ex.Message);
    }

    [Fact]
    public void CheckedColumnConvert_DoesNotSilentlyLowerToCast()
    {
        // Mutation-sensitive: if the ConvertChecked reached a value-changing Cast/unwrap branch it would
        // silently emit `Cast('Id' AS int)` (or the bare column) — dropping the checked intent. Prove
        // lowering throws and the dropped-guard Cast tree is never the result.
        Column? lowered = null;
        Assert.Throws<UnsupportedTypedExpressionException>(
            () => lowered = LowerSelect(p => checked((int)p.Id)));
        Assert.Null(lowered);

        Column droppedGuardCast = new(new DeltaSharp.Plans.Expressions.Cast(
            Functions.Col("Id").Expr, IntegerType.Instance));
        Assert.NotEqual(droppedGuardCast.Expr, (lowered ?? Functions.Col("Id")).Expr);
    }

    [Fact]
    public void NormalColumnArithmetic_StillLowersToPlus_NoRegression()
    {
        // The checked rejection must not regress plain (non-checked) column arithmetic: `p.Age + p.Other`
        // still lowers to the arithmetic Plus over two column references.
        Column lowered = LowerSelect(p => p.Age + p.Other);

        Assert.Equal(Functions.Col("Age").Plus(Functions.Col("Other")).Expr, lowered.Expr);
    }

    // ----- Finding B: integer `/` lowers to Spark FRACTIONAL division (DOUBLE), identical to the
    // untyped `Functions`/`Column` API — no fork. Pinned as intentional (Spark SQL semantics). -----

    [Fact]
    public void IntegerDivision_LowersToSamePlanAsUntyped()
    {
        // `p => p.Age / p.Other` (C# int / int) lowers to the SAME `Divide` IR as the untyped
        // `Functions.Col("Age") / Functions.Col("Other")`. Per Spark SQL this is fractional division
        // returning DOUBLE (5/2 → 2.5), NOT C# integer truncation (2). This is Spark-faithful, shared
        // with the untyped API (no fork), and pinned here as intentional. Result-type materialization
        // (double vs int) is a #178 encoder concern; M1 only lowers to expressions.
        Column typed = LowerSelect(p => p.Age / p.Other);
        Column untyped = Functions.Col("Age") / Functions.Col("Other");

        Assert.Equal(untyped.Expr, typed.Expr);
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

    // ----- Finding 1: string concatenation `+` is rejected, never mis-lowered to numeric Plus -----

    [Fact]
    public void StringConcatenation_ThrowsDeterministicDiagnostic()
    {
        // C# compiles `p.Name + "b"` to an ExpressionType.Add node (Method == string.Concat, result
        // type string). It must NOT lower to a numeric Plus (which would execute as
        // CAST(Name AS Double) + CAST("b" AS Double)) — reject it deterministically instead.
        var ex = Assert.Throws<UnsupportedTypedExpressionException>(
            () => LowerSelect(p => p.Name + "b"));

        Assert.Contains("String concatenation is not supported", ex.Message);
    }

    [Fact]
    public void StringConcatenation_DoesNotLowerToNumericPlus()
    {
        // Mutation-sensitive: if the numeric guard were removed the Add arm would silently produce a
        // `Plus(Col("Name"), Lit("b"))` tree. Prove it never does by confirming lowering throws rather
        // than returning that (or any) column.
        Column? lowered = null;
        Assert.Throws<UnsupportedTypedExpressionException>(
            () => lowered = LowerSelect(p => p.Name + "b"));
        Assert.Null(lowered);
    }

    [Fact]
    public void NumericAddition_StillLowersToArithmeticPlus_NoRegression()
    {
        // The numeric guard must not regress genuine numeric arithmetic: `p.Age + 1` still lowers to
        // the arithmetic Plus op.
        Column lowered = LowerSelect(p => p.Age + 1);

        Assert.Equal(Functions.Col("Age").Plus(Functions.Lit(1)).Expr, lowered.Expr);
    }

    // ----- Item 10: boolean operators lower structurally (mutation-sensitive) -----

    [Fact]
    public void LogicalNot_LowersStructurally()
    {
        Column lowered = Lower(p => !(p.Age > 18));

        Assert.Equal(Functions.Col("Age").Gt(Functions.Lit(18)).Not().Expr, lowered.Expr);
    }

    [Fact]
    public void BitwiseComplement_OnNumericColumn_ThrowsDeterministicDiagnostic()
    {
        // C# '~p.Age' is a BITWISE complement over an integer column. It shares ExpressionType.Not
        // with logical '!' (the compiler emits a 'Not' node whose Type is Int32, not Boolean), but
        // has no faithful Spark mapping. It must be REJECTED, never silently lowered to a boolean
        // Spark Not over a numeric column. Mutation sentinel: dropping the operand-type guard in
        // LowerNot lowers this to `NOT 'Age` (a silent wrong plan) instead of throwing.
        var ex = Assert.Throws<UnsupportedTypedExpressionException>(() => LowerSelect(p => ~p.Age));

        Assert.Contains("bitwise", ex.Message, StringComparison.OrdinalIgnoreCase);
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
