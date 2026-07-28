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
    private static IReadOnlyList<AttributeReference> Columns(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new AttributeReference(
                string.Create(CultureInfo.InvariantCulture, $"customer_metric_{i:D3}"),
                IntegerType.Instance,
                true,
                new ExprId(i + 1)))
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
        Assert.Contains("(+30 more)", fifty, StringComparison.Ordinal);
        Assert.Contains("(+380 more)", fourHundred, StringComparison.Ordinal);
    }

    /// <summary>The count must be ACCURATE, not merely present — an overflow marker that reports the wrong
    /// number is worse than none, because it is believed.</summary>
    [Theory]
    [InlineData(21)]
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
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"customer_metric_{width - 1:D3}"),
            ex.Candidates[width - 1]);
    }

    /// <summary>A schema that fits is listed in full, with no overflow marker at all — the common case must
    /// not pay for the wide-schema fix.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void SchemasWithinTheBound_AreListedInFull_WithNoOverflowMarker(int width)
    {
        string message = AnalysisException.UnresolvedColumn("nosuch", Columns(width)).Message;

        Assert.DoesNotContain("more)", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2026', message);
        for (int i = 0; i < width; i++)
        {
            Assert.Contains(
                string.Create(CultureInfo.InvariantCulture, $"customer_metric_{i:D3}"),
                message,
                StringComparison.Ordinal);
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
            Assert.Contains("(+4980 more)", message, StringComparison.Ordinal);
        }
    }

    /// <summary>The sibling factory carries the same contract; a fix applied to only one of the two list
    /// factories is a fix that will drift.</summary>
    [Fact]
    public void AmbiguousReference_BoundsItsCandidateList_WithAnAccurateCount()
    {
        AnalysisException ex = AnalysisException.AmbiguousReference("amount", Columns(75));

        Assert.Contains("(+55 more)", ex.Message, StringComparison.Ordinal);
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
