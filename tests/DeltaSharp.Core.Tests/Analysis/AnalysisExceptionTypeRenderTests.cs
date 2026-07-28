using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DeltaSharp.Analysis;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Analysis;

/// <summary>
/// #687 council round 7 (Balanced) — the <b>type-render</b> contract for every analyzer diagnostic that
/// interpolates a user-authored <see cref="DataType"/> into its message.
/// <para><b>The regression this closes.</b> A type render is not a token, it is a <em>recursive
/// collection</em>: <c>StructType.SimpleString</c> is <c>struct&lt;f1:int,f2:string,…&gt;</c> over
/// user-authored field names. Five call sites interpolated that flat string raw, so the only thing bounding
/// them was the whole-message backstop — which cuts the container and destroys the signal that anything was
/// cut. Measured at <c>b5ebd76</c> for an ordinary <c>df.Select("payload.typo")</c> against a nested payload
/// struct:</para>
/// <code>
/// fields= 40   len=1025   bare ellipsis, no count
/// fields= 60   len=1025   bare ellipsis, no count
/// fields=400   len=1025   bare ellipsis, no count      60-field message == 400-field message: True
/// </code>
/// <para>These are TRUSTED-path field names — the user's own nested schema. Nested payload structs of forty
/// or more fields are ordinary, so this fired on the common case, not the hostile one.</para>
/// <para><b>Why the previous suites could not see it.</b> They ranged over the <em>factories</em> in
/// <c>AnalysisException</c>. These sites compose their text in <c>Analyzer</c> and
/// <c>ExpressionCoercion</c> and hand it to a factory as an opaque <c>detail</c> string, so no
/// factory-ranging theory can reach them. This suite ranges over the <b>diagnostic sites</b> instead, driving
/// each through the real front end with a hostile schema.</para>
/// <para><b>The property, stated once.</b> For every site: the render is bounded, it is control-character
/// free, and whenever it omits fields it says how many. "Bounded" alone is what produced the defect — a cap
/// that truncates the container destroys the evidence that truncation happened.</para>
/// </summary>
public sealed class AnalysisExceptionTypeRenderTests
{
    /// <summary>Field-count scales. The small one is an <em>ordinary</em> nested payload struct: the point
    /// of the finding is that this is not a hostile input.</summary>
    private const int OrdinaryFields = 60;

    private const int WideFields = 400;

    /// <summary>Realistic field-name length (24 characters). A corpus whose items are all shorter than the
    /// bound cannot test the bound — that is precisely why the round-6 suite saw neither of round 7's
    /// defects, and it is not repeated here.</summary>
    private static string FieldName(int i) =>
        string.Create(CultureInfo.InvariantCulture, $"nested_payload_attr_{i:D4}");

    private static StructType WideStruct(int fields) =>
        new(Enumerable.Range(0, fields)
            .Select(i => new StructField(FieldName(i), StringType.Instance, true))
            .ToArray());

    private static StructType PayloadSchema(int fields) =>
        new(
        [
            new StructField("payload", WideStruct(fields), true),
            new StructField("id", IntegerType.Instance, true),
            new StructField("flag", BooleanType.Instance, true),
        ]);

    /// <summary>
    /// Every analyzer diagnostic that renders a user-authored type into its message, driven through the
    /// real constraint front end. Each row is (label, predicate) where the predicate makes the site fire
    /// against <see cref="PayloadSchema"/>.
    /// </summary>
    public static TheoryData<string, string> TypeRenderingSites() => new()
    {
        { "no such struct field", "payload.typo > 0" },
        { "extract from non-struct", "id.typo > 0" },
        { "binary operand mismatch", "payload > 0" },
        { "boolean operand", "payload AND flag" },
    };

    /// <summary>
    /// The subset of sites whose render is the <em>wide</em> type, and so the only rows for which schema
    /// width is observable at all. "extract from non-struct" renders the child's type (<c>int</c>), which
    /// is width-independent by construction — asserting width-discrimination there would be asserting
    /// something false, so it is excluded here rather than weakened everywhere.
    /// </summary>
    public static TheoryData<string, string> WideTypeRenderingSites() => new()
    {
        { "no such struct field", "payload.typo > 0" },
        { "binary operand mismatch", "payload > 0" },
        { "boolean operand", "payload AND flag" },
    };

    private static string MessageFor(string predicate, int fields)
    {
        Exception ex = Assert.ThrowsAny<Exception>(
            () => ConstraintExpressionFrontend.ParseResolveWithInput(predicate, PayloadSchema(fields)));
        return ex.Message;
    }

    private static IReadOnlyList<int> OverflowCounts(string message) =>
        Regex.Matches(message, @"\(\+(\d+) more\)")
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToArray();

    /// <summary>
    /// The headline property. A wide render must stay under the whole-message backstop <b>on its own</b>,
    /// so the backstop is never what does the cutting — if it were, the elision would be a bare ellipsis
    /// with no count, which is the defect.
    /// </summary>
    [Theory]
    [MemberData(nameof(TypeRenderingSites))]
    public void EveryTypeRenderingSite_StaysUnderTheBackstop(string label, string predicate)
    {
        string message = MessageFor(predicate, WideFields);
        Assert.True(
            message.Length < AnalysisException.MaxMessageLength,
            string.Create(
                CultureInfo.InvariantCulture,
                $"[{label}] rendered {message.Length} chars; the backstop at " +
                $"{AnalysisException.MaxMessageLength} would cut it with a bare ellipsis and no count."));
    }

    /// <summary>
    /// The honesty property: a site that drops fields must say how many. Asserted for every site that
    /// actually elides — a site whose render fits is not required to carry a count.
    /// </summary>
    [Theory]
    [MemberData(nameof(TypeRenderingSites))]
    public void EveryTypeRenderingSite_ThatElides_ReportsAnOverflowCount(string label, string predicate)
    {
        string message = MessageFor(predicate, WideFields);
        Assert.True(
            !message.Contains('\u2026', StringComparison.Ordinal)
                || OverflowCounts(message).Count > 0
                || message.Contains(" fields)", StringComparison.Ordinal),
            string.Create(
                CultureInfo.InvariantCulture,
                $"[{label}] elided with a bare ellipsis and no count: {message}"));
    }

    /// <summary>
    /// The discriminating property, and the one that fails loudest against <c>b5ebd76</c>: two schemas of
    /// different width must not produce the same message. At <c>b5ebd76</c> the 60-field and 400-field
    /// messages were byte-identical, so the render carried no information about the schema at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(WideTypeRenderingSites))]
    public void EveryTypeRenderingSite_DistinguishesSchemaWidth(string label, string predicate)
    {
        string ordinary = MessageFor(predicate, OrdinaryFields);
        string wide = MessageFor(predicate, WideFields);
        Assert.False(
            string.Equals(ordinary, wide, StringComparison.Ordinal),
            string.Create(
                CultureInfo.InvariantCulture,
                $"[{label}] a {OrdinaryFields}-field and a {WideFields}-field schema rendered the " +
                $"identical message, so the width is unobservable: {wide}"));
    }

