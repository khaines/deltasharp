using System;
using DeltaSharp.Storage.Delta;
using Xunit;
using SharedDiagnosticText = DeltaSharp.Diagnostics.DiagnosticText;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// #687 council round 2, item 3 — the <b>length-bound drift guard</b>, the sibling of
/// <see cref="DiagnosticTextElisionBoundTests"/>.
/// <para>Three layers couple to one default echo length: the shared primitive in
/// <c>DeltaSharp.Abstractions</c>, Storage's forwarder, and <c>SqlParser.EchoedTokenMaxLength</c> in
/// <c>DeltaSharp.Core</c>. The hazard is subtle and is the same one the elision bound had: an
/// <i>optional-parameter default</i> in a forwarder binds the FORWARDING layer's constant at the call site, so
/// while Storage declared its own verbatim <c>128</c>, editing the Abstractions constant silently did nothing
/// to any Storage caller. Storage now aliases the shared declaration
/// (<c>internal const int DefaultMaxLength = SharedDiagnosticText.DefaultMaxLength;</c> — legal for
/// <c>const int</c>), so there is exactly one definition and the coupling is compile-time.</para>
/// <para>The bound is pinned to a <b>literal</b> on purpose. A bare
/// <c>Assert.Equal(SharedDiagnosticText.DefaultMaxLength, DiagnosticText.DefaultMaxLength)</c> would move with
/// the constant and be vacuous under exactly the mutation it guards; the literal forces a deliberate
/// re-baseline.</para>
/// </summary>
public sealed class DiagnosticTextLengthBoundTests
{
    /// <summary>The default echo length, written as a literal on purpose (see the class remark).</summary>
    private const int ExpectedDefaultMaxLength = 128;

    [Fact]
    public void SharedDefaultMaxLength_IsExactlyOneHundredTwentyEight_PinnedAsALiteral()
    {
        Assert.Equal(ExpectedDefaultMaxLength, SharedDiagnosticText.DefaultMaxLength);
    }

    [Fact]
    public void StorageDefaultMaxLength_IsTheSharedDeclaration_NotAnIndependentCopy()
    {
        // If someone re-introduces a verbatim `128` in Storage this still passes — which is why the
        // BEHAVIOURAL assertion below matters: it proves Storage's default-parameter binding actually tracks
        // the shared bound rather than merely agreeing with it today.
        Assert.Equal(ExpectedDefaultMaxLength, DiagnosticText.DefaultMaxLength);
    }

    [Fact]
    public void StorageSanitize_DefaultOverload_ElidesAtExactlyTheSharedBound()
    {
        // The boundary is inclusive on both helpers: exactly-at-bound survives whole, one-over elides.
        string atBound = new('c', ExpectedDefaultMaxLength);
        string overBound = new('c', ExpectedDefaultMaxLength + 1);

        Assert.Equal(atBound, DiagnosticText.Sanitize(atBound));
        Assert.DoesNotContain('\u2026', DiagnosticText.Sanitize(atBound));

        string elided = DiagnosticText.Sanitize(overBound);
        Assert.NotEqual(overBound, elided);
        Assert.Contains('\u2026', elided);

        // And the two layers agree character-for-character at the boundary, which is the actual invariant:
        // Storage's default-parameter binding resolves to the same number the shared primitive uses.
        Assert.Equal(SharedDiagnosticText.Sanitize(overBound), elided);
    }

    [Fact]
    public void CleanInputUnderTheBound_IsReturnedUnchanged_FastPathIsObservablyAllocationFree()
    {
        // #696 council item 9: Sanitize is now called per-row-group x per-nested-column, so the clean-input
        // fast path must return the SAME INSTANCE (no copy, no StringBuilder) rather than an equal string.
        // ReferenceEquals is the only assertion that can tell those two apart.
        const string clean = "col-00000000-0000-0000-0000-000000000000";

        Assert.True(ReferenceEquals(clean, SharedDiagnosticText.Sanitize(clean)));
        Assert.True(ReferenceEquals(clean, DiagnosticText.Sanitize(clean)));
    }

    [Fact]
    public void FastPath_DoesNotSwallowInjectionUnsafeInput_EvenWhenUnderTheBound()
    {
        // The negative half of the fast path: short-but-hostile input must still take the slow path.
        const string hostile = "TRAIL\r\nFORGED";

        string sanitized = SharedDiagnosticText.Sanitize(hostile);

        Assert.False(ReferenceEquals(hostile, sanitized));
        Assert.Equal("TRAIL\uFFFD\uFFFDFORGED", sanitized);
    }
}
