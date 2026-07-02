using DeltaSharp.Core.Tests.LazyEager;
using DeltaSharp.Diagnostics;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests;

/// <summary>
/// STORY-04.3.2 (#165): the public <see cref="Column"/> operator surface — arithmetic, comparison,
/// boolean, and null predicates. Each operator records the right internal expression node and
/// operands with <b>no</b> evaluation, <b>no</b> schema lookup, and (AC4) <b>no</b> type coercion:
/// the analyzer, not the API, reports operator misuse under ADR-0008's three-valued logic.
/// </summary>
public class ColumnOperatorTests
{
    private static Column Col(string name) => Functions.Col(name);

    private static Expression ExprOf(Column column) => column.Expr;

    // ----- AC1: arithmetic records the operator kind + operands without evaluation ---------------

    [Theory] // AC1
    [InlineData(nameof(Column.Plus), "Add")]
    [InlineData(nameof(Column.Minus), "Subtract")]
    [InlineData(nameof(Column.Multiply), "Multiply")]
    [InlineData(nameof(Column.Divide), "Divide")]
    [InlineData(nameof(Column.Mod), "Remainder")]
    public void ArithmeticMethod_BuildsBinaryArithmeticWithOperatorAndOperands(
        string method, string expected)
    {
        Column left = Col("a");
        Column right = Col("b");

        Column result = method switch
        {
            nameof(Column.Plus) => left.Plus(right),
            nameof(Column.Minus) => left.Minus(right),
            nameof(Column.Multiply) => left.Multiply(right),
            nameof(Column.Divide) => left.Divide(right),
            _ => left.Mod(right),
        };

        var node = Assert.IsType<BinaryArithmetic>(ExprOf(result));
        Assert.Equal(expected, node.Operator.ToString());
        Assert.Same(ExprOf(left), node.Left);
        Assert.Same(ExprOf(right), node.Right);
        Assert.Null(node.Type); // arithmetic result type is unknown until the analyzer coerces it
    }

    // ----- AC1: comparison records the operator kind, boolean-typed, without evaluation ----------

    [Theory] // AC1
    [InlineData(nameof(Column.EqualTo), "Equal")]
    [InlineData(nameof(Column.NotEqual), "NotEqual")]
    [InlineData(nameof(Column.Lt), "LessThan")]
    [InlineData(nameof(Column.Leq), "LessThanOrEqual")]
    [InlineData(nameof(Column.Gt), "GreaterThan")]
    [InlineData(nameof(Column.Geq), "GreaterThanOrEqual")]
    public void ComparisonMethod_BuildsBinaryComparisonWithOperatorAndBooleanType(
        string method, string expected)
    {
        Column left = Col("a");
        Column right = Col("b");

        Column result = method switch
        {
            nameof(Column.EqualTo) => left.EqualTo(right),
            nameof(Column.NotEqual) => left.NotEqual(right),
            nameof(Column.Lt) => left.Lt(right),
            nameof(Column.Leq) => left.Leq(right),
            nameof(Column.Gt) => left.Gt(right),
            _ => left.Geq(right),
        };

        var node = Assert.IsType<BinaryComparison>(ExprOf(result));
        Assert.Equal(expected, node.Operator.ToString());
        Assert.Same(ExprOf(left), node.Left);
        Assert.Same(ExprOf(right), node.Right);
        Assert.IsType<BooleanType>(node.Type); // comparison is boolean even before analysis
    }

    // ----- AC2: boolean And/Or/Not build the 3VL nodes -------------------------------------------

    [Fact] // AC2
    public void And_BuildsAndNode()
    {
        Column left = Col("a");
        Column right = Col("b");
        Column result = left.And(right);

        var node = Assert.IsType<And>(ExprOf(result));
        Assert.Same(ExprOf(left), node.Left);
        Assert.Same(ExprOf(right), node.Right);
        Assert.IsType<BooleanType>(node.Type);
    }

    [Fact] // AC2
    public void Or_BuildsOrNode()
    {
        Column left = Col("a");
        Column right = Col("b");
        Column result = left.Or(right);

        var node = Assert.IsType<Or>(ExprOf(result));
        Assert.Same(ExprOf(left), node.Left);
        Assert.Same(ExprOf(right), node.Right);
        Assert.IsType<BooleanType>(node.Type);
    }

