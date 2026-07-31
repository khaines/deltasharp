using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

    private sealed record EchoSite(
        string Name,
        string Frame,
        string? StringLiteralSql = null,
        bool StringLiteralViaStatementDoor = true,
        string? StringLiteralProse = null,
        string? HostileTemplate = null,
        bool HostileViaStatementDoor = true,
        string? HostileProse = null,
        string? BoundedSql = null,
        string? BoundedProse = null,
        string? AcceptedStringLiteralSql = null);

    /// <summary>
    /// The complete <see cref="SqlParser.Describe"/> call-site inventory. Each site is classified by
    /// executable behavior: a string literal is either rejected as its kind or accepted as a primary, and an
    /// arbitrary delimited identifier is either rejected at a hostile echo site or accepted so only a bounded
    /// token can reach the rejection. <see cref="EchoSiteInventory_ReconcilesEveryDescribeCall"/> ties this
    /// inventory to the production source so adding a call site requires classifying it here.
    /// </summary>
    private static readonly EchoSite[] EchoSites =
    [
        new(
            "constraint-trailing-input",
            "SqlParser.ParseConstraintExpression",
            StringLiteralSql: "a > 0 'SEC\r\nRET_PAYLOAD'",
            StringLiteralViaStatementDoor: false,
            StringLiteralProse: "unexpected trailing input 'string literal'",
            HostileTemplate: "other > 0 `{0}`",
            HostileViaStatementDoor: false,
            HostileProse: "unexpected trailing input '"),
        new(
            "expected-select",
            "SqlParser.ParseStatement",
            StringLiteralSql: "'SEC\r\nRET_PAYLOAD' a FROM t",
            StringLiteralProse: "expected SELECT but found 'string literal'",
            HostileTemplate: "`{0}` a FROM t",
            HostileProse: "expected SELECT but found '"),
        new(
            "expected-from",
            "SqlParser.ParseStatement",
            StringLiteralSql: "SELECT a 'SEC\r\nRET_PAYLOAD' t",
            StringLiteralProse: "expected FROM but found 'string literal'",
            HostileTemplate: "SELECT a `x` `{0}` t",
            HostileProse: "expected FROM but found '"),
        new(
            "alias-after-as",
            "SqlParser.ExpectAliasName",
            StringLiteralSql: "SELECT a AS 'SEC\r\nRET_PAYLOAD' FROM t",
            StringLiteralProse: "expected an alias name after AS but found 'string literal'",
            BoundedSql: "SELECT a AS 1 FROM t",
            BoundedProse: "expected an alias name after AS but found '1'"),
        new(
            "relation-name",
            "SqlParser.ParseRelation",
            StringLiteralSql: "SELECT a FROM 'SEC\r\nRET_PAYLOAD'",
            StringLiteralProse: "expected a table name but found 'string literal'",
            BoundedSql: "SELECT a FROM 1",
            BoundedProse: "expected a table name but found '1'"),
        new(
            "relation-identifier-after-dot",
            "SqlParser.ParseRelation",
            StringLiteralSql: "SELECT a FROM t.'SEC\r\nRET_PAYLOAD'",
            StringLiteralProse: "expected an identifier after '.' but found 'string literal'",
            BoundedSql: "SELECT a FROM t.(",
            BoundedProse: "expected an identifier after '.' but found '('"),
        new(
            "trailing-after-query",
            "SqlParser.ExpectEnd",
            StringLiteralSql: "SELECT a FROM t 'SEC\r\nRET_PAYLOAD'",
            StringLiteralProse: "unexpected 'string literal' after the query",
            HostileTemplate: "SELECT a FROM t `{0}`",
            HostileProse: "unexpected '"),
        new(
            "expected-expression",
            "SqlParser.ParsePrimary",
            BoundedSql: "SELECT a FROM t WHERE a > )",
            BoundedProse: "expected an expression but found ')'",
            AcceptedStringLiteralSql: "SELECT 'SAFE_LITERAL' FROM t"),
        new(
            "constraint-identifier-after-dot",
            "SqlParser.ParseColumnReference",
            StringLiteralSql: "a.'SEC\r\nRET_PAYLOAD' > 0",
            StringLiteralViaStatementDoor: false,
            StringLiteralProse: "expected an identifier after '.' but found 'string literal'",
            BoundedSql: "SELECT a FROM t WHERE b.( > 0",
            BoundedProse: "expected an identifier after '.' but found '('"),
        new(
            "expected-rparen",
            "SqlParser.Expect",
            StringLiteralSql: "(a > 1 'SEC\r\nRET_PAYLOAD'",
            StringLiteralViaStatementDoor: false,
            StringLiteralProse: "expected ')' but found 'string literal'",
            HostileTemplate: "SELECT a FROM t WHERE (a > 1 `{0}`",
            HostileProse: "expected ')' but found '"),
    ];

    public static TheoryData<string, string, bool, string, string> HostileEchoSites()
    {
        var data = new TheoryData<string, string, bool, string, string>();
        foreach (EchoSite site in EchoSites.Where(site => site.HostileTemplate is not null))
        {
            data.Add(
                site.Name,
                site.HostileTemplate!,
                site.HostileViaStatementDoor,
                site.HostileProse!,
                site.Frame);
        }

        return data;
    }

    public static TheoryData<string, string, string, string> BoundedEchoSites()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (EchoSite site in EchoSites.Where(site => site.BoundedSql is not null))
        {
            data.Add(site.Name, site.BoundedSql!, site.BoundedProse!, site.Frame);
        }

        return data;
    }

    public static TheoryData<string, string, bool, string, string> StringLiteralEchoSites()
    {
        var data = new TheoryData<string, string, bool, string, string>();
        foreach (EchoSite site in EchoSites.Where(site => site.StringLiteralSql is not null))
        {
            data.Add(
                site.Name,
                site.StringLiteralSql!,
                site.StringLiteralViaStatementDoor,
                site.StringLiteralProse!,
                site.Frame);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HostileEchoSites))]
    public void SyntaxError_HostileTokenAtEveryReachableEchoSite_IsControlCharFreeAndBounded(
        string site, string template, bool viaStatementDoor, string expectedProse, string expectedFrame)
    {
        Assert.NotEmpty(site);

        // ONE crafted token that is BOTH a CRLF log-injection payload AND oversized, so a single assertion
        // proves both halves of the #687 contract at each site.
        string hostile = CrLfPayload + new string('x', FloodLength);
        string sql = string.Format(CultureInfo.InvariantCulture, template, hostile);

        SqlParseException ex = ParseSyntaxError(sql, viaStatementDoor);

        Assert.Equal(SqlParseErrorKind.SyntaxError, ex.ErrorKind);

        Assert.Contains(expectedProse, ex.Message, StringComparison.Ordinal);
        Assert.Equal(expectedFrame, ThrowingParserFrame(ex));
        AssertHygienic(ex.Message);
    }

    [Theory]
    [MemberData(nameof(BoundedEchoSites))]
    public void SyntaxError_AttackerUnreachableEchoSites_StillNameTheOffendingToken(
        string site, string sql, string expected, string expectedFrame)
    {
        Assert.NotEmpty(site);
        SqlParseException ex = ParseSyntaxError(sql, viaStatementDoor: true);

        Assert.Equal(SqlParseErrorKind.SyntaxError, ex.ErrorKind);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(expectedFrame, ThrowingParserFrame(ex));
        AssertHygienic(ex.Message);
    }

    [Fact]
    public void HostileAndBoundedEchoSites_ReachDistinctObservedSites()
    {
        string hostile = CrLfPayload + new string('x', FloodLength);
        (string Frame, string Detail)[] observed = EchoSites
            .Select(site =>
            {
                string sql = site.HostileTemplate is not null
                    ? string.Format(CultureInfo.InvariantCulture, site.HostileTemplate, hostile)
                    : site.BoundedSql!;
                bool viaStatementDoor = site.HostileTemplate is not null
                    ? site.HostileViaStatementDoor
                    : true;
                SqlParseException ex = ParseSyntaxError(sql, viaStatementDoor);
                return (ThrowingParserFrame(ex), DiagnosticDetail(ex));
            })
            .ToArray();

        Assert.Equal(observed.Length, observed.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(StringLiteralEchoSites))]
    public void SyntaxError_StringLiteralEchoSites_ReportKind_NotDecodedValue(
        string site, string sql, bool viaStatementDoor, string expectedProse, string expectedFrame)
    {
        Assert.NotEmpty(site);

        SqlParseException ex = ParseSyntaxError(sql, viaStatementDoor);

        Assert.Contains(expectedProse, ex.Message, StringComparison.Ordinal);
        Assert.Equal(expectedFrame, ThrowingParserFrame(ex));
        Assert.DoesNotContain("RET_PAYLOAD", ex.Message, StringComparison.Ordinal);
        AssertHygienic(ex.Message);
    }

    [Fact]
    public void StringLiteralEchoSites_ReachDistinctObservedSites()
    {
        (string Frame, string Detail)[] observed = StringLiteralEchoSites()
            .Select(row =>
            {
                SqlParseException ex = ParseSyntaxError((string)row[1], (bool)row[2]);
                return (ThrowingParserFrame(ex), DiagnosticDetail(ex));
            })
            .ToArray();

        Assert.Equal(observed.Length, observed.Distinct().Count());
    }

    [Fact]
    public void EchoSiteInventory_ReconcilesEveryDescribeCall()
    {
        string source = File.ReadAllText(SqlParserSourcePath());
        string code = string.Join(
            '\n',
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        int declarations = Regex.Matches(code, @"private static string Describe\b").Count;
        int references = Regex.Matches(code, @"\bDescribe\b").Count - declarations;

        Assert.Equal(1, declarations);
        Assert.Equal(references, EchoSites.Length);
        Assert.Equal(EchoSites.Length, EchoSites.Select(site => site.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            EchoSites,
            site => Assert.True(
                (site.HostileTemplate is null) != (site.BoundedSql is null),
                $"{site.Name} must classify arbitrary lexeme reachability exactly once"));
        Assert.All(
            EchoSites,
            site => Assert.True(
                (site.StringLiteralSql is null) != (site.AcceptedStringLiteralSql is null),
                $"{site.Name} must classify string-literal reachability exactly once"));

        EchoSite accepted = Assert.Single(EchoSites, site => site.AcceptedStringLiteralSql is not null);
        SqlParser.Parse(accepted.AcceptedStringLiteralSql!);
    }

    private static SqlParseException ParseSyntaxError(string sql, bool viaStatementDoor) =>
        Assert.Throws<SqlParseException>(
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

    private static string ThrowingParserFrame(SqlParseException exception)
    {
        var target = exception.TargetSite;
        Assert.NotNull(target);
        Type? declaringType = target.DeclaringType;
        Assert.NotNull(declaringType);
        Assert.Equal(typeof(SqlParser), declaringType);
        return $"{declaringType.Name}.{target.Name}";
    }

    private static string DiagnosticDetail(SqlParseException exception)
    {
        int detail = exception.Message.IndexOf(": ", StringComparison.Ordinal);
        Assert.True(detail >= 0, $"syntax diagnostic has no position/detail separator: {exception.Message}");
        return Regex.Replace(exception.Message[(detail + 2)..], "'[^']*'", "'<token>'");
    }

    private static string SqlParserSourcePath()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DeltaSharp.sln")))
            {
                return Path.Combine(
                    directory.FullName,
                    "src",
                    "DeltaSharp.Core",
                    "Sql",
                    "SqlParser.cs");
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate DeltaSharp.sln above test base directory '{AppContext.BaseDirectory}'.");
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
        // chokepoint every constructor routes through. Nothing was exploitable, and this pins the stronger
        // posture so a future untrusted-input caller inherits it for free.
        //
        // A count of the in-repo callers used to stand here and was wrong at HEAD — it said three, there are
        // five, in two files. It is not corrected because it should never have been load-bearing: the whole
        // point of moving the backstop into a private chokepoint is that the caller list stops mattering.
        // Counting the callers is the argument you make when there is no chokepoint, and making it next to
        // one is how a comment ends up asserting something weaker than the code.
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
    public void PublicConstructorBackstop_IsIndependentOfAttackerLength_OnceBothInputsExceedItsBudget()
    {
        const string prefix = "TRAIL\r\nFORGED";
        string shorter = prefix + new string('z', 4_096);
        string longer = prefix + new string('z', FloodLength);

        foreach (Func<string, SqlParseException> create in new Func<string, SqlParseException>[]
        {
            message => new SqlParseException(message),
            message => new SqlParseException(message, new InvalidOperationException("inner")),
        })
        {
            SqlParseException shorterException = create(shorter);
            SqlParseException longerException = create(longer);

            Assert.Equal(shorterException.Message, longerException.Message);
            Assert.EndsWith("\u2026", shorterException.Message, StringComparison.Ordinal);
            Assert.True(shorterException.Message.Length < shorter.Length);
            AssertHygienic(shorterException.Message);
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
        // UnsupportedFeature messages carry long FIXED onboarding prose. The backstop DOES apply here — every
        // constructor routes through it — but it is a no-op in practice and always has been: the construct is
        // a compile-time constant from the parser's own keyword maps, never source text, so no attacker-chosen
        // token can reach this message, and the longest one this factory can compose is comfortably inside
        // the cap. That is why the prose survives intact rather than being exempt from the
        // backstop, and this test pins the CONSEQUENCE (no elision glyph) rather than the mechanism. See the
        // remarks on SqlParseException.Unsupported, which is the authoritative statement of this.
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
