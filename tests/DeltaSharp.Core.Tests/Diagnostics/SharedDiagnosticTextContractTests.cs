using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Xunit;
using SharedDiagnosticText = DeltaSharp.Diagnostics.DiagnosticText;

namespace DeltaSharp.Core.Tests.Diagnostics;

/// <summary>
/// #687 council round 3 (Security A) — a <b>direct</b> guard on the shared
/// <c>DeltaSharp.Abstractions</c> primitive, in an assembly that does not reach it through
/// <c>DeltaSharp.Storage</c>.
/// <para>The primitive's lone-surrogate and astral rules were only covered from
/// <c>DeltaSharp.Storage.Tests</c>, through Storage's forwarder. Security proved the gap: mutating the fast
/// path to drop <i>only</i> the surrogate bail-out produced <b>1 RED, Storage only</b> — the Core suites, which
/// consume the same primitive from <c>SqlParser</c>, <c>SqlParseException</c> and <c>AnalysisException</c>,
/// stayed 61/61 green. A Storage refactor that stopped forwarding would have silently deleted the primitive's
/// only guard.</para>
/// <para>There is no <c>DeltaSharp.Abstractions.Tests</c> project and adding one is out of scope for a
/// diagnostics fix (and would collide with the other in-flight branches), so this lives in
/// <c>DeltaSharp.Core.Tests</c> — the primitive's <i>other</i> first-class consumer. That placement is not
/// merely a hedge: Core.Tests multi-targets <c>net8.0</c> and <c>net10.0</c>, so these assertions also give the
/// primitive cross-TFM coverage that the <c>net10.0</c>-only Storage suite cannot.</para>
/// </summary>
public sealed class SharedDiagnosticTextContractTests
{
    [Fact]
    public void LoneSurrogates_AreNeutralized_ThroughTheAbstractionsPrimitiveDirectly()
    {
        // Malformed UTF-16. A lone surrogate is not a code point, cannot be classified, and must never reach a
        // log sink — it corrupts UTF-8 encoders and JSON serializers downstream.
        Assert.Equal("a\uFFFDb", SharedDiagnosticText.Sanitize("a\uD800b"));  // lone HIGH
        Assert.Equal("a\uFFFDb", SharedDiagnosticText.Sanitize("a\uDC00b"));  // lone LOW
        Assert.Equal("\uFFFD\uFFFD", SharedDiagnosticText.Sanitize("\uDC00\uD800")); // reversed pair
        Assert.Equal("\uFFFD", SharedDiagnosticText.Sanitize("\uD800", maxLength: -10)); // no length cap
    }

    [Fact]
    public void WellFormedAstralPairs_AreKept_ThroughTheAbstractionsPrimitiveDirectly()
    {
        // The negative half: the surrogate handling must not be a blanket ban.
        Assert.Equal("a\U0001F600b", SharedDiagnosticText.Sanitize("a\U0001F600b"));
        Assert.Equal("\U00020000col", SharedDiagnosticText.Sanitize("\U00020000col"));
    }

    [Fact]
    public void AstralFormatCharacters_AreNeutralized_ThroughTheAbstractionsPrimitiveDirectly()
    {
        // The classification is code-point-aware, so the TAG block is caught here exactly as U+202E is.
        Assert.Equal("col\uFFFDname", SharedDiagnosticText.Sanitize("col\U000E0001name"));
        Assert.Equal("col\uFFFDname", SharedDiagnosticText.Sanitize("col\U0001D173name"));
        Assert.Equal("col\uFFFDname", SharedDiagnosticText.Sanitize("col\u202Ename"));
    }

    [Fact]
    public void EveryOutputIsWellFormedUtf16_ForAnyInput_IncludingMalformedOnes()
    {
        // The property the whole primitive owes its callers, asserted as a property rather than by example:
        // whatever goes in, what comes out can be encoded. Deterministic seed — this is a regression guard,
        // not a fuzzer.
        var rng = new Random(20260727);

        for (int n = 0; n < 20_000; n++)
        {
            int length = rng.Next(0, 24);
            var builder = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                builder.Append((char)rng.Next(0, 0x11000)); // deliberately includes bare surrogates
            }

            string sanitized = SharedDiagnosticText.Sanitize(builder.ToString(), rng.Next(-1, 32));

            Assert.False(ContainsLoneSurrogate(sanitized), "sanitized output carries a lone surrogate");
            Assert.DoesNotContain(
                sanitized.EnumerateRunes(),
                r => Rune.GetUnicodeCategory(r)
                    is UnicodeCategory.Control or UnicodeCategory.Format
                        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
        }
    }

