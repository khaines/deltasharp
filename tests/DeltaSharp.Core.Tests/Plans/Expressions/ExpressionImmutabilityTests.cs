using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// STORY-04.4.2 (#168) <b>AC2</b>: nested expressions are immutable — an analyzer-style rule that
/// rewrites a child returns a <b>new</b> parent tree without mutating the original, sharing
/// untouched subtrees by reference (structural sharing) via the merged
/// <see cref="TreeNode{TNode}"/> transforms (#167).
/// </summary>
public class ExpressionImmutabilityTests
{
    // A resolution rule: replace UnresolvedAttribute("x") with a resolved AttributeReference.
    private static Expression ResolveX(Expression node)
        => node is UnresolvedAttribute { Name: "x" }
            ? new AttributeReference("x", IntegerType.Instance, nullable: false, new ExprId(42))
            : node;

    [Fact]
    public void TransformUp_RewritingChild_ReturnsNewParent_OriginalUnchanged()
    {
        var x = new UnresolvedAttribute("x");
        var y = new UnresolvedAttribute("y");
        var original = new BinaryArithmetic(x, y, ArithmeticOperator.Add);
        string originalRender = original.SimpleString;

        var rewritten = (BinaryArithmetic)original.TransformUp(ResolveX);

        // A new parent is returned.
        Assert.NotSame(original, rewritten);
        // The original is untouched: same instances, same render.
        Assert.Same(x, original.Left);
        Assert.Same(y, original.Right);
        Assert.Equal(originalRender, original.SimpleString);
        Assert.False(original.Resolved);

        // The rewrite applied to the targeted child only.
        Assert.IsType<AttributeReference>(rewritten.Left);
        Assert.Equal("x", ((AttributeReference)rewritten.Left).Name);
    }

    [Fact]
    public void TransformUp_SharesUntouchedSubtreesByReference()
    {
        var x = new UnresolvedAttribute("x");
        // An untouched sibling subtree that the rule never rewrites.
        var sibling = new BinaryComparison(new UnresolvedAttribute("y"), Literal.OfInt(1), ComparisonOperator.GreaterThan);
        var root = new And(new IsNotNull(x), sibling);

        var rewritten = (And)root.TransformUp(ResolveX);

        Assert.NotSame(root, rewritten);
        // The untouched sibling subtree is the SAME instance (structural sharing).
        Assert.Same(sibling, rewritten.Right);
        // The rewritten branch is new.
        Assert.NotSame(root.Left, rewritten.Left);
    }

    [Fact]
    public void TransformDown_RewritesAndPreservesOriginal()
    {
        var x = new UnresolvedAttribute("x");
        var original = new Alias(new Cast(x, LongType.Instance), "xl");

        var rewritten = (Alias)original.TransformDown(ResolveX);

        Assert.NotSame(original, rewritten);
        Assert.IsType<UnresolvedAttribute>(((Cast)original.Child).Child);
        Assert.IsType<AttributeReference>(((Cast)rewritten.Child).Child);
        Assert.Equal("xl", rewritten.Name);
    }

    [Fact]
    public void WithNewChildren_NoOp_ReturnsSameInstance()
    {
        var left = new UnresolvedAttribute("a");
        var right = new UnresolvedAttribute("b");
        var node = new BinaryArithmetic(left, right, ArithmeticOperator.Subtract);

        Expression same = node.WithNewChildren([left, right]);

        Assert.Same(node, same);
    }

    [Fact]
    public void WithNewChildren_ChangedChild_ReturnsNewInstance()
    {
        var left = new UnresolvedAttribute("a");
        var right = new UnresolvedAttribute("b");
        var node = new BinaryArithmetic(left, right, ArithmeticOperator.Subtract);
        var newRight = Literal.OfInt(3);

        var replaced = (BinaryArithmetic)node.WithNewChildren([left, newRight]);

        Assert.NotSame(node, replaced);
        Assert.Same(left, replaced.Left);
        Assert.Same(newRight, replaced.Right);
        Assert.Equal(ArithmeticOperator.Subtract, replaced.Operator);
    }

    [Fact]
    public void WithNewChildren_WrongArity_Throws()
    {
        var node = new IsNull(new UnresolvedAttribute("a"));

        Assert.Throws<ArgumentException>(() => node.WithNewChildren([]));
    }

    [Fact]
    public void TransformUp_NoMatch_ReturnsSameRootInstance()
    {
        var node = new BinaryArithmetic(new UnresolvedAttribute("p"), new UnresolvedAttribute("q"), ArithmeticOperator.Add);

        Expression unchanged = node.TransformUp(n => n);

        Assert.Same(node, unchanged);
    }

    [Fact]
    public void StructuralEquality_IsValueBasedAndHashStable()
    {
        var a = new BinaryArithmetic(new UnresolvedAttribute("x"), Literal.OfInt(1), ArithmeticOperator.Add);
        var b = new BinaryArithmetic(new UnresolvedAttribute("x"), Literal.OfInt(1), ArithmeticOperator.Add);
        var different = new BinaryArithmetic(new UnresolvedAttribute("x"), Literal.OfInt(2), ArithmeticOperator.Add);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, different);
    }

    [Fact]
    public void BinaryLiteral_IsImmutable_AgainstInputAndReturnedArrayMutation()
    {
        var input = new byte[] { 1, 2, 3 };
        var literal = Literal.OfBinary(input);

        string renderBefore = literal.SimpleString;
        int hashBefore = literal.GetHashCode();
        var equalBefore = Literal.OfBinary(new byte[] { 1, 2, 3 });
        Assert.Equal("0x010203", renderBefore);

        // Mutate the ORIGINAL input array after construction (defensive copy on the way in).
        input[0] = 0x63;

        // Mutate the array handed back from the Value getter (defensive copy on the way out).
        var returned = Assert.IsType<byte[]>(literal.Value);
        returned[0] = 0x63;

        // Neither mutation is visible: render, equality, and hash are unchanged.
        Assert.Equal("0x010203", literal.SimpleString);
        Assert.Equal(renderBefore, literal.SimpleString);
        Assert.Equal(hashBefore, literal.GetHashCode());
        Assert.Equal(equalBefore, literal);
        Assert.Equal(equalBefore.GetHashCode(), literal.GetHashCode());
    }
}