    [Fact] // AC2
    public void Not_BuildsNotNode_OverChild()
    {
        Column child = Col("flag");
        Column result = child.Not();

        var node = Assert.IsType<Not>(ExprOf(result));
        Assert.Same(ExprOf(child), node.Child);
        Assert.IsType<BooleanType>(node.Type);
    }

    [Fact] // AC2: Not's nullability follows its child (NOT NULL = NULL under 3VL)
    public void Not_NullabilityFollowsChild()
    {
        // A comparison over unresolved refs is conservatively nullable → Not is nullable too.
        Column nullableChild = Col("a").Gt(Col("b"));
        Assert.True(ExprOf(nullableChild).Nullable);
        Assert.True(ExprOf(nullableChild.Not()).Nullable);

        // A non-null child (IsNull never yields NULL) → Not stays non-null.
        Column nonNullChild = Col("a").IsNull();
        Assert.False(ExprOf(nonNullChild).Nullable);
        Assert.False(ExprOf(nonNullChild.Not()).Nullable);
    }

    // ----- AC3: null checks + null-safe equality are distinct node kinds -------------------------

    [Fact] // AC3
    public void IsNull_And_IsNotNull_AreDistinctNodeKinds()
    {
        Column column = Col("a");

        var isNull = Assert.IsType<IsNull>(ExprOf(column.IsNull()));
        var isNotNull = Assert.IsType<IsNotNull>(ExprOf(column.IsNotNull()));

        Assert.Same(ExprOf(column), isNull.Child);
        Assert.Same(ExprOf(column), isNotNull.Child);
        // Distinct kinds preserve Spark null behavior; neither ever yields SQL NULL.
        Assert.False(isNull.Nullable);
        Assert.False(isNotNull.Nullable);
        Assert.NotEqual(isNull.NodeName, isNotNull.NodeName);
    }

    [Fact] // AC3: EqualNullSafe (<=>) is a distinct node from EqualTo (=)
    public void EqualNullSafe_IsDistinctFromEqualTo()
    {
        Column a = Col("a");
        Column b = Col("b");

        var nullSafe = Assert.IsType<EqualNullSafe>(ExprOf(a.EqualNullSafe(b)));
        var equalTo = Assert.IsType<BinaryComparison>(ExprOf(a.EqualTo(b)));

        Assert.Same(ExprOf(a), nullSafe.Left);
        Assert.Same(ExprOf(b), nullSafe.Right);
        Assert.Equal(ComparisonOperator.Equal, equalTo.Operator);
        // EqualNullSafe never yields NULL; a plain comparison over nullable operands is nullable.
        Assert.False(nullSafe.Nullable);
        Assert.True(equalTo.Nullable);
    }

    // ----- Literal coercion: mixed Column ⟨op⟩ scalar builds ⟨op⟩(colExpr, Lit(scalar)) -----------

    [Fact] // literal coercion
    public void Gt_WithScalar_CoercesToLiteral()
    {
        var node = Assert.IsType<BinaryComparison>(ExprOf(Col("age").Gt(5)));

        Assert.Equal(ComparisonOperator.GreaterThan, node.Operator);
        Assert.IsType<UnresolvedAttribute>(node.Left);
        var literal = Assert.IsType<Literal>(node.Right);
        Assert.Equal(5, literal.Value);
        Assert.IsType<IntegerType>(literal.Type);
    }

    [Fact] // literal coercion for arithmetic
    public void Plus_WithScalar_CoercesToLiteral()
    {
        var node = Assert.IsType<BinaryArithmetic>(ExprOf(Col("x").Plus(10L)));

        Assert.Equal(ArithmeticOperator.Add, node.Operator);
        var literal = Assert.IsType<Literal>(node.Right);
        Assert.Equal(10L, literal.Value);
    }

    [Fact] // EqualNullSafe(null) builds a typed SQL NULL literal — the "is null?" null-safe idiom
    public void EqualNullSafe_WithNull_CoercesToNullLiteral()
    {
        var node = Assert.IsType<EqualNullSafe>(ExprOf(Col("a").EqualNullSafe((object?)null)));

        var literal = Assert.IsType<Literal>(node.Right);
        Assert.True(literal.IsNull);
        Assert.IsType<NullType>(literal.Type);
    }