    /// <summary>Hygiene still holds at every site: field names are user-authored and may carry CR/LF.</summary>
    [Theory]
    [MemberData(nameof(TypeRenderingSites))]
    public void EveryTypeRenderingSite_IsControlCharacterFree(string label, string predicate)
    {
        var hostile = new StructType(
        [
            new StructField("payload", new StructType(
                [new StructField("forged\r\nname", StringType.Instance, true)]), true),
            new StructField("id", IntegerType.Instance, true),
            new StructField("flag", BooleanType.Instance, true),
        ]);

        Exception ex = Assert.ThrowsAny<Exception>(
            () => ConstraintExpressionFrontend.ParseResolveWithInput(predicate, hostile));
        Assert.DoesNotContain(ex.Message, char.IsControl);
        Assert.True(ex.Message.Length < AnalysisException.MaxMessageLength, $"[{label}] unbounded");
    }

    /// <summary>
    /// Count accuracy for the struct-field site: shown fields plus the reported overflow must equal the
    /// schema width. An inaccurate count is worse than none — it is a confident lie.
    /// </summary>
    [Theory]
    [InlineData(OrdinaryFields)]
    [InlineData(WideFields)]
    public void StructFieldRender_ReportsAnAccurateCount(int fields)
    {
        string message = MessageFor("payload.typo > 0", fields);
        int shown = Regex.Matches(message, @"nested_payload_attr_\d{4}:").Count;
        int reported = Assert.Single(OverflowCounts(message));
        Assert.Equal(fields, shown + reported);
    }

    /// <summary>
    /// Round 6's elevated finding, pinned with Balanced's exact corpus: three legitimate, distinct column
    /// names that share a 32-character prefix, in a message far short of the backstop — so nothing needed
    /// bounding here at all, and a flat per-item cap collapsed distinct names anyway. No cross-HEAD
    /// character counts are quoted: see the note on the sibling test in
    /// <c>AnalysisExceptionCandidateListingTests</c> for why that sentence is now stated as a property
    /// rather than as a measurement.
    /// </summary>
    [Fact]
    public void LegitimateNamesSharingALongPrefix_AreListedInFull()
    {
        var schema = new StructType(
        [
            new StructField("customer_lifetime_value_usd_2023", StringType.Instance, true),
            new StructField("customer_lifetime_value_usd_2023_q1", StringType.Instance, true),
            new StructField("customer_lifetime_value_usd_2023_q2", StringType.Instance, true),
        ]);

        string message = Assert.ThrowsAny<Exception>(
            () => ConstraintExpressionFrontend.ParseResolveWithInput("nosuch > 0", schema)).Message;

        Assert.DoesNotContain('\u2026', message);
        foreach (StructField field in schema)
        {
            Assert.Contains(field.Name, message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The <b>type-list</b> factories (<c>UnknownFunction</c>, <c>InvalidFunctionArgument</c>) render a
    /// list whose elements are themselves collections. Bounding the list without bounding each element
    /// leaves a single wide struct argument to blow the whole message — the same defect one level up.
    /// Width must stay observable through both the element bound and the list bound.
    /// </summary>
    [Theory]
    [InlineData("UnknownFunction")]
    [InlineData("InvalidFunctionArgument")]
    public void TypeListFactories_BoundEachElementAndKeepWidthObservable(string factory)
    {
        string Render(int fields)
        {
            DataType[] types = [WideStruct(fields), IntegerType.Instance];
            AnalysisException ex = factory == "UnknownFunction"
                ? AnalysisException.UnknownFunction("my_udf", types)
                : AnalysisException.InvalidFunctionArgument("my_udf", types, "a numeric type");
            return ex.Message;
        }

        string ordinary = Render(OrdinaryFields);
        string wide = Render(WideFields);

        Assert.True(wide.Length < AnalysisException.MaxMessageLength, wide);
        Assert.NotEqual(ordinary, wide);

        // Honest as well as bounded: shown fields plus the reported overflow must equal the real width.
        foreach ((string message, int fields) in new[] { (ordinary, OrdinaryFields), (wide, WideFields) })
        {
            int shown = Regex.Matches(message, @"nested_payload_attr_\d{4}:").Count;
            Assert.Equal(fields, shown + OverflowCounts(message).Sum());
        }
    }

    /// <summary>
    /// The renderer's hard length guarantee, exercised over the pathological shapes the budget-driven walk
    /// has to survive: deep nesting (which carries no fields and so never trips the character budget), a
    /// single enormous field name, and a wide struct behind several composite wrappers.
    /// </summary>
    [Theory]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(320)]
    public void DiagnosticType_NeverExceedsItsBudget_ForAnyShape(int budget)
    {
        foreach (DataType type in PathologicalTypes())
        {
            string rendered = CoercionHelpers.DiagnosticType(type, budget);
            Assert.True(
                rendered.Length <= budget,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"budget {budget} exceeded: {rendered.Length} chars for {type.GetType().Name}"));
            Assert.DoesNotContain(rendered, char.IsControl);
        }
    }

    /// <summary>
    /// The CASE-branch result-type site (<c>ExpressionCoercion.CoerceCaseWhen</c>). The Core SQL parser has
    /// no <c>CASE</c> grammar, so this site is <b>not reachable</b> through the constraint front end that
    /// drives the theories above — a SQL row for it would have died on
    /// <c>unexpected trailing input 'WHEN'</c> and passed for the wrong reason. It is reached instead
    /// through the coercion pass directly, which is the door the DataFrame <c>when(...)</c> builder uses.
    /// </summary>
    [Fact]
    public void CaseBranchTypeList_IsBoundedAndCountCarrying()
    {
        var wide = new AttributeReference("payload", WideStruct(WideFields), true, new ExprId(1));
        // Two branches whose result types have no common type: the wide struct and an int.
        CaseWhen caseWhen = new CaseWhen(Literal.OfBoolean(true), wide)
            .WithElse(Literal.OfInt(1));

        Exception ex = Assert.ThrowsAny<Exception>(() => ExpressionCoercion.Coerce(caseWhen));
        Assert.True(ex.Message.Length < AnalysisException.MaxMessageLength, ex.Message);
        Assert.DoesNotContain(ex.Message, char.IsControl);
    }

    private static IEnumerable<DataType> PathologicalTypes()
    {
        yield return WideStruct(5_000);
        yield return new StructType([new StructField(new string('n', 10_000), IntegerType.Instance, true)]);
        yield return new ArrayType(new MapType(StringType.Instance, WideStruct(500), true), true);

        DataType deep = WideStruct(50);
        for (int i = 0; i < 40; i++)
        {
            deep = new ArrayType(deep, true);
        }

        yield return deep;
        yield return IntegerType.Instance;
    }
    /// <summary>
    /// #687 council round 10 (Balanced BLOCKING 1) — the type-render analogue of
    /// <c>ListingBudget_IsSpentBeforeAnythingIsElided</c>, which is the one test shape that has actually
    /// caught this family.
    /// <para>The listing constants were replaced by a derivation two rounds ago, but types kept a hand-picked
    /// <c>DiagnosticTypeMaxLength = 320</c> shared by the one-slot and two-slot sites. It elided an ordinary
    /// nested payload struct barely a dozen fields wide, with most of the message budget unspent, while
    /// claiming in its own doc to show such a struct intact. It was
    /// also unpinned: cutting it to 64, which elides a <em>two</em>-field struct, was 0 RED across the suite.
    /// That is the same vacuity the listing corpus had, one family over, and it landed because this file
    /// asserted backstop, count, width and hostile-input properties but never <b>utilisation</b>.</para>
    /// <para>The property needs no constant: either nothing was elided and every field name is present, or
    /// something was elided and one more field at this schema's own name length would not have fit. Sweeping
    /// widths continuously is the part that matters — a corpus sampling 60 and 400 cannot see a bound that
    /// bites at 13.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ContinuousFieldWidths))]
    public void TypeBudget_IsSpentBeforeAnyFieldIsElided(int fields)
    {
        string message = MessageFor("payload.typo > 0", fields);

        if (!message.Contains('\u2026'))
        {
            for (int i = 0; i < fields; i++)
            {
                Assert.Contains(FieldName(i), message, StringComparison.Ordinal);
            }

            return;
        }

        // Elided: prove it was necessary rather than merely permitted. "int" is the narrowest field this
        // schema produces, so if even that would still have fitted, the budget was left unspent.
        int narrowest = FieldName(0).Length + ":string,".Length;
        Assert.True(
            message.Length + narrowest > AnalysisException.MaxMessageLength,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{fields} fields elided at {message.Length} chars with room for another {narrowest} under "
                    + $"the {AnalysisException.MaxMessageLength} cap — the type budget is not being spent"));
    }

