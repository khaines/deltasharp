using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// #518: the Storage-side <see cref="DeltaSchemaJson"/> that stamps the Parquet footer schema
/// (<c>org.apache.spark.sql.parquet.row.metadata</c>, written at
/// <c>ParquetFileWriter</c>) must serialize <see cref="ArrayType"/>/<see cref="MapType"/> with the
/// SAME nested object shape as the engine's canonical <c>SchemaJson</c> that stamps the Delta log
/// <c>metaData.schemaString</c> (written at <c>DeltaTableWriter</c> via <c>SchemaJson.ToJson</c>).
/// Before #518 the footer stringified complex types to their <c>TypeName</c> while the log emitted
/// the object form, so for a complex-typed column the two schema strings disagreed. These tests pin
/// that footer == log for every complex shape, which is the hard prerequisite for stamping
/// column-mapping ids into nested trees (#191/#676).
/// </summary>
public sealed class DeltaSchemaJsonComplexTypeTests
{
    // Both serializers are exercised on the SAME schema; equality here is exactly the footer↔log
    // agreement (DeltaTableWriter's SchemaJson.ToJson vs ParquetFileWriter's DeltaSchemaJson.ToJson).
    private static void AssertFooterMatchesLog(StructType schema)
    {
        string log = SchemaJson.ToJson(schema);
        string footer = DeltaSchemaJson.ToJson(schema);
        Assert.Equal(log, footer);
    }

    [Fact]
    public void ArrayColumn_FooterSchema_MatchesLogSchema()
    {
        var schema = new StructType(new[]
        {
            new StructField("tags", DataTypes.CreateArrayType(DataTypes.StringType, containsNull: true), nullable: true),
        });

        // Pins the exact wire shape so BOTH serializers cannot silently drift together to a wrong form.
        const string golden =
            "{\"type\":\"struct\",\"fields\":[" +
            "{\"name\":\"tags\",\"type\":{\"type\":\"array\",\"elementType\":\"string\",\"containsNull\":true}," +
            "\"nullable\":true,\"metadata\":{}}]}";
        Assert.Equal(golden, DeltaSchemaJson.ToJson(schema));
        AssertFooterMatchesLog(schema);
    }

    [Fact]
    public void ArrayColumn_ContainsNullFalse_IsHonored()
    {
        var schema = new StructType(new[]
        {
            new StructField("ids", DataTypes.CreateArrayType(DataTypes.LongType, containsNull: false), nullable: false),
        });

        Assert.Contains(
            "\"type\":{\"type\":\"array\",\"elementType\":\"long\",\"containsNull\":false}",
            DeltaSchemaJson.ToJson(schema),
            StringComparison.Ordinal);
        AssertFooterMatchesLog(schema);
    }

    [Fact]
    public void MapColumn_FooterSchema_MatchesLogSchema()
    {
        var schema = new StructType(new[]
        {
            new StructField(
                "props",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType, valueContainsNull: true),
                nullable: true),
        });

