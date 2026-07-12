using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Typed field-metadata interop (issue #330): the Storage-side <see cref="DeltaSchemaJson"/> writer
/// must serialize typed metadata (numeric column-mapping ids, identity numbers/booleans) exactly like
/// the engine's <c>SchemaJson</c>. The golden strings below are duplicated byte-for-byte from
/// Engine.Tests' <c>TypedFieldMetadataTests</c> (the two projects cannot see each other's internal
/// serializer), so asserting both writers reproduce the same literal proves they stay consistent.
/// </summary>
public sealed class DeltaSchemaJsonTypedMetadataTests
{
    // Byte-for-byte identical to TypedFieldMetadataTests.ColumnMappingGolden / IdentityGolden.
    private const string ColumnMappingGolden =
        "{\"type\":\"struct\",\"fields\":[" +
        "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":{" +
        "\"delta.columnMapping.id\":5,\"delta.columnMapping.physicalName\":\"col-a1b2c3\"}}]}";

    private const string IdentityGolden =
        "{\"type\":\"struct\",\"fields\":[" +
        "{\"name\":\"seq\",\"type\":\"long\",\"nullable\":false,\"metadata\":{" +
        "\"delta.identity.allowExplicitInsert\":false,\"delta.identity.start\":1,\"delta.identity.step\":2}}]}";

    [Fact]
    public void ColumnMappingSchema_SerializesToSameGoldenString_AsEngineWriter()
    {
        var schema = new StructType(new[]
        {
            new StructField(
                "id",
                DataTypes.LongType,
                nullable: false,
                FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(5)),
                    new KeyValuePair<string, MetadataValue>(
                        "delta.columnMapping.physicalName", MetadataValue.String("col-a1b2c3")),
                })),
        });

        Assert.Equal(ColumnMappingGolden, DeltaSchemaJson.ToJson(schema));
    }

    [Fact]
    public void IdentitySchema_SerializesToSameGoldenString_AsEngineWriter()
    {
        var schema = new StructType(new[]
        {
            new StructField(
                "seq",
                DataTypes.LongType,
                nullable: false,
                FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>(
                        "delta.identity.allowExplicitInsert", MetadataValue.Boolean(false)),
                    new KeyValuePair<string, MetadataValue>("delta.identity.start", MetadataValue.Long(1)),
                    new KeyValuePair<string, MetadataValue>("delta.identity.step", MetadataValue.Long(2)),
                })),
        });

        Assert.Equal(IdentityGolden, DeltaSchemaJson.ToJson(schema));
    }

    [Fact]
    public void IntegralDouble_KeepsFractionalPart()
    {
        var schema = new StructType(new[]
        {
            new StructField(
                "d",
                DataTypes.LongType,
                metadata: FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>("k", MetadataValue.Double(5.0)),
                })),
        });

        Assert.Contains("\"k\":5.0", DeltaSchemaJson.ToJson(schema), StringComparison.Ordinal);
    }
}
