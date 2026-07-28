using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using DeltaSharp.Storage;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// #687 END-TO-END: a named CHECK constraint's predicate is an <b>attacker-authored</b>
/// <c>delta.constraints.&lt;name&gt;</c> table property. On write-time enforcement the write path
/// (<c>DeltaSinkFactory</c> → <c>ConstraintExpressionFrontend.ParseResolveWithInput</c>) hands it to the Core
/// SQL parser, which used to raw-interpolate the offending lexeme into the surfaced exception message. A
/// hostile hand-authored <c>_delta_log</c> could therefore drive raw CR/LF (log-line forgery) and an
/// unbounded render (a 100,000-char token → a 100,151-char message) into whatever structured-log sink the
/// caller reports the failure to.
/// <para>These tests reproduce the red-team's exact payloads through the <b>public write door</b> and assert
/// the surfaced message — and every message in its inner-exception chain — is control-character-free and
/// length-bounded, while the write still fails closed with nothing committed.</para>
/// </summary>
[Collection(SessionExecutionTestCollection.Name)]
public sealed class HostileConstraintDiagnosticHygieneTests : IDisposable
{
    /// <summary>The red-team's payload: a backtick-quoted trailing token carrying a raw CR and LF.</summary>
    private const string CrLfPayload = "TRAIL\r\nFORGED";

    /// <summary>The red-team's flood size — a 100,000-char quoted token rendered a 100,151-char message.</summary>
    private const int FloodLength = 100_000;

    /// <summary>The bounded-render budget: the parser's detail cap plus its fixed position prefix, with slack
    /// for the outer <c>QueryExecutionException</c> wrapper prose. Orders of magnitude below the 100,151-char
    /// render the red-team measured, and independent of the attacker's token length.</summary>
    private const int MaxSurfacedMessageLength = 1024;

