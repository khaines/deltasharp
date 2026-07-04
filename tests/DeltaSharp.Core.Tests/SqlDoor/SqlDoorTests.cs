using System;
using System.Collections.Generic;
using DeltaSharp.Analysis;
using DeltaSharp.Execution;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.SqlDoor;

/// <summary>
/// STORY-04.1.3 (#159) — the SQL door into the shared plan pipeline. Covers the four acceptance
/// criteria on the Core surface: a supported statement lowers to an <b>unresolved</b> logical plan
/// without executing (AC1); an unsupported construct raises a deterministic
/// <see cref="SqlParseException"/> at parse time, before execution (AC2); equivalent SQL and
/// DataFrame expressions lower to the <b>same</b> shared IR nodes (AC3); and a stopped session throws
/// the same lifecycle error model as <see cref="SparkSession.Read"/> (AC4). See
/// <c>docs/engineering/design/sql-door.md</c>.
/// </summary>
[Collection(SparkSessionTestCollection.Name)]
public sealed class SqlDoorTests
{
    public SqlDoorTests()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
    }

    private static SparkSession NewSession() =>
        SparkSession.Builder().AppName("sql-door").GetOrCreate();

    // ---------------- AC1: Sql builds an unresolved plan without executing ----------------

    [Fact]
    public void Sql_SelectStarFromTable_BuildsUnresolvedProjectOverRelation_WithoutExecuting()
    {
        using SparkSession spark = NewSession();
        // A backend that throws if any action reaches it proves the door never executes.
        spark.QueryExecutor = new ThrowingQueryExecutor();

        DataFrame df = spark.Sql("SELECT * FROM t");

        Assert.NotNull(df);
        Assert.False(df.Plan.Resolved);
        Project project = Assert.IsType<Project>(df.Plan);
        Expression only = Assert.Single(project.ProjectList);
        Assert.IsType<UnresolvedStar>(only);
        UnresolvedRelation relation = Assert.IsType<UnresolvedRelation>(project.Child);
        Assert.Equal(new[] { "t" }, relation.Identifier);
    }

    [Fact]
    public void Sql_SelectProjectFilter_LowersToProjectOverFilterOverRelation()
    {
        using SparkSession spark = NewSession();

        DataFrame df = spark.Sql("SELECT a FROM t WHERE b > 1");

        Project project = Assert.IsType<Project>(df.Plan);
        Filter filter = Assert.IsType<Filter>(project.Child);
        Assert.IsType<UnresolvedRelation>(filter.Child);
        Assert.False(df.Plan.Resolved);
    }

    // ---------------- AC3: SQL and DataFrame lower to the SAME shared nodes ----------------

    [Fact]
    public void Sql_ProjectFilter_StructurallyEqualsDataFrameEquivalentNodes()
    {
        using SparkSession spark = NewSession();

        DataFrame sqlDf = spark.Sql("SELECT a FROM t WHERE b > 1");

        // The expected plan built directly from the shared IR the DataFrame API uses.
        var expected = new Project(
            new Expression[] { new UnresolvedAttribute("a") },
            new Filter(
                new BinaryComparison(
                    new UnresolvedAttribute("b"), Literal.OfInt(1), ComparisonOperator.GreaterThan),
                new UnresolvedRelation(new[] { "t" })));

        Assert.True(expected.Equals(sqlDf.Plan));
        Assert.Equal(expected.GetHashCode(), sqlDf.Plan.GetHashCode());

        // Tie the lowered expressions directly to what the public DataFrame/Column API builds.
        var project = (Project)sqlDf.Plan;
        var filter = (Filter)project.Child;
        Assert.True(Functions.Col("a").Expr.Equals(Assert.Single(project.ProjectList)));
        Assert.True((Functions.Col("b") > Functions.Lit(1)).Expr.Equals(filter.Condition));
    }

    [Fact]
    public void Sql_Alias_LowersToSameAliasNodeAsColumnAs()
    {
        using SparkSession spark = NewSession();

        DataFrame df = spark.Sql("SELECT a AS x FROM t");

        var project = (Project)df.Plan;
        Expression element = Assert.Single(project.ProjectList);
        Assert.True(Functions.Col("a").As("x").Expr.Equals(element));
    }

    [Fact]
    public void Sql_QualifiedStar_LowersToSameStarNodeAsColStar()
    {
        using SparkSession spark = NewSession();

        DataFrame df = spark.Sql("SELECT t.* FROM t");

        var project = (Project)df.Plan;
        Expression element = Assert.Single(project.ProjectList);
        Assert.True(Functions.Col("t.*").Expr.Equals(element));
    }

    [Fact]
    public void Sql_BooleanAndArithmeticExpressions_EqualDataFrameEquivalent()
    {
        using SparkSession spark = NewSession();

        DataFrame df = spark.Sql("SELECT a FROM t WHERE a = 'x' AND b > 1 OR NOT c");

        var filter = (Filter)((Project)df.Plan).Child;
        Column expected = Functions.Col("a").EqualTo("x")
            .And(Functions.Col("b") > Functions.Lit(1))
            .Or(Functions.Col("c").Not());
        Assert.True(expected.Expr.Equals(filter.Condition));
    }

    [Fact]
    public void Sql_NegativeLiteral_FoldsIntoSameLiteralAsLit()
    {
        using SparkSession spark = NewSession();

        DataFrame df = spark.Sql("SELECT a FROM t WHERE b > -1");

        var filter = (Filter)((Project)df.Plan).Child;
        Assert.True((Functions.Col("b") > Functions.Lit(-1)).Expr.Equals(filter.Condition));
    }

    // ---------------- AC2: unsupported constructs → deterministic error at parse time ----------------

    [Theory]
    [InlineData("SELECT a FROM t JOIN u ON t.a = u.a", "JOIN", "JOIN")]
    [InlineData("SELECT a FROM t, u", "IMPLICIT_JOIN", "implicit join")]
    [InlineData("SELECT count(a) FROM t", "FUNCTION_CALL", "function call")]
    [InlineData("SELECT a FROM t GROUP BY a", "GROUP_BY", "GROUP BY")]
    [InlineData("SELECT a FROM t ORDER BY a", "ORDER_BY", "ORDER BY")]
    [InlineData("SELECT a FROM t HAVING a > 1", "HAVING", "HAVING")]
    [InlineData("SELECT a FROM t LIMIT 5", "LIMIT", "LIMIT")]
    [InlineData("SELECT DISTINCT a FROM t", "SELECT_DISTINCT", "SELECT DISTINCT")]
    [InlineData("SELECT a FROM (SELECT a FROM t)", "SUBQUERY", "subquery")]
    [InlineData("SELECT a FROM t UNION SELECT a FROM u", "UNION", "UNION")]
    [InlineData("INSERT INTO t VALUES (1)", "INSERT", "INSERT")]
    [InlineData("CREATE TABLE t (a INT)", "CREATE", "CREATE")]
    public void Sql_UnsupportedConstruct_ThrowsUnsupportedFeature_NamingConstruct_WithoutExecuting(
        string sql, string expectedConstruct, string expectedMessagePart)
    {
        using SparkSession spark = NewSession();
        spark.QueryExecutor = new ThrowingQueryExecutor();

        SqlParseException ex = Assert.Throws<SqlParseException>(() => spark.Sql(sql));

        Assert.Equal(SqlParseErrorKind.UnsupportedFeature, ex.ErrorKind);
        Assert.Equal(expectedConstruct, ex.Construct);
        Assert.Contains(expectedMessagePart, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_UnsupportedConstruct_ExposesStableTokenSeparateFromProse()
    {
        using SparkSession spark = NewSession();

        SqlParseException ex = Assert.Throws<SqlParseException>(() => spark.Sql("SELECT a FROM t GROUP BY a"));

        // The Construct is the stable programmatic token (no spaces / prose); the human phrasing lives
        // only in the message.
        Assert.Equal("GROUP_BY", ex.Construct);
        string construct = Assert.IsType<string>(ex.Construct);
        Assert.DoesNotContain(' ', construct);
        Assert.Contains("GROUP BY", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT a FROM t JOIN u ON t.a = u.a", "DataFrame.Join(")]
    [InlineData("SELECT a FROM t GROUP BY a", "DataFrame.GroupBy(")]
    [InlineData("SELECT a FROM t ORDER BY a", "DataFrame.OrderBy(")]
    [InlineData("SELECT a FROM t LIMIT 5", "DataFrame.Limit(")]
    [InlineData("SELECT DISTINCT a FROM t", "DataFrame.Distinct(")]
    [InlineData("SELECT a FROM t UNION SELECT a FROM u", "DataFrame.Union(")]
    public void Sql_UnsupportedConstructWithDataFrameEquivalent_AppendsOnboardingHint(
        string sql, string expectedHint)
    {
        using SparkSession spark = NewSession();

        SqlParseException ex = Assert.Throws<SqlParseException>(() => spark.Sql(sql));

        Assert.Contains(expectedHint, ex.Message, StringComparison.Ordinal);
    }

    // ---- AC2: recognizable-but-unsupported constructs classify as UnsupportedFeature (not syntax) ----

    [Theory]
    [InlineData("SELECT a FROM t WHERE -a > 1", "UNARY_MINUS")]
    [InlineData("SELECT a FROM t WHERE -(a) > 1", "UNARY_MINUS")]
    [InlineData("SELECT 99999999999999999999999999 FROM t", "DECIMAL_LITERAL")]
    [InlineData("SELECT a FROM t WHERE a IS NULL", "IS_NULL")]
    [InlineData("SELECT a FROM t WHERE a IS NOT NULL", "IS_NULL")]
    [InlineData("SELECT a FROM t WHERE a IN (1, 2)", "IN")]
    [InlineData("SELECT a FROM t WHERE a LIKE 'x%'", "LIKE")]
    [InlineData("SELECT a FROM t WHERE a BETWEEN 1 AND 2", "BETWEEN")]
    public void Sql_RecognizableUnsupportedConstruct_ClassifiesAsUnsupportedFeature(
        string sql, string expectedConstruct)
    {
        using SparkSession spark = NewSession();

        SqlParseException ex = Assert.Throws<SqlParseException>(() => spark.Sql(sql));

        Assert.Equal(SqlParseErrorKind.UnsupportedFeature, ex.ErrorKind);
        Assert.Equal(expectedConstruct, ex.Construct);
    }

    [Fact]
    public void Sql_UnsupportedConstruct_IsDeterministic()
    {
        using SparkSession spark = NewSession();

        SqlParseException first = Assert.Throws<SqlParseException>(() => spark.Sql("SELECT a FROM t LIMIT 5"));
        SqlParseException second = Assert.Throws<SqlParseException>(() => spark.Sql("SELECT a FROM t LIMIT 5"));

        Assert.Equal(first.Message, second.Message);
        Assert.Equal(first.ErrorKind, second.ErrorKind);
        Assert.Equal(first.Construct, second.Construct);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SELECT")]
    [InlineData("SELECT a")]
    [InlineData("SELECT a FROM")]
    [InlineData("SELECT FROM t")]
    [InlineData("SELECT a FROM t WHERE")]
    [InlineData("SELECT 'unterminated FROM t")]
    public void Sql_MalformedStatement_ThrowsSyntaxError(string sql)
    {
        using SparkSession spark = NewSession();

        SqlParseException ex = Assert.Throws<SqlParseException>(() => spark.Sql(sql));

        Assert.Equal(SqlParseErrorKind.SyntaxError, ex.ErrorKind);
        Assert.Null(ex.Construct);
    }

    [Fact]
    public void Sql_Null_ThrowsArgumentNullException()
    {
        using SparkSession spark = NewSession();

        Assert.Throws<ArgumentNullException>(() => spark.Sql(null!));
    }

    // ---------------- AC4: stopped session uses the same lifecycle model as Read ----------------

    [Fact]
    public void Sql_OnStoppedSession_ThrowsSessionStopped_MatchingReadModel()
    {
        SparkSession spark = NewSession();
        spark.Stop();

        SessionStoppedException fromSql = Assert.Throws<SessionStoppedException>(() => spark.Sql("SELECT * FROM t"));
        SessionStoppedException fromRead = Assert.Throws<SessionStoppedException>(() => _ = spark.Read);

        // Same error model: same exception type and the same deterministic per-member message shape.
        Assert.Equal(fromSql.GetType(), fromRead.GetType());
        Assert.Equal(
            SessionStoppedException.ForMember("Sql", "sql-door").Message, fromSql.Message);
        Assert.Contains("Sql", fromSql.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_OnStoppedSession_ChecksLifecycleBeforeArgument()
    {
        SparkSession spark = NewSession();
        spark.Stop();

        // Lifecycle guard runs before the null-argument check, exactly like Read has no body to reach.
        Assert.Throws<SessionStoppedException>(() => spark.Sql(null!));
    }

    // ---------------- Recursion-depth guard: deep input is a caught error, not a crash ----------------

    [Fact]
    public void Sql_DeeplyNestedParentheses_ThrowsCaughtSqlParseException_NotStackOverflow()
    {
        using SparkSession spark = NewSession();

        // ~2000 nested parens would overflow a realistic 1 MB worker-thread stack (uncatchable
        // StackOverflow crashing the whole process) without the parser's recursion-depth guard.
        const int depth = 2000;
        string sql = "SELECT a FROM t WHERE " + new string('(', depth) + "1" + new string(')', depth) + " = 1";

        SqlParseException ex = Assert.Throws<SqlParseException>(() => spark.Sql(sql));

        Assert.Equal(SqlParseErrorKind.SyntaxError, ex.ErrorKind);
        Assert.Contains("nesting too deep", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_DeepNotChain_ThrowsCaughtSqlParseException_NotPlanDepthExceeded()
    {
        using SparkSession spark = NewSession();

        // A long NOT chain used to leak the INTERNAL PlanDepthExceededException (or, past the guard,
        // overflow the stack). It must surface as the public, catchable SqlParseException.
        string notChain = string.Concat(System.Linq.Enumerable.Repeat("NOT ", 2000));
        string sql = "SELECT a FROM t WHERE " + notChain + "c";

        SqlParseException ex = Assert.Throws<SqlParseException>(() => spark.Sql(sql));

        Assert.Equal(SqlParseErrorKind.SyntaxError, ex.ErrorKind);
    }

    [Fact]
    public void Sql_ModeratelyNestedParentheses_StillParses()
    {
        using SparkSession spark = NewSession();

        // Well within the bound: proves the guard does not reject legitimate nesting.
        const int depth = 50;
        string sql = "SELECT a FROM t WHERE " + new string('(', depth) + "b > 1" + new string(')', depth);

        DataFrame df = spark.Sql(sql);

        Assert.IsType<Filter>(((Project)df.Plan).Child);
    }

    // ---------------- SELECT ALL (default set quantifier) ----------------

    [Fact]
    public void Sql_SelectAll_IsEquivalentToSelectWithoutQuantifier()
    {
        using SparkSession spark = NewSession();

        DataFrame withAll = spark.Sql("SELECT ALL a FROM t");
        DataFrame without = spark.Sql("SELECT a FROM t");

        Assert.True(without.Plan.Equals(withAll.Plan));
        var project = (Project)withAll.Plan;
        Expression only = Assert.Single(project.ProjectList);
        Assert.True(Functions.Col("a").Expr.Equals(only));
    }

    [Fact]
    public void Sql_ColumnNamedAll_IsStillParsedAsColumn()
    {
        using SparkSession spark = NewSession();

        // 'all' followed by FROM is a column reference, not a quantifier.
        DataFrame df = spark.Sql("SELECT all FROM t");

        var project = (Project)df.Plan;
        Expression only = Assert.Single(project.ProjectList);
        var attribute = Assert.IsType<UnresolvedAttribute>(only);
        Assert.Equal(new[] { "all" }, attribute.NameParts);
    }

    // ---------------- SQL comments (line and block) are skipped ----------------

    [Fact]
    public void Sql_LineComment_IsSkipped()
    {
        using SparkSession spark = NewSession();

        DataFrame df = spark.Sql("SELECT a FROM t -- trailing comment\n");

        Assert.True(spark.Sql("SELECT a FROM t").Plan.Equals(df.Plan));
    }

    [Fact]
    public void Sql_BlockComment_IsSkipped()
    {
        using SparkSession spark = NewSession();

        DataFrame df = spark.Sql("SELECT a /* inline */ FROM t");

        Assert.True(spark.Sql("SELECT a FROM t").Plan.Equals(df.Plan));
    }

    [Fact]
    public void Sql_DoubleDash_NoLongerParsesAsArithmetic()
    {
        using SparkSession spark = NewSession();

        // '1--1' is 'SELECT 1' with the rest of the line commented out — NOT 1 - (-1) arithmetic.
        DataFrame df = spark.Sql("SELECT 1--1\nFROM t");

        var project = (Project)df.Plan;
        Expression only = Assert.Single(project.ProjectList);
        Literal literal = Assert.IsType<Literal>(only);
        Assert.True(Functions.Lit(1).Expr.Equals(literal));
    }

    // ---------------- AC3: multipart references converge across both front-ends (item 5) ----------------

    [Fact]
    public void Sql_QualifiedColumn_LowersToSameMultipartAttributeAsCol()
    {
        using SparkSession spark = NewSession();

        DataFrame df = spark.Sql("SELECT t.a FROM t");

        var project = (Project)df.Plan;
        Expression element = Assert.Single(project.ProjectList);
        var attribute = Assert.IsType<UnresolvedAttribute>(element);
        Assert.Equal(new[] { "t", "a" }, attribute.NameParts);

        // Both doors now build the identical multipart reference.
        Assert.True(Functions.Col("t.a").Expr.Equals(element));
    }

    [Fact]
    public void Sql_QualifiedColumn_ResolvesThroughSharedAnalyzerPipeline()
    {
        using SparkSession spark = NewSession();
        var schema = new StructType(new[]
        {
            new StructField("a", IntegerType.Instance, nullable: true),
        });
        var catalog = new LocalCatalog();
        catalog.Register("t", schema);
        var analyzer = new Analyzer(catalog);

        DataFrame df = spark.Sql("SELECT t.a FROM t");
        var resolved = Assert.IsType<Project>(analyzer.Resolve(df.Plan));

        Assert.True(resolved.Resolved);
        var reference = Assert.IsType<AttributeReference>(Assert.Single(resolved.ProjectList));
        Assert.Equal("a", reference.Name);
    }

    // ---------------- AC3: multi-column projection order is significant (item 7) ----------------

    [Fact]
    public void Sql_MultiColumnProject_PreservesOrderAndContent()
    {
        using SparkSession spark = NewSession();

        DataFrame df = spark.Sql("SELECT a, b FROM t");

        var expected = new Project(
            new Expression[] { new UnresolvedAttribute("a"), new UnresolvedAttribute("b") },
            new UnresolvedRelation(new[] { "t" }));
        Assert.True(expected.Equals(df.Plan));

        // Reversing the projection order must NOT compare equal — proves order is asserted.
        var reversed = new Project(
            new Expression[] { new UnresolvedAttribute("b"), new UnresolvedAttribute("a") },
            new UnresolvedRelation(new[] { "t" }));
        Assert.False(reversed.Equals(df.Plan));
    }

    [Fact]
    public void Sql_MultiColumnProjectWithAliases_PreservesOrderAndContent()
    {
        using SparkSession spark = NewSession();

        DataFrame df = spark.Sql("SELECT a AS x, b AS y FROM t");

        var project = (Project)df.Plan;
        Assert.Collection(
            project.ProjectList,
            e => Assert.True(Functions.Col("a").As("x").Expr.Equals(e)),
            e => Assert.True(Functions.Col("b").As("y").Expr.Equals(e)));
    }

    // ---------------- Diagnostics: position and chained-comparison hint ----------------

    [Fact]
    public void Sql_SyntaxError_ReportsOneBasedPositionOfOffendingToken()
    {
        using SparkSession spark = NewSession();

        // '#' is the 8th character (1-based) of the string; the deterministic message tags that offset.
        SqlParseException ex = Assert.Throws<SqlParseException>(() => spark.Sql("SELECT # FROM t"));

        Assert.Contains("position 8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_ChainedComparison_GivesParenthesizeHint()
    {
        using SparkSession spark = NewSession();

        SqlParseException ex = Assert.Throws<SqlParseException>(() => spark.Sql("SELECT a FROM t WHERE a = b = c"));

        Assert.Equal(SqlParseErrorKind.SyntaxError, ex.ErrorKind);
        Assert.Contains("chained comparison", ex.Message, StringComparison.Ordinal);
    }

    private sealed class ThrowingQueryExecutor : IQueryExecutor
    {
        public IReadOnlyList<Row> Collect(
            LogicalPlan analyzedPlan, ExecutionOptions options, ExecutionMetricsSink? metricsSink = null) =>
            throw new InvalidOperationException("The SQL door must not execute.");

        public long Count(
            LogicalPlan analyzedPlan, ExecutionOptions options, ExecutionMetricsSink? metricsSink = null) =>
            throw new InvalidOperationException("The SQL door must not execute.");

        public string ExplainPhysical(LogicalPlan analyzedPlan) =>
            throw new InvalidOperationException("The SQL door must not execute.");
    }
}
