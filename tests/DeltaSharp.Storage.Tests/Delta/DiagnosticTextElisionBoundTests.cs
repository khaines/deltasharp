using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// #687 follow-up — the <b>elision-bound drift guard</b>. Storage elides an attacker-influenceable list two
/// different ways, and both must stay on the SAME bound:
/// <list type="number">
/// <item>through <see cref="DiagnosticText.SanitizeAndJoin"/> (e.g. <c>DeltaProtocolException</c>'s
/// unsupported reader/writer feature list); and</item>
/// <item>by reading <see cref="DiagnosticText.MaxEchoedListItems"/> <i>directly</i> for a hand-rolled listing
/// (e.g. <c>DeltaConstraintDependentColumnException</c>'s dependent-CHECK listing).</item>
/// </list>
/// When the sanitizing primitive was hoisted into <c>DeltaSharp.Abstractions</c> (so
/// <c>DeltaSharp.Core</c>'s SQL parser could share it without a Core→Storage dependency), path 1 briefly
/// inherited the primitive's OWN item cap while path 2 kept reading Storage's — two independent constants
/// behind one name, agreeing today, with no signal if either moved. The primitive now takes the item cap as a
/// <b>required</b> parameter and Storage passes its own constant, so the two paths are provably governed by a
/// single declaration.
/// <para>These tests are the standing guard. #686's aggregate-flooding regressions assert <c>… (+N more)</c>
/// behaviour and several message-length ceilings that all derive from this bound, so it is pinned to a
/// <b>literal</b> — a bare <c>Assert.Equal(MaxEchoedListItems, …)</c> would move with the constant and prove
/// nothing.</para>
/// </summary>
public sealed class DiagnosticTextElisionBoundTests
{
    /// <summary>The elision bound, written as a literal on purpose (see the class remark).</summary>
    private const int ExpectedElisionBound = 16;

    /// <summary>Comfortably more items than the bound, so every test sees a real elision.</summary>
    private const int OversizedListCount = 20;

    [Fact]
    public void MaxEchoedListItems_IsExactlySixteen_PinnedAsALiteral()
    {
        // The single authoritative Storage elision bound. Changing it is a deliberate act that must also
        // re-baseline #686's flooding regressions and message-length ceilings — this assertion is the tripwire
        // that forces that conversation instead of letting the bound drift silently.
        Assert.Equal(ExpectedElisionBound, DiagnosticText.MaxEchoedListItems);
    }

    [Fact]
    public void SanitizeAndJoin_ElidesAtStorageDeclaredBound_NotAtAPrimitiveSuppliedDefault()
    {
        string[] tokens = Enumerable.Range(0, OversizedListCount)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"tok{i:D2}"))
            .ToArray();

        string joined = DiagnosticText.SanitizeAndJoin(tokens, DiagnosticText.ConfigTokenMaxLength);

        // Exactly the declared bound is rendered; the first dropped item is genuinely absent.
        Assert.Equal(ExpectedElisionBound, CountPresent(tokens, joined));
        Assert.Contains("tok15", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("tok16", joined, StringComparison.Ordinal);

        // The observable render contract #686 depends on: ", " separator, and the elision tail spelled
        // EXACTLY "… (+N more)" with N counting the dropped remainder.
        Assert.EndsWith(", … (+4 more)", joined, StringComparison.Ordinal);
        Assert.Equal(ExpectedElisionBound + 1, joined.Split(", ", StringSplitOptions.None).Length);
    }

    [Fact]
    public void BothStorageElisionPaths_AgreeOnTheBound_ForTheSameListSize()
    {
        // The cross-path guard. One path goes through SanitizeAndJoin, the other reads MaxEchoedListItems
        // directly; if they ever desynchronize, two Storage message surfaces that #666 deliberately made
        // uniform would start eliding at different counts. Driven through the real public factories, so this
        // exercises the shipping messages rather than the helper in isolation.
        string[] features = Enumerable.Range(0, OversizedListCount)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"feat{i:D2}"))
            .ToArray();
        DeltaTableConstraint[] dependents = Enumerable.Range(0, OversizedListCount)
            .Select(i => new DeltaTableConstraint(
                DeltaConstraintKind.Check,
                string.Create(CultureInfo.InvariantCulture, $"feat{i:D2}"),
                "amount > 0"))
            .ToArray();

        string viaSanitizeAndJoin = DeltaProtocolException.UnsupportedFeatures("reader", features).Message;
        string viaDirectConstant =
            DeltaConstraintDependentColumnException.ForColumnChange("amount", dependents).Message;

        int shownByJoin = CountPresent(features, viaSanitizeAndJoin);
        int shownByDirect = CountPresent(features, viaDirectConstant);

        Assert.Equal(shownByJoin, shownByDirect);
        Assert.Equal(ExpectedElisionBound, shownByJoin);

        // Both spell the remainder identically, so the two surfaces stay visually uniform too.
        const string tail = "… (+4 more)";
        Assert.Contains(tail, viaSanitizeAndJoin, StringComparison.Ordinal);
        Assert.Contains(tail, viaDirectConstant, StringComparison.Ordinal);
    }

    [Fact]
    public void ListShorterThanTheBound_IsRenderedWhole_WithNoElisionTail()
    {
        // The negative half: the bound only bites above itself, so an ordinary short list is untouched.
        string[] tokens = Enumerable.Range(0, ExpectedElisionBound)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"tok{i:D2}"))
            .ToArray();

        string joined = DiagnosticText.SanitizeAndJoin(tokens, DiagnosticText.ConfigTokenMaxLength);

        Assert.Equal(ExpectedElisionBound, CountPresent(tokens, joined));
        Assert.DoesNotContain("more)", joined, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2026', joined);
    }

    private static int CountPresent(IReadOnlyList<string> tokens, string rendered) =>
        tokens.Count(t => rendered.Contains(t, StringComparison.Ordinal));
}
