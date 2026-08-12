using System;
using System.Linq;
using System.Text;
using DeltaSharp.Diagnostics;
using Xunit;

namespace DeltaSharp.Abstractions.Tests;

/// <summary>
/// #687 council round 3, item 1 — the <b>astral half</b> of the injection-unsafe rule. This is the PRIMARY
/// guard for the shared <see cref="DiagnosticText"/> hygiene primitive, and #706 relocated it into
/// <c>DeltaSharp.Abstractions.Tests</c> — the assembly that OWNS the primitive — so it runs on both
/// <c>net8.0</c> and <c>net10.0</c> (the primitive multi-targets) and calls the primitive DIRECTLY rather
/// than through the Storage forwarder.
/// <para>The <c>Cf</c> (format) category was added to the neutralization set in round 2, but
/// <c>IsInjectionUnsafe</c> takes a <c>char</c> and so only decides BMP code points. <c>Cc</c>, <c>Zl</c> and
/// <c>Zp</c> are entirely BMP, so for those that was the whole rule — but <c>Cf</c> is not. The surrogate-pair
/// branch of <c>Sanitize</c> used to append a well-formed pair verbatim on the argument that "neither half is
/// a control/separator", which the <c>Cf</c> addition silently invalidated. It now category-checks the decoded
/// <see cref="Rune"/> against the identical four categories.</para>
/// <para>The strongest instance is the <b>TAG block</b> (U+E0020–U+E007F), which encodes arbitrary ASCII
/// invisibly — the canonical invisible-text smuggling vector, and a stronger form of "make the log lie" than
/// the U+202E that was already neutralized. A hostile <c>_delta_log</c> JSON string can carry any UTF-16, so
/// this is reachable on the same path as the rest of #687.</para>
/// <para>These tests are the standing guard for that corner. They are the counterpart to
/// <c>DeltaSharp.Storage.Tests</c>'s <c>StorageMessageHygieneTests.Sanitize_NeutralizesLoneSurrogates_ButKeepsValidPairs</c>,
/// which covers lone surrogates and pins U+1F600 (category <c>So</c>) as a pair that must SURVIVE. Read the
/// two together: that test proves the branch is not over-broad, these prove it is not under-broad.</para>
/// </summary>
public sealed class DiagnosticTextAstralHygieneTests
{
    /// <summary>Astral <c>Cf</c> code points. Each must be neutralized to a single U+FFFD.</summary>
    public static TheoryData<string, int> AstralFormatCharacters() => new()
    {
        { "LANGUAGE TAG", 0xE0001 },
        { "TAG LATIN CAPITAL A", 0xE0041 },
        { "TAG SPACE (start of the smuggling alphabet)", 0xE0020 },
        { "CANCEL TAG (end of the smuggling alphabet)", 0xE007F },
        { "KAITHI NUMBER SIGN", 0x110BD },
        { "EGYPTIAN HIEROGLYPH VERTICAL JOINER", 0x13430 },
        { "SHORTHAND FORMAT LETTER OVERLAP", 0x1BCA0 },
        { "MUSICAL SYMBOL BEGIN BEAM", 0x1D173 },
        { "MUSICAL SYMBOL END PHRASE", 0x1D17A },
    };

    [Theory]
    [MemberData(nameof(AstralFormatCharacters))]
    public void AstralFormatCharacter_IsNeutralized_LikeItsBmpCounterparts(string name, int codePoint)
    {
        string payload = char.ConvertFromUtf32(codePoint);
        string raw = "col" + payload + "name";

        // Precondition, so a Unicode-data change that reclassifies the code point fails loudly here rather
        // than quietly making the assertion below vacuous.
        Assert.Equal(
            System.Globalization.UnicodeCategory.Format,
            Rune.GetUnicodeCategory(new Rune(codePoint)));

        string sanitized = DiagnosticText.Sanitize(raw);

        Assert.DoesNotContain(payload, sanitized, StringComparison.Ordinal);
        Assert.Equal("col\uFFFDname", sanitized);

        // The whole surrogate PAIR collapses to one replacement char, not two — the neutralization operates on
        // the code point, not on the UTF-16 code units.
        Assert.Equal(1, sanitized.Count(c => c == '\uFFFD'));

        _ = name;
    }

    [Fact]
    public void TagBlockSmuggledPayload_DoesNotSurviveSanitization()
    {
        // The concrete attack: "EVIL" encoded in U+E0020–U+E007F and appended to an innocuous-looking column
        // name. Pre-fix this rendered as `safe_column` in every log viewer while carrying a hidden payload that
        // a downstream consumer (or a human copy-pasting the identifier) would pick up.
        var builder = new StringBuilder("safe_column");
        foreach (char c in "EVIL")
        {
            builder.Append(char.ConvertFromUtf32(0xE0000 + c));
        }

        string smuggled = builder.ToString();

        // Non-vacuity: the payload really is present and really is invisible in the INPUT.
        Assert.Equal(4, smuggled.EnumerateRunes().Count(r => r.Value is >= 0xE0020 and <= 0xE007F));

        string sanitized = DiagnosticText.Sanitize(smuggled);

        Assert.DoesNotContain(sanitized.EnumerateRunes(), r => r.Value is >= 0xE0020 and <= 0xE007F);
        Assert.Equal("safe_column\uFFFD\uFFFD\uFFFD\uFFFD", sanitized);
    }