    /// <summary>Every width from 1 to 60. The bound that shipped bit at 13, between the two widths this file
    /// previously sampled.</summary>
    public static TheoryData<int> ContinuousFieldWidths()
    {
        var data = new TheoryData<int>();
        for (int fields = 1; fields <= 60; fields++)
        {
            data.Add(fields);
        }

        return data;
    }

    /// <summary>
    /// The field-name bound must not cut a REAL field name, and the corpus must be able to tell.
    /// <para>A flat cap of 32 stood inside the type renderer — the same number four seats rejected for
    /// candidate names — and every field name the file generated was shorter than it, so no row could reach
    /// it. Restoring the 32 was measured at <b>0 RED across the whole suite</b>. That is the vacuity pattern
    /// this PR keeps hitting: a corpus whose items are all shorter than the bound cannot test the bound. The
    /// widths themselves are not named here; they are read off the generators by
    /// <see cref="TheFixturesThatPredateThisPin_CannotReachTheDiscriminatingRegion"/>, because every attempt
    /// in this change to write down what the fixtures are has been wrong.</para>
    /// <para>These names are real-world lengths (35 and 43). The first must survive verbatim; the point of
    /// the ceiling is to stop ONE pathological name consuming the render, not to trim ordinary schemas.</para>
    /// </summary>
    [Fact]
    public void RealisticFieldNames_SurviveTheFieldNameCeilingVerbatim()
    {
        const string Long = "customer_lifetime_value_rolling_90d";
        const string Longer = "net_revenue_retention_trailing_twelve_mths";
        var payload = new StructType(
        [
            new StructField(Long, StringType.Instance, true),
            new StructField(Longer, StringType.Instance, true),
        ]);
        var schema = new StructType(
        [
            new StructField("payload", payload, true),
            new StructField("other", IntegerType.Instance, true),
        ]);

        Exception ex = Assert.ThrowsAny<Exception>(
            () => ConstraintExpressionFrontend.ParseResolveWithInput("payload.typo > 0", schema));

        Assert.Contains(Long, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Longer, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2026', ex.Message);
    }

    /// <summary>
    /// The ceiling is nevertheless load-bearing: a single pathological name must not be able to consume the
    /// render and starve every sibling field. Bounded, and the count still reports what was dropped.
    /// </summary>
    [Fact]
    public void OnePathologicalFieldName_CannotStarveItsSiblings()
    {
        var payload = new StructType(
        [
            new StructField(new string('z', 5_000), StringType.Instance, true),
            .. Enumerable.Range(0, 8).Select(i => new StructField(FieldName(i), StringType.Instance, true)),
        ]);
        var schema = new StructType(
        [
            new StructField("payload", payload, true),
            new StructField("other", IntegerType.Instance, true),
        ]);

        Exception ex = Assert.ThrowsAny<Exception>(
            () => ConstraintExpressionFrontend.ParseResolveWithInput("payload.typo > 0", schema));

        Assert.True(ex.Message.Length <= AnalysisException.MaxMessageLength, ex.Message);
        Assert.Contains(FieldName(0), ex.Message, StringComparison.Ordinal);
    }


    /// <summary>
    /// #687 council round 12 (Quality) — the <b>type-render depth bound</b>. Mutating
    /// <c>MaxEchoedTypeDepth</c> from 4 to 1 was 0 RED, so the entire depth dimension of this suite was
    /// unguarded: every assertion here concerned width, length or the backstop, and none concerned nesting.
    /// <para>The claim the bound has to earn is a UX one — an ordinary nested payload must render with its
    /// field names intact, all the way to the leaf. Delta schemas nest naturally (<c>address.geo.latitude</c>)
    /// and a diagnostic that collapses the level containing the misspelling is no help. So this asserts the
    /// depth at which real schemas live rather than restating the constant: nest a realistic payload and
    /// require the leaf field name verbatim. Measured at this HEAD, nesting 1–4 keeps it and 5 collapses to
    /// <c>(1 fields)</c>, so this is the bound's actual reach, not a number copied from the source.</para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void OrdinaryNestedPayloads_RenderTheirLeafFieldNameVerbatim(int nesting)
    {
        DataType nested = new StructType([new StructField("latitude_degrees", DoubleType.Instance, true)]);
        for (int level = 1; level < nesting; level++)
        {
            nested = new StructType(
            [
                new StructField(
                    string.Create(CultureInfo.InvariantCulture, $"geo_location_{level}"), nested, true),
            ]);
        }

        var schema = new StructType([new StructField("payload", nested, true)]);
        Exception ex = Assert.ThrowsAny<Exception>(
            () => ConstraintExpressionFrontend.ParseResolveWithInput("payload.typo > 0", schema));

        Assert.True(
            ex.Message.Contains("latitude_degrees", StringComparison.Ordinal),
            string.Create(
                CultureInfo.InvariantCulture,
                $"a payload nested {nesting} deep lost its leaf field name to the depth bound; a user "
                    + $"misspelling a field at this depth is shown a collapsed type: {ex.Message}"));
    }

    /// <summary>
    /// #687 council round 14 (Balanced BLOCKING 2) — <b>a nested type must spend its budget too.</b>
    /// <para>The struct walk handed each child render the <i>full</i> budget instead of what was left, so the
    /// child rendered as though it owned the whole message and the parent then measured that oversized result
    /// against the space actually remaining — and broke on its <b>first</b> field. One level of nesting was
    /// enough: a wide payload one struct deep spent a small fraction of its budget and showed <b>zero</b>
    /// field names. That is the ordinary shape of a Delta schema (<c>address.geo.latitude</c>), not a hostile one.
    /// </para>
    /// <para>The oracle is the same independent question the listing suite asks, and deliberately not the
    /// implementation's own predicate: reconstruct from the <i>rendered message</i> what showing one more
    /// field would have cost, and require that it would not have fit. Fields are uniform width, so the next
    /// field's cost is known from the ones already shown. Sweeping depth is the point — the defect was
    /// invisible at depth 1, where every prior assertion in this file lives.</para>
    /// </summary>
    [Fact]
    public void NestedPayloads_SpendTheirBudget_BeforeAnyFieldIsElided()
    {
        var counterexamples = new List<string>();

        for (int depth = 1; depth <= 4; depth++)
        {
            for (int width = 1; width <= 60; width++)
            {
                foreach (int nameLength in LegacySweptNameLengths)
                {
                    DataType payload = new StructType(
                    [
                        .. Enumerable.Range(0, width).Select(i => new StructField(
                            string.Create(CultureInfo.InvariantCulture, $"{i:D2}")
                                .PadRight(nameLength, 'f')[..nameLength],
                            IntegerType.Instance,
                            true)),
                    ]);

                    for (int level = 1; level < depth; level++)
                    {
                        payload = new StructType(
                        [
                            new StructField(
                                string.Create(CultureInfo.InvariantCulture, $"lvl{level}"), payload, true),
                        ]);
                    }

                    var schema = new StructType([new StructField("payload", payload, true)]);
                    Exception ex = Assert.ThrowsAny<Exception>(
                        () => ConstraintExpressionFrontend.ParseResolveWithInput(
                            "payload.nosuchfield > 0", schema));

                    if (CouldHaveShownOneMoreField(ex.Message, width, nameLength) is { } failure)
                    {
                        counterexamples.Add(
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"depth={depth} nameLength={nameLength} {failure}"));
                    }
                }
            }
        }

        Assert.True(
            counterexamples.Count == 0,
            string.Create(CultureInfo.InvariantCulture, $"{counterexamples.Count} cells hid fields with "
                + $"budget to spare; first 8:\n") + string.Join("\n", counterexamples.Take(8)));
    }