    [Fact] // an existing Column passed to the object? overload passes through (Lit idempotence)
    public void ScalarOverload_WithColumn_PassesThroughUnchanged()
    {
        Column right = Col("b");
        var node = Assert.IsType<BinaryArithmetic>(ExprOf(Col("a").Plus((object)right)));

        Assert.Same(ExprOf(right), node.Right);
    }

    // ----- C# operator overloads mirror the named methods ----------------------------------------

    [Fact] // operator overloads
    public void ArithmeticOperators_BuildSameNodesAsMethods()
    {
        Column a = Col("a");
        Column b = Col("b");

        Assert.Equal(ArithmeticOperator.Add, ArithOp(a + b));
        Assert.Equal(ArithmeticOperator.Subtract, ArithOp(a - b));
        Assert.Equal(ArithmeticOperator.Multiply, ArithOp(a * b));
        Assert.Equal(ArithmeticOperator.Divide, ArithOp(a / b));
        Assert.Equal(ArithmeticOperator.Remainder, ArithOp(a % b));

        static ArithmeticOperator ArithOp(Column c) =>
            Assert.IsType<BinaryArithmetic>(c.Expr).Operator;
    }

    [Fact] // operator overloads
    public void ComparisonOperators_BuildSameNodesAsMethods()
    {
        Column a = Col("a");
        Column b = Col("b");

        Assert.Equal(ComparisonOperator.LessThan, CmpOp(a < b));
        Assert.Equal(ComparisonOperator.LessThanOrEqual, CmpOp(a <= b));
        Assert.Equal(ComparisonOperator.GreaterThan, CmpOp(a > b));
        Assert.Equal(ComparisonOperator.GreaterThanOrEqual, CmpOp(a >= b));

        static ComparisonOperator CmpOp(Column c) =>
            Assert.IsType<BinaryComparison>(c.Expr).Operator;
    }

    [Fact] // operator overloads with a scalar coerce to a literal on the correct side
    public void ComparisonOperator_WithScalar_KeepsOperandOrder()
    {
        // col < 5  →  LessThan(col, lit 5)
        var right = Assert.IsType<BinaryComparison>(ExprOf(Col("age") < 5));
        Assert.Equal(ComparisonOperator.LessThan, right.Operator);
        Assert.IsType<UnresolvedAttribute>(right.Left);
        Assert.IsType<Literal>(right.Right);

        // 5 < col  →  LessThan(lit 5, col)  (operands reversed, NOT rewritten to GreaterThan)
        var left = Assert.IsType<BinaryComparison>(ExprOf(5 < Col("age")));
        Assert.Equal(ComparisonOperator.LessThan, left.Operator);
        Assert.IsType<Literal>(left.Left);
        Assert.IsType<UnresolvedAttribute>(left.Right);
    }

    [Fact] // & / | / ! operator overloads delegate to And/Or/Not
    public void BooleanOperators_BuildBooleanNodes()
    {
        Column a = Col("a");
        Column b = Col("b");

        Assert.IsType<And>(ExprOf(a & b));
        Assert.IsType<Or>(ExprOf(a | b));
        Assert.IsType<Not>(ExprOf(!a));
    }

    // ----- Immutability: operators return NEW Columns; operands are unchanged ---------------------

    [Fact] // immutability
    public void Operators_ReturnNewColumns_OperandsUnchanged()
    {
        Column a = Col("a");
        Column b = Col("b");
        Expression aExpr = ExprOf(a);
        Expression bExpr = ExprOf(b);

        Column result = a.Plus(b);

        Assert.NotSame(a, result);
        Assert.NotSame(b, result);
        Assert.Same(aExpr, ExprOf(a)); // operands' wrapped IR is untouched
        Assert.Same(bExpr, ExprOf(b));
        var node = Assert.IsType<BinaryArithmetic>(ExprOf(result));
        Assert.Same(aExpr, node.Left);
        Assert.Same(bExpr, node.Right);
    }

    // ----- AC4: the API does not coerce/validate types — misuse builds a node, analyzer rejects ---

