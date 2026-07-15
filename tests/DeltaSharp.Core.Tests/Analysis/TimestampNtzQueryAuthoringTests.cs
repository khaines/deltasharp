using DeltaSharp.Analysis;
using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Analysis;

/// <summary>
/// timestamp_ntz query-authoring analysis coverage (#558). Two seams that the runtime relies on but
/// that had no PR-owned analyzer test:
/// <list type="bullet">
/// <item>a raw <c>date</c>-vs-<c>timestamp_ntz</c> comparison is resolved by type coercion, which
/// widens the pair to <c>timestamp_ntz</c> and inserts a <c>Cast(date → timestamp_ntz)</c> on the
/// date operand — so the mixed pair NEVER reaches
/// <c>ComparisonKernels</c>' fail-closed <see cref="System.NotSupportedException"/> (that guard exists
/// only for an un-coerced pair that cannot arise from analysis);</item>
/// <item>a <c>timestamp_ntz</c> literal flows through analysis intact (typed, valued, resolved) in
/// both output (Select) and predicate (Filter) position, ready for physical translation.</item>
/// </list>
/// </summary>
public class TimestampNtzQueryAuthoringTests
{
    private static readonly StructType EventsSchema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField("dt", DateType.Instance, nullable: true),
        new StructField("tsn", TimestampNtzType.Instance, nullable: true),
    });

    private static Analyzer EventsAnalyzer()
    {
        var catalog = new LocalCatalog();
        catalog.Register("events", EventsSchema);
        return new Analyzer(catalog);
    }

    private static UnresolvedRelation Relation() => new(new[] { "events" });

    private static UnresolvedAttribute Col(string name) => new(name);

    // ---- FIX 2: date-vs-timestamp_ntz comparison coercion (the fail-closed kernel is never hit) ----

    [Fact]
    public void Coerce_DateThenTimestampNtz_CastsDateOperandToTimestampNtz()
    {
        // date <op> timestamp_ntz: FindTightestCommonType(date, ntz) = ntz, so CoerceComparison widens
        // the date operand with an inserted Cast(date -> timestamp_ntz) and leaves the ntz operand alone.
        // Exercised across ordering AND equality operators (the offset seam matters for both families).
        foreach (ComparisonOperator op in new[]
        {
            ComparisonOperator.GreaterThan, ComparisonOperator.Equal, ComparisonOperator.LessThanOrEqual,
        })
        {
            var comparison = new BinaryComparison(Literal.OfDate(19_000), Literal.OfTimestampNtz(0L), op);

            var coerced = Assert.IsType<BinaryComparison>(ExpressionCoercion.Coerce(comparison));

            var leftCast = Assert.IsType<Cast>(coerced.Left);
            Assert.Equal(TimestampNtzType.Instance, leftCast.TargetType);
            Assert.Equal(DateType.Instance, leftCast.Child.Type);      // the pre-cast operand was the date
            Assert.Same(comparison.Right, coerced.Right);              // ntz operand untouched

            // Both operands are now timestamp_ntz — the raw date-vs-ntz kernel guard can never be reached.
            Assert.Equal(TimestampNtzType.Instance, coerced.Left.Type);
            Assert.Equal(TimestampNtzType.Instance, coerced.Right.Type);
        }
    }

    [Fact]
    public void Coerce_TimestampNtzThenDate_CastsDateOperandToTimestampNtz()
    {
        // The reverse operand order coerces symmetrically: the date operand (now on the right) gets the
        // inserted Cast(date -> timestamp_ntz); the ntz operand (now on the left) is untouched.
        foreach (ComparisonOperator op in new[]
        {
            ComparisonOperator.GreaterThan, ComparisonOperator.Equal, ComparisonOperator.LessThanOrEqual,
        })
        {
            var comparison = new BinaryComparison(Literal.OfTimestampNtz(0L), Literal.OfDate(19_000), op);

            var coerced = Assert.IsType<BinaryComparison>(ExpressionCoercion.Coerce(comparison));

            Assert.Same(comparison.Left, coerced.Left);               // ntz operand untouched
            var rightCast = Assert.IsType<Cast>(coerced.Right);
            Assert.Equal(TimestampNtzType.Instance, rightCast.TargetType);
            Assert.Equal(DateType.Instance, rightCast.Child.Type);

            Assert.Equal(TimestampNtzType.Instance, coerced.Left.Type);
            Assert.Equal(TimestampNtzType.Instance, coerced.Right.Type);
        }
    }

    [Fact]
    public void Resolve_FilterDateEqualsTimestampNtz_InsertsCastOnDateOperand_NeitherReachesKernelGuard()
    {
        // End-to-end through the analyzer (not just the isolated rule): a Filter whose condition compares
        // a date column to a timestamp_ntz column resolves to a boolean comparison whose date operand is
        // wrapped in Cast(date -> timestamp_ntz), so the physical comparison kernel only ever sees ntz-vs-ntz.
        Analyzer analyzer = EventsAnalyzer();
        var filter = new Filter(
            new BinaryComparison(Col("dt"), Col("tsn"), ComparisonOperator.Equal), Relation());

        var resolved = Assert.IsType<Filter>(analyzer.Resolve(filter));
        var comparison = Assert.IsType<BinaryComparison>(resolved.Condition);

        var leftCast = Assert.IsType<Cast>(comparison.Left);
        Assert.Equal(TimestampNtzType.Instance, leftCast.TargetType);
        Assert.Equal(TimestampNtzType.Instance, comparison.Right.Type);   // ntz column stays ntz
        Assert.IsType<AttributeReference>(comparison.Right);              // and stays a bare reference
        Assert.Equal(BooleanType.Instance, comparison.Type);
    }

    [Fact]
    public void Resolve_FilterTimestampNtzEqualsDate_InsertsCastOnDateOperand()
    {
        // Reverse operand order, resolved end-to-end: the date column (right) is cast to timestamp_ntz.
        Analyzer analyzer = EventsAnalyzer();
        var filter = new Filter(
            new BinaryComparison(Col("tsn"), Col("dt"), ComparisonOperator.Equal), Relation());

        var resolved = Assert.IsType<Filter>(analyzer.Resolve(filter));
        var comparison = Assert.IsType<BinaryComparison>(resolved.Condition);

        Assert.Equal(TimestampNtzType.Instance, comparison.Left.Type);    // ntz column stays ntz
        Assert.IsType<AttributeReference>(comparison.Left);
        var rightCast = Assert.IsType<Cast>(comparison.Right);
        Assert.Equal(TimestampNtzType.Instance, rightCast.TargetType);
    }

    // ---- FIX 3: a timestamp_ntz literal flows through analysis in Select and Filter position ----

    [Fact]
    public void Resolve_SelectTimestampNtzLiteral_FlowsThroughAnalysisTypedAndValued()
    {
        // A timestamp_ntz literal projected in a Select survives analysis as a resolved, ntz-typed
        // literal carrying its wall-clock micros — ready for physical translation.
        const long wallClockMicros = 1_700_000_000_000_000L;
        Analyzer analyzer = EventsAnalyzer();
        var project = new Project(
            new Expression[] { new Alias(Literal.OfTimestampNtz(wallClockMicros), "t") }, Relation());

        var resolved = Assert.IsType<Project>(analyzer.Resolve(project));
        var alias = Assert.IsType<Alias>(resolved.ProjectList[0]);
        var literal = Assert.IsType<Literal>(alias.Child);

        Assert.True(literal.Resolved);
        Assert.Equal(TimestampNtzType.Instance, literal.Type);
        Assert.Equal(wallClockMicros, Assert.IsType<long>(literal.Value));
    }

    [Fact]
    public void Resolve_FilterOnTimestampNtzLiteralComparison_FlowsThroughAnalysisAsNtzVsNtz()
    {
        // A timestamp_ntz literal used in a predicate against a timestamp_ntz column needs NO coercion
        // (both operands are already ntz) and flows through analysis as a boolean ntz-vs-ntz comparison.
        const long wallClockMicros = 1_700_000_000_000_000L;
        Analyzer analyzer = EventsAnalyzer();
        var filter = new Filter(
            new BinaryComparison(Col("tsn"), Literal.OfTimestampNtz(wallClockMicros), ComparisonOperator.GreaterThan),
            Relation());

        var resolved = Assert.IsType<Filter>(analyzer.Resolve(filter));
        var comparison = Assert.IsType<BinaryComparison>(resolved.Condition);

        Assert.IsType<AttributeReference>(comparison.Left);
        Assert.Equal(TimestampNtzType.Instance, comparison.Left.Type);
        var literal = Assert.IsType<Literal>(comparison.Right);           // literal untouched (no cast)
        Assert.Equal(TimestampNtzType.Instance, literal.Type);
        Assert.Equal(wallClockMicros, Assert.IsType<long>(literal.Value));
        Assert.Equal(BooleanType.Instance, comparison.Type);
    }
}
