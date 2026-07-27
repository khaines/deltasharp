using System;
using System.Globalization;
using System.Linq;
using System.Text;
using DeltaSharp.Diagnostics;
using DeltaSharp.Sql;
using Xunit;

namespace DeltaSharp.Core.Tests.SqlDoor;

/// <summary>
/// #687 — SQL-parser diagnostic hygiene. SQL text is <b>not always caller-authored</b>: a Delta
/// <c>delta.constraints.&lt;name&gt;</c> CHECK predicate is read from the table's <c>_delta_log</c> and parsed
/// on the write path (<c>ConstraintExpressionFrontend.ParseResolveWithInput</c>), so a hostile table chooses
/// the token the parser echoes back. Before this change the parser raw-interpolated that lexeme into
/// <see cref="SqlParseException"/>'s message, which is surfaced to the caller and typically handed to a
/// structured-log sink — a <b>log-injection</b> (raw CR/LF forges log lines) and <b>unbounded-render</b>
/// (a 100&#160;000-char token → a 100&#160;000-char message) vector.
/// <para>The contract asserted here: every <see cref="SqlParseErrorKind.SyntaxError"/> message is
/// control-character-free and length-bounded at every site an attacker-chosen token can reach — while an
/// ordinary short token still comes back verbatim, so interactive SQL debugging is unchanged.</para>
/// </summary>
public sealed class SqlParserDiagnosticHygieneTests
{
    /// <summary>The red-team's exact payload: a quoted token carrying a raw CR and LF.</summary>
    private const string CrLfPayload = "TRAIL\r\nFORGED";

    /// <summary>The red-team's flood size — a 100,000-char quoted token rendered a 100,151-char message.</summary>
    private const int FloodLength = 100_000;

    /// <summary>
    /// Every <see cref="SqlParser"/> syntax-error site an <b>attacker-chosen lexeme can actually reach</b>. A
    /// backtick-quoted (delimited) identifier is the only token kind whose text is arbitrary, so these are the
    /// positions where such an identifier is rejected and echoed: the constraint door's trailing-input check,
    /// <c>expected SELECT</c>, <c>expected FROM</c>, <c>unexpected … after the query</c>, and
    /// <c>expected ')'</c>. Each template's <c>{0}</c> is filled with the hostile token.
    /// </summary>
    /// <remarks>
    /// The <c>expectedProse</c> column is what makes this theory a <i>coverage</i> guard and not merely five
    /// hygiene assertions: hygiene holds unconditionally at the <see cref="SqlParseException"/> chokepoint, so
    /// without it a grammar change that collapsed two of these templates onto the same site would shrink real
    /// coverage while the suite stayed green. Pinning the prose per row makes that collapse a test failure.
    /// The sibling <see cref="BoundedEchoSites"/> theory already works this way.
    /// </remarks>
    public static TheoryData<string, string, bool, string> HostileEchoSites() => new()
    {
        // SqlParser.ParseConstraintExpression.
        { "constraint-trailing-input", "other > 0 `{0}`", false, "unexpected trailing input '" },

        // SqlParser.ParseStatement.
        { "expected-select", "`{0}` a FROM t", true, "expected SELECT but found '" },

        // SqlParser.ParseStatement.
        { "expected-from", "SELECT a `x` `{0}` t", true, "expected FROM but found '" },

        // SqlParser.ExpectEnd.
        { "trailing-after-query", "SELECT a FROM t `{0}`", true, "unexpected '" },

        // SqlParser.Expect(RParen, ")").
        { "expected-rparen", "SELECT a FROM t WHERE (a > 1 `{0}`", true, "expected ')' but found '" },
    };

    /// <summary>
    /// The remaining <see cref="SqlParser"/> echo sites. Their offending lexeme can only ever be an operator,
    /// numeric literal, or keyword (a delimited identifier would have been <i>accepted</i> in those positions),
    /// so they are attacker-bounded by construction — but they share the same <c>Describe</c> chokepoint, so
    /// these cases pin that routing them through the sanitizer did not change what they name.
    /// </summary>
    public static TheoryData<string, string> BoundedEchoSites() => new()
    {
        // SqlParser.ExpectAliasName: "expected an alias name after AS but found '<tok>'".
        { "SELECT a AS 1 FROM t", "expected an alias name after AS but found '1'" },

        // SqlParser.ParseRelation: "expected a table name but found '<tok>'".
        { "SELECT a FROM 1", "expected a table name but found '1'" },

        // SqlParser.ParseRelation: "expected an identifier after '.' but found '<tok>'".
        { "SELECT a FROM t.(", "expected an identifier after '.' but found '('" },

        // SqlParser.ParseColumnReference: "expected an identifier after '.' but found '<tok>'".
        { "SELECT a FROM t WHERE b.( > 0", "expected an identifier after '.' but found '('" },

        // SqlParser.ParsePrimary: "expected an expression but found '<tok>'".
        { "SELECT a FROM t WHERE a > )", "expected an expression but found ')'" },
    };