    [Fact] // AC4
    public void ArithmeticOverBooleanPredicate_BuildsNodeWithoutThrowing()
    {
        // A boolean predicate used in an arithmetic context is *type misuse*, but the API must NOT
        // reject or coerce it — it simply records (IsNull(a)) + 1. The analyzer (FEAT-04.5) is the
        // single place that reports this under ADR-0008. Here we prove the API builds it silently.
        Column boolean = Col("a").IsNull();

        Column misuse = boolean.Plus(1);

        var node = Assert.IsType<BinaryArithmetic>(ExprOf(misuse));
        Assert.IsType<IsNull>(node.Left); // the boolean operand is preserved verbatim, not coerced
        Assert.IsType<Literal>(node.Right);
        Assert.Null(node.Type); // no result type invented at the API layer
    }

    [Fact] // AC4: arithmetic operands are recorded as-is with no numeric widening/casting inserted
    public void Arithmetic_DoesNotInsertCastsOrCoercions()
    {
        // Adding a long literal to an int column would coerce in Spark's analyzer, but the API must
        // not insert a Cast — it records the raw operands and leaves promotion to the analyzer.
        var node = Assert.IsType<BinaryArithmetic>(ExprOf(Col("i").Plus(3L)));

        Assert.IsType<UnresolvedAttribute>(node.Left);
        var literal = Assert.IsType<Literal>(node.Right);
        Assert.IsType<LongType>(literal.Type); // stays a long; no cast node wraps either operand
    }

    // ----- Null guards: a null Column operand is rejected; a null scalar is a NULL literal ---------

    [Fact]
    public void Method_NullColumnOperand_Throws()
    {
        Column a = Col("a");
        Assert.Throws<ArgumentNullException>(() => a.Plus((Column)null!));
        Assert.Throws<ArgumentNullException>(() => a.EqualTo((Column)null!));
        Assert.Throws<ArgumentNullException>(() => a.And((Column)null!));
        Assert.Throws<ArgumentNullException>(() => a.EqualNullSafe((Column)null!));
    }

    [Fact]
    public void Operator_NullColumnOperand_Throws()
    {
        Column a = Col("a");
        Assert.Throws<ArgumentNullException>(() => (null! as Column)! + a);
        Assert.Throws<ArgumentNullException>(() => a & (null as Column)!);
        Assert.Throws<ArgumentNullException>(() => !(null as Column)!);
    }

    [Fact] // a null scalar via the object? overload is a typed SQL NULL literal, not an error
    public void ScalarOverload_NullValue_BuildsNullLiteral()
    {
        var node = Assert.IsType<BinaryArithmetic>(ExprOf(Col("a").Plus((object?)null)));
        var literal = Assert.IsType<Literal>(node.Right);
        Assert.True(literal.IsNull);
    }

    // ----- Lazy guarantee: building operator chains touches no execution seam (audit-style) -------

    [Fact] // lazy: mirrors the #169 audit non-vacuity — pure construction observes no execution
    public void OperatorChains_AreLazy_ObserveNoExecution()
    {
        var recording = new RecordingAudit();

        using (ExecutionAudit.BeginScope(recording))
        {
            // A deep chain across every operator family — all pure IR construction, no evaluation.
            Column chain = Col("a").Plus(1).Multiply(Col("b"))
                .Gt(10)
                .And(Col("c").IsNotNull())
                .Or(Col("d").EqualNullSafe((object?)null))
                .Not();

            // Touch the built tree the way construction-time code does; still no execution.
            Assert.False(chain.Expr.Resolved);
            Assert.IsType<Not>(chain.Expr);
        }

        Assert.True(recording.ObservedNoExecution);
    }

    [Fact] // lazy: no operator consults a schema — every leaf stays unresolved
    public void Operators_PerformNoSchemaLookup()
    {
        Column result = Col("unknown_a").Plus(Col("unknown_b")).Gt(0);

        Assert.False(result.Expr.Resolved); // still unresolved: no catalog/schema was consulted
        Assert.Equal("(('unknown_a + 'unknown_b) > 0)", result.ToString());
    }

