using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DeltaSharp.Analysis;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Analysis;

/// <summary>
/// #687 (analyzer half). The SQL parser's token echo was bounded and neutralized first; this covers the
/// <b>sibling</b> leak on the same hostile path — the analyzer's own diagnostics.
/// <para>
/// A <c>delta.constraints.&lt;name&gt;</c> CHECK predicate is attacker-authored in the hostile-<c>_delta_log</c>
/// threat model, and the write path hands it to <c>ConstraintExpressionFrontend</c> → the analyzer. The parser
/// is already hygienic about literals (it reports the token KIND, <c>string literal</c>, never the value), but
/// once the predicate is parsed the analyzer renders the <b>resolved expression tree</b> into its message —
/// and a <see cref="Literal"/> leaf renders its decoded <em>value</em>. So
/// <c>amount &gt; 'A&lt;CR&gt;&lt;LF&gt;FORGED'</c> drove raw CR/LF into the surfaced message (log-line
/// forgery) and a 100&#160;000-character literal drove a 100&#160;156-character render.
/// </para>
/// <para>
/// The fix is two composed layers, mirroring the parser's: <c>CoercionHelpers.DiagnosticReference</c> bounds
/// the rendered reference at each diagnostic call site (so the surrounding explanatory prose survives), and
/// the single private <see cref="AnalysisException"/> constructor sanitizes the whole composed message as a
/// backstop (so every factory — including ones that never touch the expression renderer, and ones added
/// later — is covered by construction).
/// </para>
/// </summary>
public sealed class AnalyzerDiagnosticHygieneTests
{
    /// <summary>The red-team payload shape: raw CR and LF, which forge a log line at a structured-log sink.</summary>
    private const string CrLf = "\r\n";

    /// <summary>The red-team's flood size — a 100 000-character literal rendered a 100 156-character message.</summary>
    private const int FloodLength = 100_000;

