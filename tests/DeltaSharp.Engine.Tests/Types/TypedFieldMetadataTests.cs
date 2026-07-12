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
public sealed class TypedFieldMetadataTests
{
    // Golden schema strings, in FieldMetadata's sorted-key order, asserted byte-for-byte here and by
    // the Storage-side DeltaSchemaJson test. A column-mapping id is an unquoted JSON integer.
    public const string ColumnMappingGolden =
        "{\"type\":\"struct\",\"fields\":[" +
        "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":{" +
        "\"delta.columnMapping.id\":5,\"delta.columnMapping.physicalName\":\"col-a1b2c3\"}}]}";

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
                        "delta.columnMapping.physicalName", MetadataValue.String("col-a1b2c3")),
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
        Assert.True(structType["id"].Metadata.TryGetString("delta.columnMapping.physicalName", out string? physical));
        Assert.Equal("col-a1b2c3", physical);
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
    [InlineData("100000000000000000000", MetadataValueKind.Double)] // out of Int64 range
    public void JsonNumber_IsDiscriminatedLikeSpark(string numberLiteral, MetadataValueKind expected)
    {
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
    public void MalformedMetadataJson_Throws()
    {
        // Genuinely invalid JSON is still rejected precisely.
        Assert.Throws<SchemaValidationException>(() =>
            SchemaJson.FromJson("{\"type\":\"struct\",\"fields\":[{\"name\":\"f\",\"type\":\"long\","
                + "\"nullable\":true,\"metadata\":\"not-an-object\"}]}"));
    }
}