    // ----- Operand-order efficacy: pin Left/Right *identity* for every C# operator overload --------
    // These close the mutation gaps Quality proved: swapping operands (a-b → b-a) or reversing the
    // scalar-on-left forms (5-col → col-5) previously stayed GREEN. Assert.Same on Left/Right makes
    // any operand swap redden. Arithmetic and reversed comparisons are non-commutative, so order is
    // semantically load-bearing (5 - col ≠ col - 5; 5 < col ≠ col < 5).

    [Fact] // FIX 1: C# arithmetic Column⟨op⟩Column overloads pin operand identity & order
    public void ArithmeticOperators_ColumnColumn_PreserveOperandOrder()
    {
        Column a = Col("a");
        Column b = Col("b");

        AssertArith(a + b, ArithmeticOperator.Add, a, b);
        AssertArith(a - b, ArithmeticOperator.Subtract, a, b);
        AssertArith(a * b, ArithmeticOperator.Multiply, a, b);
        AssertArith(a / b, ArithmeticOperator.Divide, a, b);
        AssertArith(a % b, ArithmeticOperator.Remainder, a, b);

        static void AssertArith(Column result, ArithmeticOperator op, Column left, Column right)
        {
            var node = Assert.IsType<BinaryArithmetic>(result.Expr);
            Assert.Equal(op, node.Operator);
            Assert.Same(left.Expr, node.Left);
            Assert.Same(right.Expr, node.Right);
        }
    }

    [Fact] // FIX 1: `col ⟨op⟩ scalar` keeps the column on the LEFT and the literal on the RIGHT
    public void ArithmeticOperators_ScalarOnRight_PreserveOperandOrder()
    {
        Column c = Col("age");

        AssertRight(c + 5, ArithmeticOperator.Add, c, 5);
        AssertRight(c - 5, ArithmeticOperator.Subtract, c, 5);
        AssertRight(c * 5, ArithmeticOperator.Multiply, c, 5);
        AssertRight(c / 5, ArithmeticOperator.Divide, c, 5);
        AssertRight(c % 5, ArithmeticOperator.Remainder, c, 5);

        static void AssertRight(Column result, ArithmeticOperator op, Column left, int rightLit)
        {
            var node = Assert.IsType<BinaryArithmetic>(result.Expr);
            Assert.Equal(op, node.Operator);
            Assert.Same(left.Expr, node.Left);
            Assert.Equal(rightLit, Assert.IsType<Literal>(node.Right).Value);
        }
    }

    [Fact] // FIX 1: reversed `scalar ⟨op⟩ col` keeps the literal on the LEFT (NOT rewritten/swapped)
    public void ArithmeticOperators_ScalarOnLeft_PreserveOperandOrder()
    {
        Column c = Col("age");

        AssertLeft(5 + c, ArithmeticOperator.Add, 5, c);
        AssertLeft(5 - c, ArithmeticOperator.Subtract, 5, c);
        AssertLeft(5 * c, ArithmeticOperator.Multiply, 5, c);
        AssertLeft(5 / c, ArithmeticOperator.Divide, 5, c);
        AssertLeft(5 % c, ArithmeticOperator.Remainder, 5, c);

        static void AssertLeft(Column result, ArithmeticOperator op, int leftLit, Column right)
        {
            var node = Assert.IsType<BinaryArithmetic>(result.Expr);
            Assert.Equal(op, node.Operator);
            Assert.Equal(leftLit, Assert.IsType<Literal>(node.Left).Value);
            Assert.Same(right.Expr, node.Right);
        }
    }

    [Fact] // FIX 1: ordering comparison Column⟨op⟩Column overloads pin operand identity & order
    public void ComparisonOperators_ColumnColumn_PreserveOperandOrder()
    {
        Column a = Col("a");
        Column b = Col("b");

        AssertCmp(a < b, ComparisonOperator.LessThan, a, b);
        AssertCmp(a <= b, ComparisonOperator.LessThanOrEqual, a, b);
        AssertCmp(a > b, ComparisonOperator.GreaterThan, a, b);
        AssertCmp(a >= b, ComparisonOperator.GreaterThanOrEqual, a, b);

        static void AssertCmp(Column result, ComparisonOperator op, Column left, Column right)
        {
            var node = Assert.IsType<BinaryComparison>(result.Expr);
            Assert.Equal(op, node.Operator);
            Assert.Same(left.Expr, node.Left);
            Assert.Same(right.Expr, node.Right);
        }
    }

