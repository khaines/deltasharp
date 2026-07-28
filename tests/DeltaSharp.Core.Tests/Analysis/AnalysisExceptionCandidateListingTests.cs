using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DeltaSharp.Analysis;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Analysis;

/// <summary>
/// #687 council round 6 (Balanced) — the <b>candidate-listing</b> contract for the two analyzer diagnostics
/// that echo a list of columns.
/// <para><b>The regression this closes.</b> The whole-message backstop added earlier in this PR was a cap on
/// the composed string, so a wide schema had its <c>given input columns: [...]</c> listing cut mid-way with no
/// indication that anything had been dropped. At <c>0ba0f8b</c> a 50-column and a 400-column table rendered
/// the <b>byte-identical</b> message, so every candidate past the cut was silently gone — <i>how many</i> is
/// not written here, because it is a property of the corpus and a prose number cannot be trusted to track it.
/// <c>OverflowCount_NamesExactlyHowManyCandidatesAreHidden</c> asserts the count instead: a hand-written
/// figure in this file was wrong by one, in a comment whose own argument is that a wrong count is worse than
/// no count. These are TRUSTED-path names: a user's own schema, not attacker text. Being told
/// "your column does not resolve" and then shown a truncated candidate list with no hint that it is truncated
/// is the difference between spotting a typo and filing a support ticket.</para>
/// <para><b>Why per-item bounding rather than a bigger cap.</b> A cap that truncates the CONTAINER destroys
/// the signal that truncation happened; any value for it is still a silent cut one column later. Bounding each
/// item and appending an explicit <c>(+N more)</c> keeps the message bounded <i>and</i> honest, and it is the
/// posture this PR already states for the parser at <c>SqlParser.cs</c> — bound the TOKEN so the PROSE
/// survives. The primitive was already hoisted for exactly this; it simply had not been applied here.</para>
/// <para><b>The invariant that makes it hold by construction.</b> Every component of these two messages is
/// individually bounded, so the composed message cannot reach <c>MaxMessageLength</c> for ANY input — see
/// <see cref="Listing_IsUnreachableByTheWholeMessageBackstop_ForAnyInput"/>. Without that, a single
/// pathological column name would push the message past the cap and take the <c>(+N more)</c> count with it,
/// re-opening the identical defect through a different field.</para>
/// </summary>
public sealed class AnalysisExceptionCandidateListingTests
{
    /// <summary>
    /// A REALISTIC-LENGTH name (35 characters, the length of <c>customer_lifetime_value_rolling_90d</c>).
    /// </summary>
    /// <remarks>
    /// Round 6, and the reason this suite missed two defects: the original corpus used
    /// <c>customer_metric_000</c> — 19 characters, comfortably under every cap in play. <b>A corpus whose
    /// items are all shorter than the bound cannot test the bound.</b> Every case here is now at or past
    /// real-world column-name length, so the per-item cap is actually exercised.
    /// </remarks>
    private static string RealisticName(int i) =>
        string.Create(CultureInfo.InvariantCulture, $"customer_lifetime_value_rolling_{i:D4}");

    private static IReadOnlyList<AttributeReference> Columns(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new AttributeReference(
                RealisticName(i), IntegerType.Instance, true, new ExprId(i + 1)))
            .ToArray();

    /// <summary>
    /// The precise defect, pinned as a regression: at <c>0ba0f8b</c> these two messages were byte-identical
    /// because both had been cut at the cap. A test that only asserted "the message is bounded" would have
    /// passed there; this one cannot.
    /// </summary>
    [Fact]
    public void WideSchemas_OfDifferentWidths_DoNotRenderIdentically()
    {
        string fifty = AnalysisException.UnresolvedColumn("nosuch", Columns(50)).Message;
        string fourHundred = AnalysisException.UnresolvedColumn("nosuch", Columns(400)).Message;

        Assert.NotEqual(fifty, fourHundred);
        AssertCountIsAccurate(fifty, 50);
        AssertCountIsAccurate(fourHundred, 400);
    }

