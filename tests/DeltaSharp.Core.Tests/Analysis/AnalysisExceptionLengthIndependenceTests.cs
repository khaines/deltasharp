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
/// #687 council round 4 (Quality) — the <b>length-independence property</b> for
/// <see cref="AnalysisException"/>'s whole-message backstop.
/// <para><b>The vacuity this closes.</b> <c>AnalysisException.MaxMessageLength</c> is the SOLE length bound for
/// the dozen factories that compose their own message and never touch the expression renderer, so
/// <c>DiagnosticReferenceMaxLength</c> (layer 1) cannot help them. Every assertion guarding it was written in
/// terms of the constant itself — <c>message.Length &lt;= AnalysisException.MaxMessageLength + 1</c> — so all
/// of them moved with the mutation they existed to catch. Raising the constant to 1&#160;000&#160;000 produced
/// <b>zero</b> failures across all 5&#160;212 tests in all five suites, while a hostile
/// <c>delta.constraints</c> input the suite ALREADY exercises rendered a 100&#160;143-character
/// attacker-controlled log line. That is the exact unbounded-render class #687 exists to close, shipping green.
/// </para>
/// <para><b>Why a property and not a literal pin.</b> Elsewhere in this PR the remedy for a
/// constant-referencing guard was to pin a literal (<c>DiagnosticText.DefaultMaxLength</c> to <c>128</c>,
/// <c>MaxEchoedListItems</c> to <c>16</c>), because there the NUMBER is the contract that downstream tests
/// depend on. Here the number is not the contract — <i>boundedness</i> is. A literal pin would forbid a
/// legitimate re-baseline (say 1024 → 2048 for a genuinely richer diagnostic) while still permitting the
/// dangerous change if someone re-baselined the pin along with the constant. A property that says "the render
/// does not grow with the attacker's input" survives a deliberate re-baseline and is the shape already used
/// one layer up (<c>RenderedReference_IsIndependentOfAttackerInputLength</c>).</para>
/// <para><b>How far that survival actually extends (round 5, Quality).</b> Not unqualified, and the honest
/// bound is worth stating rather than implying: the property holds for any re-baseline of the cap up to
/// roughly the SMALL scale below (~10&#160;000 rendered characters on both axes), because past that the small
/// payload stops being truncated and there is no longer a bounded render to compare against. Beyond it the
/// suite fails <b>closed</b>, on the non-vacuity precondition, with a message naming the axis and the observed
/// length — a test that says "this case can no longer observe the backstop" rather than one that silently
/// passes. That is the correct failure direction, and re-scaling is then a one-line change here. Quality found
/// this by re-baselining to 4096, which the original cardinality scale (200 items, 1030–3774 chars) could not
/// survive; both scales below are now chosen so the two axes have comparable headroom.</para>
/// <para><b>Coverage is the class, not an example.</b> The property is asserted over every factory that
/// interpolates attacker-influenceable text, along BOTH growth axes each one exposes: the length of an
/// individual token, and the CARDINALITY of a list (a wide hostile schema, a long candidate set). A property
/// that held for two of twelve factories would be better than a literal pin but weaker than the guarantee the
/// backstop actually owes.</para>
/// </summary>
public sealed class AnalysisExceptionLengthIndependenceTests
{
    /// <summary>Both scales must exceed the backstop so truncation is genuinely engaged; they differ by 10x so
    /// any length term that survives is loud.</summary>
    private const int SmallPayload = 10_000;

    private const int LargePayload = 100_000;

    private static string Pad(int length) => new('z', length);

    private static IReadOnlyList<AttributeReference> Columns(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new AttributeReference(
                string.Create(CultureInfo.InvariantCulture, $"c{i:D6}"),
                IntegerType.Instance,
                true,
                new ExprId(i + 1)))
            .ToArray();