    [Fact]
    public void FastPath_SharesThePredicate_SoAHostileCharacterUnderTheCapStillAllocates()
    {
        // Security note E: the fast path is safe *because* it calls the same IsInjectionUnsafe predicate as the
        // slow path, not because it restates the rule. These assertions pin the observable consequence — a
        // short clean token is returned by reference, a short HOSTILE one is not — so an "optimization" that
        // inlines a specialized scan and gets it subtly wrong is caught here.
        const string clean = "ordinary_column_name";

        Assert.Same(clean, SharedDiagnosticText.Sanitize(clean));

        foreach (string hostile in new[] { "a\rb", "a\u202Eb", "a\u2028b", "a\U000E0001b", "a\uD800b" })
        {
            Assert.NotSame(hostile, SharedDiagnosticText.Sanitize(hostile));
            Assert.Contains('\uFFFD', SharedDiagnosticText.Sanitize(hostile));
        }
    }

    /// <summary>A LONE (unpaired) surrogate is malformed UTF-16; a WELL-FORMED pair is legitimate astral text
    /// that must survive. Checking for "no surrogates at all" would contradict the primitive's contract.</summary>
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
    /// <summary>
    /// #687 council round 11 — an <b>exactly fitting listing spends its whole budget</b>, with no item
    /// discarded and no marker emitted.
    /// <para>The greedy walk charges every item for an overflow suffix that will not exist if the listing
    /// turns out to fit, so it stopped short while the complete listing was inside the budget all along:
    /// 107 seven-character names elided two of them at 1015 characters when the full listing renders in
    /// 1020. Two of a user's own column names discarded with budget to spare, on the trusted path — worse
    /// than the unbounded original this bound replaced.</para>
    /// <para>The oracle is the exact boundary and needs no constant: a collection whose full render is
    /// <i>exactly</i> the budget must render in full with no marker; one character less must elide and
    /// report the count.</para>
    /// <para>Round 11 fixed this with a dedicated pre-check. Round 14 removed that branch: the pre-check
    /// only ever asked whether <em>everything</em> fits, so it could not stop the walk eliding more than it
    /// had to, and it is now the k == Count case of the max-k search. This test is deliberately phrased
    /// against the <em>behaviour</em> rather than the mechanism, so it survived that rewrite unchanged —
    /// which is the point. It also replaces a round-10 test that named the last-item <em>exemption</em>,
    /// a special case for the final item of the property now provided for all of them.</para>
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(40)]
    [InlineData(107)]
    public void AnExactlyFittingListing_SpendsItsWholeBudget(int count)
    {
        foreach (int itemLength in new[] { 1, 4, 7, 12, 18, 33, 64 })
        {
            string[] items = [.. Enumerable.Range(0, count).Select(i =>
                string.Create(CultureInfo.InvariantCulture, $"{i:D2}").PadRight(itemLength, 'x')[..itemLength])];
            int exact = (count * itemLength) + ((count - 1) * ", ".Length);

            string full = SharedDiagnosticText.SanitizeToBudget(items, exact, 1, itemLength);
            Assert.True(
                full.Length == exact,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"count={count} itemLength={itemLength} budget={exact} rendered {full.Length}: an "
                        + $"exactly-fitting listing elided and left its budget unspent — {full}"));
            Assert.DoesNotContain('\u2026', full);

            // One character short: the listing must elide AND say by how much. Without the count this would
            // be the silent-truncation defect the whole round exists to prevent.
            string tight = SharedDiagnosticText.SanitizeToBudget(items, exact - 1, 1, itemLength);
            Assert.Contains(" more)", tight, StringComparison.Ordinal);
            Assert.NotEqual(full, tight);
        }
    }

}
