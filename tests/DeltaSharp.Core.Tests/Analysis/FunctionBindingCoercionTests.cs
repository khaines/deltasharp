using DeltaSharp.Analysis;
using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Analysis;

/// <summary>
/// STORY-04.5.2 (#171) — analyzer <b>function binding</b> and <b>type coercion</b>. These tests cover
/// the four acceptance criteria: each M1 function binds to a typed scalar/aggregate contract (AC1);
/// operands/arguments coerce to Spark-compatible common types with <see cref="Cast"/> nodes inserted
/// under ADR-0008 (AC2); unknown/ill-typed/ill-arity calls and operator type mismatches raise a
/// precise <see cref="AnalysisException"/> naming the function/types (AC3); and aggregate functions
/// used outside a valid aggregate context are rejected before physical planning (AC4). They also
/// close the deferred Batch L type-validation findings (#160/#165/#166 and the null-typed-resolved
/// guard).
/// </summary>
public class FunctionBindingCoercionTests
{
    private static readonly StructType NumbersSchema = new(new[]
    {
        new StructField("i", IntegerType.Instance, nullable: false),
        new StructField("l", LongType.Instance, nullable: true),
        new StructField("d", DoubleType.Instance, nullable: true),
        new StructField("dec", new DecimalType(10, 2), nullable: true),
        new StructField("b", BooleanType.Instance, nullable: true),
        new StructField("s", StringType.Instance, nullable: true),
        new StructField("dt", DateType.Instance, nullable: true),
        new StructField("ts", TimestampType.Instance, nullable: true),
    });

    private static Analyzer NumbersAnalyzer()
    {
        var catalog = new LocalCatalog();
        catalog.Register("nums", NumbersSchema);
        return new Analyzer(catalog);
    }

    private static UnresolvedRelation Relation(string name) => new(new[] { name });

    private static UnresolvedAttribute Col(string name) => new(name);

    private static UnresolvedFunction Fn(string name, params Expression[] args) => new(name, args);

    // ---- AC1: function binding — scalar vs aggregate, result types ----

    [Fact]
    public void Bind_Count_IsAggregateLongNonNull()
    {
        ResolvedFunction bound = FunctionRegistry.Bind(Fn("count", Literal.OfInt(1)));

        Assert.Equal("count", bound.Name);
        Assert.Equal(FunctionKind.Aggregate, bound.Kind);
        Assert.Equal(LongType.Instance, bound.Type);
        Assert.False(bound.Nullable);
    }

    [Fact]
    public void Bind_Sum_WidensIntegralToLong_DecimalByTenDigits_FloatToDouble()
    {
        Assert.Equal(LongType.Instance, FunctionRegistry.Bind(Fn("sum", Literal.OfInt(1))).Type);
        Assert.Equal(DoubleType.Instance, FunctionRegistry.Bind(Fn("sum", Literal.OfDouble(1))).Type);

        var dec = Literal.OfDecimal(0, new DecimalType(10, 2));
        var sumDec = (DecimalType)FunctionRegistry.Bind(Fn("sum", dec)).Type;
        Assert.Equal(20, sumDec.Precision);
        Assert.Equal(2, sumDec.Scale);
    }

    [Fact]
    public void Bind_Avg_IsDoubleForIntegral_WidensDecimalByFour()
    {
        Assert.Equal(DoubleType.Instance, FunctionRegistry.Bind(Fn("avg", Literal.OfLong(1))).Type);

        var dec = Literal.OfDecimal(0, new DecimalType(10, 2));
        var avgDec = (DecimalType)FunctionRegistry.Bind(Fn("avg", dec)).Type;
        Assert.Equal(14, avgDec.Precision);
        Assert.Equal(6, avgDec.Scale);
    }

    [Fact]
    public void Bind_MinMax_ReturnInputTypeAndAreNullable()
    {
        ResolvedFunction min = FunctionRegistry.Bind(Fn("min", Literal.OfInt(1)));
        ResolvedFunction max = FunctionRegistry.Bind(Fn("max", Literal.OfString("a")));

        Assert.Equal(FunctionKind.Aggregate, min.Kind);
        Assert.Equal(IntegerType.Instance, min.Type);
        Assert.True(min.Nullable);
        Assert.Equal(StringType.Instance, max.Type);
    }