    private static IReadOnlyList<string> Names(int count) =>
        Enumerable.Range(0, count)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"n{i:D6}"))
            .ToArray();

    private static IReadOnlyList<DataType> Types(int count) =>
        Enumerable.Repeat<DataType>(IntegerType.Instance, count).ToArray();

    /// <summary>Every factory that interpolates attacker-influenceable text, keyed by the growth axis under
    /// test. The <see cref="Func{T, TResult}"/> takes the payload scale and returns the composed exception.
    /// </summary>
    public static TheoryData<string, Func<int, Exception>> UnboundedGrowthAxes() => new()
    {
        // ---- token-length axis -------------------------------------------------------------------------
        { "UnknownFunction/name", n => AnalysisException.UnknownFunction(Pad(n), Types(1)) },
        { "UnresolvedColumn/name", n => AnalysisException.UnresolvedColumn(Pad(n), Columns(2)) },
        { "AmbiguousReference/name", n => AnalysisException.AmbiguousReference(Pad(n), Columns(2)) },
        { "TableOrViewNotFound/part", n => AnalysisException.TableOrViewNotFound(new[] { "ns", Pad(n) }) },
        { "InvalidFunctionArgument/name", n => AnalysisException.InvalidFunctionArgument(Pad(n), Types(1), "an integer") },
        { "InvalidFunctionArgument/expected", n => AnalysisException.InvalidFunctionArgument("f", Types(1), Pad(n)) },
        { "DataTypeMismatch/reference", n => AnalysisException.DataTypeMismatch(Pad(n), "boolean expected") },
        { "DataTypeMismatch/detail", n => AnalysisException.DataTypeMismatch("(a > b)", Pad(n)) },
        { "UnresolvedStructField/reference", n => AnalysisException.UnresolvedStructField(Pad(n), "no such field") },
        { "UnresolvedStructField/detail", n => AnalysisException.UnresolvedStructField("s.f", Pad(n)) },
        { "UnresolvedExpression/reference", n => AnalysisException.UnresolvedExpression(Pad(n), "Project") },
        { "UnsupportedProjection/message", n => AnalysisException.UnsupportedProjection(Pad(n)) },

        // NOT an axis, verified rather than assumed: UnsupportedProjection's `reference` argument goes only to
        // the typed Reference property and is never interpolated into the message. That is the deliberate
        // design from earlier in this PR — Reference/RootColumn/Candidates stay RAW so DeltaSinkFactory can
        // match on them — so there is nothing for the backstop to bound. Adding it as a row failed the
        // non-vacuity precondition, which is the theory working as intended.
        { "MisplacedAggregate/reference", n => AnalysisException.MisplacedAggregate(Pad(n), "Filter") },
        { "NestedAggregate/outer", n => AnalysisException.NestedAggregate(Pad(n), "sum(x)") },
        { "UntypedResolvedExpression/reference", n => AnalysisException.UntypedResolvedExpression(Pad(n), "Project") },
        { "UnsupportedDataSource/path", n => AnalysisException.UnsupportedDataSource("parquet", Pad(n)) },
        { "UnsupportedDataSource/format", n => AnalysisException.UnsupportedDataSource(Pad(n), "/t") },
        { "ConflictingTimeTravel/detail", n => AnalysisException.ConflictingTimeTravel("/t", Pad(n)) },
        { "InvalidTimeTravelValue/value", n => AnalysisException.InvalidTimeTravelValue("version", Pad(n), "not a long") },
        { "InvalidTimeTravelValue/reason", n => AnalysisException.InvalidTimeTravelValue("version", "x", Pad(n)) },
        { "FileSourceResolutionFailed/reason", n => AnalysisException.FileSourceResolutionFailed("parquet", "/t", Pad(n)) },
        { "UnsupportedDataSink/format", n => AnalysisException.UnsupportedDataSink(Pad(n), "/t", Names(2)) },
        { "UnsupportedWriteFormat/format", n => AnalysisException.UnsupportedWriteFormat(Pad(n), "/t", Names(2), Names(2)) },
    };

    // THE CARDINALITY THEORY IS GONE, and its absence is the point (round 6, Architect).
    //
    // It used to hold the factories that relied on this backstop to bound a LIST. Every one of them now
    // bounds its own items and reports an explicit (+N more) count, so none of them relies on the backstop
    // for cardinality any more and none can be asserted to render identically across widths — the numeral
    // differs, which is exactly what makes the diagnostic honest.
    //
    // What replaced it is stronger, and is deliberately in AnalysisExceptionCandidateListingTests rather
    // than here: EveryListComposingFactory_ReportsAnOverflowCount ranges over the whole class instead of
    // enumerating axes by hand, which is what the earlier revision of this suite got wrong — it pinned the
    // truncation of UnknownFunction and InvalidFunctionArgument AS EXPECTED BEHAVIOUR and so ratified the
    // very defect it was meant to guard against.

    [Theory]
    [MemberData(nameof(UnboundedGrowthAxes))]
    public void FactoryRender_IsIndependentOfAttackerTokenLength(string axis, Func<int, Exception> build)
    {
        AssertIndependent(axis, build, SmallPayload, LargePayload);
    }

    private static void AssertIndependent(
        string axis, Func<int, Exception> build, int small, int large)
    {
        ArgumentNullException.ThrowIfNull(build);

        string smallMessage = build(small).Message;
        string largeMessage = build(large).Message;

        // Non-vacuity: the SMALLER payload must already have been bounded, otherwise "the two renders are
        // equal" would be trivially true for reasons unrelated to bounding. The elision glyph is the direct,
        // constant-free evidence that truncation fired — none of the payloads above contain one.
        //
        // CONTAINS, not ENDS-WITH (round 6): the candidate-listing factories now bound their components
        // individually rather than letting the whole message be cut, so the glyph legitimately appears in the
        // MIDDLE of the message. Requiring it at the tail would have made those rows fail for a reason that
        // has nothing to do with the property under test.
        Assert.True(
            smallMessage.Contains('\u2026', StringComparison.Ordinal),
            string.Create(
                CultureInfo.InvariantCulture,
                $"[{axis}] the {small}-scale payload was not truncated (rendered {smallMessage.Length} chars) — "
                    + $"the case is too weak to observe the backstop"));

        // The property itself: a 10x larger attacker input renders the SAME message. No constant is named, so
        // this survives a legitimate re-baseline of the cap while still failing any removal or inflation of it.
        Assert.Equal(smallMessage.Length, largeMessage.Length);
        Assert.Equal(smallMessage, largeMessage);
    }

    /// <summary>
    /// Round 5 (Quality) — asserts the <c>UnsupportedProjection/reference</c> EXCLUSION noted in
    /// <see cref="UnboundedGrowthAxes"/> rather than only commenting it. That argument reaches the typed
    /// <c>Reference</c> property and deliberately never the message, so it is correctly absent from the growth
    /// theory; but with the reason living only in a comment, a future edit that interpolated it would draw no
    /// objection. There would be no leak — the backstop still bounds the message — yet the growth theory would
    /// silently lose an axis it is supposed to cover. This turns that into a failing test.
    /// </summary>
    [Fact]
    public void UnsupportedProjection_Reference_IsNotInterpolatedIntoTheMessage()
    {
        string hostile = Pad(LargePayload);
        AnalysisException ex = AnalysisException.UnsupportedProjection("unsupported projection", hostile);

        // The raw channel is intact: DeltaSinkFactory matches on Reference, so it must NOT be bounded.
        Assert.Equal(hostile, ex.Reference);

        // ...and it must not have reached the message. Probing with a short prefix rather than the whole
        // payload so this still fires if a future edit interpolates a TRUNCATED form of the reference.
        Assert.DoesNotContain(Pad(64), ex.Message, StringComparison.Ordinal);
        Assert.Equal("unsupported projection", ex.Message);
    }

    [Fact]
    public void TheBackstopIsTheOnlyThingBoundingTheseFactories_SoTheyRenderNearIt()
    {
        // A calibration assertion, so a future reader can see WHY the property above is the right guard here
        // rather than layer 1: these factories never touch CoercionHelpers.DiagnosticReference, so nothing
        // shortens them before construction. Deliberately expressed as "close to the bound" without asserting
        // the bound's value.
        // DataTypeMismatch, not UnknownFunction (round 6): the function factories now bound their own
        // components, so they no longer render anywhere near the backstop and could not demonstrate it. This
        // one interpolates a single unbounded token, which is the case the backstop legitimately owns.
        string message = AnalysisException.DataTypeMismatch(Pad(LargePayload), "boolean expected").Message;

        Assert.True(
            message.Length > 512,
            string.Create(CultureInfo.InvariantCulture, $"rendered only {message.Length} chars"));
        Assert.EndsWith("\u2026", message, StringComparison.Ordinal);
    }
}
