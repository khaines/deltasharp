using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// STORY-04.4.2 (#168): the constructor/factory argument guards reject invalid input up front so an
/// expression node can never be constructed in an inconsistent state. Each case asserts the
/// documented exception type (<see cref="ArgumentNullException"/> for null,
/// <see cref="ArgumentException"/> for empty/invalid).
/// </summary>
public class ExpressionGuardTests
{
    private static Expression Child => new UnresolvedAttribute("x");

    [Fact]
    public void Cast_NullTargetType_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => new Cast(Child, null!));

    [Fact]
    public void Cast_NullChild_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => new Cast(null!, IntegerType.Instance));

    [Fact]
    public void Alias_EmptyName_ThrowsArgument()
        => Assert.Throws<ArgumentException>(() => new Alias(Child, string.Empty));

    [Fact]
    public void Alias_NullName_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => new Alias(Child, null!));

    [Fact]
    public void Alias_NullChild_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => new Alias(null!, "a"));

    [Fact]
    public void Literal_Null_NullType_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => Literal.Null(null!));

    [Fact]
    public void Literal_OfString_Null_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => Literal.OfString(null!));

    [Fact]
    public void Literal_OfDecimal_NullType_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => Literal.OfDecimal(Int128.One, null!));

    [Fact]
    public void AttributeReference_NullName_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(
            () => new AttributeReference(null!, IntegerType.Instance, nullable: false, new ExprId(1)));

    [Fact]
    public void AttributeReference_EmptyName_ThrowsArgument()
        => Assert.Throws<ArgumentException>(
            () => new AttributeReference(string.Empty, IntegerType.Instance, nullable: false, new ExprId(1)));

    [Fact]
    public void AttributeReference_NullType_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(
            () => new AttributeReference("a", null!, nullable: false, new ExprId(1)));

    [Fact]
    public void UnresolvedFunction_NullName_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => new UnresolvedFunction(null!, [Child]));

    [Fact]
    public void UnresolvedFunction_NullArguments_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => new UnresolvedFunction("f", null!));

    [Fact]
    public void UnresolvedFunction_NullArgumentElement_ThrowsArgument()
        => Assert.Throws<ArgumentException>(() => new UnresolvedFunction("f", [Child, null!]));

    [Fact]
    public void And_NullOperand_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => new And(Child, null!));

    [Fact]
    public void BinaryComparison_NullOperand_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(
            () => new BinaryComparison(null!, Child, ComparisonOperator.Equal));

    [Fact]
    public void Not_NullChild_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => new Not(null!));
}