    [Fact] // FIX 1: `col ⟨op⟩ scalar` keeps the column on the LEFT, literal on the RIGHT
    public void ComparisonOperators_ScalarOnRight_PreserveOperandOrder()
    {
        Column c = Col("age");

        AssertRight(c < 5, ComparisonOperator.LessThan, c);
        AssertRight(c <= 5, ComparisonOperator.LessThanOrEqual, c);
        AssertRight(c > 5, ComparisonOperator.GreaterThan, c);
        AssertRight(c >= 5, ComparisonOperator.GreaterThanOrEqual, c);

        static void AssertRight(Column result, ComparisonOperator op, Column left)
        {
            var node = Assert.IsType<BinaryComparison>(result.Expr);
            Assert.Equal(op, node.Operator);
            Assert.Same(left.Expr, node.Left);
            Assert.IsType<Literal>(node.Right);
        }
    }

    [Fact] // FIX 1: reversed `scalar ⟨op⟩ col` keeps the literal on the LEFT (operator NOT flipped)
    public void ComparisonOperators_ScalarOnLeft_PreserveOperandOrder()
    {
        Column c = Col("age");

        AssertLeft(5 < c, ComparisonOperator.LessThan, c);
        AssertLeft(5 <= c, ComparisonOperator.LessThanOrEqual, c);
        AssertLeft(5 > c, ComparisonOperator.GreaterThan, c);
        AssertLeft(5 >= c, ComparisonOperator.GreaterThanOrEqual, c);

        static void AssertLeft(Column result, ComparisonOperator op, Column right)
        {
            var node = Assert.IsType<BinaryComparison>(result.Expr);
            Assert.Equal(op, node.Operator); // e.g. `5 < col` stays LessThan(lit, col), not GreaterThan
            Assert.IsType<Literal>(node.Left);
            Assert.Same(right.Expr, node.Right);
        }
    }

    // ----- object? overload efficacy: every scalar overload builds the right node + right-side Literal

    [Fact] // FIX 1: arithmetic & comparison object? overloads coerce the scalar to a right Literal
    public void ObjectOverloads_Arithmetic_And_Comparison_CoerceScalarToRightLiteral()
    {
        Column a = Col("a");

        AssertArith(a.Plus(1), ArithmeticOperator.Add, a);
        AssertArith(a.Minus(1), ArithmeticOperator.Subtract, a);
        AssertArith(a.Multiply(1), ArithmeticOperator.Multiply, a);
        AssertArith(a.Divide(1), ArithmeticOperator.Divide, a);
        AssertArith(a.Mod(1), ArithmeticOperator.Remainder, a);

        AssertCmp(a.EqualTo(1), ComparisonOperator.Equal, a);
        AssertCmp(a.NotEqual(1), ComparisonOperator.NotEqual, a);
        AssertCmp(a.Lt(1), ComparisonOperator.LessThan, a);
        AssertCmp(a.Leq(1), ComparisonOperator.LessThanOrEqual, a);
        AssertCmp(a.Gt(1), ComparisonOperator.GreaterThan, a);
        AssertCmp(a.Geq(1), ComparisonOperator.GreaterThanOrEqual, a);

        static void AssertArith(Column result, ArithmeticOperator op, Column left)
        {
            var node = Assert.IsType<BinaryArithmetic>(result.Expr);
            Assert.Equal(op, node.Operator);
            Assert.Same(left.Expr, node.Left);
            Assert.IsType<Literal>(node.Right);
        }

        static void AssertCmp(Column result, ComparisonOperator op, Column left)
        {
            var node = Assert.IsType<BinaryComparison>(result.Expr);
            Assert.Equal(op, node.Operator);
            Assert.Same(left.Expr, node.Left);
            Assert.IsType<Literal>(node.Right);
        }
    }