    [Theory]
    [InlineData("upper")]
    [InlineData("lower")]
    [InlineData("trim")]
    public void Bind_StringFunctions_AreScalarStringTyped(string name)
    {
        ResolvedFunction bound = FunctionRegistry.Bind(Fn(name, Literal.OfString("x")));

        Assert.Equal(FunctionKind.Scalar, bound.Kind);
        Assert.Equal(StringType.Instance, bound.Type);
    }

    [Fact]
    public void Bind_Upper_CoercesNonStringAtomicArgumentToString()
    {
        ResolvedFunction bound = FunctionRegistry.Bind(Fn("upper", Literal.OfInt(7)));

        var cast = Assert.IsType<Cast>(bound.Arguments[0]);
        Assert.Equal(StringType.Instance, cast.TargetType);
    }

    [Fact]
    public void Bind_Length_IsIntegerAndPassesStringThrough()
    {
        ResolvedFunction bound = FunctionRegistry.Bind(Fn("length", Literal.OfString("abc")));

        Assert.Equal(IntegerType.Instance, bound.Type);
        Assert.IsType<Literal>(bound.Arguments[0]);
    }

    [Fact]
    public void Bind_Concat_CoercesEveryArgumentToStringAndIsNullableIfAnyArgIs()
    {
        ResolvedFunction bound = FunctionRegistry.Bind(
            Fn("concat", Literal.OfString("a"), Literal.OfInt(1)));

        Assert.Equal(StringType.Instance, bound.Type);
        Assert.IsType<Literal>(bound.Arguments[0]);
        Assert.IsType<Cast>(bound.Arguments[1]);
    }

    [Fact]
    public void Bind_Coalesce_TakesCommonTypeAndWidensArguments()
    {
        ResolvedFunction bound = FunctionRegistry.Bind(
            Fn("coalesce", Literal.OfInt(1), Literal.OfLong(2)));

        Assert.Equal(LongType.Instance, bound.Type);
        var widened = Assert.IsType<Cast>(bound.Arguments[0]);
        Assert.Equal(LongType.Instance, widened.TargetType);
        Assert.IsType<Literal>(bound.Arguments[1]);
    }

    [Fact]
    public void Bind_CurrentDateAndTimestamp_AreNullaryNonNull()
    {
        ResolvedFunction date = FunctionRegistry.Bind(Fn("current_date"));
        ResolvedFunction ts = FunctionRegistry.Bind(Fn("current_timestamp"));

        Assert.Equal(DateType.Instance, date.Type);
        Assert.False(date.Nullable);
        Assert.Equal(TimestampType.Instance, ts.Type);
        Assert.False(ts.Nullable);
    }

    [Theory]
    [InlineData("year")]
    [InlineData("month")]
    [InlineData("dayofmonth")]
    public void Bind_DateParts_ReturnIntegerAndAcceptDate(string name)
    {
        ResolvedFunction bound = FunctionRegistry.Bind(Fn(name, Literal.OfDate(0)));

        Assert.Equal(IntegerType.Instance, bound.Type);
        Assert.IsType<Literal>(bound.Arguments[0]);
    }

    [Fact]
    public void Bind_DatePart_ParsesStringArgumentToDate()
    {
        ResolvedFunction bound = FunctionRegistry.Bind(Fn("year", Literal.OfString("2020-01-01")));

        var cast = Assert.IsType<Cast>(bound.Arguments[0]);
        Assert.Equal(DateType.Instance, cast.TargetType);
    }

    [Fact]
    public void Bind_ToDate_IsNullableDateAndParsesStringOrTimestamp()
    {
        ResolvedFunction fromString = FunctionRegistry.Bind(Fn("to_date", Literal.OfString("2020-01-01")));

        Assert.Equal(DateType.Instance, fromString.Type);
        Assert.True(fromString.Nullable);
        Assert.IsType<Cast>(fromString.Arguments[0]);
    }

    // ---- AC3: binding diagnostics — unknown / arity / argument type ----

