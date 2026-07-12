using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

/// <summary>
/// Typed field-metadata interop (issue #330): <see cref="SchemaJson"/> must round-trip numeric,
/// boolean, nested-object, and array metadata values losslessly — including the Long-vs-Double
/// discrimination that keeps a column-mapping id an unquoted integer. The golden column-mapping and
/// identity schema strings are shared byte-for-byte with the Storage writer
/// (<c>DeltaSchemaJsonTypedMetadataTests</c>) so both serializers stay consistent.
/// </summary>
/// <remarks>
/// TRACKED DEFERRAL (#520): these goldens match the exact shape Spark's <c>DataType.json</c> emits
/// for a single field (field order <c>name/type/nullable/metadata</c>; struct <c>type/fields</c>;
/// empty metadata <c>{}</c>; <c>delta.columnMapping.id</c>/<c>.identity.start</c>/<c>.step</c> as
/// unquoted integers; <c>physicalName</c> a quoted string; <c>allowExplicitInsert</c> a bool). A
/// truly reference-engine-emitted <c>_delta_log</c> fixture (OR-b provenance) is tracked in #520.
/// </remarks>
public sealed class TypedFieldMetadataTests
{
    // Golden schema strings, in FieldMetadata's sorted-key order, asserted byte-for-byte here and by
    // the Storage-side DeltaSchemaJson test. A column-mapping id is an unquoted JSON integer and the
    // physicalName is a full engine-shaped col-<uuid> token (what Delta actually emits).
    public const string ColumnMappingGolden =
        "{\"type\":\"struct\",\"fields\":[" +
        "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":{" +
        "\"delta.columnMapping.id\":5,\"delta.columnMapping.physicalName\":" +
        "\"col-9f2c1e77-3b4a-4d21-8f0e-1a2b3c4d5e6f\"}}]}";

    public const string IdentityGolden =
        "{\"type\":\"struct\",\"fields\":[" +
        "{\"name\":\"seq\",\"type\":\"long\",\"nullable\":false,\"metadata\":{" +
        "\"delta.identity.allowExplicitInsert\":false,\"delta.identity.start\":1,\"delta.identity.step\":2}}]}";

    private static StructType ColumnMappingSchema() =>
        new(new[]
        {
            new StructField(
                "id",
                LongType.Instance,
                nullable: false,
                FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(5)),
                    new KeyValuePair<string, MetadataValue>(
                        "delta.columnMapping.physicalName",
                        MetadataValue.String("col-9f2c1e77-3b4a-4d21-8f0e-1a2b3c4d5e6f")),
                })),
        });

    private static StructType IdentitySchema() =>
        new(new[]
        {
            new StructField(
                "seq",
                LongType.Instance,
                nullable: false,
                FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>(
                        "delta.identity.allowExplicitInsert", MetadataValue.Boolean(false)),
                    new KeyValuePair<string, MetadataValue>("delta.identity.start", MetadataValue.Long(1)),
                    new KeyValuePair<string, MetadataValue>("delta.identity.step", MetadataValue.Long(2)),
                })),
        });

    // ---- AC2: column-mapping golden string ---------------------------------------------------------

    [Fact]
    public void ColumnMappingSchema_SerializesToGoldenString()
    {
        // Proves: id metadata is emitted as an unquoted JSON integer (never "5"), physicalName as a string.
        Assert.Equal(ColumnMappingGolden, SchemaJson.ToJson(ColumnMappingSchema()));
    }

    [Fact]
    public void ColumnMappingSchema_ParseThenSerialize_IsIdentity()
    {
        // Proves: an externally-written column-mapped schema string round-trips byte-for-byte.
        DataType parsed = SchemaJson.FromJson(ColumnMappingGolden);
        Assert.Equal(ColumnMappingGolden, SchemaJson.ToJson(parsed));

        var structType = Assert.IsType<StructType>(parsed);
        MetadataValue id = structType["id"].Metadata["delta.columnMapping.id"];
        Assert.Equal(MetadataValueKind.Long, id.Kind);
        Assert.Equal(5L, id.AsLong());
        // The key-based typed getter is the ergonomic one-call read for a numeric id.
        Assert.True(structType["id"].Metadata.TryGetLong("delta.columnMapping.id", out long idViaKey));
        Assert.Equal(5L, idViaKey);
        Assert.True(structType["id"].Metadata.TryGetString("delta.columnMapping.physicalName", out string? physical));
        Assert.Equal("col-9f2c1e77-3b4a-4d21-8f0e-1a2b3c4d5e6f", physical);
    }

    // ---- AC3: identity-column golden string --------------------------------------------------------

    [Fact]
    public void IdentitySchema_SerializesToGoldenString()
    {
        // Proves: identity start/step stay unquoted integers and allowExplicitInsert stays a JSON bool.
        Assert.Equal(IdentityGolden, SchemaJson.ToJson(IdentitySchema()));
    }

    [Fact]
    public void IdentitySchema_ParseThenSerialize_IsIdentity()
    {
        DataType parsed = SchemaJson.FromJson(IdentityGolden);
        Assert.Equal(IdentityGolden, SchemaJson.ToJson(parsed));

        FieldMetadata metadata = ((StructType)parsed)["seq"].Metadata;
        Assert.Equal(MetadataValueKind.Boolean, metadata["delta.identity.allowExplicitInsert"].Kind);
        Assert.False(metadata["delta.identity.allowExplicitInsert"].AsBoolean());
        Assert.Equal(1L, metadata["delta.identity.start"].AsLong());
        Assert.Equal(2L, metadata["delta.identity.step"].AsLong());
    }

    // ---- AC1: Long-vs-Double discrimination + arrays + nested --------------------------------------

    [Theory]
    [InlineData("5", MetadataValueKind.Long)]
    [InlineData("-42", MetadataValueKind.Long)]
    [InlineData("5.0", MetadataValueKind.Double)]
    [InlineData("5.5", MetadataValueKind.Double)]
    [InlineData("1e3", MetadataValueKind.Double)]
    [InlineData("100000000000000000000", MetadataValueKind.Double)] // > Int64 range
    public void JsonNumber_IsDiscriminatedLikeSpark(string numberLiteral, MetadataValueKind expected)
    {
        // Finite integral-in-Int64 => Long; fractional/exponent => Double: this matches Spark.
        // NOTE the > Int64 case is deliberately NOT strict Spark parity: Spark's json4s parses an
        // out-of-range integer via BigInt.toLong, which SILENTLY WRAPS (mod 2^64) to a bogus Long.
        // DeltaSharp instead promotes it to a Double — lossy in mantissa but non-wrapping, so it is
        // safer and clearly different; do not read this as bit-for-bit Spark behavior.
        string json =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"f\",\"type\":\"long\",\"nullable\":true,"
            + "\"metadata\":{\"k\":" + numberLiteral + "}}]}";

        MetadataValue value = ((StructType)SchemaJson.FromJson(json))["f"].Metadata["k"];
        Assert.Equal(expected, value.Kind);
    }

    [Fact]
    public void IntegralDouble_RoundTripsAsDouble_NotLong()
    {
        // Double(5.0) must serialize with a fractional part so it never re-reads as Long(5).
        var schema = new StructType(new[]
        {
            new StructField(
                "d",
                LongType.Instance,
                metadata: FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>("k", MetadataValue.Double(5.0)),
                })),
        });

        string json = SchemaJson.ToJson(schema);
        Assert.Contains("\"k\":5.0", json, StringComparison.Ordinal);

        MetadataValue reread = ((StructType)SchemaJson.FromJson(json))["d"].Metadata["k"];
        Assert.Equal(MetadataValueKind.Double, reread.Kind);
        Assert.Equal(5.0, reread.AsDouble());
    }

    [Fact]
    public void NestedObjectAndArray_RoundTripLosslessly()
    {
        FieldMetadata nested = FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("inner", MetadataValue.Long(7)),
        });

        var array = MetadataValue.Array(new[]
        {
            MetadataValue.Long(1),
            MetadataValue.String("two"),
            MetadataValue.Boolean(true),
            MetadataValue.Null,
        });

        var schema = new StructType(new[]
        {
            new StructField(
                "f",
                LongType.Instance,
                metadata: FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>("arr", array),
                    new KeyValuePair<string, MetadataValue>("obj", MetadataValue.Nested(nested)),
                })),
        });

        var roundTripped = (StructType)SchemaJson.FromJson(SchemaJson.ToJson(schema));
        Assert.Equal(schema, roundTripped);

        FieldMetadata meta = roundTripped["f"].Metadata;
        Assert.Equal(array, meta["arr"]);
        Assert.Equal(7L, meta["obj"].AsNested()["inner"].AsLong());
    }

    [Fact]
    public void FromJson_NonFiniteMetadataNumber_IsRejected()
    {
        // Item 5: a poisoned schemaString whose number overflows to ±Infinity (1e400) parses via
        // GetDouble to a non-finite value that JSON cannot represent. Fail closed at the untrusted
        // READ boundary with a typed SchemaValidationException rather than accepting a value that
        // would later throw a raw ArgumentException on re-serialize.
        const string json =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"f\",\"type\":\"long\",\"nullable\":true,"
            + "\"metadata\":{\"k\":1e400}}]}";

        Assert.Throws<SchemaValidationException>(() => SchemaJson.FromJson(json));
    }

    [Fact]
    public void FromJson_MetadataNestedBeyondDepthBound_IsRejected()
    {
        // Item 6: the parse depth bound (JsonDocument MaxDepth = 64) is explicit and pinned. A
        // metadata object nested well past 64 levels must fail closed with SchemaValidationException.
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"type\":\"struct\",\"fields\":[{\"name\":\"f\",\"type\":\"long\","
            + "\"nullable\":true,\"metadata\":");
        const int depth = 200;
        for (int i = 0; i < depth; i++)
        {
            sb.Append("{\"n\":");
        }

        sb.Append('1');
        for (int i = 0; i < depth; i++)
        {
            sb.Append('}');
        }

        sb.Append("}]}");

        Assert.Throws<SchemaValidationException>(() => SchemaJson.FromJson(sb.ToString()));
    }

    [Fact]
    public void EmptyArrayAndEmptyNestedObject_RoundTrip()
    {
        // Item 10: an empty array and an empty nested object round-trip losslessly.
        var schema = new StructType(new[]
        {
            new StructField(
                "f",
                LongType.Instance,
                metadata: FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>(
                        "emptyArr", MetadataValue.Array(System.Array.Empty<MetadataValue>())),
                    new KeyValuePair<string, MetadataValue>(
                        "emptyObj", MetadataValue.Nested(FieldMetadata.Empty)),
                })),
        });

        string json = SchemaJson.ToJson(schema);
        Assert.Contains("\"emptyArr\":[]", json, StringComparison.Ordinal);
        Assert.Contains("\"emptyObj\":{}", json, StringComparison.Ordinal);

        var roundTripped = (StructType)SchemaJson.FromJson(json);
        Assert.Equal(schema, roundTripped);
        Assert.Empty(roundTripped["f"].Metadata["emptyArr"].AsArray());
        Assert.True(roundTripped["f"].Metadata["emptyObj"].AsNested().IsEmpty);
    }

    [Fact]
    public void ArrayOfNested_RoundTrips()
    {
        // Item 10: an array whose elements are nested metadata objects round-trips losslessly.
        var array = MetadataValue.Array(new[]
        {
            MetadataValue.Nested(FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>("a", MetadataValue.Long(1)),
            })),
            MetadataValue.Nested(FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>("b", MetadataValue.Boolean(true)),
            })),
        });

        var schema = new StructType(new[]
        {
            new StructField(
                "f",
                LongType.Instance,
                metadata: FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>("arr", array),
                })),
        });

        var roundTripped = (StructType)SchemaJson.FromJson(SchemaJson.ToJson(schema));
        Assert.Equal(schema, roundTripped);

        IReadOnlyList<MetadataValue> reread = roundTripped["f"].Metadata["arr"].AsArray();
        Assert.Equal(1L, reread[0].AsNested()["a"].AsLong());
        Assert.True(reread[1].AsNested()["b"].AsBoolean());
    }

    [Fact]
    public void MalformedMetadataJson_Throws()
    {
        // Genuinely invalid JSON is still rejected precisely.
        Assert.Throws<SchemaValidationException>(() =>
            SchemaJson.FromJson("{\"type\":\"struct\",\"fields\":[{\"name\":\"f\",\"type\":\"long\","
                + "\"nullable\":true,\"metadata\":\"not-an-object\"}]}"));
    }

    // ---- FieldMetadata key-based typed getters (Spark Metadata parity) -----------------------------

    [Fact]
    public void FieldMetadata_KeyGetters_ReturnTypedValue_WhenKindMatches()
    {
        FieldMetadata metadata = FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(7)),
            new KeyValuePair<string, MetadataValue>("delta.identity.step", MetadataValue.Double(1.5)),
            new KeyValuePair<string, MetadataValue>("delta.identity.allowExplicitInsert", MetadataValue.Boolean(true)),
            new KeyValuePair<string, MetadataValue>("comment", MetadataValue.String("hi")),
        });

        Assert.True(metadata.TryGetLong("delta.columnMapping.id", out long id));
        Assert.Equal(7L, id);
        Assert.True(metadata.TryGetDouble("delta.identity.step", out double step));
        Assert.Equal(1.5, step);
        Assert.True(metadata.TryGetBoolean("delta.identity.allowExplicitInsert", out bool allow));
        Assert.True(allow);
        Assert.True(metadata.TryGetString("comment", out string? comment));
        Assert.Equal("hi", comment);
    }

    [Fact]
    public void FieldMetadata_KeyGetters_ReturnFalse_OnMissingKeyOrWrongKind()
    {
        FieldMetadata metadata = FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("id", MetadataValue.Long(7)),
        });

        Assert.False(metadata.TryGetLong("absent", out long missing));
        Assert.Equal(0L, missing);
        // Present but wrong kind: a Long is not a Boolean/Double/String.
        Assert.False(metadata.TryGetBoolean("id", out bool wrongKind));
        Assert.False(wrongKind);
        Assert.False(metadata.TryGetString("id", out string? notString));
        Assert.Null(notString);
    }

    [Fact]
    public void FieldMetadata_FromValues_NullValue_ThrowsArgumentNullException()
    {
        // Convention: a null key/value/array-element is an ArgumentNullException across the type
        // (consistent with MetadataValue.Array and BCL dictionary null-key handling).
        Assert.Throws<ArgumentNullException>(() =>
            FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>("k", null!),
            }));
    }

    [Fact]
    public void FromJson_NonFiniteMetadataNumber_TruncatesEchoedLiteral()
    {
        // A pathological multi-KB numeric literal is bounded in the diagnostic, not echoed whole.
        string hugeLiteral = "1" + new string('0', 5000) + "e9"; // overflows to +Infinity
        string json = "{\"type\":\"struct\",\"fields\":[{\"name\":\"f\",\"type\":\"long\","
            + "\"nullable\":true,\"metadata\":{\"k\":" + hugeLiteral + "}}]}";

        var ex = Assert.Throws<SchemaValidationException>(() => SchemaJson.FromJson(json));
        Assert.Contains("is not finite", ex.Message, StringComparison.Ordinal);
        Assert.Contains("…", ex.Message, StringComparison.Ordinal);
        // The full 5000-digit literal must not be echoed.
        Assert.True(ex.Message.Length < 200, $"message unexpectedly long: {ex.Message.Length}");
    }
}
