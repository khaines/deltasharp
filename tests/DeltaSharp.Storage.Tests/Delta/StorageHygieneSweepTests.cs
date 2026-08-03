using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// A TABLE-DRIVEN message-hygiene sweep: a poison corpus × the set of reachable diagnostic "doors" across the
/// Storage surfaces, asserting on every combination that the resulting message is fully neutralized and
/// length-bounded.
/// <para><b>Why a sweep rather than N bespoke tests.</b> The council's round-1 finding was that the sanitize
/// sweep was file-partial, and the mechanical reason it went unnoticed was that ~35 individual
/// <see cref="DiagnosticText.Sanitize"/> calls were mutation-VACUOUS: a global regex drop of
/// <c>Sanitize(x) -&gt; x</c> turned only 2 tests red. Isolated tests pin only the sites someone remembered to
/// write a test for; this matrix pins a whole DOOR, so a future echo added inside an already-covered guard is
/// caught without anyone writing a new test. That is the property that would have prevented the round-1
/// misses.</para>
/// <para>Each door drives ONE guard and is named after it, so a failure names the site directly, and reverting
/// a single <c>Sanitize</c> call turns a small, identifiable set of theory cases red.</para>
/// <para>Sites deliberately NOT in the matrix are the ones that are unreachable with a crafted token (the
/// guard only fires on an exact match against a bounded literal, or the arm is structurally dead). Those are
/// sanitized for uniformity/drift-prevention and are listed in the PR body rather than given a fake test.
/// </para>
/// </summary>
[Collection(DeltaSharp.Storage.Tests.BackendFaultInjectionCollection.Name)]
public sealed class StorageHygieneSweepTests
{
    // The poison corpus. Every entry is a distinct INJECTION CLASS, not a cosmetic variation:
    //   crlf      — the classic structured-log line forgery.
    //   lineSep   — U+2028, Unicode category Zl. NOT char.IsControl, so a path-safety or identifier guard
    //               that only rejects Cc lets it through; many JSON/JS log consumers still break the line.
    //   paraSep   — U+2029 (Zp), same class.
    //   ansiEsc   — U+001B, a terminal escape reaching a console-backed log viewer.
    //   nel       — U+0085, a C1 control that several sinks treat as a line terminator.
    //   nulTab    — U+0000 truncates C-string sinks; TAB forges a column in TSV-shaped output.
    //   bidi      — U+202E RIGHT-TO-LEFT OVERRIDE, Unicode category Cf. NOT char.IsControl, so a guard that
    //               rejects only Cc/Zl/Zp lets it through; it reorders a log line's or a directory name's
    //               rendered glyphs (spoofing). The message sanitizer (Cf) and the path-segment validator
    //               (Cf) both reject it.
    //   loneSurr  — an unpaired UTF-16 surrogate (U+D800), malformed and non-encodable to a filesystem name;
    //               also makes the '\uD800' absence assertion non-vacuous.
    //   oversized — no control character at all: purely the LENGTH cap, i.e. log flooding.
    //
    // THE CORPUS IS SPELLED ONCE. Every axis in this file is this array or a NAMED SUBTRACTION from it, so a
    // tenth class added here reaches every door. The change-feed axis used to re-spell the payload names as a
    // second literal list, which meant a payload added to `Poisons` never reached the CDF door and nothing
    // said so — the corpus-axis defect this file's sibling suite already has a rule about, in this file.
    private static readonly string[] PoisonNames =
        ["crlf", "lineSep", "paraSep", "ansiEsc", "nel", "nulTab", "bidi", "loneSurr", "oversized"];

