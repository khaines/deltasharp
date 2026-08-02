using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Council round-1 completion regressions for the schema/column-mapping message surfaces (#683/#685).
/// Each test drives exactly ONE guard so a single reverted <c>Sanitize</c> call turns exactly one test red.
/// </summary>
public sealed class ColumnMappingMessageHygieneTests
{
    // U+2028 (LINE SEPARATOR) is the crux of the ColumnMapping finding: it is Unicode category Zl, NOT Cc,
    // so `char.IsControl` — the only character class FindUnsafePathSegmentReason rejects — lets it through.
    // Many log/JSON consumers still treat it as a line terminator, which is exactly what
    // DiagnosticText.IsInjectionUnsafe exists to close. A path-safety guard is not a log-injection guard.
    private const string LineSeparatorPoison = "col-a\u2028[CRITICAL] forged\u2029entry";

    private static StructField Mapped(string logicalName, string physicalName, long id) =>
        new(
            logicalName,
            DataTypes.LongType,
            nullable: true,
            FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>(
                    ColumnMapping.PhysicalNameKey, MetadataValue.String(physicalName)),
                // MUST be a LONG metadata value: TryGetLong is what the validator reads, so a string-typed id
                // would leave the field "id-less" and divert this test to the missing-id guard.
                new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(id)),
            }));

    [Fact]
    public void PhysicalName_ContainingLineSeparator_IsRejectedFailClosed_WithThePoisonNeutralized()
    {
        // #683 (council item 5): the duplicate-physical-name message echoed `physical` on the assumption that
        // EnsureSafePhysicalName had already neutralized it — but that guard rejected only Cc, not Zl/Zp, so a
        // U+2028 physical name passed the safety check and reached the duplicate message verbatim. The #683
        // path-segment fix closes the gap at its ROOT: FindUnsafePathSegmentReason now rejects Zl/Zp (and Cf/Cs
        // — a physical name is a real filesystem directory segment), so a U+2028 physical name is rejected as an
        // unsafe path segment BEFORE the duplicate check. That is strictly stronger than sanitizing it at the
        // later message; the unsafe-segment message still sanitizes the echoed name, so no U+2028 leaks either.
        var schema = new StructType(new[]
        {
            Mapped("a", LineSeparatorPoison, 1),
            Mapped("b", LineSeparatorPoison, 2),
        });
        ImmutableSortedDictionary<string, string> config = ColumnMapping.NameModeConfiguration(2);

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(ColumnMappingMode.Name, schema, config));

        Assert.Contains("not a safe path segment", ex.Message, StringComparison.Ordinal); // caught at the ROOT guard now
        Assert.DoesNotContain('\u2028', ex.Message);
        Assert.DoesNotContain('\u2029', ex.Message);
        Assert.DoesNotContain(LineSeparatorPoison, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalName_WithValidAstralCharacter_IsAccepted_ButLoneSurrogateIsRejected()
    {
        // Regression (Architect/Storage/Security seats, R2): the #683 path-segment fix rejected
        // UnicodeCategory.Surrogate in a per-UTF-16-CODE-UNIT loop, which flags BOTH halves of a well-formed
        // surrogate PAIR — so it rejected every legal astral code point (emoji, CJK Ext-B, math alphanumerics),
        // making a spec-valid Delta/Parquet table with such a column name unreadable at LOAD and uncommittable.
        // FindUnsafePathSegmentReason is now pair-aware, matching DiagnosticText.Sanitize: a valid pair is
        // accepted, only a LONE (unpaired) surrogate is rejected. Without this positive pin the fix reverts
        // silently (nothing else in the suite accepts an astral name).
        ImmutableSortedDictionary<string, string> config = ColumnMapping.NameModeConfiguration(1);

        // U+1F4C8 CHART WITH UPWARDS TREND (emoji) + U+20BB7 (CJK Extension B) — two valid astral pairs.
        var validAstral = new StructType(new[] { Mapped("logical", "sales_\U0001F4C8_\U00020BB7", 1) });
        ColumnMapping.ValidateColumnMappingSchema(ColumnMappingMode.Name, validAstral, config); // must NOT throw

        // U+20BB7 (CJK Ext-B) + U+E0100 VARIATION SELECTOR-17 — an Ideographic Variation Sequence, category Mn,
        // ASTRAL but NOT Cf. Real Japanese proper-noun column names use IVS. This pins the ACCEPT side of the
        // boundary (Connectors seat R4): the Cf reject must NOT widen to NonSpacingMark or these tables break
        // silently with no test going red.
        var validIvs = new StructType(new[] { Mapped("logical", "name_\U00020BB7\U000E0100", 1) });
        ColumnMapping.ValidateColumnMappingSchema(ColumnMappingMode.Name, validIvs, config); // must NOT throw

        // A lone high surrogate (U+D83D not followed by a low surrogate) is malformed UTF-16 → still rejected.
        var loneSurrogate = new StructType(new[] { Mapped("logical", "sales_\uD83D_bad", 1) });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(ColumnMappingMode.Name, loneSurrogate, config));
        Assert.Contains("not a safe path segment", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unpaired surrogate", ex.Message, StringComparison.Ordinal);

        // The other three malformed-UTF-16 shapes must ALSO reject — these pin the loop's `i + 1 < Length` bound
        // and its `IsLowSurrogate(next)` check, so a future mutation that drops either (Storage seat R3 residual)
        // cannot pass silently. lone LOW: no preceding high; trailing HIGH: the i+1 bound; HIGH+HIGH: next is
        // not a low surrogate.
        foreach (string malformed in new[] { "col_\uDC08_x" /* lone LOW */, "col_end\uD83D" /* trailing HIGH */,
            "col_\uD83D\uD83D_x" /* HIGH+HIGH */ })
        {
            var s = new StructType(new[] { Mapped("logical", malformed, 1) });
            Assert.Throws<DeltaProtocolException>(
                () => ColumnMapping.ValidateColumnMappingSchema(ColumnMappingMode.Name, s, config));
        }
    }

    [Fact]
    public void PhysicalName_WithAstralFormatControl_IsRejected()
    {
        // Regression (Balanced/Security seats, R3 re-poll): the surrogate-pair fast path consumed a valid pair
        // WITHOUT category-checking the combined code point, so ASTRAL Format controls (Cf) slipped through —
        // U+E0001 LANGUAGE TAG and the U+E0020–E007F TAG block are the invisible mirror of ASCII 0x20–0x7F
        // (the canonical ASCII-smuggling vector): two physical names that are ordinally DISTINCT (so the
        // uniqueness set admits both) yet render IDENTICALLY in every terminal/log/console. In a foreign
        // metaData this becomes a real partition-directory segment + Parquet column name. A path segment is
        // NOT a display string; an astral Cf is now rejected exactly as a BMP Cf (U+202E) already is.
        ImmutableSortedDictionary<string, string> config = ColumnMapping.NameModeConfiguration(1);

        // U+E0020 TAG SPACE (astral Cf, TAG block) embedded in the physical name -> REJECTED.
        var tagSmuggled = new StructType(new[] { Mapped("logical", "col1\U000E0020", 1) });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(ColumnMappingMode.Name, tagSmuggled, config));
        Assert.Contains("not a safe path segment", ex.Message, StringComparison.Ordinal);
        Assert.Contains("format control", ex.Message, StringComparison.Ordinal);

        // U+E0001 LANGUAGE TAG (astral Cf) is likewise rejected.
        var languageTag = new StructType(new[] { Mapped("logical", "col\U000E0001x", 1) });
        Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(ColumnMappingMode.Name, languageTag, config));
    }

    [Fact]
    public void DuplicatePhysicalName_AtTheMaximumSafeLength_StaysBounded()
    {
        // Scope note, stated accurately: this echo is NOT an unbounded-length vector. EnsureSafePhysicalName
        // runs FIRST and caps a physical name at 128 UTF-8 bytes, so a 5,000-char name is rejected as unsafe
        // and never reaches the duplicate-name message at all. The genuine defect here was INJECTION
        // (U+2028/U+2029 pass that guard), which the test above pins. This test pins the ordering the bound
        // depends on: at the largest length the guard admits, the message is still short.
        string longestAdmissible = new('p', 120);
        var schema = new StructType(new[]
        {
            Mapped("a", longestAdmissible, 1),
            Mapped("b", longestAdmissible, 2),
        });

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(2)));

        Assert.Contains("more than one column", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 600, $"message not bounded: {ex.Message.Length} chars");
    }

    [Fact]
    public void NoneModePartitionName_UsesTheSchemaIdentifierCap_NotTheConfigTokenCap()
    {
        // Cap-class drift (council item 10): a PARTITION COLUMN NAME is a schema identifier, so it must use
        // the same DefaultMaxLength every other partition-column echo in ColumnMapping uses. It previously
        // used the tighter config-token cap here alone, which is reserved for short protocol VALUES.
        // A name between the two caps must therefore survive un-elided.
        string between = new('q', DiagnosticText.ConfigTokenMaxLength + 20);
        Assert.True(between.Length < DiagnosticText.DefaultMaxLength);

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.EnsureNoneModePartitionNamesSafe(
                ImmutableArray.Create(between + "/unsafe")));

        Assert.Contains("is not a safe path segment", ex.Message, StringComparison.Ordinal);
        Assert.Contains(between, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoneModePartitionName_PoisonedAndOversized_IsStillNeutralizedAndBounded()
    {
        // Widening the cap must not weaken the neutralization or leave the echo unbounded.
        string huge = "seg\r\n\u2028" + new string('r', 5_000);

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.EnsureNoneModePartitionNamesSafe(ImmutableArray.Create(huge)));

        Assert.Contains("is not a safe path segment", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, ex.Message.Count(c => c == '\n'));
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\u2028', ex.Message);
        Assert.True(ex.Message.Length < 600, $"message not bounded: {ex.Message.Length} chars");
    }
}