    [Theory]
    [MemberData(nameof(HostileEchoSites))]
    public void SyntaxError_HostileTokenAtEveryReachableEchoSite_IsControlCharFreeAndBounded(
        string site, string template, bool viaStatementDoor, string expectedProse)
    {
        Assert.NotEmpty(site);

        // ONE crafted token that is BOTH a CRLF log-injection payload AND oversized, so a single assertion
        // proves both halves of the #687 contract at each site.
        string hostile = CrLfPayload + new string('x', FloodLength);
        string sql = string.Format(CultureInfo.InvariantCulture, template, hostile);

        SqlParseException ex = Assert.Throws<SqlParseException>(
            () =>
            {
                if (viaStatementDoor)
                {
                    SqlParser.Parse(sql);
                }
                else
                {
                    SqlParser.ParseConstraintExpression(sql);
                }
            });

        Assert.Equal(SqlParseErrorKind.SyntaxError, ex.ErrorKind);

        // Pin the site. Every row must reach a DISTINCT template, so coverage cannot silently shrink.
        Assert.Contains(expectedProse, ex.Message, StringComparison.Ordinal);
        AssertHygienic(ex.Message);
    }

    /// <summary>
    /// Guards the <i>distinctness</i> half of <see cref="HostileEchoSites"/> directly: two rows sharing a
    /// prose pin would still satisfy the per-row <c>Assert.Contains</c> above, so the coverage claim in that
    /// theory's summary — five sites, not four — is asserted here rather than left to the comment.
    /// </summary>
    [Fact]
    public void HostileEchoSites_EachRowPinsADistinctSite()
    {
        string[] pins = HostileEchoSites().Select(row => (string)row[3]).ToArray();

        Assert.Equal(5, pins.Length);
        Assert.Equal(pins.Length, pins.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(BoundedEchoSites))]
    public void SyntaxError_AttackerUnreachableEchoSites_StillNameTheOffendingToken(string sql, string expected)
    {
        SqlParseException ex = Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));

        Assert.Equal(SqlParseErrorKind.SyntaxError, ex.ErrorKind);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        AssertHygienic(ex.Message);
    }

    [Fact]
    public void ConstraintTrailingInput_RedTeamCrLfRepro_HasNoRawControlChars_ButStillNamesTheToken()
    {
        // The red-team's verbatim repro: delta.constraints.safe = "other > 0 `TRAIL<CR><LF>FORGED`". It used
        // to emit a 203-char message containing a literal CR and a literal LF.
        SqlParseException ex = Assert.Throws<SqlParseException>(
            () => SqlParser.ParseConstraintExpression("other > 0 `" + CrLfPayload + "`"));

        AssertHygienic(ex.Message);

        // The sanitizer only NEUTRALIZES the control characters, so the diagnostic stays useful: an operator
        // can still see exactly which token was rejected.
        Assert.Contains("TRAIL\uFFFD\uFFFDFORGED", ex.Message, StringComparison.Ordinal);
        Assert.Contains("after the constraint expression", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstraintTrailingInput_RedTeamFloodRepro_MessageIsBounded_AndKeepsItsProse()
    {
        // The red-team's second repro: a 100,000-char quoted trailing token rendered a 100,151-char message.
        SqlParseException ex = Assert.Throws<SqlParseException>(
            () => SqlParser.ParseConstraintExpression("other > 0 `" + new string('z', FloodLength) + "`"));

        AssertHygienic(ex.Message);

        // The surrounding explanatory prose SURVIVES the flood — that is precisely why the token is bounded
        // per-token rather than only capping the finished message.
        Assert.Contains("constraint is a single boolean expression", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lexer_RawControlCharacterGlyph_IsNeutralized()
    {
        // A NUL is not whitespace, so it falls through SqlLexer.ScanOperator to its
        // "unexpected character '<glyph>'" echo — a single RAW source character, neutralized centrally by
        // SqlParseException.Syntax.
        SqlParseException ex = Assert.Throws<SqlParseException>(
            () => SqlParser.ParseConstraintExpression("a > 0 \0"));

        AssertHygienic(ex.Message);
        Assert.Contains("unexpected character '\uFFFD'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\u2028")] // LINE SEPARATOR — rendered as a newline by several log viewers
    [InlineData("\u2029")] // PARAGRAPH SEPARATOR
    [InlineData("\u001b")] // ESC — the lead-in for ANSI terminal escape sequences
    [InlineData("\u0085")] // NEL (C1 control)
    [InlineData("\u202e")] // RIGHT-TO-LEFT OVERRIDE (Cf) — visually reverses the rest of a rendered log line
    [InlineData("\u200e")] // LEFT-TO-RIGHT MARK (Cf)
    [InlineData("\ufeff")] // ZERO WIDTH NO-BREAK SPACE / BOM (Cf)
    [InlineData("\u00ad")] // SOFT HYPHEN (Cf) — invisible in most renderers
    [InlineData("\u2066")] // LEFT-TO-RIGHT ISOLATE (Cf)
    [InlineData("\u200b")] // ZERO WIDTH SPACE (Cf) — hides or reorders text during triage
    [InlineData("\U000E0001")] // LANGUAGE TAG (ASTRAL Cf) — invisible to a char-wise category check
    [InlineData("\U000E0045\U000E0056\U000E0049\U000E004C")] // "EVIL" smuggled in the TAG block
    [InlineData("\U0001D173")] // MUSICAL SYMBOL BEGIN BEAM (ASTRAL Cf)
    public void SyntaxError_NonCrLfLineBreakingCharacters_AreAlsoNeutralized(string payload)
    {
        SqlParseException ex = Assert.Throws<SqlParseException>(
            () => SqlParser.ParseConstraintExpression("a > 0 `t" + payload + "u`"));

        AssertHygienic(ex.Message);
        Assert.DoesNotContain(payload, ex.Message, StringComparison.Ordinal);
        Assert.Contains("t" + new string('\uFFFD', payload.EnumerateRunes().Count()) + "u", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SyntaxError_OrdinaryTypo_IsStillEchoedVerbatim_InteractiveUxPreserved()
    {
        // Hygiene must NOT degrade interactive SQL debugging: a realistic mistyped identifier is far under the
        // cap and comes back whole and unmodified.
        SqlParseException ex = Assert.Throws<SqlParseException>(
            () => SqlParser.Parse("SELECT a FROM t WHERE a > 1 customer_lifetime_value"));

        Assert.Contains("'customer_lifetime_value'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', ex.Message);
        Assert.DoesNotContain('\u2026', ex.Message);
    }

    [Fact]
    public void SyntaxError_TokenExactlyAtTheCap_IsNotTruncated_BoundaryIsInclusive()
    {
        // The cap is inclusive: a token exactly at the bound is echoed in full (only a LONGER token is
        // elided), so the "interactive UX preserved" promise has a crisp, tested boundary.
        string atCap = new('c', DiagnosticText.DefaultMaxLength);

        SqlParseException ex = Assert.Throws<SqlParseException>(
            () => SqlParser.ParseConstraintExpression("a > 0 `" + atCap + "`"));

        Assert.Contains("'" + atCap + "'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2026', ex.Message);
    }

    [Fact]
    public void SyntaxError_TokenOneOverTheCap_IsElided_WithEllipsis()
    {
        string overCap = new('c', DiagnosticText.DefaultMaxLength + 1);

        SqlParseException ex = Assert.Throws<SqlParseException>(
            () => SqlParser.ParseConstraintExpression("a > 0 `" + overCap + "`"));

        Assert.DoesNotContain(overCap, ex.Message, StringComparison.Ordinal);
        Assert.Contains('\u2026', ex.Message);
        AssertHygienic(ex.Message);
    }

    [Fact]
    public void EchoedTokenCap_IsExactlyOneHundredTwentyEight_PinnedAsALiteral()
    {
        // The interactive-UX promise ("a realistic typo comes back whole") is calibrated to this number, and
        // three layers alias it (Abstractions -> Storage -> SqlParser.EchoedTokenMaxLength). The neighbouring
        // boundary tests reference the constant, so they would move with it and prove nothing; this one pins
        // the LITERAL so shrinking the bound is a deliberate, visible act.
        string atCap = new('c', 128);
        string overCap = new('c', 129);

        SqlParseException whole = Assert.Throws<SqlParseException>(
            () => SqlParser.ParseConstraintExpression("a > 0 `" + atCap + "`"));
        SqlParseException elided = Assert.Throws<SqlParseException>(
            () => SqlParser.ParseConstraintExpression("a > 0 `" + overCap + "`"));

        Assert.Contains(atCap, whole.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2026', whole.Message);
        Assert.Contains('\u2026', elided.Message);
    }

    [Fact]
    public void PublicConstructors_AreAlsoBounded_TheBackstopIsNotSyntaxOnly()
    {
        // Council round 2, item 4 (Architect A2 + Security): SqlParseException is a public sealed type with
        // three PUBLIC constructors that bypass the Syntax factory. The backstop used to live in the factory,
        // so the doc's "no present or future call site can bypass" claim was wrong. It now lives in a private
        // chokepoint every constructor routes through. All three current in-repo uses carry fixed prose, so
        // nothing was exploitable — this pins the stronger posture so a future untrusted-input caller inherits
        // it for free.
        string hostile = "TRAIL\r\nFORGED" + new string('z', FloodLength);

        foreach (SqlParseException ex in new[]
        {
            new SqlParseException(hostile),
            new SqlParseException(hostile, new InvalidOperationException("inner")),
        })
        {
            AssertHygienic(ex.Message);

            // `Sanitize` keeps MaxMessageLength characters and then appends the elision glyph, so the bound on
            // the rendered string is MaxMessageLength + 1. Pinned exactly rather than loosely so a change in
            // the elision shape is visible here.
            Assert.Equal(SqlParseException.MaxMessageLength + 1, ex.Message.Length);
            Assert.EndsWith("\u2026", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SyntaxError_HostileToken_StaysDeterministic_AcrossRepeatedParses()
    {
        // Sanitizing must not make the diagnostic nondeterministic — the parse door's stability contract.
        string sql = "a > 0 `" + CrLfPayload + new string('q', 500) + "`";

        SqlParseException first = Assert.Throws<SqlParseException>(() => SqlParser.ParseConstraintExpression(sql));
        SqlParseException second = Assert.Throws<SqlParseException>(() => SqlParser.ParseConstraintExpression(sql));

        Assert.Equal(first.Message, second.Message);
    }

    [Fact]
    public void SyntaxError_Position_IsStillReported_AfterSanitizing()
    {
        // The position tag is composed OUTSIDE the sanitized detail, so it survives a hostile token intact.
        SqlParseException ex = Assert.Throws<SqlParseException>(
            () => SqlParser.ParseConstraintExpression("a > 0 `" + CrLfPayload + "`"));

        Assert.StartsWith("Syntax error at position 7:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedFeature_LongOnboardingProse_IsNotTruncated_ByTheSyntaxBackstop()
    {
        // UnsupportedFeature messages carry long FIXED onboarding prose built only from compile-time construct
        // constants — never source text — so they are deliberately NOT passed through the Syntax backstop and
        // must survive intact.
        SqlParseException ex = Assert.Throws<SqlParseException>(() => SqlParser.Parse("SELECT a FROM t LIMIT 10"));

        Assert.Equal(SqlParseErrorKind.UnsupportedFeature, ex.ErrorKind);
        Assert.Equal("LIMIT", ex.Construct);
        Assert.Contains("DataFrame.Limit(...)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2026', ex.Message);
    }

    // The #687 contract, asserted uniformly: no control character and no Unicode line/paragraph separator
    // survives into the message, and the message length is bounded by the parser's own detail cap plus its
    // fixed "Syntax error at position N: " prefix — never proportional to the attacker's token length.
    private static void AssertHygienic(string message)
    {
        foreach (Rune rune in message.EnumerateRunes())
        {
            // Rune-based, not char-based, on purpose: Cc/Zl/Zp are entirely BMP but Cf is NOT (the TAG block
            // U+E0020-U+E007F and friends are astral), so a char-wise scan cannot see an astral format
            // character at all and would pass vacuously on exactly the payload it is meant to catch.
            Assert.False(
                Rune.GetUnicodeCategory(rune)
                    is UnicodeCategory.Control or UnicodeCategory.LineSeparator
                        or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format,
                FormattableString.Invariant($"message carries an injection-unsafe U+{rune.Value:X4}"));
        }

        Assert.False(ContainsLoneSurrogate(message), "message carries a lone surrogate");

        Assert.True(
            message.Length <= SqlParseException.MaxMessageLength + 64,
            FormattableString.Invariant($"message length {message.Length} exceeds the bounded render budget"));
    }

    /// <summary>A LONE (unpaired) surrogate is malformed UTF-16 that the sanitizer neutralizes; a WELL-FORMED
    /// pair is legitimate astral text (an emoji, a CJK-extension ideograph) that must survive. Checking for
    /// "no surrogates at all" would be wrong — it would contradict the primitive's deliberate contract — so
    /// this checks precisely for the malformed case.</summary>
    private static bool ContainsLoneSurrogate(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
                continue;
            }

            if (char.IsLowSurrogate(value[i]))
            {
                return true;
            }
        }

        return false;
    }

}
