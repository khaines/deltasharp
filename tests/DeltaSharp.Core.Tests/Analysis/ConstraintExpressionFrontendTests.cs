using System.Text;
using System.Threading;
using DeltaSharp.Analysis;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Sql;
using DeltaSharp.Types;
using Xunit;
namespace DeltaSharp.Core.Tests.Analysis;

// #579: the standalone constraint-expression frontend — parse a bare boolean Delta constraint and resolve it
// against a supplied schema, reusing the query path's name/type resolution. Prereq for #568 CHECK enforcement.
public sealed class ConstraintExpressionFrontendTests
{
    private static StructType Schema(params StructField[] fields) => new(fields);

    [Fact]
    public void ParseAndResolve_SimpleComparison_ResolvesToBooleanPredicate()
    {
        StructType schema = Schema(new StructField("id", LongType.Instance, nullable: false));

        Expression resolved = ConstraintExpressionFrontend.ParseAndResolve("id > 0", schema);

        // The predicate resolved (no exception — an unknown column or non-boolean result would have thrown)
        // and is boolean, so it is ready to evaluate against a matching batch on the write seam (#581).
        Assert.Equal(BooleanType.Instance, resolved.Type);
    }

    [Fact]
    public void ParseAndResolve_CompoundRangeConstraint_Resolves()
    {
        StructType schema = Schema(new StructField("amount", IntegerType.Instance, nullable: true));

        Expression resolved = ConstraintExpressionFrontend.ParseAndResolve("amount >= 0 AND amount < 100", schema);

        Assert.Equal(BooleanType.Instance, resolved.Type);
    }

    [Fact]
    public void ParseAndResolve_UnknownColumn_ThrowsAnalysisException()
    {
        StructType schema = Schema(new StructField("id", LongType.Instance, nullable: false));

        Assert.Throws<AnalysisException>(
            () => ConstraintExpressionFrontend.ParseAndResolve("missing > 0", schema));
    }

    [Fact]
    public void ParseAndResolve_NonBooleanPredicate_ThrowsAnalysisException()
    {
        StructType schema = Schema(new StructField("id", LongType.Instance, nullable: false));

        // `id + 1` is a bigint, not a boolean — the same RequireBooleanCondition rule WHERE uses rejects it.
        Assert.Throws<AnalysisException>(
            () => ConstraintExpressionFrontend.ParseAndResolve("id + 1", schema));
    }

    [Fact]
    public void ParseConstraintExpression_TrailingTokens_ThrowsSqlParseException()
    {
        // A constraint is a single expression, never a statement — trailing tokens are a syntax error.
        Assert.Throws<SqlParseException>(() => SqlParser.ParseConstraintExpression("id > 0 SELECT"));
    }

    [Fact]
    public void ParseConstraintExpression_Malformed_ThrowsSqlParseException()
    {
        Assert.Throws<SqlParseException>(() => SqlParser.ParseConstraintExpression("id >"));
    }