    [Fact] // FIX 1: And/Or/EqualNullSafe object? overloads build the right node with a right Literal
    public void ObjectOverloads_Boolean_And_NullSafe_BuildRightNodeWithLiteral()
    {
        Column a = Col("a");

        var and = Assert.IsType<And>(ExprOf(a.And(true)));
        Assert.Same(a.Expr, and.Left);
        Assert.Equal(true, Assert.IsType<Literal>(and.Right).Value);

        var or = Assert.IsType<Or>(ExprOf(a.Or(false)));
        Assert.Same(a.Expr, or.Left);
        Assert.Equal(false, Assert.IsType<Literal>(or.Right).Value);

        // EqualNullSafe with a NON-null scalar → a non-null Literal on the right (the mutation-proof case).
        var ens = Assert.IsType<EqualNullSafe>(ExprOf(a.EqualNullSafe((object?)5)));
        Assert.Same(a.Expr, ens.Left);
        var lit = Assert.IsType<Literal>(ens.Right);
        Assert.Equal(5, lit.Value);
        Assert.False(lit.IsNull);
    }

    // ----- The `==` decision is unpinned no more: reference identity, never an expression -----------

    [Fact] // FIX 1: no operator == is defined — col == null / col == other is CLR reference identity
    public void EqualityOperator_IsReferenceIdentity_AndBuildsNoExpression()
    {
        Column a = Col("a");
        Column b = Col("b");
        Column aRef = a;

        // == is NOT overloaded, so it is reference equality returning bool — it builds no IR node.
        Assert.False(a == null);
        Assert.False(a == b);
        Assert.True(a == aRef);
        Assert.True(ReferenceEquals(a, aRef));

        // Value equality of the *expression* is the EqualTo method, which DOES build a node.
        var node = Assert.IsType<BinaryComparison>(ExprOf(a.EqualTo(b)));
        Assert.Equal(ComparisonOperator.Equal, node.Operator);
        Assert.Same(a.Expr, node.Left);
        Assert.Same(b.Expr, node.Right);
    }

    // ----- FIX 2: EqualNullSafe null-binding is explicit (bare null throws; (object?)null is a NULL) -

    [Fact] // FIX 2: (object?)null builds a null Literal; a bare null binds the Column overload → throws
    public void EqualNullSafe_NullBinding_IsExplicit()
    {
        Column a = Col("a");

        var node = Assert.IsType<EqualNullSafe>(ExprOf(a.EqualNullSafe((object?)null)));
        var lit = Assert.IsType<Literal>(node.Right);
        Assert.True(lit.IsNull);

        // A BARE null binds the more-specific Column overload, which null-guards and throws — this is
        // exactly why the XML doc steers users to (object?)null / IsNull().
        Assert.Throws<ArgumentNullException>(() => a.EqualNullSafe((Column)null!));
    }

    // ----- FIX 3: ~col (PySpark's primary negation) builds the same Not node as !col / .Not() --------

    [Fact] // FIX 3: operator ~ builds a Not node identical to operator ! and .Not()
    public void TildeOperator_BuildsNotNode_LikeBangAndNot()
    {
        Column a = Col("a");

        var tilde = Assert.IsType<Not>(ExprOf(~a));
        Assert.Same(a.Expr, tilde.Child);

        Assert.IsType<Not>(ExprOf(!a));
        Assert.IsType<Not>(ExprOf(a.Not()));

        // ~col guards null the same way ! does.
        Assert.Throws<ArgumentNullException>(() => ~(null as Column)!);
    }

    // ----- FIX 7: EqNullSafe aliases delegate to EqualNullSafe (same node, same operands) -----------

    [Fact] // FIX 7: EqNullSafe(Column) / EqNullSafe(object?) delegate to EqualNullSafe
    public void EqNullSafe_Aliases_DelegateToEqualNullSafe()
    {
        Column a = Col("a");
        Column b = Col("b");

        var colNode = Assert.IsType<EqualNullSafe>(ExprOf(a.EqNullSafe(b)));
        Assert.Same(a.Expr, colNode.Left);
        Assert.Same(b.Expr, colNode.Right);

        var scalarNode = Assert.IsType<EqualNullSafe>(ExprOf(a.EqNullSafe((object?)5)));
        Assert.Same(a.Expr, scalarNode.Left);
        Assert.Equal(5, Assert.IsType<Literal>(scalarNode.Right).Value);

        // Same bare-null binding rule as EqualNullSafe.
        Assert.Throws<ArgumentNullException>(() => a.EqNullSafe((Column)null!));
    }
}
