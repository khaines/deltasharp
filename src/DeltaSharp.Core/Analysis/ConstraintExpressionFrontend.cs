using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Sql;
using DeltaSharp.Types;

namespace DeltaSharp.Analysis;

/// <summary>
/// The standalone <b>constraint-expression frontend</b> (#579, prereq for #568 per-row CHECK-constraint /
/// invariant enforcement): turns a Delta constraint string (for example a
/// <c>delta.constraints.&lt;name&gt;</c> value like <c>amount &gt;= 0 AND amount &lt; 100</c>, or a column
/// invariant) into a <b>resolved, evaluatable</b> <see cref="Expression"/> over a supplied table
/// <see cref="StructType"/> — with no surrounding <c>SELECT</c> and no catalog table.
/// </summary>
/// <remarks>
/// It composes the two pieces the write path lacked: <see cref="SqlParser.ParseConstraintExpression"/> (bare
/// boolean parse) and the analyzer's reference/type resolution. Resolution is performed by wrapping the parsed
/// predicate in a synthetic <c>Filter(expr, LocalRelation(schema))</c> and running the standard
/// <see cref="Analyzer"/>: this reuses the exact name-resolution, implicit-coercion, and
/// <c>RequireBooleanCondition</c> rules the query path uses, so an unknown column, a type mismatch, or a
/// non-boolean predicate is rejected identically to <c>WHERE</c>. The returned expression references the
/// schema's attributes and is ready for <c>ExpressionEvaluator</c> on the write seam (#581). Evaluating the
/// resolved expression (per-row enforcement) and wiring it into the writer are out of scope here.
/// <para><b>M1 grammar scope.</b> The parser accepts the M1 boolean-expression subset — comparisons,
/// <c>AND</c>/<c>OR</c>/<c>NOT</c>, arithmetic, parentheses, and int/double/string/bool literals. Common Delta
/// CHECK-constraint idioms OUTSIDE that subset — <c>IS [NOT] NULL</c>, <c>IN</c>, <c>BETWEEN</c>, <c>LIKE</c>,
/// scalar function calls, unary minus — currently surface as a deterministic
/// <see cref="SqlParseException"/> (<c>UnsupportedFeature</c>), never a crash; the fuller grammar lands with the
/// ANTLR SQL frontend (EPIC-07), and #568 inherits exactly this initial surface until then.</para>
/// </remarks>
internal static class ConstraintExpressionFrontend
{
    /// <summary>
    /// Parses <paramref name="expression"/> as a bare boolean constraint and resolves it against
    /// <paramref name="schema"/>, returning the resolved predicate expression.
    /// </summary>
    /// <param name="expression">The bare boolean constraint text (no surrounding <c>SELECT</c>).</param>
    /// <param name="schema">The table schema the constraint's column references resolve against.</param>
    /// <returns>The resolved boolean <see cref="Expression"/> (attribute references bound to
    /// <paramref name="schema"/>), ready to evaluate against a matching batch.</returns>
    /// <exception cref="SqlParseException">The constraint text is malformed, nests too deeply, or has
    /// trailing tokens.</exception>
    /// <exception cref="AnalysisException">A referenced column is unknown, a type does not match, or the
    /// predicate is not boolean.</exception>
    internal static Expression ParseAndResolve(string expression, StructType schema)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(schema);

        Expression parsed = SqlParser.ParseConstraintExpression(expression);

        // Resolve the bare predicate the SAME way WHERE is resolved: analyze Filter(expr, LocalRelation(schema)).
        // LocalRelation carries the schema inline (no catalog table lookup), so the empty catalog is never
        // consulted; the analyzer mints the relation's Output attributes from the schema and binds the
        // predicate's references + types against them, and RequireBooleanCondition rejects a non-boolean result.
        var plan = new Filter(parsed, new LocalRelation(schema, Array.Empty<Row>()));
        LogicalPlan resolved = new Analyzer(new LocalCatalog()).Resolve(plan);

        return ((Filter)resolved).Condition;
    }
}