    private static readonly StructType AmountSchema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField("amount", IntegerType.Instance, nullable: true),
        new StructField("label", StringType.Instance, nullable: true),
    });

    /// <summary>Hostile CHECK predicates, each reaching a DIFFERENT analyzer diagnostic that interpolates
    /// attacker-authored text. The first four go through the expression renderer (the confirmed
    /// <c>DataTypeMismatch</c> leak); the rest reach factories that interpolate a raw name, a raw CANDIDATE
    /// list, or a raw function name instead — they never touch the renderer, so only the constructor backstop
    /// covers them.
    /// <para>Payloads span both halves of the injection class: <c>Cc</c> (raw CR/LF, forges a new log record)
    /// and <c>Cf</c> format characters (U+202E RTL-override visually reverses the rest of a rendered line;
    /// U+200B/U+200E/U+FEFF hide or reorder text during triage — they cannot forge a record but serve the same
    /// "make the log lie" objective).</para></summary>
    public static TheoryData<string, string> HostilePredicates() => new()
    {
        { "comparison-mismatch", "amount > 'A" + CrLf + "FORGED'" },
        { "arithmetic-mismatch", "(amount + 'A" + CrLf + "FORGED') > 0" },
        { "not-operand-mismatch", "NOT 'A" + CrLf + "FORGED'" },
        { "nested-in-conjunction", "amount > 0 AND amount > 'A" + CrLf + "FORGED'" },
        { "unresolved-column-name", "`A" + CrLf + "FORGED` > 0" },
        { "struct-field-on-scalar", "amount.`A" + CrLf + "FORGED` > 0" },

        // Cf (format) payloads, at the renderer and at the backstop-only factories.
        { "rtl-override-literal", "amount > 'A\u202EFORGED'" },
        { "zero-width-space-literal", "amount > 'A\u200BFORGED'" },
        { "bom-literal", "amount > 'A\uFEFFFORGED'" },
        { "lrm-and-soft-hyphen-literal", "amount > 'A\u200E\u00ADFORGED'" },
        { "isolate-literal", "amount > 'A\u2066FORGED\u2069'" },
        { "rtl-override-column", "`A\u202EFORGED` > 0" },

        // Astral Cf (council round 3, item 1): the TAG block encodes arbitrary ASCII invisibly. `IsInjectionUnsafe`
        // takes a char and cannot see these; they are neutralized in Sanitize's surrogate-pair branch.
        { "tag-smuggled-literal", "amount > 'safe\U000E0045\U000E0056\U000E0049\U000E004CFORGED'" },
        { "tag-smuggled-column", "`safe\U000E0045\U000E0056\U000E0049\U000E004CFORGED` > 0" },
        { "astral-music-format-literal", "amount > 'A\U0001D173FORGED'" },
    };

    [Theory]
    [MemberData(nameof(HostilePredicates))]
    public void HostilePredicate_AnalyzerDiagnostic_CarriesNoControlCharacters(string site, string predicate)
    {
        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => ConstraintExpressionFrontend.ParseAndResolve(predicate, AmountSchema));

        AssertHygienic(site, ex.Message);

        // Non-vacuity: the diagnostic really did echo the attacker's text (neutralized), so this case is
        // exercising the sanitizer rather than passing because nothing was interpolated at all.
        Assert.Contains("FORGED", ex.Message, StringComparison.Ordinal);
        Assert.Contains('\uFFFD', ex.Message);
    }

    [Fact]
    public void HostilePredicate_WithOversizedLiteral_RendersBoundedMessage()
    {
        // The red-team's flood shape: pre-fix this rendered a 100 156-character AnalysisException.Message
        // (and a 100 195-character surfaced QueryExecutionException.Message).
        string predicate = "amount > '" + new string('z', FloodLength) + "'";

        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => ConstraintExpressionFrontend.ParseAndResolve(predicate, AmountSchema));

        AssertHygienic("flood", ex.Message);

        // The render is bounded by the REFERENCE cap, well below the whole-message backstop — i.e. the
        // per-reference layer is what bounds this, and it leaves room for the explanatory prose.
        Assert.True(
            ex.Message.Length < AnalysisException.MaxMessageLength,
            FormattableString.Invariant($"message length {ex.Message.Length} reached the backstop"));
        Assert.Contains("requires comparable operand types", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'int' and 'string'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderedReference_IsIndependentOfAttackerInputLength()
    {
        // The property that actually closes the unbounded-render class: growing the payload by 10x must not
        // grow the message at all.
        string Message(int payload) =>
            Assert.Throws<AnalysisException>(
                    () => ConstraintExpressionFrontend.ParseAndResolve(
                        "amount > '" + new string('z', payload) + "'", AmountSchema))
                .Message;

        Assert.Equal(Message(10_000), Message(100_000));
    }

    [Fact]
    public void WholeMessageBackstop_BoundsAFactoryThatBypassesTheExpressionRenderer()
    {
        // UnresolvedColumn interpolates the raw name AND the raw candidate list — it never touches
        // CoercionHelpers, so only the constructor backstop covers it. A hostile _delta_log authors the
        // schema too, so the candidate names are attacker-controlled as well.
        var hostileSchema = new StructType(
            Enumerable.Range(0, 40)
                .Select(i => new StructField(
                    FormattableString.Invariant($"c{i}_") + new string('w', 500), IntegerType.Instance, true))
                .ToArray());

        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => ConstraintExpressionFrontend.ParseAndResolve("missing > 0", hostileSchema));

        AssertHygienic("wide-hostile-schema", ex.Message);
        Assert.True(
            ex.Message.Length <= AnalysisException.MaxMessageLength + 1,
            FormattableString.Invariant($"message length {ex.Message.Length} exceeds the backstop"));
    }

    /// <summary>The factories the whole-message backstop <b>uniquely</b> protects: each takes direct string
    /// input, composes its own message, and never reaches the expression renderer — so the per-reference layer
    /// cannot help them. They are exercised through their public factories rather than through SQL because the
    /// M1 SQL door rejects a function call at PARSE time, so a hostile CHECK predicate cannot reach
    /// <c>UnknownFunction</c> today; the analyzer can still raise them (a DataFrame-API path, or the EPIC-07
    /// frontend), and the backstop must hold when it does.</summary>
    public static TheoryData<string, Func<Exception>> BackstopOnlyFactories() => new()
    {
        {
            "unknown-function",
            () => AnalysisException.UnknownFunction(
                "A" + CrLf + "\u202EFORGED", new DataType[] { IntegerType.Instance })
        },
        {
            "unknown-function-flood",
            () => AnalysisException.UnknownFunction(new string('z', FloodLength), Array.Empty<DataType>())
        },
        {
            "table-or-view-not-found",
            () => AnalysisException.TableOrViewNotFound(new[] { "ns", "A" + CrLf + "\u202EFORGED" })
        },
        {
            "table-or-view-not-found-flood",
            () => AnalysisException.TableOrViewNotFound(new[] { new string('z', FloodLength) })
        },
        {
            "ambiguous-reference",
            () => AnalysisException.AmbiguousReference(
                "A" + CrLf + "\u202EFORGED",
                new[]
                {
                    new AttributeReference("A\u200BFORGED", IntegerType.Instance, true, new ExprId(1)),
                    new AttributeReference(new string('z', FloodLength), IntegerType.Instance, true, new ExprId(2)),
                })
        },
        {
            "invalid-function-argument",
            () => AnalysisException.InvalidFunctionArgument(
                "A\uFEFFFORGED", Array.Empty<DataType>(), "argument #1 ('" + new string('z', FloodLength) + "')")
        },
    };

    [Theory]
    [MemberData(nameof(BackstopOnlyFactories))]
    public void BackstopOnlyFactory_IsBoundedAndNeutralized(string site, Func<Exception> build)
    {
        ArgumentNullException.ThrowIfNull(build);

        Exception ex = build();

        AssertHygienic(site, ex.Message);
    }

    [Fact]
    public void StructuredProperties_StayUnsanitized_SoCallerMatchingKeepsWorking()
    {
        // The message is prose; Reference/RootColumn/Candidates are the MACHINE-READABLE channel that callers
        // match on (the Delta dependent-column reclassifier resolves a dropped column by RootColumn). Rewriting
        // them would silently break that matching and lose information the message elided, so the hygiene fix
        // must apply to the message ONLY.
        string hostileName = "A" + CrLf + "FORGED";

        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => ConstraintExpressionFrontend.ParseAndResolve("`" + hostileName + "` > 0", AmountSchema));

        Assert.Equal(hostileName, ex.Reference);
        Assert.Equal(hostileName, ex.RootColumn);
        Assert.DoesNotContain('\uFFFD', ex.Reference!);
        AssertHygienic("structured-properties", ex.Message);
    }

    [Fact]
    public void AutoNamingRenderer_StaysRaw_SoOutputColumnNamesAreNotRewritten()
    {
        // The load-bearing separation. CoercionHelpers.PrettyReference is shared by diagnostics AND by Spark's
        // auto-naming of a function in output position, where the result becomes a real output-schema COLUMN
        // NAME. Sanitizing inside the shared renderer would have changed query RESULTS, not just prose — so the
        // hygiene lives in a diagnostic-only entry point and the raw renderer is left alone.
        var literal = Literal.OfString("A" + CrLf + "FORGED");

        string raw = CoercionHelpers.PrettyReference(literal);
        string diagnostic = CoercionHelpers.DiagnosticReference(literal);

        Assert.Contains(CrLf, raw, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', raw);
        Assert.DoesNotContain(CrLf, diagnostic, StringComparison.Ordinal);
        Assert.Contains('\uFFFD', diagnostic);
    }

    [Fact]
    public void AutoNamingRenderer_StaysUnbounded_SoALongNameSurvivesWhole()
    {
        // Same separation on the LENGTH axis: an over-cap auto-name must not be elided into a `…` column name.
        var literal = Literal.OfString(new string('z', FloodLength));

        string raw = CoercionHelpers.PrettyReference(literal);

        Assert.DoesNotContain('…', raw);
        Assert.True(raw.Length > CoercionHelpers.DiagnosticReferenceMaxLength);
    }

    [Theory]
    [InlineData(0, false)] // exactly at the cap: shown whole, no elision
    [InlineData(1, true)]  // one char over: elided
    public void DiagnosticReference_ElidesExactlyAtItsCap(int overshoot, bool expectElision)
    {
        // The rendered form is `"<payload>"` — two quote chars around the payload — so size the payload to land
        // the whole render exactly on the cap.
        int payload = CoercionHelpers.DiagnosticReferenceMaxLength - 2 + overshoot;
        var literal = Literal.OfString(new string('z', payload));

        string rendered = CoercionHelpers.DiagnosticReference(literal);

        Assert.Equal(expectElision, rendered.EndsWith('…'));
        Assert.True(rendered.Length <= CoercionHelpers.DiagnosticReferenceMaxLength + 1);
    }

    [Fact]
    public void LegitimateDiagnostic_IsUnchanged_SoInteractiveUxIsNotDegraded()
    {
        // Control: the hygiene layers must be INERT for every realistic diagnostic. A normal type error still
        // renders its reference whole, with no replacement char and no elision — pinned as an exact string so
        // a future tightening of either cap cannot quietly degrade ordinary analyzer UX.
        AnalysisException ex = Assert.Throws<AnalysisException>(
            () => ConstraintExpressionFrontend.ParseAndResolve("amount > 'abc'", AmountSchema));

        Assert.Equal(
            "cannot resolve '(amount > \"abc\")' due to data type mismatch: the 'GreaterThan' operator "
            + "requires comparable operand types but got 'int' and 'string'.",
            ex.Message);
    }

    [Fact]
    public void WellFormedPredicate_StillResolves()
    {
        // Control: nothing about the hygiene change may disturb the success path.
        Expression resolved = ConstraintExpressionFrontend.ParseAndResolve(
            "amount > 0 AND label > 'a'", AmountSchema);

        Assert.Equal(BooleanType.Instance, resolved.Type);
    }

    /// <summary>The #687 contract: no control character (nor a Unicode line/paragraph separator, which several
    /// log viewers render as a newline) may survive into a diagnostic, and the render must be bounded.</summary>
    private static void AssertHygienic(string site, string message)
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
                FormattableString.Invariant($"[{site}] injection-unsafe U+{rune.Value:X4}"));
        }

        Assert.False(ContainsLoneSurrogate(message), site + ": message carries a lone surrogate");

        Assert.True(
            message.Length <= AnalysisException.MaxMessageLength + 1,
            FormattableString.Invariant($"[{site}] message length {message.Length} exceeds the backstop"));
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
