using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// #687 END-TO-END, analyzer half. Sibling of <see cref="HostileConstraintDiagnosticHygieneTests"/>, which
/// covers the SQL <b>parser</b>'s token echo on the same hostile path.
/// <para>
/// A <c>delta.constraints.&lt;name&gt;</c> CHECK predicate that PARSES cleanly but fails ANALYSIS reaches a
/// second, independent echo: the analyzer renders the resolved expression tree into its message, and a string
/// literal renders its decoded <em>value</em>. So a predicate the parser handles perfectly —
/// <c>amount &gt; 'A&lt;CR&gt;&lt;LF&gt;FORGED'</c> — still drove raw CR/LF into the surfaced
/// <c>QueryExecutionException.Message</c> (measured 194 chars with a raw CR and LF), and a 100 000-character
/// literal drove a 100 195-character surfaced message.
/// </para>
/// <para>These tests drive the exact payloads through the <b>public write door</b> against a hand-authored
/// hostile <c>_delta_log</c> and assert the surfaced message — and every message in its inner-exception chain
/// — is control-character-free and length-bounded, while the write still fails closed.</para>
/// </summary>
[Collection(SessionExecutionTestCollection.Name)]
public sealed class HostileConstraintAnalyzerHygieneTests : IDisposable
{
    private const string CrLfPayload = "A\r\nFORGED";

    private const int FloodLength = 100_000;

    /// <summary>The bounded-render budget at the sink: the analyzer's whole-message backstop plus slack for the
    /// outer <c>QueryExecutionException</c> wrapper prose. Orders of magnitude below the 100 195-char render
    /// measured pre-fix, and independent of the attacker's literal length.</summary>
    private const int MaxSurfacedMessageLength = 1280;

    private static readonly StructType AmountSchema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField("amount", IntegerType.Instance, nullable: true),
    });

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "delta-hostile-analyzer-" + Guid.NewGuid().ToString("N"));

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
    public void HostileLiteral_WithRawCrLf_SurfacesNoControlCharacters_AndFailsClosed()
    {
        // Pre-fix: QueryExecutionException[194] carrying a raw CR and LF —
        // cannot resolve '(amount > "A<CR><LF>FORGED")' due to data type mismatch: …
        string table = Table("crlf");
        SeedWithHostileConstraint(table, "amount > '" + CrLfPayload + "'");

        Exception ex = Assert.ThrowsAny<Exception>(() => Append(table, Amounts(1)));

        AssertChainIsHygienic(ex);

        // Non-vacuity: the analyzer diagnostic really is the one being surfaced (this is not passing because
        // some earlier, already-hygienic failure short-circuited the path).
        Assert.Contains("data type mismatch", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FORGED", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(CommitFile(table, 2)));
    }

    [Fact]
    public void HostileLiteral_Oversized_SurfacesBoundedMessage_AndFailsClosed()
    {
        // Pre-fix: QueryExecutionException[100195] / AnalysisException[100156].
        string table = Table("flood");
        SeedWithHostileConstraint(table, "amount > '" + new string('z', FloodLength) + "'");

        Exception ex = Assert.ThrowsAny<Exception>(() => Append(table, Amounts(1)));

        AssertChainIsHygienic(ex);
        Assert.Contains("data type mismatch", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(CommitFile(table, 2)));
    }

    [Fact]
    public void HostileLiteral_CombiningBothPayloads_SurfacesHygienicMessage_AndFailsClosed()
    {
        string table = Table("crlf-flood");
        SeedWithHostileConstraint(
            table, "amount > '" + CrLfPayload + new string('x', FloodLength) + "'");

        Exception ex = Assert.ThrowsAny<Exception>(() => Append(table, Amounts(1)));

        AssertChainIsHygienic(ex);
        Assert.False(File.Exists(CommitFile(table, 2)));
    }

    [Fact]
    public void HostileLiteral_FormatCharacters_SurfaceNoBidiOrZeroWidthPayload_AndFailClosed()
    {
        // Council round 2, item 2 (Security + red-team, independently): before UnicodeCategory.Format was added
        // to IsInjectionUnsafe, this reached the real sink intact —
        //   E2E_FormatChars_SurviveToTheSink: len=195 :: … '(amount > "A<U+202E>FORGED<U+200E><U+FEFF>")' …
        // U+202E visually reverses the remainder of a rendered log line and the zero-width characters hide or
        // reorder text during incident triage. It cannot forge a NEW record (hence Medium, not High), but it
        // serves the same "make the log lie" objective this PR exists to close.
        string table = Table("format-chars");
        SeedWithHostileConstraint(table, "amount > 'A\u202EFORGED\u200E\uFEFF\u00AD\u2066\u200B'");

        Exception ex = Assert.ThrowsAny<Exception>(() => Append(table, Amounts(1)));

        AssertChainIsHygienic(ex);

        // Non-vacuity: the analyzer diagnostic really is the one surfaced, and the neutralized text is present.
        Assert.Contains("data type mismatch", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FORGED", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(CommitFile(table, 2)));
    }

    [Fact]
    public void HostileLiteral_InsideNotOperand_SurfacesHygienicMessage_AndFailsClosed()
    {
        // A second analyzer throw site (ExpressionCoercion.RequireBoolean), reached without any comparison —
        // pre-fix: QueryExecutionException[139] with raw CR/LF.
        string table = Table("not-operand");
        SeedWithHostileConstraint(table, "NOT '" + CrLfPayload + "'");

        Exception ex = Assert.ThrowsAny<Exception>(() => Append(table, Amounts(1)));

        AssertChainIsHygienic(ex);
        Assert.False(File.Exists(CommitFile(table, 2)));
    }

    private string Table(string name) => Path.Combine(_root, name);

    private static SparkSession NewSession()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
        return SparkSession.Builder().AppName("hostile-analyzer-hygiene").GetOrCreate();
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
    // proportional to the attacker's literal length.
    private static void AssertChainIsHygienic(Exception thrown)
    {
        for (Exception? current = thrown; current is not null; current = current.InnerException)
        {
            string message = current.Message;
            for (int i = 0; i < message.Length; i++)
            {
                char c = message[i];
                Assert.False(
                    char.IsControl(c)
                    || char.GetUnicodeCategory(c)
                        is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format,
                    FormattableString.Invariant(
                        $"{current.GetType().Name}.Message carries injection-unsafe U+{(int)c:X4} at index {i}"));
            }

            Assert.True(
                message.Length <= MaxSurfacedMessageLength,
                FormattableString.Invariant(
                    $"{current.GetType().Name}.Message length {message.Length} exceeds the bounded render budget"));
        }
    }
}
