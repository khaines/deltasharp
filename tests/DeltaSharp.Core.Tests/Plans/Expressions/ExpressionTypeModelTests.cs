using System;
using System.Collections.Generic;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// STORY-04.4.2 (#168) <b>AC3</b>: literals and casts represent types using the ADR-0008 / ADR-0016
/// shared type-system model (the <see cref="DataType"/> hierarchy from DeltaSharp.Abstractions), in
/// the natural CLR storage shape, and equality is type-aware.
/// </summary>
public class ExpressionTypeModelTests
{
    [Fact]
    public void Literals_RecordSharedTypeAndStorageShape()
    {
        AssertLiteral(Literal.OfBoolean(true), BooleanType.Instance, true);
        AssertLiteral(Literal.OfByte(1), ByteType.Instance, (sbyte)1);
        AssertLiteral(Literal.OfShort(2), ShortType.Instance, (short)2);
        AssertLiteral(Literal.OfInt(3), IntegerType.Instance, 3);
        AssertLiteral(Literal.OfLong(4), LongType.Instance, 4L);
        AssertLiteral(Literal.OfFloat(1.5f), FloatType.Instance, 1.5f);
        AssertLiteral(Literal.OfDouble(2.5d), DoubleType.Instance, 2.5d);
        AssertLiteral(Literal.OfString("hi"), StringType.Instance, "hi");
        AssertLiteral(Literal.OfDate(19000), DateType.Instance, 19000);          // epoch-day (int)
        AssertLiteral(Literal.OfTimestamp(1_700_000_000_000_000L), TimestampType.Instance, 1_700_000_000_000_000L); // epoch-micros (long)
        AssertLiteral(Literal.OfTimestampNtz(1_700_000_000_000_000L), TimestampNtzType.Instance, 1_700_000_000_000_000L); // wall-clock epoch-micros (long)
    }

    [Fact]
    public void DecimalLiteral_RecordsDecimalTypeAndUnscaledInt128()
    {
        var type = new DecimalType(10, 2);

        var literal = Literal.OfDecimal((Int128)12345, type);

        Assert.Same(type, literal.Type);
        Assert.Equal((Int128)12345, Assert.IsType<Int128>(literal.Value));
        Assert.False(literal.IsNull);
    }

    [Fact]
    public void BinaryLiteral_CopiesBytesDefensively()
    {
        byte[] source = [1, 2, 3];

        var literal = Literal.OfBinary(source);
        source[0] = 99;

        Assert.Equal(BinaryType.Instance, literal.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(literal.Value));
    }

    [Fact]
    public void NullLiteral_CarriesTypedNullOfSharedType()
    {
        var type = new DecimalType(20, 4);

        var literal = Literal.Null(type);

        Assert.Same(type, literal.Type);
        Assert.True(literal.IsNull);
        Assert.Null(literal.Value);
        Assert.True(literal.Nullable);
    }

    [Fact]
    public void Cast_TargetTypeIsSharedTypeAndIsTheResultType()
    {
        var target = new DecimalType(38, 10);
        var cast = new Cast(new UnresolvedAttribute("x"), target);

        Assert.Same(target, cast.TargetType);
        Assert.Same(target, cast.Type);
    }

    [Fact]
    public void Comparison_IsAlwaysBooleanTyped()
    {
        var comparison = new BinaryComparison(Literal.OfInt(1), Literal.OfInt(2), ComparisonOperator.LessThan);

        Assert.Equal(BooleanType.Instance, comparison.Type);
    }

    [Fact]
    public void Arithmetic_ResultTypeDerivesFromResolvedOperands()
    {
        // Once both operands are typed, the result type is a function of the children (Spark parity:
        // Add.dataType). int + long widens to bigint under ADR-0008 numeric promotion.
        var arithmetic = new BinaryArithmetic(Literal.OfInt(1), Literal.OfLong(2), ArithmeticOperator.Add);

        Assert.Equal(LongType.Instance, arithmetic.Type);
    }

    [Fact]
    public void Arithmetic_TypeIsNullWhileOperandUnresolved()
    {
        // With an unresolved operand the result type cannot be derived and stays null until analysis.
        var arithmetic = new BinaryArithmetic(
            new UnresolvedAttribute("x"), Literal.OfInt(2), ArithmeticOperator.Add);

        Assert.Null(arithmetic.Type);
    }