    private static readonly StructType AmountSchema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField("amount", IntegerType.Instance, nullable: true),
    });

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "delta-hostile-constraint-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void HostileCheckPredicate_WithRawCrLf_SurfacesNoControlCharacters_AndFailsClosed()
    {
        // The red-team's verbatim repro: delta.constraints.safe = "other > 0 `TRAIL<CR><LF>FORGED`".
        string table = Table("crlf");
        SeedWithHostileConstraint(table, "other > 0 `" + CrLfPayload + "`");

        Exception ex = Assert.ThrowsAny<Exception>(() => Append(table, Amounts(1)));

        AssertChainIsHygienic(ex);
        Assert.False(File.Exists(CommitFile(table, 2))); // fail-closed: the write was rejected, nothing committed
    }

    [Fact]
    public void HostileCheckPredicate_WithOversizedToken_SurfacesBoundedMessage_AndFailsClosed()
    {
        // The red-team's second repro: a 100,000-char quoted trailing token rendered a 100,151-char message.
        string table = Table("flood");
        SeedWithHostileConstraint(table, "other > 0 `" + new string('z', FloodLength) + "`");

        Exception ex = Assert.ThrowsAny<Exception>(() => Append(table, Amounts(1)));

        AssertChainIsHygienic(ex);
        Assert.False(File.Exists(CommitFile(table, 2)));
    }

    [Fact]
    public void HostileCheckPredicate_CombiningBothPayloads_SurfacesHygienicMessage_AndFailsClosed()
    {
        // Both halves of the class at once, in the same token.
        string table = Table("crlf-flood");
        SeedWithHostileConstraint(table, "other > 0 `" + CrLfPayload + new string('x', FloodLength) + "`");

        Exception ex = Assert.ThrowsAny<Exception>(() => Append(table, Amounts(1)));

        AssertChainIsHygienic(ex);
        Assert.False(File.Exists(CommitFile(table, 2)));
    }

    [Fact]
    public void HostileCheckPredicate_WithRawControlGlyph_SurfacesNoControlCharacters_AndFailsClosed()
    {
        // A NUL inside the predicate reaches the LEXER's raw-glyph echo rather than the parser's token echo,
        // covering the other half of the frontend.
        string table = Table("nul-glyph");
        SeedWithHostileConstraint(table, "amount > 0 \u0000");

        Exception ex = Assert.ThrowsAny<Exception>(() => Append(table, Amounts(1)));

        AssertChainIsHygienic(ex);
        Assert.False(File.Exists(CommitFile(table, 2)));
    }

    [Fact]
    public void HostileCheckPredicate_WithFormatCharacters_SurfacesNoBidiOrZeroWidthPayload_AndFailsClosed()
    {
        // Council round 2, item 2: the Cf half of the injection class at the PARSER sink. Pre-fix these
        // survived the Cc-only neutralization set intact — the failures were all and only the Cf payloads,
        // which is what identified the missing category rather than a missing character.
        string table = Table("format-chars");
        SeedWithHostileConstraint(table, "other > 0 `TRAIL\u202EFORGED\u200E\uFEFF\u00AD\u2066\u200B`");

        Exception ex = Assert.ThrowsAny<Exception>(() => Append(table, Amounts(1)));

        AssertChainIsHygienic(ex);
        Assert.Contains("FORGED", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(CommitFile(table, 2)));
    }

    [Fact]
    public void HostileCheckPredicate_TagBlockSmuggling_SurfacesNoInvisiblePayload_AndFailsClosed()
    {
        // Council round 3, item 1: the ASTRAL half of the Cf rule at the PARSER sink. A hostile `_delta_log`
        // JSON string can carry any UTF-16, so the TAG block is reachable here exactly as CR/LF is.
        string table = Table("tag-smuggle");
        SeedWithHostileConstraint(
            table, "other > 0 `TRAIL\U000E0045\U000E0056\U000E0049\U000E004CFORGED`");

        Exception ex = Assert.ThrowsAny<Exception>(() => Append(table, Amounts(1)));

        AssertChainIsHygienic(ex);
        Assert.Contains("FORGED", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ex.Message.EnumerateRunes(), r => r.Value is >= 0xE0020 and <= 0xE007F);
        Assert.False(File.Exists(CommitFile(table, 2)));
    }

    [Fact]
    public void HostileCheckPredicate_SecurityAstralRepro_SurfacesNoInvisiblePayload_AndFailsClosed()
    {
        // Security's round-3 parser repro, pinned verbatim. Pre-fix:
        //   E2E_AstralFormatChars_Parser_StillReachTheSink
        //     … unexpected trailing input 'TRAIL<U+E0001><U+1BCA0>FORGED'
        // Two different astral Cf blocks in one token, so this also covers the non-TAG astral ranges.
        string table = Table("security-astral");
        SeedWithHostileConstraint(table, "other > 0 `TRAIL\U000E0001\U0001BCA0FORGED`");

        Exception ex = Assert.ThrowsAny<Exception>(() => Append(table, Amounts(1)));

        AssertChainIsHygienic(ex);
        Assert.Contains("TRAIL\uFFFD\uFFFDFORGED", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(CommitFile(table, 2)));
    }

    [Fact]
    public void WellFormedCheckPredicate_StillEnforced_ControlIsolatesTheHygieneChange()
    {
        // Control: the hygiene change must not disturb normal enforcement — a well-formed CHECK still parses,
        // resolves, and rejects a violating row (and admits a satisfying one).
        string table = Table("well-formed");
        SeedWithHostileConstraint(table, "amount > 0");

        Assert.Throws<DeltaConstraintViolationException>(() => Append(table, Amounts(-1)));
        Append(table, Amounts(5));
        Assert.True(File.Exists(CommitFile(table, 2)));
    }

    private string Table(string name) => Path.Combine(_root, name);

    private static SparkSession NewSession()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
        return SparkSession.Builder().AppName("hostile-constraint-hygiene").GetOrCreate();
    }

    private static IReadOnlyList<Row> Amounts(params int?[] amounts) =>
        amounts.Select((a, i) => new Row(AmountSchema, i + 1, a)).ToList();

    private static string CommitFile(string table, long version) =>
        Path.Combine(table, "_delta_log", FormattableString.Invariant($"{version:D20}.json"));

    private static void Append(string table, IReadOnlyList<Row> rows)
    {
        using SparkSession spark = NewSession();
        spark.CreateDataFrame(rows, AmountSchema).Write.Format("delta").Mode("append").Save(table);
    }

    // Seeds v0 with clean data, then hand-authors a v1 metadata commit carrying the attacker's
    // delta.constraints.<name> predicate — the hostile `_delta_log` the threat model assumes.
    private static void SeedWithHostileConstraint(string table, string predicate)
    {
        Append(table, Amounts(10));

        string logDir = Path.Combine(table, "_delta_log");
        string metaLine = File.ReadAllLines(Path.Combine(logDir, FormattableString.Invariant($"{0:D20}.json")))
            .First(line => line.Contains("\"metaData\"", StringComparison.Ordinal));
        JsonNode root = JsonNode.Parse(metaLine)!;
        JsonObject metadata = root["metaData"]!.AsObject();
        if (metadata["configuration"] is not JsonObject configuration)
        {
            configuration = new JsonObject();
            metadata["configuration"] = configuration;
        }

        configuration["delta.constraints.safe"] = predicate;
        File.WriteAllText(
            Path.Combine(logDir, FormattableString.Invariant($"{1:D20}.json")), root.ToJsonString() + "\n");
    }

    // The #687 contract at the SINK: no message anywhere in the surfaced chain may carry a control character
    // (or a Unicode line/paragraph separator, which several log viewers render as a newline), and none may be
    // proportional to the attacker's token length.
    private static void AssertChainIsHygienic(Exception thrown)
    {
        for (Exception? current = thrown; current is not null; current = current.InnerException)
        {
            string message = current.Message;
            foreach (Rune rune in message.EnumerateRunes())
            {
                // Rune-based, not char-based: Cc/Zl/Zp are entirely BMP but Cf is NOT (the TAG block
                // U+E0020-U+E007F and friends are astral), so a char-wise scan cannot see an astral format
                // character and would pass vacuously on exactly the payload it is meant to catch.
                Assert.False(
                    Rune.GetUnicodeCategory(rune)
                        is UnicodeCategory.Control or UnicodeCategory.LineSeparator
                            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format,
                    FormattableString.Invariant(
                        $"{current.GetType().Name}.Message carries injection-unsafe U+{rune.Value:X4}"));
            }

            Assert.False(ContainsLoneSurrogate(message), "surfaced message carries a lone surrogate");

            Assert.True(
                message.Length <= MaxSurfacedMessageLength,
                FormattableString.Invariant(
                    $"{current.GetType().Name}.Message length {message.Length} exceeds the bounded render budget"));
        }
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