    public static TheoryData<string> Poisons
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in PoisonNames)
            {
                data.Add(name);
            }

            return data;
        }
    }

    private static string Payload(string name) => name switch
    {
        "crlf" => "p\r\n[CRITICAL] forged log line",
        "lineSep" => "p\u2028[CRITICAL] forged log line",
        "paraSep" => "p\u2029[CRITICAL] forged log line",
        "ansiEsc" => "p\u001b[31m[CRITICAL] forged log line",
        "nel" => "p\u0085[CRITICAL] forged log line",
        "nulTab" => "p\0forged\tcolumn",
        "bidi" => "p\u202e[CRITICAL] forged log line",
        "loneSurr" => "p\ud800[CRITICAL] forged log line",
        "oversized" => new string('p', 20_000),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    // DERIVED, not guessed. The widest legitimate message renders TWO bounded identifier LISTS (e.g. the
    // Parquet writer's batch-vs-writer schema mismatch): each is MaxEchoedListItems (16) names, each capped at
    // DefaultMaxLength (128) plus an ellipsis and a ", " separator, so ~16 * 131 = ~2,100 characters per list.
    // Two lists plus prose plus an elision marker lands near 4,500; 6,000 leaves head-room for wording changes
    // without ever admitting an unbounded render. For scale: the two StructType.SimpleString echoes this
    // round replaced produced 129,008 and 128,985 characters, so a regression is caught by 20x, not by a
    // hair.
    private const int MessageCeiling = 6_000;

    private static void AssertNeutralizedAndBounded(string message, string payload)
    {
        Assert.DoesNotContain(payload, message, StringComparison.Ordinal);
        foreach (char c in new[] { '\r', '\n', '\0', '\t', '\u001b', '\u0085', '\u2028', '\u2029', '\u202e', '\uD800' })
        {
            Assert.DoesNotContain(c, message);
        }

        Assert.True(
            message.Length <= MessageCeiling,
            string.Create(CultureInfo.InvariantCulture, $"message was {message.Length} chars (ceiling {MessageCeiling})"));
    }

    private static StructType Flat(string name) =>
        new([new StructField(name, DataTypes.LongType, nullable: true)]);

    private static StructField Mapped(string logicalName, string physicalName, long id) =>
        new(
            logicalName,
            DataTypes.LongType,
            nullable: true,
            FieldMetadata.FromValues(
            [
                new KeyValuePair<string, MetadataValue>(
                    ColumnMapping.PhysicalNameKey, MetadataValue.String(physicalName)),
                new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(id)),
            ]));

    // ---------------------------------------------------------------------------------------------------
    // Column mapping
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_WriteColumnNotInMappedSchema(string poison)
    {
        string p = Payload(poison);

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(() => ColumnMapping.MapWriteSchemaToPhysical(
            Flat(p), new StructType([Mapped("other", "col-1", 1)]), ColumnMappingMode.Name));

        Assert.Contains("is not present in the", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_NestedWriteColumnRejected(string poison)
    {
        string p = Payload(poison);
        var nested = new StructType(
            [new StructField(p, new StructType([new StructField("x", DataTypes.LongType, nullable: true)]), nullable: true)]);

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(() => ColumnMapping.MapWriteSchemaToPhysical(
            nested, new StructType([Mapped("other", "col-1", 1)]), ColumnMappingMode.Name));

        Assert.Contains("nested", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_PhysicalPartitionColumnNotInSchema(string poison)
    {
        string p = Payload(poison);

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(() => ColumnMapping.PhysicalPartitionColumns(
            new StructType([Mapped("other", "col-1", 1)]), [p], ColumnMappingMode.Name));

        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_PartitionColumnUnsupportedType(string poison)
    {
        string p = Payload(poison);
        var schema = new StructType([new StructField(p, DataTypes.BinaryType, nullable: true)]);

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(
            () => ColumnMapping.EnsurePartitionColumnsInSchema(schema, [p]));

        Assert.Contains("not a supported Delta partition-column type", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_DuplicatePartitionColumn(string poison)
    {
        string p = Payload(poison);

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(
            () => ColumnMapping.EnsurePartitionColumnsInSchema(Flat(p), [p, p]));

        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_CaseInsensitiveDuplicateColumn(string poison)
    {
        // Both echoed names are poisoned, so a revert of EITHER Sanitize on that line turns this red.
        string p = Payload(poison) + "A";
        string q = Payload(poison) + "a";

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(() => ColumnMapping.EnsureNoCaseInsensitiveDuplicateColumns(
            new StructType(
            [
                new StructField(p, DataTypes.LongType, nullable: true),
                new StructField(q, DataTypes.LongType, nullable: true),
            ])));

        Assert.Contains("collides case-insensitively", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, Payload(poison));
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_IdModeMissingId(string poison)
    {
        string p = Payload(poison);
        var field = new StructField(
            p,
            DataTypes.LongType,
            nullable: true,
            FieldMetadata.FromValues(
            [
                new KeyValuePair<string, MetadataValue>(
                    ColumnMapping.PhysicalNameKey, MetadataValue.String("col-1")),
            ]));

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(
            () => ColumnMapping.ToPhysicalSchema(new StructType([field]), ColumnMappingMode.Id));

        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_UnrecognizedMode(string poison)
    {
        // The mode VALUE is a configuration token read straight out of the log's metadata; nothing upstream
        // constrains it (it is rejected precisely BECAUSE it is unrecognized), so it reaches the echo raw.
        string p = Payload(poison);

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(
            () => ColumnMapping.ResolveMode(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ColumnMapping.ModeKey] = p,
            }));

        Assert.Contains("Unrecognized", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_NameModeMissingPhysicalName(string poison)
    {
        // Name mode with an id but NO physicalName: a different arm from Door_ColumnMapping_IdModeMissingId
        // (which is the missing-ID arm), with its own echo of the LOGICAL name.
        string p = Payload(poison);
        var field = new StructField(
            p,
            DataTypes.LongType,
            nullable: true,
            FieldMetadata.FromValues(
            [
                new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(1)),
            ]));

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(
            () => ColumnMapping.ToPhysicalSchema(new StructType([field]), ColumnMappingMode.Name));

        Assert.Contains(ColumnMapping.PhysicalNameKey, ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_EvolveRetainedColumnWithoutId(string poison)
    {
        // Schema EVOLUTION over a mapped table whose retained column carries a physicalName but no id: its own
        // echo of the LOGICAL name, on a path no other door enters.
        string p = Payload(poison);
        var current = new StructType(
        [
            new StructField(
                p,
                DataTypes.LongType,
                nullable: true,
                FieldMetadata.FromValues(
                [
                    new KeyValuePair<string, MetadataValue>(
                        ColumnMapping.PhysicalNameKey, MetadataValue.String("col-1")),
                ])),
        ]);

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(
            () => ColumnMapping.EvolveNameModeMapping(
                current, current, ColumnMapping.NameModeConfiguration(1), new SeededPhysicalNameSource("unused")));

        Assert.Contains(ColumnMapping.IdKey, ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_NoneModeUnsafePartitionName(string poison)
    {
        string p = Payload(poison) + "/x"; // '/' guarantees the path-segment guard fires for every payload

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(
            () => ColumnMapping.EnsureNoneModePartitionNamesSafe([p]));

        AssertNeutralizedAndBounded(ex.Message, Payload(poison));
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_ValidateSchema_DuplicatePhysicalName(string poison)
    {
        string p = Payload(poison);

        // BOTH echoed tokens are poisoned. This door previously poisoned the LOGICAL names and asserted the
        // message "echoes both" — it does not: the duplicate-name line renders only
        // SanitizeEchoedToken(physical), and the test supplied "col-dup", a clean literal of its own
        // choosing. Under a `Sanitize -> identity` mutation 21 of the 24 doors went red and this one stayed
        // green at 7/7: a vacuous door. The revision after that poisoned the PHYSICAL name and left the
        // LOGICAL one a clean literal ("a"/"b") — which moved the vacuity rather than removing it, because
        // the message that actually fires (ColumnMapping:257) echoes BOTH the logical name and the physical
        // one, and only one of them could turn this red. Both carry the payload now, so reverting either
        // sanitizer on that line reddens this door.
        //
        // The message that fires is decided by EnsureSafePhysicalName. The #683 path-segment fix
        // (FindUnsafePathSegmentReason now rejects Zl/Zp/Cf/Cs, not only Cc) closes the gap this door's
        // earlier revision documented and relied on: EVERY poison class — CR/LF/NUL/ESC/NEL (Cc), U+2028/2029
        // (Zl/Zp), U+202E (Cf), a lone surrogate (Cs), and oversized (byte cap) — is now stopped at the
        // UNSAFE-SEGMENT guard, whose message echoes `Sanitize(logicalName)` AND
        // `SanitizeEchoedToken(physical)` (ColumnMapping:257), so every payload exercises two real, sanitized
        // echoes.
        //
        // AND THE DUPLICATE-PHYSICAL-NAME ECHO (ColumnMapping:408) IS THEREFORE UNREACHABLE WITH A POISONED
        // TOKEN — that is what the DoesNotContain below disproves by EXECUTION rather than by reading: for
        // every payload class the upstream segment guard fires first, so a name reaching :408 has already
        // been proved a safe path segment (no Cc/Zl/Zp/Cf/Cs, <= 128 UTF-8 bytes). It is defense-in-depth,
        // is listed as such in this file's unreachable set, and its sanitizer is pinned by the shared
        // DiagnosticText.Sanitize unit tests.
        var schema = new StructType([Mapped(p + "-a", p, 1), Mapped(p + "-b", p, 2)]);

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(() => ColumnMapping.ValidateColumnMappingSchema(
            ColumnMappingMode.Name,
            schema,
            new Dictionary<string, string> { [ColumnMapping.MaxColumnIdKey] = "2" }));

        Assert.Contains("not a safe path segment", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("assigned to more than one column", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    // The three ValidateColumnMappingSchema arms BELOW the safe-segment guard — missing id, id out of range,
    // id above maxColumnId — each echo the LOGICAL name (`field.Name`), and no door reached any of them: the
    // door above stops at the segment guard by construction. They are reachable exactly as written here: the
    // PHYSICAL name is a clean, safe segment (so the upstream guard passes) and the LOGICAL name carries the
    // payload, which is the shape a foreign metaData can trivially declare.
    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_ValidateSchema_MissingId(string poison)
    {
        string p = Payload(poison);
        var field = new StructField(
            p,
            DataTypes.LongType,
            nullable: true,
            FieldMetadata.FromValues(
            [
                new KeyValuePair<string, MetadataValue>(
                    ColumnMapping.PhysicalNameKey, MetadataValue.String("col-1")),
            ]));

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(() => ColumnMapping.ValidateColumnMappingSchema(
            ColumnMappingMode.Name,
            new StructType([field]),
            new Dictionary<string, string> { [ColumnMapping.MaxColumnIdKey] = "1" }));

        Assert.Contains("has no '" + ColumnMapping.IdKey + "'", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_ValidateSchema_IdOutOfRange(string poison)
    {
        string p = Payload(poison);

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(() => ColumnMapping.ValidateColumnMappingSchema(
            ColumnMappingMode.Name,
            new StructType([Mapped(p, "col-1", 0)]),
            new Dictionary<string, string> { [ColumnMapping.MaxColumnIdKey] = "1" }));

        Assert.Contains("outside the valid column-mapping", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_ValidateSchema_IdAboveMaxColumnId(string poison)
    {
        string p = Payload(poison);

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(() => ColumnMapping.ValidateColumnMappingSchema(
            ColumnMappingMode.Name,
            new StructType([Mapped(p, "col-1", 7)]),
            new Dictionary<string, string> { [ColumnMapping.MaxColumnIdKey] = "1" }));

        Assert.Contains("exceeds the tracked", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    // ---------------------------------------------------------------------------------------------------
    // Parquet type mapping
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ParquetTypeMapping_UnsupportedNestedRead(string poison)
    {
        string p = Payload(poison);
        // A nested type whose SimpleString would recursively embed every field name verbatim: the guard must
        // echo the bounded KIND plus the sanitized column label, never SimpleString.
        var nested = new MapType(DataTypes.StringType, new StructType(
            [new StructField(p, DataTypes.LongType, nullable: true)]), valueContainsNull: true);

        DeltaStorageException ex = Assert.ThrowsAny<DeltaStorageException>(
            () => ParquetTypeMapping.EnsureReadSupported(new StructField(p, nested, nullable: true)));

        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ParquetTypeMapping_CreateFieldUnsupported(string poison)
    {
        string p = Payload(poison);
        var nested = new MapType(DataTypes.StringType, new StructType(
            [new StructField(p, DataTypes.LongType, nullable: true)]), valueContainsNull: true);

        DeltaStorageException ex = Assert.ThrowsAny<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(new StructField(p, nested, nullable: true)));

        AssertNeutralizedAndBounded(ex.Message, p);
    }

    // The remaining ParquetTypeMapping arms. Every one of them echoes the column name (and two of them a
    // second token), and none was reached by a door: the two doors above enter through ONE shape
    // (map-of-struct), which decides which arm fires and leaves the other seven rendering clean literals
    // under any revert.
    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ParquetTypeMapping_UnsupportedScalarWrite(string poison)
    {
        // NullType is the one DeltaSharp scalar with no Parquet physical representation, so it reaches the
        // `_` arm that renders BOTH the column name and the type's SimpleString.
        string p = Payload(poison);

        DeltaStorageException ex = Assert.ThrowsAny<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(new StructField(p, DataTypes.NullType, nullable: true)));

        Assert.Contains("is not supported", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ParquetTypeMapping_ColumnMappingIdOutsideFieldIdRange(string poison)
    {
        string p = Payload(poison);

        DeltaStorageException ex = Assert.ThrowsAny<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(Mapped(p, "col-1", 0)));

        Assert.Contains("outside the Parquet", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ParquetTypeMapping_ArrayElementUnsupported(string poison)
    {
        string p = Payload(poison);
        var arrayOfNested = new ArrayType(
            new MapType(DataTypes.StringType, DataTypes.StringType, valueContainsNull: true), containsNull: true);

        DeltaStorageException ex = Assert.ThrowsAny<DeltaStorageException>(
            () => ParquetTypeMapping.EnsureReadSupported(new StructField(p, arrayOfNested, nullable: true)));

        Assert.Contains("array column", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ParquetTypeMapping_MapKeyUnsupported(string poison)
    {
        string p = Payload(poison);
        var mapWithNestedKey = new MapType(
            new ArrayType(DataTypes.StringType, containsNull: true), DataTypes.StringType, valueContainsNull: true);

        DeltaStorageException ex = Assert.ThrowsAny<DeltaStorageException>(
            () => ParquetTypeMapping.EnsureReadSupported(new StructField(p, mapWithNestedKey, nullable: true)));

        Assert.Contains("map column", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ParquetTypeMapping_ZeroFieldStruct(string poison)
    {
        string p = Payload(poison);

        DeltaStorageException ex = Assert.ThrowsAny<DeltaStorageException>(
            () => ParquetTypeMapping.EnsureReadSupported(new StructField(p, new StructType([]), nullable: true)));

        Assert.Contains("zero-field struct", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ParquetTypeMapping_StructFieldUnsupported(string poison)
    {
        // BOTH echoed tokens carry the payload — the column name and the nested FIELD name — so a revert of
        // either sanitizer on that line reddens this door.
        string p = Payload(poison);
        var structOfNested = new StructType(
        [
            new StructField(
                p + "-field",
                new MapType(DataTypes.StringType, DataTypes.StringType, valueContainsNull: true),
                nullable: true),
        ]);

        DeltaStorageException ex = Assert.ThrowsAny<DeltaStorageException>(
            () => ParquetTypeMapping.EnsureReadSupported(new StructField(p + "-col", structOfNested, nullable: true)));

        Assert.Contains("struct column", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ParquetTypeMapping_DecimalPrecisionUnsupported(string poison)
    {
        string p = Payload(poison);

        DeltaStorageException ex = Assert.ThrowsAny<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(
                new StructField(p, DataTypes.CreateDecimalType(38, 2), nullable: true)));

        Assert.Contains("decimal precision", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMappingProjection_NestedColumnRejected(string poison)
    {
        string p = Payload(poison);
        var schema = new StructType(
            [new StructField(p, new StructType([new StructField("x", DataTypes.LongType, nullable: true)]), nullable: true)]);

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(
            () => ColumnMappingProjection.ResolvePhysicalNames(schema, ColumnMappingMode.Name));

        Assert.Contains("nested", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    // ---------------------------------------------------------------------------------------------------
    // Parquet write path
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Poisons))]
    public async Task Door_ParquetFileWriter_NonNullableNull(string poison)
    {
        // The writer has THREE independent "non-nullable column holds a null" guards on three separate value
        // lanes — the generic <T> lane, the decimal lane, and the string/binary lane's EnsureNullable — each
        // with its own echo of the (physical) column name. A single LongType column enters only the first,
        // so the door drives one column per lane.
        string p = Payload(poison);
        foreach (DataType type in (DataType[])
            [DataTypes.LongType, DataTypes.CreateDecimalType(10, 2), DataTypes.StringType, DataTypes.BinaryType])
        {
            var schema = new StructType([new StructField(p, type, nullable: false)]);

            MutableColumnVector vector = ColumnVectors.Create(type, 1);
            vector.AppendNull();
            var batch = new ManagedColumnBatch(schema, [vector], 1);

            using var output = new MemoryStream();
            DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
                () => new ParquetFileWriter().WriteAsync(output, schema, [batch], CancellationToken.None));

            Assert.Contains("holds a null at row", ex.Message, StringComparison.Ordinal);
            AssertNeutralizedAndBounded(ex.Message, p);
        }
    }

    // A wide, poisoned struct: SimpleString recurses through every one of these field names, so any site that
    // echoes it for a NESTED type is both an unbounded aggregate and a raw-name echo.
    private static StructType PoisonedStruct(string payload) =>
        new(Enumerable.Range(0, 3_000)
            .Select(i => new StructField(
                string.Create(CultureInfo.InvariantCulture, $"{payload}_{i}"), DataTypes.StringType, nullable: true))
            .ToArray());

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnMapping_NonAtomicPartitionColumnType(string poison)
    {
        // Round-5 (Architect): the guard fires ONLY when the type is non-atomic, so field.DataType.SimpleString
        // always recursed. Measured 74,052 chars with raw U+2028 -- one line below a Sanitize(column) this
        // sweep had already added.
        string p = Payload(poison);
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.EnsurePartitionColumnsInSchema(
                new StructType([new StructField(p, PoisonedStruct(p), nullable: true)]), [p]));

        Assert.Contains("supported Delta partition-column type", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);

        // The KIND is the whole diagnosis and it survives.
        Assert.Contains("has type 'struct'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_DeltaReadEncoding_NonAtomicPartitionColumnType(string poison)
    {
        // Round-5 (Architect): BuildConstantColumn takes the partition column's DECLARED type from a foreign
        // schemaString, so a hostile table declaring a struct partition column would — before the R1 fix —
        // have flowed into ColumnVectors.Create and thrown a raw, unbounded nested-name message. The type is
        // now validated at the ENTRY guard (DeltaWriteEncoding.IsSupportedPartitionType) before any vector is
        // built. Measured 73,944 chars with raw U+2028. Security's round-4 audit called this site "scalar by
        // construction"; the predicate said otherwise, which is why it is adjudicated by execution here.
        string p = Payload(poison);
        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(
            () => DeltaReadEncoding.BuildConstantColumn(PoisonedStruct(p), "v", 1));

        Assert.Contains("is not supported as a Delta partition column", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
        Assert.Contains("Type 'struct'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_DeltaWriteEncoding_NestedVectorType(string poison)
    {
        // Round-5 (Architect): ColumnVectors.Create really does build StructColumnVector, so `source` can be
        // nested-typed at both DeltaWriteEncoding default arms. One field is enough to prove the echo; the
        // flood follows from the same SimpleString recursion the two doors above measure.
        string p = Payload(poison);
        var oneField = new StructType([new StructField(p, DataTypes.StringType, nullable: true)]);
        var source = (StructColumnVector)ColumnVectors.Create(oneField, 1);
        ((MutableColumnVector)source.Child(0)).AppendBytes("v"u8);
        source.EndStruct();
        var destination = (StructColumnVector)ColumnVectors.Create(oneField, 1);

        DeltaStorageException append = Assert.Throws<DeltaStorageException>(
            () => DeltaWriteEncoding.AppendValue(destination, source, 0));
        Assert.Contains("no columnar encoding for type", append.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(append.Message, p);

        DeltaStorageException format = Assert.Throws<DeltaStorageException>(
            () => DeltaWriteEncoding.FormatPartitionValue(source, 0));
        Assert.Contains("is not supported as a Delta partition column", format.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(format.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ColumnBatchPartitioner_PartitionColumnNotInWriteSchema(string poison)
    {
        // Round-4 (Security): reachable through the PUBLIC DeltaWriteTarget.AppendAsync door. BOTH tokens
        // were raw — the column name, and `fullSchema.SimpleString`, which on a 5,000-column schema rendered
        // ~129,000 characters with every field name verbatim.
        string p = Payload(poison);
        var wide = new StructType(
            Enumerable.Range(0, 5_000)
                .Select(i => new StructField(
                    string.Create(CultureInfo.InvariantCulture, $"{p}_{i}"), DataTypes.StringType, nullable: true))
                .ToArray());

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => ColumnBatchPartitioner.Partition(wide, [p], []));

        Assert.Contains("is not present in the write schema", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);

        // The bounded rendering keeps the operator-useful facts: how many columns, and a sample of names.
        Assert.Contains("5000 column(s)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("… (+4984 more)", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public async Task Door_ParquetFileWriter_BatchSchemaMismatch(string poison)
    {
        // Round-4 (Security): an internal-invariant guard, but InternalsVisibleTo makes WriteAsync directly
        // callable and it rendered TWO full StructTypes raw (~129,000 chars on a wide schema).
        string p = Payload(poison);
        var writerSchema = new StructType(
            Enumerable.Range(0, 5_000)
                .Select(i => new StructField(
                    string.Create(CultureInfo.InvariantCulture, $"{p}_{i}"), DataTypes.LongType, nullable: true))
                .ToArray());

        var batchSchema = new StructType([new StructField(p, DataTypes.LongType, nullable: true)]);
        MutableColumnVector vector = ColumnVectors.Create(DataTypes.LongType, 1);
        vector.AppendValue(1L);
        var batch = new ManagedColumnBatch(batchSchema, [vector], 1);

        using var output = new MemoryStream();
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => new ParquetFileWriter().WriteAsync(output, writerSchema, [batch], CancellationToken.None));

        Assert.Contains("but the writer schema is", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    // ---------------------------------------------------------------------------------------------------
    // Write-path constraints
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_Constraints_MalformedInvariant(string poison)
    {
        string p = Payload(poison);
        var schema = new StructType(
        [
            new StructField(
                p,
                DataTypes.LongType,
                nullable: true,
                FieldMetadata.FromValues(
                [
                    new KeyValuePair<string, MetadataValue>(
                        "delta.invariants", MetadataValue.String("{\"not-an-expression\":1}")),
                ])),
        ]);

        DeltaProtocolException ex = Assert.ThrowsAny<DeltaProtocolException>(
            () => DeltaTableConstraints.CollectForWrite(snapshot: null, schema));

        AssertNeutralizedAndBounded(ex.Message, p);
    }

    // ---------------------------------------------------------------------------------------------------
    // Object-store path echoes (Hive-encoded — see PathDisclosureHygieneTests for the VALUE-drop rule)
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Poisons))]
    public async Task Door_Backend_NotFound(string poison)
    {
        string p = Payload(poison).Replace('\0', 'z').Replace('/', 'z') + ".parquet";
        string root = Path.Combine(Path.GetTempPath(), "sweep-" + Path.GetRandomFileName());
        using var backend = new Backends.LocalFileSystemBackend(root);
        try
        {
            DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
                () => backend.OpenReadAsync(p, CancellationToken.None).AsTask());

            AssertNeutralizedAndBounded(ex.Message, p);
            Assert.Equal(p, ex.Path); // raw, on the typed property
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    // The `{detail}` channel: a GENUINE framework IO exception, whose message embeds the absolute path the
    // runtime saw. Door_Backend_NotFound cannot cover this -- NotFound strips the payload and produces no
    // framework detail at all, so until now the only channel that carries FOREIGN text verbatim through
    // Redact was untested for injection. Redact's PII strip (root + Hive value) is not a hygiene strip.
    [Theory]
    [MemberData(nameof(Poisons))]
    public async Task Door_Backend_FrameworkDetailOnIoFailure(string poison)
    {
        string p = Payload(poison);
        string root = Path.Combine(Path.GetTempPath(), "sweep-" + Path.GetRandomFileName());
        using var backend = new Backends.LocalFileSystemBackend(root);
        try
        {
            await backend.PutIfAbsentAsync("plain.bin", new byte[] { 1 }, CancellationToken.None);
            Backends.LocalFileSystemBackend.IoFaultHook = tag => tag == "delete"
                ? new IOException($"Could not delete file '{Path.Combine(root, "sub", p.Replace('/', 'z').Replace('\\', 'z'))}'.")
                : null;

            DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
                () => backend.DeleteAsync("plain.bin", CancellationToken.None).AsTask());

            AssertNeutralizedAndBounded(ex.Message, p);
        }
        finally
        {
            Backends.LocalFileSystemBackend.IoFaultHook = null;
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_DescribePath_PoisonedPartitionColumnName(string poison)
    {
        string p = Payload(poison).Replace('\0', 'z').Replace('=', 'z');

        string rendered = DiagnosticText.DescribePath(p + "=value/part-0.parquet");

        AssertNeutralizedAndBounded(rendered, p);
        Assert.DoesNotContain("value", rendered, StringComparison.Ordinal); // partition VALUE dropped
    }

    // ---------------------------------------------------------------------------------------------------
    // Schema enforcement — the ORDINARY mergeSchema=true write path (no crafted file, no foreign log)
    // ---------------------------------------------------------------------------------------------------

    // DeltaSchemaEnforcer.MergeType switches on (tableType, writeType) and its arms match only when BOTH
    // sides are the SAME kind, so the default arm — commented "a differing scalar type" — also catches a
    // MISMATCHED KIND. A table column declared struct<...> against a scalar write is the everyday
    // mergeSchema shape, and it rendered the whole nested type. Highest-reachability site in this sweep:
    // reached by a public write with no attacker-authored file at all.
    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_SchemaEnforcer_IncompatibleKind(string poison)
    {
        string p = Payload(poison);
        var table = new StructType([new StructField("payload", PoisonedStructOf(p), nullable: true)]);
        var write = new StructType([new StructField("payload", DataTypes.LongType, nullable: true)]);

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: false));

        Assert.Contains("is not compatible with the table type", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);

        // The SAME message renders the WRITE type too, and with the table side nested and the write side a
        // scalar that token was a bounded literal — so the write-side renderer was vacuous. Drive the mirror
        // shape as well, which is just as ordinary a mergeSchema write.
        var scalarTable = new StructType([new StructField("payload", DataTypes.LongType, nullable: true)]);
        var nestedWrite = new StructType([new StructField("payload", PoisonedStructOf(p), nullable: true)]);

        DeltaSchemaMismatchException mirrored = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                scalarTable, nestedWrite, SchemaEvolutionMode.MergeSchema, partitionColumns: null,
                typeWideningEnabled: false));

        Assert.Contains("is not compatible with the table type", mirrored.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(mirrored.Message, p);
    }

    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_SchemaEnforcer_PartitionColumnEvolution(string poison)
    {
        string p = Payload(poison);
        var table = new StructType([new StructField("part", PoisonedStructOf(p), nullable: true)]);
        var write = new StructType([new StructField("part", DataTypes.StringType, nullable: true)]);

        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, ["part"], typeWideningEnabled: false));

        AssertNeutralizedAndBounded(ex.Message, p);

        // Mirror shape: the partition-evolution message renders the WRITE type as well, and it was the clean
        // side in the row above.
        var scalarTable = new StructType([new StructField("part", DataTypes.StringType, nullable: true)]);
        var nestedWrite = new StructType([new StructField("part", PoisonedStructOf(p), nullable: true)]);

        DeltaSchemaMismatchException mirrored = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                scalarTable, nestedWrite, SchemaEvolutionMode.MergeSchema, ["part"], typeWideningEnabled: false));

        AssertNeutralizedAndBounded(mirrored.Message, p);
    }

    // The WIDENING arms (DeltaSchemaMismatchException.TypeWideningUnsupported /
    // PartitionColumnWideningDeferred) each render TWO types, and no door could poison either — because
    // NEITHER CAN CARRY A NAME. This is the executed disproof rather than a reading of the allowlist: a type
    // change reaches those arms only when TypeWidening classifies it as sanctioned, and no nested
    // (name-bearing) type is sanctioned in either direction. The control at the end shows the arm IS
    // reachable and that what it renders is a pair of bounded type literals.
    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_SchemaEnforcer_WideningArms_CannotCarryANameBearingType(string poison)
    {
        string p = Payload(poison);
        StructType nested = PoisonedStructOf(p);
        var alsoNested = new ArrayType(nested, containsNull: true);

        foreach ((DataType from, DataType to) in new (DataType, DataType)[]
        {
            (nested, DataTypes.LongType),
            (DataTypes.LongType, nested),
            (nested, alsoNested),
            (alsoNested, nested),
            (nested, nested),
        })
        {
            Assert.False(TypeWidening.IsSanctionedWidening(from, to));
            Assert.False(TypeWidening.IsAnySanctionedWidening(from, to));
            Assert.False(TypeWidening.IsSchemaEvolutionWidening(from, to));
        }

        // …and the routing follows: a nested-vs-scalar change is classified IncompatibleType, never one of
        // the two widening kinds, so the four DescribeType calls on those arms cannot see a name.
        var table = new StructType([new StructField("payload", nested, nullable: true)]);
        var write = new StructType([new StructField("payload", DataTypes.LongType, nullable: true)]);
        DeltaSchemaMismatchException ex = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                table, write, SchemaEvolutionMode.MergeSchema, partitionColumns: null, typeWideningEnabled: false));
        Assert.Equal(DeltaSchemaMismatchKind.IncompatibleType, ex.Kind);
        Assert.DoesNotContain("The type change", ex.Message, StringComparison.Ordinal);

        // The CONTROL: the widening arm IS reachable, and what it renders is a pair of bounded type literals.
        var wideTable = new StructType([new StructField("payload", DataTypes.IntegerType, nullable: true)]);
        var wideWrite = new StructType([new StructField("payload", DataTypes.LongType, nullable: true)]);
        DeltaSchemaMismatchException widening = Assert.Throws<DeltaSchemaMismatchException>(
            () => DeltaSchemaEnforcer.Reconcile(
                wideTable, wideWrite, SchemaEvolutionMode.MergeSchema, partitionColumns: null,
                typeWideningEnabled: false));
        Assert.Equal(DeltaSchemaMismatchKind.TypeWideningUnsupported, widening.Kind);
        Assert.Contains("The type change 'int'→'bigint'", widening.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(widening.Message, p);
    }

    // ChangeDataWriter's reserved-name guard fires ONLY on a case-insensitive match against one of three
    // bounded literals, so the name it echoes is always a case variant of "_change_type" /
    // "_commit_version" / "_commit_timestamp" and can never carry a payload. Executed disproof, both ways:
    // the poisoned name does not fire the guard at all, and the name that does fire it is the bounded
    // literal's case variant.
    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_ChangeDataWriter_ReservedColumnName_IsValueConditional(string poison)
    {
        string p = Payload(poison);

        ChangeDataWriter.EnsureNoReservedColumnNames(Flat(p)); // does not throw: no reserved-name match

        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(
            () => ChangeDataWriter.EnsureNoReservedColumnNames(Flat("_ChAnGe_TyPe")));
        Assert.Contains("reserved Change Data Feed metadata column name", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'_ChAnGe_TyPe'", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    // 40 leaves, each carrying the poison: enough that a recursive SimpleString render is unmistakably
    // unbounded (the 20,000-char payload alone renders ~800,000 characters) without slowing the sweep.
    private static StructType PoisonedStructOf(string poison)
    {
        var fields = new List<StructField>(40);
        for (int i = 0; i < 40; i++)
        {
            fields.Add(new StructField(
                string.Create(CultureInfo.InvariantCulture, $"{poison}{i}"), DataTypes.StringType, nullable: true));
        }

        return new StructType(fields);
    }

    // ---------------------------------------------------------------------------------------------------
    // Change feed — CDF-EE-08 leaf-schema gate (the ONE enumerated family that had no sweep door, which is
    // why three rounds of sweeping missed the SimpleString echo inside it).
    // ---------------------------------------------------------------------------------------------------

    // The EE-08 gate compares the version's metadata-declared column type against the cdc file's LEAF type.
    // `expected` comes from ColumnMappingProjection.BuildDataSchema, which copies `field.DataType` VERBATIM
    // from the foreign `schemaString` — it does NOT flatten — while `fileType` is a ParquetLeafColumn.Type and
    // is therefore always a leaf. A StructType can never equal a leaf, so a struct-typed metadata column makes
    // the guard fire UNCONDITIONALLY, rendering the whole nested type into the message.
    //
    // REACHABILITY, established by execution rather than by reading (round-7 adjudication): the struct only
    // SURVIVES to EE-08 under column-mapping mode NONE. Under name/id mode, ResolvePhysicalNames — called by
    // BuildVersionPhysicalDataSchema BEFORE BuildDataSchema — rejects a nested top-level column fail-closed, so
    // EE-08 never sees one and only the NAME branch of the leaf-type comparison is reachable with a struct.
    // Both outcomes are pinned here: if that upstream guard is ever relaxed, the name/id rows start reporting
    // the EE-08 message and the hygiene assertion still holds them.
    [Theory]
    [MemberData(nameof(PoisonsByMappingMode))]
    public async Task Door_ChangeFeed_Ee08NestedMetadataColumn(string poison, string mode)
    {
        string p = Payload(poison);
        DeltaReadException ex = await ReadPoisonedCdcAsync(p, mode);

        Assert.Contains(
            mode == "none" ? "has leaf type" : "is a nested",
            ex.Message,
            StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    // DERIVED from PoisonNames — a CROSS PRODUCT with the mapping modes, not a second spelling of the
    // corpus. The previous revision listed seven payload names here as a fresh literal, so the two classes
    // it happened to omit (bidi, loneSurr) never reached the change-feed surface and a tenth class added to
    // the corpus would have skipped it in silence. Both omitted classes pass this door, so nothing is
    // subtracted; the lone-surrogate row is the one whose payload the JSON metadata round-trip may itself
    // replace with U+FFFD before the door sees it (indistinguishable from Sanitize's own replacement in the
    // rendered message), so that row pins the door's SHAPE and the other eight pin the payload.
    public static TheoryData<string, string> PoisonsByMappingMode
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (string poison in PoisonNames)
            {
                foreach (string mode in new[] { "none", "name", "id" })
                {
                    data.Add(poison, mode);
                }
            }

            return data;
        }
    }

    // ChangeFeedReader:901 and :914 echo the TOP-LEVEL data column name, and the seeder below hardcoded it
    // as the clean literal "s" — so both echoes were mutation-VACUOUS: reverting either
    // `Sanitize(expectedField.Name)` to the raw name left this whole file green. The poison now lands in the
    // token those two lines actually render.
    //
    // NONE mode only, and that is the reachability fact rather than a convenience: under name/id mode
    // `expected` carries the PHYSICAL name, and a physical name carrying any class in this corpus is
    // rejected fail-closed at LOAD by ColumnMapping.EnsureSafePhysicalName (the unsafe-path-segment guard)
    // before a cdc file is ever opened — disproved by execution in
    // Door_ChangeFeed_MappedPoisonedPhysicalName_IsStoppedAtLoad below, which is what keeps this a
    // reachability claim rather than a reading of the code.
    [Theory]
    [MemberData(nameof(Poisons))]
    public async Task Door_ChangeFeed_Ee08PoisonedDataColumnName(string poison)
    {
        string p = Payload(poison);
        DeltaReadException ex = await ReadPoisonedCdcAsync(p, "none", topLevelColumn: p, omitColumnFromBody: false);

        Assert.Contains("has leaf type", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    // The sibling arm, ChangeFeedReader:901: the version's metadata declares a data column the cdc file body
    // does not carry at all. Its echo is the same foreign top-level name, and no door reached it.
    [Theory]
    [MemberData(nameof(Poisons))]
    public async Task Door_ChangeFeed_MissingDataColumn(string poison)
    {
        string p = Payload(poison);
        DeltaReadException ex = await ReadPoisonedCdcAsync(p, "none", topLevelColumn: p, omitColumnFromBody: true);

        Assert.Contains("is missing the version's data column", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    // THE REACHABILITY DISPROOF for the two doors above being none-mode only, executed rather than asserted
    // in a comment: under a mapped mode the same poisoned top-level name is a PHYSICAL name, and the snapshot
    // load rejects it — at the leaf-only guard when the declared type is nested, at
    // ColumnMapping.EnsureSafePhysicalName when it is a leaf — so the change feed is never consulted and
    // ChangeFeedReader's echoes cannot see a poisoned `expected` name in name/id mode. Both messages that do
    // fire are themselves in this sweep (ColumnMapping:257 and :1024), so these rows are hygiene assertions
    // too, not only reachability notes.
    [Theory]
    [MemberData(nameof(PoisonsThatSurviveTheMetadataJson))]
    public async Task Door_ChangeFeed_MappedPoisonedPhysicalName_IsStoppedAtLoad(string poison)
    {
        string p = Payload(poison);

        // (a) The LEAF-declared shape — the one the missing-data-column arm uses. Here the poisoned name is a
        // physical name and the unsafe-path-segment guard is what fires.
        DeltaReadException leaf = await ReadPoisonedCdcAsync(p, "name", topLevelColumn: p, omitColumnFromBody: true);
        Assert.Contains("not a safe path segment", leaf.Message, StringComparison.Ordinal);

        // (b) The STRUCT-declared shape — the one the EE-08 leaf-type arm uses. A nested mapped column is
        // rejected even earlier, by the leaf-only guard, so it never reaches the path-segment check.
        DeltaReadException nested = await ReadPoisonedCdcAsync(p, "name", topLevelColumn: p, omitColumnFromBody: false);
        Assert.Contains("is a nested", nested.Message, StringComparison.Ordinal);

        // Neither EE-08 echo is reachable in a mapped mode with a poisoned name: that is the disproof.
        foreach (DeltaReadException ex in new[] { leaf, nested })
        {
            Assert.DoesNotContain("has leaf type", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("is missing the version's data column", ex.Message, StringComparison.Ordinal);
            AssertNeutralizedAndBounded(ex.Message, p);
        }
    }

    // The corpus MINUS the classes that cannot travel the metadata-JSON channel, so a door that asserts WHICH
    // guard fires is not handed a payload that arrives already clean. ONE class is subtracted and the reason
    // was measured, not reasoned: System.Text.Json replaces an unpaired surrogate with U+FFFD when the
    // schemaString is written, and U+FFFD IS a safe path segment — so the `loneSurr` row reached EE-08 instead
    // of the load-time guard this door exists to disprove reachability with. It stays in every door that only
    // asserts the message is neutralized, where it is harmless. The subtraction is DERIVED from PoisonNames
    // and pinned by PoisonCorpus_IsSingleSourced below, so renaming a payload cannot silently widen it.
    private static readonly string[] JsonUntransportablePoisons = ["loneSurr"];

    public static TheoryData<string> PoisonsThatSurviveTheMetadataJson
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in PoisonNames)
            {
                if (!JsonUntransportablePoisons.Contains(name, StringComparer.Ordinal))
                {
                    data.Add(name);
                }
            }

            return data;
        }
    }

    // The corpus MINUS the classes a PARQUET FOOTER cannot carry back verbatim. Measured, not reasoned: the
    // Thrift footer is UTF-8, and an unpaired surrogate is not encodable, so a forged leaf name carrying
    // `loneSurr` reads back as U+FFFD and no longer matches the requested field — the door would then be
    // asserting on a column-not-found message instead of the guard it exists to pin. Same shape of named
    // subtraction as JsonUntransportablePoisons, and pinned by PoisonCorpus_IsSingleSourced the same way.
    private static readonly string[] FooterUntransportablePoisons = ["loneSurr"];

    public static TheoryData<string> PoisonsThatSurviveAParquetFooter
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in PoisonNames)
            {
                if (!FooterUntransportablePoisons.Contains(name, StringComparer.Ordinal))
                {
                    data.Add(name);
                }
            }

            return data;
        }
    }

    // The corpus MINUS the classes a POSIX FILE NAME cannot carry. Measured: an unpaired surrogate is not
    // encodable to a filesystem name at all (the runtime rejects the name before any backend guard runs), so
    // a door that must actually CREATE the poisoned name cannot use it. Same named-subtraction discipline as
    // the two axes above, and pinned by PoisonCorpus_IsSingleSourced.
    private static readonly string[] FileNameUntransportablePoisons = ["loneSurr"];

    public static TheoryData<string> PoisonsThatSurviveAFileName
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in PoisonNames)
            {
                if (!FileNameUntransportablePoisons.Contains(name, StringComparer.Ordinal))
                {
                    data.Add(name);
                }
            }

            return data;
        }
    }

    // THE CORPUS PIN. Every axis in this file must be PoisonNames or a NAMED subtraction from it; this is the
    // check that says so mechanically, because the defect it replaces was a second literal list of payload
    // names that drifted from the first without anything going red. Bounded on BOTH sides: a subtraction that
    // names a payload PoisonNames no longer has is dead (and would silently stop subtracting anything), and a
    // subtraction that swallowed the corpus would leave a door with nothing to run.
    [Fact]
    public void PoisonCorpus_IsSingleSourced()
    {
        Assert.Equal(PoisonNames.Length, Poisons.Count);
        Assert.Equal(PoisonNames.Length * 3, PoisonsByMappingMode.Count);

        foreach (string subtracted in JsonUntransportablePoisons)
        {
            Assert.Contains(subtracted, PoisonNames, StringComparer.Ordinal);
        }

        foreach (string subtracted in FooterUntransportablePoisons)
        {
            Assert.Contains(subtracted, PoisonNames, StringComparer.Ordinal);
        }

        foreach (string subtracted in FileNameUntransportablePoisons)
        {
            Assert.Contains(subtracted, PoisonNames, StringComparer.Ordinal);
        }

        Assert.Equal(PoisonNames.Length - JsonUntransportablePoisons.Length, PoisonsThatSurviveTheMetadataJson.Count);
        Assert.Equal(
            PoisonNames.Length - FooterUntransportablePoisons.Length, PoisonsThatSurviveAParquetFooter.Count);
        Assert.Equal(
            PoisonNames.Length - FileNameUntransportablePoisons.Length, PoisonsThatSurviveAFileName.Count);
        Assert.True(
            JsonUntransportablePoisons.Length < PoisonNames.Length
                && FooterUntransportablePoisons.Length < PoisonNames.Length
                && FileNameUntransportablePoisons.Length < PoisonNames.Length,
            "every payload class was subtracted, so the doors that assert WHICH guard fires run on nothing");

        // Every name resolves to a payload: a corpus entry with no Payload arm is a row that throws instead of
        // testing, and the switch is the only place the two spellings could drift now.
        foreach (string name in PoisonNames)
        {
            Assert.False(string.IsNullOrEmpty(Payload(name)));
        }
    }

    // The CONDITIONAL-CREATE ambiguity arms. Both are ambiguous-outcome paths a commit MUST distinguish, and
    // both echo the caller-supplied relative path; no door drove either, so their DescribePath renders were
    // vacuous. NOTE the platform split: on POSIX the fd-anchored implementation runs, on Windows the
    // string-path twin — this door pins whichever one the runner executes, which is what makes the Windows
    // twin a platform-gated rather than an untested site.
    [Theory]
    [MemberData(nameof(PoisonsThatSurviveAFileName))]
    public async Task Door_Backend_ConditionalCreateAmbiguity(string poison)
    {
        string p = StorablePoisonName(poison);
        string root = Path.Combine(Path.GetTempPath(), "sweep-" + Path.GetRandomFileName());
        using var backend = new Backends.LocalFileSystemBackend(root);
        try
        {
            // (a) The publish itself fails ambiguously.
            Backends.LocalFileSystemBackend.IoFaultHook = tag => tag == "publish"
                ? new IOException("link failed")
                : null;
            DeltaStorageException ambiguous = await Assert.ThrowsAsync<DeltaStorageException>(
                () => backend.PutIfAbsentAsync(p, new byte[] { 1 }, CancellationToken.None).AsTask());
            Assert.Equal(StorageErrorKind.RetryUnsafeAmbiguous, ambiguous.Kind);
            Assert.Contains("Conditional-create of", ambiguous.Message, StringComparison.Ordinal);
            AssertNeutralizedAndBounded(ambiguous.Message, p);
            Assert.Equal(p, ambiguous.Path); // raw, on the typed property
            Backends.LocalFileSystemBackend.IoFaultHook = null;

            // (b) The publish succeeded but the directory entry could not be made durable.
            if (!OperatingSystem.IsWindows())
            {
                Backends.DirectoryFsync.FsyncHook = _ => 5; // EIO
                DeltaStorageException notDurable = await Assert.ThrowsAsync<DeltaStorageException>(
                    () => backend.PutIfAbsentAsync(p, new byte[] { 1 }, CancellationToken.None).AsTask());
                Assert.Equal(StorageErrorKind.RetryUnsafeAmbiguous, notDurable.Kind);
                Assert.Contains("could not be made durable", notDurable.Message, StringComparison.Ordinal);
                AssertNeutralizedAndBounded(notDurable.Message, p);
                Assert.Equal(p, notDurable.Path);
            }
        }
        finally
        {
            Backends.LocalFileSystemBackend.IoFaultHook = null;
            Backends.DirectoryFsync.FsyncHook = null;
            TryDeleteTree(root);
        }
    }

    // The staged-write stream DESCRIBES its path ONCE in its constructor and every message it raises reuses
    // that field, so the constructor's DescribePath is the single site the whole stream's hygiene depends on —
    // and nothing pinned it.
    [Theory]
    [MemberData(nameof(PoisonsThatSurviveAFileName))]
    public async Task Door_Backend_StagedWriteStreamDisplayPath(string poison)
    {
        string p = StorablePoisonName(poison);
        string root = Path.Combine(Path.GetTempPath(), "sweep-" + Path.GetRandomFileName());
        using var backend = new Backends.LocalFileSystemBackend(root);
        try
        {
            Stream stream = await backend.OpenWriteAsync(p, CancellationToken.None);
            await using (stream.ConfigureAwait(false))
            {
                Backends.LocalFileSystemBackend.IoFaultHook = tag => tag == "write"
                    ? new IOException("write failed")
                    : null;

                DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
                    async () => await stream.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None));

                Assert.Contains("staged write to", ex.Message, StringComparison.OrdinalIgnoreCase);
                AssertNeutralizedAndBounded(ex.Message, p);
            }
        }
        finally
        {
            Backends.LocalFileSystemBackend.IoFaultHook = null;
            TryDeleteTree(root);
        }
    }

    // The symlink-escape rejection: a table-relative path that canonicalizes outside the confined root. The
    // echoed token is the caller-supplied relative path, so it is fully attacker-influenced.
    [Theory]
    [MemberData(nameof(PoisonsThatSurviveAFileName))]
    public async Task Door_Backend_SymlinkEscape(string poison)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating a directory symlink needs elevation on Windows; the POSIX runner pins the site
        }

        string p = StorablePoisonName(poison);
        string root = Path.Combine(Path.GetTempPath(), "sweep-" + Path.GetRandomFileName());
        string outside = Path.Combine(Path.GetTempPath(), "sweep-outside-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(outside, "secret.parquet"), new byte[] { 1 });
        using var backend = new Backends.LocalFileSystemBackend(root);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, p), outside);
            string escaping = p + "/secret.parquet";

            DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
                () => backend.OpenReadAsync(escaping, CancellationToken.None).AsTask());

            Assert.Equal(StorageErrorKind.PathNotConfined, ex.Kind);
            AssertNeutralizedAndBounded(ex.Message, p);
        }
        finally
        {
            TryDeleteTree(root);
            TryDeleteTree(outside);
        }
    }

    // A payload rendered as a STORABLE object name: the two characters a POSIX file name cannot contain are
    // replaced, and the name is trimmed to NAME_MAX-safe length (255 on macOS/Linux; these doors CREATE the
    // name, unlike Door_Backend_NotFound which only resolves it). 200 characters still exceeds
    // DiagnosticText's 128-char cap by 1.5x, so the flood class keeps exercising the cap at these sites.
    private static string StorablePoisonName(string poison)
    {
        string payload = Payload(poison).Replace('\0', 'z').Replace('/', 'z');
        return (payload.Length > 200 ? payload[..200] : payload) + ".parquet";
    }

    private static void TryDeleteTree(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // THE DOCUMENTED-UNREACHABLE SET
    //
    // Every redaction site in src/DeltaSharp.Storage is either pinned by a door above (its revert reddens at
    // least one shipped test) or listed HERE with an EXECUTED disproof — never left as a silent survivor.
    // MEASURED-AT the commit carrying this block, by a per-site revert sweep of all 135 redaction call sites
    // (DiagnosticText.Sanitize / SanitizeEchoedToken / DescribePath / DescribeSchema / DescribeType /
    // SanitizeAndJoin) against the full tests/DeltaSharp.Storage.Tests suite: before this change RED 77,
    // GREEN 58; after it RED 111, GREEN 24, and the 24 are exactly the sites enumerated below.
    // The three reasons, and where each is disproved:
    //
    //   (a) VALUE-CONDITIONAL: the guard only fires on a match against a bounded literal, so the token it
    //       echoes cannot carry a payload.
    //         ChangeDataWriter.cs:136 (reserved CDF column name)
    //           -> Door_ChangeDataWriter_ReservedColumnName_IsValueConditional
    //         ColumnMapping.cs:408 (duplicate physical name)
    //           -> Door_ColumnMapping_ValidateSchema_DuplicatePhysicalName (the DoesNotContain arm: every
    //              payload class is stopped by the upstream safe-segment guard first)
    //
    //   (b) BOUNDED-TYPE RENDER: the arm is statically reachable only with a SCALAR type, whose SimpleString
    //       is a short literal ("void", "date", "decimal(10,2)"), so the renderer has no name to leak.
    //         ParquetTypeMapping.cs:87 (type SimpleString), NestedParquetColumnReader.cs:151, :215, :643,
    //         :695 (element type), :1156
    //           -> Door_Unreachable_TypeRenderArms_ReceiveOnlyBoundedScalars
    //         DeltaSchemaMismatchException.cs:146 (TypeWideningUnsupported, both DescribeType renders) and
    //         :162 (PartitionColumnWideningDeferred, both renders) — these two arms are entered ONLY after
    //         the enforcer has classified the change as a Delta-sanctioned WIDENING, and the widening
    //         relation is a bounded set of scalar->scalar pairs, so neither type can be name-bearing.
    //           -> Door_SchemaEnforcer_WideningArms_CannotCarryANameBearingType (the `path` render on the
    //              same two lines IS payload-bearing and IS pinned, by Door_SchemaEnforcer_* above)
    //
    //   (c) DOMINATED: an upstream guard, a decoder, or a platform branch always fires first.
    //         ParquetFileWriter.cs:287/288 and ParquetFileReader.cs:1791 (value-switch defaults, dominated by
    //         ParquetTypeMapping.CreateField), ParquetFileReader.cs:1896 (promotion default, dominated by
    //         ValidateFileField)
    //           -> Door_Unreachable_TypeRenderArms_ReceiveOnlyBoundedScalars
    //         NestedParquetColumnReader.cs:255 (struct per-row count), :946 (no chunk metadata), :991
    //         (Int32 overflow)
    //           -> Door_NestedRead_ForgedLeafPath arms (c), (d), (e)
    //         ChangeFeedReader.cs:844 (id-branch declared type)
    //           -> Door_ChangeFeed_Ee08NestedMetadataColumn id-mode rows +
    //              Door_ChangeFeed_MappedPoisonedPhysicalName_IsStoppedAtLoad
    //         LocalFileSystemBackend.PutIfAbsentAsync (:399, :427 — RetryUnsafeAmbiguous on race)
    //         and StagedWriteStream.Publish (:2138 RetryUnsafeAmbiguous, :2147 AlreadyExists, :2164 RetryUnsafeAmbiguous)
    //         — the non-confined WINDOWS producer set. The doors do not branch on platform:
    //         Door_Backend_ConditionalCreateAmbiguity, Door_Backend_NotFound and
    //         Door_Backend_StagedWriteStreamDisplayPath pin whichever twin the runner executes, so these
    //         five are covered on a Windows runner and dominated on this one.
    // ---------------------------------------------------------------------------------------------------
    [Theory]
    [MemberData(nameof(Poisons))]
    public async Task Door_Unreachable_TypeRenderArms_ReceiveOnlyBoundedScalars(string poison)
    {
        string p = Payload(poison);
        StructType nested = PoisonedStructOf(p);

        // (1) EVERY scalar DeltaSharp admits renders a bounded literal — so a type renderer that can only be
        // handed a scalar has nothing to leak, whatever it calls.
        DataType[] scalars =
        [
            DataTypes.BooleanType, DataTypes.ByteType, DataTypes.ShortType, DataTypes.IntegerType,
            DataTypes.LongType, DataTypes.FloatType, DataTypes.DoubleType, DataTypes.StringType,
            DataTypes.BinaryType, DataTypes.DateType, DataTypes.TimestampType, DataTypes.TimestampNtzType,
            DataTypes.CreateDecimalType(10, 2), DataTypes.NullType,
        ];
        foreach (DataType type in scalars)
        {
            Assert.True(
                type.SimpleString.Length <= 32,
                $"scalar SimpleString is not a bounded literal: {type.SimpleString.Length} chars");
            AssertNeutralizedAndBounded(type.SimpleString, p);
        }

        // (2) …and the NESTED types are intercepted before those arms, by an arm that renders only the KIND.
        DeltaStorageException mapping = Assert.ThrowsAny<DeltaStorageException>(
            () => ParquetTypeMapping.CreateField(new StructField("c", nested, nullable: true)));
        Assert.Contains("nested types (phased", mapping.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("' of type '", mapping.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(mapping.Message, p);

        DeltaStorageException shape = Assert.ThrowsAny<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateShape(
                new global::Parquet.Schema.DataField<int?>("x"), nested, "c"));
        Assert.Contains("the file column is not a struct", shape.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("is not supported", shape.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(shape.Message, p);

        // …and a nested leaf inside a nested request is rejected by the nested-within-nested arm (pinned by
        // Door_NestedRead_NestedWithinNested) rather than by the physical-type comparison, which is why the
        // latter's requested-type render can only ever see a scalar.
        DeltaStorageException leaf = Assert.ThrowsAny<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateShape(
                new global::Parquet.Schema.StructField("c", new global::Parquet.Schema.DataField<int?>("f")),
                new StructType([new StructField("f", nested, nullable: true)]),
                "c"));
        Assert.Contains("a nested type within a nested type", leaf.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not match the requested", leaf.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(leaf.Message, p);

        // (3) The writer's and reader's VALUE-switch defaults are dominated by the schema mapping: every
        // scalar CreateField admits round-trips through both switches, and the one scalar it rejects (void)
        // never reaches them.
        foreach (DataType type in scalars)
        {
            if (type == DataTypes.NullType)
            {
                DeltaStorageException rejected = Assert.ThrowsAny<DeltaStorageException>(
                    () => ParquetTypeMapping.CreateField(new StructField("c", type, nullable: true)));
                Assert.Contains("Parquet mapping for column", rejected.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("Parquet write for column", rejected.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("Parquet read for column", rejected.Message, StringComparison.Ordinal);
                continue;
            }

            var schema = new StructType([new StructField("c", type, nullable: true)]);
            MutableColumnVector vector = ColumnVectors.Create(type, 1);
            vector.AppendNull();
            using var output = new MemoryStream();
            await new ParquetFileWriter().WriteAsync(
                output, schema, [new ManagedColumnBatch(schema, [vector], 1)], CancellationToken.None);

            using var input = new MemoryStream(output.ToArray(), writable: false);
            await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
                input, schema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
                CancellationToken.None))
            {
            }
        }

        // (4) The promotion default arm is dominated by the file-field validator: a pair that is NOT a
        // sanctioned widening is rejected before any promotion dispatch happens.
        byte[] stringFile = await ScalarFileAsync("c");
        DeltaStorageException promotion = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            using var input = new MemoryStream(stringFile, writable: false);
            await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
                input, new StructType([new StructField("c", DataTypes.StringType, nullable: true)]), null,
                nullFillMissingColumns: false, allowTypeWideningPromotion: true, CancellationToken.None))
            {
            }
        });
        Assert.DoesNotContain("cannot promote physical type", promotion.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(promotion.Message, p);
    }

    // ---------------------------------------------------------------------------------------------------
    // Parquet READ path — request-side echoes and FILE-derived leaf paths (#653's channel)
    // ---------------------------------------------------------------------------------------------------

    // A REQUESTED nested column under column-mapping id mode. The name is the caller's/table's, and the
    // guard fires before any resolution, so the echo is fully attacker-influenced.
    [Theory]
    [MemberData(nameof(Poisons))]
    public async Task Door_ParquetFileReader_NestedRequestUnderIdMode(string poison)
    {
        string p = Payload(poison);
        byte[] bytes = await ScalarFileAsync("clean");
        var requested = new StructType(
        [
            new StructField(
                p,
                new StructType([new StructField("x", DataTypes.LongType, nullable: true)]),
                nullable: true),
        ]);

        using var stream = new MemoryStream(bytes, writable: false);
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
                stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
                resolveByFieldId: true, CancellationToken.None))
            {
            }
        });

        Assert.Contains("nested column under column-mapping id mode", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    // A SCALAR request whose name resolves to a NESTED file column: the name is echoed from the request, and
    // the file supplies the nested shape (both sides foreign in a Delta read).
    [Theory]
    [MemberData(nameof(PoisonsThatSurviveAParquetFooter))]
    public async Task Door_ParquetFileReader_ScalarRequestForNestedFileColumn(string poison)
    {
        string p = Payload(poison);
        byte[] bytes = await StructOfIntFileAsync();
        bytes = await ParquetTestHelpers.ForgeFieldNameAsync(bytes, "S", p);

        using var stream = new MemoryStream(bytes, writable: false);
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
                stream, Flat(p), null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
                CancellationToken.None))
            {
            }
        });

        Assert.Contains("but the file column is nested", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    // The nested reader's SHAPE validator, reached at its own boundary: a requested struct field the file
    // does not carry, and one whose physical type disagrees. Both echo the REQUESTED field name, which is
    // the foreign token here.
    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_NestedRead_RequestedStructField(string poison)
    {
        string p = Payload(poison);

        var fileStruct = new global::Parquet.Schema.StructField(
            "col", new global::Parquet.Schema.DataField<int?>("x"));
        DeltaStorageException missing = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateShape(
                fileStruct,
                new StructType([new StructField(p, DataTypes.IntegerType, nullable: true)]),
                "col"));
        Assert.Contains("is missing requested field", missing.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(missing.Message, p);

        // Present in the file but of a different physical type: the same requested name is echoed through the
        // CONTEXT label the physical-type guard renders.
        var poisonedFileStruct = new global::Parquet.Schema.StructField(
            "col", new global::Parquet.Schema.DataField<int?>(p));
        DeltaStorageException mismatch = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateShape(
                poisonedFileStruct,
                new StructType([new StructField(p, DataTypes.StringType, nullable: true)]),
                "col"));
        Assert.Contains("does not match the requested", mismatch.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(mismatch.Message, p);
    }

    // The nested-within-nested arm, which its own production comment calls defense-in-depth "unreachable from
    // the read path today". It is NOT unreachable at the method boundary — ValidateShape is the reader's
    // public shape entry point and hands the requested element type straight to it — so it is pinned here
    // rather than declared dead; the upstream guard that makes it unreachable END TO END is itself pinned by
    // Door_ParquetTypeMapping_ArrayElementUnsupported.
    [Theory]
    [MemberData(nameof(Poisons))]
    public void Door_NestedRead_NestedWithinNested(string poison)
    {
        string p = Payload(poison);
        var fileList = new global::Parquet.Schema.ListField("col", new global::Parquet.Schema.DataField<int?>("element"));

        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateShape(
                fileList, new ArrayType(PoisonedStructOf(p), containsNull: true), "col"));

        Assert.Contains("a nested type within a nested type", ex.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ex.Message, p);
    }

    // FILE-DERIVED leaf paths (#653). The leaf NAME comes out of the footer, so a hostile file chooses it;
    // these guards each echo it. The file is written clean and then forged, which is exactly the threat model
    // (a valid writer would never emit these).
    [Theory]
    [MemberData(nameof(PoisonsThatSurviveAParquetFooter))]
    public async Task Door_NestedRead_ForgedLeafPath(string poison)
    {
        string p = Payload(poison);
        byte[] clean = await StructOfIntFileAsync();
        byte[] renamed = await ParquetTestHelpers.ForgeLeafColumnNameAsync(clean, "A", p);

        // (a) A NEGATIVE declared value count on a poisoned leaf.
        byte[] negative = await ParquetTestHelpers.ForgeLeafNumValuesAsync(renamed, p, -1);
        DeltaStorageException negativeCount = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadStructFieldAsync(negative, p));
        Assert.Contains("declares a negative value count", negativeCount.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(negativeCount.Message, p);

        // (b) A declared count whose eager decode breaches the ceiling.
        byte[] huge = await ParquetTestHelpers.ForgeLeafNumValuesAsync(renamed, p, 2_000_000_000);
        DeltaStorageException ceiling = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadStructFieldAsync(huge, p));
        Assert.Contains("eager decode would exceed", ceiling.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(ceiling.Message, p);

        // (c) DISPROOF for the Int32.MaxValue arm: a count ABOVE int.MaxValue is dominated by the ceiling
        // charge above (2^31 slots x >= 13 bytes each already exceeds the 4 GiB ceiling), so that arm cannot
        // be the one that fires and its echo is unreachable rather than untested.
        byte[] overflow = await ParquetTestHelpers.ForgeLeafNumValuesAsync(renamed, p, 3_000_000_000);
        DeltaStorageException dominated = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadStructFieldAsync(overflow, p));
        Assert.Contains("eager decode would exceed", dominated.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("exceeding Int32.MaxValue", dominated.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(dominated.Message, p);

        // (d) DISPROOF for the struct-field per-row arm. TWO ways to make a struct field's declared value
        // count disagree with the row count, and NEITHER reaches it: a REPEATED leaf inside a struct is
        // rejected first by the structural-level guard, and a FORGED count is rejected first by the decoder
        // (the pages still hold the honest number of values). Both messages are held to the hygiene contract.
        byte[] repeated = await ParquetTestHelpers.WriteStructWithRepeatedFieldAsync(
            [1, 2], [10, 11, 12], [0, 1, 0]);
        byte[] repeatedPoisoned = await ParquetTestHelpers.ForgeLeafColumnNameAsync(repeated, "A", p);
        DeltaStorageException structural = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadStructFieldAsync(repeatedPoisoned, p));
        Assert.DoesNotContain("one value per row", structural.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(structural.Message, p);

        byte[] miscounted = await ParquetTestHelpers.ForgeLeafNumValuesAsync(renamed, p, 5);
        DeltaStorageException decodeFailure = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadStructFieldAsync(miscounted, p));
        Assert.DoesNotContain("one value per row", decodeFailure.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(decodeFailure.Message, p);

        // (e) DISPROOF for the no-column-chunk-metadata arm: renaming the schema element WITHOUT its
        // PathInSchema is the only way a leaf loses its chunk, and the reader's row-group chunk-projection
        // check rejects that file BEFORE the nested decode runs, so that echo is dominated too. The message
        // that DOES fire is held to the same hygiene contract.
        byte[] orphaned = await ParquetTestHelpers.ForgeFieldNameAsync(clean, "A", p);
        DeltaStorageException projection = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadStructFieldAsync(orphaned, p));
        // (f) The DATE/TIMESTAMP decode's out-of-range catch, on a poisoned leaf: the file annotates an
        // ordinary INT32 as a logical DATE whose value is outside the representable range, so Parquet.Net's
        // conversion throws and the reader's fail-closed catch renders the FILE-DERIVED leaf path.
        byte[] dateAnnotated = await ParquetTestHelpers.ForgeColumnConvertedTypeToDateAsync(renamed, 0, 1);
        DeltaStorageException outOfRange = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadStructFieldAsync(dateAnnotated, p, DataTypes.DateType));
        Assert.Contains("outside", outOfRange.Message, StringComparison.Ordinal);
        AssertNeutralizedAndBounded(outOfRange.Message, p);
    }

    private static async Task<DeltaStorageException> ThrowingReadAsync(byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
                stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
                CancellationToken.None))
            {
            }
        });
    }

    private static async Task ReadStructFieldAsync(byte[] bytes, string leafName, DataType? fieldType = null)
    {
        var requested = new StructType(
        [
            new StructField(
                "S",
                new StructType([new StructField(leafName, fieldType ?? DataTypes.IntegerType, nullable: true)]),
                nullable: true),
        ]);
        using var stream = new MemoryStream(bytes, writable: false);
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
            stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
        }
    }

    private sealed class SweepInner
    {
        public int A { get; set; }
    }

    private sealed class SweepStructRow
    {
        public int Id { get; set; }

        public SweepInner? S { get; set; }
    }

    private static async Task<byte[]> StructOfIntFileAsync()
    {
        var rows = new List<SweepStructRow>
        {
            // int.MaxValue days since the epoch is outside DateTime's range, so a footer that re-annotates
            // this INT32 column as a logical DATE drives the decode's out-of-range fail-closed catch.
            new() { Id = 1, S = new SweepInner { A = int.MaxValue } },
            new() { Id = 2, S = new SweepInner { A = int.MaxValue } },
        };
        using var stream = new MemoryStream();
        await global::Parquet.Serialization.ParquetSerializer.SerializeAsync(
            rows, stream, cancellationToken: CancellationToken.None);
        return stream.ToArray();
    }

    private static async Task<byte[]> ScalarFileAsync(string columnName)
    {
        var schema = new StructType([new StructField(columnName, DataTypes.LongType, nullable: true)]);
        MutableColumnVector vector = ColumnVectors.Create(DataTypes.LongType, 1);
        vector.AppendValue(1L);
        using var output = new MemoryStream();
        await new ParquetFileWriter().WriteAsync(
            output, schema, [new ManagedColumnBatch(schema, [vector], 1)], CancellationToken.None);
        return output.ToArray();
    }

    // Seeds a FOREIGN CDF table whose v0 metadata declares a STRUCT-typed data column (its nested field names
    // carrying <paramref name="poison"/>) and whose v1 `cdc` file body carries the SAME column as a plain
    // string LEAF, then reads the change feed through the public door.
    // <paramref name="topLevelColumn"/> is the TOP-LEVEL data column name — a parameter and not the literal
    // "s" it used to be, because that hardcoded literal is exactly what left ChangeFeedReader:901/914
    // unpinned. <paramref name="omitColumnFromBody"/> writes a body carrying only `_change_type`, which is the
    // missing-data-column arm (:901).
    private static async Task<DeltaReadException> ReadPoisonedCdcAsync(
        string poison, string mode, string topLevelColumn = "s", bool omitColumnFromBody = false)
    {
        bool mapped = mode != "none";

        // A wide nested struct so the pre-fix render is a FLOOD, not merely an echo: 200 filler leaves plus the
        // poisoned one. Rendering this type recursively is exactly the unbounded behavior under test.
        var nested = new List<StructField>(201) { new(poison, DataTypes.StringType, nullable: true) };
        for (int i = 0; i < 200; i++)
        {
            nested.Add(new StructField(
                string.Create(CultureInfo.InvariantCulture, $"f{i}"), DataTypes.StringType, nullable: true));
        }

        // The MISSING arm declares the column as a plain leaf: the defect under test there is that the body
        // does not carry the column at all, and a struct declaration would trip the leaf-type arm first on any
        // body that did.
        DataType declaredType = omitColumnFromBody ? DataTypes.StringType : new StructType(nested);
        StructField declared = mapped
            ? new StructField(topLevelColumn, declaredType, nullable: true, FieldMetadata.FromValues(
            [
                new KeyValuePair<string, MetadataValue>(
                    ColumnMapping.PhysicalNameKey, MetadataValue.String(topLevelColumn)),
                new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(1)),
            ]))
            : new StructField(topLevelColumn, declaredType, nullable: true);
        string schemaJson = DeltaSchemaJson.ToJson(new StructType([declared]));

        // The cdc BODY: the data column as a string leaf (mapped modes additionally stamp field_id 1 on it) +
        // `_change_type`.
        StructField bodyColumn = mapped
            ? new StructField(topLevelColumn, DataTypes.StringType, nullable: true, FieldMetadata.FromValues(
                [new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(1))]))
            : new StructField(topLevelColumn, DataTypes.StringType, nullable: true);
        var changeTypeField = new StructField(
            ChangeDataWriter.ChangeTypeColumn, DataTypes.StringType, nullable: false);
        StructType bodySchema = omitColumnFromBody
            ? new StructType([changeTypeField])
            : new StructType([bodyColumn, changeTypeField]);
        MutableColumnVector value = ColumnVectors.Create(DataTypes.StringType, 1);
        MutableColumnVector changeType = ColumnVectors.Create(DataTypes.StringType, 1);
        value.AppendBytes(Encoding.UTF8.GetBytes("x"));
        changeType.AppendBytes(Encoding.UTF8.GetBytes(ChangeDataWriter.InsertChange));
        MutableColumnVector[] bodyVectors = omitColumnFromBody ? [changeType] : [value, changeType];

        byte[] parquetBytes;
        using (var buffer = new MemoryStream())
        {
            await new ParquetFileWriter().WriteAsync(
                buffer,
                bodySchema,
                [new ManagedColumnBatch(bodySchema, bodyVectors, 1)],
                CancellationToken.None);
            parquetBytes = buffer.ToArray();
        }

        string root = Path.Combine(Path.GetTempPath(), "sweep-cdf-" + Path.GetRandomFileName());
        try
        {
            const string relativePath = "_change_data/cdc-sweep.parquet";
            using (var backend = new Backends.LocalFileSystemBackend(root))
            {
                await backend.PutIfAbsentAsync(relativePath, parquetBytes, CancellationToken.None);

                string mappingConfig = mapped
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"\"delta.columnMapping.mode\":\"{mode}\","
                        + $"\"delta.columnMapping.maxColumnId\":\"1\",")
                    : string.Empty;
                string protocol = mapped
                    ? "{\"protocol\":{\"minReaderVersion\":3,\"minWriterVersion\":7,"
                        + "\"readerFeatures\":[\"columnMapping\"],"
                        + "\"writerFeatures\":[\"columnMapping\",\"changeDataFeed\"]}}"
                    : "{\"protocol\":{\"minReaderVersion\":1,\"minWriterVersion\":7,"
                        + "\"writerFeatures\":[\"changeDataFeed\"]}}";
                string metadata =
                    "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
                    + "\"schemaString\":" + JsonSerializer.Serialize(schemaJson)
                    + ",\"partitionColumns\":[],\"configuration\":{" + mappingConfig
                    + "\"delta.enableChangeDataFeed\":\"true\"}}}";
                await backend.PutIfAbsentAsync(
                    "_delta_log/00000000000000000000.json",
                    Encoding.UTF8.GetBytes(protocol + "\n" + metadata + "\n"),
                    CancellationToken.None);

                string cdcLine = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{{\"cdc\":{{\"path\":\"{relativePath}\",\"partitionValues\":{{}},"
                    + $"\"size\":{parquetBytes.Length},\"dataChange\":false}}}}");
                await backend.PutIfAbsentAsync(
                    "_delta_log/00000000000000000001.json",
                    Encoding.UTF8.GetBytes(cdcLine + "\n"),
                    CancellationToken.None);
            }

            using var source = DeltaReadSource.ForLocalPath(root);
            return await Assert.ThrowsAsync<DeltaReadException>(async () =>
            {
                DeltaChangeFeedInfo info = await source.LoadChangeFeedAsync(
                    DeltaChangeFeedRange.FromVersion(1, 1), CancellationToken.None);
                await foreach (ColumnBatch batch in source.ReadChangeBatchesAsync(info, CancellationToken.None))
                {
                }
            });
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