    [Fact]
    public void NullableUnder_Arithmetic_WidensToNullableUnderLegacyOnly()
    {
        // #614: `1 + 2` over NOT-NULL literal operands is NOT-NULL under Ansi (overflow throws), but
        // widens to nullable under Legacy (overflow nulls). NullableUnder(Ansi) must equal Nullable.
        var arithmetic = new BinaryArithmetic(Literal.OfInt(1), Literal.OfInt(2), ArithmeticOperator.Add);

        Assert.False(arithmetic.Nullable);
        Assert.False(arithmetic.NullableUnder(AnsiMode.Ansi));
        Assert.True(arithmetic.NullableUnder(AnsiMode.Legacy));
    }

    [Fact]
    public void NullableUnder_NonIdentityCast_WidensToNullableUnderLegacyOnly()
    {
        // #614: a non-identity (lossy-capable) cast of a NOT-NULL operand widens to nullable under
        // Legacy (invalid cast nulls) but follows the child under Ansi.
        var cast = new Cast(Literal.OfInt(1), LongType.Instance);

        Assert.False(cast.Nullable);
        Assert.False(cast.NullableUnder(AnsiMode.Ansi));
        Assert.True(cast.NullableUnder(AnsiMode.Legacy));
    }

    [Fact]
    public void NullableUnder_IdentityCast_FollowsChildInBothModes()
    {
        // An identity cast introduces no null even under Legacy, so it follows the (NOT-NULL) child.
        var cast = new Cast(Literal.OfInt(1), IntegerType.Instance);

        Assert.False(cast.NullableUnder(AnsiMode.Ansi));
        Assert.False(cast.NullableUnder(AnsiMode.Legacy));
    }

    // An overflow-capable value over NOT-NULL literal operands: Nullable is false, so it isolates the
    // Legacy widening (NullableUnder(Legacy) == true, NullableUnder(Ansi) == false).
    private static BinaryArithmetic OverflowValue() =>
        new(Literal.OfInt(1), Literal.OfInt(2), ArithmeticOperator.Add);

    [Fact]
    public void NullableUnder_And_PropagatesOverflowChildUnderLegacyOnly()
    {
        var and = new And(OverflowValue(), OverflowValue());

        Assert.False(and.Nullable);
        Assert.False(and.NullableUnder(AnsiMode.Ansi));
        Assert.True(and.NullableUnder(AnsiMode.Legacy));
    }

    [Fact]
    public void NullableUnder_Or_PropagatesOverflowChildUnderLegacyOnly()
    {
        var or = new Or(OverflowValue(), OverflowValue());

        Assert.False(or.Nullable);
        Assert.False(or.NullableUnder(AnsiMode.Ansi));
        Assert.True(or.NullableUnder(AnsiMode.Legacy));
    }

    [Fact]
    public void NullableUnder_Not_PropagatesOverflowChildUnderLegacyOnly()
    {
        var not = new Not(OverflowValue());

        Assert.False(not.Nullable);
        Assert.False(not.NullableUnder(AnsiMode.Ansi));
        Assert.True(not.NullableUnder(AnsiMode.Legacy));
    }

    [Fact]
    public void NullableUnder_Comparison_PropagatesOverflowOperandUnderLegacyOnly()
    {
        var comparison = new BinaryComparison(OverflowValue(), Literal.OfInt(0), ComparisonOperator.LessThan);

        Assert.False(comparison.Nullable);
        Assert.False(comparison.NullableUnder(AnsiMode.Ansi));
        Assert.True(comparison.NullableUnder(AnsiMode.Legacy));
    }

    [Fact]
    public void NullableUnder_CaseWhen_PropagatesOverflowBranchValueUnderLegacyOnly()
    {
        // A branch VALUE of `1 + 2` with a NOT-NULL else: NOT-NULL under Ansi, nullable under Legacy.
        var caseWhen = new CaseWhen(Literal.OfBoolean(true), OverflowValue()).WithElse(Literal.OfInt(0));

        Assert.False(caseWhen.Nullable);
        Assert.False(caseWhen.NullableUnder(AnsiMode.Ansi));
        Assert.True(caseWhen.NullableUnder(AnsiMode.Legacy));
    }

    [Fact]
    public void NullableUnder_PropagatesAnyFunction_WidensOverOverflowArgUnderLegacyOnly()
    {
        // #627: an OR-propagating (PropagatesAny) function wrapping an overflow-capable `1 + 2` is
        // NOT-NULL under Ansi (its stored `_nullable` == args.Any(Nullable) == false), but nullable
        // under Legacy because the argument can null on overflow. This is the abs(v+v)/sqrt(v*v) case.
        var wrapped = new ResolvedFunction(
            "length", FunctionKind.Scalar, IntegerType.Instance, nullable: false, new[] { OverflowValue() },
            nullPropagation: FunctionNullability.PropagatesAny);

        Assert.False(wrapped.Nullable);
        Assert.False(wrapped.NullableUnder(AnsiMode.Ansi));
        Assert.True(wrapped.NullableUnder(AnsiMode.Legacy));
    }

