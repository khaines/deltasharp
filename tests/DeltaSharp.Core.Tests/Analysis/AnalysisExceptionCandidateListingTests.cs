using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// indication that anything had been dropped. Measured at <c>0ba0f8b</c>: a 50-column table rendered 1025
/// characters (down from 1107) and a 400-column table rendered the <b>byte-identical</b> message — 355
/// candidates silently gone. These are TRUSTED-path names: a user's own schema, not attacker text. Being told
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
        Assert.Contains("(+36 more)", fifty, StringComparison.Ordinal);
        Assert.Contains("(+386 more)", fourHundred, StringComparison.Ordinal);
    }

    /// <summary>The count must be ACCURATE, not merely present — an overflow marker that reports the wrong
    /// number is worse than none, because it is believed.</summary>
    [Theory]
    [InlineData(15)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(400)]
    [InlineData(20_000)]
    public void OverflowCount_NamesExactlyHowManyCandidatesAreHidden(int width)
    {
        AnalysisException ex = AnalysisException.UnresolvedColumn("nosuch", Columns(width));

        int shown = AnalysisException.MaxEchoedCandidates;
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"(+{width - shown} more)"),
            ex.Message,
            StringComparison.Ordinal);

        // ...and the structured channel still carries every one of them, unmodified — including the ones the
        // message elides, which is what keeps the raw channel worth having.
        Assert.Equal(width, ex.Candidates.Count);
        Assert.Equal(RealisticName(width - 1), ex.Candidates[width - 1]);
    }

    /// <summary>A schema that fits is listed in full, with no overflow marker at all — the common case must
    /// not pay for the wide-schema fix.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(14)]
    public void SchemasWithinTheBound_AreListedInFull_WithNoOverflowMarker(int width)
    {
        string message = AnalysisException.UnresolvedColumn("nosuch", Columns(width)).Message;

        Assert.DoesNotContain("more)", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2026', message);
        for (int i = 0; i < width; i++)
        {
            Assert.Contains(RealisticName(i), message, StringComparison.Ordinal);
        }
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
            Assert.Contains("(+4986 more)", message, StringComparison.Ordinal);
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

        Assert.Contains("(+61 more)", ex.Message, StringComparison.Ordinal);
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
}
