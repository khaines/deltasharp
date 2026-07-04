using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeltaSharp.Sql;

/// <summary>
/// The hand-written lexer for the M1 SQL door. It scans a SQL string into a flat
/// <see cref="SqlToken"/> list (ending with a single <see cref="SqlTokenKind.EndOfInput"/>) that
/// <see cref="SqlParser"/> consumes. Keywords are matched case-insensitively (ANSI parity);
/// identifiers may be unquoted (<c>[A-Za-z_][A-Za-z0-9_]*</c>) or backtick-quoted (Spark parity).
/// It performs no allocation of engine state and never touches a catalog — building tokens does no
/// query work (ADR-0001). Any character it cannot classify raises a deterministic
/// <see cref="SqlParseException"/> (<see cref="SqlParseErrorKind.SyntaxError"/>) tagged with the
/// 1-based source position.
/// </summary>
internal static class SqlLexer
{
    private static readonly Dictionary<string, SqlTokenKind> Keywords =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["SELECT"] = SqlTokenKind.Select,
            ["FROM"] = SqlTokenKind.From,
            ["WHERE"] = SqlTokenKind.Where,
            ["AS"] = SqlTokenKind.As,
            ["AND"] = SqlTokenKind.And,
            ["OR"] = SqlTokenKind.Or,
            ["NOT"] = SqlTokenKind.Not,
            ["TRUE"] = SqlTokenKind.True,
            ["FALSE"] = SqlTokenKind.False,
            ["NULL"] = SqlTokenKind.Null,
        };

    /// <summary>Scans <paramref name="sql"/> into a token list terminated by
    /// <see cref="SqlTokenKind.EndOfInput"/>.</summary>
    /// <param name="sql">The SQL text to tokenize.</param>
    /// <exception cref="SqlParseException">A character cannot be classified, or a string/quoted
    /// identifier is unterminated.</exception>
    public static IReadOnlyList<SqlToken> Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        int i = 0;
        int n = sql.Length;

        while (i < n)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            int position = i + 1;

            if (c == '\'')
            {
                tokens.Add(ScanString(sql, ref i, position));
                continue;
            }

            if (c == '`')
            {
                tokens.Add(ScanQuotedIdentifier(sql, ref i, position));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                tokens.Add(ScanWord(sql, ref i, position));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(sql[i + 1])))
            {
                tokens.Add(ScanNumber(sql, ref i, position));
                continue;
            }

            tokens.Add(ScanOperator(sql, ref i, position));
        }

        tokens.Add(new SqlToken(SqlTokenKind.EndOfInput, string.Empty, n + 1));
        return tokens;
    }

    private static SqlToken ScanWord(string sql, ref int i, int position)
    {
        int start = i;
        while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
        {
            i++;
        }

        string text = sql[start..i];
        SqlTokenKind kind = Keywords.TryGetValue(text, out SqlTokenKind keyword)
            ? keyword
            : SqlTokenKind.Identifier;
        return new SqlToken(kind, text, position);
    }

    private static SqlToken ScanNumber(string sql, ref int i, int position)
    {
        int start = i;
        bool isDecimal = false;

        while (i < sql.Length && char.IsDigit(sql[i]))
        {
            i++;
        }

        if (i < sql.Length && sql[i] == '.')
        {
            isDecimal = true;
            i++;
            while (i < sql.Length && char.IsDigit(sql[i]))
            {
                i++;
            }
        }

        if (i < sql.Length && (sql[i] == 'e' || sql[i] == 'E'))
        {
            isDecimal = true;
            i++;
            if (i < sql.Length && (sql[i] == '+' || sql[i] == '-'))
            {
                i++;
            }

            if (i >= sql.Length || !char.IsDigit(sql[i]))
            {
                throw SqlParseException.Syntax("malformed numeric literal exponent", position);
            }

            while (i < sql.Length && char.IsDigit(sql[i]))
            {
                i++;
            }
        }

        string text = sql[start..i];
        return new SqlToken(
            isDecimal ? SqlTokenKind.DecimalLiteral : SqlTokenKind.IntegerLiteral, text, position);
    }

    private static SqlToken ScanString(string sql, ref int i, int position)
    {
        var value = new StringBuilder();
        i++; // opening quote
        while (i < sql.Length)
        {
            char c = sql[i];
            if (c == '\'')
            {
                if (i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    value.Append('\'');
                    i += 2;
                    continue;
                }

                i++; // closing quote
                return new SqlToken(SqlTokenKind.StringLiteral, value.ToString(), position);
            }

            value.Append(c);
            i++;
        }

        throw SqlParseException.Syntax("unterminated string literal", position);
    }

    private static SqlToken ScanQuotedIdentifier(string sql, ref int i, int position)
    {
        var value = new StringBuilder();
        i++; // opening backtick
        while (i < sql.Length)
        {
            char c = sql[i];
            if (c == '`')
            {
                if (i + 1 < sql.Length && sql[i + 1] == '`')
                {
                    value.Append('`');
                    i += 2;
                    continue;
                }

                i++; // closing backtick
                if (value.Length == 0)
                {
                    throw SqlParseException.Syntax("empty backtick-quoted identifier", position);
                }

                return new SqlToken(SqlTokenKind.Identifier, value.ToString(), position);
            }

            value.Append(c);
            i++;
        }

        throw SqlParseException.Syntax("unterminated backtick-quoted identifier", position);
    }

    private static SqlToken ScanOperator(string sql, ref int i, int position)
    {
        char c = sql[i];
        char next = i + 1 < sql.Length ? sql[i + 1] : '\0';

        switch (c)
        {
            case ',':
                i++;
                return new SqlToken(SqlTokenKind.Comma, ",", position);
            case '.':
                i++;
                return new SqlToken(SqlTokenKind.Dot, ".", position);
            case '(':
                i++;
                return new SqlToken(SqlTokenKind.LParen, "(", position);
            case ')':
                i++;
                return new SqlToken(SqlTokenKind.RParen, ")", position);
            case '*':
                i++;
                return new SqlToken(SqlTokenKind.Star, "*", position);
            case '+':
                i++;
                return new SqlToken(SqlTokenKind.Plus, "+", position);
            case '-':
                i++;
                return new SqlToken(SqlTokenKind.Minus, "-", position);
            case '/':
                i++;
                return new SqlToken(SqlTokenKind.Slash, "/", position);
            case '%':
                i++;
                return new SqlToken(SqlTokenKind.Percent, "%", position);
            case '=':
                i++;
                return new SqlToken(SqlTokenKind.Equal, "=", position);
            case '<':
                if (next == '=')
                {
                    i += 2;
                    return new SqlToken(SqlTokenKind.LessThanOrEqual, "<=", position);
                }

                if (next == '>')
                {
                    i += 2;
                    return new SqlToken(SqlTokenKind.NotEqual, "<>", position);
                }

                i++;
                return new SqlToken(SqlTokenKind.LessThan, "<", position);
            case '>':
                if (next == '=')
                {
                    i += 2;
                    return new SqlToken(SqlTokenKind.GreaterThanOrEqual, ">=", position);
                }

                i++;
                return new SqlToken(SqlTokenKind.GreaterThan, ">", position);
            case '!':
                if (next == '=')
                {
                    i += 2;
                    return new SqlToken(SqlTokenKind.NotEqual, "!=", position);
                }

                break;
        }

        string glyph = c.ToString(CultureInfo.InvariantCulture);
        throw SqlParseException.Syntax($"unexpected character '{glyph}'", position);
    }
}
