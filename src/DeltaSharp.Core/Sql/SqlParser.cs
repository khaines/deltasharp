using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Sql;

/// <summary>
/// The hand-written recursive-descent parser for the M1 SQL door (STORY-04.1.3 / #159). It lowers a
/// SQL string into the <b>same</b> unresolved logical-plan and expression IR the DataFrame API builds
/// (<see cref="Project"/>, <see cref="Filter"/>, <see cref="UnresolvedRelation"/>,
/// <see cref="UnresolvedAttribute"/>, <see cref="Literal"/>, <see cref="BinaryComparison"/>,
/// <see cref="BinaryArithmetic"/>, <see cref="And"/>/<see cref="Or"/>/<see cref="Not"/>) so SQL and
/// DataFrame plans converge after lowering (AC3). It resolves nothing (no catalog, no schema) — the
/// analyzer binds names later — and executes nothing (AC1). Supported subset:
/// <code>
/// statement      := SELECT selectList FROM relation [ WHERE booleanExpr ] EOF
/// selectList     := '*' | selectItem (',' selectItem)*
/// selectItem     := expr [ [AS] identifier ] | qualifiedStar
/// relation       := identifier ('.' identifier)*
/// booleanExpr    := orExpr
/// orExpr         := andExpr (OR andExpr)*
/// andExpr        := notExpr (AND notExpr)*
/// notExpr        := NOT notExpr | comparison
/// comparison     := additive (compOp additive)?
/// additive       := multiplicative (('+'|'-') multiplicative)*
/// multiplicative := unary (('*'|'/'|'%') unary)*
/// unary          := ('+'|'-') numericLiteral | primary
/// primary        := literal | columnRef | '(' expr ')'
/// </code>
/// Anything outside this subset raises a deterministic <see cref="SqlParseException"/> at parse time
/// (AC2). The ANTLR4-grammar frontend of ADR-0007 supersedes this focused door in EPIC-07.
/// </summary>
internal sealed class SqlParser
{
    /// <summary>
    /// The maximum expression-nesting recursion depth the parser accepts. Aligned with
    /// <see cref="TreeNode{TNode}.MaxDepth"/> (the node-tree depth bound). Deeply nested inputs —
    /// e.g. thousands of parentheses or a long <c>NOT NOT …</c> chain — would otherwise recurse
    /// unboundedly and overflow the (small, ~1&#160;MB under gRPC/Kestrel) worker-thread stack,
    /// crashing the whole process with an <b>uncatchable</b> <see cref="System.StackOverflowException"/>.
    /// Note parenthesized groups build no node (they return the inner expression unwrapped), so the
    /// construction-time <see cref="TreeNode{TNode}"/> guard cannot see them — this explicit counter
    /// is what makes over-deep input a deterministic, catchable <see cref="SqlParseException"/>.
    /// </summary>
    private const int MaxExpressionDepth = TreeNode<LogicalPlan>.MaxDepth;

    private readonly IReadOnlyList<SqlToken> _tokens;
    private int _pos;
    private int _depth;

    private SqlParser(IReadOnlyList<SqlToken> tokens)
    {
        _tokens = tokens;
    }

    /// <summary>Parses <paramref name="sql"/> into an unresolved logical plan.</summary>
    /// <param name="sql">The SQL text to parse and lower.</param>
    /// <returns>The unresolved logical plan the SQL lowers to.</returns>
    /// <exception cref="SqlParseException">The text is malformed
    /// (<see cref="SqlParseErrorKind.SyntaxError"/>) or uses a construct outside the M1 subset
    /// (<see cref="SqlParseErrorKind.UnsupportedFeature"/>).</exception>
    public static LogicalPlan Parse(string sql)
    {
        IReadOnlyList<SqlToken> tokens = SqlLexer.Tokenize(sql);
        try
        {
            return new SqlParser(tokens).ParseStatement();
        }
        catch (PlanDepthExceededException ex)
        {
            // Belt-and-suspenders: the recursion-depth guard below should fire first, but if an
            // over-deep tree ever reaches node construction, translate the INTERNAL plan-depth
            // exception into the public, catchable SqlParseException the door promises (AC2).
            throw new SqlParseException(
                "Syntax error: expression nesting too deep to parse.", ex);
        }
        catch (InsufficientExecutionStackException ex)
        {
            // Defense-in-depth for inputs whose *physical* call-stack cost outruns the node-depth
            // counter (e.g. thousands of parentheses, each of which descends the full precedence
            // ladder but builds no node). RuntimeHelpers.EnsureSufficientExecutionStack() fires while
            // there is still headroom, so this is a deterministic, catchable failure — never an
            // uncatchable StackOverflowException that would crash the whole driver process.
            throw new SqlParseException(
                "Syntax error: expression nesting too deep to parse.", ex);
        }
    }

