using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using SharedDiagnosticText = DeltaSharp.Diagnostics.DiagnosticText;

namespace DeltaSharp.Core.Tests.Diagnostics;

/// <summary>
/// #687 council round 3 (Security A) — a <b>direct</b> guard on the shared
/// <c>DeltaSharp.Abstractions</c> primitive, in an assembly that does not reach it through
/// <c>DeltaSharp.Storage</c>.
/// <para>The primitive's lone-surrogate and astral rules were only covered from
/// <c>DeltaSharp.Storage.Tests</c>, through Storage's forwarder. Security proved the gap: mutating the fast
/// path to drop <i>only</i> the surrogate bail-out failed in <b>Storage alone</b> — the Core suites, which
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
    /// a wide listing of short names discarded some of the user's own column names with budget to spare, on
    /// the trusted path — worse than the unbounded original this bound replaced.</para>
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


    /// <summary>
    /// #687 council round 16 (Security BLOCKING) — the documented exemption is the ONLY way the result may
    /// exceed its budget, and it may exceed it only by the marker's own width.
    /// </summary>
    /// <remarks>
    /// <para>The summary said "at most <c>budget</c> characters" and the remarks said room for the suffix
    /// "is reserved before each item is committed". Neither survived round 14, which deleted the per-item
    /// reserve in favour of a feasibility scan; and the terminal path returns the bare marker whatever the
    /// budget, so a zero budget with 100,000 items yields 16 characters. One caller today pairs it with a
    /// reserve and is safe, but the contract is the API — this primitive lives in Abstractions expressly so
    /// that Core, Storage and Engine can share it, and the next caller reads the summary, not the loop.</para>
    /// <para>Written as a test rather than left as the corrected sentence, because a sentence cannot fail.
    /// Figures in this change have been wrong in prose ten times and in an assertion never once. If the
    /// exemption ever widens beyond the marker, this fails instead of the documentation quietly going
    /// stale.</para>
    /// <para><b>Pinned in both directions, which the first version was not.</b> As seven parametrized rows
    /// each returning early when the result fitted, it was green under the mutation that deletes the
    /// exemption altogether — a terminal path returning the empty string satisfies "never exceeds its
    /// budget" perfectly, and destroys the count the exemption exists to preserve. Widening was pinned;
    /// narrowing to nothing was not, and a reviewer mutating only outward would have called it covered.
    /// That is the general shape: an exemption is a permission, so a test that only checks the permission
    /// is not abused says nothing about it being exercised. It is now one fact with an interior sweep that
    /// counts how many cells actually reached the exemption and fails if none did — the same
    /// non-vacuity remedy the two walk pins in this file and its Core sibling already carry.</para>
    /// </remarks>
    [Fact]
    public void TheMarkerExemption_IsTheOnlyWayTheResultMayExceedItsBudget()
    {
        (int Budget, int Count)[] cases =
        [
            (0, 1), (0, 100000), (1, 7), (5, 3), (11, 40), (24, 400), (60, 12),
            (2, 2), (8, 9), (13, 5), (30, 1000), (47, 60),
        ];

        int exercised = 0;
        var violations = new List<string>();
        foreach ((int budget, int count) in cases)
        {
            string[] items = [.. Enumerable.Range(0, count).Select(i =>
                string.Create(CultureInfo.InvariantCulture, $"column_name_{i:D5}"))];

            string rendered = SharedDiagnosticText.SanitizeToBudget(
                items,
                static (item, allowance) => SharedDiagnosticText.Sanitize(item, allowance),
                budget,
                8,
                64);

            if (rendered.Length <= budget)
            {
                continue;
            }

            exercised++;

            // Over budget is permitted in exactly one shape: the bare marker, nothing else, and no wider
            // than the marker for the number it actually reports. Every failure carries its cell and they
            // are collected rather than thrown at the first: folding seven parametrized rows into one sweep
            // gained cells and would otherwise have lost the row name that used to identify them, which is
            // a worse debugging surface than the rows it replaced.
            Match marker = Regex.Match(rendered, @"^\u2026 \(\+(\d+) more\)$");
            if (!marker.Success)
            {
                violations.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"budget={budget} count={count}: exceeded its budget with something other than the "
                        + $"bare marker — '{rendered}'"));
                continue;
            }

            int reported = int.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture);
            if (reported != count)
            {
                violations.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"budget={budget} count={count}: the marker reported {reported} hidden items — "
                        + $"'{rendered}'"));
            }

            int markerWidth = SharedDiagnosticText.OverflowMarkerLength(count);
            if (rendered.Length != markerWidth)
            {
                violations.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"budget={budget} count={count}: rendered {rendered.Length} characters where the bare "
                        + $"marker is {markerWidth} — '{rendered}'"));
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{violations.Count} of {cases.Length} cells exceeded their budget outside the one "
                    + $"permitted shape:\n")
            + string.Join("\n", violations));

        Assert.True(
            exercised > 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"none of {cases.Length} cells reached the exemption, so every assertion above was skipped "
                    + $"and this test would stay green with the exemption deleted — raise the counts or "
                    + $"lower the budgets until at least one cell cannot fit a single item"));
    }

    /// <summary>The exact width of the <c>… (+N more)</c> marker, spelled out rather than taken from
    /// <c>OverflowMarkerLength</c>, so the oracle below shares no arithmetic with the code it judges.
    /// </summary>
    private static int MarkerWidth(int hidden) =>
        string.Create(CultureInfo.InvariantCulture, $"\u2026 (+{hidden} more)").Length;

    /// <summary>
    /// #687 council round 17 (Architect BLOCKING, sibling half) — the listing shows the <b>largest</b>
    /// number of items that fits, not the one below the first that does not.
    /// <para>This primitive's downward scan was already pinned when the identical scan in
    /// <c>CoercionHelpers</c> was not, and that asymmetry is the finding: a property held at one of a pair
    /// of sites and not at the other, with the untravelled half being the <em>test</em>. Both halves now
    /// state the same property in the same shape, and both end by asserting that their corpus still
    /// reaches the region where the property has any content.</para>
    /// <para>That region exists only where the feasible counts are <b>not a prefix</b>. Taking one more
    /// item costs its own width plus a separator, but the step that takes the LAST one also deletes the
    /// marker, refunding that marker and its separator. Where an item is cheaper than the refund, a count
    /// can be infeasible while a larger one fits and a stop-at-first-failure walk halts below it; at or
    /// above the refund no such count exists and the two walk shapes are the same function — which is
    /// exactly how the sibling site stayed unguarded.</para>
    /// <para>The refund is computed below as <c>refund</c> and asserted in both directions, rather than
    /// written here as a number. The same sentence in the Core sibling did name one, it was too large by
    /// one, and too large is the direction that sends a maintainer choosing a corpus by it to a corpus
    /// that cannot see the defect. A threshold a reader is expected to ACT on belongs in an assertion; a
    /// figure that only describes belongs in prose. This one is acted on.</para>
    /// </summary>
    [Fact]
    public void TheListingCountIsTheLargestFeasibleCount_NotOneShortOfTheFirstInfeasibleOne()
    {
        var counterexamples = new List<string>();
        var misruled = new List<string>();
        int cells = 0;
        int discriminating = 0;
        int dearestDiscriminating = 0;

        // What reaching the final item refunds: the separator the marker would have needed, plus the marker
        // for a single hidden item. Taking one more item costs a separator and the item, so an item cheaper
        // than this refund creates a k that fails while a larger k succeeds. Computed here and asserted
        // below rather than written into SanitizeToBudget's comment as a literal, where the same sentence
        // in the Core sibling was wrong by one in the direction that would send a maintainer to a
        // NON-discriminating corpus. It was wrong there because it was RIGHT here and then copied: this
        // renderer separates with ", " and the struct with a single ',', so a separator-derived figure
        // shared across the pair is correct at its origin and off by exactly the difference at its
        // destination. Both sites compute it now, which is the only form of that sentence safe to copy.
        int refund = ", ".Length + MarkerWidth(1);

        // Chosen, not enumerated, and self-checking: trim itemLength to {12, 18} and this test goes RED,
        // because every remaining item is at or above the refund and the corpus stops reaching the region
        // it exists to pin. That is the criterion for a literal corpus here — trimming it must break the
        // test, or a chokepoint must make the property hold whatever the list contains. A corpus that
        // satisfies neither is a claim with a green checkmark, which is what the sibling suite deleted.
        foreach (int itemLength in new[] { 1, 2, 3, 4, 6, 8, 10, 11, 12, 18 })
        {
            foreach (int count in new[] { 2, 3, 4, 5, 9, 11, 30, 107 })
            {
                string[] items =
                [
                    .. Enumerable.Range(0, count).Select(i =>
                        string.Create(CultureInfo.InvariantCulture, $"{i:D3}")
                            .PadRight(itemLength, 'x')[..itemLength]),
                ];

                // Pinning the allowance to the item length keeps every item verbatim, so the oracle needs
                // no model of per-item truncation — a different property, guarded elsewhere.
                for (int budget = 1; budget <= 420; budget++)
                {
                    cells++;
                    string rendered =
                        SharedDiagnosticText.SanitizeToBudget(items, budget, itemLength, itemLength);

                    int Cost(int k) => (k * itemLength) + ((k - 1) * ", ".Length)
                        + (k == count ? 0 : ", ".Length + MarkerWidth(count - k));

                    int largestFeasible = 0;
                    for (int k = 1; k <= count; k++)
                    {
                        if (Cost(k) <= budget)
                        {
                            largestFeasible = k;
                        }
                    }

                    for (int k = 1; k < largestFeasible; k++)
                    {
                        if (Cost(k) > budget)
                        {
                            discriminating++;
                            dearestDiscriminating =
                                Math.Max(dearestDiscriminating, ", ".Length + itemLength);
                            if (", ".Length + itemLength >= refund)
                            {
                                misruled.Add(string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"itemLength={itemLength} count={count} budget={budget}: "
                                        + $"discriminating although one more item costs "
                                        + $"{", ".Length + itemLength}, which the refund {refund} does not "
                                        + $"exceed"));
                            }

                            break;
                        }
                    }

                    // Counted from the text, not from the marker: everything that is not the marker is an
                    // item. Reading the count off the marker would be reading the renderer's own answer.
                    int shown = rendered.Length == 0
                        ? 0
                        : rendered.Split(", ", StringSplitOptions.None)
                            .Count(segment => !segment.StartsWith('\u2026'));

                    if (shown != largestFeasible)
                    {
                        counterexamples.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"itemLength={itemLength} count={count} budget={budget}: showed {shown} of a "
                                + $"feasible {largestFeasible} in {rendered.Length} chars — {rendered}"));
                    }
                }
            }
        }

        Assert.True(
            counterexamples.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{counterexamples.Count} of {cells} cells listed fewer items than fit; first 5:\n")
            + string.Join("\n", counterexamples.Take(5)));

        Assert.True(
            misruled.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{misruled.Count} of {cells} cells discriminate outside the rule for choosing a corpus, "
                    + $"so the rule is wrong and following it would produce a corpus that cannot see the "
                    + $"defect; first 5:\n")
            + string.Join("\n", misruled.Take(5)));

        // Tightness. The check above admits a refund stated too large, which is exactly the error that
        // shipped in the Core sibling's prose; equality pins it from both sides.
        Assert.Equal(refund - 1, dearestDiscriminating);

        Assert.True(
            discriminating > 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"none of {cells} cells has a feasible count above an infeasible one, so this corpus can "
                    + $"no longer distinguish a downward scan from a forward walk — shorten the items "
                    + $"until one costs less than {refund} with its separator, or lower the budgets until "
                    + $"it can"));
    }
}