    /// <summary>The count must be ACCURATE, not merely present — an overflow marker that reports the wrong
    /// number is worse than none, because it is believed.</summary>
    [Theory]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(400)]
    [InlineData(20_000)]
    public void OverflowCount_NamesExactlyHowManyCandidatesAreHidden(int width)
    {
        AnalysisException ex = AnalysisException.UnresolvedColumn("nosuch", Columns(width));

        AssertCountIsAccurate(ex.Message, width);

        // ...and the structured channel still carries every one of them, unmodified — including the ones the
        // message elides, which is what keeps the raw channel worth having.
        Assert.Equal(width, ex.Candidates.Count);
        Assert.Equal(RealisticName(width - 1), ex.Candidates[width - 1]);
    }

    /// <summary>
    /// Round 7 (Quality): <b>nothing is elided while the budget still has room.</b> A fixed item cap made a
    /// listing discard names with more than half the message budget unused — a 30-column listing capped at 20
    /// rendered 491 characters against a 1024-character ceiling, which is <i>worse than the unbounded
    /// original</i> at every width that used to fit. The bound is now the space actually remaining, so this
    /// sweeps the boundary band <b>continuously</b>: the previous corpus stopped at 20 and resumed at 50,
    /// leaving 21–46 — precisely where the regression lived — untested at every width.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContinuousWidths))]
    public void ListingBudget_IsSpentBeforeAnythingIsElided(int width)
    {
        string message = AnalysisException.UnresolvedColumn("nosuch", Columns(width)).Message;
        string listing = message[(message.IndexOf('[', StringComparison.Ordinal) + 1)..^1];

        if (!message.Contains('\u2026'))
        {
            for (int i = 0; i < width; i++)
            {
                Assert.Contains(RealisticName(i), message, StringComparison.Ordinal);
            }

            return;
        }

        // Elided: prove it was necessary. One more item at this corpus's own name length would not have fit
        // under the whole-message cap, so no information was discarded while there was room to keep it.
        int itemCost = RealisticName(0).Length + ", ".Length;
        Assert.True(
            message.Length + itemCost > AnalysisException.MaxMessageLength,
            string.Create(
                CultureInfo.InvariantCulture,
                $"width {width} elided at {message.Length} chars with room for another {itemCost}-char " +
                $"name under the {AnalysisException.MaxMessageLength} cap; listing was {listing.Length}"));
    }

    /// <summary>Every width from 1 to 60 — the band a two-point corpus cannot see.</summary>
    public static TheoryData<int> ContinuousWidths()
    {
        var data = new TheoryData<int>();
        for (int width = 1; width <= 60; width++)
        {
            data.Add(width);
        }

        return data;
    }

    /// <summary>
    /// The load-bearing invariant. Every component is bounded individually, so no input — however hostile —
    /// can push these messages to the whole-message cap. If this ever fails, the backstop has started doing
    /// the cutting again and the <c>(+N more)</c> count is being silently destroyed along with it.
    /// </summary>
    [Fact]
    public void Listing_IsUnreachableByTheWholeMessageBackstop_ForAnyInput()
    {
        // Simultaneously pathological on all three axes: the reference name, every candidate name, and the
        // cardinality.
        var hostile = Enumerable.Range(0, 5_000)
            .Select(i => new AttributeReference(
                new string('w', 5_000), IntegerType.Instance, true, new ExprId(i + 1)))
            .ToArray();

        foreach (string message in new[]
        {
            AnalysisException.UnresolvedColumn(new string('q', 100_000), hostile).Message,
            AnalysisException.AmbiguousReference(new string('q', 100_000), hostile).Message,
        })
        {
            Assert.True(
                message.Length < AnalysisException.MaxMessageLength,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"rendered {message.Length} chars, at or past the backstop — the overflow count is being "
                        + $"destroyed by the whole-message cap again"));

            // The count survived precisely because the backstop never fired.
            AssertCountIsAccurate(message, 5_000, itemPattern: "w+");
        }
    }

    /// <summary>
    /// Round 6, Architect BLOCKING 1 — the bound must not eat the DISCRIMINATOR.
    /// <para><c>AttributeReference.SimpleString</c> is <c>"{Name}#{ExprId}"</c>, and <c>Sanitize</c>
    /// truncates from the TAIL. Capping the composite therefore deleted <c>#ExprId</c> — the only thing
    /// distinguishing two same-named candidates — and rendered two byte-identical entries in the one message
    /// whose entire purpose is to tell them apart. This is the same class as the defect the round-6 fix was
    /// for, one level down: there, bounding destroyed the FACT THAT elision happened; here it destroyed the
    /// PAYLOAD the message exists to carry.</para>
    /// </summary>
    [Fact]
    public void AmbiguousCandidates_KeepTheirExprIdDiscriminator_WhenTheNameIsElided()
    {
        // A name that is genuinely long enough to force elision of the composite, with identical spellings so
        // the ExprId is the ONLY thing that can distinguish them.
        string shared = new('n', 200);
        var matches = new[]
        {
            new AttributeReference(shared, IntegerType.Instance, true, new ExprId(11)),
            new AttributeReference(shared, IntegerType.Instance, true, new ExprId(97)),
        };

        string message = AnalysisException.AmbiguousReference(shared, matches).Message;

        Assert.Contains("#11", message, StringComparison.Ordinal);
        Assert.Contains("#97", message, StringComparison.Ordinal);

        // ...and the two rendered candidates must not be the same string.
        string listing = message[(message.IndexOf("could be: ", StringComparison.Ordinal) + 10)..];
        string[] rendered = listing.TrimEnd('.').Split(", ");
        Assert.Equal(2, rendered.Length);
        Assert.NotEqual(rendered[0], rendered[1]);
    }

    /// <summary>A realistic ambiguity renders both name and id in full — the common case pays nothing.</summary>
    [Fact]
    public void AmbiguousCandidates_OfRealisticLength_RenderNameAndIdInFull()
    {
        var matches = new[]
        {
            new AttributeReference("customer_lifetime_value_rolling_90d", IntegerType.Instance, true, new ExprId(11)),
            new AttributeReference("customer_lifetime_value_rolling_90d", IntegerType.Instance, true, new ExprId(97)),
        };

        string message = AnalysisException.AmbiguousReference("customer_lifetime_value_rolling_90d", matches).Message;

        Assert.Contains("customer_lifetime_value_rolling_90d#11", message, StringComparison.Ordinal);
        Assert.Contains("customer_lifetime_value_rolling_90d#97", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2026', message);
    }

    /// <summary>An ExprId at the extreme still survives: the name budget is what gives, never the id.</summary>
    [Fact]
    public void AmbiguousCandidates_KeepTheIdEvenAtTheExtremeExprId()
    {
        var matches = new[]
        {
            new AttributeReference(new string('n', 500), IntegerType.Instance, true, new ExprId(int.MaxValue)),
        };

        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"#{int.MaxValue}"),
            AnalysisException.AmbiguousReference("x", matches).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every factory that composes a caller-supplied LIST, exercised as a class rather than enumerated by
    /// hand.
    /// </summary>
    /// <remarks>
    /// This exists because the previous revision fixed two of four list factories and left a class comment
    /// asserting the rule held for all of them — while the suite actively PINNED the truncation of the other
    /// two as expected behaviour. A comment asserting a global property is worth nothing without a test that
    /// ranges over the whole class; this is that test, and adding a new list factory without a count will
    /// fail it.
    /// </remarks>
    /// <summary>
    /// Round 7 (Security, elevated by Balanced): distinct legitimate names must render <b>distinctly</b>.
    /// A flat per-item cap collapsed <c>customer_lifetime_value_usd_2023_q1</c> and <c>…_q2</c> — the two
    /// the user actually meant — into the identical string, in a 190-character message nowhere near the
    /// backstop, so nothing needed bounding at all. Verified RED at <c>2d686a7</c>: three legitimate names
    /// produced two identical renders at both sites.
    /// <para>Names deliberately share a 32-character prefix, so this corpus can only pass if the bound is
    /// wide enough to reach the part that differs. A corpus whose items are all shorter than the bound
    /// cannot test the bound — that is exactly how the previous guard here passed while the defect
    /// shipped.</para>
    /// </summary>
    [Fact]
    public void LegitimateNamesSharingALongPrefix_RenderDistinctly()
    {
        string[] names =
        [
            "customer_lifetime_value_usd_2023",
            "customer_lifetime_value_usd_2023_q1",
            "customer_lifetime_value_usd_2023_q2",
        ];
        IReadOnlyList<AttributeReference> input = names
            .Select((n, i) => new AttributeReference(n, IntegerType.Instance, true, new ExprId(i + 11)))
            .ToArray();

        foreach (string message in new[]
        {
            AnalysisException.UnresolvedColumn("clv", input).Message,
            AnalysisException.AmbiguousReference("clv", input).Message,
        })
        {
            Assert.DoesNotContain('\u2026', message);
            foreach (string name in names)
            {
                Assert.Contains(name, message, StringComparison.Ordinal);
            }

            // ...and the renders are pairwise distinct, which is the property the shared prefix attacks.
            string body = message[(message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]
                .Trim('[', ']', '.');
            string[] rendered = body.Split(", ");
            Assert.Equal(rendered.Length, rendered.Distinct(StringComparer.Ordinal).Count());
        }
    }

    public static TheoryData<string, Func<int, Exception>> ListComposingFactories() => new()
    {
        { "UnresolvedColumn", n => AnalysisException.UnresolvedColumn("nosuch", Columns(n)) },
        { "AmbiguousReference", n => AnalysisException.AmbiguousReference("amb", Columns(n)) },
        { "UnknownFunction", n => AnalysisException.UnknownFunction("f", Types(n)) },
        { "InvalidFunctionArgument", n => AnalysisException.InvalidFunctionArgument("f", Types(n), "an integer") },
        { "TableOrViewNotFound", n => AnalysisException.TableOrViewNotFound(LongNames(n)) },
        { "UnsupportedDataSink", n => AnalysisException.UnsupportedDataSink("x", "/t", LongNames(n)) },
        { "UnsupportedWriteFormat", n => AnalysisException.UnsupportedWriteFormat("x", "/t", LongNames(n), LongNames(n)) },
    };

    /// <summary>
    /// The count oracle, stated once and free of any tuning constant: the names actually rendered plus the
    /// reported overflow must equal the true width. Asserting a literal <c>(+N more)</c> instead couples the
    /// test to whatever item cap happened to be in force, so it has to be <i>re-tuned</i> rather than
    /// <i>re-verified</i> every time the bound changes — and it says nothing about whether the count is
    /// right, only that it is unchanged.
    /// </summary>
    private static void AssertCountIsAccurate(string message, int width, string itemPattern = @"customer_lifetime_value_rolling_\d{4}")
    {
        int shown = Regex.Matches(message, itemPattern).Count;
        int reported = OverflowCounts(message).Sum();
        Assert.Equal(width, shown + reported);
    }

    /// <summary>Extracts every <c>(+N more)</c> count from a message, in order.</summary>
    private static int[] OverflowCounts(string message)
    {
        var counts = new List<int>();
        int at = 0;
        while ((at = message.IndexOf("(+", at, StringComparison.Ordinal)) >= 0)
        {
            int end = message.IndexOf(" more)", at, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            counts.Add(int.Parse(message[(at + 2)..end], CultureInfo.InvariantCulture));
            at = end;
        }

        return counts.ToArray();
    }

    private static IReadOnlyList<DataType> Types(int count) =>
        Enumerable.Repeat<DataType>(IntegerType.Instance, count).ToArray();

    private static IReadOnlyList<string> LongNames(int count) =>
        Enumerable.Range(0, count).Select(RealisticName).ToArray();

    [Theory]
    [MemberData(nameof(ListComposingFactories))]
    public void EveryListComposingFactory_ReportsAnOverflowCount(string factory, Func<int, Exception> build)
    {
        ArgumentNullException.ThrowIfNull(build);

        const int Width = 400;
        int[] atWidth = OverflowCounts(build(Width).Message);
        int[] atDouble = OverflowCounts(build(Width * 2).Message);

        Assert.True(
            atWidth.Length > 0,
            string.Create(CultureInfo.InvariantCulture, $"[{factory}] elided its list without any (+N more)"));

        // Accuracy asserted WITHOUT naming a budget: each list shows a fixed number of items, so doubling the
        // input must move every reported count by exactly the width. This holds whatever per-list allowance a
        // factory uses — which matters, because the one factory composing TWO lists necessarily halves it.
        // A literal count here would have been coupled to that allowance and would have had to be re-tuned
        // rather than re-verified.
        Assert.Equal(atWidth.Length, atDouble.Length);
        for (int i = 0; i < atWidth.Length; i++)
        {
            Assert.Equal(Width, atDouble[i] - atWidth[i]);
        }
    }

    /// <summary>
    /// The budget invariant, measured rather than derived: no list factory can be driven to the
    /// whole-message backstop by any combination of long items, a long reference and huge cardinality. The
    /// failure message reports the headroom so a constant change is self-diagnosing.
    /// </summary>
    [Theory]
    [MemberData(nameof(ListComposingFactories))]
    public void EveryListComposingFactory_StaysUnderTheBackstop(string factory, Func<int, Exception> build)
    {
        ArgumentNullException.ThrowIfNull(build);

        string message = build(5_000).Message;
        int headroom = AnalysisException.MaxMessageLength - message.Length;

        Assert.True(
            headroom > 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"[{factory}] rendered {message.Length} chars, headroom {headroom} — the whole-message cap "
                    + $"is eliding this list again, which destroys its (+N more) count"));
    }

    /// <summary>The sibling factory carries the same contract; a fix applied to only one of the two list
    /// factories is a fix that will drift.</summary>
    [Fact]
    public void AmbiguousReference_BoundsItsCandidateList_WithAnAccurateCount()
    {
        AnalysisException ex = AnalysisException.AmbiguousReference("amount", Columns(75));

        AssertCountIsAccurate(ex.Message, 75);
        Assert.True(ex.Message.Length < AnalysisException.MaxMessageLength);
        Assert.Equal(75, ex.Candidates.Count);
    }

    /// <summary>
    /// A single pathological candidate name is elided with a VISIBLE marker rather than being allowed to
    /// consume the whole listing's budget and crowd out its neighbours.
    /// </summary>
    [Fact]
    public void APathologicalCandidateName_IsElidedPerItem_WithoutHidingTheOthers()
    {
        var input = new[]
        {
            new AttributeReference("alpha", IntegerType.Instance, true, new ExprId(1)),
            new AttributeReference(new string('z', 10_000), IntegerType.Instance, true, new ExprId(2)),
            new AttributeReference("omega", IntegerType.Instance, true, new ExprId(3)),
        };

        string message = AnalysisException.UnresolvedColumn("nosuch", input).Message;

        Assert.Contains("alpha", message, StringComparison.Ordinal);
        Assert.Contains("omega", message, StringComparison.Ordinal);
        Assert.Contains('\u2026', message);
        Assert.DoesNotContain(new string('z', AnalysisException.MaxEchoedCandidateLength + 1), message, StringComparison.Ordinal);
    }
    /// <summary>
    /// Round 9, Architect BLOCKING — the listing budget is only as honest as the PROSE is bounded.
    /// <para>The budget for a listing is <c>MaxMessageLength − prose</c>, so an <b>unbounded free token</b>
    /// interpolated into that prose can drive the message past the cap on its own. The whole-message backstop
    /// then cuts from the tail, which is precisely where the listing and its <c>(+N more)</c> count live. An
    /// 816-character path did exactly that to <see cref="AnalysisException.UnsupportedDataSink"/> and
    /// <see cref="AnalysisException.UnsupportedWriteFormat"/> — the same defect the listing budget was built
    /// to prevent, arriving through the prose instead of through the list.</para>
    /// <para>This ranges over every list-composing factory by REFLECTION and oversizes <b>every</b> string
    /// parameter, not a named one. That distinction is the point: the review repro used the <c>path</c>
    /// parameter, but <c>format</c> was equally unbounded and equally capable of it, and a test naming
    /// <c>path</c> would have closed one of the two. A factory added later with a new free token is caught
    /// without anyone remembering to add a row.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ListComposingFactoryNames))]
    public void NoFreeProseToken_CanCrowdOutAListingsOverflowCount(string factoryName)
    {
        MethodInfo factory = FactoryMethods().Single(m => m.Name == factoryName);
        object[] args = [.. factory.GetParameters().Select((p, i) => Oversized(p.ParameterType, i))];
        var ex = (AnalysisException)factory.Invoke(null, args)!;

        // Sanitize appends an elision mark, so a message the backstop has cut is exactly one character over
        // the cap. That makes "the backstop fired" directly observable rather than inferred.
        Assert.True(
            ex.Message.Length <= AnalysisException.MaxMessageLength,
            string.Create(
                CultureInfo.InvariantCulture,
                $"[{factoryName}] rendered {ex.Message.Length} chars, so the whole-message backstop did the "
                    + $"cutting and took the overflow count off the tail: …{ex.Message[^60..]}"));

        // The listing must be ACCOUNTED FOR, not merely bounded: every item is either rendered or counted.
        // Demanding a count unconditionally would be wrong — a narrow listing can genuinely fit beside even
        // a pathological token, and then there is nothing to report.
        string[][] listings = [.. args.OfType<string[]>()];
        if (listings.Length > 0)
        {
            int total = listings.Sum(l => l.Length);
            int shown = listings.Sum(l => l.Count(n => ex.Message.Contains(n, StringComparison.Ordinal)));
            Assert.Equal(total, shown + OverflowCounts(ex.Message).Sum());
        }
    }

    /// <summary>
    /// The other half of the same bound, and the one that has been claimed before without a corpus able to
    /// exercise it: bounding the free tokens must cost the COMMON case nothing. Allocation is max-min fair
    /// and shortest-first, so an ordinary format plus an ordinary path consume only what they need and are
    /// reproduced verbatim — no elision mark anywhere in the message.
    /// </summary>
    [Fact]
    public void FreeProseTokens_AreBoundedOnlyWhenTheyDoNotFit()
    {
        const string Path = "s3://analytics-prod-lake/warehouse/customer/lifetime_value/dt=2026-07-28/part-0000";
        string[] formats = ["csv", "json", "parquet"];

        foreach (string message in new[]
        {
            AnalysisException.UnsupportedDataSink("delta", Path, formats).Message,
            AnalysisException.UnsupportedWriteFormat("delta", Path, formats, ["delta", "parquet"]).Message,
        })
        {
            Assert.Contains(Path, message, StringComparison.Ordinal);
            Assert.Contains("'delta'", message, StringComparison.Ordinal);
            foreach (string format in formats)
            {
                Assert.Contains(format, message, StringComparison.Ordinal);
            }

            Assert.DoesNotContain('\u2026', message);
        }
    }

    /// <summary>Every public factory that composes at least one listing, found by reflection so the
    /// enumeration cannot drift from the type.</summary>
    private static IEnumerable<MethodInfo> FactoryMethods() =>
        typeof(AnalysisException)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(AnalysisException))
            .Where(m => m.GetParameters().Any(p => IsCollection(p.ParameterType)));

    public static TheoryData<string> ListComposingFactoryNames()
    {
        var data = new TheoryData<string>();
        foreach (MethodInfo m in FactoryMethods())
        {
            data.Add(m.Name);
        }

        return data;
    }

    private static bool IsCollection(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>);

    /// <summary>A pathological value for each parameter shape: every free token long enough to consume the
    /// whole message on its own, and every collection wide enough to have something to elide.</summary>
    private static object Oversized(Type type, int seed)
    {
        if (type == typeof(string))
        {
            return new string('p', 900);
        }

        if (type == typeof(int))
        {
            return 3;
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (!IsCollection(type))
        {
            throw new NotSupportedException(
                string.Create(CultureInfo.InvariantCulture, $"no oversized value defined for {type}"));
        }

        Type item = type.GetGenericArguments()[0];
        if (item == typeof(string))
        {
            // Distinct per parameter, so a factory composing TWO listings cannot have one list's names
            // counted against the other's overflow.
            return Enumerable.Range(0, 40).Select(i => RealisticName((seed * 1000) + i)).ToArray();
        }

        if (item == typeof(AttributeReference))
        {
            return Columns(40);
        }

        if (item == typeof(DataType))
        {
            return Enumerable.Range(0, 40).Select(object (_) => IntegerType.Instance).Cast<DataType>().ToArray();
        }

        throw new NotSupportedException(
            string.Create(CultureInfo.InvariantCulture, $"no oversized collection defined for {item}"));
    }

}
