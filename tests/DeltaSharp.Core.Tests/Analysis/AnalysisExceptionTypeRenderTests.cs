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
    /// names that share a 32-character prefix. At <c>2d686a7</c> the per-item cap of 32 collapsed the two
    /// the user actually meant into the identical render, in a 190-character message nowhere near the
    /// backstop. Nothing needed bounding here at all.
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
    /// nested payload struct at <b>13 fields</b> — 359 characters rendered against a 1024-character message,
    /// two thirds of the budget unspent — while claiming in its own doc to show such a struct intact. It was
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
    /// candidate names — while every field in this file's fixture is 24 characters, so no row could reach it.
    /// Restoring the 32 was measured at <b>0 RED across the whole suite</b>. That is the vacuity pattern this
    /// PR has now hit five times: a corpus whose items are all shorter than the bound cannot test the bound.
    /// </para>
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

}
