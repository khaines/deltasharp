namespace DeltaSharp.Sql;

/// <summary>
/// The lexical categories the M1 SQL door's <see cref="SqlLexer"/> emits. The set is intentionally
/// small — only what the STORY-04.1.3 subset (<c>SELECT &lt;cols|*&gt; FROM &lt;relation&gt;
/// [WHERE &lt;predicate&gt;]</c>) needs to lower into the shared logical-plan IR. Anything a token
/// stream can spell but the parser does not accept surfaces as a deterministic
/// <see cref="SqlParseException"/> (AC2), not a new token kind.
/// </summary>
internal enum SqlTokenKind
{
    /// <summary>The <c>SELECT</c> keyword.</summary>
    Select,

    /// <summary>The <c>FROM</c> keyword.</summary>
    From,

    /// <summary>The <c>WHERE</c> keyword.</summary>
    Where,

    /// <summary>The <c>AS</c> keyword.</summary>
    As,

    /// <summary>The <c>AND</c> keyword.</summary>
    And,

    /// <summary>The <c>OR</c> keyword.</summary>
    Or,

    /// <summary>The <c>NOT</c> keyword.</summary>
    Not,

    /// <summary>The <c>TRUE</c> boolean-literal keyword.</summary>
    True,

    /// <summary>The <c>FALSE</c> boolean-literal keyword.</summary>
    False,

    /// <summary>The <c>NULL</c> literal keyword.</summary>
    Null,

    /// <summary>An identifier (a column/table name part, unquoted or backtick-quoted).</summary>
    Identifier,

    /// <summary>An integer numeric literal (no fraction or exponent), e.g. <c>42</c>.</summary>
    IntegerLiteral,

    /// <summary>A fractional/exponent numeric literal, e.g. <c>1.5</c> or <c>2e3</c>.</summary>
    DecimalLiteral,

    /// <summary>A single-quoted string literal (with <c>''</c> escaping a quote).</summary>
    StringLiteral,

    /// <summary>The <c>,</c> select-list separator.</summary>
    Comma,

    /// <summary>The <c>.</c> identifier-part / qualified-star separator.</summary>
    Dot,

    /// <summary>The <c>(</c> grouping open.</summary>
    LParen,

    /// <summary>The <c>)</c> grouping close.</summary>
    RParen,

    /// <summary>The <c>*</c> glyph — either a select-list star or the multiply operator.</summary>
    Star,

    /// <summary>The <c>+</c> add operator.</summary>
    Plus,

    /// <summary>The <c>-</c> subtract / unary-minus operator.</summary>
    Minus,

    /// <summary>The <c>/</c> divide operator.</summary>
    Slash,

    /// <summary>The <c>%</c> remainder operator.</summary>
    Percent,

    /// <summary>The <c>=</c> equality comparison.</summary>
    Equal,

    /// <summary>The <c>&lt;&gt;</c> or <c>!=</c> inequality comparison.</summary>
    NotEqual,

    /// <summary>The <c>&lt;</c> less-than comparison.</summary>
    LessThan,

    /// <summary>The <c>&lt;=</c> less-than-or-equal comparison.</summary>
    LessThanOrEqual,

    /// <summary>The <c>&gt;</c> greater-than comparison.</summary>
    GreaterThan,

    /// <summary>The <c>&gt;=</c> greater-than-or-equal comparison.</summary>
    GreaterThanOrEqual,

    /// <summary>The synthetic end-of-input sentinel the parser stops on.</summary>
    EndOfInput,
}

/// <summary>
/// A single lexeme produced by <see cref="SqlLexer"/>: its <see cref="Kind"/>, the original
/// <see cref="Text"/> (for identifiers/literals and diagnostics), and the 1-based
/// <see cref="Position"/> of its first character in the source SQL (used to build deterministic,
/// position-tagged <see cref="SqlParseException"/> messages).
/// </summary>
/// <param name="Kind">The lexical category.</param>
/// <param name="Text">The source text of the lexeme (for a string literal, the decoded value).</param>
/// <param name="Position">The 1-based index of the lexeme's first character in the source.</param>
/// <param name="IsQuoted">
/// <see langword="true"/> when an <see cref="SqlTokenKind.Identifier"/> was backtick-quoted (a Spark
/// <em>delimited</em> identifier). A delimited identifier is ALWAYS a literal column/relation name and
/// is never interpreted as a keyword, set quantifier, or pseudo-keyword by the parser. Always
/// <see langword="false"/> for every other token kind.
/// </param>
internal readonly record struct SqlToken(
    SqlTokenKind Kind, string Text, int Position, bool IsQuoted = false);
