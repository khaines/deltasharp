using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
    private const string SyntaxInvocationPattern =
        @"SqlParseException\s*\.\s*Syntax\s*\(";

    private const string UnsupportedInvocationPattern =
        @"SqlParseException\s*\.\s*Unsupported\s*\(";

    private const string NestingTooDeepInvocationPattern =
        @"SqlParseException\s*\.\s*NestingTooDeep\s*\(";

    private const string ConstraintNestingTooDeepInvocationPattern =
        @"SqlParseException\s*\.\s*ConstraintNestingTooDeep\s*\(";

    private const string InternalInvocationPattern =
        @"SqlParseException\s*\.\s*Internal\s*\(";

    private const string ConstructorInvocationPattern =
        @"new\s+SqlParseException\s*\(";

    /// <summary>
    /// Discovers <c>Map*</c> keyword-mapping helpers regardless of declared visibility, so the audit
    /// closes on the same terms as the widened factory set (a <c>public</c>/<c>internal</c> Map helper
    /// cannot escape discovery the way a <c>NonPublic</c>-only reflection would let it).
    /// </summary>
    private const string MapHelperPattern =
        @"(?:private|internal|public)\s+static\s+string\??\s+(?<name>Map\w+)\(";

    /// <summary>
    /// The closed set of <see cref="SqlParseException"/> factory methods, each paired with the source
    /// invocation pattern the audits key off, and the producer inventory's recognizer.
    /// <see cref="SqlParseExceptionFactorySet_IsClosed"/> pins BOTH the reflected producer set (across
    /// every visibility, static and instance, and any SqlParseException-or-base return type) AND these
    /// keys to the same literal four-name set — so a new factory can be silenced neither by a one-line
    /// table edit nor by a base-typed return, and its call-site pattern must be registered here for the
    /// producer inventory to recognize it. The two depth factories take no source text, so they exist
    /// solely to keep the fixed-prose depth diagnostics off the banned public message constructors.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AuditedFactoryInvocations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Syntax"] = SyntaxInvocationPattern,
            ["Unsupported"] = UnsupportedInvocationPattern,
            ["NestingTooDeep"] = NestingTooDeepInvocationPattern,
            ["ConstraintNestingTooDeep"] = ConstraintNestingTooDeepInvocationPattern,
            ["Internal"] = InternalInvocationPattern,
        };

    /// <summary>
    /// The audited Core producer files (repo-<c>src/</c>-relative), shared by the producer inventory,
    /// the block-comment ban, and the RS0030-suppression pin so those guards cannot drift out of step.
    /// </summary>
    private static readonly string[] AuditedProducers =
    [
        "DeltaSharp.Core/Analysis/ConstraintExpressionFrontend.cs",
        "DeltaSharp.Core/Sql/SqlLexer.cs",
        "DeltaSharp.Core/Sql/SqlParseException.cs",
        "DeltaSharp.Core/Sql/SqlParser.cs",
    ];

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
    public void StringLiteralEchoSites_NeverDiscloseDecodedValues_AtAnyRelevantLength(
        string site, string sql, bool viaStatementDoor, string expectedProse, string expectedFrame)
    {
        Assert.NotEmpty(site);
        Assert.NotEmpty(expectedProse);
        Assert.NotEmpty(expectedFrame);

        foreach (string payload in new[] { "K7V2", "P1N4X6", "hunter2" })
        {
            AssertStringLiteralPayloadNotDisclosed(sql, viaStatementDoor, payload, payload);
        }

        foreach (int length in new[] { 8, 60, 140, 400, 520, 1_200, FloodLength })
        {
            string payload = $"SECRET_{length}_" + new string('q', length);
            string marker = $"SECRET_{length}_";
            string endMarker = $"_END_SECRET_{length}";
            payload += endMarker;
            AssertStringLiteralPayloadNotDisclosed(
                sql,
                viaStatementDoor,
                payload,
                marker,
                endMarker);
        }
    }

    [Fact]
    public void DescribeStringLiteralArm_IsExactlyKindOnly()
    {
        string body = MethodBody(
            SqlParserCode(),
            "private static string Describe(");
        MatchCollection arms = Regex.Matches(
            body,
            @"SqlTokenKind\.StringLiteral(?<guard>\s+when[^,\r\n]*?)?\s*=>\s*(?<value>[^,\r\n]+),");
        Match arm = Assert.Single(arms.Cast<Match>());

        Assert.True(string.IsNullOrWhiteSpace(arm.Groups["guard"].Value));
        Assert.Equal("\"string literal\"", arm.Groups["value"].Value.Trim());
    }

    [Fact]
    public void EveryMapHelper_ReturnsOnlyStableTokens()
    {
        string code = SqlParserCode();
        string[] expected =
        [
            "MapNotPredicateKeyword",
            "MapPredicateKeyword",
            "MapStatementKeyword",
            "MapTrailingConstruct",
        ];
        string[] discovered = Regex.Matches(code, MapHelperPattern)
            .Cast<Match>()
            .Select(match => match.Groups["name"].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, discovered);

        ISet<string> registered = ConstructInfoKeys();
        var stableToken = new Regex(@"^(?:null|""(?:[^""\\]|\\.)*"")$");
        foreach (string method in discovered)
        {
            string body = MapMethodBody(code, method);
            MatchCollection switchArms = Regex.Matches(
                body,
                @"=>\s*(?<value>.*?)(?=,\s*(?:\r?\n|$)|\r?\n\s*\})",
                RegexOptions.Singleline);
            Assert.NotEmpty(switchArms);
            Assert.Equal(Regex.Matches(body, "=>").Count, switchArms.Count);
            Assert.All(
                switchArms,
                arm =>
                {
                    string value = arm.Groups["value"].Value.Trim();
                    Assert.Matches(stableToken, value);
                    AssertConstructIsRegistered(value, method, registered);
                });

            MatchCollection earlyReturns = Regex.Matches(
                body,
                @"\breturn\s+(?<value>[^;]+);");
            Assert.All(
                earlyReturns,
                statement =>
                {
                    string value = statement.Groups["value"].Value.Trim();
                    int switchAt = value.IndexOf(" switch", StringComparison.Ordinal);
                    if (switchAt >= 0)
                    {
                        Assert.Matches(
                            new Regex(
                                @"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*(?:\(\))?$"),
                            value[..switchAt].Trim());
                        int openBrace = value.IndexOf('{', switchAt);
                        Assert.True(openBrace >= 0, $"Map switch has no body: {value}");
                        int closeBrace = FindMatchingBrace(value, openBrace);
                        Assert.True(
                            string.IsNullOrWhiteSpace(value[(closeBrace + 1)..]),
                            $"Map switch has an unaudited suffix: {value[(closeBrace + 1)..]}");
                    }
                    else
                    {
                        Assert.Matches(stableToken, value);
                        AssertConstructIsRegistered(value, method, registered);
                    }
                });
        }
    }

    [Fact]
    public void EveryMapHelper_ReturnsOnlyRegisteredConstructs_ForHostileTokens()
    {
        var constructInfo = Assert.IsAssignableFrom<IDictionary>(
            typeof(SqlParseException)
                .GetField("ConstructInfo", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null));
        MethodInfo[] maps = typeof(SqlParser)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => method.Name.StartsWith("Map", StringComparison.Ordinal))
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(4, maps.Length);

        string[] texts =
        [
            "IS", "IN", "LIKE", "BETWEEN", "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE",
            "DROP", "ALTER", "TRUNCATE", "WITH", "VALUES", "SHOW", "DESCRIBE", "DESC",
            "EXPLAIN", "USE", "SET", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS",
            "OUTER", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "UNION", "INTERSECT",
            "EXCEPT", "MINUS", "WINDOW", "CLUSTER", "DISTRIBUTE", "SORT",
            "SECRET_HOSTILE_TOKEN",
            "COL1_SECRET9",
            "héllo_секрет",
            new string('x', 400),
            new string('z', FloodLength),
        ];
        SqlToken[] tokens =
        [
            .. from kind in Enum.GetValues<SqlTokenKind>()
               from text in texts
               from quoted in new[] { false, true }
               from position in new[] { 1, 2, 17, 400 }
               select new SqlToken(kind, text, Position: position, IsQuoted: quoted),
        ];
        var notToken = new SqlToken(SqlTokenKind.Not, "NOT", Position: 1);

        // Reconcile the corpus with the ACTUAL switch case labels: every keyword a Map switches on
        // must be exercised by the hostile-token loop below. A new arm keyed on a keyword absent
        // from `texts` fails HERE (fail-closed) rather than silently escaping the registered-construct
        // check because the loop never feeds that keyword in. This ties the hand-maintained corpus to
        // the source it is meant to cover instead of trusting it as a proxy.
        string parserSource = SqlParserCode();
        var corpus = new HashSet<string>(texts, StringComparer.Ordinal);

        static string[] CaseLabels(string body) => Regex.Matches(
                body,
                @"(?<labels>(?:""(?:[^""\\]|\\.)*""\s*(?:or\s+)?)+)=>")
            .Cast<Match>()
            .SelectMany(arm => Regex.Matches(
                    arm.Groups["labels"].Value,
                    @"""(?<text>(?:[^""\\]|\\.)*)""")
                .Cast<Match>()
                .Select(literal => literal.Groups["text"].Value))
            .ToArray();

        foreach (MethodInfo map in maps)
        {
            string body = MapMethodBody(parserSource, map.Name);
            string[] labels = CaseLabels(body);
            Assert.NotEmpty(labels);
            Assert.All(
                labels,
                label => Assert.True(
                    corpus.Contains(label),
                    $"{map.Name} switches on case label '{label}' absent from the hostile-token "
                        + "corpus; add it to texts[] so the registered-construct loop exercises that arm."));

            // Fail closed on arm SHAPE: every `=>` arm must be accounted for by either an extracted
            // label run or the `_` discard. A `when`-guarded, constant-pattern, or otherwise
            // unreadable arm is invisible to CaseLabels, so without this the reconciliation is
            // fail-OPEN — the arm would be neither reconciled here nor fed to the loop below
            // (Round-12 finding). The output-side registration guard is the primary control; this
            // keeps the input-side reconciliation honest.
            int labelArms = Regex.Matches(
                body,
                @"(?<labels>(?:""(?:[^""\\]|\\.)*""\s*(?:or\s+)?)+)=>").Count;
            int discardArms = Regex.Matches(body, @"(?<![\w""])_\s*=>").Count;
            Assert.Equal(Regex.Matches(body, "=>").Count, labelArms + discardArms);
        }

        // Anti-vacuity: the extractor must pull representative labels from every Map, including the
        // trailing member of each `or`-joined arm (DESC, OUTER, MINUS, SORT) — a regex that dropped
        // or-tails would leave a new unlisted or-tail arm unreconciled.
        string[] allLabels = maps
            .SelectMany(map => CaseLabels(MapMethodBody(parserSource, map.Name)))
            .ToArray();
        Assert.All(
            new[] { "IS", "BETWEEN", "INSERT", "DESC", "JOIN", "OUTER", "MINUS", "SORT" },
            representative => Assert.Contains(representative, allLabels));

        foreach (MethodInfo map in maps)
        {
            foreach (SqlToken token in tokens)
            {
                object?[] args = map.GetParameters().Length == 1
                    ? [token]
                    : [notToken, token];
                string? result = (string?)map.Invoke(null, args);
                Assert.True(
                    result is null || constructInfo.Contains(result),
                    $"{map.Name} returned unregistered construct '{result}' for {token}");
            }
        }
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
        string code = SqlParserCode();
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

    [Fact]
    public void EveryDynamicSyntaxDiagnostic_RoutesLexemesThroughDescribe()
    {
        string code = SqlParserCode() + "\n" + ConstraintExpressionFrontendCode();
        string[] calls = InvocationArguments(code, SyntaxInvocationPattern).ToArray();
        Assert.Equal(
            EchoSites.Length,
            calls.Count(arguments => arguments.Contains("Describe(", StringComparison.Ordinal)));

        foreach (string arguments in calls)
        {
            string message = FirstArgument(arguments);
            Assert.True(
                IsSafeSyntaxMessage(message),
                $"Syntax diagnostic first argument is not fixed/Describe-routed prose: {message}");
        }
    }

    [Fact]
    public void EveryUnsupportedConstruct_IsFixedOrMapped()
    {
        string code = SqlParserCode() + "\n" + ConstraintExpressionFrontendCode();
        string[] calls = InvocationArguments(code, UnsupportedInvocationPattern).ToArray();
        Assert.NotEmpty(calls);

        string[] mapped = ["construct", "unsupported", "negatedPredicate", "predicate"];
        string[] mapHelpers = Regex.Matches(code, MapHelperPattern)
            .Cast<Match>()
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        var usesByName = new Dictionary<string, int>(StringComparer.Ordinal);
        ISet<string> registered = ConstructInfoKeys();
        foreach (string call in calls)
        {
            string construct = FirstArgument(call);
            bool fixedLiteral = Regex.IsMatch(construct, @"^""(?:[^""\\]|\\.)*""$");
            Assert.True(
                fixedLiteral || mapped.Contains(construct, StringComparer.Ordinal),
                $"Unsupported construct is neither fixed nor mapped: {construct}");
            if (fixedLiteral)
            {
                // A direct literal call site must pass a REGISTERED construct — not merely a literal.
                // Since the R13 keystone makes Unsupported fail closed, an unregistered direct construct
                // would silently render generic prose instead of naming the feature; audit the property
                // (registration) here just as the Map output side does (Round-13 finding).
                AssertConstructIsRegistered(construct, "an Unsupported call site", registered);
            }
            else
            {
                usesByName[construct] = usesByName.GetValueOrDefault(construct) + 1;
            }
        }

        foreach (string construct in mapped)
        {
            Assert.DoesNotMatch(
                new Regex($@"\bout\s+(?:var\s+)?{construct}\b"),
                code);
            Match[] allAssignments = Regex.Matches(
                    code,
                    $@"\b{construct}\s*(?<operator>\?\?=|\+=|=)(?!=)\s*(?<value>[^;]+);")
                .Cast<Match>()
                .ToArray();
            Match[] excluded = allAssignments
                .Where(assignment =>
                {
                    int lineStart = code.LastIndexOf('\n', assignment.Index);
                    string prefix = code[(lineStart + 1)..assignment.Index];
                    return Regex.IsMatch(prefix, @"\bExpression\s*$");
                })
                .ToArray();
            if (construct == "predicate")
            {
                Assert.Single(excluded);
            }
            else
            {
                Assert.Empty(excluded);
            }

            Match[] assignments = allAssignments.Except(excluded).ToArray();
            Assert.True(
                assignments.Length >= usesByName.GetValueOrDefault(construct),
                $"No assignment found for mapped construct '{construct}'.");
            Assert.All(
                assignments,
                assignment =>
                {
                    string op = assignment.Groups["operator"].Value;
                    string value = assignment.Groups["value"].Value.Trim();
                    Assert.True(
                        op is "=" or "??=",
                        $"Mapped construct '{construct}' uses unsupported assignment operator '{op}'.");
                    Assert.Matches(
                        new Regex(
                            @"^Map\w+\([^;]+\)(?:\s*\?\?\s*Map\w+\([^;]+\))*$"),
                        value);
                    Assert.All(
                        Regex.Matches(value, @"\b(?<map>Map\w+)\(").Cast<Match>(),
                        map => Assert.Contains(
                            map.Groups["map"].Value,
                            mapHelpers,
                            StringComparer.Ordinal));
                });
        }
    }

    [Fact]
    public void EveryExpectGlyph_IsFixedLiteral()
    {
        string code = SqlParserCode();
        int declarations = Regex.Matches(code, @"private void Expect\(").Count;
        int references = Regex.Matches(code, @"\bExpect\(").Count - declarations;
        int fixedLiteralCalls = Regex.Matches(
            code,
            @"\bExpect\(\s*SqlTokenKind\.[^,\r\n]+,\s*""(?:[^""\\]|\\.)*""\s*\)").Count;

        Assert.Equal(1, declarations);
        Assert.Equal(references, fixedLiteralCalls);
    }

    [Fact]
    public void Producers_ComposeNoMessageAtAConstructionSite()
    {
        // The public message-taking constructors are banned (RS0030) with NO pragma exemption, so the
        // parser and constraint frontend must build every diagnostic through the audited factories
        // (Syntax / Unsupported / NestingTooDeep / ConstraintNestingTooDeep). This is the source-level
        // belt to the compiler ban: not a single `new SqlParseException(...)` remains at a producer
        // call site, so no message — and therefore no lexeme — can be composed at construction.
        // The two depth factories that replaced the former public-ctor sites take NO source text
        // (an Exception and a compile-time int), so their fixed-prose messages cannot carry a lexeme.
        string code = SqlParserCode() + "\n" + ConstraintExpressionFrontendCode();
        Assert.Empty(InvocationArguments(code, ConstructorInvocationPattern));
    }

    [Fact]
    public void EveryCoreSqlParseExceptionProducer_IsAudited()
    {
        // Root the sweep at the whole repository `src/`, not just DeltaSharp.Core, so a producer that
        // appears in another assembly (Storage/Executor/Engine) is a guard failure rather than an
        // unobserved event (Round-12 finding).
        string srcRoot = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(SqlParserSourcePath())!, "..", ".."));

        string[] SourcesUnderSrc() => Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        // Fail-closed inventory keyed on MENTION, not construction syntax: any file that so much as
        // names SqlParseException must be a known file. This catches a new producer written with the
        // target-typed `new(...)` house idiom (which ConstructorInvocationPattern cannot see) and a
        // producer in any assembly — the file simply will not be in this pinned set (Round-12 finding).
        string[] mentioning = SourcesUnderSrc()
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"\bSqlParseException\b"))
            .Select(path => Path.GetRelativePath(srcRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "DeltaSharp.Core/Analysis/AnalysisException.cs",
                "DeltaSharp.Core/Analysis/ConstraintExpressionFrontend.cs",
                "DeltaSharp.Core/Session/SparkSession.cs",
                "DeltaSharp.Core/Sql/SqlLexer.cs",
                "DeltaSharp.Core/Sql/SqlParseErrorKind.cs",
                "DeltaSharp.Core/Sql/SqlParseException.cs",
                "DeltaSharp.Core/Sql/SqlParser.cs",
                "DeltaSharp.Core/Sql/SqlToken.cs",
            },
            mentioning);

        // Within that inventory the actual PRODUCERS (files that build the exception via an audited
        // factory or the public constructor) carry the stricter hygiene bans below.
        string[] producers = SourcesUnderSrc()
            .Where(path =>
            {
                string source = File.ReadAllText(path);
                return AuditedFactoryInvocations.Values
                    .Append(ConstructorInvocationPattern)
                    .Any(pattern => Regex.IsMatch(source, pattern));
            })
            .Select(path => Path.GetRelativePath(srcRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AuditedProducers, producers);

        foreach (string producer in producers)
        {
            string code = File.ReadAllText(Path.Combine(srcRoot, producer));
            Assert.DoesNotMatch(
                new Regex(
                    @"using\s+\w+\s*=\s*[\w.]*SqlParseException\s*;"),
                code);
            Assert.DoesNotMatch(
                new Regex(
                    @"\bSqlParseException\s+\w+\s*=\s*new\s*\("),
                code);
        }
    }

    [Fact]
    public void AuditedProducerSources_ContainNoBlockComments()
    {
        // The source-shape guards read production source with line-comment (`//`) stripping plus
        // quote-skipping hand parsers (MethodBody, FindMatchingParenthesis, FindMatchingBrace), but
        // they do NOT track block comments. A `/* ... */` carrying an unbalanced brace or paren would
        // desync those parsers and silently vacuate the guards for the rest of a method (Round-13
        // finding). Ban block comments in the audited producers so the hand parsers cannot desync;
        // `//` prose is unaffected. (Verified: none of these files contains `//` inside a string
        // literal, so stripping `//` to end-of-line cannot hide a real `/*`.)
        string srcRoot = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(SqlParserSourcePath())!, "..", ".."));
        foreach (string producer in AuditedProducers)
        {
            string source = File.ReadAllText(
                Path.Combine(srcRoot, producer.Replace('/', Path.DirectorySeparatorChar)));
            string withoutLineComments = Regex.Replace(source, @"//[^\n]*", string.Empty);
            Assert.DoesNotMatch(new Regex(@"/\*"), withoutLineComments);
        }
    }

    [Fact]
    public void Rs0030Suppressions_AreAbsentFromEverySqlParseExceptionMentioner()
    {
        // The compiler ban on the public message constructors has exactly ONE sanctioned escape:
        // `#pragma warning disable RS0030`. Round-15 removed every such suppression by routing the
        // fixed-prose depth diagnostics through audited factories. Pin the count to ZERO across EVERY
        // Core file that so much as mentions SqlParseException — not just the four producer files — so
        // the hatch cannot be re-introduced next door (e.g. SparkSession.cs), where a pragma plus a
        // target-typed `new(...)` would otherwise defeat both the compiler ban and the source guards
        // at once (Round-14/15 finding). Files that do NOT mention SqlParseException keep their
        // legitimate RS0030 suppressions (Expression.Compile / ADR-0001) untouched.
        string srcRoot = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(SqlParserSourcePath())!, "..", ".."));
        foreach (string path in Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            string source = File.ReadAllText(path);
            if (!Regex.IsMatch(source, @"\bSqlParseException\b"))
            {
                continue;
            }

            Assert.Empty(Regex.Matches(source, @"#pragma\s+warning\s+disable\s+[^\r\n]*\bRS0030\b")
                .Cast<Match>());
        }
    }

    [Fact]
    public void DepthFactoryCallSites_PassOnlyACaughtVariable()
    {
        // NestingTooDeep chains a caller-supplied inner exception, which ex.ToString() renders (the
        // observable a structured-log sink emits, not audited by AssertHygienic). Require every call
        // site to pass a BARE caught-variable identifier, never a construction expression — so a
        // future author cannot chain `new InvalidOperationException($"…{lexeme}", ex)` and leak a
        // lexeme through ToString() (Round-14 finding). ConstraintNestingTooDeep takes only an int.
        string code = SqlParserCode() + "\n" + ConstraintExpressionFrontendCode();
        string[] nestingCalls = InvocationArguments(code, NestingTooDeepInvocationPattern).ToArray();
        Assert.NotEmpty(nestingCalls);
        Assert.All(
            nestingCalls,
            arguments => Assert.Matches(new Regex(@"^[A-Za-z_]\w*$"), arguments.Trim()));

        string[] constraintCalls =
            InvocationArguments(code, ConstraintNestingTooDeepInvocationPattern).ToArray();
        Assert.All(
            constraintCalls,
            arguments => Assert.Matches(new Regex(@"^[A-Za-z_]\w*$"), arguments.Trim()));
    }

    [Fact]
    public void GlyphBindings_AreClosed_AcrossEveryAuditedProducer()
    {
        // IsSafeSyntaxMessage blesses a `{glyph}` interpolation hole by identifier NAME, on the
        // premise that `glyph` is bound to exactly one source character. LexerGlyphBinding pins that
        // premise in the lexer only; a `Syntax($"… '{glyph}'")` in the parser with a `glyph` parameter
        // bound to a full token bypasses it. Pin every `glyph` binding across all audited producers to
        // the single approved source-character binding (or a parameter), so the name cannot be reused
        // for attacker-controlled text (Round-14 finding).
        string code = SqlParserCode() + "\n" + SqlLexerCode() + "\n" + ConstraintExpressionFrontendCode();
        string[] bindings = Regex.Matches(code, @"\b(?:var|[A-Za-z_][\w.]*)\s+glyph\s*(?<tail>=[^;]+;|[,)])")
            .Cast<Match>()
            .Select(match => match.Groups["tail"].Value.Trim())
            .OrderBy(tail => tail, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { ")", "= c.ToString(CultureInfo.InvariantCulture);" },
            bindings);
    }

    [Fact]
    public void SqlDoor_ThrowsOnlySqlParseException_ForHostileInput()
    {
        // THE type-axis PROPERTY guard. Every other control keys on the type name SqlParseException —
        // a proxy for "no lexeme escapes the SQL door". This asserts the property directly: for a
        // corpus of hostile inputs, every exception that escapes SqlParser.Parse /
        // ParseConstraintExpression MUST be a SqlParseException with a hygienic message and a hygienic
        // (fixed-prose, lexeme-free) inner. It catches leaks the source-shape audits cannot — an
        // IMPLICIT throw such as an unguarded double.Parse on a non-ASCII decimal digit surfaces as a
        // raw FormatException here (Round-15 live-leak finding), and a hostile chained inner surfaces
        // through InnerException.
        string flood = new string('9', 100_000);
        string[] hostile =
        [
            "a > \u0661.\u0665",                              // Arabic-Indic decimal → double.Parse vector
            "a > \u0967\u0968.\u0969",                        // Devanagari decimal
            "a > \u0661e\u0662",                              // non-ASCII exponent
            $"a > {flood}.{flood}",                           // decimal flood
            $"a > {flood}",                                   // integer flood
            "a > 1.5e99999999",                               // overflow exponent
            "a > '" + new string('x', 100_000) + "'",         // string-literal flood
            "a > 'TRAIL\r\nFORGED SECRET'",                   // CR/LF + decoded literal
            "a \u202E > 0",                                   // RTL override
            "\uD800 a > 0",                                   // lone surrogate
            "SELECT * FROM t WHERE a > \u0661.\u0665",        // statement door → same decimal vector
            "SELECT " + new string('z', 100_000) + " FROM t",
        ];

        foreach (string sql in hostile)
        {
            foreach (Action door in new Action[]
            {
                () => SqlParser.Parse(sql),
                () => SqlParser.ParseConstraintExpression(sql),
            })
            {
                Exception? thrown = Record.Exception(door);
                if (thrown is null)
                {
                    continue;
                }

                Assert.IsType<SqlParseException>(thrown);
                AssertHygienic(thrown.Message);
                if (thrown.InnerException is not null)
                {
                    AssertHygienic(thrown.InnerException.Message);
                }
            }
        }
    }

    [Fact]
    public void AuditedProducerExceptionConstructions_ComposeAFixedLiteralMessage()
    {
        // Source ALLOW-LIST complementing the fail-closed door catch. Round-16 replaced a construction
        // syntax proxy with a deny-list over interpolation syntax; both were bypassed (+ concatenation,
        // helper indirection, a hoisted local, a BCL type with a public message ctor). Flip to an
        // allow-list over the PROPERTY: EVERY `new <…>Exception(` in an audited producer — regardless
        // of construction shape or exception type — must pass a fixed-literal-chain first argument, so
        // no lexeme can be composed into any exception the producer builds. SqlParseException's own
        // message ctors are separately RS0030-banned, so its exact type name is skipped (it is built
        // only through the private ctor from the audited factories).
        string srcRoot = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(SqlParserSourcePath())!, "..", ".."));
        foreach (string producer in AuditedProducers)
        {
            string source = File.ReadAllText(
                Path.Combine(srcRoot, producer.Replace('/', Path.DirectorySeparatorChar)));
            foreach (Match construction in Regex.Matches(
                source,
                @"\bnew\s+(?<type>[A-Za-z_][\w.]*Exception)\s*\("))
            {
                string type = construction.Groups["type"].Value;
                if (type == "SqlParseException"
                    || type.EndsWith(".SqlParseException", StringComparison.Ordinal))
                {
                    continue;
                }

                int openParen = construction.Index + construction.Length - 1;
                int closeParen = FindMatchingParenthesis(source, openParen);
                string arguments = source[(openParen + 1)..closeParen];
                int comma = TopLevelOperator(arguments, ',');
                string firstArgument = comma < 0 ? arguments : arguments[..comma];
                Assert.True(
                    IsLiteralChain(firstArgument),
                    $"{producer}: new {type}(...) composes a non-literal message: {firstArgument.Trim()}");
            }
        }
    }

    [Fact]
    public void BothDoors_CarryTheFailClosedTypeAxisBackstop()
    {
        // The type axis is closed STRUCTURALLY by a fail-closed backstop at each door boundary —
        // `catch (Exception ex) when (ex is not SqlParseException) { … throw SqlParseException.Internal(); }`
        // — so any non-SqlParseException (a BCL conversion, an unexpected internal error, a future
        // producer's throw) becomes fixed prose with the raw inner dropped, and no lexeme escapes on the
        // type axis. Pin BOTH backstops so removing one (letting a raw exception escape) fails closed.
        string code = SqlParserCode();
        int backstops = Regex.Matches(
            code,
            @"catch\s*\(\s*Exception\s+\w+\s*\)\s*when\s*\(\s*\w+\s+is\s+not\s+SqlParseException\s*\)"
                + @"[^}]*?SqlParseException\s*\.\s*Internal\s*\(\s*\)",
            RegexOptions.Singleline).Count;
        Assert.Equal(2, backstops);
    }

    [Fact]
    public void SqlParseExceptionFactorySet_IsClosed()
    {
        // Discover every producer DECLARED on SqlParseException that hands back a SqlParseException —
        // OR a base type it is assignable to (SystemException/Exception), the ThrowHelper idiom —
        // across every visibility and both static and instance. Keying on the exact return type (R12)
        // let an `internal static Exception Detail(...)` escape the set; a base-typed return + Instance
        // now cannot hide a producer.
        string[] factories = typeof(SqlParseException)
            .GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method =>
                method.ReturnType.IsAssignableFrom(typeof(SqlParseException))
                && typeof(Exception).IsAssignableFrom(method.ReturnType))
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // Pin the closed set to the LITERAL factory names, NOT to AuditedFactoryInvocations.Keys — so a
        // new factory cannot be waved through by a one-line table edit (Round-12 finding). The table is
        // then reconciled against the SAME literal pin, so the producer inventory's recognizer stays
        // complete while neither the reflected set nor the table can be silenced independently.
        string[] pinned =
            ["ConstraintNestingTooDeep", "Internal", "NestingTooDeep", "Syntax", "Unsupported"];
        Assert.Equal(pinned, factories);
        Assert.Equal(
            pinned,
            AuditedFactoryInvocations.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray());

        // Each audited factory's invocation pattern must target that exact factory name (and only
        // it), so the pattern the source audits run cannot drift onto a sibling or a renamed factory.
        foreach ((string name, string pattern) in AuditedFactoryInvocations)
        {
            Assert.Matches(pattern, $"SqlParseException.{name}(");
            Assert.DoesNotMatch(pattern, $"SqlParseException.{name}Extra(");
        }
    }

    [Fact]
    public void UnsupportedFallback_ForUnregisteredConstruct_RendersFixedProse_NotVerbatim()
    {
        // The Unsupported factory FAILS CLOSED: an unregistered construct token renders fixed generic
        // prose, never the raw token, so no future or mis-wired producer (any Map arm shape, direct
        // call site, …) can leak an unregistered construct verbatim through the message. This is the
        // Round-12 keystone that makes the corpus/case-label reconciliation defense-in-depth rather
        // than the sole control. The stable token is still preserved on Construct for programmatic use.
        const string Unregistered = "ZZ_UNREGISTERED_SECRET_CONSTRUCT";
        MethodInfo unsupported = typeof(SqlParseException).GetMethod(
            "Unsupported",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var ex = (SqlParseException)unsupported.Invoke(null, [Unregistered, 7])!;

        Assert.DoesNotContain(Unregistered, ex.Message, StringComparison.Ordinal);
        Assert.Contains("an unsupported SQL construct", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Unregistered, ex.Construct);
        AssertHygienic(ex.Message);
    }

    [Fact]
    public void UnsupportedConstruct_IsLengthBoundedAndSanitized_OnAssignment()
    {
        // Construct is length-bounded AND control-char sanitized on assignment (MaxConstructLength via
        // DiagnosticText.Sanitize) so it cannot become an unbounded, injection-carrying raw-token sink
        // even if a future or mis-wired producer ever hands Unsupported an oversized/hostile value —
        // the last place a raw token could otherwise survive after the keystone. The doc claims BOTH
        // properties, so audit BOTH (Round-14 finding: the length half was tested, the sanitize half
        // was not).
        MethodInfo unsupported = typeof(SqlParseException).GetMethod(
            "Unsupported",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var bounded = (SqlParseException)unsupported.Invoke(null, [new string('z', 300), 1])!;
        Assert.NotNull(bounded.Construct);
        Assert.True(
            bounded.Construct!.Length <= 129,
            $"Construct was {bounded.Construct.Length} chars; expected it bounded to MaxConstructLength "
                + "(128) plus at most a one-character truncation ellipsis.");

        var hostile = (SqlParseException)unsupported.Invoke(null, ["TRAIL\r\nFORGED\u202Ehostile", 1])!;
        AssertHygienic(hostile.Construct!);
    }

    [Fact]
    public void EveryLexerSyntaxDiagnostic_UsesApprovedProse()
    {
        string[] calls = InvocationArguments(SqlLexerCode(), SyntaxInvocationPattern).ToArray();
        Assert.Equal(6, calls.Length);
        Assert.All(
            calls,
            arguments => Assert.True(
                IsSafeSyntaxMessage(FirstArgument(arguments)),
                $"Lexer syntax diagnostic is not fixed or approved glyph prose: {FirstArgument(arguments)}"));
    }

    [Fact]
    public void LexerCannotIntroduceUnauditedMessageSurfaces()
    {
        string code = SqlLexerCode();
        Assert.DoesNotMatch(new Regex(UnsupportedInvocationPattern), code);
        Assert.DoesNotMatch(new Regex(ConstructorInvocationPattern), code);
    }

    [Fact]
    public void LexerGlyphBinding_IsExactlyOneSourceCharacter()
    {
        string code = SqlLexerCode();
        MatchCollection assignments = Regex.Matches(
            code,
            @"\bglyph\s*(?<operator>\?\?=|\+=|=)(?!=)\s*(?<value>[^;]+);");

        Match declaration = Assert.Single(assignments.Cast<Match>());
        Assert.Equal("=", declaration.Groups["operator"].Value);
        Assert.Equal(
            "c.ToString(CultureInfo.InvariantCulture)",
            declaration.Groups["value"].Value.Trim());
    }

    [Fact]
    public void LexerUnexpectedCharacter_NeverDisclosesFollowingLiteral()
    {
        foreach (int length in new[] { 8, 60, 140, 400, 520, 1_200, FloodLength })
        {
            string marker = $"SECRET_{length}_";
            string payload = marker + new string('q', length);
            SqlParseException ex = ParseSyntaxError(
                $"a > 0 # '{payload}'",
                viaStatementDoor: false);

            Assert.Contains("unexpected character '#'", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(payload, ex.Message, StringComparison.Ordinal);
            AssertHygienic(ex.Message);
        }
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

    private static void AssertStringLiteralPayloadNotDisclosed(
        string sql,
        bool viaStatementDoor,
        string payload,
        params string[] forbidden)
    {
        string probe = sql.Replace("SEC\r\nRET_PAYLOAD", payload, StringComparison.Ordinal);
        Assert.NotEqual(sql, probe);

        SqlParseException ex = ParseSyntaxError(probe, viaStatementDoor);
        Assert.DoesNotContain(payload, ex.Message, StringComparison.Ordinal);
        Assert.All(
            forbidden,
            value => Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal));
        if (payload.Contains('q'))
        {
            Assert.DoesNotContain(new string('q', 16), ex.Message, StringComparison.Ordinal);
        }
        Assert.Contains("string literal", ex.Message, StringComparison.Ordinal);
        AssertHygienic(ex.Message);
    }

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

    private static string SqlParserCode()
    {
        string path = SqlParserSourcePath();
        Assert.True(File.Exists(path), $"SqlParser source does not exist at '{path}'");
        string source = File.ReadAllText(path);
        return string.Join(
            '\n',
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    private static IEnumerable<string> InvocationArguments(string source, string invocationPattern)
    {
        foreach (Match invocation in Regex.Matches(source, invocationPattern))
        {
            int openParen = invocation.Index + invocation.Length - 1;
            int closeParen = FindMatchingParenthesis(source, openParen);
            yield return source[(openParen + 1)..closeParen];
        }
    }

    private static int FindMatchingParenthesis(string source, int openParen)
    {
        int depth = 0;
        for (int i = openParen; i < source.Length; i++)
        {
            if (source[i] is '"' or '\'')
            {
                i = SkipQuoted(source, i);
                continue;
            }

            if (source[i] == '(')
            {
                depth++;
            }
            else if (source[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Unbalanced invocation at source offset {openParen}.");
    }

    private static ISet<string> ConstructInfoKeys()
    {
        var constructInfo = Assert.IsAssignableFrom<IDictionary>(
            typeof(SqlParseException)
                .GetField("ConstructInfo", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null));
        return constructInfo.Keys
            .Cast<object>()
            .Select(key => (string)key)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string MapMethodBody(string source, string mapName)
    {
        // Anchor on the declaration and fail closed if the visibility-agnostic marker is ambiguous
        // (a future call site written `string? MapX(`, or one Map name prefixing another) rather than
        // silently taking the first match (Round-13 finding).
        string marker = $"string? {mapName}(";
        Assert.Single(Regex.Matches(source, Regex.Escape(marker)).Cast<Match>());
        return MethodBody(source, marker);
    }

    private static void AssertConstructIsRegistered(
        string value, string origin, ISet<string> registered)
    {
        if (value == "null")
        {
            return;
        }

        // A Map* helper or a direct Unsupported call site can only hand over null or a quoted stable
        // token; that token must be a registered ConstructInfo key, or SqlParseException.Unsupported
        // renders generic fallback prose instead of the construct's intended message. Auditing the
        // OUTPUT literals (independent of arm/label SHAPE) closes the when-guard / constant-pattern /
        // early-return escapes the input-side reconciliation cannot see (Round-12/13 findings).
        string construct = value.Trim('"');
        Assert.True(
            registered.Contains(construct),
            $"{origin} produces construct '{construct}', which is not a registered ConstructInfo key; "
                + "register it (or SqlParseException.Unsupported renders generic fallback prose for it).");
    }

    private static string MethodBody(string source, string marker)
    {
        int method = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(method >= 0, $"method marker not found: {marker}");
        int openBrace = source.IndexOf('{', method + marker.Length);
        Assert.True(openBrace >= 0, $"method body not found: {marker}");
        int depth = 0;
        for (int i = openBrace; i < source.Length; i++)
        {
            if (source[i] is '"' or '\'')
            {
                i = SkipQuoted(source, i);
                continue;
            }

            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[(openBrace + 1)..i];
            }
        }

        throw new InvalidOperationException($"Unbalanced method body: {marker}");
    }

    private static int FindMatchingBrace(string source, int openBrace)
    {
        int depth = 0;
        for (int i = openBrace; i < source.Length; i++)
        {
            if (source[i] is '"' or '\'')
            {
                i = SkipQuoted(source, i);
                continue;
            }

            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Unbalanced switch body at source offset {openBrace}.");
    }

    private static string FirstArgument(string arguments)
    {
        int depth = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] is '"' or '\'')
            {
                i = SkipQuoted(arguments, i);
                continue;
            }

            if (arguments[i] is '(' or '[' or '{')
            {
                depth++;
            }
            else if (arguments[i] is ')' or ']' or '}')
            {
                depth--;
            }
            else if (arguments[i] == ',' && depth == 0)
            {
                return arguments[..i].Trim();
            }
        }

        return arguments.Trim();
    }

    private static int SkipQuoted(string source, int quote)
    {
        char delimiter = source[quote];
        bool verbatim = delimiter == '"'
            && (quote > 0 && source[quote - 1] == '@'
                || quote > 1 && source[quote - 2] == '@');
        for (int i = quote + 1; i < source.Length; i++)
        {
            if (!verbatim && source[i] == '\\')
            {
                i++;
                continue;
            }

            if (source[i] != delimiter)
            {
                continue;
            }

            if (verbatim && i + 1 < source.Length && source[i + 1] == delimiter)
            {
                i++;
                continue;
            }

            return i;
        }

        throw new InvalidOperationException($"Unterminated quoted token at source offset {quote}.");
    }

    private static bool IsSafeSyntaxMessage(string expression)
    {
        const string ExpectedGlyphMessage =
            "$\"expected '{glyph}' but found '{Describe(Current)}'\"";
        const string UnexpectedGlyphMessage =
            "$\"unexpected character '{glyph}'\"";
        if (expression.Contains("{glyph}", StringComparison.Ordinal)
            && !string.Equals(expression.Trim(), ExpectedGlyphMessage, StringComparison.Ordinal)
            && !string.Equals(expression.Trim(), UnexpectedGlyphMessage, StringComparison.Ordinal))
        {
            return false;
        }

        string normalized = Regex.Replace(
                expression,
                @"\{Describe\([^{}]*\)\}",
                "DESCRIBED_TOKEN")
            .Replace("{glyph}", "FIXED_GLYPH", StringComparison.Ordinal)
            .Replace("$\"", "\"", StringComparison.Ordinal);
        if (Regex.IsMatch(normalized, @"\{[^{}]+\}"))
        {
            return false;
        }

        if (IsLiteralChain(normalized))
        {
            return true;
        }

        int question = TopLevelOperator(normalized, '?');
        int colon = question < 0 ? -1 : TopLevelOperator(normalized, ':', question + 1);
        return question >= 0
            && colon > question
            && IsLiteralChain(normalized[(question + 1)..colon])
            && IsLiteralChain(normalized[(colon + 1)..]);
    }

    private static bool IsLiteralChain(string expression) =>
        Regex.IsMatch(
            expression.Trim(),
            @"^""(?:[^""\\]|\\.)*""(?:\s*\+\s*""(?:[^""\\]|\\.)*"")*$",
            RegexOptions.Singleline);

    private static int TopLevelOperator(string expression, char op, int start = 0)
    {
        int depth = 0;
        for (int i = start; i < expression.Length; i++)
        {
            if (expression[i] is '"' or '\'')
            {
                i = SkipQuoted(expression, i);
                continue;
            }

            if (expression[i] is '(' or '[' or '{')
            {
                depth++;
            }
            else if (expression[i] is ')' or ']' or '}')
            {
                depth--;
            }
            else if (expression[i] == op && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static string ConstraintExpressionFrontendCode()
    {
        string path = Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(SqlParserSourcePath())!,
                "..",
                "Analysis",
                "ConstraintExpressionFrontend.cs"));
        Assert.True(File.Exists(path), $"constraint frontend source does not exist at '{path}'");
        string source = File.ReadAllText(path);
        return string.Join(
            '\n',
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    private static string SqlLexerCode()
    {
        string path = Path.Combine(Path.GetDirectoryName(SqlParserSourcePath())!, "SqlLexer.cs");
        Assert.True(File.Exists(path), $"SQL lexer source does not exist at '{path}'");
        string source = File.ReadAllText(path);
        return string.Join(
            '\n',
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
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