    /// <summary>
    /// Reconstructs, from the rendered message alone, what showing one more field would have cost, and
    /// reports a failure when that would have fit. Independent of how the renderer decides.
    /// </summary>
    private static string? CouldHaveShownOneMoreField(string message, int width, int nameLength)
    {
        var marker = Regex.Match(message, @" \u2026 \(\+(\d+) more\)");
        int shownNames = Regex.Matches(message, @"\d{2}f+:").Count;

        if (!marker.Success)
        {
            return shownNames >= width
                ? null
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"width={width} rendered {shownNames} of {width} fields with no overflow marker");
        }

        int hidden = int.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture);
        int shown = width - hidden;
        if (shown <= 0)
        {
            // Nothing shown at all: the only honest bound is that a single field could not have fit.
            int lone = nameLength + ":int".Length;
            return message.Length + lone <= AnalysisException.MaxMessageLength
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"width={width} showed NO field names at {message.Length} chars, though one costs "
                        + $"{lone} and the cap is {AnalysisException.MaxMessageLength}")
                : null;
        }

        // Uniform fields, so one more costs a separator plus the width of those already rendered.
        int piece = nameLength + ":int".Length;
        int nextMarker = hidden == 1
            ? 0
            : string.Create(CultureInfo.InvariantCulture, $" \u2026 (+{hidden - 1} more)").Length;
        int projected = message.Length - marker.Length + 1 + piece + nextMarker;

        return projected > AnalysisException.MaxMessageLength
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"width={width}: showed {shown} of {width} fields at {message.Length} chars, but showing "
                    + $"{shown + 1} would have cost {projected}, which fits under "
                    + $"{AnalysisException.MaxMessageLength}");
    }

    /// <summary>
    /// The contract every composite render owes, stated once for all of them: <b>a render given a budget must
    /// fit inside that budget.</b>
    /// <para>Struct, array and map each recurse, and each has to hand its children the space that is actually
    /// LEFT rather than the space it started with. The struct child and the map <em>value</em> both got the
    /// full budget — the map value because it was given <c>budget - 5</c> with no account of what the key had
    /// already consumed. Sweeping struct shapes alone could not see the map, which is why this asserts the
    /// invariant over the composite kinds and their combinations instead of over one shape.</para>
    /// <para>Stated this way the property needs no oracle and no knowledge of how the budget is divided: it is
    /// simply the postcondition of the function's own signature.</para>
    /// </summary>
    [Fact]
    public void EveryCompositeRender_FitsTheBudgetItWasGiven()
    {
        var wide = new StructType(
        [
            .. Enumerable.Range(0, 40).Select(i => new StructField(
                string.Create(CultureInfo.InvariantCulture, $"field_name_number_{i:D3}"),
                StringType.Instance,
                true)),
        ]);

        DataType[] shapes =
        [
            wide,
            new ArrayType(wide, true),
            new MapType(wide, wide, true),
            new MapType(new ArrayType(wide, true), new MapType(wide, wide, true), true),
            new StructType([new StructField("payload", new MapType(wide, wide, true), true)]),
            new ArrayType(new MapType(wide, new ArrayType(wide, true), true), true),
        ];

        var violations = new List<string>();
        foreach (DataType shape in shapes)
        {
            // MORE BUDGET MUST NEVER YIELD A SHORTER RENDER. This is the property the postcondition alone
            // cannot see: the renderer falls back to a bare summary when a composed render overruns, so
            // overspending a child's budget does not surface as an oversized string — it surfaces as the whole
            // render silently collapsing. A map at budget 900 emitted "map<…>", six characters, where the same
            // map at 600 rendered 598. Monotonic length catches that, and needs no model of how the budget is
            // divided between children.
            //
            // Length, specifically, and NOT the number of field names — which was the first draft and is
            // false. A composite legitimately trades one kind of detail for another: at budget 77 a nested
            // map's value collapses to "map<…>" leaving the key room for a field name, and at 78 the value
            // renders real structure and the key gives that name back. Fewer names, more information. An
            // oracle has to assert something true before it can be strict, and the true statement here is
            // about how much the render says, not how many names it happens to contain.
            int longest = 0;
            for (int budget = CoercionHelpers.MinDiagnosticTypeLength; budget <= 1200; budget++)
            {
                int length = CoercionHelpers.DiagnosticType(shape, budget).Length;
                if (length < longest)
                {
                    violations.Add(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"budget={budget} rendered {length} chars for {shape.GetType().Name}, fewer than "
                                + $"the {longest} a smaller budget managed"));
                }

                longest = Math.Max(longest, length);
            }

            // From the method's own documented precondition, not from 1: below MinDiagnosticTypeLength it
            // throws by contract, and asserting a postcondition outside a stated precondition tests nothing.
            for (int budget = CoercionHelpers.MinDiagnosticTypeLength; budget <= 1200; budget++)
            {
                string rendered = CoercionHelpers.DiagnosticType(shape, budget);
                if (rendered.Length > budget)
                {
                    violations.Add(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"budget={budget} produced {rendered.Length} chars for {shape.GetType().Name}"));
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Create(CultureInfo.InvariantCulture, $"{violations.Count} renders overran their budget; "
                + $"first 5:\n") + string.Join("\n", violations.Take(5)));
    }

    /// <summary>
    /// The map's slack reclaim: <b>a small value must not cost the key its detail.</b> Both children are
    /// offered half the budget, and whatever the value declines returns to the key — so the same key renders
    /// with more of its fields visible when paired with a cheap value than with an expensive one.
    /// <para>Pinned because removing the reclaim is otherwise 0 RED while demonstrably changing output over
    /// the map shapes and budgets swept below. A live
    /// branch that no assertion reaches is exactly the class this PR keeps finding, so it is dispositioned by
    /// measurement rather than argued to be equivalent.</para>
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(600)]
    [InlineData(900)]
    [InlineData(1200)]
    public void AMapWithACheapValue_SpendsTheSlackOnItsKey(int budget)
    {
        var wide = new StructType(
        [
            .. Enumerable.Range(0, 40).Select(i => new StructField(
                string.Create(CultureInfo.InvariantCulture, $"field_name_number_{i:D3}"),
                StringType.Instance,
                true)),
        ]);

        // The key's own natural render, measured rather than assumed, so the assertion knows exactly how
        // much detail was available to show.
        int natural = CoercionHelpers.DiagnosticType(wide, 100_000).Length;
        string rendered = CoercionHelpers.DiagnosticType(new MapType(wide, IntegerType.Instance, true), budget);
        int keyNames = Regex.Matches(rendered, "field_name_number_").Count;

        // With a 3-character value, everything except a few characters of syntax belongs to the key. So the
        // key must show as much as that space allows: all 40 fields once the budget can hold its natural
        // render, and otherwise a listing that actually spends what it was given.
        int expected = budget >= natural + "map<,int>".Length ? 40 : keyNames;
        Assert.True(
            keyNames == expected && rendered.Length > budget / 2,
            string.Create(
                CultureInfo.InvariantCulture,
                $"budget={budget}: map<wideStruct,int> rendered {rendered.Length} chars showing {keyNames} "
                    + $"fields, leaving {budget - rendered.Length} unused of {budget} — the key's natural "
                    + $"render is {natural}, so the slack an int value declined was not returned to it"));
    }

    /// <summary>
    /// The top-level field count rendered inside the outermost <c>struct&lt;…&gt;</c>, counted by nesting
    /// depth rather than by the overflow marker.
    /// </summary>
    /// <remarks>
    /// Reading the count off the <c>(+N more)</c> marker looks equivalent and is not: a nested struct emits
    /// a marker of its own, so a regex finds the INNER one and reports a field count that can come out
    /// negative. The first version of this sweep did exactly that and produced a spurious counterexample.
    /// Depth-counting is independent of the renderer and of the marker.
    /// </remarks>
    private static int TopLevelFields(string rendered)
    {
        if (!rendered.StartsWith("struct<", StringComparison.Ordinal))
        {
            return -1;
        }

        int depth = 0;
        int fields = 0;
        for (int i = 6; i < rendered.Length; i++)
        {
            switch (rendered[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case ':' when depth == 1: fields++; break;
                default: break;
            }
        }

        return fields;
    }

    private static StructType ShapedStruct(int width, int innerFields)
    {
        var inner = new StructType(
        [
            .. Enumerable.Range(0, Math.Max(innerFields, 1)).Select(i => new StructField(
                string.Create(CultureInfo.InvariantCulture, $"g{i:D3}xxxxxx"), IntegerType.Instance, true)),
        ]);

        return new StructType(
        [
            .. Enumerable.Range(0, width).Select(i => new StructField(
                string.Create(CultureInfo.InvariantCulture, $"f{i:D3}xxxxxxxx"),
                innerFields > 0 && i % 2 == 0 ? inner : IntegerType.Instance,
                true)),
        ]);
    }

    /// <summary>
    /// #687 council round 15 (Quality BLOCKING) — the struct render over the <b>product</b> of budget,
    /// cardinality and child shape.
    /// <para>The walk that this replaces was forward-greedy, and a forward-greedy walk over a budget that
    /// its own children consume is not merely suboptimal — it is NON-MONOTONE. A child handed more room
    /// renders its own structure instead of a summary and starves the fields after it, so the render showed
    /// strictly LESS at a larger budget: one more character of budget dropped a field name and left the
    /// render shorter than it had been. The identical defect had already been found and fixed in
    /// <c>DiagnosticText.SanitizeToBudget</c>, with the reason written down there; this walk kept the old
    /// shape. Round 14 fixed its RESERVE and left its SHAPE.</para>
    /// <para>The previous guard, <see cref="TypeBudget_IsSpentBeforeAnyFieldIsElided(int)"/>, sweeps
    /// cardinality at ONE budget over a scalar-leaf schema, so it could reach neither the budget axis nor
    /// the compact-versus-expanded alternation that causes this. A sweep over one axis with the others held
    /// fixed cannot find a defect that lives on their product — the sixth time that has been true in this
    /// change, and the reason this asserts four properties over the product rather than one along a line.
    /// </para>
    /// </summary>
    [Fact]
    public void TheStructRender_IsMonotoneAndFullySpent_AcrossBudgetWidthAndChildShape()
    {
        var counterexamples = new List<string>();
        int cells = 0;

        foreach (int width in new[] { 1, 2, 3, 5, 8, 13, 21, 40, 60, 120, 400 })
        {
            foreach (int innerFields in new[] { 0, 1, 3, 8, 20 })
            {
                foreach (bool wrapped in new[] { false, true })
                {
                    StructType shape = ShapedStruct(width, innerFields);
                    DataType subject = wrapped
                        ? new StructType([new StructField("outer", shape, true)])
                        : shape;

                    string previous = string.Empty;
                    int previousFields = -1;

                    for (int budget = CoercionHelpers.MinDiagnosticTypeLength; budget <= 1100; budget++)
                    {
                        string rendered = CoercionHelpers.DiagnosticType(subject, budget);
                        cells++;
                        string where = string.Create(
                            CultureInfo.InvariantCulture,
                            $"width={width} inner={innerFields} wrapped={wrapped} budget={budget}");

                        // 1. Never larger than the budget it was given.
                        if (rendered.Length > budget)
                        {
                            counterexamples.Add(string.Create(
                                CultureInfo.InvariantCulture, $"{where}: rendered {rendered.Length}"));
                        }

                        int fields = TopLevelFields(rendered);

                        // 2. More budget never shows fewer field names. This is the property the defect
                        //    violated, and it is what a reader of the diagnostic is actually looking for:
                        //    the name they got wrong is in that list or it is not.
                        if (previousFields >= 0 && fields < previousFields)
                        {
                            counterexamples.Add(string.Create(
                                CultureInfo.InvariantCulture,
                                $"{where}: {previousFields} fields at {budget - 1}, {fields} at {budget}"));
                        }

                        // 3. At an equal field count, more budget never shows less DETAIL. Stated only at
                        //    equal counts on purpose: round 14 established that a render legitimately
                        //    trades length for breadth, so an unconditional length assertion here would be
                        //    asserting something false.
                        if (previousFields == fields && rendered.Length < previous.Length)
                        {
                            counterexamples.Add(string.Create(
                                CultureInfo.InvariantCulture,
                                $"{where}: {previous.Length} chars at {budget - 1}, {rendered.Length}"));
                        }

                        previous = rendered;
                        previousFields = fields;
                    }
                }
            }
        }

        Assert.True(
            counterexamples.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{counterexamples.Count} of {cells} cells violated a struct-render property; first 5:\n")
            + string.Join("\n", counterexamples.Take(5)));
    }

    /// <summary>
    /// #687 council round 15 — the companion property: a field is elided only when it could not have been
    /// shown. Asks the independent question (could ONE MORE have been shown?) rather than the renderer's
    /// own, for the reason set out on the listing sweep.
    /// <para><b>Necessary and, on its own, not sufficient</b> — see
    /// <see cref="TheFieldCountIsTheLargestFeasibleCount_NotOneShortOfTheFirstInfeasibleOne"/>. "Could one
    /// more have been shown" is a <em>local</em> question, and the feasible counts are not a prefix: the
    /// overflow marker disappears entirely at the last step, so a count can be infeasible while a LARGER
    /// one fits. In that region showing one more genuinely does not fit and this test is correctly silent,
    /// while showing every remaining field does. Round 17 found a real defect sitting precisely there.</para>
    /// </summary>
    [Fact]
    public void NoFieldIsElided_WhileTheBudgetCouldStillHavePaidForIt()
    {
        var counterexamples = new List<string>();

        foreach (int width in new[] { 2, 5, 13, 31, 60, 120, 400 })
        {
            foreach (int innerFields in new[] { 0, 1, 8 })
            {
                for (int budget = 64; budget <= 1024; budget += 7)
                {
                    string rendered =
                        CoercionHelpers.DiagnosticType(ShapedStruct(width, innerFields), budget);
                    Match marker = Regex.Match(rendered, @"\(\+(\d+) more\)");
                    if (!marker.Success)
                    {
                        continue;
                    }

                    int hidden = int.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture);

                    // The cheapest a further field could possibly be: separator, name, colon, and the most
                    // compact form of its type. Reconstructed from the corpus, not from the renderer.
                    int compactChild = innerFields > 0
                        ? string.Create(
                            CultureInfo.InvariantCulture, $"struct<({Math.Max(innerFields, 1)} fields)>").Length
                        : "int".Length;
                    int cheapest = 1 + "f000xxxxxxxx".Length + 1 + compactChild;
                    int markerNow = string.Create(CultureInfo.InvariantCulture, $"\u2026 (+{hidden} more)").Length;
                    int cost = hidden == 1
                        ? cheapest - (1 + markerNow)
                        : cheapest + string.Create(
                            CultureInfo.InvariantCulture, $"\u2026 (+{hidden - 1} more)").Length - markerNow;

                    if (rendered.Length + cost <= budget)
                    {
                        counterexamples.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"width={width} inner={innerFields} budget={budget}: {rendered.Length} chars, "
                            + $"{hidden} hidden, one more costs {cost}"));
                    }
                }
            }
        }

        Assert.True(
            counterexamples.Count == 0,
            string.Join("\n", counterexamples.Take(5)));
    }

    /// <summary>
    /// A struct of <paramref name="fieldCount"/> uniformly shaped fields whose names are exactly
    /// <paramref name="nameLength"/> characters, so that the cheapest possible field is a corpus parameter
    /// rather than a fixed property of the fixture.
    /// </summary>
    /// <remarks>
    /// The fixtures that predate this helper fix their names at whatever width was realistic for the
    /// property they were written for, and none of them can reach the region this file was missing a guard
    /// for. That claim is not made in prose here — two attempts to write it down were wrong, the second
    /// while correcting the first — it is asserted by
    /// <see cref="TheFixturesThatPredateThisPin_CannotReachTheDiscriminatingRegion"/>, which reads the
    /// widths off the generators themselves. Name length is not decoration: together with the child type it
    /// decides whether the feasible field counts form a prefix.
    /// </remarks>
    private static StructType UniformStruct(int fieldCount, int nameLength, string childKind)
    {
        DataType child = childKind switch
        {
            "int" => IntegerType.Instance,
            "nested" => new StructType([new StructField("g0", IntegerType.Instance, true)]),
            _ => new StructType(
            [
                .. Enumerable.Range(0, 8).Select(i => new StructField(
                    string.Create(CultureInfo.InvariantCulture, $"g{i}"), IntegerType.Instance, true)),
            ]),
        };

        return new StructType(
        [
            .. Enumerable.Range(0, fieldCount).Select(i => new StructField(
                Alphabet[i] + new string('x', nameLength - 1), child, true)),
        ]);
    }

    /// <summary>
    /// #687 council round 18 (Architect BLOCKING) — the fixtures that predate the pin above cannot reach
    /// the region it guards, asserted rather than described.
    /// </summary>
    /// <remarks>
    /// <para>Two revisions of that claim shipped as prose and both were wrong, the second while correcting
    /// the first. Each compared the wrong quantity: the discriminating region is decided by a field's
    /// MARGINAL cost — separator, name, colon and the most compact child — and both sentences compared the
    /// NAME width alone, which is smaller. One of them concluded "well above the refund" about a name that
    /// is exactly equal to it and another that is below it, reaching the right verdict for a reason that
    /// does not hold.</para>
    /// <para>A sentence telling a maintainer how to build a discriminating corpus has a silent, delayed
    /// failure mode: follow it, get a corpus that cannot see the defect, and every test passes. So it is
    /// not a sentence any more. This reads the names off the generators, charges each the CHEAPEST child
    /// the renderer can emit, and asserts the result is at or above the refund — the condition under which
    /// the feasible counts form a prefix and the two walk shapes are indistinguishable. If a future fixture
    /// drops below it, this fails and says which one, rather than a comment silently becoming true.</para>
    /// </remarks>
    [Fact]
    public void TheFixturesThatPredateThisPin_CannotReachTheDiscriminatingRegion()
    {
        // The cheapest field the renderer can emit is the one whose child renders shortest, so charging
        // every legacy name that child is the most favourable case for discrimination. If even that clears
        // the refund, no budget can put these fixtures in the region.
        int cheapestChild = CoercionHelpers
            .DiagnosticType(IntegerType.Instance, CoercionHelpers.MinDiagnosticTypeLength).Length;
        int refund = 1 + MarkerWidth(1);

        // Read off the generators rather than restated, which is the whole point: a width written down here
        // is a width that can drift away from the fixture it claims to describe.
        StructType shaped = ShapedStruct(4, 2);
        string[] names =
        [
            FieldName(0),
            .. shaped.Fields.Select(f => f.Name),
            .. shaped.Fields.Select(f => f.DataType).OfType<StructType>()
                .SelectMany(inner => inner.Fields).Select(f => f.Name),
            .. LegacySweptNameLengths.Select(n => new string('f', n)),
        ];

        var reachable = new List<string>();
        foreach (string name in names.Distinct(StringComparer.Ordinal))
        {
            int marginalCost = 1 + name.Length + 1 + cheapestChild;
            if (marginalCost < refund)
            {
                reachable.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{name}' ({name.Length} chars) has marginal cost {marginalCost}, below the refund "
                        + $"{refund}"));
            }
        }

        Assert.True(
            reachable.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{reachable.Count} legacy fixture name(s) now reach the discriminating region, so the pin "
                    + $"above is no longer the only guard that can see it and this remark is stale:\n")
            + string.Join("\n", reachable));

        Assert.True(
            names.Distinct(StringComparer.Ordinal).Count() > 1,
            "the generators produced fewer than two distinct names, so this assertion inspected almost "
                + "nothing — it must enumerate the fixtures, not a sample of them");
    }

    /// <summary>Distinct leading characters, so names stay unique down to a length of one.</summary>
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>The field-name widths swept by <see cref="NoFieldIsElided_WhileTheBudgetCouldStillHavePaidForIt"/>,
    /// hoisted so that
    /// <see cref="TheFixturesThatPredateThisPin_CannotReachTheDiscriminatingRegion"/> can READ them.
    /// A second reviewer falsified the deleted prose by this route rather than by marginal cost — a
    /// predating fixture does not fix ONE width, it sweeps three — so the assertion has to cover the swept
    /// widths as well as the hard-coded ones, and restating them here would reintroduce exactly the copy
    /// this change exists to remove.</summary>
    private static readonly int[] LegacySweptNameLengths = [8, 16, 32];

    /// <summary>The exact width of the <c>… (+N more)</c> marker, spelled out rather than borrowed from
    /// <c>DiagnosticText</c>, so that the oracle below shares no arithmetic with the code it judges.</summary>
    private static int MarkerWidth(int hidden) =>
        string.Create(CultureInfo.InvariantCulture, $"\u2026 (+{hidden} more)").Length;

    /// <summary>
    /// #687 council round 17 (Architect BLOCKING) — the rendered field count is the <b>largest</b> count
    /// that fits, not the one below the first that does not.
    /// <para>Pass 1 of the struct render scans <em>downward</em> for its count. Reverting it to a
    /// forward stop-at-first-failure walk was <b>0 RED across the whole solution</b> while measurably
    /// eliding fields whose full render fits. The failure message below prints the offending cell — its
    /// shape, its budget, the count it showed, the count that fitted and the render itself — so no comment
    /// needs to restate one, and an earlier revision of this sentence restated it wrongly (a four-field
    /// cell described as three) precisely because a restated cell is copied, not computed. Invariant 1,
    /// the bound firing when everything would have fitted, unpinned.</para>
    /// <para>The identical scan in <c>DiagnosticText.SanitizeToBudget</c> <em>is</em> pinned. This is the
    /// seventh time in this change that a fix landed at one of a pair of sites, and the first time the
    /// half that failed to travel was the <b>test</b>. The remedy is not only to port the guard but to
    /// make it say out loud which region it needs to reach, which is the assertion at the end.</para>
    /// <para><b>Why every existing guard was blind.</b> Not a missing axis this time but a fixture VALUE.
    /// The counts that fit form a prefix — and so the two walk shapes agree — whenever taking one more
    /// field costs at least as much as reaching the last one refunds, that refund being the separator plus
    /// the marker which vanishes outright at <c>k == fieldCount</c>. The threshold is <c>refund</c> below,
    /// computed from <c>MarkerWidth</c> at run time and named in the non-vacuity message, deliberately not
    /// written here as a number: the previous revision of this sentence stated one that was too large by
    /// one, which would have led a maintainer picking a corpus by it to choose a NON-discriminating one —
    /// the worst failure mode available to a comment, since it survives review by sounding like guidance.
    /// Every fixture that existed before this test used a single long name width (see the two name helpers
    /// above), well beyond the threshold, so the region where the walks differ did not exist in the corpus
    /// at all. Sixth vacuity of the "corpus cannot see the bound" family, and the one that hid the
    /// longest.</para>
    /// <para><b>The oracle.</b> Each field's minimum cost is reconstructed from the corpus — the name, a
    /// colon, and the most compact form of its type — so the largest feasible count is arithmetic that
    /// borrows nothing from the renderer. The assertion is EQUALITY, not a bound: a count too small is the
    /// defect, and a count too large would mean the model is optimistic and the test itself unsound. It
    /// holds on every one of the cells below.</para>
    /// <para><b>The anti-vacuity half also catches a blinded oracle, not just a drifted corpus.</b>
    /// Anchoring the search to <c>shown</c> and <c>shown + 1</c> AND reverting the scan silences the
    /// counterexample assertion completely — nothing is reported, because an oracle that starts from the
    /// renderer's answer cannot contradict it. The corpus-region assertions still fail, since the anchored
    /// search collapses the discriminating set to empty and the tightness check reads zero where it expects
    /// the dearest discriminating field. So the two halves are not the same claim twice: one says the
    /// renderer is right, the other says this test is still in a position to tell.</para>
    /// <para><b>Why the scan is over every k and not over shown + 1.</b> A reviewer building this same
    /// oracle independently reported clean on its first attempt, because it asked only whether ONE more
    /// field would have fitted. The marker vanishes outright at k == fieldCount, so the smallest count
    /// that beats the rendered one can be two or more above it, and an oracle that steps once from the
    /// renderer's own answer is anchored to that answer. This one never looks at the rendered count until
    /// it has computed the whole feasible set, which is the only reason it is not just the renderer asking
    /// itself for a second opinion.</para>
    /// <para><b>The cardinalities are not decorative either.</b> Six four-character fields at budget 61 is
    /// the witness two seats reported independently: the complete render is exactly 61 characters and the
    /// forward walk shows four of them. A third seat's witness — three five-character fields at budget 37,
    /// whose complete render is exactly the budget and which the forward walk renders as one field — is a
    /// cell of this sweep for the same reason. It is a cell of this sweep rather than a row of its own, because a
    /// reported witness pinned as a literal only ever guards the cell it was reported at.</para>
    /// </summary>
    [Fact]
    public void TheFieldCountIsTheLargestFeasibleCount_NotOneShortOfTheFirstInfeasibleOne()
    {
        var counterexamples = new List<string>();
        int cells = 0;
        int discriminating = 0;

        // What reaching the final field refunds: the separator the marker would have needed, plus the
        // marker for a single hidden field. A field whose marginal cost is strictly below this creates a
        // count that fails while a larger one succeeds, which is the whole region this test exists for.
        //
        // Computed rather than stated, and the reason is sharper than "figures go stale". The number that
        // stood here was CORRECT — at the sibling site. That renderer separates with ", " and this one with
        // a single ',', so one sentence copied across the pair without re-measuring is right in one file and
        // wrong in the other, which is the hardest form to catch by reading: it survives comparison with its
        // twin. Every quantity these two walks derive from their separator is exposed to that copy, so both
        // now compute it. The sentence still travels; the number no longer does.
        int refund = 1 + MarkerWidth(1);
        var misruled = new List<string>();
        int dearestDiscriminating = 0;

        foreach (string childKind in new[] { "int", "nested", "wide" })
        {
            string compact = childKind switch
            {
                "int" => "int",
                "nested" => "struct<(1 fields)>",
                _ => "struct<(8 fields)>",
            };

            foreach (int nameLength in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 12 })
            {
                foreach (int fieldCount in new[] { 2, 3, 4, 5, 6, 7, 9, 11, 30 })
                {
                    StructType subject = UniformStruct(fieldCount, nameLength, childKind);
                    int perField = nameLength + 1 + compact.Length;

                    for (int budget = CoercionHelpers.MinDiagnosticTypeLength; budget <= 400; budget++)
                    {
                        cells++;
                        string rendered = CoercionHelpers.DiagnosticType(subject, budget);

                        // "struct<" + k fields + (k-1) commas + [" … (+h more)"] + ">".
                        int Cost(int k) => 7 + (k * perField) + (k - 1)
                            + (fieldCount == k ? 0 : 1 + MarkerWidth(fieldCount - k)) + 1;

                        int largestFeasible = 0;
                        bool anySmallerInfeasible = false;
                        for (int k = 1; k <= fieldCount; k++)
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
                                anySmallerInfeasible = true;
                            }
                        }

                        // The cell can tell the two walk shapes apart only where the feasible counts are
                        // not a prefix. Counted, and asserted non-zero below.
                        if (anySmallerInfeasible)
                        {
                            discriminating++;
                            dearestDiscriminating = Math.Max(dearestDiscriminating, perField + 1);

                            // And the stated rule for CHOOSING such a corpus is executed here rather than
                            // described above: a cell can only be discriminating if one more field costs
                            // strictly less than the final field refunds. Necessary, not sufficient — the
                            // budget must also land in the band — so this is asserted in the direction that
                            // guides a maintainer, which is the direction the old comment got wrong.
                            if (perField + 1 >= refund)
                            {
                                misruled.Add(string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"child={childKind} nameLength={nameLength} fields={fieldCount} "
                                        + $"budget={budget}: discriminating although a field's marginal "
                                        + $"cost {perField + 1} is not below the refund {refund}"));
                            }
                        }

                        int shown = TopLevelFields(rendered);
                        if (shown != largestFeasible || rendered.Length > budget)
                        {
                            counterexamples.Add(string.Create(
                                CultureInfo.InvariantCulture,
                                $"child={childKind} nameLength={nameLength} fields={fieldCount} "
                                    + $"budget={budget}: showed {shown} of a feasible {largestFeasible} "
                                    + $"in {rendered.Length} chars — {rendered}"));
                        }
                    }
                }
            }
        }

        Assert.True(
            counterexamples.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{counterexamples.Count} of {cells} cells rendered fewer fields than fit; first 5:\n")
            + string.Join("\n", counterexamples.Take(5)));

        Assert.True(
            misruled.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{misruled.Count} of {cells} cells discriminate outside the rule this test states for "
                    + $"picking a corpus, so the rule is wrong and following it would produce a corpus "
                    + $"that cannot see the defect; first 5:\n")
            + string.Join("\n", misruled.Take(5)));

        // Tightness, which the necessary direction alone does not give. A refund stated too LARGE still
        // satisfies every check above — it merely admits shapes that never discriminate — and too large by
        // one is exactly what the previous revision of this comment said. Equality pins it from both
        // sides: the dearest field the corpus ever discriminates on is the last one below the refund.
        Assert.Equal(refund - 1, dearestDiscriminating);

        // The anti-vacuity half, and the part that would have prevented this round. A corpus can drift out
        // of the region a test was written for without any assertion noticing, because passing looks
        // identical either way. This one fails loudly instead.
        Assert.True(
            discriminating > 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"none of {cells} cells has a feasible count above an infeasible one, so this corpus can "
                    + $"no longer distinguish a downward scan from a forward walk — shorten the names "
                    + $"until a field's marginal cost (name + 1 + child + separator) falls strictly below "
                    + $"{refund}, or lower the budgets until it can"));
    }
}