    [Fact]
    public void Bind_UnknownFunction_ThrowsNamingFunctionAndArgumentTypes()
    {
        var ex = Assert.Throws<AnalysisException>(
            () => FunctionRegistry.Bind(Fn("no_such_fn", Literal.OfInt(1))));

        Assert.Equal(AnalysisErrorKind.UnresolvedFunction, ex.Kind);
        Assert.Equal("no_such_fn", ex.Reference);
        Assert.Contains("no_such_fn", ex.Message);
        Assert.Contains("int", ex.Message);
    }

    [Fact]
    public void Bind_WrongArity_ThrowsNamingFunctionAndExpectedForm()
    {
        var ex = Assert.Throws<AnalysisException>(
            () => FunctionRegistry.Bind(Fn("upper", Literal.OfString("a"), Literal.OfString("b"))));

        Assert.Equal(AnalysisErrorKind.InvalidFunctionArgument, ex.Kind);
        Assert.Equal("upper", ex.Reference);
        Assert.Contains("upper", ex.Message);
        Assert.Contains("exactly one argument", ex.Message);
    }

    [Fact]
    public void Bind_SumOfNonNumeric_ThrowsNamingFunctionAndTypes()
    {
        var ex = Assert.Throws<AnalysisException>(
            () => FunctionRegistry.Bind(Fn("sum", Literal.OfString("a"))));

        Assert.Equal(AnalysisErrorKind.InvalidFunctionArgument, ex.Kind);
        Assert.Equal("sum", ex.Reference);
        Assert.Contains("numeric", ex.Message);
        Assert.Contains("string", ex.Message);
    }

    [Fact]
    public void Bind_NullaryWithArguments_Throws()
    {
        var ex = Assert.Throws<AnalysisException>(
            () => FunctionRegistry.Bind(Fn("current_date", Literal.OfInt(1))));

        Assert.Equal(AnalysisErrorKind.InvalidFunctionArgument, ex.Kind);
        Assert.Contains("takes no arguments", ex.Message);
    }

    // ---- AC2: end-to-end coercion through the analyzer ----

    private static ResolvedFunction ResolvedProjection(Analyzer analyzer, Expression element)
    {
        var project = new Project(new[] { new Alias(element, "out") }, Relation("nums"));
        var resolved = Assert.IsType<Project>(analyzer.Resolve(project));
        var alias = Assert.IsType<Alias>(resolved.ProjectList[0]);
        return Assert.IsType<ResolvedFunction>(alias.Child);
    }

    [Fact]
    public void Resolve_BindsScalarFunctionOverColumn()
    {
        ResolvedFunction bound = ResolvedProjection(NumbersAnalyzer(), Fn("upper", Col("s")));

        Assert.Equal(FunctionKind.Scalar, bound.Kind);
        Assert.Equal(StringType.Instance, bound.Type);
    }

    [Fact]
    public void Resolve_ArithmeticWidening_IntPlusLong_YieldsLong_WithCastInserted()
    {
        var analyzer = NumbersAnalyzer();
        var inner = new Project(
            new[] { new Alias(new BinaryArithmetic(Col("i"), Col("l"), ArithmeticOperator.Add), "sum") },
            Relation("nums"));
        var outer = new Project(new Expression[] { Col("sum") }, inner);

        var resolvedOuter = Assert.IsType<Project>(analyzer.Resolve(outer));
        var resolvedInner = Assert.IsType<Project>(resolvedOuter.Child);
        var alias = Assert.IsType<Alias>(resolvedInner.ProjectList[0]);
        var arithmetic = Assert.IsType<BinaryArithmetic>(alias.Child);

        Assert.Equal(LongType.Instance, arithmetic.Type);
        // The int operand is widened to long via an inserted Cast; the long operand is untouched.
        var leftCast = Assert.IsType<Cast>(arithmetic.Left);
        Assert.Equal(LongType.Instance, leftCast.TargetType);
        Assert.IsType<AttributeReference>(arithmetic.Right);
        // The alias output attribute (observed from the outer projection) carries the derived type.
        var output = Assert.IsType<AttributeReference>(resolvedOuter.ProjectList[0]);
        Assert.Equal(LongType.Instance, output.Type);
    }