        const string golden =
            "{\"type\":\"struct\",\"fields\":[" +
            "{\"name\":\"props\",\"type\":{\"type\":\"map\",\"keyType\":\"string\",\"valueType\":\"long\"," +
            "\"valueContainsNull\":true},\"nullable\":true,\"metadata\":{}}]}";
        Assert.Equal(golden, DeltaSchemaJson.ToJson(schema));
        AssertFooterMatchesLog(schema);
    }

    [Fact]
    public void MapColumn_ValueContainsNullFalse_IsHonored()
    {
        var schema = new StructType(new[]
        {
            new StructField(
                "counts",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: false),
                nullable: false),
        });

        Assert.Contains(
            "\"type\":{\"type\":\"map\",\"keyType\":\"string\",\"valueType\":\"integer\",\"valueContainsNull\":false}",
            DeltaSchemaJson.ToJson(schema),
            StringComparison.Ordinal);
        AssertFooterMatchesLog(schema);
    }

    [Fact]
    public void ArrayOfStruct_FooterSchema_MatchesLogSchema()
    {
        var element = new StructType(new[]
        {
            new StructField("k", DataTypes.StringType, nullable: false),
            new StructField("v", DataTypes.DoubleType, nullable: true),
        });
        var schema = new StructType(new[]
        {
            new StructField("rows", DataTypes.CreateArrayType(element, containsNull: false), nullable: true),
        });

        AssertFooterMatchesLog(schema);
    }

    [Fact]
    public void MapWithStructValue_FooterSchema_MatchesLogSchema()
    {
        var value = new StructType(new[]
        {
            new StructField("lat", DataTypes.DoubleType, nullable: false),
            new StructField("lon", DataTypes.DoubleType, nullable: false),
        });
        var schema = new StructType(new[]
        {
            new StructField(
                "geo",
                DataTypes.CreateMapType(DataTypes.StringType, value, valueContainsNull: true),
                nullable: true),
        });

        AssertFooterMatchesLog(schema);
    }

    [Fact]
    public void DeeplyNestedComplexSchema_FooterSchema_MatchesLogSchema()
    {
        // struct { a: array<map<string, array<struct{ x: long }>>>, m: map<string, struct{ y: array<integer> }> }
        var innerStruct = new StructType(new[]
        {
            new StructField("x", DataTypes.LongType, nullable: false),
        });
        var arrayOfStruct = DataTypes.CreateArrayType(innerStruct, containsNull: true);
        var mapToArray = DataTypes.CreateMapType(DataTypes.StringType, arrayOfStruct, valueContainsNull: false);
        var arrayOfMap = DataTypes.CreateArrayType(mapToArray, containsNull: false);

        var mValue = new StructType(new[]
        {
            new StructField("y", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });
        var schema = new StructType(new[]
        {
            new StructField("a", arrayOfMap, nullable: true),
            new StructField("m", DataTypes.CreateMapType(DataTypes.StringType, mValue, valueContainsNull: true), nullable: false),
        });

        AssertFooterMatchesLog(schema);
    }

    [Fact]
    public void ColumnMappingMetadataOnNestedLeaf_FooterSchema_MatchesLogSchema()
    {
        // The #191/#676 motivation: column mapping stamps delta.columnMapping.id / physicalName into
        // nested field trees. The footer serializer must emit those typed numeric ids AND the nested
        // object shape byte-identically to the log, or a mapped complex-typed table's footer and log
        // schemas would disagree.
        FieldMetadata leafMapping = FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(7)),
            new KeyValuePair<string, MetadataValue>(
                "delta.columnMapping.physicalName", MetadataValue.String("col-7")),
        });
        var element = new StructType(new[]
        {
            new StructField("leaf", DataTypes.LongType, nullable: false, leafMapping),
        });
        var schema = new StructType(new[]
        {
            new StructField(
                "nested",
                DataTypes.CreateArrayType(element, containsNull: false),
                nullable: true,
                FieldMetadata.FromValues(new[]
                {
                    new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(3)),
                    new KeyValuePair<string, MetadataValue>(
                        "delta.columnMapping.physicalName", MetadataValue.String("col-3")),
                })),
        });

        string footer = DeltaSchemaJson.ToJson(schema);
        Assert.Contains("\"delta.columnMapping.id\":7", footer, StringComparison.Ordinal);
        Assert.Contains("\"elementType\":{\"type\":\"struct\"", footer, StringComparison.Ordinal);
        AssertFooterMatchesLog(schema);
    }

    [Fact]
    public void NestedDecimalTypes_FooterSchema_MatchesLogSchema()
    {
        // DecimalType has a parameterized TypeName ("decimal(p,s)") and serializes as a JSON *string*
        // (the shared default arm) even inside an array/map object shape. Pin that the footer emits
        // the string form at the recursion base — not an object — byte-identically to the log.
        var schema = new StructType(new[]
        {
            new StructField(
                "amounts",
                DataTypes.CreateArrayType(DataTypes.CreateDecimalType(20, 4), containsNull: true),
                nullable: true),
            new StructField(
                "rates",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.CreateDecimalType(10, 2), valueContainsNull: false),
                nullable: false),
        });

        string footer = DeltaSchemaJson.ToJson(schema);
        Assert.Contains("\"elementType\":\"decimal(20,4)\"", footer, StringComparison.Ordinal);
        Assert.Contains("\"valueType\":\"decimal(10,2)\"", footer, StringComparison.Ordinal);
        AssertFooterMatchesLog(schema);
    }

    [Fact]
    public void EmptyStruct_FooterSchema_MatchesLogSchema()
    {
        // An empty struct must emit {"type":"struct","fields":[]} identically in footer and log, both
        // as a field type and nested as an array element (the foreach over zero fields must not drift).
        var schema = new StructType(new[]
        {
            new StructField("empty", StructType.Empty, nullable: true),
            new StructField("empties", DataTypes.CreateArrayType(StructType.Empty, containsNull: false), nullable: false),
        });

        string footer = DeltaSchemaJson.ToJson(schema);
        Assert.Contains("\"type\":{\"type\":\"struct\",\"fields\":[]}", footer, StringComparison.Ordinal);
        AssertFooterMatchesLog(schema);
    }

    [Fact]
    public void MapWithStructKey_FooterSchema_MatchesLogSchema()
    {
        // A complex (struct) map KEY exercises the keyType recursion into a struct (MapType rejects
        // only NullType/MapType keys). The footer must match the log for the key-side recursion too,
        // not just the value side.
        var key = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: false),
            new StructField("zone", DataTypes.IntegerType, nullable: false),
        });
        var schema = new StructType(new[]
        {
            new StructField(
                "byRegion",
                DataTypes.CreateMapType(key, DataTypes.LongType, valueContainsNull: true),
                nullable: true),
        });

        string footer = DeltaSchemaJson.ToJson(schema);
        Assert.Contains("\"keyType\":{\"type\":\"struct\"", footer, StringComparison.Ordinal);
        AssertFooterMatchesLog(schema);
    }
}
