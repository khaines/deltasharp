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
}