    /// <summary>
    /// Parses a <b>bare boolean expression</b> — a Delta CHECK constraint / column invariant such as
    /// <c>id &gt; 0</c> or <c>amount &gt;= 0 AND amount &lt; 100</c>, with no surrounding
    /// <c>SELECT … FROM …</c> — into an <b>unresolved</b> expression tree (#579, prereq for #568). Resolve
    /// it against a table schema with <see cref="DeltaSharp.Analysis.ConstraintExpressionFrontend"/>. The whole
    /// input must be a single expression; any trailing tokens are a syntax error (a constraint string is one
    /// predicate, never a statement).
    /// </summary>
    /// <param name="expression">The bare boolean-expression text.</param>
    /// <returns>The unresolved expression tree the text lowers to.</returns>
    /// <exception cref="SqlParseException">The text is malformed, nests too deeply, or carries trailing tokens
    /// after a complete expression.</exception>
    public static Expression ParseConstraintExpression(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        IReadOnlyList<SqlToken> tokens = SqlLexer.Tokenize(expression);
        var parser = new SqlParser(tokens);
        try
        {
            Expression parsed = parser.ParseExpression();
            if (parser.Current.Kind != SqlTokenKind.EndOfInput)
            {
                throw SqlParseException.Syntax(
                    $"unexpected trailing input '{parser.Current.Text}' after the constraint expression; a "
                    + "constraint is a single boolean expression, not a statement",
                    parser.Current.Position);
            }

            return parsed;
        }
        catch (PlanDepthExceededException ex)
        {
            throw new SqlParseException("Syntax error: expression nesting too deep to parse.", ex);
        }
        catch (InsufficientExecutionStackException ex)
        {
            // Defense-in-depth (mirrors Parse): a constraint whose PHYSICAL call-stack cost outruns the
            // node-depth counter — e.g. thousands of parentheses, each descending the full precedence ladder
            // but building no node — trips RuntimeHelpers.EnsureSufficientExecutionStack() while there is still
            // headroom, so it surfaces as a deterministic, catchable SqlParseException, never an uncatchable
            // StackOverflowException that would crash the driver process on a hostile constraint string.
            throw new SqlParseException("Syntax error: expression nesting too deep to parse.", ex);
        }
    }

    private SqlToken Current => _tokens[_pos];