    [Fact]
    public void NullableUnder_PropagatesAnyFunction_MultiArg_WidensIfAnyArgOverflowsUnderLegacy()
    {
        // A second OR-propagating shape (multi-arg concat-like): a NOT-NULL literal plus an
        // overflow-capable arg is NOT-NULL under Ansi but nullable under Legacy (ANY arg nulls).
        var wrapped = new ResolvedFunction(
            "concat", FunctionKind.Scalar, StringType.Instance, nullable: false,
            new Expression[] { Literal.OfInt(7), OverflowValue() },
            nullPropagation: FunctionNullability.PropagatesAny);

        Assert.False(wrapped.NullableUnder(AnsiMode.Ansi));
        Assert.True(wrapped.NullableUnder(AnsiMode.Legacy));
    }

    [Fact]
    public void NullableUnder_PropagatesAllFunction_NotWidenedByOneOverflowArg()
    {
        // #627: coalesce(v+v, 0) is PropagatesAll — null only if ALL args null. The NOT-NULL `0` pins
        // the result NOT-NULL, so even under Legacy the overflow-capable first arg does NOT widen it.
        var coalesce = new ResolvedFunction(
            "coalesce", FunctionKind.Scalar, IntegerType.Instance, nullable: false,
            new Expression[] { OverflowValue(), Literal.OfInt(0) },
            nullPropagation: FunctionNullability.PropagatesAll);

        Assert.False(coalesce.Nullable);
        Assert.False(coalesce.NullableUnder(AnsiMode.Ansi));
        Assert.False(coalesce.NullableUnder(AnsiMode.Legacy));
    }

    [Fact]
    public void NullableUnder_PropagatesAllFunction_WidensWhenEveryArgOverflowsUnderLegacy()
    {
        // coalesce(v+v, w+w): every arg is overflow-capable, so under Legacy ALL can null → nullable.
        // Under Ansi both throw instead of nulling, so the result stays NOT-NULL (byte-identical).
        var coalesce = new ResolvedFunction(
            "coalesce", FunctionKind.Scalar, IntegerType.Instance, nullable: false,
            new Expression[] { OverflowValue(), OverflowValue() },
            nullPropagation: FunctionNullability.PropagatesAll);

        Assert.False(coalesce.NullableUnder(AnsiMode.Ansi));
        Assert.True(coalesce.NullableUnder(AnsiMode.Legacy));
    }

    [Fact]
    public void NullableUnder_FixedFunction_NeverWidenedUnderLegacy()
    {
        // #627: a Fixed function (e.g. count — never null) keeps its exact stored nullability in BOTH
        // modes; an overflow-capable argument must NOT widen it. Mirrors aggregates and to_date.
        var count = new ResolvedFunction(
            "count", FunctionKind.Aggregate, LongType.Instance, nullable: false, new[] { OverflowValue() },
            nullPropagation: FunctionNullability.Fixed);

        Assert.False(count.NullableUnder(AnsiMode.Ansi));
        Assert.False(count.NullableUnder(AnsiMode.Legacy));

        // A Fixed function whose stored nullability is `true` (e.g. to_date) stays nullable, mode-independent.
        var toDate = new ResolvedFunction(
            "to_date", FunctionKind.Scalar, DateType.Instance, nullable: true, new[] { OverflowValue() },
            nullPropagation: FunctionNullability.Fixed);

        Assert.True(toDate.NullableUnder(AnsiMode.Ansi));
        Assert.True(toDate.NullableUnder(AnsiMode.Legacy));
    }

