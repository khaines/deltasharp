using System.Text.Json;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// <see cref="DeltaSchemaJson"/> must serialize field metadata faithfully (M7) — matching the engine's
/// <c>SchemaJson.WriteMetadata</c> shape (a JSON object of string key/values in sorted key order) —
/// rather than emitting an always-empty <c>metadata</c> object. (D-6 follow-up: once the engine's
/// serializer is shared/relocated out of its assembly, this layer should call it directly.)
/// </summary>
public sealed class DeltaSchemaJsonTests
{
    [Fact]
    public void ToJson_SerializesFieldMetadataInSortedOrder()
    {
        FieldMetadata metadata = FieldMetadata.FromEntries(new[]
        {
            new KeyValuePair<string, string>("unit", "kg"),
            new KeyValuePair<string, string>("comment", "the mass"),
        });

        var schema = new StructType(new[]
        {
            new StructField("mass", DataTypes.DoubleType, nullable: true, metadata),
            new StructField("id", DataTypes.LongType, nullable: false),
        });

        string json = DeltaSchemaJson.ToJson(schema);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement fields = document.RootElement.GetProperty("fields");

        JsonElement massMetadata = fields[0].GetProperty("metadata");
        Assert.Equal("the mass", massMetadata.GetProperty("comment").GetString());
        Assert.Equal("kg", massMetadata.GetProperty("unit").GetString());

        // Deterministic sorted key order: "comment" is serialized before "unit".
        Assert.True(
            json.IndexOf("\"comment\"", StringComparison.Ordinal) < json.IndexOf("\"unit\"", StringComparison.Ordinal),
            "Field metadata keys must serialize in sorted order.");

        // A field without metadata serializes an empty object (not a dropped property).
        JsonElement idMetadata = fields[1].GetProperty("metadata");
        Assert.Equal(JsonValueKind.Object, idMetadata.ValueKind);
        Assert.Empty(idMetadata.EnumerateObject().ToArray());
    }

    [Fact]
    public void ToJson_EmitsSparkStructShapeForAtomicFields()
    {
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("amount", DataTypes.CreateDecimalType(10, 2), nullable: true),
        });

        string json = DeltaSchemaJson.ToJson(schema);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("struct", document.RootElement.GetProperty("type").GetString());
        JsonElement fields = document.RootElement.GetProperty("fields");
        Assert.Equal("id", fields[0].GetProperty("name").GetString());
        Assert.Equal("long", fields[0].GetProperty("type").GetString());
        Assert.False(fields[0].GetProperty("nullable").GetBoolean());
        Assert.Equal("decimal(10,2)", fields[1].GetProperty("type").GetString());
        Assert.True(fields[1].GetProperty("nullable").GetBoolean());
    }
}