    private LogicalPlan ParseStatement()
    {
        SqlToken first = Current;
        if (first.Kind != SqlTokenKind.Select)
        {
            if (first.Kind == SqlTokenKind.EndOfInput)
            {
                throw SqlParseException.Syntax("expected a SELECT statement but found end of input", first.Position);
            }

            string? construct = MapStatementKeyword(first);
            throw construct is not null
                ? SqlParseException.Unsupported(construct, first.Position)
                : SqlParseException.Syntax($"expected SELECT but found '{first.Text}'", first.Position);
        }

        Advance();
        RejectSetQuantifier();

        IReadOnlyList<Expression> projectList = ParseSelectList();

        SqlToken fromToken = Current;
        if (fromToken.Kind != SqlTokenKind.From)
        {
            // A recognizable clause/statement keyword standing where FROM is expected ('SELECT a
            // LIMIT 10', 'SELECT a GROUP BY b') is a named UnsupportedFeature, not an opaque token
            // error. ParseSelectItem deliberately leaves such an unquoted keyword unconsumed (it is
            // not an implicit alias) precisely so it surfaces here with the right diagnostic.
            string? unsupported = MapTrailingConstruct(fromToken) ?? MapStatementKeyword(fromToken);
            if (unsupported is not null)
            {
                throw SqlParseException.Unsupported(unsupported, fromToken.Position);
            }

            throw SqlParseException.Syntax(
                fromToken.Kind == SqlTokenKind.EndOfInput
                    ? "expected FROM but found end of input"
                    : $"expected FROM but found '{fromToken.Text}'",
                fromToken.Position);
        }

        Advance();
        LogicalPlan plan = ParseRelation();

        if (Current.Kind == SqlTokenKind.Where)
        {
            Advance();
            Expression predicate = ParseExpression();
            plan = new Filter(predicate, plan);
        }

        ExpectEnd();
        return new Project(projectList, plan);
    }

    private void RejectSetQuantifier()
    {
        // At most ONE leading set quantifier is allowed after SELECT. ALL is the DEFAULT quantifier
        // (Spark parity): 'SELECT ALL a' means 'SELECT a'. Consume and ignore it — but only when a
        // select item actually follows, so a column literally named 'all' ('SELECT all FROM t') is
        // still parsed as a column reference.
        bool consumedAll = false;
        if (IsSetQuantifier("ALL") && StartsSelectItem(Peek(1)))
        {
            Advance();
            consumedAll = true;
        }

        // DISTINCT as a set quantifier is recognizable SQL the M1 door does not implement (DISTINCT
        // needs the Distinct operator + dedup semantics). Reject it whether it leads the select list
        // ('SELECT DISTINCT a') or follows a consumed ALL ('SELECT ALL DISTINCT a'). In the latter
        // case it must be in quantifier position (another select item follows) so it never slips
        // through ParseSelectList as an implicitly-aliased column reference.
        if (IsSetQuantifier("DISTINCT") && (!consumedAll || StartsSelectItem(Peek(1))))
        {
            throw SqlParseException.Unsupported("SELECT_DISTINCT", Current.Position);
        }

        // A SECOND set quantifier after a consumed ALL ('SELECT ALL ALL a') is malformed: SQL allows
        // at most one leading set quantifier. Guard it explicitly so it cannot be mis-parsed as an
        // implicitly-aliased column ('ALL' aliased to the following item).
        if (consumedAll && IsSetQuantifier("ALL") && StartsSelectItem(Peek(1)))
        {
            throw SqlParseException.Syntax(
                "duplicate set quantifier after SELECT; at most one of ALL or DISTINCT is allowed",
                Current.Position);
        }
    }

    private bool IsSetQuantifier(string keyword) =>
        Current.Kind == SqlTokenKind.Identifier
        && !Current.IsQuoted
        && string.Equals(Current.Text, keyword, System.StringComparison.OrdinalIgnoreCase);

    private static bool StartsSelectItem(SqlToken token) =>
        token.Kind is not (SqlTokenKind.From
            or SqlTokenKind.Comma
            or SqlTokenKind.As
            or SqlTokenKind.EndOfInput);

    private IReadOnlyList<Expression> ParseSelectList()
    {
        var items = new List<Expression>();
        items.Add(ParseSelectItem());
        while (Current.Kind == SqlTokenKind.Comma)
        {
            Advance();
            items.Add(ParseSelectItem());
        }

        return items;
    }

