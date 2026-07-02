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
