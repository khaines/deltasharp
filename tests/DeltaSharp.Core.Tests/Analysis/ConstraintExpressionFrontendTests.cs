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
}
