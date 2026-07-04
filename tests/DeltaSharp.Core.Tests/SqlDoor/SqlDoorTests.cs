using System;
using System.Collections.Generic;
using DeltaSharp.Execution;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
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
    [InlineData("SELECT a FROM t JOIN u ON t.a = u.a", "JOIN")]
    [InlineData("SELECT a FROM t, u", "comma-separated table list (implicit join)")]
    [InlineData("SELECT count(a) FROM t", "function call")]
    [InlineData("SELECT a FROM t GROUP BY a", "GROUP BY")]
    [InlineData("SELECT a FROM t ORDER BY a", "ORDER BY")]
    [InlineData("SELECT a FROM t HAVING a > 1", "HAVING")]
    [InlineData("SELECT a FROM t LIMIT 5", "LIMIT")]
    [InlineData("SELECT DISTINCT a FROM t", "SELECT DISTINCT")]
    [InlineData("SELECT a FROM (SELECT a FROM t)", "subquery")]
    [InlineData("SELECT a FROM t UNION SELECT a FROM u", "set operation (UNION/INTERSECT/EXCEPT)")]
    [InlineData("INSERT INTO t VALUES (1)", "INSERT")]
    [InlineData("CREATE TABLE t (a INT)", "CREATE")]
    public void Sql_UnsupportedConstruct_ThrowsUnsupportedFeature_NamingConstruct_WithoutExecuting(
        string sql, string expectedConstruct)
    {
        using SparkSession spark = NewSession();
        spark.QueryExecutor = new ThrowingQueryExecutor();

        SqlParseException ex = Assert.Throws<SqlParseException>(() => spark.Sql(sql));

        Assert.Equal(SqlParseErrorKind.UnsupportedFeature, ex.ErrorKind);
        Assert.Equal(expectedConstruct, ex.Construct);
        Assert.Contains(expectedConstruct, ex.Message, StringComparison.Ordinal);
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
