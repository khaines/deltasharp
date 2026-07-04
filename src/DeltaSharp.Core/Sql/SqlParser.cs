using System.Collections.Generic;
using System.Globalization;
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
    private readonly IReadOnlyList<SqlToken> _tokens;
    private int _pos;

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
        return new SqlParser(tokens).ParseStatement();
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
        // DISTINCT/ALL as a set quantifier immediately after SELECT is recognizable SQL the M1 door
        // does not implement (DISTINCT needs the Distinct operator + dedup semantics).
        if (Current.Kind == SqlTokenKind.Identifier
            && string.Equals(Current.Text, "DISTINCT", System.StringComparison.OrdinalIgnoreCase))
        {
            throw SqlParseException.Unsupported("SELECT DISTINCT", Current.Position);
        }
    }

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

        // Implicit alias: 'SELECT a b' aliases 'a' as 'b' (Spark parity).
        if (Current.Kind == SqlTokenKind.Identifier)
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
            throw SqlParseException.Unsupported("subquery", Current.Position);
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
            Advance();
            return new Not(ParseNot());
        }

        return ParseComparison();
    }

    private Expression ParseComparison()
    {
        Expression left = ParseAdditive();
        if (TryComparisonOperator(Current.Kind, out ComparisonOperator op))
        {
            Advance();
            Expression right = ParseAdditive();
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

            throw SqlParseException.Syntax(
                $"unary '{sign.Text}' is only supported directly before a numeric literal",
                sign.Position);
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
                    throw SqlParseException.Unsupported("subquery", token.Position);
                }

                Expression inner = ParseExpression();
                Expect(SqlTokenKind.RParen, ")");
                return inner;

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
            throw SqlParseException.Unsupported("function call", start.Position);
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

        throw SqlParseException.Syntax($"integer literal '{token.Text}' is out of range", token.Position);
    }

    // ---------------------------------------------------------------------------------------------
    // Token helpers and diagnostic mapping.
    // ---------------------------------------------------------------------------------------------

    private void Advance() => _pos++;

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

    private static string Describe(SqlToken token) =>
        token.Kind == SqlTokenKind.EndOfInput ? "end of input" : token.Text;

    private static string? MapStatementKeyword(SqlToken token)
    {
        if (token.Kind != SqlTokenKind.Identifier)
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
            "WITH" => "WITH (common table expression)",
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
            return "comma-separated table list (implicit join)";
        }

        if (token.Kind != SqlTokenKind.Identifier)
        {
            return null;
        }

        return token.Text.ToUpperInvariant() switch
        {
            "JOIN" or "INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS" or "OUTER" => "JOIN",
            "GROUP" => "GROUP BY",
            "ORDER" => "ORDER BY",
            "HAVING" => "HAVING",
            "LIMIT" => "LIMIT",
            "OFFSET" => "OFFSET",
            "UNION" or "INTERSECT" or "EXCEPT" or "MINUS" => "set operation (UNION/INTERSECT/EXCEPT)",
            "WINDOW" => "WINDOW",
            "CLUSTER" or "DISTRIBUTE" or "SORT" => "CLUSTER/DISTRIBUTE/SORT BY",
            _ => null,
        };
    }
}