    private Expression ParseSelectItem()
    {
        // A bare '*' (unqualified star) is only valid as a select item, never as an expression
        // operand, so recognize it here before falling through to expression parsing.
        if (Current.Kind == SqlTokenKind.Star)
        {
            Advance();
            return new UnresolvedStar();
        }

        Expression expr = ParseExpression();

        // A qualified star ('t.*') is produced by the column-reference parser and cannot be aliased.
        if (expr is UnresolvedStar)
        {
            return expr;
        }

        if (Current.Kind == SqlTokenKind.As)
        {
            Advance();
            return new Alias(expr, ExpectAliasName());
        }

        // Implicit alias: 'SELECT a b' aliases 'a' as 'b' (Spark parity). An unquoted clause/statement
        // keyword (LIMIT/GROUP/ORDER/HAVING/JOIN/UNION/…) is NOT an implicit alias: leaving it
        // unconsumed lets the clause-position diagnostic name it as an UnsupportedFeature ('SELECT a
        // LIMIT 10') instead of eating it as 'a AS LIMIT' and then failing on the following token with
        // a misleading error. A *quoted* '`limit`' is a delimited literal name and remains a valid
        // implicit alias (MapTrailingConstruct/MapStatementKeyword ignore quoted identifiers).
        if (Current.Kind == SqlTokenKind.Identifier
            && MapTrailingConstruct(Current) is null
            && MapStatementKeyword(Current) is null)
        {
            string alias = Current.Text;
            Advance();
            return new Alias(expr, alias);
        }

        return expr;
    }

    private string ExpectAliasName()
    {
        if (Current.Kind != SqlTokenKind.Identifier)
        {
            throw SqlParseException.Syntax(
                $"expected an alias name after AS but found '{Describe(Current)}'", Current.Position);
        }

        string name = Current.Text;
        Advance();
        return name;
    }

    private LogicalPlan ParseRelation()
    {
        if (Current.Kind == SqlTokenKind.LParen)
        {
            throw SqlParseException.Unsupported("SUBQUERY", Current.Position);
        }

        var parts = new List<string>();
        if (Current.Kind != SqlTokenKind.Identifier)
        {
            throw SqlParseException.Syntax(
                $"expected a table name but found '{Describe(Current)}'", Current.Position);
        }

        parts.Add(Current.Text);
        Advance();

        while (Current.Kind == SqlTokenKind.Dot)
        {
            Advance();
            if (Current.Kind != SqlTokenKind.Identifier)
            {
                throw SqlParseException.Syntax(
                    $"expected an identifier after '.' but found '{Describe(Current)}'", Current.Position);
            }

            parts.Add(Current.Text);
            Advance();
        }

        return new UnresolvedRelation(parts);
    }

    private void ExpectEnd()
    {
        SqlToken token = Current;
        if (token.Kind == SqlTokenKind.EndOfInput)
        {
            return;
        }

        string? construct = MapTrailingConstruct(token);
        throw construct is not null
            ? SqlParseException.Unsupported(construct, token.Position)
            : SqlParseException.Syntax($"unexpected '{Describe(token)}' after the query", token.Position);
    }

    // ---------------------------------------------------------------------------------------------
    // Expression grammar (precedence-climbing recursive descent).
    // ---------------------------------------------------------------------------------------------

    private Expression ParseExpression() => ParseOr();

    private Expression ParseOr()
    {
        Expression left = ParseAnd();
        while (Current.Kind == SqlTokenKind.Or)
        {
            Advance();
            left = new Or(left, ParseAnd());
        }

        return left;
    }

    private Expression ParseAnd()
    {
        Expression left = ParseNot();
        while (Current.Kind == SqlTokenKind.And)
        {
            Advance();
            left = new And(left, ParseNot());
        }

        return left;
    }

    private Expression ParseNot()
    {
        if (Current.Kind == SqlTokenKind.Not)
        {
            int position = Current.Position;
            Advance();
            EnterRecursion(position);
            try
            {
                return new Not(ParseNot());
            }
            finally
            {
                _depth--;
            }
        }

        return ParseComparison();
    }

