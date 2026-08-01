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
/// <remarks>
/// <para>
/// EVERY test here pins a <c>golden</c> literal in addition to the footer↔log comparison. That is
/// load-bearing since #679 consolidated both paths onto one serializer: with a single implementation
/// the footer↔log assertion is <c>f(x) == f(x)</c> and can no longer fail for a serializer defect, so
/// on its own it would silently stop guarding the wire shape. The goldens carry that weight now; the
/// footer↔log assertion is retained because it still guards the <em>delegation seam</em> (it fails if
/// <c>DeltaSchemaJson.ToJson</c> stops forwarding to <c>SchemaJson.ToJson</c>).
/// </para>
/// <para>
/// The goldens were captured by running the PRE-consolidation Storage serializer (the deleted
/// <c>DeltaSchemaJson.WriteType</c>/<c>WriteStruct</c>/<c>WriteMetadata</c> at commit a6ff45f) over
/// these exact schemas, so they are simultaneously a wire-shape pin AND a cross-commit differential
/// fixture: they prove #679 preserved behavior rather than merely asserting it.
/// </para>
/// </remarks>
public sealed class DeltaSchemaJsonComplexTypeTests
{
    // Guards the DELEGATION SEAM: since #679 both sides resolve to the same serializer, so this
    // fails only if DeltaSchemaJson.ToJson stops forwarding to SchemaJson.ToJson (or transforms its
    // result) — NOT if the shared serializer itself regresses. It must therefore never be the only
    // assertion in a test; the per-test golden is what pins the emitted bytes.
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

        const string golden =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"ids\",\"type\":{\"type\":\"array\"" +
            ",\"elementType\":\"long\",\"containsNull\":false},\"nullable\":false" +
            ",\"metadata\":{}}]}";
        Assert.Equal(golden, DeltaSchemaJson.ToJson(schema));
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

        const string golden =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"counts\",\"type\":{\"type\":\"map\"" +
            ",\"keyType\":\"string\",\"valueType\":\"integer\",\"valueContainsNull\":false}" +
            ",\"nullable\":false,\"metadata\":{}}]}";
        Assert.Equal(golden, DeltaSchemaJson.ToJson(schema));
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

        const string golden =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"rows\",\"type\":{\"type\":\"array\"" +
            ",\"elementType\":{\"type\":\"struct\",\"fields\":[{\"name\":\"k\",\"type\":\"string\"" +
            ",\"nullable\":false,\"metadata\":{}},{\"name\":\"v\",\"type\":\"double\"" +
            ",\"nullable\":true,\"metadata\":{}}]},\"containsNull\":false},\"nullable\":true" +
            ",\"metadata\":{}}]}";
        Assert.Equal(golden, DeltaSchemaJson.ToJson(schema));
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

        const string golden =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"geo\",\"type\":{\"type\":\"map\"" +
            ",\"keyType\":\"string\",\"valueType\":{\"type\":\"struct\"" +
            ",\"fields\":[{\"name\":\"lat\",\"type\":\"double\",\"nullable\":false" +
            ",\"metadata\":{}},{\"name\":\"lon\",\"type\":\"double\",\"nullable\":false" +
            ",\"metadata\":{}}]},\"valueContainsNull\":true},\"nullable\":true,\"metadata\":{}}]}";
        Assert.Equal(golden, DeltaSchemaJson.ToJson(schema));
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

        const string golden =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"a\",\"type\":{\"type\":\"array\"" +
            ",\"elementType\":{\"type\":\"map\",\"keyType\":\"string\"" +
            ",\"valueType\":{\"type\":\"array\",\"elementType\":{\"type\":\"struct\"" +
            ",\"fields\":[{\"name\":\"x\",\"type\":\"long\",\"nullable\":false,\"metadata\":{}}]}" +
            ",\"containsNull\":true},\"valueContainsNull\":false},\"containsNull\":false}" +
            ",\"nullable\":true,\"metadata\":{}},{\"name\":\"m\",\"type\":{\"type\":\"map\"" +
            ",\"keyType\":\"string\",\"valueType\":{\"type\":\"struct\"" +
            ",\"fields\":[{\"name\":\"y\",\"type\":{\"type\":\"array\",\"elementType\":\"integer\"" +
            ",\"containsNull\":true},\"nullable\":true,\"metadata\":{}}]}" +
            ",\"valueContainsNull\":true},\"nullable\":false,\"metadata\":{}}]}";
        Assert.Equal(golden, DeltaSchemaJson.ToJson(schema));
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

        const string golden =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"nested\",\"type\":{\"type\":\"array\"" +
            ",\"elementType\":{\"type\":\"struct\",\"fields\":[{\"name\":\"leaf\"" +
            ",\"type\":\"long\",\"nullable\":false,\"metadata\":{\"delta.columnMapping.id\":7" +
            ",\"delta.columnMapping.physicalName\":\"col-7\"}}]},\"containsNull\":false}" +
            ",\"nullable\":true,\"metadata\":{\"delta.columnMapping.id\":3" +
            ",\"delta.columnMapping.physicalName\":\"col-3\"}}]}";
        Assert.Equal(golden, footer);
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

        const string golden =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"amounts\",\"type\":{\"type\":\"array\"" +
            ",\"elementType\":\"decimal(20,4)\",\"containsNull\":true},\"nullable\":true" +
            ",\"metadata\":{}},{\"name\":\"rates\",\"type\":{\"type\":\"map\"" +
            ",\"keyType\":\"string\",\"valueType\":\"decimal(10,2)\",\"valueContainsNull\":false}" +
            ",\"nullable\":false,\"metadata\":{}}]}";
        Assert.Equal(golden, footer);
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

        const string golden =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"empty\",\"type\":{\"type\":\"struct\"" +
            ",\"fields\":[]},\"nullable\":true,\"metadata\":{}},{\"name\":\"empties\"" +
            ",\"type\":{\"type\":\"array\",\"elementType\":{\"type\":\"struct\",\"fields\":[]}" +
            ",\"containsNull\":false},\"nullable\":false,\"metadata\":{}}]}";
        Assert.Equal(golden, footer);
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

        const string golden =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"byRegion\",\"type\":{\"type\":\"map\"" +
            ",\"keyType\":{\"type\":\"struct\",\"fields\":[{\"name\":\"region\"" +
            ",\"type\":\"string\",\"nullable\":false,\"metadata\":{}},{\"name\":\"zone\"" +
            ",\"type\":\"integer\",\"nullable\":false,\"metadata\":{}}]},\"valueType\":\"long\"" +
            ",\"valueContainsNull\":true},\"nullable\":true,\"metadata\":{}}]}";
        Assert.Equal(golden, footer);
        AssertFooterMatchesLog(schema);
    }
}
