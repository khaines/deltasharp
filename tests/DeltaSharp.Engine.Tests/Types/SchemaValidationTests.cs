using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

public class SchemaValidationTests
{
    [Fact]
    public void Struct_RejectsDuplicateFieldNames_WithPreciseMessage()
    {
        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() =>
            new StructType(new[]
            {
                new StructField("id", IntegerType.Instance),
                new StructField("name", StringType.Instance),
                new StructField("id", LongType.Instance),
            }));

        Assert.Contains("id", ex.Message);
        Assert.Contains("0", ex.Message);
        Assert.Contains("2", ex.Message);
    }

    // #705/#707 (Round 4): the duplicate-name throw renders DiagnosticText.Sanitize(field.Name), NOT the raw
    // name — the same rule the sibling TypeCoercionException.ForPath pins hold for the coercion message (see
    // TypeCoercionTests). The producer is reachable from Snapshot.ParseSchema → SchemaJson.FromJson over an
    // attacker-influenceable metaData.schemaString in _delta_log, and it sits OUTSIDE the Storage
    // source-scan guard's root, so only a behavioural pin can catch a silent revert to '{field.Name}'.
    [Fact]
    public void Struct_DuplicateFieldName_MessageEchoesSanitizedName_NotTheRawOne()
    {
        // A duplicated name carrying CRLF (log injection) and an oversized run (unbounded log line).
        string hostile = "dup\r\nINJECTED" + new string('x', 200);

        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() =>
            new StructType(new[]
            {
                new StructField(hostile, IntegerType.Instance),
                new StructField(hostile, LongType.Instance),
            }));

        // The rule restated independently of the primitive: every control character becomes U+FFFD and the
        // token is capped at 128 retained characters with a trailing ellipsis.
        const string SanitizedHead = "dup\uFFFD\uFFFDINJECTED";
        string expected = SanitizedHead + new string('x', 128 - SanitizedHead.Length) + "…";

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(hostile, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 200), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);

        // The diagnostic value of the message — which two positions collided — survives sanitization.
        Assert.Contains("0 and 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_AllowsCaseDifferingFieldNames()
    {
        // Spark parity: case-only ambiguity is a name-resolution concern, not a type error.
        var type = new StructType(new[]
        {
            new StructField("id", IntegerType.Instance),
            new StructField("ID", LongType.Instance),
        });

        Assert.Equal(2, type.Count);
    }

    [Fact]
    public void Map_RejectsNullTypeKey()
    {
        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() =>
            new MapType(NullType.Instance, IntegerType.Instance));
        Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_RejectsMapTypeKey()
    {
        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() =>
            new MapType(new MapType(StringType.Instance, IntegerType.Instance), IntegerType.Instance));
        Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // #705/#707 (Round 4): the unsupported-key throw renders the bounded keyType.TypeName (a fixed literal
    // such as 'map'/'void'), NOT keyType.SimpleString — which recursively walks nested StructType field names
    // and would echo a foreign name, control characters and all, into the message. Same adjudication as the
    // sibling TypeCoercionException.ForPath pins: bounded KIND on the message, exact type off it.
    [Fact]
    public void Map_UnsupportedKeyType_MessageEchoesBoundedTypeName_NotRecursiveFieldNames()
    {
        var hostileKey = new MapType(
            StringType.Instance,
            new StructType(new[] { new StructField("ssn_col\r\nINJECTED", StringType.Instance) }));

        // Non-vacuity: SimpleString really does carry the nested foreign field name (and its CRLF), so the
        // assertions below discriminate between the two renderings rather than restating a tautology.
        Assert.Contains("ssn_col\r\nINJECTED", hostileKey.SimpleString, StringComparison.Ordinal);

        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() =>
            new MapType(hostileKey, IntegerType.Instance));

        Assert.Contains("'map'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ssn_col", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.True(ex.Message.Length < 256, $"message was {ex.Message.Length} chars; expected bounded.");
    }

    // ROUND 5 — the SIBLING ingestion producers. SchemaJson.FromJson parses an attacker-influenceable
    // metaData.schemaString out of _delta_log; four of its throw sites echoed the raw, unbounded 'name'/
    // 'kind' token straight into SchemaValidationException.Message (ReadType's unknown complex kind,
    // ParseNamedType's unknown type name, and BOTH ParseDecimal malformed-decimal throws — the
    // trailing-garbage site and the unparseable-precision/scale site, each pinned by its own row below).
    //
    // WHY sanitize at the PRODUCER (Round-6 correction — the earlier note here over-claimed the route).
    // Every current SchemaJson.FromJson caller wraps the SchemaValidationException in a FIXED-message outer
    // (Snapshot.ParseSchema and DeltaCommitter.ParseCommittedSchema → DeltaProtocolException.Inconsistent;
    // ChangeFeedReader → DeltaReadException; DeltaLog → DeltaProtocolException.Malformed, reached
    // transitively via ColumnMappingIdentity.FromMetadata whose callers all wrap — all with fixed text), so
    // for THESE producers the raw token reaches only InnerException — the raw-inner channel ratified in
    // #744, further suppressed for rendering by DeltaReadException.ToString() → DescribeWithoutInner.
    // Sanitizing here therefore
    // (a) minimizes the tenant payload that lands in that #744 raw-inner sink, shrinking its retention and
    // erasure scope, and (b) removes the dependence on every current AND future FromJson caller remembering
    // to wrap with fixed text. DeltaReadSource's direct lift (`throw new DeltaReadException(ex.Message, ex)`,
    // DeltaReadSource.cs:177) IS live, but for this catch it carries SchemaValidationExceptions originating
    // in ColumnMappingProjection (new StructType, ColumnMappingProjection.cs:92) — the StructType/MapType
    // producers already sanitized in Round 4 — not these SchemaJson ones. These pins drive FromJson
    // end-to-end (not the private helpers) so
    // a revert to '{name}' or '{kind}' at ANY of the four sites turns the matching case RED.
    private const int SanitizeCap = 128;

    private static string ExpectedSanitized(string raw)
    {
        // The rule restated independently of DiagnosticText: every control character becomes U+FFFD and the
        // token is capped at 128 retained characters with a trailing ellipsis.
        string neutralized = raw.Replace("\r", "\uFFFD", StringComparison.Ordinal)
            .Replace("\n", "\uFFFD", StringComparison.Ordinal)
            .Replace("\t", "\uFFFD", StringComparison.Ordinal);
        return neutralized.Length <= SanitizeCap ? neutralized : neutralized[..SanitizeCap] + "…";
    }

    private static string JsonString(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    [Fact]
    public void SchemaJson_UnknownTypeName_MessageEchoesSanitizedName_NotTheRawOne()
    {
        string hostile = "weird\r\nINJECTED" + new string('x', 200);
        string json = "{\"type\":\"struct\",\"fields\":[{\"name\":\"c\",\"type\":" + JsonString(hostile)
            + ",\"nullable\":true,\"metadata\":{}}]}";

        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() => SchemaJson.FromJson(json));

        Assert.Contains(ExpectedSanitized(hostile), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(hostile, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 200), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.Contains("Unknown type name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaJson_UnknownComplexTypeKind_MessageEchoesSanitizedKind_NotTheRawOne()
    {
        string hostile = "bogus\r\nKIND" + new string('y', 200);
        string json = "{\"type\":" + JsonString(hostile) + "}";

        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() => SchemaJson.FromJson(json));

        Assert.Contains(ExpectedSanitized(hostile), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(hostile, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('y', 200), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.Contains("Unknown complex type kind", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // BOTH ParseDecimal throw sites, each reached by construction rather than by hope. Site #1 (the
    // trailing-garbage guard, `close != name.Length - 1`) fires when the closing paren is NOT the final
    // character; site #2 (the precision/scale guard) fires only when the paren IS final and the
    // comma-delimited body fails int.TryParse.
    //
    // ROUND-6 FIX. The Round-5 rows appended the 200-character filler AFTER the whole name, which forced
    // `close != name.Length - 1` — so BOTH rows hit site #1 and site #2's Sanitize(name) was UNPINNED (it
    // could be reverted to a raw '{name}' with the suite still green). The filler is now injected BETWEEN a
    // prefix and a suffix, so the third row keeps ')' as the FINAL character and lands on site #2.
    //                    prefix                      suffix   site
    [InlineData("decimal(10,2)\r\nTRAILING", "", false)]     // #1 — paren is not final
    [InlineData("decimal(9\r\n9,2GARBAGE)", "", false)]      // #1 — filler lands after the ')'
    [InlineData("decimal(9\r\n9", ",2)", true)]              // #2 — ')' IS final; "9\r\n9…" fails int.TryParse
    public void SchemaJson_MalformedDecimal_MessageEchoesSanitizedName_NotTheRawOne(
        string prefix,
        string suffix,
        bool closingParenIsFinal)
    {
        string hostile = prefix + new string('z', 200) + suffix;

        // Non-vacuity / routing pin: mirrors ParseDecimal's ACTUAL site selector
        // (`close != name.Length - 1`, where `close = name.IndexOf(')')`) rather than an EndsWith
        // approximation, so a future row carrying an INTERIOR ')' cannot silently re-route to site #1 with
        // this pin still green.
        Assert.Equal(closingParenIsFinal, hostile.IndexOf(')') == hostile.Length - 1);

        string json = "{\"type\":\"struct\",\"fields\":[{\"name\":\"c\",\"type\":" + JsonString(hostile)
            + ",\"nullable\":true,\"metadata\":{}}]}";

        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() => SchemaJson.FromJson(json));

        Assert.Contains(ExpectedSanitized(hostile), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(hostile, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('z', 200), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.Contains("Malformed decimal type", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // Sanitizing must be a NO-OP on clean, short input: the ordinary diagnostic text is byte-identical, so
    // the fix costs no diagnosability. (Non-vacuity for the hostile rows above.)
    [InlineData("\"decimal(10,2) junk\"", "Malformed decimal type 'decimal(10,2) junk'.")]
    [InlineData("\"decimal(a,b)\"", "Malformed decimal type 'decimal(a,b)'.")]
    [InlineData("\"int32\"", "Unknown type name 'int32'.")]
    [InlineData("{\"type\":\"tuple\"}", "Unknown complex type kind 'tuple'.")]
    public void SchemaJson_CleanToken_MessageIsUnchangedBySanitizing(string typeJson, string expected)
    {
        string json = "{\"type\":\"struct\",\"fields\":[{\"name\":\"c\",\"type\":" + typeJson
            + ",\"nullable\":true,\"metadata\":{}}]}";

        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() => SchemaJson.FromJson(json));

        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public void SchemaJson_ValidDecimalName_StillParses()
    {
        Assert.Equal(new DecimalType(10, 2), SchemaJson.FromJson("\"decimal(10,2)\""));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(39, 0)]
    [InlineData(-1, 0)]
    public void Decimal_RejectsPrecisionOutOfRange(int precision, int scale)
    {
        Assert.Throws<SchemaValidationException>(() => new DecimalType(precision, scale));
    }

    [Theory]
    [InlineData(10, -1)]
    [InlineData(10, 11)]
    public void Decimal_RejectsScaleOutOfRange(int precision, int scale)
    {
        Assert.Throws<SchemaValidationException>(() => new DecimalType(precision, scale));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(38, 38)]
    [InlineData(10, 2)]
    public void Decimal_AcceptsValidPrecisionAndScale(int precision, int scale)
    {
        var type = new DecimalType(precision, scale);
        Assert.Equal(precision, type.Precision);
        Assert.Equal(scale, type.Scale);
    }

    [Fact]
    public void StructField_RejectsNullOrEmptyName()
    {
        Assert.Throws<ArgumentNullException>(() => new StructField(null!, IntegerType.Instance));
        Assert.Throws<ArgumentException>(() => new StructField(string.Empty, IntegerType.Instance));
    }

    [Fact]
    public void StructField_RejectsNullType()
    {
        Assert.Throws<ArgumentNullException>(() => new StructField("x", null!));
    }

    [Fact]
    public void Array_RejectsNullElementType()
    {
        Assert.Throws<ArgumentNullException>(() => new ArrayType(null!));
    }

    [Fact]
    public void Map_RejectsNullKeyOrValueType()
    {
        Assert.Throws<ArgumentNullException>(() => new MapType(null!, IntegerType.Instance));
        Assert.Throws<ArgumentNullException>(() => new MapType(StringType.Instance, null!));
    }

    [Fact]
    public void Struct_RejectsNullFieldsArgument()
    {
        Assert.Throws<ArgumentNullException>(() => new StructType(null!));
    }

    [Fact]
    public void Metadata_RejectsNullKeyOrValue()
    {
        // A null key/value is an ArgumentNullException (consistent with MetadataValue.Array and
        // BCL dictionary null-key handling); ArgumentNullException derives from ArgumentException,
        // so callers catching the base type are unaffected.
        Assert.Throws<ArgumentNullException>(() =>
            FieldMetadata.FromEntries(new[] { new KeyValuePair<string, string>(null!, "v") }));
        Assert.Throws<ArgumentNullException>(() =>
            FieldMetadata.FromEntries(new[] { new KeyValuePair<string, string>("k", null!) }));
    }
}