    private Expression ParseComparison()
    {
        Expression left = ParseAdditive();

        // A predicate-position NOT prefixing IN/LIKE/BETWEEN ('a NOT IN (…)') is a recognizable-but-
        // unsupported predicate. NOT is a keyword token, so it would otherwise escape the identifier-
        // based MapPredicateKeyword hook below and be left trailing after the query as an opaque
        // "unexpected 'NOT'" SyntaxError — surface the named UnsupportedFeature hint here instead.
        // (A LEADING boolean NOT — 'WHERE NOT a = b' — is consumed by ParseNot before reaching here,
        // so this never intercepts logical negation.)
        string? negatedPredicate = MapNotPredicateKeyword(Current, Peek(1));
        if (negatedPredicate is not null)
        {
            throw SqlParseException.Unsupported(negatedPredicate, Current.Position);
        }

        // Recognizable-but-unsupported predicates (IS [NOT] NULL, IN, LIKE, BETWEEN) surface here as a
        // named UnsupportedFeature rather than as a misleading trailing-token SyntaxError.
        string? predicate = MapPredicateKeyword(Current);
        if (predicate is not null)
        {
            throw SqlParseException.Unsupported(predicate, Current.Position);
        }

        if (TryComparisonOperator(Current.Kind, out ComparisonOperator op))
        {
            Advance();
            Expression right = ParseAdditive();

            // Comparisons are non-associative in the M1 grammar; 'a = b = c' is a common mistake with
            // a clearer hint than an opaque "unexpected '=' after the query".
            if (TryComparisonOperator(Current.Kind, out _))
            {
                throw SqlParseException.Syntax(
                    "chained comparison is not supported; parenthesize the operands", Current.Position);
            }

            return new BinaryComparison(left, right, op);
        }

        return left;
    }

    private Expression ParseAdditive()
    {
        Expression left = ParseMultiplicative();
        while (true)
        {
            ArithmeticOperator op;
            if (Current.Kind == SqlTokenKind.Plus)
            {
                op = ArithmeticOperator.Add;
            }
            else if (Current.Kind == SqlTokenKind.Minus)
            {
                op = ArithmeticOperator.Subtract;
            }
            else
            {
                return left;
            }

            Advance();
            left = new BinaryArithmetic(left, ParseMultiplicative(), op);
        }
    }

    private Expression ParseMultiplicative()
    {
        Expression left = ParseUnary();
        while (true)
        {
            ArithmeticOperator op;
            if (Current.Kind == SqlTokenKind.Star)
            {
                op = ArithmeticOperator.Multiply;
            }
            else if (Current.Kind == SqlTokenKind.Slash)
            {
                op = ArithmeticOperator.Divide;
            }
            else if (Current.Kind == SqlTokenKind.Percent)
            {
                op = ArithmeticOperator.Remainder;
            }
            else
            {
                return left;
            }

            Advance();
            left = new BinaryArithmetic(left, ParseUnary(), op);
        }
    }

    private Expression ParseUnary()
    {
        // Unary +/- is supported only directly in front of a numeric literal (folded into the literal
        // so 'WHERE b > -1' lowers to the same Literal the DataFrame API's Lit(-1) builds). A general
        // arithmetic negation node is out of the M1 subset.
        if (Current.Kind == SqlTokenKind.Minus || Current.Kind == SqlTokenKind.Plus)
        {
            bool negative = Current.Kind == SqlTokenKind.Minus;
            SqlToken sign = Current;
            Advance();
            if (Current.Kind == SqlTokenKind.IntegerLiteral || Current.Kind == SqlTokenKind.DecimalLiteral)
            {
                return ParseNumericLiteral(negative);
            }

            throw SqlParseException.Unsupported("UNARY_MINUS", sign.Position);
        }

        return ParsePrimary();
    }