    [Fact]
    public void NullableUnder_UnderAnsi_EqualsNullable_ForRepresentativeNodes()
    {
        // #614 invariant: under Ansi, NullableUnder must be byte-identical to Nullable for every
        // propagating node — the Legacy widening is the ONLY behavioral change.
        var structAttr = new AttributeReference(
            "s",
            new StructType(new[] { new StructField("f", IntegerType.Instance, nullable: false) }),
            nullable: false,
            new ExprId(1));
        BinaryArithmetic overflow = OverflowValue();
        Expression[] representatives =
        {
            overflow,
            new Cast(Literal.OfInt(1), LongType.Instance),
            new Alias(overflow, "a"),
            new And(overflow, overflow),
            new Or(overflow, overflow),
            new Not(overflow),
            new BinaryComparison(overflow, Literal.OfInt(0), ComparisonOperator.LessThan),
            new GetStructField(structAttr, 0, "f"),
            new CaseWhen(Literal.OfBoolean(true), overflow).WithElse(Literal.OfInt(0)),

            // #627 ResolvedFunction across all three FunctionNullability classifications, each over an
            // overflow-capable arg — the Ansi lens must reproduce the stored Nullable in every case.
            new ResolvedFunction(
                "length", FunctionKind.Scalar, IntegerType.Instance, nullable: false, new[] { overflow },
                nullPropagation: FunctionNullability.PropagatesAny),
            new ResolvedFunction(
                "coalesce", FunctionKind.Scalar, IntegerType.Instance, nullable: false,
                new Expression[] { overflow, Literal.OfInt(0) },
                nullPropagation: FunctionNullability.PropagatesAll),
            new ResolvedFunction(
                "sum", FunctionKind.Aggregate, LongType.Instance, nullable: true, new[] { overflow },
                nullPropagation: FunctionNullability.Fixed),
        };

        foreach (Expression node in representatives)
        {
            Assert.Equal(node.Nullable, node.NullableUnder(AnsiMode.Ansi));
        }
    }

    [Fact]
    public void EveryNullablePropagatingNode_AlsoOverridesNullableUnder()
    {
        // #614 reflection guard: any concrete Expression that OVERRIDES Nullable to propagate a child's
        // nullability MUST also override NullableUnder, or a Legacy overflow/lossy-cast output column
        // silently under-reports NOT-NULL (the exact #614 bug). The allowlist is the set of nodes whose
        // Nullable is a constant/stored/non-propagating value, so a mode-aware variant would be a no-op.
        // ResolvedFunction is NO LONGER exempt (#627): it now overrides NullableUnder to recompute
        // nullability mode-awarely from its FunctionNullability classification.
        var exempt = new HashSet<string>(StringComparer.Ordinal)
        {
            "Literal",           // constant nullability (IsNull)
            "AttributeReference", // stored leaf nullability
            "IsNull",            // result is never null (non-propagating)
            "IsNotNull",         // result is never null (non-propagating)
            "EqualNullSafe",     // result is never null (non-propagating)
        };

        Type baseType = typeof(Expression);
        var offenders = new List<string>();
        foreach (Type type in baseType.Assembly.GetTypes())
        {
            if (type.IsAbstract || type == baseType || !baseType.IsAssignableFrom(type))
            {
                continue;
            }

            bool overridesNullable = type.GetProperty("Nullable")!.GetGetMethod()!.DeclaringType == type;
            if (!overridesNullable || exempt.Contains(type.Name))
            {
                continue;
            }

            bool overridesNullableUnder =
                type.GetMethod("NullableUnder", new[] { typeof(AnsiMode) })!.DeclaringType == type;
            if (!overridesNullableUnder)
            {
                offenders.Add(type.Name);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These Expression nodes override Nullable but not NullableUnder (add a mode-aware override "
            + "or exempt them with rationale): " + string.Join(", ", offenders));
    }

    [Fact]
    public void UnresolvedMarkers_HaveNoKnownType()
    {
        Assert.Null(new UnresolvedAttribute("x").Type);
        Assert.Null(new UnresolvedStar().Type);
        Assert.Null(new UnresolvedFunction("f", []).Type);
    }

    [Fact]
    public void Literal_StructuralEquality_IsTypeAware()
    {
        Assert.Equal(Literal.OfInt(5), Literal.OfInt(5));
        Assert.NotEqual<Expression>(Literal.OfInt(5), Literal.OfLong(5));     // same value, different type
        Assert.NotEqual<Expression>(Literal.OfInt(5), Literal.OfInt(6));
        Assert.Equal(Literal.OfBinary([1, 2]), Literal.OfBinary([1, 2]));     // value equality over bytes
        Assert.Equal(Literal.Null(IntegerType.Instance), Literal.Null(IntegerType.Instance));
        Assert.NotEqual<Expression>(Literal.Null(IntegerType.Instance), Literal.Null(LongType.Instance));
    }

    private static void AssertLiteral(Literal literal, DataType expectedType, object expectedValue)
    {
        Assert.Same(expectedType, literal.Type);
        Assert.False(literal.IsNull);
        Assert.Equal(expectedValue, literal.Value);
    }
}