    [Fact]
    public void ParseConstraintExpression_PathologicalParenNesting_ThrowsSqlParseException()
    {
        // A constraint string is UNTRUSTED (it comes from a table's metaData). ~2000 nested parens each descend
        // the parser's precedence ladder but build NO node, outrunning the node-depth counter — so
        // RuntimeHelpers.EnsureSufficientExecutionStack must still deflect it into a CATCHABLE SqlParseException
        // (mirroring Parse's hardening), never an (uncatchable) StackOverflowException that crashes the driver
        // process. Mirrors SqlDoorTests.Sql_DeeplyNestedParentheses_ThrowsCaughtSqlParseException_NotStackOverflow.
        const int depth = 2000;
        string expr = new string('(', depth) + "id > 0" + new string(')', depth);

        SqlParseException ex = Assert.Throws<SqlParseException>(() => SqlParser.ParseConstraintExpression(expr));
        Assert.Contains("nesting too deep", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseConstraintExpression_DeepNestingOnSmallStack_TranslatesStackGuard_ToSqlParseException()
    {
        // The finding this pins: on a reduced-stack worker thread (constrained executor hosts run smaller than
        // the ~1 MB default), a VALID-DEPTH but paren-heavy constraint (999 parens, UNDER the 1000 node-depth
        // cap, so the node-depth counter can NOT catch it) trips the PHYSICAL stack guard
        // (RuntimeHelpers.EnsureSufficientExecutionStack) first, throwing InsufficientExecutionStackException.
        // ParseConstraintExpression must translate THAT to a catchable SqlParseException (mirroring Parse) — a
        // caller catching SqlParseException must not see a leaked InsufficientExecutionStackException. Run on a
        // 256 KB stack (matches the council's reproducing probe); the guard is catchable, so the host survives.
        string expr = new string('(', 999) + "id > 0" + new string(')', 999);
        Exception? captured = null;
        var worker = new Thread(
            () =>
            {
                try
                {
                    SqlParser.ParseConstraintExpression(expr);
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
            },
            maxStackSize: 256 * 1024);
        worker.Start();
        worker.Join();

        Assert.IsType<SqlParseException>(captured);
    }

    [Fact]
    public void ParseAndResolve_MultipartReference_ThrowsSqlParseException()
    {
        // Red-team #587 finding: nested field access (#580) is not landed, so a multipart reference `s.f`
        // currently RESOLVES to a TOP-LEVEL column `f` — silently enforcing the WRONG constraint — when the
        // schema has both a struct `s` (with field `f`) AND a top-level `f`. Must be rejected fail-closed.
        StructType structType = new(new[] { new StructField("f", IntegerType.Instance, nullable: true) });
        StructType schema = Schema(
            new StructField("s", structType, nullable: true),
            new StructField("f", IntegerType.Instance, nullable: true));

        SqlParseException ex = Assert.Throws<SqlParseException>(
            () => ConstraintExpressionFrontend.ParseAndResolve("s.f > 0", schema));
        Assert.Contains("multipart reference", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseAndResolve_ConstraintDeeperThanBound_ThrowsSqlParseException()
    {
        // Red-team #587 finding (Critical): a valid-depth-but-deeply-nested constraint (under the parser's 1000
        // node-depth cap, so it PARSES) would drive the analyzer's recursive resolve deep enough to exhaust a
        // small worker-thread stack — an uncatchable StackOverflow crash on hostile constraint metadata. A real
        // CHECK constraint is shallow, so the frontend bounds untrusted constraint depth and rejects an
        // over-deep one BEFORE resolve, deterministically on any stack (no reliance on stack-size behavior).
        var sb = new StringBuilder("id");
        for (int i = 0; i < 200; i++)
        {
            sb.Append(" + 1"); // ~depth 201, over the frontend's depth bound but well under the 1000 parser cap
        }

        sb.Append(" > 0");
        StructType schema = Schema(new StructField("id", LongType.Instance, nullable: false));

        SqlParseException ex = Assert.Throws<SqlParseException>(
            () => ConstraintExpressionFrontend.ParseAndResolve(sb.ToString(), schema));
        Assert.Contains("nests deeper than", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseAndResolve_ModeratelyDeepButWithinBound_Resolves()
    {
        // No over-reject: a constraint comfortably within the depth bound (a 40-term arithmetic predicate)
        // resolves to a boolean. Proves the bound rejects only genuinely pathological nesting.
        var sb = new StringBuilder("id");
        for (int i = 0; i < 40; i++)
        {
            sb.Append(" + 1");
        }

        sb.Append(" > 0");
        StructType schema = Schema(new StructField("id", LongType.Instance, nullable: false));

        Expression resolved = ConstraintExpressionFrontend.ParseAndResolve(sb.ToString(), schema);
        Assert.Equal(BooleanType.Instance, resolved.Type);
    }
}