    private Expression ParsePrimary()
    {
        SqlToken token = Current;
        switch (token.Kind)
        {
            case SqlTokenKind.IntegerLiteral:
            case SqlTokenKind.DecimalLiteral:
                return ParseNumericLiteral(negative: false);

            case SqlTokenKind.StringLiteral:
                Advance();
                return Literal.OfString(token.Text);

            case SqlTokenKind.True:
                Advance();
                return Literal.OfBoolean(true);

            case SqlTokenKind.False:
                Advance();
                return Literal.OfBoolean(false);

            case SqlTokenKind.Null:
                Advance();
                return Literal.Null(NullType.Instance);

            case SqlTokenKind.LParen:
                Advance();
                if (Current.Kind == SqlTokenKind.Select)
                {
                    throw SqlParseException.Unsupported("SUBQUERY", token.Position);
                }

                EnterRecursion(token.Position);
                try
                {
                    Expression inner = ParseExpression();
                    Expect(SqlTokenKind.RParen, ")");
                    return inner;
                }
                finally
                {
                    _depth--;
                }

            case SqlTokenKind.Identifier:
                return ParseColumnReference();

            default:
                throw SqlParseException.Syntax(
                    $"expected an expression but found '{Describe(token)}'", token.Position);
        }
    }

    private Expression ParseColumnReference()
    {
        SqlToken start = Current;

        // 'name(' is a function call, which the M1 door does not evaluate (aggregates/scalar
        // functions arrive with the analyzer's function registry usage in EPIC-07).
        if (Peek(1).Kind == SqlTokenKind.LParen)
        {
            throw SqlParseException.Unsupported("FUNCTION_CALL", start.Position);
        }

        var parts = new List<string> { start.Text };
        Advance();

        while (Current.Kind == SqlTokenKind.Dot)
        {
            Advance();
            if (Current.Kind == SqlTokenKind.Star)
            {
                Advance();
                return new UnresolvedStar(parts);
            }

            if (Current.Kind != SqlTokenKind.Identifier)
            {
                throw SqlParseException.Syntax(
                    $"expected an identifier after '.' but found '{Describe(Current)}'", Current.Position);
            }

            parts.Add(Current.Text);
            Advance();
        }

        return new UnresolvedAttribute(parts);
    }

