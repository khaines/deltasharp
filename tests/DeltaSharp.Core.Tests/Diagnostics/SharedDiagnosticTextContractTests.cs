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
    /// This paragraph used to say that no figure in this change had ever been wrong inside an assertion,
    /// which was false when it was written and false by the author's own retraction two rounds earlier: a
    /// margin computed and printed by an assertion was wrong because the assertion measured a hand-picked
    /// corpus. An assertion is only as honest as the population it measures, and a tally of how often the
    /// change has erred is itself the kind of claim it keeps getting wrong, so neither is restated. What
    /// survives is the reason for the form: if the exemption ever widens beyond the marker, this fails,
    /// instead of documentation quietly going stale.</para>
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

    [Fact]
    public void SanitizeAndJoin_LazyPath_AllocationIsBoundedByMaxItems_NotBySequenceLength()
    {
        // #767 ALLOCATION ORACLE. The structural sibling cannot kill the regression it names:
        // streaming and materializing are output-EQUIVALENT, so no assertion over the result string
        // separates them. GC.GetAllocatedBytesForCurrentThread is exact byte accounting, not timing,
        // so this is deterministic. The fixed pool makes the generator itself allocation-free; the only
        // allocation attributable to the call is the primitive's own.
        // Streaming measures ~1.2 KB; materializing 1e6 refs measures ~16.8 MB. 64 KB threshold
        // is ~4 orders of magnitude clear of both — regression detector, not perf assertion.
        const int N = 1_000_000;
        string[] pool = Enumerable.Range(0, 64).Select(i => "tok-" + i).ToArray();
        IEnumerable<string> Hostile()
        {
            for (int i = 0; i < N; i++) { yield return pool[i & 63]; }
        }

        _ = SharedDiagnosticText.SanitizeAndJoin(Hostile(), maxItemLength: 32, maxItems: 16); // warm JIT

        long before = GC.GetAllocatedBytesForCurrentThread();
        string result = SharedDiagnosticText.SanitizeAndJoin(Hostile(), maxItemLength: 32, maxItems: 16);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("(+999984 more)", result, StringComparison.Ordinal);

        Assert.True(
            allocated < 64 * 1024,
            $"SanitizeAndJoin allocated {allocated:N0} bytes for {N:N0}-token lazy sequence at maxItems=16; "
            + "expected < 65536. Allocation must be bounded by maxItems, not by sequence length (#767): "
            + "the lazy path has been reverted to materializing the hostile tail.");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(15, 16)]
    [InlineData(16, 16)]
    [InlineData(17, 16)]
    [InlineData(40, 16)]
    [InlineData(5, 2)]
    public void SanitizeAndJoin_FastAndLazyPaths_ProduceIdenticalOutput(int tokenCount, int maxItems)
    {
        string[] tokens = Enumerable.Range(0, tokenCount).Select(i => $"tok-{i}").ToArray();
        // Force lazy path with a non-IReadOnlyList wrapper
        IEnumerable<string> lazy = tokens.Select(x => x);

        string fast = SharedDiagnosticText.SanitizeAndJoin(tokens, maxItemLength: 32, maxItems: maxItems);
        string slowPath = SharedDiagnosticText.SanitizeAndJoin(lazy, maxItemLength: 32, maxItems: maxItems);

        Assert.Equal(fast, slowPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SanitizeAndJoin_NegativeMaxItems_ThrowsIdenticallyOnBothPaths(bool forceLazy)
    {
        // Guard is before the branch split, so both paths must throw identically.
        string[] tokens = ["a"];
        IEnumerable<string> input = forceLazy ? tokens.Select(x => x) : tokens;
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SharedDiagnosticText.SanitizeAndJoin(input, maxItemLength: 32, maxItems: -1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SanitizeAndJoin_NegativeMaxItemLength_ThrowsIdenticallyOnBothPaths(bool forceLazy)
    {
        // maxItemLength guard is also before the branch split.
        string[] tokens = ["a"];
        IEnumerable<string> input = forceLazy ? tokens.Select(x => x) : tokens;
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SharedDiagnosticText.SanitizeAndJoin(input, maxItemLength: -1, maxItems: 16));
    }

    // ---- #751: unambiguous separator escaping ----------------------------------------------------------

    [Fact]
    public void SanitizeAndJoin_ItemContainingTheSeparator_IsQuotedSoItCannotBeMistakenForThreeItems()
    {
        // The defect this issue closes: a single item named "a, b, c" was byte-identical to three items.
        // With CSV-style quoting the one item is wrapped, so a reader can tell them apart.
        string threeItems = SharedDiagnosticText.SanitizeAndJoin(
            ["a", "b", "c"], maxItemLength: 32, maxItems: 16);
        string oneItem = SharedDiagnosticText.SanitizeAndJoin(
            ["a, b, c"], maxItemLength: 32, maxItems: 16);

        Assert.Equal("a, b, c", threeItems);      // clean items are untouched (no golden churn)
        Assert.Equal("\"a, b, c\"", oneItem);      // the embedded-separator item is quoted
        Assert.NotEqual(threeItems, oneItem);      // the ambiguity is gone
    }

    [Fact]
    public void SanitizeAndJoin_ItemContainingAQuote_HasItsQuotesDoubledAndIsWrapped()
    {
        // CSV / RFC 4180 escaping: an embedded quote is doubled and the item is wrapped, so the closing
        // quote of the token is always the next UN-doubled quote.
        string rendered = SharedDiagnosticText.SanitizeAndJoin(
            ["say \"hi\""], maxItemLength: 32, maxItems: 16);

        Assert.Equal("\"say \"\"hi\"\"\"", rendered);
    }

    [Fact]
    public void SanitizeAndJoin_CleanItems_AreReturnedVerbatim_NoQuoting()
    {
        // A raw (unquoted) item never contains the separator or a quote — that is the whole invariant that
        // makes the grammar unambiguous. Verify ordinary identifiers pass through untouched.
        string rendered = SharedDiagnosticText.SanitizeAndJoin(
            ["col_a", "col.b", "feature-x"], maxItemLength: 32, maxItems: 16);

        Assert.Equal("col_a, col.b, feature-x", rendered);
        Assert.DoesNotContain('"', rendered);
    }

    [Fact]
    public void SanitizeAndJoin_ControlCharsInItem_AreNeutralizedBeforeQuoting()
    {
        // Sanitize runs first, so CR/LF/NEL/U+2028/U+2029 become U+FFFD; quoting only decides how the
        // already-neutralized content is delimited. An item bearing a separator AND a control char is both
        // neutralized and quoted.
        string rendered = SharedDiagnosticText.SanitizeAndJoin(
            ["a, \r\nb"], maxItemLength: 32, maxItems: 16);

        Assert.DoesNotContain('\r', rendered);
        Assert.DoesNotContain('\n', rendered);
        Assert.Equal("\"a, \uFFFD\uFFFDb\"", rendered);
    }

    [Fact]
    public void SanitizeAndJoin_QuotingIsAppliedAfterTheItemBound_ContentStaysBounded()
    {
        // Per-item bound is preserved: Sanitize caps the CONTENT at maxItemLength; quoting adds only the
        // bounded escape overhead (<= 2*content + 2). The separator survives the cap here, so the bounded
        // content is quoted — never the full 50-plus-char item.
        string longItem = "a, " + new string('x', 50);
        string rendered = SharedDiagnosticText.SanitizeAndJoin(
            [longItem], maxItemLength: 10, maxItems: 16);

        // 10 chars of content ("a, xxxxxxx"), then the truncation ellipsis, all wrapped.
        Assert.Equal("\"a, xxxxxxx…\"", rendered);
    }

    [Fact]
    public void SanitizeAndJoin_OverflowMarker_IsNeverQuoted_AndStaysDistinctFromQuotedItems()
    {
        // The elision marker is a structural suffix, not an item, so it renders bare. Even when the shown
        // items are quoted (they contain separators), the "… (+N more)" tail is verbatim and countable.
        string[] items = Enumerable.Range(0, 20)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"v{i}, x"))
            .ToArray();

        string rendered = SharedDiagnosticText.SanitizeAndJoin(items, maxItemLength: 32, maxItems: 16);

        Assert.Contains("\"v0, x\"", rendered);              // shown items are quoted
        Assert.EndsWith("… (+4 more)", rendered);            // marker bare and un-quoted
        Assert.DoesNotContain("\"… (+4 more)\"", rendered);
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(5, 2)]
    [InlineData(40, 16)]
    public void SanitizeAndJoin_QuotedItems_RenderIdenticallyOnFastAndLazyPaths(int tokenCount, int maxItems)
    {
        // The escaping must be path-independent — the #767 fast/lazy equivalence extends to quoted output.
        string[] tokens = Enumerable.Range(0, tokenCount)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"n{i}, \"q\""))
            .ToArray();
        IEnumerable<string> lazy = tokens.Select(x => x);

        string fast = SharedDiagnosticText.SanitizeAndJoin(tokens, maxItemLength: 32, maxItems: maxItems);
        string slow = SharedDiagnosticText.SanitizeAndJoin(lazy, maxItemLength: 32, maxItems: maxItems);

        Assert.Equal(fast, slow);
    }

    // ---- #751 Round-1: forged elision marker, injectivity, and the escape-overhead ceiling ----

    [Fact]
    public void SanitizeAndJoin_ForgedElisionMarkerItem_IsQuoted_SoItCannotMasqueradeAsTheStructuralMarker()
    {
        // The elision marker "… (+N more)" is appended bare. An item literally named "… (+7 more)" would,
        // without the '…'-lead force-quote, render byte-identically to the structural marker. It must render
        // quoted and therefore distinguishable.
        string rendered = SharedDiagnosticText.SanitizeAndJoin(
            ["real", "… (+7 more)"], maxItemLength: 64, maxItems: 16);

        Assert.Equal("real, \"… (+7 more)\"", rendered);
        Assert.Contains("\"… (+7 more)\"", rendered);   // the forged item is quoted
    }

    [Fact]
    public void SanitizeAndJoin_ForgedMarker_WithARealElision_StaysDistinctFromTheStructuralMarker()
    {
        // A forged "… (+N more)" item shown alongside a genuine elision: the forged one is quoted, the
        // structural tail is bare — so a reader can still tell the real count from the impostor.
        string[] items =
        [
            "… (+99 more)",
            .. Enumerable.Range(0, 20).Select(i => string.Create(CultureInfo.InvariantCulture, $"c{i}")),
        ];

        string rendered = SharedDiagnosticText.SanitizeAndJoin(items, maxItemLength: 64, maxItems: 4);

        Assert.StartsWith("\"… (+99 more)\"", rendered);   // impostor item: quoted
        Assert.EndsWith("… (+17 more)", rendered);         // structural marker: bare
        Assert.DoesNotContain("\"… (+17 more)\"", rendered);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(64)]
    public void SanitizeAndJoin_EscapeOverhead_NeverExceeds_TwoTimesContentPlusTwo_PerItem(int length)
    {
        // The rendered width bound the XML remark states: a single item's render grows by at most the
        // RFC-4180 escape overhead (<= 2*L + 2). The worst case is a non-empty all-quotes item: every one of
        // L characters doubles, plus the two wrapping quotes, so the render is EXACTLY 2*L + 2. An EMPTY item
        // needs no quoting (no separator/quote/'…'), so it stays 0 — still within the ceiling.
        string allQuotes = new('"', length);

        string rendered = SharedDiagnosticText.SanitizeAndJoin(
            [allQuotes], maxItemLength: 4096, maxItems: 16);

        Assert.True(
            rendered.Length <= (2 * length) + 2,
            $"render width {rendered.Length} exceeded the 2*L+2 ceiling for L={length}.");
        if (length > 0)
        {
            Assert.Equal((2 * length) + 2, rendered.Length);   // non-empty all-quotes hits the ceiling exactly
        }
    }

    [Theory]
    [InlineData("a, b")]           // separator-bearing name
    [InlineData("say \"hi\"")]     // quote-bearing name
    [InlineData("… (+7 more)")]    // forged elision-marker lead
    public void SanitizeAndJoinCounted_QuotesHostileItem_AndAppendsStructuralElision(string hostile)
    {
        // #751 C1(a): SanitizeAndJoinCounted must (a) RFC-4180 QUOTE a hostile item (separator-bearing,
        // quote-bearing, or '…'-lead) so it cannot masquerade as two columns / as the structural marker, AND
        // (b) append the STRUCTURAL "… (+N more)" tail BARE when total > shown.Count. total 9 - shown 2 = 7.
        // Dropping the EscapeItem quoting (SharedDiagnosticText / DiagnosticText.cs:~293) turns this red.
        var shown = new[] { hostile, "clean" };

        string rendered = SharedDiagnosticText.SanitizeAndJoinCounted(shown, total: 9, maxItemLength: 64);

        string quoted = "\"" + hostile.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        Assert.StartsWith(quoted + ", clean", rendered);   // hostile item quoted, clean item verbatim
        Assert.EndsWith(", … (+7 more)", rendered);          // structural elision appended BARE
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(20)]
    [InlineData(42)]
    [InlineData(99)]
    public void SanitizeAndJoin_IsInjective_ParseReversesJoin_OverAHostileAlphabet(int seed)
    {
        // Round-trip / injectivity property (#751): for any list whose length does not force an elision,
        // parse(join(items)) == items.Select(Sanitize). The corpus is drawn from the adversarial alphabet
        // that motivated quoting — the separator, a quote, the elision-marker shape, control characters, and
        // boundary lengths — so a regression in the escaping turns this red.
        const int maxItemLength = 24;
        string[] alphabet =
        [
            "clean", "col.b", "feature-x", "", "a", ",", ", ", "\"", "a, b", "say \"hi\"",
            "… (+7 more)", "…leading", "a\r\nb", "x\u2028y", new string('z', 40), new string('"', 12),
            "trailing…", "a,b,c",
        ];

        var rng = new Random(seed);
        int count = rng.Next(1, 12);
        string[] raw = Enumerable.Range(0, count).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray();

        // maxItems >= count so no structural elision marker is appended and the render is fully reversible.
        string joined = SharedDiagnosticText.SanitizeAndJoin(raw, maxItemLength, maxItems: count, separator: ", ");

        string[] expected = raw.Select(r => SharedDiagnosticText.Sanitize(r, maxItemLength)).ToArray();
        string[] parsed = ParseRfc4180(joined, ", ");

        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(20)]
    [InlineData(42)]
    [InlineData(99)]
    public void SanitizeAndJoin_ForcedElision_AppendsExactlyOneStructuralMarker_WithTheRealHiddenCount(int seed)
    {
        // #751 C2 second leg: when maxItems < count a STRUCTURAL "… (+N more)" marker IS appended (exactly
        // one, at the tail, BARE), and N is the REAL hidden count (count - maxItems). The '…'-lead force-quote
        // still holds: any shown item literally leading with '…' renders quoted, so the structural marker is
        // the ONLY bare "… (+N more)" — an oracle a regression in the force-quote or the elision count breaks.
        const int maxItemLength = 24;
        string[] alphabet =
        [
            "clean", "col.b", "feature-x", "a", "a, b", "say \"hi\"", "… (+7 more)", "…leading",
            "a\r\nb", new string('z', 40), "trailing…", "a,b,c",
        ];

        var rng = new Random(seed);
        int count = rng.Next(3, 16);
        int maxItems = rng.Next(1, count);   // strictly fewer shown than present -> forced elision
        string[] raw = Enumerable.Range(0, count).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray();

        string joined = SharedDiagnosticText.SanitizeAndJoin(raw, maxItemLength, maxItems, separator: ", ");

        string marker = string.Create(CultureInfo.InvariantCulture, $"… (+{count - maxItems} more)");
        Assert.EndsWith(", " + marker, joined);

        // Exactly ONE occurrence of the BARE structural marker: a forged '…'-lead item renders quoted
        // ("\"…"), so it never matches the unquoted ", …" boundary. Count bare-marker occurrences.
        int occurrences = CountOccurrences(joined, ", " + marker);
        Assert.Equal(1, occurrences);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal);
            i >= 0;
            i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    // A minimal RFC-4180 reader for the SanitizeAndJoin grammar: a field beginning with '"' is quoted and
    // runs to the next UN-doubled '"' (each "" unescaping to one "); any other field runs to the next
    // separator. Used only to PROVE injectivity of the join — the production code never parses these.
    private static string[] ParseRfc4180(string text, string separator)
    {
        var fields = new List<string>();
        int i = 0;
        while (true)
        {
            if (i < text.Length && text[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;   // closing quote
                        break;
                    }

                    sb.Append(text[i]);
                    i++;
                }

                fields.Add(sb.ToString());
            }
            else
            {
                int next = text.IndexOf(separator, i, StringComparison.Ordinal);
                if (next < 0)
                {
                    fields.Add(text[i..]);
                    return [.. fields];
                }

                fields.Add(text[i..next]);
                i = next;
            }

            if (i >= text.Length)
            {
                return [.. fields];
            }

            // Consume the separator between fields.
            if (string.CompareOrdinal(text, i, separator, 0, separator.Length) == 0)
            {
                i += separator.Length;
            }
        }
    }
}