    [Fact]
    public void Resolve_DecimalArithmetic_YieldsDecimalResultType()
    {
        var analyzer = NumbersAnalyzer();
        var project = new Project(
            new[] { new Alias(new BinaryArithmetic(Col("dec"), Col("i"), ArithmeticOperator.Add), "r") },
            Relation("nums"));

        var resolved = Assert.IsType<Project>(analyzer.Resolve(project));
        var alias = Assert.IsType<Alias>(resolved.ProjectList[0]);
        Assert.IsType<DecimalType>(alias.Child.Type);
    }

    [Fact]
    public void Resolve_NullArithmetic_PromotesNullToOtherOperand()
    {
        var analyzer = NumbersAnalyzer();
        var arithmetic = new BinaryArithmetic(Col("i"), Literal.Null(NullType.Instance), ArithmeticOperator.Add);
        var project = new Project(new[] { new Alias(arithmetic, "r") }, Relation("nums"));

        var resolved = Assert.IsType<Project>(analyzer.Resolve(project));
        var alias = Assert.IsType<Alias>(resolved.ProjectList[0]);
        Assert.Equal(IntegerType.Instance, alias.Child.Type);
    }

    // ---- AC3: operator type-validation diagnostics (#165/#166/#160) ----

    [Fact]
    public void Resolve_BooleanInArithmetic_ThrowsDataTypeMismatch()
    {
        var analyzer = NumbersAnalyzer();
        var project = new Project(
            new[] { new Alias(new BinaryArithmetic(Col("b"), Col("i"), ArithmeticOperator.Add), "r") },
            Relation("nums"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(project));

        Assert.Equal(AnalysisErrorKind.DataTypeMismatch, ex.Kind);
        Assert.Contains("numeric", ex.Message);
        Assert.Contains("boolean", ex.Message);
    }

    [Fact]
    public void Resolve_NonBooleanFilterCondition_ThrowsDataTypeMismatch()
    {
        var analyzer = NumbersAnalyzer();
        var filter = new Filter(Col("i"), Relation("nums"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(filter));

        Assert.Equal(AnalysisErrorKind.DataTypeMismatch, ex.Kind);
        Assert.Contains("Filter", ex.Message);
        Assert.Contains("boolean", ex.Message);
    }

    [Fact]
    public void Resolve_BooleanFilterCondition_IsAccepted()
    {
        var analyzer = NumbersAnalyzer();
        var filter = new Filter(
            new BinaryComparison(Col("i"), Literal.OfInt(1), ComparisonOperator.GreaterThan),
            Relation("nums"));

        var resolved = analyzer.Resolve(filter);

        Assert.True(resolved.Resolved);
    }

    [Fact]
    public void Resolve_CaseWhen_NonBooleanCondition_ThrowsDataTypeMismatch()
    {
        var analyzer = NumbersAnalyzer();
        var caseWhen = new CaseWhen(Col("i"), Literal.OfInt(1)).WithElse(Literal.OfInt(0));
        var project = new Project(new[] { new Alias(caseWhen, "r") }, Relation("nums"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(project));

        Assert.Equal(AnalysisErrorKind.DataTypeMismatch, ex.Kind);
        Assert.Contains("boolean", ex.Message);
    }

    [Fact]
    public void Resolve_CaseWhen_IncompatibleBranchValues_ThrowsDataTypeMismatch()
    {
        var analyzer = NumbersAnalyzer();
        var condition = new BinaryComparison(Col("i"), Literal.OfInt(1), ComparisonOperator.GreaterThan);
        var caseWhen = new CaseWhen(condition, Col("i")).WithElse(Col("b"));
        var project = new Project(new[] { new Alias(caseWhen, "r") }, Relation("nums"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(project));

        Assert.Equal(AnalysisErrorKind.DataTypeMismatch, ex.Kind);
        Assert.Contains("common type", ex.Message);
    }

    [Fact]
    public void Resolve_CaseWhen_CompatibleBranches_DeriveCommonResultType()
    {
        var analyzer = NumbersAnalyzer();
        var condition = new BinaryComparison(Col("i"), Literal.OfInt(1), ComparisonOperator.GreaterThan);
        var caseWhen = new CaseWhen(condition, Col("i")).WithElse(Col("l"));
        var project = new Project(new[] { new Alias(caseWhen, "r") }, Relation("nums"));

        var resolved = Assert.IsType<Project>(analyzer.Resolve(project));
        var alias = Assert.IsType<Alias>(resolved.ProjectList[0]);
        Assert.Equal(LongType.Instance, alias.Child.Type);
    }

    // ---- AC4: aggregate-context validation (#166) ----

    [Fact]
    public void Resolve_AggregateInProjection_IsRejected()
    {
        var analyzer = NumbersAnalyzer();
        var project = new Project(new[] { new Alias(Fn("sum", Col("i")), "s") }, Relation("nums"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(project));

        Assert.Equal(AnalysisErrorKind.MisplacedAggregate, ex.Kind);
        Assert.Equal("sum", ex.Reference);
        Assert.Contains("Project", ex.Message);
    }

    [Fact]
    public void Resolve_AggregateInGroupingKey_IsRejected()
    {
        var analyzer = NumbersAnalyzer();
        var aggregate = new Aggregate(
            groupingExpressions: new[] { Fn("sum", Col("i")) },
            aggregateExpressions: new Expression[] { Col("i") },
            Relation("nums"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(aggregate));

        Assert.Equal(AnalysisErrorKind.MisplacedAggregate, ex.Kind);
        Assert.Equal("sum", ex.Reference);
    }

    [Fact]
    public void Resolve_AggregateInAggregateExpressions_IsAccepted()
    {
        var analyzer = NumbersAnalyzer();
        var aggregate = new Aggregate(
            groupingExpressions: new Expression[] { Col("i") },
            aggregateExpressions: new[] { new Alias(Fn("sum", Col("l")), "total") },
            Relation("nums"));

        var resolved = Assert.IsType<Aggregate>(analyzer.Resolve(aggregate));

        Assert.True(resolved.Resolved);
        var alias = Assert.IsType<Alias>(resolved.AggregateExpressions[0]);
        var bound = Assert.IsType<ResolvedFunction>(alias.Child);
        Assert.Equal(FunctionKind.Aggregate, bound.Kind);
        Assert.Equal(LongType.Instance, bound.Type);
    }

    [Fact]
    public void Resolve_BareAggregate_AutoNamesOutputColumn_SparkParity()
    {
        // Spark parity: a BARE aggregate (no alias) in aggregate-expression position is auto-named
        // with the function's pretty SQL string — `groupBy(i).agg(sum(l))` exposes a `sum(l)` output
        // column (unqualified argument name, NO `#id` ExprId suffix). This pins the auto-naming the
        // ToAttribute ResolvedFunction case mints once binding has typed the call.
        var analyzer = NumbersAnalyzer();
        var aggregate = new Aggregate(
            groupingExpressions: new Expression[] { Col("i") },
            aggregateExpressions: new Expression[] { Col("i"), Fn("sum", Col("l")) },
            Relation("nums"));

        // A parent projection reads the auto-named aggregate out of the Aggregate's derived output,
        // proving `sum(l)` is exposed under its pretty SQL name (unqualified arg, no `#id`).
        var project = new Project(new Expression[] { Col("i"), Col("sum(l)") }, aggregate);

        var resolved = Assert.IsType<Project>(analyzer.Resolve(project));
        var resolvedAggregate = Assert.IsType<Aggregate>(resolved.Child);

        Assert.True(resolvedAggregate.Resolved);
        // The bare aggregate stays a ResolvedFunction in the plan; its output attribute is auto-named.
        var sumCall = Assert.IsType<ResolvedFunction>(resolvedAggregate.AggregateExpressions[1]);
        Assert.Equal("sum", sumCall.Name);

        // The parent Project bound `sum(l)` against the Aggregate output: the auto-named, typed column.
        var projectedSum = Assert.IsType<AttributeReference>(resolved.ProjectList[1]);
        Assert.Equal("sum(l)", projectedSum.Name);   // pretty SQL name, no `#id`
        Assert.IsType<LongType>(projectedSum.Type);   // integral sum accumulates in bigint
    }

    // ---- A1 (#166): nested aggregates are rejected ----

    [Theory]
    [InlineData("sum")]    // sum(sum(l)) — aggregate directly inside another aggregate's argument
    [InlineData("count")]  // sum(count(l)) — a different nested aggregate
    public void Resolve_NestedAggregate_IsRejected(string innerName)
    {
        // Spark rejects one aggregate nested inside another aggregate's argument subtree. A plain
        // aggregate (sum(l), count(1)) stays legal — only an aggregate ARGUMENT that itself contains
        // an aggregate is the nesting error this closes for #166.
        var analyzer = NumbersAnalyzer();
        var aggregate = new Aggregate(
            groupingExpressions: new Expression[] { Col("i") },
            aggregateExpressions: new[] { new Alias(Fn("sum", Fn(innerName, Col("l"))), "x") },
            Relation("nums"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(aggregate));

        Assert.Equal(AnalysisErrorKind.MisplacedAggregate, ex.Kind);
        Assert.Contains("nested", ex.Message);
        Assert.Contains("aggregate", ex.Message);
        Assert.Contains(innerName, ex.Message);   // names the offending nested aggregate
    }

    [Fact]
    public void Resolve_PlainAggregateWithScalarArithmetic_IsAccepted()
    {
        // Guard the accept side of A1: a plain aggregate combined with a scalar (sum(x)+1) is NOT a
        // nested aggregate and must still pass — only aggregate-inside-aggregate is rejected.
        var analyzer = NumbersAnalyzer();
        var aggregate = new Aggregate(
            groupingExpressions: new Expression[] { Col("i") },
            aggregateExpressions: new[]
            {
                new Alias(new BinaryArithmetic(Fn("sum", Col("l")), Literal.OfLong(1), ArithmeticOperator.Add), "x"),
            },
            Relation("nums"));

        var resolved = Assert.IsType<Aggregate>(analyzer.Resolve(aggregate));

        Assert.True(resolved.Resolved);
    }

    // ---- A2: diagnostics render the pretty (ExprId-free) reference ----

    [Fact]
    public void Resolve_ArithmeticMismatch_DiagnosticReference_HasNoExprIdAndIsInfix()
    {
        // The DataTypeMismatch 'reference' must use Spark's pretty SQL form — the infix `(b + i)`
        // with BARE attribute names — never the internal `(b#7 + i#8)` with ExprIds.
        var analyzer = NumbersAnalyzer();
        var project = new Project(
            new[] { new Alias(new BinaryArithmetic(Col("b"), Col("i"), ArithmeticOperator.Add), "r") },
            Relation("nums"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(project));

        Assert.Equal(AnalysisErrorKind.DataTypeMismatch, ex.Kind);
        Assert.DoesNotContain("#", ex.Message);          // no leaked ExprId anywhere in the message
        Assert.Contains("(b + i)", ex.Message);          // infix pretty form, bare names
        Assert.DoesNotContain("#", ex.Reference!);
    }

    // ---- A2b: composite resolved references (CASE WHEN / boolean) never leak an ExprId ----

    [Fact]
    public void Resolve_CaseWhenIncompatibleValues_DiagnosticReference_HasNoExprIdAndRendersCaseWhen()
    {
        // The :116 branch/else common-type mismatch must render the offending CASE via the pretty
        // (ExprId-free) renderer — recursing through the boolean `And` condition and the branch/else
        // values — never the internal SimpleString with `#id` suffixes.
        var analyzer = NumbersAnalyzer();
        var condition = new And(
            new BinaryComparison(Col("i"), Literal.OfInt(1), ComparisonOperator.GreaterThan),
            new BinaryComparison(Col("i"), Literal.OfInt(9), ComparisonOperator.LessThan));
        var caseWhen = new CaseWhen(condition, Col("i")).WithElse(Col("s"));   // int vs string: no common type
        var project = new Project(new[] { new Alias(caseWhen, "r") }, Relation("nums"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(project));

        Assert.Equal(AnalysisErrorKind.DataTypeMismatch, ex.Kind);
        Assert.DoesNotContain("#", ex.Message);
        Assert.DoesNotContain("#", ex.Reference!);
        Assert.Contains("CASE WHEN", ex.Message);   // Spark pretty CASE form, bare names
    }

    [Fact]
    public void Resolve_NonBooleanBooleanOperand_DiagnosticReference_HasNoExprId()
    {
        // A non-boolean operand of And/Or/Not (:173) must render its reference via the pretty
        // renderer, so a bare `i` attribute appears without its `#id`.
        var analyzer = NumbersAnalyzer();
        var and = new And(Col("i"), Col("b"));   // left operand `i` is int, not boolean
        var project = new Project(new[] { new Alias(and, "r") }, Relation("nums"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(project));

        Assert.Equal(AnalysisErrorKind.DataTypeMismatch, ex.Kind);
        Assert.Contains("boolean", ex.Message);
        Assert.DoesNotContain("#", ex.Message);
        Assert.DoesNotContain("#", ex.Reference!);
    }

    // ---- A3: DISTINCT is uppercased in the auto-name ----

    [Fact]
    public void Resolve_DistinctAggregate_AutoNamesWithUppercaseDistinct()
    {
        // Spark's pretty SQL uppercases the DISTINCT qualifier: count(DISTINCT l), not count(distinct l).
        var analyzer = NumbersAnalyzer();
        var distinctCount = new UnresolvedFunction("count", new Expression[] { Col("l") }, isDistinct: true);
        var aggregate = new Aggregate(
            groupingExpressions: new Expression[] { Col("i") },
            aggregateExpressions: new Expression[] { Col("i"), distinctCount },
            Relation("nums"));
        var project = new Project(
            new Expression[] { Col("i"), Col("count(DISTINCT l)") }, aggregate);

        var resolved = Assert.IsType<Project>(analyzer.Resolve(project));

        var projected = Assert.IsType<AttributeReference>(resolved.ProjectList[1]);
        Assert.Equal("count(DISTINCT l)", projected.Name);   // uppercase DISTINCT, no `#id`
    }

    // ---- B1: non-decimal Divide yields Double with both operands cast to Double ----

    [Theory]
    [InlineData("i")]   // int / int
    [InlineData("l")]   // int / long
    public void Resolve_DivideNonDecimal_YieldsDouble_WithBothOperandsCast(string rightColumn)
    {
        var analyzer = NumbersAnalyzer();
        var project = new Project(
            new[]
            {
                new Alias(
                    new BinaryArithmetic(Col("i"), Col(rightColumn), ArithmeticOperator.Divide), "r"),
            },
            Relation("nums"));

        var resolved = Assert.IsType<Project>(analyzer.Resolve(project));
        var alias = Assert.IsType<Alias>(resolved.ProjectList[0]);
        var divide = Assert.IsType<BinaryArithmetic>(alias.Child);

        Assert.Equal(DoubleType.Instance, divide.Type);
        var leftCast = Assert.IsType<Cast>(divide.Left);
        Assert.Equal(DoubleType.Instance, leftCast.TargetType);
        var rightCast = Assert.IsType<Cast>(divide.Right);
        Assert.Equal(DoubleType.Instance, rightCast.TargetType);
    }

    // ---- B2: a non-boolean Join condition is a DataTypeMismatch ----

    [Fact]
    public void Resolve_NonBooleanJoinCondition_ThrowsDataTypeMismatch()
    {
        var catalog = new LocalCatalog();
        catalog.Register("l", new StructType(new[] { new StructField("lid", LongType.Instance) }));
        catalog.Register("r", new StructType(new[] { new StructField("rid", LongType.Instance) }));
        var analyzer = new Analyzer(catalog);
        var join = new Join(Relation("l"), Relation("r"), JoinType.Inner, condition: Col("lid"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(join));

        Assert.Equal(AnalysisErrorKind.DataTypeMismatch, ex.Kind);
        Assert.Contains("Join", ex.Message);
        Assert.Contains("boolean", ex.Message);
    }

    // ---- B3: bare/aliased aggregate auto-name coverage beyond sum(...) ----

    [Fact]
    public void Resolve_BareAggregates_AutoNameCountAvgAndAlias()
    {
        // Pins output column names for count(1) → count(1), avg(d) → avg(d), and an aliased
        // sum(i).As("total") → total, beyond the single sum(...) case previously covered.
        var analyzer = NumbersAnalyzer();
        var aggregate = new Aggregate(
            groupingExpressions: new Expression[] { Col("i") },
            aggregateExpressions: new Expression[]
            {
                Col("i"),
                Fn("count", Literal.OfInt(1)),
                Fn("avg", Col("d")),
                new Alias(Fn("sum", Col("i")), "total"),
            },
            Relation("nums"));
        var project = new Project(
            new Expression[] { Col("count(1)"), Col("avg(d)"), Col("total") }, aggregate);

        var resolved = Assert.IsType<Project>(analyzer.Resolve(project));

        Assert.Equal("count(1)", Assert.IsType<AttributeReference>(resolved.ProjectList[0]).Name);
        Assert.Equal("avg(d)", Assert.IsType<AttributeReference>(resolved.ProjectList[1]).Name);
        Assert.Equal("total", Assert.IsType<AttributeReference>(resolved.ProjectList[2]).Name);
    }

    // ---- B4: BinaryComparison widening + reject ----

    [Fact]
    public void Resolve_CrossNumericComparison_InsertsWideningCast()
    {
        // A comparison over int vs long widens the int operand to long via an inserted Cast (the
        // long operand is untouched); the comparison itself is boolean-typed.
        var analyzer = NumbersAnalyzer();
        var project = new Project(
            new[]
            {
                new Alias(new BinaryComparison(Col("i"), Col("l"), ComparisonOperator.Equal), "r"),
            },
            Relation("nums"));

        var resolved = Assert.IsType<Project>(analyzer.Resolve(project));
        var alias = Assert.IsType<Alias>(resolved.ProjectList[0]);
        var comparison = Assert.IsType<BinaryComparison>(alias.Child);

        var leftCast = Assert.IsType<Cast>(comparison.Left);
        Assert.Equal(LongType.Instance, leftCast.TargetType);
        Assert.IsType<AttributeReference>(comparison.Right);
    }

    [Fact]
    public void Resolve_UncomparableComparison_ThrowsDataTypeMismatch()
    {
        // string vs int has no common comparable type in M1 (string↔numeric casts deferred), so the
        // comparison is rejected with a DataTypeMismatch rather than silently mis-comparing.
        var analyzer = NumbersAnalyzer();
        var project = new Project(
            new[]
            {
                new Alias(new BinaryComparison(Col("s"), Col("i"), ComparisonOperator.Equal), "r"),
            },
            Relation("nums"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(project));

        Assert.Equal(AnalysisErrorKind.DataTypeMismatch, ex.Kind);
        Assert.Contains("comparable", ex.Message);
    }

    // ---- null-typed-resolved guard (defense-in-depth over the coercion pass) ----

    [Fact]
    public void CheckAnalysis_ResolvedButUntypedExpression_IsRejected()
    {
        // A resolved expression that reaches CheckAnalysis without a result type (a coercion gap the
        // normal pass would have typed or rejected) must not leak downstream. A test-only untyped
        // leaf placed in a Filter condition exercises the guard directly.
        var analyzer = NumbersAnalyzer();
        var filter = new Filter(new UntypedResolvedLeaf(), Relation("nums"));

        var ex = Assert.Throws<AnalysisException>(() => analyzer.Resolve(filter));

        Assert.Equal(AnalysisErrorKind.UntypedResolvedExpression, ex.Kind);
    }

    /// <summary>A resolved leaf with no result type — models a coercion gap so the CheckAnalysis
    /// null-typed guard is testable (no such node arises from the normal binding/coercion pass).</summary>
    private sealed class UntypedResolvedLeaf : Expression
    {
        public UntypedResolvedLeaf()
            : base(Array.Empty<Expression>())
        {
        }

        public override DataType? Type => null;

        public override bool Resolved => true;

        public override string NodeName => "UntypedResolvedLeaf";

        public override string SimpleString => "untyped()";

        public override Expression WithNewChildren(IReadOnlyList<Expression> newChildren) => this;

        protected override bool NodeEquals(Expression other) => other is UntypedResolvedLeaf;

        protected override int NodeHashCode() => 0;
    }
}