    private Expression ParseNumericLiteral(bool negative)
    {
        SqlToken token = Current;
        Advance();

        if (token.Kind == SqlTokenKind.DecimalLiteral)
        {
            double value = double.Parse(token.Text, CultureInfo.InvariantCulture);
            return Literal.OfDouble(negative ? -value : value);
        }

        if (long.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
        {
            long signed = negative ? -parsed : parsed;
            return signed is >= int.MinValue and <= int.MaxValue
                ? Literal.OfInt((int)signed)
                : Literal.OfLong(signed);
        }

        // Spark promotes an out-of-INT64-range integer literal to DECIMAL; the M1 door has no DECIMAL
        // literal node yet, so name it as an unsupported feature rather than a plain syntax error.
        throw SqlParseException.Unsupported("DECIMAL_LITERAL", token.Position);
    }

    // ---------------------------------------------------------------------------------------------
    // Token helpers and diagnostic mapping.
    // ---------------------------------------------------------------------------------------------

    private void Advance() => _pos++;

    private void EnterRecursion(int position)
    {
        // Two complementary bounds make over-deep input a deterministic, catchable SqlParseException
        // instead of an uncatchable StackOverflowException: (1) a node-depth counter aligned with
        // TreeNode.MaxDepth, and (2) a physical-stack check that fires when the actual call stack —
        // which grows ~one full precedence ladder per parenthesis level — nears exhaustion.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (++_depth > MaxExpressionDepth)
        {
            throw SqlParseException.Syntax("expression nesting too deep", position);
        }
    }

    private SqlToken Peek(int ahead)
    {
        int index = _pos + ahead;
        return index < _tokens.Count ? _tokens[index] : _tokens[^1];
    }

    private void Expect(SqlTokenKind kind, string glyph)
    {
        if (Current.Kind != kind)
        {
            throw SqlParseException.Syntax(
                $"expected '{glyph}' but found '{Describe(Current)}'", Current.Position);
        }

        Advance();
    }

    private static bool TryComparisonOperator(SqlTokenKind kind, out ComparisonOperator op)
    {
        switch (kind)
        {
            case SqlTokenKind.Equal:
                op = ComparisonOperator.Equal;
                return true;
            case SqlTokenKind.NotEqual:
                op = ComparisonOperator.NotEqual;
                return true;
            case SqlTokenKind.LessThan:
                op = ComparisonOperator.LessThan;
                return true;
            case SqlTokenKind.LessThanOrEqual:
                op = ComparisonOperator.LessThanOrEqual;
                return true;
            case SqlTokenKind.GreaterThan:
                op = ComparisonOperator.GreaterThan;
                return true;
            case SqlTokenKind.GreaterThanOrEqual:
                op = ComparisonOperator.GreaterThanOrEqual;
                return true;
            default:
                op = default;
                return false;
        }
    }

    private static string Describe(SqlToken token) => token.Kind switch
    {
        // Do not echo the decoded value of a string literal into an error message; report its kind so
        // untrusted literal contents never leak back to the caller verbatim.
        SqlTokenKind.EndOfInput => "end of input",
        SqlTokenKind.StringLiteral => "string literal",
        _ => token.Text,
    };

    private static string? MapPredicateKeyword(SqlToken token)
    {
        // A backtick-quoted (delimited) identifier is a literal column name, never a predicate
        // keyword: 'WHERE a = `is`' references a column named 'is' (Spark parity).
        if (token.Kind != SqlTokenKind.Identifier || token.IsQuoted)
        {
            return null;
        }

        return token.Text.ToUpperInvariant() switch
        {
            "IS" => "IS_NULL",
            "IN" => "IN",
            "LIKE" => "LIKE",
            "BETWEEN" => "BETWEEN",
            _ => null,
        };
    }

    private static string? MapNotPredicateKeyword(SqlToken notToken, SqlToken next)
    {
        // Only an UNQUOTED IN/LIKE/BETWEEN after NOT names an unsupported predicate; a quoted
        // 'NOT `in`' has a delimited identifier that is a literal name, not a predicate keyword.
        if (notToken.Kind != SqlTokenKind.Not || next.Kind != SqlTokenKind.Identifier || next.IsQuoted)
        {
            return null;
        }

        return next.Text.ToUpperInvariant() switch
        {
            "IN" => "NOT_IN",
            "LIKE" => "NOT_LIKE",
            "BETWEEN" => "NOT_BETWEEN",
            _ => null,
        };
    }

    private static string? MapStatementKeyword(SqlToken token)
    {
        // A backtick-quoted identifier is a delimited name, not a statement keyword.
        if (token.Kind != SqlTokenKind.Identifier || token.IsQuoted)
        {
            return null;
        }

        return token.Text.ToUpperInvariant() switch
        {
            "INSERT" => "INSERT",
            "UPDATE" => "UPDATE",
            "DELETE" => "DELETE",
            "MERGE" => "MERGE",
            "CREATE" => "CREATE",
            "DROP" => "DROP",
            "ALTER" => "ALTER",
            "TRUNCATE" => "TRUNCATE",
            "WITH" => "CTE",
            "VALUES" => "VALUES",
            "SHOW" => "SHOW",
            "DESCRIBE" or "DESC" => "DESCRIBE",
            "EXPLAIN" => "EXPLAIN",
            "USE" => "USE",
            "SET" => "SET",
            _ => null,
        };
    }

    private static string? MapTrailingConstruct(SqlToken token)
    {
        if (token.Kind == SqlTokenKind.Comma)
        {
            return "IMPLICIT_JOIN";
        }

        if (token.Kind != SqlTokenKind.Identifier || token.IsQuoted)
        {
            return null;
        }

        return token.Text.ToUpperInvariant() switch
        {
            "JOIN" or "INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS" or "OUTER" => "JOIN",
            "GROUP" => "GROUP_BY",
            "ORDER" => "ORDER_BY",
            "HAVING" => "HAVING",
            "LIMIT" => "LIMIT",
            "OFFSET" => "OFFSET",
            "UNION" or "INTERSECT" or "EXCEPT" or "MINUS" => "UNION",
            "WINDOW" => "WINDOW",
            "CLUSTER" or "DISTRIBUTE" or "SORT" => "SORT_BY",
            _ => null,
        };
    }
}