    [Fact]
    public void LegitimateAstralText_IsStillPreservedWhole_TheBranchIsNotOverBroad()
    {
        // The negative half, and the reason the fix is a CATEGORY check rather than a blanket ban on pairs:
        // ordinary astral text — emoji, CJK extension ideographs, mathematical alphanumerics — is not a
        // control, format or separator character and must survive an interactive diagnostic unmodified.
        foreach (string legitimate in new[]
        {
            "a\U0001F600b",       // EMOJI GRINNING FACE (So)
            "\U00020000col",       // CJK EXT-B ideograph (Lo)
            "x\U0001D400y",        // MATHEMATICAL BOLD CAPITAL A (Lu)
            "\U0001F1FA\U0001F1F8", // REGIONAL INDICATOR pair (So, So)
        })
        {
            Assert.Equal(legitimate, DiagnosticText.Sanitize(legitimate));
            Assert.DoesNotContain('\uFFFD', DiagnosticText.Sanitize(legitimate));
        }
    }

    [Fact]
    public void AstralFormatCharacter_IsNeutralized_EvenWithNoLengthCap()
    {
        // maxLength < 0 disables truncation entirely; the category check must be independent of the cap.
        string raw = "col" + char.ConvertFromUtf32(0xE0041) + "name";

        Assert.Equal("col\uFFFDname", DiagnosticText.Sanitize(raw, maxLength: -1));
    }

    [Fact]
    public void AstralFormatCharacter_StraddlingTheCap_IsStillNotSplitIntoALoneSurrogate()
    {
        // The interaction between the two surrogate rules: the cap back-off must still run, and whichever of
        // "dropped by the cap" or "neutralized by category" applies, the result must never contain a lone
        // surrogate.
        string raw = "abc" + char.ConvertFromUtf32(0xE0041) + "def";

        for (int cap = 1; cap <= raw.Length + 2; cap++)
        {
            string sanitized = DiagnosticText.Sanitize(raw, cap);

            Assert.DoesNotContain(sanitized, c => char.IsSurrogate(c));
            Assert.DoesNotContain(
                sanitized.EnumerateRunes(),
                r => Rune.GetUnicodeCategory(r) == System.Globalization.UnicodeCategory.Format);
        }
    }

    [Fact]
    public void FastPath_IsInclusiveAtTheCap_ExactlyAtBoundStillReturnsTheSameInstance()
    {
        // Council round 3, item 2 (Quality): `raw.Length <= maxLength` -> `<` produced ZERO failing tests, so
        // the inclusive boundary was unguarded. This is a PERFORMANCE boundary, not a correctness one — the
        // slow path returns a character-identical string for the exact-cap case — but an unguarded boundary
        // means a future edit silently sends the commonest in-budget input down the allocating path, which is
        // precisely what the fast path was added (#696 council item 9) to avoid.
        string atBound = new('c', DiagnosticText.DefaultMaxLength);
        string oneUnder = new('c', DiagnosticText.DefaultMaxLength - 1);

        Assert.Same(atBound, DiagnosticText.Sanitize(atBound));
        Assert.Same(oneUnder, DiagnosticText.Sanitize(oneUnder));

        // One over the bound necessarily allocates (it is elided), so the boundary is crisp on both sides.
        string oneOver = new('c', DiagnosticText.DefaultMaxLength + 1);
        string elided = DiagnosticText.Sanitize(oneOver);

        Assert.NotSame(oneOver, elided);
        Assert.EndsWith("\u2026", elided, StringComparison.Ordinal);
    }

    [Fact]
    public void FastPath_IsInclusiveAtAnArbitraryCap_NotJustTheDefault()
    {
        // The bound is a parameter, so pin the boundary generically too — otherwise the guard above could be
        // satisfied by a special case keyed on DefaultMaxLength.
        foreach (int cap in new[] { 1, 2, 7, 63, 512 })
        {
            string atBound = new('c', cap);

            Assert.Same(atBound, DiagnosticText.Sanitize(atBound, cap));
            Assert.NotSame(
                atBound,
                DiagnosticText.Sanitize(atBound, cap - 1));
        }
    }

    [Fact]
    public void FastPath_AndSlowPath_AgreeOnTheExactCapCase()
    {
        // The correctness statement behind the perf boundary: whichever path runs, the RESULT is identical.
        // Driven through a deliberately larger cap so the same input provably takes the slow path too.
        string atBound = new('c', DiagnosticText.DefaultMaxLength);
        string withPair = new string('c', DiagnosticText.DefaultMaxLength - 2) + "\U0001F600";

        Assert.Equal(atBound, DiagnosticText.Sanitize(atBound, DiagnosticText.DefaultMaxLength));
        Assert.Equal(
            withPair,
            DiagnosticText.Sanitize(withPair, DiagnosticText.DefaultMaxLength));
        Assert.NotSame(
            withPair,
            DiagnosticText.Sanitize(withPair, DiagnosticText.DefaultMaxLength));
    }
}
