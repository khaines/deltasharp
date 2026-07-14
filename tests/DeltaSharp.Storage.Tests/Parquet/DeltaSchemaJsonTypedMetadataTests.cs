using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Typed field-metadata interop (issue #330): the Storage-side <see cref="DeltaSchemaJson"/> writer
/// must serialize typed metadata (numeric column-mapping ids, identity numbers/booleans) exactly like
/// the engine's <c>SchemaJson</c>. Since #330's round-1 fix, both writers call the SAME shared
/// <c>SchemaJson.WriteMetadataValue</c> in <c>DeltaSharp.Abstractions</c>, so the parity is
/// structural rather than a coincidence of two duplicated literals — asserted directly in
/// <see cref="BothWriters_EmitByteIdenticalMetadata_ForEveryValueKind"/>.
/// </summary>
public sealed class DeltaSchemaJsonTypedMetadataTests
{
    // Byte-for-byte identical to TypedFieldMetadataTests.ColumnMappingGolden / IdentityGolden.
    private const string ColumnMappingGolden =
        "{\"type\":\"struct\",\"fields\":[" +
        "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":{" +
        "\"delta.columnMapping.id\":5,\"delta.columnMapping.physicalName\":" +
        "\"col-9f2c1e77-3b4a-4d21-8f0e-1a2b3c4d5e6f\"}}]}";

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
                        "delta.columnMapping.physicalName",
                        MetadataValue.String("col-9f2c1e77-3b4a-4d21-8f0e-1a2b3c4d5e6f")),
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

    [Fact]
    public void BothWriters_EmitByteIdenticalMetadata_ForEveryValueKind()
    {
        // DIRECT parity: the engine's internal SchemaJson (visible here via InternalsVisibleTo) and
        // the Storage DeltaSchemaJson must emit byte-identical output for a schema whose metadata
        // exercises every MetadataValueKind — string, long, double, bool, null, array, and nested
        // object. This proves the shared-writer collapse (no drift) rather than trusting a copied
        // literal. Field types stay atomic/struct so DeltaSchemaJson.WriteType's tracked complex-type
        // deferral (#518) does not enter the comparison.
        var nested = FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("inner.long", MetadataValue.Long(42)),
            new KeyValuePair<string, MetadataValue>("inner.str", MetadataValue.String("nested")),
        });

        var array = MetadataValue.Array(new[]
        {
            MetadataValue.Long(1),
            MetadataValue.Double(2.5),
            MetadataValue.Boolean(true),
            MetadataValue.String("s"),
            MetadataValue.Null,
        });

        var schema = new StructType(new[]
        {
            new StructField(
                "everything",
                DataTypes.LongType,
                nullable: false,
                FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>("k.arr", array),
                    new KeyValuePair<string, MetadataValue>("k.bool", MetadataValue.Boolean(false)),
                    new KeyValuePair<string, MetadataValue>("k.double", MetadataValue.Double(3.0)),
                    new KeyValuePair<string, MetadataValue>("k.long", MetadataValue.Long(7)),
                    new KeyValuePair<string, MetadataValue>("k.nested", MetadataValue.Nested(nested)),
                    new KeyValuePair<string, MetadataValue>("k.null", MetadataValue.Null),
                    new KeyValuePair<string, MetadataValue>("k.string", MetadataValue.String("text")),
                })),
        });

        string engineJson = SchemaJson.ToJson(schema);
        string storageJson = DeltaSchemaJson.ToJson(schema);

        Assert.Equal(engineJson, storageJson);
    }

    [Fact]
    public void TypeChangesMetadata_RoundTripsThroughSchemaJson()
    {
        // #495: a widened field carries delta.typeChanges (a JSON array of {fromType,toType} objects). It
        // must serialize AND parse back with the exact array-of-nested shape and key/value strings so a
        // reader can promote (Delta PROTOCOL.md "Type Change Metadata").
        var typeChanges = MetadataValue.Array(new[]
        {
            MetadataValue.Nested(FieldMetadata.FromEntries(new[]
            {
                new KeyValuePair<string, string>("fromType", "short"),
                new KeyValuePair<string, string>("toType", "integer"),
            })),
            MetadataValue.Nested(FieldMetadata.FromEntries(new[]
            {
                new KeyValuePair<string, string>("fromType", "integer"),
                new KeyValuePair<string, string>("toType", "long"),
            })),
        });
        var schema = new StructType(new[]
        {
            new StructField(
                "value",
                DataTypes.LongType,
                nullable: true,
                FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>("delta.typeChanges", typeChanges),
                })),
        });

        string json = DeltaSchemaJson.ToJson(schema);
        Assert.Contains("\"delta.typeChanges\":[{\"fromType\":\"short\",\"toType\":\"integer\"},{\"fromType\":\"integer\",\"toType\":\"long\"}]", json, StringComparison.Ordinal);

        var parsed = (StructType)SchemaJson.FromJson(json);
        StructField field = parsed["value"];
        Assert.True(field.Metadata.TryGetValue("delta.typeChanges", out MetadataValue? changes));
        Assert.True(changes!.TryGetArray(out IReadOnlyList<MetadataValue>? entries));
        Assert.Equal(2, entries!.Count);

        Assert.True(entries[0].TryGetNested(out FieldMetadata? first));
        Assert.True(first!.TryGetString("fromType", out string? f0));
        Assert.True(first.TryGetString("toType", out string? t0));
        Assert.Equal("short", f0);
        Assert.Equal("integer", t0);

        Assert.True(entries[1].TryGetNested(out FieldMetadata? second));
        Assert.True(second!.TryGetString("fromType", out string? f1));
        Assert.True(second.TryGetString("toType", out string? t1));
        Assert.Equal("integer", f1);
        Assert.Equal("long", t1);
    }
}
