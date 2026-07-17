using System.Collections.Generic;
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
    /// <exception cref="SqlParseException">The constraint text is malformed, has trailing tokens, nests too
    /// deeply (either the parser's node-depth cap or the frontend's untrusted-constraint depth bound), or
    /// contains a multipart / nested-field reference (<c>s.f</c>), which is not supported until #580.</exception>
    /// <exception cref="AnalysisException">A referenced column is unknown, a type does not match, or the
    /// predicate is not boolean.</exception>
    internal static Expression ParseAndResolve(string expression, StructType schema) =>
        ParseResolveWithInput(expression, schema).Predicate;

    /// <summary>
    /// Parses <paramref name="expression"/> as a bare boolean constraint, resolves it against
    /// <paramref name="schema"/>, and returns both the resolved predicate AND the resolved input attributes
    /// (the synthetic relation's <see cref="LocalRelation.Output"/>, in schema order). The write seam (#581)
    /// needs the input attributes to translate the predicate's references into ordinal
    /// <c>ColumnReference</c>s for the columnar evaluator.
    /// </summary>
    /// <param name="expression">The bare boolean constraint text (no surrounding <c>SELECT</c>).</param>
    /// <param name="schema">The table schema the constraint's column references resolve against.</param>
    /// <returns>The resolved boolean predicate and the resolved input attribute list (schema order).</returns>
    /// <exception cref="SqlParseException">The constraint text is malformed, has trailing tokens, or nests
    /// deeper than the parser's node-depth cap or the frontend's untrusted-constraint depth bound.</exception>
    /// <exception cref="AnalysisException">A referenced column is unknown, a type does not match, or the
    /// predicate is not boolean.</exception>
    internal static (Expression Predicate, IReadOnlyList<AttributeReference> Input) ParseResolveWithInput(
        string expression, StructType schema)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(schema);

        Expression parsed = SqlParser.ParseConstraintExpression(expression);

        // Pre-resolve validation (fail closed on UNTRUSTED constraint text before the analyzer touches it):
        // bound the expression DEPTH. A real CHECK constraint / invariant is a shallow predicate, but a hostile
        // deeply-nested constraint would drive the analyzer's recursive TransformUp deep enough to exhaust a
        // small worker-thread stack (an uncatchable StackOverflow). The whole transform infrastructure is
        // stack-size-sensitive at high depth (the node-depth cap is 1000; DeterminismTests exercises 900), so
        // rather than reach in there, bound the UNTRUSTED constraint to MaxConstraintExpressionDepth — 10x a
        // realistic constraint, well under the parser cap, and safe to resolve on a small (>=256 KB) stack.
        // (A MULTIPART reference `s.f` is now RESOLVED as nested field access — GetStructField, #580 — rather
        // than rejected; the analyzer binds it to the struct field, or fails closed if the base is not a struct.)
        ValidateConstraintShape(parsed);

        // Resolve the bare predicate the SAME way WHERE is resolved: analyze Filter(expr, LocalRelation(schema)).
        // LocalRelation carries the schema inline (no catalog table lookup), so the empty catalog is never
        // consulted; the analyzer mints the relation's Output attributes from the schema and binds the
        // predicate's references + types against them, and RequireBooleanCondition rejects a non-boolean result.
        var plan = new Filter(parsed, new LocalRelation(schema, Array.Empty<Row>()));
        var resolved = (Filter)new Analyzer(new LocalCatalog()).Resolve(plan);
        IReadOnlyList<AttributeReference> input = ((LocalRelation)resolved.Child).Output
            ?? throw new InvalidOperationException("Resolved constraint relation exposes no output attributes.");
        return (resolved.Condition, input);
    }

    /// <summary>The maximum nesting depth a constraint expression may reach before resolution. A real Delta
    /// CHECK constraint / invariant is a shallow predicate; this bound (10x a realistic constraint, well below
    /// the parser's node-depth cap) keeps a hostile deeply-nested constraint from exhausting the analyzer's
    /// recursive transform on a small worker-thread stack, while accepting every realistic constraint.</summary>
    private const int MaxConstraintExpressionDepth = 100;

    // Fail-closes an UNTRUSTED constraint BEFORE the analyzer sees it: rejects any expression nesting deeper
    // than MaxConstraintExpressionDepth. Walked ITERATIVELY (explicit stack of (node, depth)) so a deep
    // constraint cannot overflow this pre-resolve scan. A multipart reference `s.f` is NOT rejected here — it
    // resolves as nested field access (GetStructField, #580); the analyzer fails closed if the base is not a
    // struct or the field is absent.
    private static void ValidateConstraintShape(Expression root)
    {
        var pending = new Stack<(Expression Node, int Depth)>();
        pending.Push((root, 1));
        while (pending.Count > 0)
        {
            (Expression node, int depth) = pending.Pop();

            if (depth > MaxConstraintExpressionDepth)
            {
                throw new SqlParseException(
                    $"Constraint expression nests deeper than {MaxConstraintExpressionDepth} levels; a CHECK "
                    + "constraint must be a shallow predicate over the table's columns.");
            }

            foreach (Expression child in node.Children)
            {
                pending.Push((child, depth + 1));
            }
        }
    }
}
